using System.Text;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Recovery;

namespace BitBroom.Rescue.Core.Fat;

/// <summary>
/// Read-only exFAT volume — the file system on modern SD/SDXC cards and most cameras, and
/// therefore central to photo/video recovery. exFAT directory entries come in sets: a File
/// entry (0x85), a Stream Extension (0xC0) holding the first cluster + data length + the
/// "no FAT chain" (contiguous) flag, and File Name entries (0xC1) holding the UTF-16 name.
/// Deletion clears the high (in-use) bit of each entry type (0x85→0x05 etc.) but leaves the
/// set otherwise intact, so the full name, size and location survive until reused. Because
/// most camera files are written contiguously (NoFatChain set), content recovery is
/// direct and high-fidelity.
/// </summary>
public sealed class ExFatVolume
{
    private readonly ISectorSource _source;

    private ExFatVolume(ISectorSource source)
    {
        _source = source;
    }

    public int BytesPerSector { get; private init; }

    public int SectorsPerCluster { get; private init; }

    public int ClusterSize => BytesPerSector * SectorsPerCluster;

    public long FatOffsetByte { get; private init; }

    public long ClusterHeapOffsetByte { get; private init; }

    public long ClusterCount { get; private init; }

    public uint RootDirCluster { get; private init; }

    public static ExFatVolume? TryOpen(ISectorSource source)
    {
        byte[] boot = source.ReadBestEffort(0, 512);
        if (boot.Length < 512 || Encoding.ASCII.GetString(boot, 3, 8) != "EXFAT   ")
        {
            return null;
        }

        uint fatOffset = BitConverter.ToUInt32(boot, 0x50);
        uint clusterHeapOffset = BitConverter.ToUInt32(boot, 0x58);
        uint clusterCount = BitConverter.ToUInt32(boot, 0x5C);
        uint rootCluster = BitConverter.ToUInt32(boot, 0x60);
        int bytesPerSector = 1 << boot[0x6C];
        int sectorsPerCluster = 1 << boot[0x6D];
        if (bytesPerSector == 0 || sectorsPerCluster == 0)
        {
            return null;
        }

        return new ExFatVolume(source)
        {
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            FatOffsetByte = (long)fatOffset * bytesPerSector,
            ClusterHeapOffsetByte = (long)clusterHeapOffset * bytesPerSector,
            ClusterCount = clusterCount,
            RootDirCluster = rootCluster,
        };
    }

    public long ClusterToOffset(uint cluster) => ClusterHeapOffsetByte + (long)(cluster - 2) * ClusterSize;

    public List<RecoverableItem> ScanDeleted(long minSize = 1, CancellationToken cancellationToken = default)
    {
        var items = new List<RecoverableItem>();
        var visited = new HashSet<uint>();
        ScanDirectory(RootDirCluster, "\\", items, visited, minSize, 0, cancellationToken);
        return items;
    }

    private void ScanDirectory(uint dirCluster, string path, List<RecoverableItem> items,
        HashSet<uint> visited, long minSize, int depth, CancellationToken ct)
    {
        if (depth > 64 || !visited.Add(dirCluster))
        {
            return;
        }

        byte[] dir = ReadClusterChain(dirCluster, maxBytes: 8 * 1024 * 1024);
        for (int off = 0; off + 32 <= dir.Length; off += 32)
        {
            ct.ThrowIfCancellationRequested();
            byte type = dir[off];
            if (type == 0x00)
            {
                break; // end of directory
            }

            bool isFileEntry = type is 0x85 or 0x05;
            if (!isFileEntry)
            {
                continue;
            }

            bool deleted = (type & 0x80) == 0;
            int secondaryCount = dir[off + 1];
            ushort fileAttrs = BitConverter.ToUInt16(dir, off + 4);
            bool isDir = (fileAttrs & 0x10) != 0;

            // The Stream Extension entry immediately follows the File entry.
            int streamOff = off + 32;
            if (streamOff + 32 > dir.Length)
            {
                break;
            }

            byte streamType = dir[streamOff];
            if (streamType is not (0xC0 or 0x40))
            {
                continue;
            }

            byte secondaryFlags = dir[streamOff + 1];
            bool noFatChain = (secondaryFlags & 0x02) != 0;
            int nameLength = dir[streamOff + 3];
            uint firstCluster = BitConverter.ToUInt32(dir, streamOff + 0x14);
            long dataLength = BitConverter.ToInt64(dir, streamOff + 0x18);

            string name = ReadName(dir, streamOff + 32, nameLength, secondaryCount);

            if (isDir && !deleted && firstCluster >= 2)
            {
                ScanDirectory(firstCluster, path + name + "\\", items, visited, minSize, depth + 1, ct);
            }
            else if (deleted && !isDir && dataLength >= minSize && firstCluster >= 2)
            {
                uint fc = firstCluster;
                long len = dataLength;
                bool contiguous = noFatChain;
                items.Add(new RecoverableItem
                {
                    Name = name.Length > 0 ? name : $"exfat_{firstCluster}.bin",
                    OriginalPath = path + (name.Length > 0 ? name : $"exfat_{firstCluster}.bin"),
                    SizeBytes = dataLength,
                    Source = RecoverySource.FileSystemMetadata,
                    Confidence = contiguous ? RecoveryConfidence.Good : RecoveryConfidence.Fair,
                    ConfidenceReason = contiguous
                        ? "exFAT contiguous file (NoFatChain) — read directly from the first cluster"
                        : "fragmented exFAT file — FAT chain may be cleared; recovered best-effort",
                    IsResident = false,
                    ContentProvider = _ => contiguous ? ReadContiguous(fc, len) : ReadChainOrContiguous(fc, len),
                    ContentStreamProvider = (stream, ct) => WriteContiguous(fc, len, stream, ct),
                });
            }

            off += secondaryCount * 32; // skip the secondary entries of this set
        }
    }

