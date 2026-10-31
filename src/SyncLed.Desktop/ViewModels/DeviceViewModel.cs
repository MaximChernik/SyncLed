using Avalonia.Media;
using SyncLed.Avalonia.Infrastructure;
using SyncLed.Avalonia.Services;
using SyncLed.Avalonia.ViewModels;

namespace SyncLed.Avalonia.ViewModels;

public sealed record ChoiceItem(string Value, string Title)
{
    public override string ToString() => Title;
}

public sealed record GroupItem(string Value, string Title)
{
    public override string ToString() => Title;
}

public sealed record ColorChoice(string Name, string Hex, Rgb Color);

/// <summary>Состояние и настройки одной ленты.</summary>
public sealed class DeviceViewModel : ViewModelBase
{
    private static readonly ChoiceItem[] ModeItems =
    {
        new("inherit", "Как в группе"),
        new("sync", "Синхронизация с экраном"),
        new("color", "Свой цвет"),
        new("off", "Выключено")
    };

    private readonly AppServices _services;
    private readonly DeviceConfig _device;
    private readonly ILedStrip _strip;
    private bool _updating;
    private bool _enabled;
    private MonitorChoice? _selectedMonitor;
    private ChoiceItem? _selectedMode;
    private GroupItem? _selectedGroup;
    private string _statusText = "Нет связи";
    private IBrush _statusBrush = new SolidColorBrush(Color.Parse("#7E8899"));
    private List<Rgb> _previewColors = new();
    private string? _selectedColorHex;
    private int _shift;
    private bool _reverse;
    private int _horizontal;
    private int _gainR;
    private int _gainG;
    private int _gainB;

    public event Action? Changed;

    /// <summary>Вспыхивает, когда меняется общий список «Мои цвета» (все ленты).</summary>
    public event Action? SavedColorsChanged;

