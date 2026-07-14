using System.Text;
using BitBroom.Rescue.Core.Recovery;

namespace BitBroom.Rescue.Core.RecycleBin;

/// <summary>Metadata parsed from a modern Recycle Bin $I index file.</summary>
public sealed record RecycleIndex(string OriginalPath, long Size, DateTime DeletedUtc);

/// <summary>
/// Recovers files from the Windows Recycle Bin ($Recycle.Bin\&lt;SID&gt;). Since Vista, each
/// deleted file becomes a pair: an $I index file (original path, size, deletion time) and an
/// $R file holding the intact content. This is the highest-confidence recovery there is —
/// nothing was overwritten, the bytes are simply sitting in a hidden folder — and it gives
/// back the true original path, which a raw MFT scan of the same $R file cannot.
///
/// The $I parser is pure and unit-tested; the directory scan pairs $I with its $R sibling.
/// </summary>
public static class RecycleBinScanner
{
    /// <summary>Parses a modern ($I) index file (Vista v1 fixed-path, or Win8+ v2 length-prefixed).</summary>
    public static RecycleIndex? ParseIndex(byte[] data)
    {
        if (data.Length < 24)
        {
            return null;
        }

        long version = BitConverter.ToInt64(data, 0);
        long size = BitConverter.ToInt64(data, 8);
        long deletedFt = BitConverter.ToInt64(data, 16);
        DateTime deleted = SafeFileTime(deletedFt);

        string path;
        if (version >= 2)
        {
            if (data.Length < 28)
            {
                return null;
            }

            int chars = BitConverter.ToInt32(data, 24);
            int byteLen = Math.Max(0, (chars - 1) * 2); // exclude trailing null
            if (28 + byteLen > data.Length)
            {
                byteLen = Math.Max(0, data.Length - 28);
            }

            path = Encoding.Unicode.GetString(data, 28, byteLen);
        }
        else
        {
            // v1: fixed 260-char UTF-16 path, null-terminated.
            int maxBytes = Math.Min(520, data.Length - 24);
            string raw = Encoding.Unicode.GetString(data, 24, maxBytes);
            int nul = raw.IndexOf('\0');
            path = nul >= 0 ? raw[..nul] : raw;
        }

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return new RecycleIndex(path, size, deleted);
    }

    /// <summary>
    /// Scans a $Recycle.Bin\&lt;SID&gt; directory (or any folder of $I/$R pairs) and returns
    /// recoverable items whose content is the paired $R file. Reads via normal file APIs;
    /// works on the live volume without admin (the bin is accessible to the owning user).
    /// </summary>
    public static List<RecoverableItem> ScanDirectory(string binDirectory)
    {
        var items = new List<RecoverableItem>();
        if (!Directory.Exists(binDirectory))
        {
            return items;
        }

        IEnumerable<string> indexFiles;
        try
        {
            indexFiles = Directory.EnumerateFiles(binDirectory, "$I*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            return items;
        }

        foreach (string indexPath in indexFiles)
        {
            RecycleIndex? index;
            try
            {
                index = ParseIndex(File.ReadAllBytes(indexPath));
            }
            catch (Exception)
            {
                continue;
            }

            if (index is null)
            {
                continue;
            }

            // The content file is the same name with $R instead of $I.
            string fileName = Path.GetFileName(indexPath);
            string rName = "$R" + fileName[2..];
            string rPath = Path.Combine(binDirectory, rName);
            if (!File.Exists(rPath) && !Directory.Exists(rPath))
            {
                continue; // orphaned index — content already purged
            }

            string displayName = Path.GetFileName(index.OriginalPath);
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = rName;
            }

            string capturedRPath = rPath;
            items.Add(new RecoverableItem
            {
                Name = displayName,
                OriginalPath = index.OriginalPath,
                SizeBytes = index.Size,
                ModifiedUtc = index.DeletedUtc,
                Source = RecoverySource.RecycleBin,
                Confidence = RecoveryConfidence.High,
                ConfidenceReason = "intact file still present in the Recycle Bin — fully recoverable",
                IsResident = false,
                ContentProvider = _ => File.ReadAllBytes(capturedRPath),
            });
        }

        return items;
    }

    /// <summary>Scans every per-user $Recycle.Bin\&lt;SID&gt; folder on a mounted drive letter.</summary>
    public static List<RecoverableItem> ScanVolume(char driveLetter)
    {
        var items = new List<RecoverableItem>();
        string root = $@"{char.ToUpperInvariant(driveLetter)}:\$Recycle.Bin";
        if (!Directory.Exists(root))
        {
            return items;
        }

        IEnumerable<string> sidDirs;
        try
        {
            sidDirs = Directory.EnumerateDirectories(root);
        }
        catch (Exception)
        {
            return items;
        }

        foreach (string sidDir in sidDirs)
        {
            items.AddRange(ScanDirectory(sidDir));
        }

        return items;
    }

    private static DateTime SafeFileTime(long ft)
    {
        try
        {
            return ft <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(ft);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }
}
