using System.Text;

namespace BitBroom.Rescue.Core.Ntfs;

/// <summary>NTFS attribute type codes we care about for recovery.</summary>
public enum NtfsAttrType : uint
{
    StandardInformation = 0x10,
    AttributeList = 0x20,
    FileName = 0x30,
    ObjectId = 0x40,
    Data = 0x80,
    IndexRoot = 0x90,
    IndexAllocation = 0xA0,
    Bitmap = 0xB0,
    End = 0xFFFFFFFF,
}

/// <summary>$FILE_NAME namespaces. Win32/POSIX names are preferred over the short DOS (8.3) alias.</summary>
public enum NtfsNamespace : byte
{
    Posix = 0,
    Win32 = 1,
    Dos = 2,
    Win32AndDos = 3,
}

/// <summary>
/// One extent of a non-resident attribute: <paramref name="ClusterCount"/> clusters starting
/// at logical cluster <paramref name="Lcn"/>. Sparse runs (holes) have no on-disk location.
/// </summary>
public readonly record struct DataRun(long Lcn, long ClusterCount, bool IsSparse);

/// <summary>A single parsed NTFS attribute (resident bytes or a non-resident runlist).</summary>
public sealed class NtfsAttribute
{
    public required NtfsAttrType Type { get; init; }

    public string? Name { get; init; }

    public bool IsResident { get; init; }

    /// <summary>Resident content (null when non-resident).</summary>
    public byte[]? ResidentData { get; init; }

    /// <summary>Actual (logical) data size in bytes.</summary>
    public long RealSize { get; init; }

    /// <summary>Runlist for non-resident attributes.</summary>
    public IReadOnlyList<DataRun> Runs { get; init; } = [];

    public bool IsUnnamedData => Type == NtfsAttrType.Data && string.IsNullOrEmpty(Name);
}

/// <summary>A recovered $FILE_NAME entry (name + the parent directory it lived in).</summary>
public sealed class FileNameInfo
{
    public required string Name { get; init; }

    public required NtfsNamespace Namespace { get; init; }

    /// <summary>MFT record number of the parent directory (low 48 bits of the reference).</summary>
    public long ParentRecordNumber { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime ModifiedUtc { get; init; }
}

/// <summary>
/// A fully parsed MFT record. Present whether the file is live or deleted; the
/// <see cref="InUse"/> flag distinguishes them (cleared = deleted). All the information
/// needed to recover a file — real name, parent, timestamps, and either resident bytes or
/// the cluster runlist — hangs off this object.
/// </summary>
public sealed class MftRecord
{
    public long RecordNumber { get; init; }

    public ushort Sequence { get; init; }

    public bool InUse { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>Base record number (0 = this is a base record; else it's an extension record).</summary>
    public long BaseRecordNumber { get; init; }

    public DateTime CreatedUtc { get; set; }

    public DateTime ModifiedUtc { get; set; }

    public List<NtfsAttribute> Attributes { get; } = [];

    public List<FileNameInfo> FileNames { get; } = [];

    /// <summary>Best display name — prefers Win32/POSIX over the DOS 8.3 alias.</summary>
    public FileNameInfo? BestName
    {
        get
        {
            FileNameInfo? best = null;
            foreach (FileNameInfo fn in FileNames)
            {
                if (best is null || (best.Namespace == NtfsNamespace.Dos && fn.Namespace != NtfsNamespace.Dos))
                {
                    best = fn;
                }
            }

            return best;
        }
    }

    /// <summary>The unnamed $DATA attribute (the file's primary content), if present.</summary>
    public NtfsAttribute? PrimaryData
    {
        get
        {
            foreach (NtfsAttribute a in Attributes)
            {
                if (a.IsUnnamedData)
                {
                    return a;
                }
            }

            return null;
        }
    }

    /// <summary>Alternate data streams (named $DATA attributes).</summary>
    public IEnumerable<NtfsAttribute> AlternateStreams
    {
        get
        {
            foreach (NtfsAttribute a in Attributes)
            {
                if (a.Type == NtfsAttrType.Data && !string.IsNullOrEmpty(a.Name))
                {
                    yield return a;
                }
            }
        }
    }
}

internal static class NtfsTime
{
    public static DateTime FromFileTime(long ft)
    {
        try
        {
            return ft <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(ft);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }
}
