using System.Buffers.Binary;
using System.Text;
using BitBroom.Rescue.Core.Carving;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Recovery;
using Xunit;

namespace BitBroom.Rescue.Core.Tests;

public class CarvingTests
{
    private static byte[] MakeJpeg(int payload)
    {
        var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8, 0xFF, 0xE0]);              // SOI + APP0
        ms.Write(Encoding.ASCII.GetBytes("JFIF\0"));
        byte[] body = new byte[payload];
        new Random(payload).NextBytes(body);
        ms.Write(body);
        ms.Write([0xFF, 0xD9]);                           // EOI
        return ms.ToArray();
    }

    private static byte[] MakePng(int payload)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            byte[] len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            ms.Write(len);
            ms.Write(Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write(new byte[4]); // fake CRC (validator only checks IEND tail bytes)
        }

        Chunk("IHDR", new byte[13]);
        Chunk("IDAT", new byte[payload]);
        // IEND with the exact standard CRC so the validator's tail check passes.
        ms.Write([0x00, 0x00, 0x00, 0x00]);
        ms.Write(Encoding.ASCII.GetBytes("IEND"));
        ms.Write([0xAE, 0x42, 0x60, 0x82]);
        return ms.ToArray();
    }

    [Fact]
    public void Carves_contiguous_jpeg_between_junk_byte_exact()
    {
        byte[] jpeg = MakeJpeg(5000);
        var disk = new MemoryStream();
        disk.Write(new byte[4096]);          // leading junk (unallocated)
        long at = disk.Position;
        disk.Write(jpeg);
        disk.Write(new byte[10000]);         // trailing junk
        byte[] image = disk.ToArray();

        using var source = new MemorySource(image);
        var carver = new FileCarver(source);
        List<RecoverableItem> hits = carver.Carve();

        RecoverableItem jpg = Assert.Single(hits, h => h.Extension == "jpg");
        Assert.Equal(RecoveryConfidence.Good, jpg.Confidence);
        Assert.Equal(jpeg, jpg.ContentProvider(CancellationToken.None));
    }

    [Fact]
    public void Carves_png_via_embedded_length()
    {
        byte[] png = MakePng(3000);
        var disk = new MemoryStream();
        disk.Write(new byte[8192]);
        disk.Write(png);
        disk.Write(new byte[4096]);
        using var source = new MemorySource(disk.ToArray());

        var carver = new FileCarver(source);
        List<RecoverableItem> hits = carver.Carve();

        RecoverableItem hit = Assert.Single(hits, h => h.Extension == "png");
        Assert.Equal(png, hit.ContentProvider(CancellationToken.None));
    }

    [Fact]
    public void Finds_multiple_files_in_one_scan()
    {
        var disk = new MemoryStream();
        disk.Write(new byte[512]);
        disk.Write(MakeJpeg(2000));
        disk.Write(new byte[512]);
        disk.Write(MakePng(1500));
        disk.Write(new byte[512]);
        disk.Write(MakeJpeg(800));
        using var source = new MemorySource(disk.ToArray());

        var carver = new FileCarver(source);
        List<RecoverableItem> hits = carver.Carve();

        Assert.Equal(2, hits.Count(h => h.Extension == "jpg"));
        Assert.Equal(1, hits.Count(h => h.Extension == "png"));
    }

    [Fact]
    public void Header_without_valid_content_is_rejected()
    {
        // JPEG SOI but no EOI footer within range → validator rejects it.
        var disk = new MemoryStream();
        disk.Write(new byte[1024]);
        disk.Write([0xFF, 0xD8, 0xFF, 0xE0]);
        disk.Write(new byte[9000]); // no FF D9 anywhere
        using var source = new MemorySource(disk.ToArray());

        var carver = new FileCarver(source);
        List<RecoverableItem> hits = carver.Carve();

        Assert.DoesNotContain(hits, h => h.Extension == "jpg");
    }
}
