using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BitBroom.Rescue.Core.Io;

/// <summary>
/// A read-only <see cref="ISectorSource"/> over a live Windows volume (\\.\C:) or physical
/// disk (\\.\PhysicalDrive0). Raw device reads must be sector-aligned in both offset and
/// length, so this class transparently rounds every request out to sector boundaries and
/// returns the caller's exact slice. Requires Administrator.
/// </summary>
public sealed class RawDeviceSource : ISectorSource
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly object _gate = new();

    private RawDeviceSource(SafeFileHandle handle, string description, int sectorSize, long length)
    {
        _handle = handle;
        _stream = new FileStream(handle, FileAccess.Read);
        Description = description;
        SectorSize = sectorSize;
        Length = length;
    }

    public long Length { get; }

    public int SectorSize { get; }

    public string Description { get; }

    /// <summary>Opens a volume by drive letter, e.g. 'C'. Read-only; requires admin.</summary>
    public static RawDeviceSource OpenVolume(char driveLetter, int sectorSize = 512)
    {
        char letter = char.ToUpperInvariant(driveLetter);
        string path = $@"\\.\{letter}:";
        SafeFileHandle handle = NativeIo.OpenReadOnly(path);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open volume {letter}: read-only (Win32 error {err}). Administrator rights are required.");
        }

        long length = NativeIo.QueryLength(handle);
        return new RawDeviceSource(handle, path, sectorSize, length);
    }

    /// <summary>Opens a physical disk by number, e.g. 0 → \\.\PhysicalDrive0. Read-only; requires admin.</summary>
    public static RawDeviceSource OpenPhysicalDisk(int diskNumber, int sectorSize = 512)
    {
        string path = $@"\\.\PhysicalDrive{diskNumber}";
        SafeFileHandle handle = NativeIo.OpenReadOnly(path);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open {path} read-only (Win32 error {err}). Administrator rights are required.");
        }

        long length = NativeIo.QueryLength(handle);
        return new RawDeviceSource(handle, path, sectorSize, length);
    }

    public int Read(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (count <= 0)
        {
            return 0;
        }

        // Raw device reads must be sector-aligned. Round the window out, then copy the slice.
        long alignedStart = offset - (offset % SectorSize);
        long end = offset + count;
        long alignedEnd = end % SectorSize == 0 ? end : end + (SectorSize - end % SectorSize);
        int alignedLen = checked((int)(alignedEnd - alignedStart));

        byte[] scratch = new byte[alignedLen];
        int read;
        lock (_gate)
        {
            _stream.Seek(alignedStart, SeekOrigin.Begin);
            read = 0;
            while (read < alignedLen)
            {
                int n;
                try
                {
                    n = _stream.Read(scratch, read, alignedLen - read);
                }
                catch (IOException)
                {
                    // Unreadable sector/region — report a short read rather than throwing so
                    // imaging and best-effort scans can skip past bad areas.
                    break;
                }

                if (n <= 0)
                {
                    break;
                }

                read += n;
            }
        }

        int skip = (int)(offset - alignedStart);
        int available = Math.Max(0, read - skip);
        int toCopy = Math.Min(count, available);
        if (toCopy > 0)
        {
            Array.Copy(scratch, skip, buffer, bufferOffset, toCopy);
        }

        return toCopy;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _handle.Dispose();
    }
}
