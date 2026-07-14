using System.Buffers.Binary;
using System.Text;

namespace BitBroom.Rescue.Core.Carving;

/// <summary>One ISO-BMFF (MP4/MOV) top-level box found on disk.</summary>
public readonly record struct Mp4Box(long Offset, long Size, string Type, bool SizeValid);

/// <summary>
/// Structure-aware scanner for ISO Base Media File Format (MP4/MOV/HEIF). Rather than
/// header→footer carving (which fails on the fragmented, interleaved files that action cams
/// and phones produce), it walks the box structure: ftyp, moov, mdat, moof, etc. This is the
/// foundation for correctly reassembling video whose ftyp/moov and mdat are separated on
/// disk — the exact case where naive carvers hand back unplayable files.
///
/// A well-formed MP4 is a sequence of boxes, each: [uint32 size][4-char type][payload]. A
/// size of 1 means a 64-bit size follows the type; size 0 means "to end of file".
/// </summary>
public static class Mp4BoxScanner
{
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "ftyp", "moov", "mdat", "free", "skip", "wide", "moof", "mfra", "pdin",
        "uuid", "meta", "meco", "styp", "sidx", "ssix", "prft",
    };

    /// <summary>
    /// Reads the box chain starting at <paramref name="data"/>[start], stopping at the first
    /// box whose type is unrecognized or whose size is implausible. Returns the boxes and the
    /// total contiguous, structurally-valid length from <paramref name="start"/>.
    /// </summary>
    public static (List<Mp4Box> Boxes, long ContiguousLength) ScanChain(ReadOnlySpan<byte> data, int start = 0)
    {
        var boxes = new List<Mp4Box>();
        int p = start;
        long contiguous = 0;

        while (p + 8 <= data.Length)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(p, 4));
            string type = Encoding.ASCII.GetString(data.Slice(p + 4, 4));

            if (!IsPlausibleType(type))
            {
                break;
            }

            long size = size32;
            int headerLen = 8;
            if (size32 == 1)
            {
                if (p + 16 > data.Length)
                {
                    break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(p + 8, 8));
                headerLen = 16;
            }
            else if (size32 == 0)
            {
                // "to EOF" — take the rest of the buffer.
                size = data.Length - p;
            }

            if (size < headerLen)
            {
                boxes.Add(new Mp4Box(p, size, type, SizeValid: false));
                break;
            }

            boxes.Add(new Mp4Box(p, size, type, SizeValid: true));
            contiguous = Math.Min(p + size, data.Length);
            if (p + size > data.Length)
            {
                break; // box extends beyond our buffer — caller may need a bigger window
            }

            p += (int)size;
        }

        return (boxes, contiguous);
    }

    /// <summary>True if the box chain begins with a valid ftyp box (i.e. this is a real MP4 start).</summary>
    public static bool StartsWithFtyp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            return false;
        }

        string type = Encoding.ASCII.GetString(data.Slice(4, 4));
        return type == "ftyp";
    }

    /// <summary>Returns true when the chain contains both the metadata (moov) and media (mdat) boxes.</summary>
    public static bool HasMoovAndMdat(IEnumerable<Mp4Box> boxes)
    {
        bool moov = false, mdat = false;
        foreach (Mp4Box b in boxes)
        {
            if (b.Type == "moov")
            {
                moov = true;
            }

            if (b.Type == "mdat")
            {
                mdat = true;
            }
        }

        return moov && mdat;
    }

    private static bool IsPlausibleType(string type)
    {
        if (KnownTypes.Contains(type))
        {
            return true;
        }

        // Accept any 4-printable-ASCII type (many valid boxes aren't in our short list),
        // which keeps the walk going through vendor boxes while rejecting random binary.
        foreach (char c in type)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return false;
            }
        }

        return true;
    }
}
