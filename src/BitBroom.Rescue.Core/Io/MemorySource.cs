namespace BitBroom.Rescue.Core.Io;

/// <summary>
/// A read-only <see cref="ISectorSource"/> over an in-memory byte array. Used by the test
/// suite's synthetic-filesystem builders so parser and carver logic can be validated
/// end-to-end without touching any real device.
/// </summary>
public sealed class MemorySource : ISectorSource
{
    private readonly byte[] _data;

    public MemorySource(byte[] data, int sectorSize = 512, string description = "memory")
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        SectorSize = sectorSize;
        Description = description;
    }

    public long Length => _data.Length;

    public int SectorSize { get; }

    public string Description { get; }

    public int Read(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (offset >= _data.Length || count <= 0)
        {
            return 0;
        }

        int toCopy = (int)Math.Min(count, _data.Length - offset);
        Array.Copy(_data, offset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}
