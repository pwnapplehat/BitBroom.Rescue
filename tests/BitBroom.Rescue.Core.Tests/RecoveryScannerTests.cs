using System.Text;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Ntfs;
using BitBroom.Rescue.Core.Recovery;
using BitBroom.Rescue.Core.Tests.Synthetic;
using Xunit;

namespace BitBroom.Rescue.Core.Tests;

public class RecoveryScannerTests
{
    [Fact]
    public void Scanner_finds_deleted_files_with_paths_and_confidence()
    {
        var img = new SyntheticNtfsImage();
        int docs = img.AddDirectory("Documents", deleted: false);
        img.AddFile("alive.txt", Encoding.UTF8.GetBytes("live"), deleted: false);
        img.AddFile("resume.docx", Encoding.UTF8.GetBytes("small resident resume"), deleted: true, parent: docs);
        byte[] big = new byte[9000];
        new Random(7).NextBytes(big);
        img.AddFile("photo.raw", big, deleted: true, forceNonResident: true, parent: docs);

        using var source = new MemorySource(img.Build());
        NtfsVolume vol = NtfsVolume.TryOpen(source)!;
        var scanner = new NtfsDeletedFileScanner(vol);

        List<RecoverableItem> items = scanner.Scan(minSize: 1);

        // Only the two deleted files, not the live one.
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.Name == "alive.txt");

        RecoverableItem resume = items.Single(i => i.Name == "resume.docx");
        Assert.Equal(RecoveryConfidence.High, resume.Confidence); // resident
        Assert.True(resume.IsResident);
        Assert.Equal("\\Documents\\resume.docx", resume.OriginalPath);
        Assert.Equal("docx", resume.Extension);

        RecoverableItem photo = items.Single(i => i.Name == "photo.raw");
        Assert.False(photo.IsResident);
        Assert.Equal("\\Documents\\photo.raw", photo.OriginalPath);

        // Byte-exact content on demand.
        Assert.Equal(big, photo.ContentProvider(CancellationToken.None));
    }

    [Fact]
    public void Destination_guard_refuses_same_volume_as_source()
    {
        // Source came from C:, user tries to recover onto C: — must refuse.
        Assert.NotNull(RecoveryDestinationGuard.Validate(@"C:\", @"C:\Recovered"));
        Assert.Contains("SAME drive", RecoveryDestinationGuard.Validate(@"C:\", @"C:\Recovered")!);

        // Different drive is fine.
        Assert.Null(RecoveryDestinationGuard.Validate(@"C:\", @"D:\Recovered"));

        // Unknown source (e.g. recovering from an image) — allow, can't prove same-volume.
        Assert.Null(RecoveryDestinationGuard.Validate(null, @"D:\Recovered"));

        // Malformed destinations refused.
        Assert.NotNull(RecoveryDestinationGuard.Validate(@"C:\", "not-rooted"));
        Assert.NotNull(RecoveryDestinationGuard.Validate(@"C:\", ""));
    }
}
