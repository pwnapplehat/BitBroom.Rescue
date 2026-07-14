namespace BitBroom.Rescue.Core.Io;

/// <summary>
/// A read-only <see cref="ISectorSource"/> over a raw disk-image file (.img/.dd/.raw or a
/// clone produced by BitBroom Rescue's imager). This is the recommended way to recover from
/// a failing drive (image first, then scan the image), and it is what the entire test suite
/// runs against — recovery logic is validated on synthetic images with zero real-disk or
/// admin dependency.
/// </summary>
public sealed class ImageFileSource : ISectorSource
{
    private readonly FileStream _stream;
    private readonly object _gate = new();

    public ImageFileSource(string path, int sectorSize = 512)
    {
        // Explicitly read-only + shareable: we never open an image for writing.
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Description = path;
        SectorSize = sectorSize;
        Length = _stream.Length;
    }

    /// <summary>Wraps an already-open read stream (e.g. a MemoryStream in tests). Does not own it.</summary>
    internal ImageFileSource(Stream stream, string description, int sectorSize)
    {
        _stream = stream as FileStream ?? throw new ArgumentException("Use ImageFileSource(path) or MemorySource for non-file streams.");
        Description = description;
        SectorSize = sectorSize;
        Length = _stream.Length;
    }

    public long Length { get; }

    public int SectorSize { get; }

    public string Description { get; }

    public int Read(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (offset >= Length || count <= 0)
        {
            return 0;
        }

        int want = (int)Math.Min(count, Length - offset);
        int total = 0;
        lock (_gate)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            while (total < want)
            {
                int n = _stream.Read(buffer, bufferOffset + total, want - total);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }
        }

        return total;
    }

    public void Dispose() => _stream.Dispose();
}
