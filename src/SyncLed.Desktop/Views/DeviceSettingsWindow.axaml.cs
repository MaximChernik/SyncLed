using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace SyncLed.Avalonia.Views;

/// <summary>
/// Отдельные настройки одной ленты: по умолчанию значения наследуются от группы
/// и общих настроек; переключатель включает собственные значения для этой ленты.
/// Изменения применяются сразу и сохраняются с небольшой задержкой.
/// </summary>
public sealed partial class DeviceSettingsWindow : Window, INotifyPropertyChanged
{
    private static readonly IBrush HintMuted = new SolidColorBrush(Color.Parse("#8490A4"));
    private static readonly IBrush HintAccent = new SolidColorBrush(Color.Parse("#817BFF"));

    private readonly DeviceConfig _device;
    private readonly AppConfig _config;
    private readonly DispatcherTimer _saveTimer;
    private bool _loading;
    private bool _dirty;
    private bool _useCustom;
    private int _brightness = 100;
    private int _saturation = 100;
    private int _gammaPercent = 100;
    private int _smoothing = 35;
    private int _blackThreshold = 6;
    private int _edgeDepthPercent = 12;
    private string _hint = "";
    private IBrush _hintBrush = HintMuted;

    // Свойства привязываются через INotifyPropertyChanged; PropertyChanged из
    // AvaloniaObject имеет другую сигнатуру, поэтому объявляем свой явно.
    public new event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Для загрузки разметки из XAML; рабочее окно создаётся с конфигом.</summary>
    public DeviceSettingsWindow()
        : this(new DeviceConfig(), new AppConfig())
    {
    }

    public DeviceSettingsWindow(DeviceConfig device, AppConfig config)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        InitializeComponent();

        EffectiveSettings effective = config.ResolveSettings(device);
        bool own = device.Brightness.HasValue || device.Saturation.HasValue
                   || device.GammaPercent.HasValue || device.Smoothing.HasValue
                   || device.BlackThreshold.HasValue || device.EdgeDepthPercent.HasValue;

        _useCustom = own;
        _brightness = own ? device.Brightness ?? effective.Brightness : effective.Brightness;
        _saturation = own ? device.Saturation ?? effective.Saturation : effective.Saturation;
        _gammaPercent = own ? device.GammaPercent ?? effective.GammaPercent : effective.GammaPercent;
        _smoothing = own ? device.Smoothing ?? effective.Smoothing : effective.Smoothing;
        _blackThreshold = own ? device.BlackThreshold ?? effective.BlackThreshold : effective.BlackThreshold;
        _edgeDepthPercent = own ? device.EdgeDepthPercent ?? effective.EdgeDepthPercent : effective.EdgeDepthPercent;
        UpdateHint();

