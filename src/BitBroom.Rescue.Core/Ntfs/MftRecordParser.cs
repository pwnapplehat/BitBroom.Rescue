using System.Text;

namespace BitBroom.Rescue.Core.Ntfs;

/// <summary>
/// Parses a single raw MFT record buffer into an <see cref="MftRecord"/>. Handles the NTFS
/// fixup/update-sequence array, walks the attribute stream, decodes resident content and
/// non-resident runlists, and extracts every $FILE_NAME and $DATA stream. Deliberately
/// tolerant: a malformed attribute stops the walk without throwing, because on a damaged or
/// partially-overwritten volume we still want whatever survived.
/// </summary>
public static class MftRecordParser
{
    public const uint FileSignature = 0x454C4946; // "FILE"
    public const uint BaadSignature = 0x44414142; // "BAAD" (chkdsk tombstone)

    /// <summary>
    /// Parses <paramref name="buffer"/> (length == record size) after applying fixup.
    /// Returns null if the buffer is not a valid FILE record. <paramref name="recordNumber"/>
    /// is the record's index in the MFT (for identity / parent resolution).
    /// </summary>
    public static MftRecord? Parse(byte[] buffer, int bytesPerSector, long recordNumber)
    {
        if (buffer.Length < 42)
        {
            return null;
        }

        uint sig = BitConverter.ToUInt32(buffer, 0);
        if (sig != FileSignature)
        {
            return null; // BAAD or empty slot
        }

        if (!ApplyFixup(buffer, bytesPerSector))
        {
            return null;
        }

        ushort flags = BitConverter.ToUInt16(buffer, 0x16);
        ushort sequence = BitConverter.ToUInt16(buffer, 0x10);
        long baseRef = BitConverter.ToInt64(buffer, 0x20);
        long baseRecord = baseRef & 0x0000FFFFFFFFFFFF;

        var record = new MftRecord
        {
            RecordNumber = recordNumber,
            Sequence = sequence,
            InUse = (flags & 0x01) != 0,
            IsDirectory = (flags & 0x02) != 0,
            BaseRecordNumber = baseRecord,
        };

        ushort firstAttrOffset = BitConverter.ToUInt16(buffer, 0x14);
        int p = firstAttrOffset;

        while (p + 8 <= buffer.Length)
        {
            uint type = BitConverter.ToUInt32(buffer, p);
            if (type == (uint)NtfsAttrType.End)
            {
                break;
            }

            if (p + 4 > buffer.Length)
            {
                break;
            }

            int length = BitConverter.ToInt32(buffer, p + 4);
            if (length <= 0 || p + length > buffer.Length)
            {
                break; // malformed — keep what we have
            }

            ParseAttribute(buffer, p, length, type, record);
            p += length;
        }

        return record;
    }

    private static void ParseAttribute(byte[] buf, int p, int length, uint type, MftRecord record)
    {
        bool nonResident = buf[p + 0x08] != 0;
        byte nameLength = buf[p + 0x09];
        ushort nameOffset = BitConverter.ToUInt16(buf, p + 0x0A);

        string? attrName = null;
        if (nameLength > 0 && p + nameOffset + nameLength * 2 <= buf.Length)
        {
            attrName = Encoding.Unicode.GetString(buf, p + nameOffset, nameLength * 2);
        }

        switch ((NtfsAttrType)type)
        {
            case NtfsAttrType.StandardInformation when !nonResident:
            {
                ushort contentOffset = BitConverter.ToUInt16(buf, p + 0x14);
                int c = p + contentOffset;
                if (c + 24 <= buf.Length)
                {
                    record.CreatedUtc = NtfsTime.FromFileTime(BitConverter.ToInt64(buf, c));
                    record.ModifiedUtc = NtfsTime.FromFileTime(BitConverter.ToInt64(buf, c + 8));
                }

                break;
            }

            case NtfsAttrType.FileName when !nonResident:
            {
                ushort contentOffset = BitConverter.ToUInt16(buf, p + 0x14);
                FileNameInfo? fn = ParseFileName(buf, p + contentOffset);
                if (fn is not null)
                {
                    record.FileNames.Add(fn);
                }

                break;
            }

            case NtfsAttrType.Data:
            {
                NtfsAttribute? attr = nonResident
                    ? ParseNonResident(buf, p, NtfsAttrType.Data, attrName)
                    : ParseResident(buf, p, NtfsAttrType.Data, attrName);
                if (attr is not null)
                {
                    record.Attributes.Add(attr);
                }

                break;
            }
        }
    }

