using Avalonia;
using Avalonia.Media;

namespace SyncLed.Avalonia;

internal static class Program
{
    private const string MutexName = @"Local\SyncLed.SingleInstance";
    private const string ShowEventName = @"Local\SyncLed.ShowWindow";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
                showEvent.Set();
            }
            catch
            {
                // второй экземпляр уже завершается
            }
            return;
        }

        using var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        bool startMinimized = args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        BuildAvaloniaApp(startMinimized, showRequested)
            .With(new FontManagerOptions { DefaultFamilyName = "Segoe UI" })
            .StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp(bool startMinimized, EventWaitHandle showRequested) =>
        AppBuilder.Configure(() => new App(startMinimized, showRequested))
            .UsePlatformDetect()
            .LogToTrace();
}
