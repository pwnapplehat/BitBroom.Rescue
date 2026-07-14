using System.Text;
using BitBroom.Rescue.Core.Recovery;
using Xunit;

namespace BitBroom.Rescue.Core.Tests;

public class RecoveryWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbrescue-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Writes_items_reconstructs_folders_and_logs()
    {
        var items = new List<RecoverableItem>
        {
            new()
            {
                Name = "report.docx", OriginalPath = "\\Documents\\Work\\report.docx", SizeBytes = 5,
                Source = RecoverySource.FileSystemMetadata, Confidence = RecoveryConfidence.High,
                ContentProvider = _ => Encoding.UTF8.GetBytes("hello"),
            },
            new()
            {
                Name = "jpeg_1000.jpg", OriginalPath = null, SizeBytes = 3,
                Source = RecoverySource.Carved, Confidence = RecoveryConfidence.Good,
                ContentProvider = _ => [1, 2, 3],
            },
        };

        // Source is on C:, destination on temp (usually same volume in CI). To keep the test
        // volume-independent, pass sourceRoot=null (as when recovering from an image).
        var writer = new RecoveryWriter(_dir, sourceRoot: null);
        RecoveryWriteResult result = writer.WriteAll(items);

        Assert.Equal(2, result.Written);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(result.LogPath));

        string doc = Path.Combine(_dir, "recovered", "Documents", "Work", "report.docx");
        Assert.True(File.Exists(doc));
        Assert.Equal("hello", File.ReadAllText(doc));

        string carved = Path.Combine(_dir, "carved", "jpg", "jpeg_1000.jpg");
        Assert.True(File.Exists(carved));
    }

    [Fact]
    public void Refuses_construction_for_same_volume_destination()
    {
        // Destination on C: with source root C: must be refused at construction.
        Assert.Throws<InvalidOperationException>(() => new RecoveryWriter(@"C:\bbrescue_should_refuse", @"C:\"));
    }

    [Fact]
    public void Deduplicates_colliding_names()
    {
        var items = new List<RecoverableItem>
        {
            Make("dup.txt", "one"),
            Make("dup.txt", "two"),
            Make("dup.txt", "three"),
        };
        var writer = new RecoveryWriter(_dir, sourceRoot: null);
        RecoveryWriteResult result = writer.WriteAll(items);

        Assert.Equal(3, result.Written);
        string baseDir = Path.Combine(_dir, "recovered");
        Assert.True(File.Exists(Path.Combine(baseDir, "dup.txt")));
        Assert.True(File.Exists(Path.Combine(baseDir, "dup (1).txt")));
        Assert.True(File.Exists(Path.Combine(baseDir, "dup (2).txt")));
    }

    private static RecoverableItem Make(string name, string content) => new()
    {
        Name = name, OriginalPath = null, SizeBytes = content.Length,
        Source = RecoverySource.FileSystemMetadata, Confidence = RecoveryConfidence.High,
        ContentProvider = _ => Encoding.UTF8.GetBytes(content),
    };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
