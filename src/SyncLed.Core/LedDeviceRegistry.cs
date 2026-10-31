using System.Diagnostics;

namespace SyncLed;

/// <summary>Параметры поиска устройств.</summary>
public sealed record DiscoveryOptions(bool ProbeCompatible, bool ScanBluetooth);

/// <summary>
/// Провайдер световых устройств. Позволяет добавлять поддержку новых протоколов,
/// не меняя движок синхронизации и интерфейс.
/// </summary>
public interface ILedDeviceProvider
{
    string Name { get; }
    List<ILedStrip> Discover(DiscoveryOptions options);
}

/// <summary>
/// HID-устройства с протоколом RB/SC (ROBOBLOQ / SyncLight и совместимые ревизии).
/// Кроме известного VID/PID проверяет vendor-defined HID-устройства с 64-байтными
/// отчётами: если контроллер отвечает на запрос параметров, он считается лентой.
/// </summary>
public sealed class RobobloqHidProvider : ILedDeviceProvider
{
    private const ushort KnownVid = 0x1A86;
    private const ushort KnownPid = 0xFE07;
    private const int MaxUnknownProbes = 8;
    private const int UnknownProbeBudgetMs = 5000;

    public string Name => "RB/SC HID";

    public List<ILedStrip> Discover(DiscoveryOptions options)
    {
        var result = new List<ILedStrip>();
        var sw = Stopwatch.StartNew();
        int unknownProbes = 0;

        foreach (var hp in NativeHid.Enumerate())
        {
            bool known = hp.Vid == KnownVid && hp.Pid == KnownPid;
            if (!known)
            {
                if (!options.ProbeCompatible) continue;
                // Не трогаем клавиатуры, мыши и прочие стандартные HID-устройства:
                // шлём запрос только в vendor-defined интерфейсы с большими отчётами.
                if (hp.UsagePage < 0xFF00) continue;
                if (hp.OutputReportLength < 64 || hp.InputReportLength < 7) continue;
                if (unknownProbes >= MaxUnknownProbes || sw.ElapsedMilliseconds > UnknownProbeBudgetMs)
                    continue;
                unknownProbes++;
            }

            var strip = new LedStrip(hp.Path, hp.Vid, hp.Pid, hp.Product, hp.Manufacturer);
            var info = strip.QueryInfo();
            // Сразу после запуска/переподключения HID-интерфейс иногда не успевает
            // ответить. Повторяем запрос, чтобы не создать «безымянную» копию устройства.
            for (int attempt = 0; info == null && known && attempt < 2; attempt++)
            {
                Thread.Sleep(150);
                info = strip.QueryInfo();
            }
            if (info != null)
            {
                strip.ProductId = info.ProductId;
                strip.LampCount = info.LampCount is > 0 and <= 254 ? info.LampCount : 96;
                strip.Firmware = info.Firmware;
                strip.Uuid = info.Uuid;
                strip.DisplaySize = info.DisplaySize;
                result.Add(strip);
            }
            else if (known)
            {
                // Устройство есть, но не ответило — оставляем разумные значения по умолчанию.
                strip.LastError = "устройство не ответило на запрос параметров";
                result.Add(strip);
            }
        }

        return result;
    }
}

public static class LedDeviceRegistry
{
    private static readonly List<ILedDeviceProvider> Providers = new()
    {
        new RobobloqHidProvider(),
        new BleLedProvider()
    };

    public static IReadOnlyList<ILedDeviceProvider> All => Providers;

    public static void Register(ILedDeviceProvider provider)
    {
        if (Providers.All(p => p.GetType() != provider.GetType())) Providers.Add(provider);
    }

    public static List<ILedStrip> Discover(DiscoveryOptions options)
    {
        var result = new List<ILedStrip>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in Providers)
        {
            List<ILedStrip> found;
            try
            {
                found = provider.Discover(options);
            }
            catch
            {
                continue;
            }

            foreach (var strip in found)
            {
                if (!paths.Add(strip.Path)) continue;
                // Один контроллер может быть виден через несколько HID-интерфейсов.
                if (!string.IsNullOrEmpty(strip.Uuid) && !uuids.Add(strip.Uuid)) continue;
                result.Add(strip);
            }
        }

        return result;
    }
}
