using System.Text;

namespace BitBroom.Rescue.Core.Tests.Synthetic;

/// <summary>
/// Builds a minimal but genuinely valid in-memory NTFS image: a real boot sector, a real
/// $MFT (record 0 describing its own non-resident runlist), a root-directory record, and
/// arbitrary user files — live or deleted, with resident or non-resident $DATA encoded per
/// the NTFS on-disk format (attribute headers, runlists, and the fixup/USA).
///
/// Because the bytes are written to spec (not to match the parser), a round-trip through
/// <c>NtfsVolume</c> is a true correctness test of the recovery engine.
/// </summary>
public sealed class SyntheticNtfsImage
{
    private const int BytesPerSector = 512;
    private const int SectorsPerCluster = 8;
    private const int ClusterSize = BytesPerSector * SectorsPerCluster; // 4096
    private const int MftRecordSize = 1024;
    private const long MftStartLcn = 64;   // MFT begins at cluster 64
    private const long DataStartLcn = 256; // user file data begins at cluster 256
    private const int RootDirRecord = 5;   // NTFS root directory is record 5 by convention

    private readonly List<FileSpec> _files = [];
    private long _nextDataLcn = DataStartLcn;

    private sealed class FileSpec
    {
        public required int RecordNumber { get; init; }
        public required string Name { get; init; }
        public required int Parent { get; init; }
        public required byte[] Data { get; init; }
        public required bool Deleted { get; init; }
        public required bool ForceNonResident { get; init; }
        public bool IsDirectory { get; init; }
        public long DataLcn { get; set; }
        public long DataClusters { get; set; }
    }

    private int _nextRecord = 16; // records 0..15 reserved for NTFS metadata files

    /// <summary>Adds a file. Small data stays resident unless <paramref name="forceNonResident"/>.</summary>
    public int AddFile(string name, byte[] data, bool deleted, bool forceNonResident = false, int parent = RootDirRecord)
    {
        int rec = _nextRecord++;
        _files.Add(new FileSpec
        {
            RecordNumber = rec,
            Name = name,
            Parent = parent,
            Data = data,
            Deleted = deleted,
            ForceNonResident = forceNonResident,
        });
        return rec;
    }

    /// <summary>Adds a directory (so files can reference it as a parent for path reconstruction).</summary>
    public int AddDirectory(string name, bool deleted, int parent = RootDirRecord)
    {
        int rec = _nextRecord++;
        _files.Add(new FileSpec
        {
            RecordNumber = rec,
            Name = name,
            Parent = parent,
            Data = [],
            Deleted = deleted,
            ForceNonResident = false,
            IsDirectory = true,
        });
        return rec;
    }

    public byte[] Build()
    {
        int totalRecords = _nextRecord;
        long mftBytes = (long)totalRecords * MftRecordSize;
        long mftClusters = (mftBytes + ClusterSize - 1) / ClusterSize;

        // Place non-resident file data after DataStartLcn.
        foreach (FileSpec f in _files)
        {
            bool nonResident = f.ForceNonResident || f.Data.Length > 700;
            if (!f.IsDirectory && nonResident && f.Data.Length > 0)
            {
                f.DataClusters = (f.Data.Length + ClusterSize - 1) / ClusterSize;
                f.DataLcn = _nextDataLcn;
                _nextDataLcn += f.DataClusters;
            }
        }

        long imageClusters = _nextDataLcn + 16;
        long imageBytes = imageClusters * ClusterSize;
        byte[] image = new byte[imageBytes];

        WriteBootSector(image, imageClusters, mftClusters);

        // Write file data clusters.
        foreach (FileSpec f in _files)
        {
            if (f.DataClusters > 0)
            {
                Array.Copy(f.Data, 0, image, f.DataLcn * ClusterSize, f.Data.Length);
            }
        }

        // Build and place each MFT record.
        long mftByteOffset = MftStartLcn * ClusterSize;
        for (int i = 0; i < totalRecords; i++)
        {
            byte[] rec = i switch
            {
                0 => BuildMftSelfRecord(mftClusters),
                RootDirRecord => BuildDirectoryRecord(RootDirRecord, ".", RootDirRecord, deleted: false),
                _ => BuildForRecordNumber(i),
            };

            Array.Copy(rec, 0, image, mftByteOffset + (long)i * MftRecordSize, MftRecordSize);
        }

        return image;
    }

    private byte[] BuildForRecordNumber(int i)
    {
        foreach (FileSpec f in _files)
        {
            if (f.RecordNumber == i)
            {
                return f.IsDirectory
                    ? BuildDirectoryRecord(f.RecordNumber, f.Name, f.Parent, f.Deleted)
                    : BuildFileRecord(f);
            }
        }

        return BuildEmptyRecord(); // reserved/unused metadata slot
    }

