using System.Text;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Recovery;

namespace BitBroom.Rescue.Core.Fat;

public enum FatKind { Fat12, Fat16, Fat32 }

/// <summary>
/// Read-only FAT12/16/32 volume — the file system on most small USB sticks and older SD
/// cards. Recovers deleted files from directory entries: a deletion flips the first filename
/// byte to 0xE5 but leaves the entry (including the start cluster and size) intact, so the
/// name (minus its first character) and the data location survive until reused. Reads the
/// FAT to follow the cluster chain for the live/intact case; when the chain is broken by the
/// deletion, falls back to reading contiguously from the start cluster (the common outcome
/// for the small, unfragmented files typical of these media).
/// </summary>
public sealed class FatVolume
{
    private readonly ISectorSource _source;

    private FatVolume(ISectorSource source)
    {
        _source = source;
    }

    public FatKind Kind { get; private init; }

    public int BytesPerSector { get; private init; }

    public int SectorsPerCluster { get; private init; }

    public int ClusterSize => BytesPerSector * SectorsPerCluster;

    public long FatStartByte { get; private init; }

    public long DataStartByte { get; private init; }

    public long RootDirStartByte { get; private init; } // FAT12/16 fixed root

    public int RootEntryCount { get; private init; }

    public uint RootCluster { get; private init; } // FAT32

    public long TotalClusters { get; private init; }

    public static FatVolume? TryOpen(ISectorSource source)
    {
        byte[] boot = source.ReadBestEffort(0, 512);
        if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA)
        {
            return null;
        }

        int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
        int sectorsPerCluster = boot[0x0D];
        if (bytesPerSector == 0 || sectorsPerCluster == 0 || (bytesPerSector & (bytesPerSector - 1)) != 0)
        {
            return null;
        }

        int reservedSectors = BitConverter.ToUInt16(boot, 0x0E);
        int numFats = boot[0x10];
        int rootEntryCount = BitConverter.ToUInt16(boot, 0x11);
        int totalSectors16 = BitConverter.ToUInt16(boot, 0x13);
        int fatSize16 = BitConverter.ToUInt16(boot, 0x16);
        uint totalSectors32 = BitConverter.ToUInt32(boot, 0x20);
        uint fatSize32 = BitConverter.ToUInt32(boot, 0x24);

        if (numFats == 0)
        {
            return null;
        }

        long fatSize = fatSize16 != 0 ? fatSize16 : fatSize32;
        long totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
        if (fatSize == 0 || totalSectors == 0)
        {
            return null;
        }

        long rootDirSectors = (rootEntryCount * 32L + bytesPerSector - 1) / bytesPerSector;
        long firstDataSector = reservedSectors + numFats * fatSize + rootDirSectors;
        long dataSectors = totalSectors - firstDataSector;
        long clusterCount = dataSectors / sectorsPerCluster;

        // FAT32 is definitively identified by its BPB (16-bit FAT size and root-entry count
        // are both zero); otherwise use the cluster-count thresholds for FAT12 vs FAT16. The
        // structural signal is authoritative and avoids misclassifying small volumes.
        FatKind kind;
        if (fatSize16 == 0 && rootEntryCount == 0 && fatSize32 != 0)
        {
            kind = FatKind.Fat32;
        }
        else
        {
            kind = clusterCount < 4085 ? FatKind.Fat12 : clusterCount < 65525 ? FatKind.Fat16 : FatKind.Fat32;
        }

        uint rootCluster = kind == FatKind.Fat32 ? BitConverter.ToUInt32(boot, 0x2C) : 0;
        long fatStart = (long)reservedSectors * bytesPerSector;
        long rootStart = (reservedSectors + numFats * fatSize) * bytesPerSector;
        long dataStart = firstDataSector * bytesPerSector;

