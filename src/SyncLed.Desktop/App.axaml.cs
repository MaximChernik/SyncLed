using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SyncLed.Avalonia.ViewModels;
using SyncLed.Avalonia.Views;

namespace SyncLed.Avalonia;

public sealed partial class App : Application
{
    private readonly bool _startMinimized;
    private readonly EventWaitHandle _showRequested;
    private MainWindowViewModel? _viewModel;

    public App()
        : this(false, new EventWaitHandle(false, EventResetMode.ManualReset))
    {
    }

    public App(bool startMinimized, EventWaitHandle showRequested)
    {
        _startMinimized = startMinimized;
        _showRequested = showRequested;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var config = AppConfig.Load();
            _viewModel = new MainWindowViewModel(config, _startMinimized);
            var window = new MainWindow
            {
                DataContext = _viewModel
            };

            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _viewModel.AttachWindow(window);

            var waiter = new Thread(() =>
            {
                while (true)
                {
                    _showRequested.WaitOne();
                    Dispatcher.UIThread.Post(_viewModel.ShowWindow);
                }
            })
            {
                IsBackground = true,
                Name = "SyncLedSingleInstance"
            };
            waiter.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayOpenClick(object? sender, EventArgs e) => _viewModel?.ShowWindow();

    private async void OnTraySyncClick(object? sender, EventArgs e)
    {
        if (_viewModel != null) await _viewModel.ToggleSyncAsync();
    }

    private async void OnTrayExitClick(object? sender, EventArgs e)
    {
        if (_viewModel != null) await _viewModel.ExitAsync();
    }
}
