namespace BitBroom.Rescue.Core.Carving;

/// <summary>
/// How a carver decides where a carved file ends once it has found the header.
/// </summary>
public enum SizeStrategy
{
    /// <summary>Scan forward for a footer byte sequence (e.g. JPEG FF D9).</summary>
    Footer,

    /// <summary>The size is encoded in the header at a known offset (e.g. RIFF, PNG via chunks).</summary>
    EmbeddedLength,

    /// <summary>No reliable end marker — carve up to MaxSize (truncated/validated afterwards).</summary>
    MaxSize,
}

/// <summary>
/// A file-format signature the carver recognizes: the header ("magic") that marks the start,
/// how to determine the end, an upper bound, and an optional validator that confirms the
/// carved bytes are a real, coherent file of this type (so we don't hand the user garbage
/// with a valid-looking header). Validators are what let BitBroom Rescue report honest
/// confidence instead of a fake green flag.
/// </summary>
public sealed class FileSignature
{
    public required string Id { get; init; }

    public required string Extension { get; init; }

    public required byte[] Header { get; init; }

    /// <summary>Optional footer for <see cref="SizeStrategy.Footer"/> formats.</summary>
    public byte[]? Footer { get; init; }

    /// <summary>Bytes appended after the footer match (e.g. some formats include trailing padding).</summary>
    public int FooterTrailing { get; init; }

    public required SizeStrategy SizeStrategy { get; init; }

    public long MaxSize { get; init; } = 64L * 1024 * 1024;

    public long MinSize { get; init; } = 1;

    /// <summary>For <see cref="SizeStrategy.EmbeddedLength"/>: compute total file size from a header window.</summary>
    public Func<ReadOnlySpan<byte>, long>? SizeFromHeader { get; init; }

    /// <summary>Optional coherence check on the carved bytes; null means "header match is enough".</summary>
    public Func<byte[], bool>? Validate { get; init; }

    /// <summary>Human-readable category for grouping in the UI.</summary>
    public string Category { get; init; } = "File";
}
