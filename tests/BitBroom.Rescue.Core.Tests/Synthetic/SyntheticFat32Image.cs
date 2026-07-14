using System.Text;

namespace BitBroom.Rescue.Core.Tests.Synthetic;

/// <summary>
/// Builds a minimal valid FAT32 image with a root directory of 8.3 entries (live or deleted)
/// and their file data written contiguously from each entry's start cluster. Deleted entries
/// have the first name byte set to 0xE5 and their FAT chain cleared — exactly what FAT does
/// on delete — so the recovery engine is tested against real on-disk semantics.
/// </summary>
public sealed class SyntheticFat32Image
{
    private const int BytesPerSector = 512;
    private const int SectorsPerCluster = 1;
    private const int ClusterSize = BytesPerSector * SectorsPerCluster;
    private const int ReservedSectors = 32;
    private const int NumFats = 2;
    private const uint RootCluster = 2;

    private sealed class Entry
    {
        public required string Name8 { get; init; } // 8-char base, space-padded
        public required string Ext3 { get; init; }
        public required byte[] Data { get; init; }
        public required bool Deleted { get; init; }
        public uint StartCluster { get; set; }
    }

    private readonly List<Entry> _entries = [];

    public void AddFile(string name8, string ext3, byte[] data, bool deleted)
    {
        _entries.Add(new Entry
        {
            Name8 = name8.PadRight(8).Substring(0, 8),
            Ext3 = ext3.PadRight(3).Substring(0, 3),
            Data = data,
            Deleted = deleted,
        });
    }

    public byte[] Build()
    {
        // Layout: [reserved][FAT1][FAT2][data...]. Root dir is cluster 2. Files start at 3.
        int fatSectors = 64; // plenty for a tiny image
        long fatBytes = (long)fatSectors * BytesPerSector;
        uint nextCluster = 3;
        foreach (Entry e in _entries)
        {
            e.StartCluster = nextCluster;
            int clusters = Math.Max(1, (e.Data.Length + ClusterSize - 1) / ClusterSize);
            nextCluster += (uint)clusters;
        }

        long totalClusters = nextCluster + 16;
        long dataStartSector = ReservedSectors + NumFats * fatSectors;
        long totalSectors = dataStartSector + totalClusters * SectorsPerCluster;
        byte[] image = new byte[totalSectors * BytesPerSector];

        WriteBootSector(image, fatSectors, (uint)totalSectors);

        long dataStartByte = dataStartSector * BytesPerSector;
        long ClusterOffset(uint c) => dataStartByte + (long)(c - 2) * ClusterSize;

        // Root directory in cluster 2.
        byte[] root = BuildRootDirectory();
        Array.Copy(root, 0, image, ClusterOffset(RootCluster), Math.Min(root.Length, ClusterSize));

        // File data.
        foreach (Entry e in _entries)
        {
            Array.Copy(e.Data, 0, image, ClusterOffset(e.StartCluster), e.Data.Length);
        }

        return image;
    }

    private void WriteBootSector(byte[] image, int fatSectors, uint totalSectors)
    {
        image[0] = 0xEB;
        image[1] = 0x58;
        image[2] = 0x90;
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(image, 3);
        BitConverter.GetBytes((ushort)BytesPerSector).CopyTo(image, 0x0B);
        image[0x0D] = SectorsPerCluster;
        BitConverter.GetBytes((ushort)ReservedSectors).CopyTo(image, 0x0E);
        image[0x10] = NumFats;
        BitConverter.GetBytes((ushort)0).CopyTo(image, 0x11); // root entry count (0 for FAT32)
        BitConverter.GetBytes((ushort)0).CopyTo(image, 0x13); // total sectors 16 (0 → use 32)
        BitConverter.GetBytes((ushort)0).CopyTo(image, 0x16); // FAT size 16 (0 for FAT32)
        BitConverter.GetBytes(totalSectors).CopyTo(image, 0x20);
        BitConverter.GetBytes((uint)fatSectors).CopyTo(image, 0x24);
        BitConverter.GetBytes(RootCluster).CopyTo(image, 0x2C);
        image[510] = 0x55;
        image[511] = 0xAA;
    }

    private byte[] BuildRootDirectory()
    {
        var ms = new MemoryStream();
        foreach (Entry e in _entries)
        {
            byte[] entry = new byte[32];
            Encoding.ASCII.GetBytes(e.Name8).CopyTo(entry, 0);
            Encoding.ASCII.GetBytes(e.Ext3).CopyTo(entry, 8);
            if (e.Deleted)
            {
                entry[0] = 0xE5; // deletion marker
            }

            entry[0x0B] = 0x20; // archive attribute (regular file)
            BitConverter.GetBytes((ushort)(e.StartCluster >> 16)).CopyTo(entry, 0x14);
            BitConverter.GetBytes((ushort)(e.StartCluster & 0xFFFF)).CopyTo(entry, 0x1A);
            BitConverter.GetBytes((uint)e.Data.Length).CopyTo(entry, 0x1C);
            // A plausible date/time (2026-07-14 12:00:00).
            BitConverter.GetBytes((ushort)(46 << 9 | 7 << 5 | 14)).CopyTo(entry, 0x18);
            BitConverter.GetBytes((ushort)(12 << 11)).CopyTo(entry, 0x16);
            ms.Write(entry);
        }

        return ms.ToArray();
    }
}
