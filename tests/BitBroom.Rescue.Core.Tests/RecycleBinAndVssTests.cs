using System.Text;
using BitBroom.Rescue.Core.Recovery;
using BitBroom.Rescue.Core.RecycleBin;
using BitBroom.Rescue.Core.Vss;
using Xunit;

namespace BitBroom.Rescue.Core.Tests;

public class RecycleBinAndVssTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbrescue-bin", Guid.NewGuid().ToString("N"));

    public RecycleBinAndVssTests() => Directory.CreateDirectory(_dir);

    private static byte[] MakeIndexV2(string originalPath, long size, DateTime deleted)
    {
        int chars = originalPath.Length + 1; // include null terminator in the count
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(2L));                 // version 2
        ms.Write(BitConverter.GetBytes(size));               // file size
        ms.Write(BitConverter.GetBytes(deleted.ToFileTimeUtc()));
        ms.Write(BitConverter.GetBytes(chars));              // path char count
        ms.Write(Encoding.Unicode.GetBytes(originalPath + "\0"));
        return ms.ToArray();
    }

    [Fact]
    public void Parses_v2_index_original_path_size_and_time()
    {
        var when = new DateTime(2026, 7, 10, 9, 30, 0, DateTimeKind.Utc);
        byte[] idx = MakeIndexV2(@"C:\Users\me\Documents\taxes.pdf", 123456, when);

        RecycleIndex? parsed = RecycleBinScanner.ParseIndex(idx);
        Assert.NotNull(parsed);
        Assert.Equal(@"C:\Users\me\Documents\taxes.pdf", parsed!.OriginalPath);
        Assert.Equal(123456, parsed.Size);
        Assert.Equal(when, parsed.DeletedUtc);
    }

    [Fact]
    public void Scans_bin_directory_and_pairs_content()
    {
        // $I index + $R content with matching id.
        string id = "ABC123";
        byte[] content = Encoding.UTF8.GetBytes("recycled but intact");
        File.WriteAllBytes(Path.Combine(_dir, $"$I{id}.pdf"),
            MakeIndexV2(@"C:\Users\me\report.pdf", content.Length, DateTime.UtcNow));
        File.WriteAllBytes(Path.Combine(_dir, $"$R{id}.pdf"), content);

        List<RecoverableItem> items = RecycleBinScanner.ScanDirectory(_dir);

        RecoverableItem hit = Assert.Single(items);
        Assert.Equal("report.pdf", hit.Name);
        Assert.Equal(@"C:\Users\me\report.pdf", hit.OriginalPath);
        Assert.Equal(RecoveryConfidence.High, hit.Confidence);
        Assert.Equal(content, hit.ContentProvider(CancellationToken.None));
    }

    [Fact]
    public void Orphaned_index_without_content_is_skipped()
    {
        File.WriteAllBytes(Path.Combine(_dir, "$IORPHAN.txt"),
            MakeIndexV2(@"C:\gone.txt", 10, DateTime.UtcNow));
        // No matching $R file.
        Assert.Empty(RecycleBinScanner.ScanDirectory(_dir));
    }

    [Fact]
    public void Vss_parser_extracts_snapshots_from_vssadmin_output()
    {
        // Representative vssadmin 'list shadows' output.
        const string sample = """
            Contents of shadow copy set ID: {aaaa}
               Contained 1 shadow copies at creation time: 7/10/2026 9:00:00 AM
                  Shadow Copy ID: {bbbb}
                     Original Volume: (C:)\\?\Volume{cccc}\
                     Shadow Copy Volume: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12
                     Originating Machine: PC
                     Creation Time: 7/10/2026 9:00:00 AM
            Contents of shadow copy set ID: {dddd}
               Contained 1 shadow copies at creation time: 7/12/2026 3:00:00 PM
                  Shadow Copy ID: {eeee}
                     Original Volume: (D:)\\?\Volume{ffff}\
                     Shadow Copy Volume: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy15
                     Creation Time: 7/12/2026 3:00:00 PM
            """;

        List<ShadowCopy> shadows = ShadowCopyService.Parse(sample);
        Assert.Equal(2, shadows.Count);
        Assert.EndsWith("HarddiskVolumeShadowCopy12", shadows[0].DeviceObject);
        Assert.Equal('C', shadows[0].ForVolume);
        Assert.Equal('D', shadows[1].ForVolume);
        Assert.EndsWith("HarddiskVolumeShadowCopy15", shadows[1].DeviceObject);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
