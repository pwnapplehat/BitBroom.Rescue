using System.Text;

namespace BitBroom.Rescue.Core.Recovery;

public sealed record RecoveryWriteResult(int Written, int Failed, long BytesWritten, string LogPath);

/// <summary>
/// Writes recovered items to a destination directory — but only after
/// <see cref="RecoveryDestinationGuard"/> confirms it isn't the source volume. Filenames are
/// made collision-free, carved files are grouped by type, and every write (or failure) is
/// recorded in a plain-text audit log so the user has a complete, honest record of what was
/// recovered and from where.
/// </summary>
public sealed class RecoveryWriter
{
    private readonly string _destination;
    private readonly string? _sourceRoot;

    public RecoveryWriter(string destination, string? sourceRoot)
    {
        string? refusal = RecoveryDestinationGuard.Validate(sourceRoot, destination);
        if (refusal is not null)
        {
            throw new InvalidOperationException("Unsafe recovery destination: " + refusal);
        }

        _destination = Path.GetFullPath(destination);
        _sourceRoot = sourceRoot;
    }

    public RecoveryWriteResult WriteAll(
        IEnumerable<RecoverableItem> items,
        IProgress<(int Done, string Name)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_destination);
        string logPath = Path.Combine(_destination, $"bitbroom-rescue-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        using var log = new StreamWriter(logPath, append: false, Encoding.UTF8);
        log.WriteLine($"BitBroom Rescue recovery log — {DateTime.Now:u}");
        log.WriteLine($"Source: {_sourceRoot ?? "(image/unknown)"}   Destination: {_destination}");
        log.WriteLine(new string('-', 72));

        int written = 0, failed = 0, done = 0;
        long bytes = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RecoverableItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            try
            {
                byte[] content = item.ContentProvider(cancellationToken);
                string targetDir = item.Source == RecoverySource.Carved
                    ? Path.Combine(_destination, "carved", SafeSegment(item.Extension.Length > 0 ? item.Extension : "bin"))
                    : Path.Combine(_destination, ReconstructRelativeDir(item.OriginalPath));
                Directory.CreateDirectory(targetDir);

                string finalPath = MakeUnique(Path.Combine(targetDir, SafeSegment(item.Name)), used);
                File.WriteAllBytes(finalPath, content);
                written++;
                bytes += content.Length;
                log.WriteLine($"OK   {content.Length,12:N0}  [{item.Confidence}]  {finalPath}");
                progress?.Report((done, item.Name));
            }
            catch (Exception ex)
            {
                failed++;
                log.WriteLine($"FAIL              {item.Name}  — {ex.Message}");
            }
        }

        log.WriteLine(new string('-', 72));
        log.WriteLine($"Recovered {written} file(s), {bytes:N0} bytes. {failed} failed.");
        return new RecoveryWriteResult(written, failed, bytes, logPath);
    }

    private static string ReconstructRelativeDir(string? originalPath)
    {
        if (string.IsNullOrEmpty(originalPath))
        {
            return "recovered";
        }

        string trimmed = originalPath.Replace('/', '\\').TrimStart('\\');
        string dir = Path.GetDirectoryName(trimmed) ?? string.Empty;
        var parts = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(SafeSegment);
        return Path.Combine(new[] { "recovered" }.Concat(parts).ToArray());
    }

    private static string SafeSegment(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(s) ? "_" : s;
    }

    private static string MakeUnique(string path, HashSet<string> used)
    {
        if (used.Add(path) && !File.Exists(path))
        {
            return path;
        }

        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (used.Add(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
