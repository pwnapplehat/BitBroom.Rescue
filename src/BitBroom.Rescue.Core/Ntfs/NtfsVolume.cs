using BitBroom.Rescue.Core.Io;

namespace BitBroom.Rescue.Core.Ntfs;

/// <summary>
/// A read-only view over an NTFS volume (live device or image). Locates and reads the whole
/// $MFT by following its own runlist, enumerates every record (live and deleted), rebuilds
/// full paths from parent references, and extracts file content — resident bytes straight
/// from the record, or non-resident data by reading the cluster runs. Everything is
/// read-only; the class never writes to the source.
/// </summary>
public sealed class NtfsVolume
{
    private readonly ISectorSource _source;
    private List<DataRun>? _mftRuns;
    private long _mftRecordCountEstimate;

    public NtfsVolume(ISectorSource source, NtfsBootSector boot)
    {
        _source = source;
        Boot = boot;
    }

    public NtfsBootSector Boot { get; }

    /// <summary>Opens an NTFS volume from a source, or returns null if it isn't NTFS.</summary>
    public static NtfsVolume? TryOpen(ISectorSource source)
    {
        NtfsBootSector? boot = NtfsBootSector.TryRead(source);
        return boot is null ? null : new NtfsVolume(source, boot);
    }

    /// <summary>Reads the $MFT's own record (0) and resolves its runlist so we can read all of it.</summary>
    public void LoadMftRunList()
    {
        byte[] rec0 = _source.ReadBestEffort(Boot.MftByteOffset, Boot.MftRecordSize);
        MftRecord? mft = MftRecordParser.Parse(rec0, Boot.BytesPerSector, 0);
        NtfsAttribute? data = mft?.PrimaryData;

        if (data is { IsResident: false, Runs.Count: > 0 })
        {
            _mftRuns = [.. data.Runs];
            long clusters = 0;
            foreach (DataRun run in _mftRuns)
            {
                clusters += run.ClusterCount;
            }

            _mftRecordCountEstimate = clusters * Boot.ClusterSize / Boot.MftRecordSize;
        }
        else
        {
            // Fallback: assume contiguous (rare — only if record 0 is unreadable).
            _mftRuns = null;
            _mftRecordCountEstimate = 0;
        }
    }

    /// <summary>Rough number of MFT records (for progress reporting). 0 if unknown.</summary>
    public long EstimatedRecordCount => _mftRecordCountEstimate;

    /// <summary>
    /// Enumerates every MFT record by walking the $MFT runlist (or contiguously as a
    /// fallback). Unreadable or malformed records are skipped. Records stream lazily so the
    /// whole table is never held in memory at once.
    /// </summary>
    public IEnumerable<MftRecord> EnumerateRecords(CancellationToken cancellationToken = default)
    {
        _mftRuns ??= TryLoadRunsOrNull();

        long recordNumber = 0;
        int recSize = Boot.MftRecordSize;

        if (_mftRuns is { Count: > 0 })
        {
            foreach (DataRun run in _mftRuns)
            {
                if (run.IsSparse)
                {
                    recordNumber += run.ClusterCount * Boot.ClusterSize / recSize;
                    continue;
                }

                long runByteStart = run.Lcn * Boot.ClusterSize;
                long runBytes = run.ClusterCount * Boot.ClusterSize;
                foreach (MftRecord rec in EnumerateRegion(runByteStart, runBytes, recordNumber, cancellationToken))
                {
                    yield return rec;
                }

                recordNumber += runBytes / recSize;
            }
        }
        else
        {
            // Contiguous fallback: read a bounded window from the MFT start.
            const long fallbackBytes = 1L * 1024 * 1024 * 1024;
            foreach (MftRecord rec in EnumerateRegion(Boot.MftByteOffset, fallbackBytes, 0, cancellationToken))
            {
                yield return rec;
            }
        }
    }

    private List<DataRun>? TryLoadRunsOrNull()
    {
        LoadMftRunList();
        return _mftRuns;
    }

