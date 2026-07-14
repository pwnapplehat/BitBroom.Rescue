using BitBroom.Rescue.Core.Carving;
using BitBroom.Rescue.Core.Fat;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Ntfs;

namespace BitBroom.Rescue.Core.Recovery;

public enum DetectedFileSystem { Unknown, Ntfs, Fat, ExFat }

/// <summary>
/// The single high-level entry point the CLI and GUI both use. Given a read-only source, it
/// detects the file system, runs the right metadata scanner (NTFS/FAT), and can additionally
/// carve for content when the file system is gone or the user wants a deep sweep. It keeps
/// the source open for the lifetime of the session so returned items can lazily fetch their
/// bytes.
/// </summary>
public sealed class RecoverySession : IDisposable
{
    private readonly ISectorSource _source;
    private readonly bool _ownsSource;

    public RecoverySession(ISectorSource source, bool ownsSource = true)
    {
        _source = source;
        _ownsSource = ownsSource;
        FileSystem = Detect(source);
    }

    public DetectedFileSystem FileSystem { get; }

    public string SourceDescription => _source.Description;

    public static DetectedFileSystem Detect(ISectorSource source)
    {
        if (NtfsBootSector.TryRead(source) is not null)
        {
            return DetectedFileSystem.Ntfs;
        }

        // exFAT identifies itself in the boot sector's OEM name field.
        byte[] boot = source.ReadBestEffort(0, 512);
        if (boot.Length >= 11 && System.Text.Encoding.ASCII.GetString(boot, 3, 8) == "EXFAT   ")
        {
            return DetectedFileSystem.ExFat;
        }

        if (FatVolume.TryOpen(source) is not null)
        {
            return DetectedFileSystem.Fat;
        }

        return DetectedFileSystem.Unknown;
    }

    /// <summary>
    /// Scans for deleted files via file-system metadata (the fast, high-fidelity path where
    /// names, folders and dates survive). Returns an empty list if the FS is unsupported for
    /// metadata scanning — callers should then offer carving.
    /// </summary>
    public List<RecoverableItem> ScanDeletedFiles(
        long minSize = 1,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        switch (FileSystem)
        {
            case DetectedFileSystem.Ntfs:
            {
                NtfsVolume vol = NtfsVolume.TryOpen(_source)!;
                return new NtfsDeletedFileScanner(vol).Scan(minSize, progress, cancellationToken);
            }

            case DetectedFileSystem.Fat:
            {
                FatVolume vol = FatVolume.TryOpen(_source)!;
                return vol.ScanDeleted(minSize, cancellationToken);
            }

            case DetectedFileSystem.ExFat:
            {
                ExFatVolume vol = ExFatVolume.TryOpen(_source)!;
                return vol.ScanDeleted(minSize, cancellationToken);
            }

            default:
                return [];
        }
    }

    /// <summary>Signature-carves the raw source for content (the fallback when metadata is gone).</summary>
    public List<RecoverableItem> Carve(
        IReadOnlyList<FileSignature>? signatures = null,
        IProgress<CarveProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => new FileCarver(_source, signatures).Carve(progress: progress, cancellationToken: cancellationToken);

    public void Dispose()
    {
        if (_ownsSource)
        {
            _source.Dispose();
        }
    }
}
