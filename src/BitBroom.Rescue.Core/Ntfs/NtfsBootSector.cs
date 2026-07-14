using System.Text;
using BitBroom.Rescue.Core.Io;

namespace BitBroom.Rescue.Core.Ntfs;

/// <summary>Parsed NTFS Volume Boot Record — the entry point that locates the $MFT.</summary>
public sealed class NtfsBootSector
{
    public int BytesPerSector { get; private init; }

    public int SectorsPerCluster { get; private init; }

    public int ClusterSize => BytesPerSector * SectorsPerCluster;

    public long TotalSectors { get; private init; }

    public long MftStartLcn { get; private init; }

    public long MftMirrorLcn { get; private init; }

    public int MftRecordSize { get; private init; }

    public long MftByteOffset => MftStartLcn * ClusterSize;

    public long VolumeSizeBytes => (TotalSectors + 1) * BytesPerSector;

    /// <summary>Reads and validates the boot sector. Returns null if this is not an NTFS volume.</summary>
    public static NtfsBootSector? TryRead(ISectorSource source)
    {
        byte[] boot = source.ReadBestEffort(0, 512);
        if (boot.Length < 512)
        {
            return null;
        }

        // OEM ID at offset 3 is "NTFS    " for NTFS volumes.
        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
        {
            return null;
        }

        int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
        int sectorsPerCluster = boot[0x0D];
        if (bytesPerSector == 0 || sectorsPerCluster == 0)
        {
            return null;
        }

        sbyte clustersPerMftRec = (sbyte)boot[0x40];
        int clusterSize = bytesPerSector * sectorsPerCluster;
        int mftRecordSize = clustersPerMftRec >= 0
            ? clustersPerMftRec * clusterSize
            : 1 << (-clustersPerMftRec);

        return new NtfsBootSector
        {
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            TotalSectors = BitConverter.ToInt64(boot, 0x28),
            MftStartLcn = BitConverter.ToInt64(boot, 0x30),
            MftMirrorLcn = BitConverter.ToInt64(boot, 0x38),
            MftRecordSize = mftRecordSize,
        };
    }
}
