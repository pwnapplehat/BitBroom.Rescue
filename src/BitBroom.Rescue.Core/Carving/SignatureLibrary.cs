using System.Buffers.Binary;
using System.Text;

namespace BitBroom.Rescue.Core.Carving;

/// <summary>
/// The built-in signature set. Written from scratch (format specs / public magic-number
/// references), NOT copied from any GPL carver, so BitBroom Rescue stays MIT-licensed.
/// Every signature that reasonably can carries a validator so carved output is verified,
/// not just header-matched.
/// </summary>
public static class SignatureLibrary
{
    public static IReadOnlyList<FileSignature> Default { get; } = Build();

    private static List<FileSignature> Build()
    {
        var list = new List<FileSignature>
        {
            // ---- Images ----
            new()
            {
                Id = "jpeg", Extension = "jpg", Category = "Images",
                Header = [0xFF, 0xD8, 0xFF],
                Footer = [0xFF, 0xD9],
                SizeStrategy = SizeStrategy.Footer,
                MaxSize = 64L * 1024 * 1024, MinSize = 128,
                Validate = ValidateJpeg,
            },
            new()
            {
                Id = "png", Extension = "png", Category = "Images",
                Header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                SizeStrategy = SizeStrategy.EmbeddedLength,
                SizeFromHeader = SizePng, MaxSize = 128L * 1024 * 1024,
                Validate = ValidatePng,
            },
            new()
            {
                Id = "gif", Extension = "gif", Category = "Images",
                Header = Encoding.ASCII.GetBytes("GIF89a"),
                Footer = [0x00, 0x3B], SizeStrategy = SizeStrategy.Footer,
            },
            new()
            {
                Id = "bmp", Extension = "bmp", Category = "Images",
                Header = [0x42, 0x4D], SizeStrategy = SizeStrategy.EmbeddedLength,
                SizeFromHeader = h => h.Length >= 6 ? BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(2)) : 0,
                MaxSize = 64L * 1024 * 1024,
            },
            new()
            {
                Id = "tiff-le", Extension = "tif", Category = "Images",
                Header = [0x49, 0x49, 0x2A, 0x00], SizeStrategy = SizeStrategy.MaxSize,
            },
            new()
            {
                Id = "heic", Extension = "heic", Category = "Images",
                Header = Encoding.ASCII.GetBytes("ftypheic"), // matched at offset 4 (see Isobmff below)
                SizeStrategy = SizeStrategy.EmbeddedLength, SizeFromHeader = SizeIsoBmff,
            },
            new()
            {
                Id = "cr2", Extension = "cr2", Category = "Camera RAW",
                Header = [0x49, 0x49, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x43, 0x52],
                SizeStrategy = SizeStrategy.MaxSize, MaxSize = 128L * 1024 * 1024,
            },

            // ---- Documents ----
            new()
            {
                Id = "pdf", Extension = "pdf", Category = "Documents",
                Header = Encoding.ASCII.GetBytes("%PDF-"),
                Footer = Encoding.ASCII.GetBytes("%%EOF"), FooterTrailing = 2,
                SizeStrategy = SizeStrategy.Footer, MaxSize = 256L * 1024 * 1024,
                Validate = b => b.Length > 8 && Contains(b, Encoding.ASCII.GetBytes("%%EOF")),
            },
            new()
            {
                // ZIP-based: also covers OOXML (docx/xlsx/pptx) and many others.
                Id = "zip", Extension = "zip", Category = "Archives / Office",
                Header = [0x50, 0x4B, 0x03, 0x04],
                SizeStrategy = SizeStrategy.MaxSize, MaxSize = 512L * 1024 * 1024,
                Validate = b => b.Length > 22 && Contains(b, [0x50, 0x4B, 0x05, 0x06]), // EOCD present
            },
            new()
            {
                Id = "rtf", Extension = "rtf", Category = "Documents",
                Header = Encoding.ASCII.GetBytes("{\\rtf"),
                Footer = [0x7D], SizeStrategy = SizeStrategy.MaxSize,
            },
            new()
            {
                Id = "ole", Extension = "doc", Category = "Documents",
                Header = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1],
                SizeStrategy = SizeStrategy.MaxSize, MaxSize = 128L * 1024 * 1024,
            },

