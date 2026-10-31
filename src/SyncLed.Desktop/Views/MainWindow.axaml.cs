using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SyncLed.Avalonia.ViewModels;

namespace SyncLed.Avalonia.Views;

/// <summary>Главное окно: боковая навигация, шапка со статусом и область страниц.</summary>
public sealed partial class MainWindow : Window
{
    private const int HotkeyId = 0x5C1D;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const int WmHotkey = 0x0312;
    private const int GwlpWndProc = -4;

    private MainWindowViewModel? _viewModel;
    private readonly List<DeviceViewModel> _savedColorSubscriptions = new();
    private nint _windowHandle;
    private nint _previousWndProc;
    private WndProcDelegate? _windowProc;
    private bool _hotkeyRegistered;
    private readonly DispatcherTimer _cardsScrollTimer;
    private double _cardsScrollTarget;
    private bool _cardsScrollAnimating;
    private DispatcherTimer? _pageAnimTimer;
    private readonly DispatcherTimer _spinnerTimer;
    private double _spinnerAngle;

    /// <summary>Порог компактной боковой панели: уже — только иконки.</summary>
    private const double CompactSidebarWidth = 1100;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint previous, nint hwnd, int message, nint wParam, nint lParam);

    public MainWindow()
    {
        InitializeComponent();

        _cardsScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _cardsScrollTimer.Tick += (_, _) => StepCardsScroll();

        // Вращение индикатора поиска: стартует и останавливается по IsRefreshing.
        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerAngle = (_spinnerAngle + 14) % 360;
            SearchSpinner.RenderTransform = new RotateTransform(_spinnerAngle);
        };

        // Туннелирующий обработчик получает колесо раньше ScrollViewer,
        // поэтому стандартный «щелчок» смещения заменяется плавным доводом.
        DevicesScroller.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnCardsPointerWheel,
            RoutingStrategies.Tunnel);
        DevicesScroller.PointerPressed += (_, _) =>
        {
            // Ручная работа со скроллом сразу отменяет текущую анимацию.
            _cardsScrollTimer.Stop();
            _cardsScrollAnimating = false;
            _cardsScrollTarget = DevicesScroller.Offset.Y;
        };
        Opened += (_, _) => RegisterGlobalHotkey();
    }

    private void OnCardsPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || e.Delta.Y == 0 || !DevicesScroller.IsEffectivelyVisible)
            return;

        double maxOffset = Math.Max(0, DevicesScroller.Extent.Height - DevicesScroller.Viewport.Height);
        if (maxOffset <= 0) return;

        double baseOffset = _cardsScrollAnimating
            ? _cardsScrollTarget
            : DevicesScroller.Offset.Y;
        // В Avalonia колесо вниз приходит с отрицательным Delta.Y.
        double delta = -Math.Clamp(e.Delta.Y, -5d, 5d) * 160d;

        _cardsScrollTarget = Math.Clamp(baseOffset + delta, 0d, maxOffset);
        if (Math.Abs(_cardsScrollTarget - DevicesScroller.Offset.Y) < 0.5)
        {
            _cardsScrollTimer.Stop();
            _cardsScrollAnimating = false;
            return;
        }

        e.Handled = true;
        _cardsScrollAnimating = true;
        _cardsScrollTimer.Start();
    }

    private void StepCardsScroll()
    {
        double maxOffset = Math.Max(0, DevicesScroller.Extent.Height - DevicesScroller.Viewport.Height);
        if (maxOffset <= 0)
        {
            _cardsScrollTarget = 0;
            _cardsScrollTimer.Stop();
            _cardsScrollAnimating = false;
            return;
        }

        _cardsScrollTarget = Math.Clamp(_cardsScrollTarget, 0d, maxOffset);
        double current = DevicesScroller.Offset.Y;
        double remaining = _cardsScrollTarget - current;

        if (Math.Abs(remaining) < 0.5)
        {
            SetCardsOffset(_cardsScrollTarget);
            _cardsScrollTimer.Stop();
            _cardsScrollAnimating = false;
            return;
        }

        double step = remaining * 0.24d;
        if (Math.Abs(step) < 1d)
            step = Math.Sign(remaining);

        SetCardsOffset(current + step);
    }

    private void SetCardsOffset(double value)
    {
        var offset = DevicesScroller.Offset;
        DevicesScroller.Offset = new Vector(offset.X, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Devices.CollectionChanged -= OnDevicesChanged;
        }
        UnsubscribeSavedColors();

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null)
        {
            _viewModel.AttachWindow(this);
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Devices.CollectionChanged += OnDevicesChanged;
        }
        SubscribeSavedColors();

        UpdatePageChrome();

        if (_viewModel != null)
            _viewModel.IsSidebarCompact = Bounds.Width < CompactSidebarWidth;
    }

    /// <summary>Ширина окна меняется → переключение обычной/компактной панели.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _viewModel != null)
            _viewModel.IsSidebarCompact = Bounds.Width < CompactSidebarWidth;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsDevicesPage)
            or nameof(MainWindowViewModel.IsGroupsPage)
            or nameof(MainWindowViewModel.IsDiagnosticsPage)
            or nameof(MainWindowViewModel.IsSettingsPage)
            or nameof(MainWindowViewModel.IsLogPage))
        {
            UpdatePageChrome();
            AnimateCurrentPage();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsRefreshing))
        {
            UpdateSearchSpinner();
        }
    }

    /// <summary>Крутит дугу поиска, пока идёт обновление списка устройств.</summary>
    private void UpdateSearchSpinner()
    {
        if (_viewModel?.IsRefreshing == true)
        {
            _spinnerTimer.Start();
            return;
        }

        _spinnerTimer.Stop();
        _spinnerAngle = 0;
        SearchSpinner.RenderTransform = null;
    }

    /// <summary>
    /// Плавное появление новой страницы: прозрачность 0→1 и сдвиг на 10 px
    /// за 160 мс. У остальных страниц состояние сбрасывается, чтобы при
    /// быстрых переключениях не осталась полупрозрачная предыдущая.
    /// </summary>
    private void AnimateCurrentPage()
    {
        var vm = _viewModel;
        if (vm == null) return;

        Control? page =
            vm.IsDevicesPage ? PageDevices :
            vm.IsGroupsPage ? PageGroups :
            vm.IsDiagnosticsPage ? PageDiagnostics :
            vm.IsSettingsPage ? PageSettings :
            vm.IsLogPage ? PageLog : null;
        if (page == null) return;

        _pageAnimTimer?.Stop();
        _pageAnimTimer = null;
        ResetPage(PageDevices, page);
        ResetPage(PageGroups, page);
        ResetPage(PageDiagnostics, page);
        ResetPage(PageSettings, page);
        ResetPage(PageLog, page);

        var transform = new TranslateTransform(0, 10);
        page.Opacity = 0;
        page.RenderTransform = transform;

        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double p = Math.Clamp((DateTime.UtcNow - started).TotalMilliseconds / 160d, 0, 1);
            double ease = 1d - Math.Pow(1d - p, 3d);
            page.Opacity = ease;
            transform.Y = 10d * (1d - ease);
            if (p < 1d) return;

            timer.Stop();
            page.Opacity = 1;
            page.RenderTransform = null;
            if (ReferenceEquals(_pageAnimTimer, timer)) _pageAnimTimer = null;
        };
        _pageAnimTimer = timer;
        timer.Start();
    }

    private static void ResetPage(Control control, Control current)
    {
        if (ReferenceEquals(control, current)) return;
        control.Opacity = 1;
        control.RenderTransform = null;
    }

    /// <summary>Чипс пресета в шапке страницы «Устройства».</summary>
    private void OnPresetChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not PresetItem item) return;
        _viewModel?.ApplyPresetDirect(item);
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SubscribeSavedColors();
        UpdatePageChrome();
    }

    /// <summary>
    /// «Мои цвета» общие для всех лент: изменение списка в одной карточке
    /// обновляет ряды сохранённых цветов во всех остальных.
    /// </summary>
    private void SubscribeSavedColors()
    {
        UnsubscribeSavedColors();
        if (_viewModel == null) return;

        foreach (var device in _viewModel.Devices)
        {
            device.SavedColorsChanged += OnSavedColorsChanged;
            _savedColorSubscriptions.Add(device);
        }
    }

    private void UnsubscribeSavedColors()
    {
        foreach (var device in _savedColorSubscriptions)
            device.SavedColorsChanged -= OnSavedColorsChanged;
        _savedColorSubscriptions.Clear();
    }

    private void OnSavedColorsChanged()
    {
        if (_viewModel == null) return;
        foreach (var device in _viewModel.Devices)
            device.RefreshSavedColors();
    }

    /// <summary>Заголовок страницы и пустое состояние списка устройств.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_viewModel is { ExitRequested: false, MinimizeToTray: true })
        {
            e.Cancel = true;
            _viewModel.HideWindow();
            return;
        }
        base.OnClosing(e);
    }

    private void RegisterGlobalHotkey()
    {
        _windowHandle = TryGetPlatformHandle()?.Handle ?? 0;
        if (_windowHandle == 0) return;

        _hotkeyRegistered = RegisterHotKey(_windowHandle, HotkeyId, ModControl | ModAlt, (uint)'L');
        if (!_hotkeyRegistered) return;

        _windowProc = WindowProc;
        _previousWndProc = SetWindowLongPtr(_windowHandle, GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                if (_viewModel != null) await _viewModel.ToggleSyncAsync();
            });
            return 0;
        }
        return CallWindowProc(_previousWndProc, hwnd, message, wParam, lParam);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hotkeyRegistered && _windowHandle != 0)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            if (_previousWndProc != 0)
                SetWindowLongPtr(_windowHandle, GwlpWndProc, _previousWndProc);
            _hotkeyRegistered = false;
        }
        UnsubscribeSavedColors();
        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    private void UpdatePageChrome()
    {
        var viewModel = _viewModel;
        if (viewModel == null) return;

        PageTitle.Text =
            viewModel.IsGroupsPage ? "Группы" :
            viewModel.IsDiagnosticsPage ? "Диагностика" :
            viewModel.IsSettingsPage ? "Настройки" :
            viewModel.IsLogPage ? "Журнал" :
            "Устройства";

        bool empty = viewModel.Devices.Count == 0;
        EmptyState.IsVisible = empty;
        DevicesList.IsVisible = !empty;
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not Control control || control.Tag is not string page)
            return;
        _viewModel.Navigate(page);
        UpdatePageChrome();
    }
}
