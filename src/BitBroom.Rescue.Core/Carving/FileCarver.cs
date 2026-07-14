using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Recovery;

namespace BitBroom.Rescue.Core.Carving;

public sealed record CarveProgress(long BytesScanned, long BytesTotal, int Found);

/// <summary>
/// Signature-based file carver — the fallback when the file system is gone (formatted,
/// RAW, corrupted) and metadata recovery finds nothing. It scans the raw source for known
/// headers, then determines each file's end via footer / embedded-length / max-size, and
/// (where a validator exists) confirms the carved bytes are a coherent file before offering
/// them. Carved files have no original name or folder — that information lived in the file
/// system — so they're named by type and offset, exactly as an honest carver should.
///
/// This implementation carves CONTIGUOUS files (the common case). Fragmented-file
/// reconstruction (especially interleaved action-cam video) is handled separately in
/// <see cref="VideoStreamCarver"/> because it needs format-aware reassembly.
/// </summary>
public sealed class FileCarver
{
    private readonly ISectorSource _source;
    private readonly IReadOnlyList<FileSignature> _signatures;
    private readonly int _blockSize;

    public FileCarver(ISectorSource source, IReadOnlyList<FileSignature>? signatures = null, int blockSize = 4096)
    {
        _source = source;
        _signatures = signatures ?? SignatureLibrary.Default;
        _blockSize = Math.Max(512, blockSize);
    }

    /// <summary>
    /// Scans the whole source and yields a recoverable item per validated carve hit.
    /// <paramref name="startOffset"/>/<paramref name="length"/> can bound the scan (e.g. to
    /// unallocated space only, once the FS layer can report it).
    /// </summary>
    public List<RecoverableItem> Carve(
        long startOffset = 0,
        long length = -1,
        IProgress<CarveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<RecoverableItem>();
        long total = length >= 0 ? length : Math.Max(0, _source.Length - startOffset);
        if (total <= 0)
        {
            return items;
        }

        // Read in overlapping windows so a header near a block boundary is still found and we
        // have enough lookahead to read embedded-length headers.
        const int overlap = 64 * 1024;
        int windowSize = 4 * 1024 * 1024;
        long pos = startOffset;
        long end = startOffset + total;
        int maxHeader = 0;
        foreach (FileSignature s in _signatures)
        {
            maxHeader = Math.Max(maxHeader, s.Header.Length + SignatureLibrary.HeaderMatchOffset(s));
        }

        while (pos < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int want = (int)Math.Min(windowSize + overlap, end - pos);
            byte[] window = _source.ReadBestEffort(pos, want);
            if (window.Length == 0)
            {
                pos += windowSize; // skip an unreadable region
                continue;
            }

            int scanLimit = window.Length == want && want > overlap ? window.Length - overlap : window.Length - maxHeader;
            for (int i = 0; i < Math.Max(0, scanLimit); i++)
            {
                FileSignature? sig = MatchAt(window, i);
                if (sig is null)
                {
                    continue;
                }

                long fileOffset = pos + i;
                RecoverableItem? item = TryCarveAt(sig, fileOffset, cancellationToken);
                if (item is not null)
                {
                    items.Add(item);
                    progress?.Report(new CarveProgress(fileOffset - startOffset, total, items.Count));
                }
            }

            pos += windowSize;
            progress?.Report(new CarveProgress(Math.Min(pos, end) - startOffset, total, items.Count));
        }

        return items;
    }

    private FileSignature? MatchAt(byte[] window, int i)
    {
        foreach (FileSignature sig in _signatures)
        {
            int matchOffset = SignatureLibrary.HeaderMatchOffset(sig);
            int at = i + matchOffset;
            if (at + sig.Header.Length > window.Length)
            {
                continue;
            }

            if (window.AsSpan(at, sig.Header.Length).SequenceEqual(sig.Header))
            {
                return sig;
            }
        }

        return null;
    }

    // Above this size we don't buffer the carve in memory to validate it — we stream it out at
    // its structurally-determined length and report honest (Fair) confidence instead of
    // silently truncating a multi-GB file to a 2 GB byte[] and calling it "validated".
    private const long ValidationCap = 256L * 1024 * 1024;

    private RecoverableItem? TryCarveAt(FileSignature sig, long fileOffset, CancellationToken ct)
    {
        long size = DetermineSize(sig, fileOffset, ct);
        if (size < sig.MinSize || size > sig.MaxSize)
        {
            return null;
        }

        long capturedOffset = fileOffset;

        if (size <= ValidationCap)
        {
            // Small enough to buffer: read, validate, and reuse the bytes for the write.
            byte[] data = _source.ReadBestEffort(fileOffset, (int)size);
            if (data.Length == 0)
            {
                return null;
            }

            if (sig.Validate is not null && !sig.Validate(data))
            {
                return null; // header matched but content isn't a coherent file — don't offer garbage
            }

            byte[] captured = data;
            return new RecoverableItem
            {
                Name = $"{sig.Id}_{fileOffset:x}.{sig.Extension}",
                OriginalPath = null,
                SizeBytes = data.Length,
                Source = RecoverySource.Carved,
                Confidence = sig.Validate is not null ? RecoveryConfidence.Good : RecoveryConfidence.Fair,
                ConfidenceReason = sig.Validate is not null
                    ? "carved and validated as a coherent " + sig.Id.ToUpperInvariant() + " file"
                    : "carved by header signature (no end-marker validation for this type)",
                IsResident = false,
                ContentProvider = _ => captured,
                ContentStreamProvider = (stream, _) =>
                {
                    stream.Write(captured, 0, captured.Length);
                    return captured.Length;
                },
            };
        }

        // Large carve: stream at the structurally-determined length. We can't buffer it to run
        // the byte[] validator, so we're honest about that in the confidence/reason.
        long capturedSize = size;
        return new RecoverableItem
        {
            Name = $"{sig.Id}_{fileOffset:x}.{sig.Extension}",
            OriginalPath = null,
            SizeBytes = size,
            Source = RecoverySource.Carved,
            Confidence = RecoveryConfidence.Fair,
            ConfidenceReason = $"carved by structure at {size / (1024 * 1024)} MB — too large to fully validate in memory",
            IsResident = false,
            ContentProvider = _ => throw new InvalidOperationException(
                "Carved file exceeds the in-memory limit; recover via streaming (RecoveryWriter uses ContentStreamProvider)."),
            ContentStreamProvider = (stream, streamCt) => StreamRange(capturedOffset, capturedSize, stream, streamCt),
        };
    }

