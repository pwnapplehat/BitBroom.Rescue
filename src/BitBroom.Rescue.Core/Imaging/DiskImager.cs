using System.Globalization;
using System.Text;
using BitBroom.Rescue.Core.Io;

namespace BitBroom.Rescue.Core.Imaging;

public sealed record ImagingProgress(
    long BytesCopied, long BytesTotal, long BadBytes, string Phase, double RateMBps);

public sealed record ImagingResult(long BytesCopied, long BadBytes, int BadRegions, string ImagePath, string MapPath);

/// <summary>
/// Clone-first imaging, ddrescue-style. Copies a source device to an image file in two
/// passes: a fast first pass that grabs all easily-readable data and records unreadable
/// regions in a map file, then a retry pass that re-attempts only the bad regions. Bad
/// sectors are written as zeros in the image and tracked in the map, so the image is always
/// the same size as the source and recovery can run against it. The map file also enables
/// resume. The source is only ever read; only the image and map files are written.
///
/// This is the safe path for a struggling drive: image once, then run unlimited scans and
/// carves against the image without ever stressing the failing hardware again.
/// </summary>
public sealed class DiskImager
{
    private readonly ISectorSource _source;
    private readonly int _blockSize;

    public DiskImager(ISectorSource source, int blockSize = 1024 * 1024)
    {
        _source = source;
        _blockSize = Math.Max(source.SectorSize, blockSize);
    }

    public ImagingResult CreateImage(
        string imagePath,
        string? mapPath = null,
        int retryPasses = 2,
        IProgress<ImagingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        mapPath ??= imagePath + ".map";
        long total = _source.Length;
        if (total <= 0)
        {
            throw new InvalidOperationException("Source length is unknown; cannot image this device.");
        }

        var badRegions = new List<(long Start, long Length)>();
        long copied = 0;
        long badBytes = 0;

        using (var image = new FileStream(imagePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            image.SetLength(total);

            // ---- Pass 1: fast sequential copy, note bad regions ----
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long pos = 0;
            byte[] buffer = new byte[_blockSize];
            while (pos < total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(_blockSize, total - pos);
                int got = _source.Read(pos, buffer, 0, want);

                if (got == want)
                {
                    image.Seek(pos, SeekOrigin.Begin);
                    image.Write(buffer, 0, got);
                    copied += got;
                }
                else
                {
                    // Short read = unreadable region. Record it; leave zeros in the image.
                    if (got > 0)
                    {
                        image.Seek(pos, SeekOrigin.Begin);
                        image.Write(buffer, 0, got);
                        copied += got;
                    }

                    long badStart = pos + got;
                    long badLen = want - got;
                    RecordBad(badRegions, badStart, badLen);
                    badBytes += badLen;
                }

                pos += want;
                if (progress is not null)
                {
                    double mbps = clock.Elapsed.TotalSeconds > 0 ? copied / 1048576.0 / clock.Elapsed.TotalSeconds : 0;
                    progress.Report(new ImagingProgress(copied, total, badBytes, "Pass 1 (fast)", mbps));
                }
            }

            // ---- Retry passes: re-attempt only bad regions, finer-grained ----
            for (int pass = 0; pass < retryPasses && badRegions.Count > 0; pass++)
            {
                var stillBad = new List<(long Start, long Length)>();
                foreach ((long start, long length) in badRegions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long off = 0;
                    int fineBlock = Math.Max(_source.SectorSize, _blockSize / 16);
                    byte[] fine = new byte[fineBlock];
                    while (off < length)
                    {
                        int want = (int)Math.Min(fineBlock, length - off);
                        int got = _source.Read(start + off, fine, 0, want);
                        if (got > 0)
                        {
                            image.Seek(start + off, SeekOrigin.Begin);
                            image.Write(fine, 0, got);
                            copied += got;
                            badBytes -= got;
                        }

                        if (got < want)
                        {
                            RecordBad(stillBad, start + off + got, want - got);
                        }

                        off += want;
                    }

                    progress?.Report(new ImagingProgress(copied, total, badBytes, $"Retry pass {pass + 1}", 0));
                }

                badRegions = stillBad;
            }

            WriteMap(mapPath, total, badRegions);
        }

        return new ImagingResult(copied, badBytes, badRegions.Count, imagePath, mapPath);
    }

    private static void RecordBad(List<(long Start, long Length)> regions, long start, long length)
    {
        if (length <= 0)
        {
            return;
        }

        // Merge with the previous region if contiguous.
        if (regions.Count > 0)
        {
            (long pStart, long pLen) = regions[^1];
            if (pStart + pLen == start)
            {
                regions[^1] = (pStart, pLen + length);
                return;
            }
        }

        regions.Add((start, length));
    }

    private static void WriteMap(string mapPath, long total, List<(long Start, long Length)> bad)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# BitBroom Rescue image map (ddrescue-style)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# total_bytes {total}");
        sb.AppendLine("# offset            length            status(+=good, -=bad)");
        long cursor = 0;
        foreach ((long start, long length) in bad)
        {
            if (start > cursor)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"0x{cursor:X16}  0x{start - cursor:X16}  +");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"0x{start:X16}  0x{length:X16}  -");
            cursor = start + length;
        }

        if (cursor < total)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"0x{cursor:X16}  0x{total - cursor:X16}  +");
        }

        File.WriteAllText(mapPath, sb.ToString());
    }
}