        return new FatVolume(source)
        {
            Kind = kind,
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            FatStartByte = fatStart,
            RootDirStartByte = rootStart,
            RootEntryCount = rootEntryCount,
            RootCluster = rootCluster,
            DataStartByte = dataStart,
            TotalClusters = clusterCount,
        };
    }

    /// <summary>Byte offset on disk of a data cluster (clusters are numbered from 2).</summary>
    public long ClusterToOffset(uint cluster) => DataStartByte + (long)(cluster - 2) * ClusterSize;

    /// <summary>
    /// Scans directory regions for deleted (0xE5) short-name entries and yields recoverable
    /// items. Walks the FAT32 root cluster chain / FAT16 fixed root and recurses into
    /// subdirectories, reconstructing paths. Long-file-name (LFN) entries preceding a deleted
    /// short entry are stitched back where they survive.
    /// </summary>
    public List<RecoverableItem> ScanDeleted(long minSize = 1, CancellationToken cancellationToken = default)
    {
        var items = new List<RecoverableItem>();
        var visited = new HashSet<uint>();
        ScanDirectory(GetRootDirBytes(), "\\", items, visited, minSize, depth: 0, cancellationToken);
        return items;
    }

    private byte[] GetRootDirBytes()
    {
        if (Kind == FatKind.Fat32)
        {
            return ReadClusterChain(RootCluster);
        }

        return _source.ReadBestEffort(RootDirStartByte, RootEntryCount * 32);
    }

    private void ScanDirectory(byte[] dir, string path, List<RecoverableItem> items,
        HashSet<uint> visited, long minSize, int depth, CancellationToken ct)
    {
        if (depth > 64)
        {
            return; // cycle / corruption guard
        }

        var lfnParts = new List<string>();
        for (int off = 0; off + 32 <= dir.Length; off += 32)
        {
            ct.ThrowIfCancellationRequested();
            byte first = dir[off];
            byte attr = dir[off + 0x0B];

            if (first == 0x00)
            {
                break; // end of directory
            }

            if (attr == 0x0F)
            {
                // LFN entry — collect its name fragment (survives for many deletions).
                lfnParts.Insert(0, ExtractLfn(dir, off));
                continue;
            }

            string longName = lfnParts.Count > 0 ? string.Concat(lfnParts).TrimEnd('\uFFFF', '\0') : string.Empty;
            lfnParts.Clear();

            if (first == 0xE5)
            {
                // Deleted entry.
                var entry = ParseEntry(dir, off);
                if (!entry.IsDirectory && entry.Size >= minSize)
                {
                    // longName (from surviving LFN entries) is exact; the short name has '_'
                    // for the byte the deletion clobbered.
                    string name = !string.IsNullOrEmpty(longName) ? longName : entry.ShortName;
                    uint startCluster = entry.StartCluster;
                    long size = entry.Size;
                    items.Add(new RecoverableItem
                    {
                        Name = name,
                        OriginalPath = path + name,
                        SizeBytes = size,
                        ModifiedUtc = entry.ModifiedUtc,
                        Source = RecoverySource.FileSystemMetadata,
                        Confidence = startCluster >= 2
                            ? RecoveryConfidence.Fair
                            : RecoveryConfidence.Poor,
                        ConfidenceReason = startCluster >= 2
                            ? "FAT clears the cluster chain on delete; recovered as contiguous from the start cluster"
                            : "start cluster lost — content location unknown",
                        IsResident = false,
                        ContentProvider = _ => ReadContiguous(startCluster, size),
                    });
                }

                continue;
            }

            // Live entry: recurse into subdirectories to find deleted files inside them.
            var live = ParseEntry(dir, off);
            if (live.IsDirectory && live.ShortName is not "." and not ".." && live.StartCluster >= 2 && visited.Add(live.StartCluster))
            {
                string childName = !string.IsNullOrEmpty(longName) ? longName : live.ShortName;
                byte[] child = ReadClusterChain(live.StartCluster);
                ScanDirectory(child, path + childName + "\\", items, visited, minSize, depth + 1, ct);
            }
        }
    }

    private (string ShortName, bool IsDirectory, uint StartCluster, long Size, DateTime ModifiedUtc) ParseEntry(byte[] dir, int off)
    {
        // Read the full 8-char base. On a deleted entry the first byte was overwritten with
        // 0xE5, so that character is genuinely lost — surface it as '_' rather than garbage.
        var nameChars = new char[8];
        for (int i = 0; i < 8; i++)
        {
            byte b = dir[off + i];
            nameChars[i] = i == 0 && b == 0xE5 ? '_' : (char)b;
        }

        string baseName = new string(nameChars).TrimEnd();
        string ext = Encoding.ASCII.GetString(dir, off + 8, 3).TrimEnd();
        string shortName = ext.Length > 0 ? $"{baseName}.{ext}" : baseName;

        byte attr = dir[off + 0x0B];
        bool isDir = (attr & 0x10) != 0;
        ushort hi = BitConverter.ToUInt16(dir, off + 0x14);
        ushort lo = BitConverter.ToUInt16(dir, off + 0x1A);
        uint startCluster = (uint)(hi << 16 | lo);
        long size = BitConverter.ToUInt32(dir, off + 0x1C);
        DateTime modified = ParseFatTime(BitConverter.ToUInt16(dir, off + 0x18), BitConverter.ToUInt16(dir, off + 0x16));
        return (shortName, isDir, startCluster, size, modified);
    }

    private static string ExtractLfn(byte[] dir, int off)
    {
        // LFN entry stores 13 UTF-16 chars at offsets 1,14,28.
        var chars = new char[13];
        int idx = 0;
        int[] starts = [1, 14, 28];
        int[] counts = [5, 6, 2];
        for (int seg = 0; seg < 3; seg++)
        {
            for (int i = 0; i < counts[seg]; i++)
            {
                chars[idx++] = (char)BitConverter.ToUInt16(dir, off + starts[seg] + i * 2);
            }
        }

        return new string(chars);
    }

    private byte[] ReadClusterChain(uint startCluster)
    {
        // Follow the FAT chain for live directories/files. Bounded to avoid runaway on
        // corruption. Deleted files have their chain cleared, so callers use ReadContiguous.
        var ms = new MemoryStream();
        uint cluster = startCluster;
        var seen = new HashSet<uint>();
        int max = (int)Math.Min(TotalClusters + 2, 1 << 22);
        while (cluster >= 2 && cluster < TotalClusters + 2 && seen.Add(cluster) && seen.Count < max)
        {
            byte[] block = _source.ReadBestEffort(ClusterToOffset(cluster), ClusterSize);
            ms.Write(block, 0, block.Length);
            uint next = ReadFatEntry(cluster);
            if (IsEndOfChain(next))
            {
                break;
            }

            cluster = next;
        }

        return ms.ToArray();
    }

    private byte[] ReadContiguous(uint startCluster, long size)
    {
        if (startCluster < 2 || size <= 0)
        {
            return [];
        }

        long clusters = (size + ClusterSize - 1) / ClusterSize;
        byte[] output = new byte[size];
        long written = 0;
        for (long i = 0; i < clusters && written < size; i++)
        {
            long offset = ClusterToOffset((uint)(startCluster + i));
            int want = (int)Math.Min(ClusterSize, size - written);
            byte[] block = _source.ReadBestEffort(offset, want);
            if (block.Length == 0)
            {
                break;
            }

            Array.Copy(block, 0, output, written, block.Length);
            written += block.Length;
        }

        return output;
    }

    private uint ReadFatEntry(uint cluster)
    {
        switch (Kind)
        {
            case FatKind.Fat32:
            {
                byte[] b = _source.ReadBestEffort(FatStartByte + cluster * 4, 4);
                return b.Length < 4 ? 0x0FFFFFFF : BitConverter.ToUInt32(b, 0) & 0x0FFFFFFF;
            }

            case FatKind.Fat16:
            {
                byte[] b = _source.ReadBestEffort(FatStartByte + cluster * 2, 2);
                return b.Length < 2 ? 0xFFFFu : BitConverter.ToUInt16(b, 0);
            }

            default: // FAT12
            {
                long fatOffset = FatStartByte + cluster + cluster / 2;
                byte[] b = _source.ReadBestEffort(fatOffset, 2);
                if (b.Length < 2)
                {
                    return 0xFFF;
                }

                ushort val = BitConverter.ToUInt16(b, 0);
                return (cluster & 1) == 0 ? (uint)(val & 0x0FFF) : (uint)(val >> 4);
            }
        }
    }

    private bool IsEndOfChain(uint entry) => Kind switch
    {
        FatKind.Fat32 => entry >= 0x0FFFFFF8 || entry == 0,
        FatKind.Fat16 => entry >= 0xFFF8 || entry == 0,
        _ => entry >= 0xFF8 || entry == 0,
    };

    private static DateTime ParseFatTime(ushort date, ushort time)
    {
        if (date == 0)
        {
            return DateTime.MinValue;
        }

        try
        {
            int year = 1980 + (date >> 9);
            int month = date >> 5 & 0x0F;
            int day = date & 0x1F;
            int hour = time >> 11;
            int minute = time >> 5 & 0x3F;
            int second = (time & 0x1F) * 2;
            return new DateTime(year, Math.Clamp(month, 1, 12), Math.Clamp(day, 1, 28), hour % 24, minute % 60, second % 60, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }
}