    private static void WriteBootSector(byte[] image, long totalClusters, long mftClusters)
    {
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(image, 3);
        BitConverter.GetBytes((ushort)BytesPerSector).CopyTo(image, 0x0B);
        image[0x0D] = SectorsPerCluster;
        long totalSectors = totalClusters * SectorsPerCluster;
        BitConverter.GetBytes(totalSectors - 1).CopyTo(image, 0x28);
        BitConverter.GetBytes(MftStartLcn).CopyTo(image, 0x30);
        BitConverter.GetBytes(MftStartLcn + mftClusters).CopyTo(image, 0x38); // mirror (dummy)
        image[0x40] = unchecked((byte)(sbyte)-10); // 2^10 = 1024-byte records
    }

    private static byte[] BuildEmptyRecord()
    {
        // A zeroed slot has no "FILE" signature, so the parser correctly ignores it.
        return new byte[MftRecordSize];
    }

    private byte[] BuildMftSelfRecord(long mftClusters)
    {
        var attrs = new List<byte[]>
        {
            BuildStandardInformation(DateTime.UtcNow, DateTime.UtcNow),
            BuildNonResidentData(MftStartLcn, mftClusters, mftClusters * ClusterSize),
        };
        return AssembleRecord(inUse: true, isDirectory: false, attrs);
    }

    private byte[] BuildDirectoryRecord(int record, string name, int parent, bool deleted)
    {
        var attrs = new List<byte[]>
        {
            BuildStandardInformation(DateTime.UtcNow, DateTime.UtcNow),
            BuildFileName(name, parent, DateTime.UtcNow, DateTime.UtcNow),
        };
        return AssembleRecord(inUse: !deleted, isDirectory: true, attrs);
    }

    private byte[] BuildFileRecord(FileSpec f)
    {
        DateTime created = new(2026, 2, 2, 10, 0, 0, DateTimeKind.Utc);
        DateTime modified = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        var attrs = new List<byte[]>
        {
            BuildStandardInformation(created, modified),
            BuildFileName(f.Name, f.Parent, created, modified),
        };

        if (f.DataClusters > 0)
        {
            attrs.Add(BuildNonResidentData(f.DataLcn, f.DataClusters, f.Data.Length));
        }
        else
        {
            attrs.Add(BuildResidentData(f.Data));
        }

        return AssembleRecord(inUse: !f.Deleted, isDirectory: false, attrs);
    }

    // ------------------------------------------------------------------ attributes

    private static byte[] BuildStandardInformation(DateTime created, DateTime modified)
    {
        byte[] content = new byte[48];
        BitConverter.GetBytes(created.ToFileTimeUtc()).CopyTo(content, 0);
        BitConverter.GetBytes(modified.ToFileTimeUtc()).CopyTo(content, 8);
        BitConverter.GetBytes(modified.ToFileTimeUtc()).CopyTo(content, 16);
        BitConverter.GetBytes(modified.ToFileTimeUtc()).CopyTo(content, 24);
        return WrapResident((uint)0x10, name: null, content);
    }