            // ---- Audio / Video ----
            new()
            {
                Id = "mp4", Extension = "mp4", Category = "Video",
                Header = Encoding.ASCII.GetBytes("ftyp"), // matched at offset 4
                SizeStrategy = SizeStrategy.EmbeddedLength, SizeFromHeader = SizeIsoBmff,
                MaxSize = 4L * 1024 * 1024 * 1024,
            },
            new()
            {
                Id = "mov", Extension = "mov", Category = "Video",
                Header = Encoding.ASCII.GetBytes("ftypqt"), SizeStrategy = SizeStrategy.EmbeddedLength,
                SizeFromHeader = SizeIsoBmff, MaxSize = 4L * 1024 * 1024 * 1024,
            },
            new()
            {
                Id = "avi", Extension = "avi", Category = "Video",
                Header = Encoding.ASCII.GetBytes("RIFF"), SizeStrategy = SizeStrategy.EmbeddedLength,
                SizeFromHeader = SizeRiff, MaxSize = 4L * 1024 * 1024 * 1024,
            },
            new()
            {
                Id = "wav", Extension = "wav", Category = "Audio",
                Header = Encoding.ASCII.GetBytes("RIFF"), SizeStrategy = SizeStrategy.EmbeddedLength,
                SizeFromHeader = SizeRiff, MaxSize = 512L * 1024 * 1024,
            },
            new()
            {
                Id = "mp3-id3", Extension = "mp3", Category = "Audio",
                Header = Encoding.ASCII.GetBytes("ID3"), SizeStrategy = SizeStrategy.MaxSize,
                MaxSize = 128L * 1024 * 1024,
            },

            // ---- Misc ----
            new()
            {
                Id = "sqlite", Extension = "sqlite", Category = "Databases",
                Header = Encoding.ASCII.GetBytes("SQLite format 3\0"),
                SizeStrategy = SizeStrategy.MaxSize, MaxSize = 1L * 1024 * 1024 * 1024,
            },
            new()
            {
                Id = "gzip", Extension = "gz", Category = "Archives / Office",
                Header = [0x1F, 0x8B, 0x08], SizeStrategy = SizeStrategy.MaxSize,
            },
            new()
            {
                Id = "7z", Extension = "7z", Category = "Archives / Office",
                Header = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], SizeStrategy = SizeStrategy.MaxSize,
            },
        };

        return list;
    }

    /// <summary>Some ISO-BMFF/HEIC signatures match at byte offset 4 (after the box size). </summary>
    public static int HeaderMatchOffset(FileSignature sig) =>
        sig.Id is "mp4" or "mov" or "heic" ? 4 : 0;

    // ---- size decoders ----

    private static long SizePng(ReadOnlySpan<byte> window)
    {
        // Walk PNG chunks from offset 8 until IEND. window should be large enough; if not,
        // return 0 to fall back to footer/max handling.
        int p = 8;
        while (p + 8 <= window.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(window.Slice(p, 4));
            string type = Encoding.ASCII.GetString(window.Slice(p + 4, 4));
            p += 8 + (int)len + 4; // length + type + data + crc
            if (type == "IEND")
            {
                return p;
            }

            if (len > 64 * 1024 * 1024)
            {
                return 0; // implausible
            }
        }

        return 0;
    }

    private static long SizeRiff(ReadOnlySpan<byte> window)
    {
        if (window.Length < 8)
        {
            return 0;
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(window.Slice(4, 4));
        return riffSize + 8L; // "RIFF" + size field covers the rest
    }

    private static long SizeIsoBmff(ReadOnlySpan<byte> window)
    {
        // ISO Base Media (MP4/MOV/HEIC): concatenated boxes, each [uint32 size][4cc type].
        // Sum top-level box sizes until we run out of window; a full decode happens later,
        // this just estimates total length. The header was matched at offset 4, so the box
        // actually starts at offset 0.
        long total = 0;
        int p = 0;
        int boxes = 0;
        while (p + 8 <= window.Length && boxes < 64)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(window.Slice(p, 4));
            if (size < 8)
            {
                break;
            }

            total = p + size;
            p += (int)size;
            boxes++;
        }

        return total;
    }

    // ---- validators ----

    private static bool ValidateJpeg(byte[] b)
    {
        // Real JPEG starts FF D8 FF and ends FF D9; require both and a sane minimum.
        if (b.Length < 128 || b[0] != 0xFF || b[1] != 0xD8)
        {
            return false;
        }

        return b[^2] == 0xFF && b[^1] == 0xD9;
    }

    private static bool ValidatePng(byte[] b)
    {
        if (b.Length < 57)
        {
            return false;
        }

        // Ends with the IEND chunk + CRC: 00 00 00 00 "IEND" AE 42 60 82
        ReadOnlySpan<byte> iend = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
        return b.AsSpan(b.Length - 8).SequenceEqual(iend);
    }

    internal static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
