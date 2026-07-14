using System.Text;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Ntfs;
using BitBroom.Rescue.Core.Tests.Synthetic;
using Xunit;

namespace BitBroom.Rescue.Core.Tests;

public class NtfsEngineTests
{
    private static NtfsVolume OpenSynthetic(SyntheticNtfsImage img, out ISectorSource source)
    {
        source = new MemorySource(img.Build());
        NtfsVolume? vol = NtfsVolume.TryOpen(source);
        Assert.NotNull(vol);
        vol!.LoadMftRunList();
        return vol;
    }

    [Fact]
    public void Reads_boot_sector_geometry()
    {
        var img = new SyntheticNtfsImage();
        img.AddFile("hello.txt", Encoding.UTF8.GetBytes("hello"), deleted: false);
        using var source = new MemorySource(img.Build());

        NtfsBootSector? boot = NtfsBootSector.TryRead(source);
        Assert.NotNull(boot);
        Assert.Equal(512, boot!.BytesPerSector);
        Assert.Equal(4096, boot.ClusterSize);
        Assert.Equal(1024, boot.MftRecordSize);
    }

    [Fact]
    public void Non_ntfs_source_returns_null()
    {
        using var source = new MemorySource(new byte[64 * 1024]);
        Assert.Null(NtfsBootSector.TryRead(source));
        Assert.Null(NtfsVolume.TryOpen(source));
    }

    [Fact]
    public void Recovers_deleted_file_name_and_resident_content_byte_exact()
    {
        var img = new SyntheticNtfsImage();
        byte[] secret = Encoding.UTF8.GetBytes("the quick brown fox — resident payload " + new string('x', 100));
        img.AddFile("keep-me.txt", Encoding.UTF8.GetBytes("still here"), deleted: false);
        img.AddFile("deleted-secret.txt", secret, deleted: true);

        NtfsVolume vol = OpenSynthetic(img, out ISectorSource source);
        using (source)
        {
            MftRecord? hit = FindByName(vol, "deleted-secret.txt");
            Assert.NotNull(hit);
            Assert.False(hit!.InUse); // deleted
            Assert.True(hit.PrimaryData!.IsResident);

            byte[] recovered = vol.ReadAttributeData(hit.PrimaryData!);
            Assert.Equal(secret, recovered);
        }
    }

    [Fact]
    public void Recovers_deleted_file_nonresident_content_byte_exact()
    {
        var img = new SyntheticNtfsImage();
        byte[] big = new byte[20000];
        new Random(1234).NextBytes(big);
        img.AddFile("big-deleted.bin", big, deleted: true, forceNonResident: true);

        NtfsVolume vol = OpenSynthetic(img, out ISectorSource source);
        using (source)
        {
            MftRecord? hit = FindByName(vol, "big-deleted.bin");
            Assert.NotNull(hit);
            Assert.False(hit!.PrimaryData!.IsResident);
            Assert.Equal(20000, hit.PrimaryData!.RealSize);

            byte[] recovered = vol.ReadAttributeData(hit.PrimaryData!);
            Assert.Equal(big, recovered);
        }
    }

    [Fact]
    public void Distinguishes_live_from_deleted_records()
    {
        var img = new SyntheticNtfsImage();
        img.AddFile("alive.txt", Encoding.UTF8.GetBytes("a"), deleted: false);
        img.AddFile("gone.txt", Encoding.UTF8.GetBytes("b"), deleted: true);

        NtfsVolume vol = OpenSynthetic(img, out ISectorSource source);
        using (source)
        {
            MftRecord? alive = FindByName(vol, "alive.txt");
            MftRecord? gone = FindByName(vol, "gone.txt");
            Assert.True(alive!.InUse);
            Assert.False(gone!.InUse);
        }
    }

    [Fact]
    public void Parent_reference_enables_path_reconstruction()
    {
        var img = new SyntheticNtfsImage();
        int docs = img.AddDirectory("Documents", deleted: false);
        img.AddFile("inside.txt", Encoding.UTF8.GetBytes("nested"), deleted: true, parent: docs);

        NtfsVolume vol = OpenSynthetic(img, out ISectorSource source);
        using (source)
        {
            MftRecord? file = FindByName(vol, "inside.txt");
            Assert.NotNull(file);
            long parent = file!.BestName!.ParentRecordNumber;
            Assert.Equal(docs, parent);

            MftRecord? parentDir = null;
            foreach (MftRecord r in vol.EnumerateRecords())
            {
                if (r.RecordNumber == parent)
                {
                    parentDir = r;
                    break;
                }
            }

            Assert.NotNull(parentDir);
            Assert.Equal("Documents", parentDir!.BestName!.Name);
        }
    }

    [Fact]
    public void Timestamps_survive_deletion()
    {
        var img = new SyntheticNtfsImage();
        img.AddFile("dated.txt", Encoding.UTF8.GetBytes("x"), deleted: true);

        NtfsVolume vol = OpenSynthetic(img, out ISectorSource source);
        using (source)
        {
            MftRecord? hit = FindByName(vol, "dated.txt");
            Assert.Equal(new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc), hit!.ModifiedUtc);
            Assert.Equal(new DateTime(2026, 2, 2, 10, 0, 0, DateTimeKind.Utc), hit.CreatedUtc);
        }
    }

    private static MftRecord? FindByName(NtfsVolume vol, string name)
    {
        foreach (MftRecord rec in vol.EnumerateRecords())
        {
            foreach (FileNameInfo fn in rec.FileNames)
            {
                if (fn.Name == name)
                {
                    return rec;
                }
            }
        }

        return null;
    }
}
