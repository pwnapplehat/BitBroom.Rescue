using System.Buffers.Binary;
using System.Text;
using BitBroom.Rescue.Core.Carving;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Recovery;
using Xunit;

namespace BitBroom.Rescue.Core.Tests;

public class Mp4CarvingTests
{
    private static void WriteBox(MemoryStream ms, string type, byte[] payload)
    {
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(8 + payload.Length));
        ms.Write(size);
        ms.Write(Encoding.ASCII.GetBytes(type));
        ms.Write(payload);
    }

    private static byte[] MakeMp4(int mdatSize)
    {
        var ms = new MemoryStream();
        WriteBox(ms, "ftyp", Encoding.ASCII.GetBytes("mp42mp41isom"));
        WriteBox(ms, "moov", new byte[200]);
        byte[] mdat = new byte[mdatSize];
        new Random(mdatSize).NextBytes(mdat);
        WriteBox(ms, "mdat", mdat);
        return ms.ToArray();
    }

    [Fact]
    public void Box_scanner_walks_ftyp_moov_mdat()
    {
        byte[] mp4 = MakeMp4(5000);
        (List<Mp4Box> boxes, long contiguous) = Mp4BoxScanner.ScanChain(mp4);

        Assert.True(Mp4BoxScanner.StartsWithFtyp(mp4));
        Assert.True(Mp4BoxScanner.HasMoovAndMdat(boxes));
        Assert.Equal(mp4.Length, contiguous);
        Assert.Equal(3, boxes.Count);
        Assert.Equal("ftyp", boxes[0].Type);
        Assert.Equal("mdat", boxes[2].Type);
    }

    [Fact]
    public void Carves_contiguous_mp4_via_box_walk_byte_exact()
    {
        byte[] mp4 = MakeMp4(200000); // 200 KB mdat — bigger than the 4 KB header window
        var disk = new MemoryStream();
        disk.Write(new byte[4096]);
        long at = disk.Position;
        disk.Write(mp4);
        disk.Write(new byte[8192]);
        using var source = new MemorySource(disk.ToArray());

        var carver = new FileCarver(source);
        List<RecoverableItem> hits = carver.Carve();

        RecoverableItem hit = Assert.Single(hits, h => h.Extension == "mp4");
        // Box walk must size it exactly, not truncate to the window or overrun into junk.
        Assert.Equal(mp4.Length, hit.SizeBytes);
        Assert.Equal(mp4, hit.ContentProvider(CancellationToken.None));
    }

    [Fact]
    public void Box_scanner_stops_at_garbage()
    {
        var ms = new MemoryStream();
        WriteBox(ms, "ftyp", Encoding.ASCII.GetBytes("mp42"));
        ms.Write([0x00, 0x00, 0x10, 0x00]); // size
        ms.Write([0x01, 0x02, 0x03, 0x04]); // non-ASCII "type" → not a real box
        (List<Mp4Box> boxes, _) = Mp4BoxScanner.ScanChain(ms.ToArray());
        Assert.Single(boxes); // only ftyp is trusted
    }
}
