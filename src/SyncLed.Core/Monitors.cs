using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

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

    public static List<MonitorChoice> All()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            string key = dd.DeviceName;
            string title = dd.DeviceString;

            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(key, 0, ref mon, 1))
            {
                string m = mon.DeviceString?.Trim() ?? "";
                if (m.Length > 0 && !m.Contains("Generic", StringComparison.OrdinalIgnoreCase) &&
                    !m.Contains("Универсальный", StringComparison.OrdinalIgnoreCase))
                    title = m;
                else if (m.Length > 0)
                    title = m; // "Generic Monitor (XG27ACS)" тоже информативно
            }
            names[key] = title;
        }

        var list = new List<MonitorChoice>();
        MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;

            string device = info.DeviceName;
            string title = names.TryGetValue(device, out string? n) && !string.IsNullOrWhiteSpace(n) ? n : device;
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

        // Одинаковые «Generic PnP Monitor» различаем именем вывода Windows.
        foreach (var group in ordered.GroupBy(m => m.Title, StringComparer.OrdinalIgnoreCase))
        {
            var duplicates = group.ToList();
            if (duplicates.Count <= 1) continue;
            foreach (var monitor in duplicates)
            {
                int index = ordered.IndexOf(monitor);
                string output = monitor.DeviceName.Replace("\\\\.\\", "");
                ordered[index] = monitor with { Title = $"{output} · {monitor.Title}" };
            }
        }

        return ordered;
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
