using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Win32;
using SyncLed.Avalonia.Infrastructure;
using SyncLed.Avalonia.Services;

namespace SyncLed.Avalonia.ViewModels;

public sealed record DiagnosticRow(string Name, string Transport, string Status,
    string Monitor, string Mode, string Sent, string Skipped, string Traffic, string Errors);

/// <summary>Главное состояние Avalonia-приложения.</summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saveTimer;
    private DateTime _lastDiagnosticsRefresh = DateTime.MinValue;
    private Window? _window;
    private bool _isSyncing;
    private bool _isPaused;
    private bool _syncBeforePause;
    private bool _locked;
    private bool _suspended;
    private bool _isRefreshing;
    private string _page = "devices";
    private string _stateText = "Ожидание";
    private string _subtitle = "Поиск и управление подсветкой";
    private string _logText = "";
    private string _diagnosticsSummary = "Данные появятся после подключения лент.";
    private int _fps = 15;
    private int _brightness = 100;
    private int _saturation = 100;
    private int _gamma = 100;
    private int _smoothing = 35;
    private int _blackThreshold = 6;
    private int _edgeDepth = 12;
    private int _bluetoothBrightness = 20;
    private bool _startSyncOnLaunch;
    private bool _startWithWindows;
    private bool _minimizeToTray = true;
    private bool _pauseWhenInactive = true;
    private bool _probeCompatible;
    private bool _scanBluetooth;
    private PresetItem? _selectedPreset;
    private string _activePresetName = "";
    private string _activePresetText = "Свои настройки — пресет не задан";
    private bool _isPresetActive;
    private bool _isPresetModified;
    private string _newPresetName = "";
    private string _presetStatus = "";

    public MainWindowViewModel(AppConfig config, bool startMinimized)
    {
        _services = new AppServices(config);
        StartMinimized = startMinimized;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _services.Save();
        };
        _services.Log += message => Dispatcher.UIThread.Post(() => AppendLog(message));
        _services.DevicesChanged += () => Dispatcher.UIThread.Post(RefreshBindings);

        _activePresetName = config.ActivePresetName;
        Fps = config.Fps;
        Brightness = config.Brightness;
        Saturation = config.Saturation;
        Gamma = config.GammaPercent;
        Smoothing = config.Smoothing;
        BlackThreshold = config.BlackThreshold;
        EdgeDepth = config.EdgeDepthPercent;
        BluetoothBrightness = config.BluetoothBrightness;
        StartSyncOnLaunch = config.StartSyncOnLaunch;
        StartWithWindows = config.StartWithWindows;
        MinimizeToTray = config.MinimizeToTray;
        PauseWhenInactive = config.PauseWhenInactive;
        ProbeCompatible = config.ProbeCompatibleDevices;
        ScanBluetooth = config.ScanBluetooth;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SyncCommand = new AsyncRelayCommand(ToggleSyncAsync, () => !_isRefreshing);

        ApplyPresetCommand = new RelayCommand(ApplySelectedPreset, () => SelectedPreset != null);
        SavePresetCommand = new RelayCommand(() => SavePreset(null));
        DeletePresetCommand = new RelayCommand(DeleteSelectedPreset, () => SelectedPreset != null);
        RefreshPresets();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        AppendLog("SyncLed запущен. Avalonia UI.");
        if (!startMinimized) Dispatcher.UIThread.Post(async () => await InitializeAsync());
    }

    public void AttachWindow(Window window)
    {
        _window = window;
        if (StartMinimized)
        {
            window.Hide();
            Dispatcher.UIThread.Post(async () => await InitializeAsync());
        }
    }

    public void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        _window.Activate();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
    }

    public void HideWindow() => _window?.Hide();

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<DiagnosticRow> DiagnosticRows { get; } = new();
    public IReadOnlyList<DeviceGroup> Groups => _services.Config.Groups;
    public AppConfig Config => _services.Config;
    public AppServices Services => _services;

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SyncCommand { get; }

    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            if (!SetProperty(ref _isSyncing, value)) return;
            StateText = value ? "Синхронизация включена" : "Синхронизация выключена";
            OnPropertyChanged(nameof(SyncButtonText));
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!SetProperty(ref _isRefreshing, value)) return;
            SyncCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public string SyncButtonText => IsSyncing ? "Стоп" : "Старт";

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public string DiagnosticsSummary
    {
        get => _diagnosticsSummary;
        private set => SetProperty(ref _diagnosticsSummary, value);
    }

    public bool IsDevicesPage { get => _page == "devices"; }
    public bool IsGroupsPage { get => _page == "groups"; }
    public bool IsDiagnosticsPage { get => _page == "diagnostics"; }
    public bool IsSettingsPage { get => _page == "settings"; }
    public bool IsLogPage { get => _page == "log"; }

    public int Fps
    {
        get => _fps;
        set => SetSetting(ref _fps, value, x => _services.Config.Fps = x, 5, 60);
    }

    public int Brightness
    {
        get => _brightness;
        set => SetSetting(ref _brightness, value, x => _services.Config.Brightness = x, 5, 100);
    }

    public int Saturation
    {
        get => _saturation;
        set => SetSetting(ref _saturation, value, x => _services.Config.Saturation = x, 0, 200);
    }

    public int Gamma
    {
        get => _gamma;
        set => SetSetting(ref _gamma, value, x => _services.Config.GammaPercent = x, 50, 250);
    }

    public int Smoothing
    {
        get => _smoothing;
        set => SetSetting(ref _smoothing, value, x => _services.Config.Smoothing = x, 0, 95);
    }

    public int BlackThreshold
    {
        get => _blackThreshold;
        set => SetSetting(ref _blackThreshold, value, x => _services.Config.BlackThreshold = x, 0, 60);
    }

    public int EdgeDepth
    {
        get => _edgeDepth;
        set => SetSetting(ref _edgeDepth, value, x => _services.Config.EdgeDepthPercent = x, 4, 30);
    }

    public int BluetoothBrightness
    {
        get => _bluetoothBrightness;
        set => SetSetting(ref _bluetoothBrightness, value, x => _services.Config.BluetoothBrightness = x, 1, 100);
    }

    public bool StartSyncOnLaunch
    {
        get => _startSyncOnLaunch;
        set => SetFlag(ref _startSyncOnLaunch, value, x => _services.Config.StartSyncOnLaunch = x);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value)) return;
            try { AppConfig.SetWindowsAutostart(value); }
            catch (Exception ex) { AppendLog("Автозапуск: " + ex.Message); }
            _services.Config.StartWithWindows = value;
            SaveSettings();
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetFlag(ref _minimizeToTray, value, x => _services.Config.MinimizeToTray = x);
    }

    public bool PauseWhenInactive
    {
        get => _pauseWhenInactive;
        set => SetFlag(ref _pauseWhenInactive, value, x => _services.Config.PauseWhenInactive = x);
    }

    public bool ProbeCompatible
    {
        get => _probeCompatible;
        set => SetFlag(ref _probeCompatible, value, x => _services.Config.ProbeCompatibleDevices = x);
    }

    public bool ScanBluetooth
    {
        get => _scanBluetooth;
        set => SetFlag(ref _scanBluetooth, value, x => _services.Config.ScanBluetooth = x);
    }

    // ── пресеты общих настроек ─────────────────────────────────────────────

    /// <summary>Пресеты общих настроек (зеркало <see cref="AppConfig.Presets"/>).</summary>
    public ObservableCollection<PresetItem> Presets { get; } = new();

    public RelayCommand ApplyPresetCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }

    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value)) return;
            ApplyPresetCommand?.RaiseCanExecuteChanged();
            DeletePresetCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Строка состояния активного пресета: какой набор сейчас действует,
    /// изменён ли он или настройки свои.
    /// </summary>
    public string ActivePresetText
    {
        get => _activePresetText;
        private set => SetProperty(ref _activePresetText, value);
    }

    /// <summary>Текущие общие настройки совпадают с каким-нибудь пресетом.</summary>
    public bool IsPresetActive
    {
        get => _isPresetActive;
        private set => SetProperty(ref _isPresetActive, value);
    }

    /// <summary>Был применён пресет, но текущие настройки уже отличаются от него.</summary>
    public bool IsPresetModified
    {
        get => _isPresetModified;
        private set => SetProperty(ref _isPresetModified, value);
    }

    /// <summary>Имя для нового/перезаписываемого пресета.</summary>
    public string NewPresetName
    {
        get => _newPresetName;
        set => SetProperty(ref _newPresetName, value);
    }

    /// <summary>Строка состояния блока пресетов.</summary>
    public string PresetStatus
    {
        get => _presetStatus;
        private set => SetProperty(ref _presetStatus, value);
    }

    /// <summary>Синхронизирует список пресетов с конфигурацией.</summary>
    public void RefreshPresets(PresetItem? select = null)
    {
        var current = select ?? SelectedPreset;
        Presets.Clear();
        foreach (var preset in _services.Config.Presets)
            Presets.Add(new PresetItem(preset));

        SelectedPreset = current == null
            ? Presets.FirstOrDefault(p =>
                  string.Equals(p.Name, _activePresetName, StringComparison.OrdinalIgnoreCase))
              ?? Presets.FirstOrDefault()
            : Presets.FirstOrDefault(p =>
                  ReferenceEquals(p.Preset, current.Preset) ||
                  string.Equals(p.Name, current.Name, StringComparison.OrdinalIgnoreCase))
              ?? Presets.FirstOrDefault();

        UpdateActivePresetState();
    }

    /// <summary>
    /// Применяет пресет к общим настройкам (FPS, Bluetooth и собственные
    /// настройки лент/групп при этом не меняются) и сохраняет конфигурацию.
    /// </summary>
    public void ApplyPreset(SettingsPreset preset)
    {
        Brightness = preset.Brightness;
        Saturation = preset.Saturation;
        Gamma = preset.GammaPercent;
        Smoothing = preset.Smoothing;
        BlackThreshold = preset.BlackThreshold;
        EdgeDepth = preset.EdgeDepthPercent;
        RememberActivePreset(preset.Name);
        SaveSettings();
        RefreshPresets();
        SelectPresetByName(preset.Name);
        PresetStatus = $"Пресет «{preset.Name}» применён.";
    }

    /// <summary>
    /// Снимок текущих общих настроек под указанным именем; пресет с таким же
    /// именем (без учёта регистра) перезаписывается.
    /// </summary>
    public SettingsPreset SavePreset(string? name = null)
    {
        name = (name ?? NewPresetName).Trim();
        if (name.Length == 0) name = "Пресет";
        if (name.Length > 40) name = name[..40].TrimEnd();

        var preset = _services.Config.Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset == null)
        {
            preset = new SettingsPreset();
            _services.Config.Presets.Add(preset);
        }

        preset.Name = name;
        preset.Brightness = Brightness;
        preset.Saturation = Saturation;
        preset.GammaPercent = Gamma;
        preset.Smoothing = Smoothing;
        preset.BlackThreshold = BlackThreshold;
        preset.EdgeDepthPercent = EdgeDepth;

        _services.Config.Normalize();
        RememberActivePreset(preset.Name);
        SaveSettings();
        RefreshPresets();
        SelectPresetByName(preset.Name);
        NewPresetName = preset.Name;
        PresetStatus = $"Сохранено: «{preset.Name}».";
        return preset;
    }

    /// <summary>Удаляет пресет по имени (из списка или из конфигурации).</summary>
    public void DeletePreset(SettingsPreset preset)
    {
        var target = _services.Config.Presets.FirstOrDefault(p =>
                         ReferenceEquals(p, preset) ||
                         string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                     ?? preset;
        if (_services.Config.Presets.Remove(target))
            PresetStatus = $"Пресет «{target.Name}» удалён.";
        else
            PresetStatus = "Пресет не найден.";

        if (string.Equals(_activePresetName, target.Name, StringComparison.OrdinalIgnoreCase))
            RememberActivePreset("");
        SaveSettings();
        RefreshPresets();
    }

    private void ApplySelectedPreset()
    {
        if (SelectedPreset is { } item) ApplyPreset(item.Preset);
    }

    private void DeleteSelectedPreset()
    {
        if (SelectedPreset is { } item) DeletePreset(item.Preset);
    }

    /// <summary>Выбирает в списке пресет с указанным именем (без учёта регистра).</summary>
    private void SelectPresetByName(string name) =>
        SelectedPreset = Presets.FirstOrDefault(p =>
                             string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                         ?? SelectedPreset;

    /// <summary>Запоминает активный пресет в модели и в конфигурации.</summary>
    private void RememberActivePreset(string name)
    {
        if (string.Equals(_activePresetName, name, StringComparison.Ordinal) &&
            string.Equals(_services.Config.ActivePresetName, name, StringComparison.Ordinal))
            return;

        _activePresetName = name;
        _services.Config.ActivePresetName = name;
        SaveSettings();
    }

    /// <summary>Находит пресет, чьи значения совпадают с текущими общими настройками.</summary>
    private SettingsPreset? MatchCurrentSettings()
    {
        foreach (var preset in _services.Config.Presets)
            if (preset.Brightness == Brightness &&
                preset.Saturation == Saturation &&
                preset.GammaPercent == Gamma &&
                preset.Smoothing == Smoothing &&
                preset.BlackThreshold == BlackThreshold &&
                preset.EdgeDepthPercent == EdgeDepth)
                return preset;
        return null;
    }

    /// <summary>
    /// Пересчитывает отметку активного пресета: активен тот, чьи значения
    /// совпадают с текущими настройками; если совпадений нет, но пресет
    /// применялся ранее, показываем его как изменённый.
    /// </summary>
    private void UpdateActivePresetState()
    {
        var match = MatchCurrentSettings();
        if (match != null && !string.Equals(_activePresetName, match.Name, StringComparison.OrdinalIgnoreCase))
            RememberActivePreset(match.Name);

        IsPresetActive = match != null;
        IsPresetModified = match == null && _activePresetName.Length > 0;
        ActivePresetText = match != null
            ? $"Активен: «{match.Name}»"
            : IsPresetModified
                ? $"«{_activePresetName}» · настройки изменены"
                : "Свои настройки — пресет не задан";

        foreach (var item in Presets)
            item.IsActive = match != null &&
                            string.Equals(item.Name, match.Name, StringComparison.OrdinalIgnoreCase);
    }

    public bool StartMinimized { get; }
    public bool ExitRequested { get; private set; }

    public async Task ExitAsync()
    {
        if (ExitRequested) return;
        ExitRequested = true;
        try { await StopAsync(); } catch { }
        Dispose();
        _window?.Close();
    }

    public async Task InitializeAsync()
    {
        await RefreshAsync();
        if (_services.Config.StartSyncOnLaunch && Devices.Count > 0)
            await ToggleSyncAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        StateText = "Поиск устройств";
        try
        {
            await _services.RefreshAsync();
            Devices.Clear();
            foreach (var strip in _services.Strips)
                Devices.Add(new DeviceViewModel(_services, strip));

            OnPropertyChanged(nameof(Groups));
            RefreshBindings();
        }
        catch (Exception ex)
        {
            AppendLog("Поиск устройств: " + ex.Message);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task ToggleSyncAsync()
    {
        if (IsSyncing) await _services.StopSyncAsync();
        else await _services.StartSyncAsync();
        IsSyncing = _services.IsSyncing;
    }

    public async Task StopAsync() => await _services.StopSyncAsync();

    /// <summary>
    /// Перемещает карточку ленты с <paramref name="fromIndex"/> на <paramref name="toIndex"/>
    /// (перетаскивание за ручку) и сохраняет новый порядок в конфигурации:
    /// <see cref="AppConfig.Devices"/> перестраивается в порядке карточек по Uuid.
    /// </summary>
    public void MoveDevice(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= Devices.Count) return;
        if (toIndex < 0 || toIndex >= Devices.Count) return;

        Devices.Move(fromIndex, toIndex);
        PersistDeviceOrder();
        UpdateSubtitle();
    }

    /// <summary>Порядок карточек → порядок DeviceConfig (по Uuid) → немедленное сохранение.</summary>
    private void PersistDeviceOrder()
    {
        try
        {
            // Ссылки на DeviceConfig те же, что в карточках, но на всякий случай
            // подстраховываемся сопоставлением по Uuid.
            var remaining = new List<DeviceConfig>(_services.Config.Devices);
            var ordered = new List<DeviceConfig>(remaining.Count);

            foreach (var device in Devices)
            {
                int index = remaining.FindIndex(c => ReferenceEquals(c, device.Device));
                if (index < 0 && !string.IsNullOrEmpty(device.Device.Uuid))
                    index = remaining.FindIndex(c =>
                        !string.IsNullOrEmpty(c.Uuid) &&
                        string.Equals(c.Uuid, device.Device.Uuid, StringComparison.OrdinalIgnoreCase));
                if (index < 0) continue;
                ordered.Add(remaining[index]);
                remaining.RemoveAt(index);
            }

            // Конфигурацию меняем на месте: другие представления держат ссылку на список.
            _services.Config.Devices.Clear();
            _services.Config.Devices.AddRange(ordered);
            _services.Config.Devices.AddRange(remaining);

            _services.Save();
        }
        catch (Exception ex)
        {
            AppendLog("Сохранение порядка карточек: " + ex.Message);
        }
    }

    public void Navigate(string page)
    {
        _page = page;
        OnPropertyChanged(nameof(IsDevicesPage));
        OnPropertyChanged(nameof(IsGroupsPage));
        OnPropertyChanged(nameof(IsDiagnosticsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsLogPage));
        if (page == "diagnostics") _lastDiagnosticsRefresh = DateTime.MinValue;
        UpdateSubtitle();
    }

    public void SaveSettings()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void RefreshGroups()
    {
        OnPropertyChanged(nameof(Groups));
        foreach (var device in Devices) device.ApplyGroups();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect)
                _locked = true;
            else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
                _locked = false;
            UpdateAutoPause();
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _suspended = e.Mode == PowerModes.Suspend;
            UpdateAutoPause();
        });
    }

    private void UpdateAutoPause()
    {
        if (_locked || _suspended)
        {
            if (!_services.Config.PauseWhenInactive || !IsSyncing) return;
            _syncBeforePause = true;
            _isPaused = true;
            _ = StopAsync();
            AppendLog(_locked ? "Пауза: сеанс заблокирован." : "Пауза: система уходит в сон.");
            return;
        }

        if (!_isPaused) return;
        _isPaused = false;
        if (_syncBeforePause)
        {
            _syncBeforePause = false;
            _ = ToggleSyncAsync();
        }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _saveTimer.Stop();
        _timer.Stop();
        _services.Dispose();
        _services.Save();
    }

    private void OnTimerTick()
    {
        bool paused = _isPaused;
        foreach (var device in Devices)
        {
            device.UpdateFromEngine(paused);
        }

        if (_page == "diagnostics" &&
            (DateTime.UtcNow - _lastDiagnosticsRefresh).TotalMilliseconds >= 1000)
        {
            _lastDiagnosticsRefresh = DateTime.UtcNow;
            RefreshDiagnostics();
        }
    }

    /// <summary>
    /// Обнуляет счётчики диагностики (кадры движка и обмен лент) и сразу
    /// перерисовывает таблицу: удобно перед замером за период.
    /// </summary>
    public void ResetDiagnostics()
    {
        _services.ResetDiagnostics();
        RefreshDiagnostics();
        AppendLog("Счётчики диагностики сброшены.");
    }

    private void RefreshDiagnostics()
    {
        DiagnosticsSnapshot snapshot = _services.GetDiagnostics();
        DiagnosticsSummary =
            $"Движок: {snapshot.Fps:0.0} FPS · кадр {snapshot.FrameMs:0.0} мс · " +
            $"кадров {snapshot.Frames} · синхронизация {(snapshot.Running ? "включена" : "выключена")} · лент {snapshot.Strips.Count}";

        var rows = snapshot.Strips.Select(s => new DiagnosticRow(
            s.Title,
            s.Transport,
            s.Testing ? "Тест" : s.Connected ? "Подключено" : "Нет связи",
            s.Monitor,
            s.Mode,
            s.Stats.Sent.ToString(),
            s.Stats.Skipped.ToString(),
            FormatTraffic(s.Stats.Bytes),
            s.Stats.Errors.ToString()));

        DiagnosticRows.Clear();
        foreach (var row in rows) DiagnosticRows.Add(row);
    }

    private static string FormatTraffic(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        return $"{bytes / 1024.0:0.0} КБ";
    }

    private void RefreshBindings()
    {
        if (_services.IsSyncing != IsSyncing) IsSyncing = _services.IsSyncing;
        UpdateSubtitle();
    }

    private void UpdateSubtitle()
    {
        Subtitle = _page switch
        {
            "groups" => "Общие режимы, цвета и параметры устройств",
            "diagnostics" => "Кадры, трафик и подключения в реальном времени",
            "settings" => "Общие параметры изображения и приложения",
            "log" => "События и состояние контроллеров",
            _ => Devices.Count > 0
                ? $"{Devices.Count} ленты · {_services.Monitors.Count} монитора"
                : "Поиск и управление подсветкой"
        };
    }

    private void AppendLog(string message)
    {
        var lines = new List<string>(LogText.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        lines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (lines.Count > 200) lines.RemoveAt(0);
        LogText = string.Join('\n', lines);
    }

    private void SetSetting<T>(ref T field, T value, Action<T> assign, T min, T max,
        [CallerMemberName] string? name = null) where T : struct, IComparable<T>
    {
        if (Comparer<T>.Default.Compare(value, min) < 0) value = min;
        else if (Comparer<T>.Default.Compare(value, max) > 0) value = max;
        if (!SetProperty(ref field, value, name)) return;
        assign(value);
        SaveSettings();

        // Шесть параметров входят в пресет: любое их изменение меняет
        // статус активного набора («активен» → «настройки изменены»).
        if (name is nameof(Brightness) or nameof(Saturation) or nameof(Gamma)
            or nameof(Smoothing) or nameof(BlackThreshold) or nameof(EdgeDepth))
            UpdateActivePresetState();
    }

    private void SetFlag(ref bool field, bool value, Action<bool> assign, [CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name)) return;
        assign(value);
        SaveSettings();
    }
}
