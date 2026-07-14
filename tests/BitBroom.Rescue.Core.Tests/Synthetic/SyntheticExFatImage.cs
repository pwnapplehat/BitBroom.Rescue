using System.Text;

namespace BitBroom.Rescue.Core.Tests.Synthetic;

/// <summary>
/// Builds a minimal valid exFAT image with a root directory of File / Stream-Extension /
/// File-Name entry sets (live or deleted) and contiguous file data. Deleted sets have the
/// in-use bit cleared on each entry type (0x85→0x05, 0xC0→0x40, 0xC1→0x41), matching exFAT's
/// real on-disk deletion, so the recovery engine is validated against true semantics.
/// </summary>
public sealed class SyntheticExFatImage
{
    private const int BytesPerSectorShift = 9;   // 512
    private const int SectorsPerClusterShift = 3; // 8 → 4096-byte clusters
    private const int BytesPerSector = 1 << BytesPerSectorShift;
    private const int ClusterSize = BytesPerSector << SectorsPerClusterShift;
    private const uint FatOffsetSectors = 128;
    private const uint FatLengthSectors = 64;
    private const uint ClusterHeapOffsetSectors = 256;
    private const uint RootDirCluster = 2;

    private sealed class Entry
    {
        public required string Name { get; init; }
        public required byte[] Data { get; init; }
        public required bool Deleted { get; init; }
        public uint FirstCluster { get; set; }
    }

    private readonly List<Entry> _entries = [];

    public void AddFile(string name, byte[] data, bool deleted) =>
        _entries.Add(new Entry { Name = name, Data = data, Deleted = deleted });

    public byte[] Build()
    {
        // Root dir occupies cluster 2; file data starts at cluster 3.
        uint next = 3;
        foreach (Entry e in _entries)
        {
            e.FirstCluster = next;
            int clusters = Math.Max(1, (e.Data.Length + ClusterSize - 1) / ClusterSize);
            next += (uint)clusters;
        }

        uint clusterCount = next + 8;
        long heapBytes = (long)clusterCount * ClusterSize;
        long imageBytes = (long)ClusterHeapOffsetSectors * BytesPerSector + heapBytes;
        byte[] image = new byte[imageBytes];

        WriteBootSector(image, clusterCount);

        long ClusterOffset(uint c) => (long)ClusterHeapOffsetSectors * BytesPerSector + (long)(c - 2) * ClusterSize;

        byte[] root = BuildRootDirectory();
        Array.Copy(root, 0, image, ClusterOffset(RootDirCluster), Math.Min(root.Length, ClusterSize));

        foreach (Entry e in _entries)
        {
            Array.Copy(e.Data, 0, image, ClusterOffset(e.FirstCluster), e.Data.Length);
        }

        return image;
    }

    private static void WriteBootSector(byte[] image, uint clusterCount)
    {
        image[0] = 0xEB;
        image[1] = 0x76;
        image[2] = 0x90;
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(image, 3);
        BitConverter.GetBytes(FatOffsetSectors).CopyTo(image, 0x50);
        BitConverter.GetBytes(FatLengthSectors).CopyTo(image, 0x54);
        BitConverter.GetBytes(ClusterHeapOffsetSectors).CopyTo(image, 0x58);
        BitConverter.GetBytes(clusterCount).CopyTo(image, 0x5C);
        BitConverter.GetBytes(RootDirCluster).CopyTo(image, 0x60);
        image[0x6C] = BytesPerSectorShift;
        image[0x6D] = SectorsPerClusterShift;
        image[510] = 0x55;
        image[511] = 0xAA;
    }

    private byte[] BuildRootDirectory()
    {
        var ms = new MemoryStream();
        foreach (Entry e in _entries)
        {
            char[] name = e.Name.ToCharArray();
            int nameEntries = (name.Length + 14) / 15;
            int secondaryCount = 1 + nameEntries; // stream ext + name entries

            // File entry (0x85 / deleted 0x05).
            byte[] file = new byte[32];
            file[0] = e.Deleted ? (byte)0x05 : (byte)0x85;
            file[1] = (byte)secondaryCount;
            BitConverter.GetBytes((ushort)0x20).CopyTo(file, 4); // archive attribute
            ms.Write(file);

            // Stream extension (0xC0 / deleted 0x40).
            byte[] stream = new byte[32];
            stream[0] = e.Deleted ? (byte)0x40 : (byte)0xC0;
            stream[1] = 0x02; // secondary flags: NoFatChain (contiguous)
            stream[3] = (byte)name.Length;
            BitConverter.GetBytes(e.FirstCluster).CopyTo(stream, 0x14);
            BitConverter.GetBytes((long)e.Data.Length).CopyTo(stream, 0x18);
            ms.Write(stream);

            // File-name entries (0xC1 / deleted 0x41), 15 UTF-16 chars each.
            int taken = 0;
            for (int ne = 0; ne < nameEntries; ne++)
            {
                byte[] nameEntry = new byte[32];
                nameEntry[0] = e.Deleted ? (byte)0x41 : (byte)0xC1;
                for (int i = 0; i < 15 && taken < name.Length; i++, taken++)
                {
                    BitConverter.GetBytes((ushort)name[taken]).CopyTo(nameEntry, 2 + i * 2);
                }

                ms.Write(nameEntry);
            }
        }

        return ms.ToArray();
    }
}
