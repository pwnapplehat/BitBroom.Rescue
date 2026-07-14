using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using BitBroom.Rescue.Core.Recovery;

namespace BitBroom.Rescue.Core.Vss;

/// <summary>One Volume Shadow Copy snapshot: a point-in-time device path plus when it was taken.</summary>
public sealed record ShadowCopy(string DeviceObject, DateTime CreatedUtc, char? ForVolume);

/// <summary>
/// Surfaces Volume Shadow Copies — Windows' built-in "Previous Versions". These are real,
/// uncorrupted snapshots of files as they existed earlier, so recovering from them is far
/// higher fidelity than undeleting: if a snapshot from before the loss exists, the file
/// comes back perfectly. This is the most under-used recovery source on Windows, and it
/// costs the user nothing because Windows already made the snapshots.
///
/// Snapshots are enumerated via vssadmin (admin) and their files read directly from the
/// snapshot device path (\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\...).
/// </summary>
public static partial class ShadowCopyService
{
    [GeneratedRegex(@"Shadow Copy Volume:\s*(\\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeRegex();

    [GeneratedRegex(@"Creation Time:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"Original Volume:.*\(([A-Za-z]):\)", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalVolRegex();

    /// <summary>Lists all shadow copies (requires admin). Empty list if VSS is unavailable/off.</summary>
    public static List<ShadowCopy> List()
    {
        string output = RunVssadmin("list shadows");
        return Parse(output);
    }

    internal static List<ShadowCopy> Parse(string output)
    {
        var result = new List<ShadowCopy>();
        // vssadmin prints a block per shadow copy; associate the nearest Creation Time and
        // Original Volume with each Shadow Copy Volume line.
        string[] lines = output.Replace("\r\n", "\n").Split('\n');
        DateTime pendingTime = DateTime.MinValue;
        char? pendingVol = null;

        foreach (string line in lines)
        {
            Match t = TimeRegex().Match(line);
            if (t.Success && DateTime.TryParse(t.Groups[1].Value.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime dt))
            {
                pendingTime = dt.ToUniversalTime();
            }

            Match ov = OriginalVolRegex().Match(line);
            if (ov.Success)
            {
                pendingVol = char.ToUpperInvariant(ov.Groups[1].Value[0]);
            }

            Match v = VolumeRegex().Match(line);
            if (v.Success)
            {
                result.Add(new ShadowCopy(v.Groups[1].Value, pendingTime, pendingVol));
            }
        }

        return result;
    }

    /// <summary>
    /// Recovers previous versions of a file at <paramref name="volumeRelativePath"/> (e.g.
    /// "Users\me\Documents\report.docx") from every snapshot of <paramref name="driveLetter"/>.
    /// Each returned item reads directly from that snapshot — a real earlier copy of the file.
    /// </summary>
    public static List<RecoverableItem> FindPreviousVersions(char driveLetter, string volumeRelativePath)
    {
        var items = new List<RecoverableItem>();
        char drive = char.ToUpperInvariant(driveLetter);
        string rel = volumeRelativePath.Replace('/', '\\').TrimStart('\\');

        foreach (ShadowCopy sc in List())
        {
            if (sc.ForVolume is { } v && v != drive)
            {
                continue;
            }

            // Files inside a snapshot are addressed as <device>\<relative path>.
            string full = sc.DeviceObject + "\\" + rel;
            long size;
            try
            {
                if (!File.Exists(full))
                {
                    continue;
                }

                size = new FileInfo(full).Length;
            }
            catch (Exception)
            {
                continue;
            }

            string captured = full;
            DateTime snapTime = sc.CreatedUtc;
            items.Add(new RecoverableItem
            {
                Name = Path.GetFileName(rel),
                OriginalPath = $"[{snapTime:yyyy-MM-dd HH:mm}] \\{rel}",
                SizeBytes = size,
                ModifiedUtc = snapTime,
                Source = RecoverySource.ShadowCopy,
                Confidence = RecoveryConfidence.High,
                ConfidenceReason = $"exact previous version from a {snapTime:yyyy-MM-dd} shadow copy",
                IsResident = false,
                ContentProvider = _ => File.ReadAllBytes(captured),
                ContentStreamProvider = (stream, ct) =>
                {
                    using FileStream src = File.OpenRead(captured);
                    src.CopyTo(stream, 1 << 20);
                    return src.Length;
                },
            });
        }

        return items;
    }

    private static string RunVssadmin(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "vssadmin.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return string.Empty;
            }

            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return output;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
