namespace BitBroom.Rescue.Core.Recovery;

/// <summary>How a recoverable item was found — drives the confidence estimate and UI grouping.</summary>
public enum RecoverySource
{
    /// <summary>Deleted file still described by an intact MFT/directory record (name + structure survive).</summary>
    FileSystemMetadata,

    /// <summary>Recovered from a Volume Shadow Copy snapshot (a real previous version).</summary>
    ShadowCopy,

    /// <summary>Found in the Recycle Bin ($Recycle.Bin) — trivially restorable.</summary>
    RecycleBin,

    /// <summary>Reconstructed by signature carving (no name/structure; content only).</summary>
    Carved,
}

/// <summary>
/// Honest recoverability confidence. This is the antidote to the industry's fake green
/// "Excellent" flags: it reflects what we can actually tell about whether the bytes are
/// intact, and we never claim more than the evidence supports.
/// </summary>
public enum RecoveryConfidence
{
    /// <summary>Resident/tiny data or a shadow-copy/recycle-bin item — content is essentially guaranteed.</summary>
    High,

    /// <summary>Metadata intact and cluster runs point at not-yet-reallocated space — likely intact.</summary>
    Good,

    /// <summary>Recoverable but at real risk of overwrite/fragmentation — may be partial or corrupt.</summary>
    Fair,

    /// <summary>Evidence suggests the data was overwritten or the drive uses TRIM — recovery unlikely.</summary>
    Poor,
}

/// <summary>One thing the user can recover, with everything needed to write it out and to judge it honestly.</summary>
public sealed class RecoverableItem
{
    public required string Name { get; init; }

    /// <summary>Reconstructed original path (best effort), e.g. "\Users\me\Documents\report.docx".</summary>
    public string? OriginalPath { get; init; }

    public long SizeBytes { get; init; }

    public DateTime ModifiedUtc { get; init; }

    public DateTime CreatedUtc { get; init; }

    public required RecoverySource Source { get; init; }

    public required RecoveryConfidence Confidence { get; init; }

    /// <summary>True when the entire content lives in metadata (resident) — no cluster reads needed.</summary>
    public bool IsResident { get; init; }

    /// <summary>Opaque handle the engine uses to fetch the bytes when the user chooses to recover.</summary>
    public required Func<CancellationToken, byte[]> ContentProvider { get; init; }

    /// <summary>A short, honest reason for the confidence level (shown in the UI/CLI).</summary>
    public string? ConfidenceReason { get; init; }

    public string Extension => System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
}
