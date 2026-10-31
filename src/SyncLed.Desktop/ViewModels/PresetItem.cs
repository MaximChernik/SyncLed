using System.ComponentModel;
using SyncLed;

namespace SyncLed.Avalonia.ViewModels;

/// <summary>
/// Строка списка пресетов: значение конфигурации плюс отметка активного
/// набора. Отметка живёт отдельно от конфига, чтобы обновляться на лету,
/// когда пользователь двигает слайдеры и настройки расходятся с пресетом.
/// </summary>
public sealed class PresetItem : INotifyPropertyChanged
{
    private bool _isActive;

    public PresetItem(SettingsPreset preset) => Preset = preset;

    public SettingsPreset Preset { get; }

    public string Name => Preset.Name;

    /// <summary>Текущие общие настройки полностью совпадают с этим пресетом.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(Mark));
        }
    }

    /// <summary>Подпись рядом с именем активного пресета в выпадающем списке.</summary>
    public string Mark => _isActive ? "активен" : "";

    /// <summary>Имя набора вместо имени типа — так его показывают доступность и UIA.</summary>
    public override string ToString() => Preset.Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
