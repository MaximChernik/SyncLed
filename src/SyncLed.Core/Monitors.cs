using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SyncLed;

public sealed record MonitorChoice(string DeviceName, string Title, Rectangle Bounds, bool IsPrimary)
{
    public override string ToString()
    {
        string postfix = IsPrimary ? " · основной" : "";
        return $"{Title} — {Bounds.Width}×{Bounds.Height}{postfix}";
    }
}

public static class Monitors
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const int MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    /// <summary>Данные монитора из EDID: имя модели и серийный номер.</summary>
    private sealed record EdidInfo(string Name, string Serial);

    public static List<MonitorChoice> All()
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var serials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            string key = dd.DeviceName;
            string title = dd.DeviceString;
            string serial = "";

            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(key, 0, ref mon, 1))
            {
                string m = mon.DeviceString?.Trim() ?? "";

                // Имя из EDID («LG ULTRAWIDE», «DELL U2720Q») — читаемое и,
                // главное, стабильное: DISPLAY11 меняется от перезагрузки к
                // перезагрузке, а EDID-имя живёт в самом мониторе.
                var edid = ReadEdid(mon.DeviceID);
                if (edid != null && edid.Name.Length > 0)
                {
                    title = edid.Name;
                    serial = edid.Serial;
                }
                else
                {
                    // EDID не отдал имя — код модели из пути устройства
                    // (DEL4098, GSM59A8): он тоже стабильнее, чем DISPLAYxx.
                    string model = MonitorModel(mon.DeviceID);
                    if (model.Length > 0) title = model;
                    else if (m.Length > 0) title = m;
                }
            }
            titles[key] = title;
            serials[key] = serial;
        }

        var list = new List<MonitorChoice>();
        MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;

            string device = info.DeviceName;
            string title = titles.TryGetValue(device, out string? n) && !string.IsNullOrWhiteSpace(n) ? n : device;
            list.Add(new MonitorChoice(
                device,
                title,
                Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
                (info.Flags & MONITORINFOF_PRIMARY) != 0));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        var ordered = list
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.X)
            .ThenBy(m => m.Bounds.Y)
            .ToList();

        // Одинаковые названия различаем: сначала серийным номером из EDID
        // (стабилен между перезагрузками), и только если его нет — именем
        // вывода Windows (DISPLAY11).
        foreach (var group in ordered.GroupBy(m => m.Title, StringComparer.OrdinalIgnoreCase))
        {
            var duplicates = group.ToList();
            if (duplicates.Count <= 1) continue;
            foreach (var monitor in duplicates)
            {
                int index = ordered.IndexOf(monitor);
                string serial = serials.TryGetValue(monitor.DeviceName, out var s) ? s : "";
                bool uniqueSerial = serial.Length > 0 && duplicates.Count(d =>
                    serials.TryGetValue(d.DeviceName, out var other) && other == serial) == 1;
                string suffix = uniqueSerial
                    ? "S/N " + serial
                    : monitor.DeviceName.Replace("\\\\.\\", "");
                ordered[index] = monitor with { Title = $"{monitor.Title} · {suffix}" };
            }
        }

        return ordered;
    }

    /// <summary>Код модели из DeviceID монитора: DISPLAY#AUS27FE#… → AUS27FE.</summary>
    private static string MonitorModel(string monitorDeviceId)
    {
        string[] parts = SplitDeviceId(monitorDeviceId);
        return parts.Length >= 3 &&
               parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : "";
    }

    /// <summary>
    /// Разбирает DeviceID устройства вывода Windows:
    /// \\?\DISPLAY#AUS27FE#5&amp;16ebb415&amp;0&amp;UID4353#{guid}
    /// → [DISPLAY, AUS27FE, 5&amp;16ebb415&amp;0&amp;UID4353, {guid}].
    /// </summary>
    private static string[] SplitDeviceId(string? monitorDeviceId)
    {
        string id = monitorDeviceId ?? "";
        if (id.StartsWith(@"\\?\", StringComparison.Ordinal)) id = id[4..];
        return id.Split('#');
    }

    /// <summary>
    /// Читает EDID монитора из реестра Windows: имя (дескриптор 0xFC) и
    /// серийный номер (0xFF). Путь собирается из DeviceID устройства вывода.
    /// </summary>
    private static EdidInfo? ReadEdid(string monitorDeviceId)
    {
        try
        {
            string[] parts = SplitDeviceId(monitorDeviceId);
            if (parts.Length < 3 ||
                !parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase))
                return null;

            string path = $"SYSTEM\\CurrentControlSet\\Enum\\DISPLAY\\{parts[1]}\\{parts[2]}\\Device Parameters";
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key?.GetValue("EDID") is not byte[] edid || edid.Length < 126) return null;

            string name = EdidDescriptor(edid, 0xFC);
            string serial = EdidDescriptor(edid, 0xFF);
            if (serial is "0" or "") serial = "";
            return new EdidInfo(name, serial);
        }
        catch
        {
            // Доступ к реестру может быть закрыт — не ломаем список мониторов.
            return null;
        }
    }

    /// <summary>
    /// Ищет текстовый дескриптор нужного типа (0xFC — имя, 0xFF — серийник).
    /// Раскладка по данным EDID: 00 00 00 TAG 00 и далее 13 байт текста.
    /// </summary>
    private static string EdidDescriptor(byte[] edid, byte tag)
    {
        int end = Math.Min(edid.Length, 126);
        for (int offset = 54; offset + 18 <= end; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 ||
                edid[offset + 2] != 0 || edid[offset + 3] != tag)
                continue;

            var text = new StringBuilder();
            for (int i = 5; i < 18; i++)
            {
                char c = (char)edid[offset + i];
                if (c is '\n' or '\r') break;
                if (c < 32 || c > 126) continue;
                text.Append(c);
            }
            return text.ToString().Trim();
        }
        return "";
    }

    public static MonitorChoice? FindByDeviceName(IEnumerable<MonitorChoice> monitors, string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var exact = monitors.FirstOrDefault(m =>
                string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        return null;
    }
}
