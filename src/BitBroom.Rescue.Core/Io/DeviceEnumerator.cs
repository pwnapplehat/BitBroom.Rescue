namespace BitBroom.Rescue.Core.Io;

public enum StorageKind { Volume, PhysicalDisk, Image }

/// <summary>A storage target the user can scan: a mounted volume, a physical disk, or an image file.</summary>
public sealed class StorageDevice
{
    public required StorageKind Kind { get; init; }

    /// <summary>Volume drive letter (e.g. 'C') when Kind == Volume.</summary>
    public char? DriveLetter { get; init; }

    /// <summary>Physical disk number when Kind == PhysicalDisk.</summary>
    public int? DiskNumber { get; init; }

    public string? ImagePath { get; init; }

    public required string Label { get; init; }

    public long SizeBytes { get; init; }

    public string? FileSystem { get; init; }

    public bool IsRemovable { get; init; }

    /// <summary>Opens a READ-ONLY sector source for this device.</summary>
    public ISectorSource Open() => Kind switch
    {
        StorageKind.Volume => RawDeviceSource.OpenVolume(DriveLetter!.Value),
        StorageKind.PhysicalDisk => RawDeviceSource.OpenPhysicalDisk(DiskNumber!.Value),
        StorageKind.Image => new ImageFileSource(ImagePath!),
        _ => throw new InvalidOperationException("Unknown device kind."),
    };
}

/// <summary>Discovers scannable storage: mounted volumes and physical disks.</summary>
public static class DeviceEnumerator
{
    public static List<StorageDevice> ListVolumes()
    {
        var list = new List<StorageDevice>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            char letter = drive.Name.Length > 0 ? char.ToUpperInvariant(drive.Name[0]) : '?';
            long size = 0;
            string? fs = null;
            string label = $"{letter}:";
            try
            {
                if (drive.IsReady)
                {
                    size = drive.TotalSize;
                    fs = drive.DriveFormat;
                    if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
                    {
                        label = $"{letter}: ({drive.VolumeLabel})";
                    }
                }
            }
            catch (Exception)
            {
                // Unready/inaccessible volume — still list it so the user can try.
            }

            list.Add(new StorageDevice
            {
                Kind = StorageKind.Volume,
                DriveLetter = letter,
                Label = label,
                SizeBytes = size,
                FileSystem = fs,
                IsRemovable = drive.DriveType == DriveType.Removable,
            });
        }

        return list;
    }

    /// <summary>Probes \\.\PhysicalDrive0..maxProbe for existence (read-only) and reports their sizes.</summary>
    public static List<StorageDevice> ListPhysicalDisks(int maxProbe = 16)
    {
        var list = new List<StorageDevice>();
        for (int i = 0; i < maxProbe; i++)
        {
            try
            {
                using RawDeviceSource src = RawDeviceSource.OpenPhysicalDisk(i);
                list.Add(new StorageDevice
                {
                    Kind = StorageKind.PhysicalDisk,
                    DiskNumber = i,
                    Label = $"PhysicalDrive{i}",
                    SizeBytes = src.Length,
                });
            }
            catch (IOException)
            {
                // Not present or not accessible — stop probing consecutive misses early.
                if (i > 0 && list.Count == 0)
                {
                    break;
                }
            }
        }

        return list;
    }
}
