using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SyncLed;

/// <summary>Win32-обёртки для работы с HID-устройствами (hid.dll / setupapi.dll).</summary>
internal static class NativeHid
{
    public const int DIGCF_PRESENT = 0x02;
    public const int DIGCF_DEVICEINTERFACE = 0x10;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x01;
    public const uint FILE_SHARE_WRITE = 0x02;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_SEM_TIMEOUT = 121;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    public const uint WAIT_OBJECT_0 = 0;

    public const ushort VendorId = 0x1A86;   // QinHeng / Robobloq
    public const ushort ProductId = 0xFE07;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle handle, StringBuilder buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder buffer, int bufferLength);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, int numberOfBytesToWrite,
        out int numberOfBytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, int numberOfBytesToRead,
        out int numberOfBytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteFile")]
    private static extern bool WriteFileOverlapped(SafeFileHandle handle, byte[] buffer, int numberOfBytesToWrite,
        out int numberOfBytesWritten, ref OVERLAPPED overlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadFile")]
    private static extern bool ReadFileOverlapped(SafeFileHandle handle, byte[] buffer, int numberOfBytesToRead,
        out int numberOfBytesRead, ref OVERLAPPED overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, ref OVERLAPPED overlapped,
        out int bytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, ref OVERLAPPED overlapped);

    /// <summary>Создаёт auto-reset событие для overlapped-операций (IntPtr.Zero при ошибке).</summary>
    public static IntPtr CreateIoEvent() => CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);

    public static void CloseIoEvent(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { CloseHandle(handle); } catch { }
        handle = IntPtr.Zero;
    }

    /// <summary>
    /// Запись HID-репорта с таймаутом. Устройство иногда перестаёт принимать данные
    /// (не вычитан входной буфер), поэтому обычный WriteFile может зависнуть навсегда.
    /// </summary>
    public static bool Write(SafeFileHandle handle, IntPtr ioEvent, byte[] buffer, int count,
        int timeoutMs, out int written, out int error)
    {
        written = 0;
        error = 0;
        var ov = new OVERLAPPED { hEvent = ioEvent };
        if (WriteFileOverlapped(handle, buffer, count, out written, ref ov)) return true;

        error = Marshal.GetLastWin32Error();
        if (error != ERROR_IO_PENDING) return false;

        if (!WaitForEvent(handle, ioEvent, ref ov, timeoutMs, out error))
        {
            written = 0;
            return false;
        }
        if (!GetOverlappedResult(handle, ref ov, out written, false))
        {
            error = Marshal.GetLastWin32Error();
            written = 0;
            return false;
        }
        error = 0;
        return true;
    }

    /// <summary>Чтение HID-репорта с таймаутом (таймаут — error == ERROR_SEM_TIMEOUT).</summary>
    public static bool Read(SafeFileHandle handle, IntPtr ioEvent, byte[] buffer, int count,
        int timeoutMs, out int read, out int error)
    {
        read = 0;
        error = 0;
        var ov = new OVERLAPPED { hEvent = ioEvent };
        if (ReadFileOverlapped(handle, buffer, count, out read, ref ov)) return true;

        error = Marshal.GetLastWin32Error();
        if (error != ERROR_IO_PENDING) return false;

        if (!WaitForEvent(handle, ioEvent, ref ov, timeoutMs, out error))
        {
            read = 0;
            return false;
        }
        if (!GetOverlappedResult(handle, ref ov, out read, false))
        {
            error = Marshal.GetLastWin32Error();
            read = 0;
            return false;
        }
        error = 0;
        return true;
    }

    /// <summary>Ждёт завершения overlapped-операции; по таймауту отменяет именно её.</summary>
    private static bool WaitForEvent(SafeFileHandle handle, IntPtr ioEvent, ref OVERLAPPED ov,
        int timeoutMs, out int error)
    {
        error = 0;
        uint wait = WaitForSingleObject(ioEvent, (uint)Math.Max(1, timeoutMs));
        if (wait == WAIT_OBJECT_0) return true;

        error = wait == 258 /* WAIT_TIMEOUT */ ? ERROR_SEM_TIMEOUT : Marshal.GetLastWin32Error();

        // Отменяем только эту операцию и даём ей завершиться, пока OVERLAPPED ещё жив.
        // Это критично: указатель на локальную структуру нельзя оставлять в ядре.
        try { CancelIoEx(handle, ref ov); } catch { }
        try { WaitForSingleObject(ioEvent, 3000); } catch { }
        return false;
    }

    public sealed record HidPath(
        string Path,
        ushort Vid,
        ushort Pid,
        ushort Version,
        string Product,
        string Manufacturer,
        ushort UsagePage,
        int OutputReportLength,
        int InputReportLength);

    /// <summary>Перечисляет все HID-интерфейсы в системе (можно отфильтровать по VID/PID).</summary>
    public static List<HidPath> Enumerate(ushort? vid = null, ushort? pid = null)
    {
        var result = new List<HidPath>();
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return result;

        try
        {
            var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifd); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                if (needed <= 0) continue;

                IntPtr detail = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(detail, 8); // cbSize для x64
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref ifd, detail, needed, out _, IntPtr.Zero))
                        continue;

                    string path = Marshal.PtrToStringUni(detail + 4) ?? string.Empty;
                    if (path.Length == 0) continue;

                    using SafeFileHandle h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h.IsInvalid) continue;

                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(h, ref attr)) continue;
                    if (vid.HasValue && attr.VendorID != vid.Value) continue;
                    if (pid.HasValue && attr.ProductID != pid.Value) continue;

                    ushort usagePage = 0;
                    int outputLength = 0;
                    int inputLength = 0;
                    if (TryGetCaps(h, out var caps))
                    {
                        usagePage = caps.UsagePage;
                        outputLength = caps.OutputReportByteLength;
                        inputLength = caps.InputReportByteLength;
                    }

                    result.Add(new HidPath(path, attr.VendorID, attr.ProductID, attr.VersionNumber,
                        GetProductString(h), GetManufacturerString(h), usagePage, outputLength, inputLength));
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    public static string GetProductString(SafeFileHandle h)
    {
        var sb = new StringBuilder(256);
        return HidD_GetProductString(h, sb, sb.Capacity) ? sb.ToString() : string.Empty;
    }

    public static string GetManufacturerString(SafeFileHandle h)
    {
        var sb = new StringBuilder(256);
        return HidD_GetManufacturerString(h, sb, sb.Capacity) ? sb.ToString() : string.Empty;
    }

    public static bool TryGetCaps(SafeFileHandle h, out HIDP_CAPS caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(h, out IntPtr pp)) return false;
        try
        {
            var c = new HIDP_CAPS { Reserved = new ushort[17] };
            if (HidP_GetCaps(pp, ref c) < 0) return false;
            caps = c;
            return true;
        }
        finally
        {
            HidD_FreePreparsedData(pp);
        }
    }
}
