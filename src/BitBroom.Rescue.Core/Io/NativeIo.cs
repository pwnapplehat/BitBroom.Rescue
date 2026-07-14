using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BitBroom.Rescue.Core.Io;

/// <summary>Thin P/Invoke surface for opening raw devices READ-ONLY. No write entry points exist.</summary>
internal static class NativeIo
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint FILE_SHARE_READ = 0x1;
    internal const uint FILE_SHARE_WRITE = 0x2;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    internal const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    // DeviceIoControl codes.
    internal const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    internal const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    internal const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    internal const uint IOCTL_STORAGE_PREDICT_FAILURE = 0x002D1100;
    internal const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>Opens a device/volume path (e.g. \\.\C: or \\.\PhysicalDrive0) for READ-ONLY access.</summary>
    internal static SafeFileHandle OpenReadOnly(string path)
    {
        return CreateFileW(
            path,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_SEQUENTIAL_SCAN,
            IntPtr.Zero);
    }

    internal static long QueryLength(SafeFileHandle handle)
    {
        // GET_LENGTH_INFORMATION is a single 8-byte LONGLONG.
        IntPtr buf = Marshal.AllocHGlobal(8);
        try
        {
            if (DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, buf, 8, out _, IntPtr.Zero))
            {
                return Marshal.ReadInt64(buf);
            }

            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
