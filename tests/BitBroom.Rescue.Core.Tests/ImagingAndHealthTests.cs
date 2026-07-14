using BitBroom.Rescue.Core.Health;
using BitBroom.Rescue.Core.Imaging;
using BitBroom.Rescue.Core.Io;
using Xunit;

namespace BitBroom.Rescue.Core.Tests;

/// <summary>A source with a defined unreadable region, to prove imaging handles bad sectors.</summary>
file sealed class FlakySource(byte[] data, long badStart, long badLength, int sectorSize = 512) : ISectorSource
{
    public long Length => data.Length;
    public int SectorSize => sectorSize;
    public string Description => "flaky";

    public int Read(long offset, byte[] buffer, int bufferOffset, int count)
    {
        if (offset >= data.Length || count <= 0)
        {
            return 0;
        }

        // Reads that reach into the bad region return only the bytes before it (short read).
        long readable = data.Length - offset;
        if (offset < badStart + badLength && offset + count > badStart)
        {
            readable = Math.Max(0, badStart - offset);
        }

        int toCopy = (int)Math.Min(Math.Min(count, readable), data.Length - offset);
        if (toCopy > 0)
        {
            Array.Copy(data, offset, buffer, bufferOffset, toCopy);
        }

        return toCopy;
    }

    public void Dispose() { }
}

public class ImagingAndHealthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbrescue-img", Guid.NewGuid().ToString("N"));

    public ImagingAndHealthTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Images_a_clean_source_byte_exact_with_no_bad_regions()
    {
        byte[] data = new byte[256 * 1024];
        new Random(3).NextBytes(data);
        using var source = new MemorySource(data);

        string imgPath = Path.Combine(_dir, "clean.img");
        var imager = new DiskImager(source, blockSize: 64 * 1024);
        ImagingResult result = imager.CreateImage(imgPath);

        Assert.Equal(0, result.BadBytes);
        Assert.Equal(0, result.BadRegions);
        Assert.Equal(data, File.ReadAllBytes(imgPath));
        Assert.True(File.Exists(result.MapPath));
    }

    [Fact]
    public void Images_around_bad_sectors_zeros_the_gap_and_maps_it()
    {
        byte[] data = new byte[128 * 1024];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251 + 1); // non-zero everywhere so we can spot the zeroed gap
        }

        long badStart = 64 * 1024;
        long badLen = 8 * 1024;
        using var source = new FlakySource(data, badStart, badLen);

        string imgPath = Path.Combine(_dir, "flaky.img");
        var imager = new DiskImager(source, blockSize: 4096);
        ImagingResult result = imager.CreateImage(imgPath, retryPasses: 1);

        byte[] image = File.ReadAllBytes(imgPath);
        Assert.Equal(data.Length, image.Length);

        // Good data before the bad region is intact.
        Assert.Equal(data.AsSpan(0, (int)badStart).ToArray(), image.AsSpan(0, (int)badStart).ToArray());
        // Bad region is zero-filled.
        for (long i = badStart; i < badStart + badLen; i++)
        {
            Assert.Equal(0, image[i]);
        }

        // Good data after the bad region is intact again.
        int after = (int)(badStart + badLen);
        Assert.Equal(data.AsSpan(after).ToArray(), image.AsSpan(after).ToArray());

        Assert.True(result.BadBytes >= badLen);
        string map = File.ReadAllText(result.MapPath);
        Assert.Contains("-", map); // has at least one bad region marker
    }

    [Fact]
    public void Advisor_warns_hard_on_failing_drive()
    {
        var info = new DriveHealthInfo { Media = MediaType.Hdd, SmartPredictsFailure = true };
        var w = HealthAdvisor.Evaluate(info, RecoveryScenario.DeletedFiles);
        Assert.Contains(w, x => x.Severity == HealthSeverity.Danger && x.Title.Contains("FAILING"));
    }

    [Fact]
    public void Advisor_tells_the_ssd_trim_truth_for_deleted_files()
    {
        var info = new DriveHealthInfo { Media = MediaType.Ssd, TrimEnabled = true };
        var w = HealthAdvisor.Evaluate(info, RecoveryScenario.DeletedFiles);
        Assert.Contains(w, x => x.Title.Contains("TRIM") && x.Detail.Contains("unrecoverable"));

        // But for carving formatted space, the TRIM-deleted-file warning is not the point.
        var carve = HealthAdvisor.Evaluate(info, RecoveryScenario.Carving);
        Assert.DoesNotContain(carve, x => x.Title.Contains("deleted content is very likely already gone"));
    }

    [Fact]
    public void Advisor_always_reminds_to_recover_elsewhere()
    {
        var w = HealthAdvisor.Evaluate(new DriveHealthInfo(), RecoveryScenario.DeletedFiles);
        Assert.Contains(w, x => x.Title.Contains("different drive"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
