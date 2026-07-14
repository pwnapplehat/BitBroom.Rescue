namespace BitBroom.Rescue.Core.Health;

public enum MediaType { Unknown, Hdd, Ssd }

public enum HealthSeverity { Info, Caution, Danger }

/// <summary>A single honest advisory shown before/around a scan.</summary>
public sealed record HealthWarning(HealthSeverity Severity, string Title, string Detail);

/// <summary>
/// The raw facts about a drive that shape honest recovery advice: is it an SSD, does it use
/// TRIM, and does its own SMART firmware predict failure? Gathered best-effort (unknowns are
/// fine); the advisory logic that turns these into warnings is pure and separately testable.
/// </summary>
public sealed record DriveHealthInfo
{
    public MediaType Media { get; init; } = MediaType.Unknown;

    /// <summary>Null = couldn't determine. True = the drive's SMART predicts imminent failure.</summary>
    public bool? SmartPredictsFailure { get; init; }

    /// <summary>Null = couldn't determine. True = TRIM/DeleteNotify is enabled system-wide.</summary>
    public bool? TrimEnabled { get; init; }

    public string? Model { get; init; }
}

public enum RecoveryScenario
{
    DeletedFiles,
    FormattedOrRaw,
    Carving,
}

/// <summary>
/// Turns <see cref="DriveHealthInfo"/> into the honest warnings that are BitBroom Rescue's
/// entire differentiator. We tell users the uncomfortable truths the paid tools hide:
/// TRIM makes SSD deletion unrecoverable, and a failing drive must be imaged, not scanned.
/// </summary>
public static class HealthAdvisor
{
    public static IReadOnlyList<HealthWarning> Evaluate(DriveHealthInfo info, RecoveryScenario scenario)
    {
        var warnings = new List<HealthWarning>();

        // 1) Failing hardware is the most important thing to catch — refuse-scan territory.
        if (info.SmartPredictsFailure == true)
        {
            warnings.Add(new HealthWarning(
                HealthSeverity.Danger,
                "This drive reports FAILING health (SMART predicts failure)",
                "Do not scan a failing drive directly — every read can accelerate its death. " +
                "Clone it to an image first (Tools → Create image / clone-first), then recover from the image. " +
                "If it is clicking, grinding, or not detected, stop now and consult a professional data-recovery lab."));
        }

        // 2) The SSD + TRIM truth, only relevant to already-deleted data (carving formatted
        //    space or reading live metadata is unaffected by this).
        if (info.Media == MediaType.Ssd && scenario == RecoveryScenario.DeletedFiles)
        {
            if (info.TrimEnabled == true)
            {
                warnings.Add(new HealthWarning(
                    HealthSeverity.Caution,
                    "SSD with TRIM enabled — deleted content is very likely already gone",
                    "On an SSD with TRIM, the drive controller physically discards deleted data within " +
                    "seconds and returns zeros for it. Filenames and structure may still be listed, but the " +
                    "bytes are usually unrecoverable by ANY software — this is a hardware reality, not a limit " +
                    "of this tool. Recovery is realistic only if you acted within moments of the deletion."));
            }
            else if (info.TrimEnabled == false)
            {
                warnings.Add(new HealthWarning(
                    HealthSeverity.Info,
                    "SSD with TRIM disabled — recovery is possible",
                    "TRIM is off on this system, so deleted data may still be present. Act quickly and avoid " +
                    "writing to the drive."));
            }
            else
            {
                warnings.Add(new HealthWarning(
                    HealthSeverity.Info,
                    "Solid-state drive",
                    "If TRIM is enabled (the Windows default), deleted file contents are often zeroed by the " +
                    "drive within seconds and cannot be recovered by any tool. Metadata may still list them."));
            }
        }

        // 3) Universal advice.
        warnings.Add(new HealthWarning(
            HealthSeverity.Info,
            "Recover to a different drive",
            "Never save recovered files back onto the drive you're recovering from — it overwrites the very " +
            "space still holding your other files. BitBroom Rescue enforces this."));

        return warnings;
    }
}
