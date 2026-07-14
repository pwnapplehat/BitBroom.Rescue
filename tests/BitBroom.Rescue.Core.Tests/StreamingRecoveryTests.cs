using System.Text;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Ntfs;
using BitBroom.Rescue.Core.Recovery;
using BitBroom.Rescue.Core.Tests.Synthetic;
using Xunit;

namespace BitBroom.Rescue.Core.Tests;

/// <summary>
/// Guards the streaming recovery path added so files larger than a 2 GB byte[] recover
/// correctly (and memory stays flat). These verify the writer prefers the streaming provider,
/// the NTFS streamer is byte-identical to the buffered reader, and timestamps are restored.
/// </summary>
public class StreamingRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbrescue-stream", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Writer_prefers_stream_provider_and_writes_byte_exact()
    {
        byte[] payload = new byte[300_000];
        new Random(7).NextBytes(payload);

        bool bufferedCalled = false;
        var item = new RecoverableItem
        {
            Name = "big.bin",
            OriginalPath = "\\data\\big.bin",
            SizeBytes = payload.Length,
            Source = RecoverySource.FileSystemMetadata,
            Confidence = RecoveryConfidence.Good,
            ContentProvider = _ =>
            {
                bufferedCalled = true; // must NOT be used when a stream provider exists
                return [];
            },
            ContentStreamProvider = (stream, _) =>
            {
                stream.Write(payload, 0, payload.Length);
                return payload.Length;
            },
        };

        var writer = new RecoveryWriter(_dir, sourceRoot: null);
        RecoveryWriteResult result = writer.WriteAll([item]);

        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.Failed);
        Assert.False(bufferedCalled);
        Assert.Equal(payload.Length, result.BytesWritten);

        string outPath = Path.Combine(_dir, "recovered", "data", "big.bin");
        Assert.True(File.Exists(outPath));
        Assert.Equal(payload, File.ReadAllBytes(outPath));
    }

    [Fact]
    public void Writer_restores_original_modified_time()
    {
        var when = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var item = new RecoverableItem
        {
            Name = "dated.txt",
            OriginalPath = "\\dated.txt",
            SizeBytes = 3,
            ModifiedUtc = when,
            Source = RecoverySource.FileSystemMetadata,
            Confidence = RecoveryConfidence.High,
            ContentProvider = _ => Encoding.UTF8.GetBytes("abc"),
        };

        var writer = new RecoveryWriter(_dir, sourceRoot: null);
        writer.WriteAll([item]);

        string outPath = Path.Combine(_dir, "recovered", "dated.txt");
        Assert.True(File.Exists(outPath));
        Assert.Equal(when, File.GetLastWriteTimeUtc(outPath));
    }

    [Fact]
    public void Ntfs_stream_writer_matches_buffered_reader_byte_exact()
    {
        var img = new SyntheticNtfsImage();
        byte[] big = new byte[50000];
        new Random(99).NextBytes(big);
        img.AddFile("streamed.bin", big, deleted: true, forceNonResident: true);

        using var source = new MemorySource(img.Build());
        NtfsVolume vol = NtfsVolume.TryOpen(source)!;
        vol.LoadMftRunList();

        MftRecord? hit = null;
        foreach (MftRecord rec in vol.EnumerateRecords())
        {
            if (rec.FileNames.Any(fn => fn.Name == "streamed.bin"))
            {
                hit = rec;
                break;
            }
        }

        Assert.NotNull(hit);
        NtfsAttribute data = hit!.PrimaryData!;
        Assert.False(data.IsResident);

        byte[] buffered = vol.ReadAttributeData(data);
        using var ms = new MemoryStream();
        long written = vol.WriteAttributeData(data, ms, default);

        Assert.Equal(big.Length, written);
        Assert.Equal(buffered, ms.ToArray());
        Assert.Equal(big, ms.ToArray());
    }

    [Fact]
    public void Ntfs_scanner_item_exposes_working_stream_provider()
    {
        var img = new SyntheticNtfsImage();
        byte[] big = new byte[40000];
        new Random(5).NextBytes(big);
        img.AddFile("nr.bin", big, deleted: true, forceNonResident: true);

        using var source = new MemorySource(img.Build());
        NtfsVolume vol = NtfsVolume.TryOpen(source)!;
        List<RecoverableItem> items = new NtfsDeletedFileScanner(vol).Scan(minSize: 1);

        RecoverableItem item = Assert.Single(items, i => i.Name == "nr.bin");
        Assert.NotNull(item.ContentStreamProvider);

        using var ms = new MemoryStream();
        long n = item.ContentStreamProvider!(ms, default);
        Assert.Equal(big.Length, n);
        Assert.Equal(big, ms.ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