        DataContext = this;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            if (!_dirty) return;
            _dirty = false;
            _config.Save();
        };
        Closing += (_, _) => FlushPendingSave();
    }

    /// <summary>
    /// Открывает окно поверх окна владельца (или по центру экрана, если владелец
    /// не задан). Для модального режима используйте стандартный ShowDialog&lt;T&gt;(owner).
    /// </summary>
    public new void Show(Window? owner = null)
    {
        if (owner != null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        base.Show();
    }

    /// <summary>Включает/выключает собственные значения ленты.</summary>
    public bool UseCustom
    {
        get => _useCustom;
        set
        {
            if (!SetProperty(ref _useCustom, value)) return;
            _loading = true;
            try
            {
                if (value)
                {
                    EffectiveSettings effective = _config.ResolveSettings(_device);
                    Brightness = _device.Brightness ?? effective.Brightness;
                    Saturation = _device.Saturation ?? effective.Saturation;
                    GammaPercent = _device.GammaPercent ?? effective.GammaPercent;
                    Smoothing = _device.Smoothing ?? effective.Smoothing;
                    BlackThreshold = _device.BlackThreshold ?? effective.BlackThreshold;
                    EdgeDepthPercent = _device.EdgeDepthPercent ?? effective.EdgeDepthPercent;
                    WriteDevice();
                }
                else
                {
                    // Возвращаем ленту к наследованию: null — от группы или общих настроек.
                    _device.Brightness = null;
                    _device.Saturation = null;
                    _device.GammaPercent = null;
                    _device.Smoothing = null;
                    _device.BlackThreshold = null;
                    _device.EdgeDepthPercent = null;

                    EffectiveSettings inherited = _config.ResolveSettings(_device);
                    Brightness = inherited.Brightness;
                    Saturation = inherited.Saturation;
                    GammaPercent = inherited.GammaPercent;
                    Smoothing = inherited.Smoothing;
                    BlackThreshold = inherited.BlackThreshold;
                    EdgeDepthPercent = inherited.EdgeDepthPercent;
                    ScheduleSave();
                }
            }
            finally
            {
                _loading = false;
            }
            UpdateHint();
        }
    }

    public int Brightness
    {
        get => _brightness;
        set
        {
            value = Math.Clamp(value, 5, 100);
            if (!SetProperty(ref _brightness, value) || _loading || !_useCustom) return;
            _device.Brightness = value;
            ScheduleSave();
        }
    }

    public int Saturation
    {
        get => _saturation;
        set
        {
            value = Math.Clamp(value, 0, 200);
            if (!SetProperty(ref _saturation, value) || _loading || !_useCustom) return;
            _device.Saturation = value;
            ScheduleSave();
        }
    }

    public int GammaPercent
    {
        get => _gammaPercent;
        set
        {
            value = Math.Clamp(value, 50, 250);
            if (!SetProperty(ref _gammaPercent, value) || _loading || !_useCustom) return;
            _device.GammaPercent = value;
            ScheduleSave();
        }
    }

    public int Smoothing
    {
        get => _smoothing;
        set
        {
            value = Math.Clamp(value, 0, 95);
            if (!SetProperty(ref _smoothing, value) || _loading || !_useCustom) return;
            _device.Smoothing = value;
            ScheduleSave();
        }
    }

    public int BlackThreshold
    {
        get => _blackThreshold;
        set
        {
            value = Math.Clamp(value, 0, 60);
            if (!SetProperty(ref _blackThreshold, value) || _loading || !_useCustom) return;
            _device.BlackThreshold = value;
            ScheduleSave();
        }
    }

    public int EdgeDepthPercent
    {
        get => _edgeDepthPercent;
        set
        {
            value = Math.Clamp(value, 4, 30);
            if (!SetProperty(ref _edgeDepthPercent, value) || _loading || !_useCustom) return;
            _device.EdgeDepthPercent = value;
            ScheduleSave();
        }
    }

    /// <summary>Пояснение про наследование значений.</summary>
    public string Hint
    {
        get => _hint;
        private set => SetProperty(ref _hint, value);
    }

    public IBrush HintBrush
    {
        get => _hintBrush;
        private set => SetProperty(ref _hintBrush, value);
    }

    private void WriteDevice()
    {
        _device.Brightness = _brightness;
        _device.Saturation = _saturation;
        _device.GammaPercent = _gammaPercent;
        _device.Smoothing = _smoothing;
        _device.BlackThreshold = _blackThreshold;
        _device.EdgeDepthPercent = _edgeDepthPercent;
        ScheduleSave();
    }

    private void UpdateHint()
    {
        if (_useCustom)
        {
            Hint = "Лента использует собственные значения из этого окна — общие настройки и настройки группы для неё не применяются.";
            HintBrush = HintAccent;
            return;
        }

        var group = _config.FindGroup(_device.GroupId);
        Hint = group != null
            ? $"Без отдельных настроек используются значения группы «{group.Name}» и общие настройки приложения. Значения ниже показывают, что применяется сейчас."
            : "Без отдельных настроек используются общие настройки приложения. Значения ниже показывают, что применяется сейчас.";
        HintBrush = HintMuted;
    }

    /// <summary>Дебаунс-сохранение конфигурации.</summary>
    private void ScheduleSave()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushPendingSave()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _dirty = false;
        _config.Save();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