    private static string ReadName(byte[] dir, int firstNameOff, int nameLength, int secondaryCount)
    {
        var sb = new StringBuilder(nameLength);
        int taken = 0;
        int off = firstNameOff;
        int nameEntries = Math.Max(0, secondaryCount - 1);
        for (int e = 0; e < nameEntries && taken < nameLength; e++)
        {
            if (off + 32 > dir.Length)
            {
                break;
            }

            byte type = dir[off];
            if (type is 0xC1 or 0x41)
            {
                for (int i = 0; i < 15 && taken < nameLength; i++)
                {
                    char c = (char)BitConverter.ToUInt16(dir, off + 2 + i * 2);
                    sb.Append(c);
                    taken++;
                }
            }

            off += 32;
        }

        return sb.ToString();
    }

    private byte[] ReadContiguous(uint firstCluster, long size)
    {
        if (firstCluster < 2 || size <= 0)
        {
            return [];
        }

        byte[] output = new byte[size];
        long written = 0;
        long clusters = (size + ClusterSize - 1) / ClusterSize;
        for (long i = 0; i < clusters && written < size; i++)
        {
            int want = (int)Math.Min(ClusterSize, size - written);
            byte[] block = _source.ReadBestEffort(ClusterToOffset((uint)(firstCluster + i)), want);
            if (block.Length == 0)
            {
                break;
            }

            Array.Copy(block, 0, output, written, block.Length);
            written += block.Length;
        }

        return output;
    }

    private long WriteContiguous(uint firstCluster, long size, Stream dest, CancellationToken ct)
    {
        // Streams a contiguous cluster range straight to the destination — the path for large
        // (multi-GB 4K/action-cam) files that must not be buffered in memory. Deleted exFAT
        // files are overwhelmingly NoFatChain (contiguous), so this is the high-fidelity route.
        if (firstCluster < 2 || size <= 0)
        {
            return 0;
        }

        long written = 0;
        long clusters = (size + ClusterSize - 1) / ClusterSize;
        byte[] buffer = new byte[Math.Max(ClusterSize, 1 * 1024 * 1024)];
        for (long i = 0; i < clusters && written < size; i++)
        {
            ct.ThrowIfCancellationRequested();
            long clusterStart = ClusterToOffset((uint)(firstCluster + i));
            long clusterRemaining = Math.Min(ClusterSize, size - written);
            long copied = 0;
            while (copied < clusterRemaining)
            {
                int want = (int)Math.Min(buffer.Length, clusterRemaining - copied);
                int got = _source.Read(clusterStart + copied, buffer, 0, want);
                if (got <= 0)
                {
                    return written; // unreadable — stop best-effort
                }

                dest.Write(buffer, 0, got);
                written += got;
                copied += got;
            }
        }

        return written;
    }

    private byte[] ReadChainOrContiguous(uint firstCluster, long size)
    {
        // For fragmented (FAT-chained) files we attempt the chain; if it looks cleared
        // (points nowhere) we fall back to a contiguous read as a best effort.
        var ms = new MemoryStream();
        uint cluster = firstCluster;
        var seen = new HashSet<uint>();
        long remaining = size;
        while (cluster >= 2 && cluster < ClusterCount + 2 && seen.Add(cluster) && remaining > 0)
        {
            int want = (int)Math.Min(ClusterSize, remaining);
            byte[] block = _source.ReadBestEffort(ClusterToOffset(cluster), want);
            if (block.Length == 0)
            {
                break;
            }

            ms.Write(block, 0, block.Length);
            remaining -= block.Length;
            uint next = ReadFatEntry(cluster);
            if (next is 0 or 0xFFFFFFFF || next < 2)
            {
                // Chain cleared/ended — fall back to contiguous for the remainder.
                if (remaining > 0)
                {
                    return ReadContiguous(firstCluster, size);
                }

                break;
            }

            cluster = next;
        }

        return ms.Length == size ? ms.ToArray() : ReadContiguous(firstCluster, size);
    }

    private uint ReadFatEntry(uint cluster)
    {
        byte[] b = _source.ReadBestEffort(FatOffsetByte + (long)cluster * 4, 4);
        return b.Length < 4 ? 0xFFFFFFFF : BitConverter.ToUInt32(b, 0);
    }

    private byte[] ReadClusterChain(uint startCluster, long maxBytes)
    {
        var ms = new MemoryStream();
        uint cluster = startCluster;
        var seen = new HashSet<uint>();
        while (cluster >= 2 && cluster < ClusterCount + 2 && seen.Add(cluster) && ms.Length < maxBytes)
        {
            byte[] block = _source.ReadBestEffort(ClusterToOffset(cluster), ClusterSize);
            if (block.Length == 0)
            {
                break;
            }

            ms.Write(block, 0, block.Length);
            uint next = ReadFatEntry(cluster);
            if (next is 0 or 0xFFFFFFFF || next < 2)
            {
                break;
            }

            cluster = next;
        }

        return ms.ToArray();
    }
}
