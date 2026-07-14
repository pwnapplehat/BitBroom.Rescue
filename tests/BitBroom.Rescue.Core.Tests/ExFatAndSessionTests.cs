using System.Text;
using BitBroom.Rescue.Core.Fat;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Recovery;
using BitBroom.Rescue.Core.Tests.Synthetic;
using Xunit;

namespace BitBroom.Rescue.Core.Tests;

public class ExFatAndSessionTests
{
    [Fact]
    public void Recovers_deleted_exfat_photo_byte_exact_with_full_name()
    {
        byte[] jpeg = new byte[12000];
        new Random(42).NextBytes(jpeg);

        var img = new SyntheticExFatImage();
        img.AddFile("keeper.jpg", Encoding.UTF8.GetBytes("live"), deleted: false);
        img.AddFile("IMG_2026_vacation.jpg", jpeg, deleted: true);
        using var source = new MemorySource(img.Build());

        ExFatVolume vol = ExFatVolume.TryOpen(source)!;
        List<RecoverableItem> items = vol.ScanDeleted();

        RecoverableItem hit = Assert.Single(items);
        Assert.Equal("IMG_2026_vacation.jpg", hit.Name); // exFAT keeps the full long name
        Assert.Equal(RecoveryConfidence.Good, hit.Confidence); // contiguous
        Assert.Equal(jpeg, hit.ContentProvider(CancellationToken.None));
    }

    [Fact]
    public void Session_detects_each_filesystem()
    {
        var ntfs = new SyntheticNtfsImage();
        ntfs.AddFile("a.txt", Encoding.UTF8.GetBytes("x"), deleted: true);
        using (var s = new RecoverySession(new MemorySource(ntfs.Build())))
        {
            Assert.Equal(DetectedFileSystem.Ntfs, s.FileSystem);
        }

        var fat = new SyntheticFat32Image();
        fat.AddFile("A", "TXT", Encoding.UTF8.GetBytes("x"), deleted: true);
        using (var s = new RecoverySession(new MemorySource(fat.Build())))
        {
            Assert.Equal(DetectedFileSystem.Fat, s.FileSystem);
        }

        var exfat = new SyntheticExFatImage();
        exfat.AddFile("a.txt", Encoding.UTF8.GetBytes("x"), deleted: true);
        using (var s = new RecoverySession(new MemorySource(exfat.Build())))
        {
            Assert.Equal(DetectedFileSystem.ExFat, s.FileSystem);
        }
    }

    [Fact]
    public void Session_scans_deleted_across_filesystems()
    {
        var exfat = new SyntheticExFatImage();
        exfat.AddFile("gone.dat", new byte[2000], deleted: true);
        using var session = new RecoverySession(new MemorySource(exfat.Build()));
        List<RecoverableItem> items = session.ScanDeletedFiles();
        Assert.Single(items);
        Assert.Equal("gone.dat", items[0].Name);
    }
}
