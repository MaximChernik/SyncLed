using SyncLed;

namespace SyncLed.Avalonia.Services;

/// <summary>Слой приложения над устройствами, мониторами и движком синхронизации.</summary>
public sealed class AppServices : IDisposable
{
    private SyncEngine? _engine;

    public AppConfig Config { get; }
    public IReadOnlyList<ILedStrip> Strips { get; private set; } = Array.Empty<ILedStrip>();
    public IReadOnlyList<MonitorChoice> Monitors { get; private set; } = Array.Empty<MonitorChoice>();
    public bool IsSyncing { get; private set; }
    public bool IsBusy { get; private set; }

    public event Action<string>? Log;
    public event Action? DevicesChanged;

    public AppServices(AppConfig config)
    {
        Config = config;
    }

    public async Task RefreshAsync(CancellationToken cancellation = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            bool restartAfterRefresh = IsSyncing;
            if (restartAfterRefresh)
            {
                // Сначала снимаем флаг: интерфейс и трей не должны считать
                // остановленный движок работающим во время повторного поиска.
                IsSyncing = false;
                DevicesChanged?.Invoke();
            }
            await StopSyncCoreAsync(log: false);

            await Task.Run(() =>
            {
                _engine?.Dispose();
                _engine = null;
                foreach (var strip in Strips) strip.Close();

                var discovered = LedStrip.Discover(Config.ProbeCompatibleDevices, Config.ScanBluetooth);
                AddKnownBluetoothStrips(discovered);
                Strips = discovered;

                foreach (var strip in Strips)
                    Config.GetOrCreateDevice(strip.Key, strip.ProductId, strip.LampCount, strip.PathHash);
                RemoveFallbackDuplicates();
                SortStripsByConfig();
                AssignMonitors();

                Monitors = SyncLed.Monitors.All();
                _engine = new SyncEngine(Config, Strips);
                _engine.Log += message => Log?.Invoke(message);
                _engine.SetMonitors(new List<MonitorChoice>(Monitors));
            }, cancellation);

            Log?.Invoke($"Найдено устройств: {Strips.Count}.");

            if (restartAfterRefresh && Strips.Count > 0 && _engine != null)
            {
                // Новый движок после перепоиска должен продолжать тот же режим,
                // который был до нажатия «Обновить».
                _engine.Start();
                IsSyncing = true;
                Log?.Invoke("Синхронизация восстановлена после обновления устройств.");
            }

            DevicesChanged?.Invoke();
            Config.Save();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task StartSyncAsync()
    {
        if (IsBusy || IsSyncing || _engine == null || Strips.Count == 0) return Task.CompletedTask;

        Monitors = SyncLed.Monitors.All();
        _engine.SetMonitors(new List<MonitorChoice>(Monitors));
        _engine.Start();
        IsSyncing = true;
        Log?.Invoke("Синхронизация запущена.");
        DevicesChanged?.Invoke();
        Config.Save();
        return Task.CompletedTask;
    }

    public async Task StopSyncAsync(bool log = true)
    {
        if (IsBusy) return;
        if (!IsSyncing)
        {
            await StopSyncCoreAsync(log: false);
            return;
        }

        IsSyncing = false;
        DevicesChanged?.Invoke();
        await StopSyncCoreAsync(log);
    }

    public void Save() => Config.Save();

    public EngineStatus? GetStatus(string key)
    {
        if (_engine == null) return null;
        return _engine.GetStatus().FirstOrDefault(s =>
            string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public bool TryGetPreview(string key, out List<Rgb> colors)
    {
        colors = new List<Rgb>();
        return _engine?.TryGetPreview(key, out colors) ?? false;
    }

    public DiagnosticsSnapshot GetDiagnostics() =>
        _engine?.GetDiagnostics() ??
        new DiagnosticsSnapshot(false, 0, 0, 0, new List<StripDiagnostics>());

    /// <summary>Обнуляет счётчики движка и лент (кнопка «Сбросить счётчики»).</summary>
    public void ResetDiagnostics() => _engine?.ResetDiagnostics();

    public void RunTest(string key, string kind) => _engine?.RunTest(key, kind);

    public void Dispose()
    {
        foreach (var strip in Strips)
        {
            try { strip.Close(); } catch { }
        }
        _engine?.Dispose();
        _engine = null;
        IsSyncing = false;
    }

    private async Task StopSyncCoreAsync(bool log)
    {
        var engine = _engine;
        if (engine == null) return;
        await Task.Run(engine.Stop);
        if (log) Log?.Invoke("Синхронизация остановлена, ленты выключены.");
        DevicesChanged?.Invoke();
    }

    private void AddKnownBluetoothStrips(List<ILedStrip> discovered)
    {
        if (!Config.ScanBluetooth) return;

        var present = new HashSet<string>(discovered.Select(s => s.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var device in Config.Devices)
        {
            if (!string.Equals(device.ProductId, BleLedStrip.ProductIdValue, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(device.Uuid) || present.Contains(device.Uuid))
                continue;
            if (!ulong.TryParse(device.Uuid, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out ulong address))
                continue;

            string name = string.IsNullOrWhiteSpace(device.Name) ? "BLE-лента" : device.Name;
            var strip = new BleLedStrip(address, Windows.Devices.Bluetooth.BluetoothAddressType.Public, name)
            {
                LampCount = device.LampCount
            };
            discovered.Add(strip);
            present.Add(strip.Key);
        }
    }

    private void RemoveFallbackDuplicates()
    {
        var stable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var strip in Strips)
            if (!string.IsNullOrEmpty(strip.Uuid)) stable.Add(strip.PathHash);

        Config.Devices.RemoveAll(device =>
        {
            string uuid = device.Uuid ?? "";
            int at = uuid.LastIndexOf('@');
            return at > 0 && stable.Contains(uuid[(at + 1)..]);
        });
    }

    private void SortStripsByConfig()
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Config.Devices.Count; i++)
        {
            string uuid = Config.Devices[i].Uuid;
            if (!string.IsNullOrEmpty(uuid)) order.TryAdd(uuid, i);
        }

        Strips = Strips
            .OrderBy(s => order.TryGetValue(s.Key, out int index) ? index : int.MaxValue)
            .ThenBy(s => s.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    private void AssignMonitors()
    {
        if (Monitors.Count == 0) return;

        var bySize = Monitors
            .OrderByDescending(m => (long)m.Bounds.Width * m.Bounds.Height)
            .ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in Config.Devices)
        {
            if (Monitors.Any(m => string.Equals(m.DeviceName, device.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)))
                continue;

            MonitorChoice? pick = null;
            if (device.ProductId == "000503") pick = bySize.FirstOrDefault();
            else if (device.ProductId == "000000") pick = bySize.Skip(1).FirstOrDefault() ?? bySize.FirstOrDefault();
            pick ??= bySize.FirstOrDefault(m => !used.Contains(m.DeviceName)) ?? bySize[0];

            device.MonitorDeviceName = pick.DeviceName;
            used.Add(pick.DeviceName);
        }
    }
}
