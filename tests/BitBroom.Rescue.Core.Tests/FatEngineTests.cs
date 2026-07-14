using System.Text;
using BitBroom.Rescue.Core.Fat;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Recovery;
using BitBroom.Rescue.Core.Tests.Synthetic;
using Xunit;

namespace BitBroom.Rescue.Core.Tests;

public class FatEngineTests
{
    [Fact]
    public void Opens_fat32_and_reports_geometry()
    {
        var img = new SyntheticFat32Image();
        img.AddFile("HELLO", "TXT", Encoding.UTF8.GetBytes("hi"), deleted: false);
        using var source = new MemorySource(img.Build());

        FatVolume? vol = FatVolume.TryOpen(source);
        Assert.NotNull(vol);
        Assert.Equal(FatKind.Fat32, vol!.Kind);
        Assert.Equal(512, vol.BytesPerSector);
    }

    [Fact]
    public void Recovers_deleted_fat_file_content_contiguous_byte_exact()
    {
        byte[] payload = new byte[3000];
        new Random(99).NextBytes(payload);

        var img = new SyntheticFat32Image();
        img.AddFile("ALIVE", "BIN", Encoding.UTF8.GetBytes("live file"), deleted: false);
        img.AddFile("SECRET", "DAT", payload, deleted: true);
        using var source = new MemorySource(img.Build());

        FatVolume vol = FatVolume.TryOpen(source)!;
        List<RecoverableItem> items = vol.ScanDeleted();

        RecoverableItem hit = Assert.Single(items);
        Assert.Equal(3000, hit.SizeBytes);
        Assert.Equal("DAT", hit.Extension.ToUpperInvariant());
        Assert.StartsWith("_ECRET", hit.Name); // first char lost to 0xE5, shown as '_'
        Assert.Equal(payload, hit.ContentProvider(CancellationToken.None));
    }

    [Fact]
    public void Live_files_are_not_reported_as_deleted()
    {
        var img = new SyntheticFat32Image();
        img.AddFile("KEEP", "TXT", Encoding.UTF8.GetBytes("keep me"), deleted: false);
        using var source = new MemorySource(img.Build());

        FatVolume vol = FatVolume.TryOpen(source)!;
        Assert.Empty(vol.ScanDeleted());
    }

    [Fact]
    public void Non_fat_source_returns_null()
    {
        using var source = new MemorySource(new byte[64 * 1024]);
        Assert.Null(FatVolume.TryOpen(source));
    }
}
