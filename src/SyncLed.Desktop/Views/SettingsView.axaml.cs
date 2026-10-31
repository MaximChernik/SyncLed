using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SyncLed.Avalonia.ViewModels;

namespace SyncLed.Avalonia.Views;

/// <summary>Страница общих настроек: слайдеры изображения и тумблеры приложения.</summary>
public sealed partial class SettingsView : UserControl
{
    private static readonly HashSet<string> SettingProperties = new()
    {
        nameof(MainWindowViewModel.Fps),
        nameof(MainWindowViewModel.Brightness),
        nameof(MainWindowViewModel.Saturation),
        nameof(MainWindowViewModel.Gamma),
        nameof(MainWindowViewModel.Smoothing),
        nameof(MainWindowViewModel.BlackThreshold),
        nameof(MainWindowViewModel.EdgeDepth),
        nameof(MainWindowViewModel.BluetoothBrightness),
        nameof(MainWindowViewModel.StartSyncOnLaunch),
        nameof(MainWindowViewModel.StartWithWindows),
        nameof(MainWindowViewModel.MinimizeToTray),
        nameof(MainWindowViewModel.PauseWhenInactive),
        nameof(MainWindowViewModel.ProbeCompatible),
        nameof(MainWindowViewModel.ScanBluetooth)
    };

    private MainWindowViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>Любое изменение свойства настройки сохраняет конфигурацию.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && SettingProperties.Contains(e.PropertyName))
            _viewModel?.SaveSettings();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        (DataContext as MainWindowViewModel)?.SaveSettings();

    private void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        new HelpWindow().Show(owner);
    }
}