    private IEnumerable<MftRecord> EnumerateRegion(long byteStart, long byteCount, long firstRecordNumber, CancellationToken ct)
    {
        int recSize = Boot.MftRecordSize;
        int chunkBytes = Math.Max(recSize, 4 * 1024 * 1024 / recSize * recSize);
        long done = 0;
        long recordNumber = firstRecordNumber;

        while (done < byteCount)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(chunkBytes, byteCount - done);
            byte[] chunk = _source.ReadBestEffort(byteStart + done, want);
            if (chunk.Length == 0)
            {
                break;
            }

            for (int off = 0; off + recSize <= chunk.Length; off += recSize)
            {
                byte[] rec = new byte[recSize];
                Array.Copy(chunk, off, rec, 0, recSize);
                MftRecord? parsed = MftRecordParser.Parse(rec, Boot.BytesPerSector, recordNumber);
                recordNumber++;
                if (parsed is not null)
                {
                    yield return parsed;
                }
            }

            done += chunk.Length;
            if (chunk.Length < want)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Extracts the bytes of an attribute (typically the primary $DATA). Resident data comes
    /// straight from the record; non-resident data is read cluster-run by cluster-run and
    /// truncated to the real size. Sparse runs yield zeros. Returns best-effort content:
    /// unreadable clusters become zero-filled gaps so a partial recovery still succeeds.
    /// </summary>
    public byte[] ReadAttributeData(NtfsAttribute attr)
    {
        if (attr.IsResident)
        {
            return attr.ResidentData ?? [];
        }

        long remaining = attr.RealSize;
        if (remaining <= 0)
        {
            return [];
        }

        byte[] output = new byte[remaining];
        long written = 0;
        int clusterSize = Boot.ClusterSize;

        foreach (DataRun run in attr.Runs)
        {
            if (written >= remaining)
            {
                break;
            }

            long runBytes = run.ClusterCount * clusterSize;
            long take = Math.Min(runBytes, remaining - written);

            if (run.IsSparse)
            {
                written += take; // leave zeros
                continue;
            }

            long diskOffset = run.Lcn * clusterSize;
            long copied = 0;
            while (copied < take)
            {
                int want = (int)Math.Min(1 * 1024 * 1024, take - copied);
                byte[] block = _source.ReadBestEffort(diskOffset + copied, want);
                if (block.Length == 0)
                {
                    break; // unreadable region — leave the rest of this run as zeros
                }

                Array.Copy(block, 0, output, written + copied, block.Length);
                copied += block.Length;
                if (block.Length < want)
                {
                    break;
                }
            }

            written += take;
        }

        return output;
    }

    /// <summary>
    /// Streams the bytes of an attribute directly into <paramref name="dest"/> without ever
    /// buffering the whole file in memory — the path used to recover files that can exceed
    /// 2&nbsp;GB (big videos, disk images, VM disks). Same read semantics as
    /// <see cref="ReadAttributeData"/>: sparse runs and unreadable clusters become zeros, and
    /// output is truncated to the real size. Returns the number of bytes written.
    /// </summary>
    public long WriteAttributeData(NtfsAttribute attr, Stream dest, CancellationToken cancellationToken = default)
    {
        if (attr.IsResident)
        {
            byte[] resident = attr.ResidentData ?? [];
            dest.Write(resident, 0, resident.Length);
            return resident.Length;
        }

        long remaining = attr.RealSize;
        if (remaining <= 0)
        {
            return 0;
        }

        int clusterSize = Boot.ClusterSize;
        long written = 0;
        byte[] zeros = new byte[Math.Min(1 * 1024 * 1024, Math.Max(clusterSize, 65536))];
        byte[] buffer = new byte[1 * 1024 * 1024];

        foreach (DataRun run in attr.Runs)
        {
            if (written >= remaining)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            long runBytes = run.ClusterCount * clusterSize;
            long take = Math.Min(runBytes, remaining - written);

            if (run.IsSparse)
            {
                WriteZeros(dest, take, zeros);
                written += take;
                continue;
            }

            long diskOffset = run.Lcn * clusterSize;
            long copied = 0;
            while (copied < take)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, take - copied);
                int got = _source.Read(diskOffset + copied, buffer, 0, want);
                if (got <= 0)
                {
                    // Unreadable region — zero-fill the remainder of this run so the file
                    // keeps its exact length and offsets, then move on (best-effort).
                    WriteZeros(dest, take - copied, zeros);
                    copied = take;
                    break;
                }

                dest.Write(buffer, 0, got);
                copied += got;
            }

            written += take;
        }

        return written;
    }

    private static void WriteZeros(Stream dest, long count, byte[] zeros)
    {
        while (count > 0)
        {
            int chunk = (int)Math.Min(zeros.Length, count);
            dest.Write(zeros, 0, chunk);
            count -= chunk;
        }
    }
}
