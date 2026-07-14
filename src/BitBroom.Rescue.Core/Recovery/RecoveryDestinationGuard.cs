namespace BitBroom.Rescue.Core.Recovery;

/// <summary>
/// The cardinal safety rule of data recovery: never write recovered files back onto the
/// drive you are recovering from. Doing so overwrites the very free space that still holds
/// other not-yet-recovered files. This guard refuses a destination that lives on the same
/// volume as the source, so a user in a panic literally cannot shoot themselves in the foot.
/// </summary>
public static class RecoveryDestinationGuard
{
    /// <summary>Null when <paramref name="destinationDir"/> is a safe place to write recovered files.</summary>
    public static string? Validate(string? sourceRoot, string destinationDir)
    {
        if (string.IsNullOrWhiteSpace(destinationDir))
        {
            return "no destination folder was chosen";
        }

        // Reject relative input on the RAW string — never resolve it against the current
        // directory (which would silently turn "recovered" into some real absolute path).
        if (!Path.IsPathFullyQualified(destinationDir))
        {
            return "the destination is not a full, absolute path";
        }

        string dest;
        try
        {
            dest = Path.GetFullPath(destinationDir);
        }
        catch (Exception)
        {
            return "the destination path is malformed";
        }

        string? destVolume = SafeGetPathRoot(dest);
        if (destVolume is null)
        {
            return "the destination is not a full, absolute path";
        }

        // If we know which volume the source came from, refuse a same-volume destination.
        if (!string.IsNullOrEmpty(sourceRoot))
        {
            string? sourceVolume = SafeGetPathRoot(sourceRoot);
            if (sourceVolume is not null &&
                string.Equals(sourceVolume, destVolume, StringComparison.OrdinalIgnoreCase))
            {
                return "the destination is on the SAME drive you're recovering from — " +
                       "that would overwrite the files you're trying to save. Choose a different drive.";
            }
        }

        return null;
    }

    public static bool IsSafe(string? sourceRoot, string destinationDir) => Validate(sourceRoot, destinationDir) is null;

    private static string? SafeGetPathRoot(string path)
    {
        try
        {
            string root = Path.GetPathRoot(path) ?? string.Empty;
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
