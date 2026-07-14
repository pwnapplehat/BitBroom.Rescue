using BitBroom.Rescue.Core.Ntfs;

namespace BitBroom.Rescue.Core.Recovery;

public sealed record ScanProgress(long RecordsSeen, long RecordsTotal, int Found, string? CurrentName);

/// <summary>
/// Turns an NTFS volume's deleted MFT records into confidence-scored <see cref="RecoverableItem"/>s,
/// reconstructing original paths from parent references. This is the Phase-1 workhorse: the
/// "I deleted the wrong file / emptied the Recycle Bin" case, where names, folders and
/// timestamps all survive and recovery is fast and high-fidelity.
/// </summary>
public sealed class NtfsDeletedFileScanner
{
    private readonly NtfsVolume _volume;

    public NtfsDeletedFileScanner(NtfsVolume volume)
    {
        _volume = volume;
    }

    /// <summary>
    /// Scans the whole MFT for deleted files. Directory records are collected first so paths
    /// can be reconstructed. <paramref name="minSize"/> filters out empty files.
    /// </summary>
    public List<RecoverableItem> Scan(
        long minSize = 1,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _volume.LoadMftRunList();

        // First pass: index every record's best name + parent so we can build paths, and
        // stash deleted-file records for the second pass. (The MFT isn't huge relative to a
        // modern machine — this keeps path resolution simple and correct.)
        var nameByRecord = new Dictionary<long, (string Name, long Parent)>();
        var deleted = new List<MftRecord>();
        long seen = 0;
        long total = _volume.EstimatedRecordCount;

        foreach (MftRecord rec in _volume.EnumerateRecords(cancellationToken))
        {
            seen++;
            FileNameInfo? best = rec.BestName;
            if (best is not null)
            {
                nameByRecord[rec.RecordNumber] = (best.Name, best.ParentRecordNumber);
            }

            if (!rec.InUse && !rec.IsDirectory && best is not null)
            {
                deleted.Add(rec);
            }

            if (seen % 20000 == 0)
            {
                progress?.Report(new ScanProgress(seen, total, deleted.Count, best?.Name));
            }
        }

        // Second pass: build items.
        var items = new List<RecoverableItem>(deleted.Count);
        foreach (MftRecord rec in deleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NtfsAttribute? data = rec.PrimaryData;
            if (data is null)
            {
                continue;
            }

            long size = data.RealSize;
            if (size < minSize)
            {
                continue;
            }

            FileNameInfo best = rec.BestName!;
            string path = BuildPath(best.Name, best.ParentRecordNumber, nameByRecord);
            (RecoveryConfidence confidence, string reason) = ScoreConfidence(data);

            NtfsAttribute captured = data;
            items.Add(new RecoverableItem
            {
                Name = best.Name,
                OriginalPath = path,
                SizeBytes = size,
                ModifiedUtc = rec.ModifiedUtc,
                CreatedUtc = rec.CreatedUtc,
                Source = RecoverySource.FileSystemMetadata,
                Confidence = confidence,
                ConfidenceReason = reason,
                IsResident = data.IsResident,
                ContentProvider = _ => _volume.ReadAttributeData(captured),
            });
        }

        progress?.Report(new ScanProgress(seen, total, items.Count, null));
        return items;
    }

    private (RecoveryConfidence, string) ScoreConfidence(NtfsAttribute data)
    {
        if (data.IsResident)
        {
            return (RecoveryConfidence.High, "content stored inside the file record (resident) — intact");
        }

        if (data.Runs.Count == 0)
        {
            return (RecoveryConfidence.Poor, "no cluster runs survive — content location lost");
        }

        // Heuristic: a single contiguous run is the common, high-fidelity case. Many
        // fragments raise the chance a piece was reallocated. We do NOT claim certainty.
        int fragments = 0;
        foreach (DataRun r in data.Runs)
        {
            if (!r.IsSparse)
            {
                fragments++;
            }
        }

        return fragments switch
        {
            1 => (RecoveryConfidence.Good, "single contiguous cluster run — likely intact if not overwritten"),
            <= 4 => (RecoveryConfidence.Fair, $"{fragments} fragments — some pieces may have been reused"),
            _ => (RecoveryConfidence.Fair, $"{fragments} fragments — higher chance of partial recovery"),
        };
    }

    private static string BuildPath(string name, long parent, Dictionary<long, (string Name, long Parent)> index)
    {
        var parts = new List<string> { name };
        long current = parent;
        var guard = new HashSet<long>();

        // Record 5 is the root directory; stop there. Guard against cycles / missing links.
        while (current != 5 && current != 0 && guard.Add(current))
        {
            if (!index.TryGetValue(current, out (string Name, long Parent) node))
            {
                parts.Insert(0, "?");
                break;
            }

            parts.Insert(0, node.Name);
            current = node.Parent;
        }

        return "\\" + string.Join("\\", parts);
    }
}