    private long StreamRange(long offset, long size, Stream dest, CancellationToken ct)
    {
        long written = 0;
        byte[] buffer = new byte[1 * 1024 * 1024];
        while (written < size)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buffer.Length, size - written);
            int got = _source.Read(offset + written, buffer, 0, want);
            if (got <= 0)
            {
                break; // unreadable region — stop best-effort
            }

            dest.Write(buffer, 0, got);
            written += got;
        }

        return written;
    }

    private long DetermineSize(FileSignature sig, long fileOffset, CancellationToken ct)
    {
        // ISO-BMFF (MP4/MOV/HEIC): walk the actual box chain on disk so huge videos are sized
        // correctly (the small header window can't span a multi-GB mdat). This is what makes
        // video carving reliable where naive header/footer carving truncates or overruns.
        if (sig.Id is "mp4" or "mov" or "heic")
        {
            long boxed = DetermineIsoBmffSize(fileOffset, sig.MaxSize, ct);
            return boxed > 0 ? boxed : sig.MaxSize;
        }

        switch (sig.SizeStrategy)
        {
            case SizeStrategy.EmbeddedLength when sig.SizeFromHeader is not null:
            {
                byte[] head = _source.ReadBestEffort(fileOffset, 4096);
                long s = sig.SizeFromHeader(head);
                return s > 0 ? s : sig.MaxSize;
            }

            case SizeStrategy.Footer when sig.Footer is not null:
            {
                long found = ScanForFooter(sig, fileOffset, ct);
                return found > 0 ? found : sig.MaxSize;
            }

            default:
                return sig.MaxSize;
        }
    }

    /// <summary>
    /// Streams the ISO-BMFF box chain on disk (reading only each 8/16-byte box header, never
    /// the payloads) and sums box sizes until a box's type stops being plausible ASCII or the
    /// max size is hit. Returns the total contiguous, structurally-valid length — the true
    /// file size for a contiguous MP4/MOV, and a safe bound for a fragmented one.
    /// </summary>
    private long DetermineIsoBmffSize(long fileOffset, long maxSize, CancellationToken ct)
    {
        long pos = fileOffset;
        long end = fileOffset + maxSize;
        bool sawFtyp = false;

        while (pos + 8 <= end)
        {
            ct.ThrowIfCancellationRequested();
            byte[] header = _source.ReadBestEffort(pos, 16);
            if (header.Length < 8)
            {
                break;
            }

            uint size32 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);

            if (!IsPlausibleBoxType(type))
            {
                break;
            }

            if (type == "ftyp")
            {
                sawFtyp = true;
            }

            long boxSize = size32;
            if (size32 == 1)
            {
                if (header.Length < 16)
                {
                    break;
                }

                boxSize = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
            }
            else if (size32 == 0)
            {
                boxSize = end - pos; // to end
            }

            if (boxSize < 8)
            {
                break;
            }

            pos += boxSize;
        }

        long total = pos - fileOffset;
        // Require at least a couple of boxes and an ftyp to trust the box walk.
        return sawFtyp && total >= 16 ? Math.Min(total, maxSize) : 0;
    }

    private static bool IsPlausibleBoxType(string type)
    {
        foreach (char c in type)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private long ScanForFooter(FileSignature sig, long fileOffset, CancellationToken ct)
    {
        byte[] footer = sig.Footer!;
        int chunk = 1024 * 1024;
        long scanned = 0;
        byte[] carry = [];

        while (scanned < sig.MaxSize)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(chunk, sig.MaxSize - scanned);
            byte[] block = _source.ReadBestEffort(fileOffset + scanned, want);
            if (block.Length == 0)
            {
                break;
            }

            // Join a small carry-over so a footer split across chunk boundaries is found.
            byte[] search = carry.Length == 0 ? block : Concat(carry, block);
            int idx = IndexOf(search, footer, 0);
            if (idx >= 0)
            {
                long fileEnd = scanned - carry.Length + idx + footer.Length + sig.FooterTrailing;
                return fileEnd;
            }

            int keep = Math.Min(footer.Length - 1, block.Length);
            carry = block.AsSpan(block.Length - keep).ToArray();
            scanned += block.Length;
            if (block.Length < want)
            {
                break;
            }
        }

        return 0;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}
