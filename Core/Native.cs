using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HardwareCenter.Core;

/// <summary>Win32 entry points used for admin-free hardware queries and window styling.</summary>
internal static class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint ShareReadWrite = 0x00000003;
    public const uint OpenExisting = 3;

    // CTL_CODE(deviceType, function, METHOD_BUFFERED, FILE_ANY_ACCESS)
    public const uint IoctlDiskPerformance = 0x00070020;
    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    public const int StorageDeviceProperty = 0;
    public const int StorageDeviceSeekPenaltyProperty = 7;
    public const int StorageDeviceTemperatureProperty = 52;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, byte[]? inBuffer, int inBufferSize,
        [Out] byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    /// <summary>Opens a device with no access rights, which is enough for query IOCTLs and needs no elevation.</summary>
    public static SafeFileHandle OpenForQuery(string path) =>
        CreateFile(path, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    /// <summary>Runs a buffered IOCTL and returns the bytes the driver wrote, or null if it failed.</summary>
    public static byte[]? Ioctl(SafeFileHandle device, uint controlCode, byte[]? input, int outputSize)
    {
        byte[] output = new byte[outputSize];
        if (!DeviceIoControl(device, controlCode, input, input?.Length ?? 0, output, outputSize, out int returned, IntPtr.Zero))
            return null;

        if (returned < outputSize)
            Array.Resize(ref output, returned);

        return output;
    }

    // ---- Performance Data Helper (the same counters Task Manager reads) ----

    public const uint PdhFmtDouble = 0x00000200;
    public const uint PdhFmtNoCap100 = 0x00008000;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct PdhCounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhCounterValue value);

    [DllImport("pdh.dll")]
    public static extern int PdhCloseQuery(IntPtr query);

    // ---- Desktop Window Manager ----

    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaBorderColor = 34;
    public const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