    private static byte[] BuildFileName(string name, int parentRecord, DateTime created, DateTime modified)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        byte[] content = new byte[0x42 + nameBytes.Length];
        long parentRef = ((long)1 << 48) | (uint)parentRecord; // seq 1 in high bits
        BitConverter.GetBytes(parentRef).CopyTo(content, 0x00);
        BitConverter.GetBytes(created.ToFileTimeUtc()).CopyTo(content, 0x08);
        BitConverter.GetBytes(modified.ToFileTimeUtc()).CopyTo(content, 0x18);
        content[0x40] = (byte)name.Length;
        content[0x41] = (byte)1; // Win32 namespace
        nameBytes.CopyTo(content, 0x42);
        return WrapResident((uint)0x30, name: null, content);
    }

    private static byte[] BuildResidentData(byte[] data) => WrapResident((uint)0x80, name: null, data);

    private static byte[] WrapResident(uint type, string? name, byte[] content)
    {
        // Header 0x18 bytes, then content, padded to 8.
        int headerLen = 0x18;
        int total = Align8(headerLen + content.Length);
        byte[] a = new byte[total];
        BitConverter.GetBytes(type).CopyTo(a, 0x00);
        BitConverter.GetBytes(total).CopyTo(a, 0x04);
        a[0x08] = 0; // resident
        a[0x09] = 0; // name length
        BitConverter.GetBytes((ushort)0).CopyTo(a, 0x0A);
        BitConverter.GetBytes((uint)content.Length).CopyTo(a, 0x10);
        BitConverter.GetBytes((ushort)headerLen).CopyTo(a, 0x14);
        content.CopyTo(a, headerLen);
        return a;
    }

    private static byte[] BuildNonResidentData(long lcn, long clusters, long realSize)
    {
        byte[] runlist = EncodeSingleRun(lcn, clusters);
        int runlistOffset = 0x40;
        int total = Align8(runlistOffset + runlist.Length);
        byte[] a = new byte[total];
        BitConverter.GetBytes((uint)0x80).CopyTo(a, 0x00);
        BitConverter.GetBytes(total).CopyTo(a, 0x04);
        a[0x08] = 1; // non-resident
        a[0x09] = 0; // name length
        BitConverter.GetBytes((ushort)0).CopyTo(a, 0x0A);
        BitConverter.GetBytes(0L).CopyTo(a, 0x10);                      // starting VCN
        BitConverter.GetBytes(clusters - 1).CopyTo(a, 0x18);           // last VCN
        BitConverter.GetBytes((ushort)runlistOffset).CopyTo(a, 0x20);  // runlist offset
        BitConverter.GetBytes(clusters * ClusterSize).CopyTo(a, 0x28); // allocated
        BitConverter.GetBytes(realSize).CopyTo(a, 0x30);               // real size
        BitConverter.GetBytes(realSize).CopyTo(a, 0x38);               // initialized
        runlist.CopyTo(a, runlistOffset);
        return a;
    }

    private static byte[] EncodeSingleRun(long lcn, long clusters)
    {
        byte[] lenBytes = TrimLE(BitConverter.GetBytes(clusters));
        byte[] offBytes = TrimSignedLE(lcn);
        byte[] run = new byte[1 + lenBytes.Length + offBytes.Length + 1];
        run[0] = (byte)((offBytes.Length << 4) | lenBytes.Length);
        lenBytes.CopyTo(run, 1);
        offBytes.CopyTo(run, 1 + lenBytes.Length);
        // trailing 0 terminator already present (array init)
        return run;
    }

    private static byte[] TrimLE(byte[] full)
    {
        int len = full.Length;
        while (len > 1 && full[len - 1] == 0)
        {
            len--;
        }

        byte[] r = new byte[len];
        Array.Copy(full, r, len);
        return r;
    }

    private static byte[] TrimSignedLE(long value)
    {
        byte[] full = BitConverter.GetBytes(value);
        int len = 8;
        // Keep enough bytes to preserve sign.
        while (len > 1)
        {
            byte top = full[len - 1];
            byte next = full[len - 2];
            bool topIsSignExt = top == 0x00 && (next & 0x80) == 0;
            bool topIsNegExt = top == 0xFF && (next & 0x80) != 0;
            if (topIsSignExt || topIsNegExt)
            {
                len--;
            }
            else
            {
                break;
            }
        }

        byte[] r = new byte[len];
        Array.Copy(full, r, len);
        return r;
    }

    // ------------------------------------------------------------------ record assembly

    private static byte[] AssembleRecord(bool inUse, bool isDirectory, List<byte[]> attributes)
    {
        byte[] rec = new byte[MftRecordSize];
        Encoding.ASCII.GetBytes("FILE").CopyTo(rec, 0);

        const int usaOffset = 0x30;
        const int usaCount = MftRecordSize / BytesPerSector + 1; // 3 for 1024/512
        BitConverter.GetBytes((ushort)usaOffset).CopyTo(rec, 0x04);
        BitConverter.GetBytes((ushort)usaCount).CopyTo(rec, 0x06);
        BitConverter.GetBytes((ushort)1).CopyTo(rec, 0x10); // sequence
        BitConverter.GetBytes((ushort)1).CopyTo(rec, 0x12); // hard link count

        const int firstAttrOffset = 0x38;
        BitConverter.GetBytes((ushort)firstAttrOffset).CopyTo(rec, 0x14);

        ushort flags = 0;
        if (inUse)
        {
            flags |= 0x01;
        }

        if (isDirectory)
        {
            flags |= 0x02;
        }

        BitConverter.GetBytes(flags).CopyTo(rec, 0x16);
        BitConverter.GetBytes(0L).CopyTo(rec, 0x20); // base record = 0 (this is a base record)

        int p = firstAttrOffset;
        foreach (byte[] attr in attributes)
        {
            if (p + attr.Length + 8 > MftRecordSize)
            {
                break; // don't overflow the record
            }

            attr.CopyTo(rec, p);
            p += attr.Length;
        }

        // End-of-attributes marker.
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(rec, p);

        ApplyFixupForBuild(rec, usaOffset, usaCount);
        return rec;
    }

    /// <summary>Encodes the fixup: stash each sector's last 2 bytes into the USA and stamp the USN.</summary>
    private static void ApplyFixupForBuild(byte[] rec, int usaOffset, int usaCount)
    {
        const ushort usn = 0x0001;
        BitConverter.GetBytes(usn).CopyTo(rec, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int sectorTail = i * BytesPerSector - 2;
            // Move the real tail bytes into the USA slot...
            rec[usaOffset + i * 2] = rec[sectorTail];
            rec[usaOffset + i * 2 + 1] = rec[sectorTail + 1];
            // ...and stamp the USN check value into the sector tail.
            BitConverter.GetBytes(usn).CopyTo(rec, sectorTail);
        }
    }

    private static int Align8(int n) => (n + 7) & ~7;
}