    public DeviceViewModel(AppServices services, ILedStrip strip)
    {
        _services = services;
        _strip = strip;
        _device = services.Config.GetOrCreateDevice(strip.Key, strip.ProductId, strip.LampCount, strip.PathHash);
        _device.Shift = ((_device.Shift % Math.Max(1, _device.LampCount)) + _device.LampCount) % Math.Max(1, _device.LampCount);

        MonitorChoices = services.Monitors.ToList();
        GroupChoices = BuildGroups();
        Palette = BuildPalette();

        _enabled = _device.Enabled;
        _selectedMonitor = MonitorChoices.FirstOrDefault(m =>
            string.Equals(m.DeviceName, _device.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? MonitorChoices.FirstOrDefault();
        _selectedMode = ModeItems.First(m => m.Value == _device.Mode);
        _selectedGroup = GroupChoices.First(g => g.Value == (_device.GroupId ?? ""));
        _selectedColorHex = _device.StaticColor;
        _shift = _device.Shift;
        _reverse = _device.Reverse;
        _horizontal = _device.HorizontalOverride;
        _gainR = _device.GainR;
        _gainG = _device.GainG;
        _gainB = _device.GainB;
    }

    public ILedStrip Strip => _strip;
    public AppConfig Config => _services.Config;
    public int SettingsRevision { get; private set; }
    public string Key => _strip.Key;
    public DeviceConfig Device => _device;
    public string Title => AppConfig.DisplayName(_device, _strip.Title);

    /// <summary>Отображаемое имя: пользовательское или стандартное название ленты.</summary>
    public string DisplayName => Title;
    public string Detail => _strip.Detail;
    public string Transport => _strip.Transport;
    public IReadOnlyList<MonitorChoice> MonitorChoices { get; private set; }
    public IReadOnlyList<ChoiceItem> ModeChoices => ModeItems;
    public IReadOnlyList<GroupItem> GroupChoices { get; private set; }
    public IReadOnlyList<ColorChoice> Palette { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            _device.Enabled = value;
            Changed?.Invoke();
            _services.Save();
        }
    }

    public MonitorChoice? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (!SetProperty(ref _selectedMonitor, value) || _updating || value == null) return;
            _device.MonitorDeviceName = value.DeviceName;
            Changed?.Invoke();
        }
    }

    public ChoiceItem? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value) || _updating || value == null) return;
            _device.Mode = value.Value;
            OnPropertyChanged(nameof(IsStaticMode));
            Changed?.Invoke();
            _services.Save();
        }
    }

    public bool IsStaticMode => _selectedMode?.Value == "color";

    public GroupItem? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value) || _updating || value == null) return;
            _device.GroupId = value.Value;
            if (!string.IsNullOrEmpty(value.Value) && _device.Mode == "sync")
                _device.Mode = "inherit";
            SelectedMode = ModeItems.First(m => m.Value == _device.Mode);
            Changed?.Invoke();
        }
    }

    public int Shift
    {
        get => _shift;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(1, _device.LampCount - 1));
            if (!SetProperty(ref _shift, value)) return;
            _device.Shift = value;
            Changed?.Invoke();
        }
    }

    public bool Reverse
    {
        get => _reverse;
        set
        {
            if (!SetProperty(ref _reverse, value)) return;
            _device.Reverse = value;
            Changed?.Invoke();
        }
    }

    public int Horizontal
    {
        get => _horizontal;
        set
        {
            if (!SetProperty(ref _horizontal, value)) return;
            _device.HorizontalOverride = value;
            Changed?.Invoke();
        }
    }

    public int GainR
    {
        get => _gainR;
        set
        {
            if (!SetProperty(ref _gainR, value)) return;
            _device.GainR = value;
            Changed?.Invoke();
        }
    }

    public int GainG
    {
        get => _gainG;
        set
        {
            if (!SetProperty(ref _gainG, value)) return;
            _device.GainG = value;
            Changed?.Invoke();
        }
    }

    public int GainB
    {
        get => _gainB;
        set
        {
            if (!SetProperty(ref _gainB, value)) return;
            _device.GainB = value;
            Changed?.Invoke();
        }
    }

    public string? SelectedColorHex
    {
        get => _selectedColorHex;
        set
        {
            if (!SetProperty(ref _selectedColorHex, value)) return;
            if (_updating || string.IsNullOrWhiteSpace(value)) return;
            _device.StaticColor = value;
            _device.Mode = "color";
            SelectedMode = ModeItems.First(m => m.Value == "color");
            Changed?.Invoke();
            _services.Save();
        }
    }

    /// <summary>Общие «Мои цвета» (#RRGGBB): один и тот же список у всех лент.</summary>
    public IReadOnlyList<string> SavedColors => _services.Config.SavedColors;

    /// <summary>Текущий цвет ленты (#RRGGBB) с учётом наследования от группы.</summary>
    public string CurrentColorHex => ColorUtil.ToHex(_services.Config.ResolveStaticColor(_device));

    /// <summary>Переименовывает ленту: пустое имя или совпадение с названием
    /// устройства возвращают стандартное название.</summary>
    public void Rename(string name)
    {
        string text = (name ?? "").Trim();
        if (text.Length > 60) text = text[..60].TrimEnd();

        bool standard = text.Length == 0 ||
            string.Equals(text, _strip.Title, StringComparison.OrdinalIgnoreCase);
        string value = standard ? "" : text;

        if (string.Equals(_device.Name, value, StringComparison.Ordinal)) return;

        _device.Name = value;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DisplayName));
        Changed?.Invoke();
        _services.Save();
    }

    /// <summary>Сохраняет текущий цвет ленты в общие «Мои цвета».</summary>
    public void SaveCurrentColor()
    {
        if (!_services.Config.AddSavedColor(CurrentColorHex)) return;
        _services.Save();
        SavedColorsChanged?.Invoke();
    }

    /// <summary>Удаляет цвет из общих «Моих цветов».</summary>
    public void RemoveSavedColor(string hex)
    {
        string value = ColorUtil.NormalizeHex(hex, "");
        if (value.Length == 0) return;

        _services.Config.RemoveSavedColor(value);
        _services.Save();
        SavedColorsChanged?.Invoke();
    }

    /// <summary>Применяет цвет из «Моих цветов» к этой ленте.</summary>
    public void SelectSavedColor(string hex) => ApplyColor(ColorUtil.NormalizeHex(hex, ""));

    /// <summary>Переключает ленту в режим своего цвета с указанным оттенком.</summary>
    public void ApplyColor(string hex)
    {
        string value = ColorUtil.NormalizeHex(hex, "");
        if (value.Length == 0) return;

        SelectedColorHex = value;
        if (_device.Mode != "color")
        {
            _device.Mode = "color";
            _selectedMode = ModeItems.First(m => m.Value == "color");
            OnPropertyChanged(nameof(SelectedMode));
            OnPropertyChanged(nameof(IsStaticMode));
        }
        Changed?.Invoke();
        _services.Save();
    }

    /// <summary>Перечитывает общий список «Мои цвета» (после его изменения).</summary>
    public void RefreshSavedColors() => OnPropertyChanged(nameof(SavedColors));

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }
    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public List<Rgb> PreviewColors
    {
        get => _previewColors;
        private set => SetProperty(ref _previewColors, value);
    }

    public void ApplyMonitors(IReadOnlyList<MonitorChoice> monitors)
    {
        var current = _device.MonitorDeviceName;
        _updating = true;
        try
        {
            MonitorChoices = monitors.ToList();
            OnPropertyChanged(nameof(MonitorChoices));
            SelectedMonitor = MonitorChoices.FirstOrDefault(m =>
                string.Equals(m.DeviceName, current, StringComparison.OrdinalIgnoreCase))
                ?? MonitorChoices.FirstOrDefault();
        }
        finally
        {
            _updating = false;
        }
    }

    public void ApplyGroups()
    {
        _updating = true;
        try
        {
            GroupChoices = BuildGroups();
            OnPropertyChanged(nameof(GroupChoices));
            SelectedGroup = GroupChoices.First(g => g.Value == (_device.GroupId ?? ""));
        }
        finally
        {
            _updating = false;
        }
    }

    public void UpdateFromEngine(bool paused)
    {
        var status = _services.GetStatus(Key);
        string mode = status?.Mode ?? _services.Config.ResolveMode(_device);
        List<Rgb> colors;
        if (!_device.Enabled)
            colors = new List<Rgb>();
        else if (mode == "sync")
            colors = _services.TryGetPreview(Key, out var preview) ? preview : new List<Rgb>();
        else if (mode == "color")
            colors = new List<Rgb> { _services.Config.ResolveStaticOutput(_device) };
        else
            colors = new List<Rgb>();

        if (!Same(colors, _previewColors)) PreviewColors = colors;

        if (!_device.Enabled)
        {
            StatusText = "Выключена";
            StatusBrush = new SolidColorBrush(Color.Parse("#7E8899"));
        }
        else if (paused)
        {
            StatusText = "Пауза";
            StatusBrush = new SolidColorBrush(Color.Parse("#F6BC60"));
        }
        else if (status is { Connected: true })
        {
            StatusText = "Подключено";
            StatusBrush = new SolidColorBrush(Color.Parse("#43D3A4"));
        }
        else
        {
            StatusText = string.IsNullOrEmpty(status?.Error) ? "Нет связи" : "Занято";
            StatusBrush = new SolidColorBrush(Color.Parse("#FF7082"));
        }
    }

    public void RunTest(string kind) => _services.RunTest(Key, kind);

    /// <summary>Персональные настройки изменились — обновляем миниатюру и превью.</summary>
    public void NotifySettingsChanged()
    {
        SettingsRevision++;
        OnPropertyChanged(nameof(SettingsRevision));
        Changed?.Invoke();
        _services.Save();
    }

    private IReadOnlyList<GroupItem> BuildGroups() =>
        new[] { new GroupItem("", "Без группы") }
            .Concat(_services.Config.Groups.Select(g => new GroupItem(g.Id, g.Name)))
            .ToList();

    private static IReadOnlyList<ColorChoice> BuildPalette() =>
        ColorUtil.Palette.Select(p => new ColorChoice(
            p.Name,
            ColorUtil.ToHex(p.Color),
            p.Color)).ToList();

    private static bool Same(IReadOnlyList<Rgb> a, IReadOnlyList<Rgb> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
