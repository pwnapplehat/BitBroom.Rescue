namespace BitBroom.Rescue.Core.Io;

/// <summary>
/// A read-only, seekable byte source over a storage device or disk image. This is the
/// single abstraction the entire recovery engine reads through — NTFS/FAT parsers,
/// carvers and imagers all take an <see cref="ISectorSource"/> and never touch a raw OS
/// handle themselves.
///
/// The contract is deliberately read-only: there is no write method anywhere on this
/// interface or its implementations, which is how BitBroom Rescue guarantees at the type
/// level that a scan can never modify the media it is recovering from.
/// </summary>
public interface ISectorSource : IDisposable
{
    /// <summary>Total addressable length in bytes (0 if unknown, e.g. some raw devices).</summary>
    long Length { get; }

    /// <summary>Logical sector size in bytes (512 or 4096 typically).</summary>
    int SectorSize { get; }

    /// <summary>Human-readable description (device path or image file name).</summary>
    string Description { get; }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes at absolute byte <paramref name="offset"/>
    /// into <paramref name="buffer"/>. Returns the number of bytes actually read (may be less
    /// than requested at end-of-media or across an unreadable region). Never throws for a
    /// short read; throws only for programmer error (null buffer, negative offset).
    /// </summary>
    int Read(long offset, byte[] buffer, int bufferOffset, int count);
}

public static class SectorSourceExtensions
{
    /// <summary>Reads exactly <paramref name="count"/> bytes or throws <see cref="EndOfStreamException"/>.</summary>
    public static byte[] ReadExact(this ISectorSource source, long offset, int count)
    {
        byte[] buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = source.Read(offset + total, buffer, total, count - total);
            if (n <= 0)
            {
                throw new EndOfStreamException(
                    $"Wanted {count} bytes at offset {offset}, got {total} before the source ended or an unreadable region.");
            }

            total += n;
        }

        return buffer;
    }

    /// <summary>Reads up to <paramref name="count"/> bytes; the returned array is trimmed to what was read.</summary>
    public static byte[] ReadBestEffort(this ISectorSource source, long offset, int count)
    {
        byte[] buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = source.Read(offset + total, buffer, total, count - total);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        if (total == count)
        {
            return buffer;
        }

        byte[] trimmed = new byte[total];
        Array.Copy(buffer, trimmed, total);
        return trimmed;
    }
}
