using System.Diagnostics;
using System.Runtime.InteropServices;
using BitBroom.Rescue.Core.Io;
using Microsoft.Win32.SafeHandles;

namespace BitBroom.Rescue.Core.Health;

/// <summary>
/// Best-effort, READ-ONLY hardware probe. Detects SSD vs HDD (seek-penalty query), reads the
/// drive's SMART failure prediction, and checks the system-wide TRIM setting. Everything is
/// dependency-free (IOCTLs + fsutil); anything it can't determine is reported as unknown
/// rather than guessed.
/// </summary>
public static class DiskHealthProbe
{
    public static DriveHealthInfo Probe(int physicalDiskNumber)
    {
        MediaType media = MediaType.Unknown;
        bool? smartFail = null;

        try
        {
            using SafeFileHandle h = NativeIo.OpenReadOnly($@"\\.\PhysicalDrive{physicalDiskNumber}");
            if (!h.IsInvalid)
            {
                media = QuerySeekPenalty(h) switch
                {
                    true => MediaType.Hdd,
                    false => MediaType.Ssd,
                    null => MediaType.Unknown,
                };
                smartFail = QueryPredictFailure(h);
            }
        }
        catch (Exception)
        {
            // Not permitted / unsupported — leave as unknown.
        }

        return new DriveHealthInfo
        {
            Media = media,
            SmartPredictsFailure = smartFail,
            TrimEnabled = QueryTrimEnabled(),
        };
    }

    // STORAGE_PROPERTY_QUERY { PropertyId, QueryType, AdditionalParameters[1] } = 12 bytes.
    // StorageDeviceSeekPenaltyProperty = 7, PropertyStandardQuery = 0.
    // DEVICE_SEEK_PENALTY_DESCRIPTOR { Version, Size, IncursSeekPenalty(BOOLEAN) }.
    private static bool? QuerySeekPenalty(SafeFileHandle h)
    {
        const int querySize = 12;
        const int descSize = 12;
        IntPtr inBuf = Marshal.AllocHGlobal(querySize);
        IntPtr outBuf = Marshal.AllocHGlobal(descSize);
        try
        {
            for (int i = 0; i < querySize; i++)
            {
                Marshal.WriteByte(inBuf, i, 0);
            }

            Marshal.WriteInt32(inBuf, 0, 7); // StorageDeviceSeekPenaltyProperty
            Marshal.WriteInt32(inBuf, 4, 0); // PropertyStandardQuery

            if (!NativeIo.DeviceIoControl(h, NativeIo.IOCTL_STORAGE_QUERY_PROPERTY,
                    inBuf, querySize, outBuf, descSize, out _, IntPtr.Zero))
            {
                return null;
            }

            byte incursSeekPenalty = Marshal.ReadByte(outBuf, 8);
            return incursSeekPenalty != 0;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    // STORAGE_PREDICT_FAILURE { ULONG PredictFailure; UCHAR VendorSpecific[512]; }.
    private static bool? QueryPredictFailure(SafeFileHandle h)
    {
        const int size = 4 + 512;
        IntPtr outBuf = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeIo.DeviceIoControl(h, NativeIo.IOCTL_STORAGE_PREDICT_FAILURE,
                    IntPtr.Zero, 0, outBuf, size, out _, IntPtr.Zero))
            {
                return null;
            }

            uint predict = (uint)Marshal.ReadInt32(outBuf, 0);
            return predict != 0;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
        }
    }

    private static bool? QueryTrimEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "fsutil.exe",
                Arguments = "behavior query disabledeletenotify",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            // Output contains lines like "NTFS DisableDeleteNotify = 0" (0 => TRIM enabled).
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq > 0 && line.Contains("DisableDeleteNotify", StringComparison.OrdinalIgnoreCase))
                {
                    string val = line[(eq + 1)..].Trim();
                    if (val.StartsWith('0'))
                    {
                        return true;  // delete-notify NOT disabled → TRIM enabled
                    }

                    if (val.StartsWith('1'))
                    {
                        return false;
                    }
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