    private static FileNameInfo? ParseFileName(byte[] buf, int c)
    {
        if (c + 0x42 > buf.Length)
        {
            return null;
        }

        long parentRef = BitConverter.ToInt64(buf, c);
        long parent = parentRef & 0x0000FFFFFFFFFFFF;
        long created = BitConverter.ToInt64(buf, c + 0x08);
        long modified = BitConverter.ToInt64(buf, c + 0x18);
        byte nameLen = buf[c + 0x40];
        byte ns = buf[c + 0x41];
        if (c + 0x42 + nameLen * 2 > buf.Length)
        {
            return null;
        }

        string name = Encoding.Unicode.GetString(buf, c + 0x42, nameLen * 2);
        if (name.Length == 0)
        {
            return null;
        }

        return new FileNameInfo
        {
            Name = name,
            Namespace = (NtfsNamespace)ns,
            ParentRecordNumber = parent,
            CreatedUtc = NtfsTime.FromFileTime(created),
            ModifiedUtc = NtfsTime.FromFileTime(modified),
        };
    }

    private static NtfsAttribute? ParseResident(byte[] buf, int p, NtfsAttrType type, string? name)
    {
        uint contentLength = BitConverter.ToUInt32(buf, p + 0x10);
        ushort contentOffset = BitConverter.ToUInt16(buf, p + 0x14);
        int start = p + contentOffset;
        if (start < 0 || start + contentLength > buf.Length)
        {
            return null;
        }

        byte[] data = new byte[contentLength];
        Array.Copy(buf, start, data, 0, (int)contentLength);
        return new NtfsAttribute
        {
            Type = type,
            Name = name,
            IsResident = true,
            ResidentData = data,
            RealSize = contentLength,
        };
    }

    private static NtfsAttribute? ParseNonResident(byte[] buf, int p, NtfsAttrType type, string? name)
    {
        if (p + 0x38 > buf.Length)
        {
            return null;
        }

        long realSize = BitConverter.ToInt64(buf, p + 0x30);
        ushort runlistOffset = BitConverter.ToUInt16(buf, p + 0x20);
        int attrLength = BitConverter.ToInt32(buf, p + 0x04);
        List<DataRun> runs = ParseRunList(buf, p + runlistOffset, p + attrLength);

        return new NtfsAttribute
        {
            Type = type,
            Name = name,
            IsResident = false,
            RealSize = realSize,
            Runs = runs,
        };
    }

    /// <summary>
    /// Decodes an NTFS runlist. Each run: one header byte (low nibble = #length bytes, high
    /// nibble = #offset bytes), then the length, then a SIGNED offset delta relative to the
    /// previous run's LCN. A zero offset field means a sparse run (a hole).
    /// </summary>
    internal static List<DataRun> ParseRunList(byte[] buf, int start, int end)
    {
        var runs = new List<DataRun>();
        int r = start;
        long prevLcn = 0;

        while (r < end && r < buf.Length && buf[r] != 0)
        {
            int lenBytes = buf[r] & 0x0F;
            int offBytes = (buf[r] >> 4) & 0x0F;
            r++;
            if (lenBytes == 0 || r + lenBytes + offBytes > buf.Length)
            {
                break;
            }

            long runLength = 0;
            for (int i = 0; i < lenBytes; i++)
            {
                runLength |= (long)buf[r + i] << (8 * i);
            }

            r += lenBytes;

            if (offBytes == 0)
            {
                // Sparse run: no on-disk location.
                runs.Add(new DataRun(0, runLength, IsSparse: true));
                continue;
            }

            long offDelta = 0;
            for (int i = 0; i < offBytes; i++)
            {
                offDelta |= (long)buf[r + i] << (8 * i);
            }

            if ((buf[r + offBytes - 1] & 0x80) != 0)
            {
                offDelta |= -1L << (8 * offBytes); // sign-extend
            }

            r += offBytes;
            prevLcn += offDelta;
            runs.Add(new DataRun(prevLcn, runLength, IsSparse: false));
        }

        return runs;
    }

    /// <summary>
    /// Applies the NTFS fixup (update-sequence array): the last two bytes of every sector in
    /// a record are replaced by a check value that must equal the USN; the originals live in
    /// the USA. Restores them. Returns false if the record fails the torn-write check.
    /// </summary>
    internal static bool ApplyFixup(byte[] rec, int bytesPerSector)
    {
        ushort usaOffset = BitConverter.ToUInt16(rec, 0x04);
        ushort usaCount = BitConverter.ToUInt16(rec, 0x06);
        if (usaCount == 0 || usaOffset + usaCount * 2 > rec.Length)
        {
            return false;
        }

        ushort usn = BitConverter.ToUInt16(rec, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int sectorTail = i * bytesPerSector - 2;
            if (sectorTail + 2 > rec.Length)
            {
                break;
            }

            if (BitConverter.ToUInt16(rec, sectorTail) != usn)
            {
                return false; // torn write / not a coherent record
            }

            ushort original = BitConverter.ToUInt16(rec, usaOffset + i * 2);
            rec[sectorTail] = (byte)(original & 0xFF);
            rec[sectorTail + 1] = (byte)(original >> 8);
        }

        return true;
    }
}
