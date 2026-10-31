using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SyncLed.Avalonia.Views;

/// <summary>
/// Переиспользуемая тёмная заголовочная панель для окон с SystemDecorations="None":
/// знак приложения, заголовок, перетаскивание, двойной щелчок (разворот/восстановление)
/// и кнопки свернуть/развернуть/закрыть. Кнопки показываются по свойствам
/// <see cref="ShowMinimize"/> и <see cref="ShowMaximize"/>; закрытие всегда доступно
/// и вызывает обычное Close() владельца (сохраняется логика OnClosing диалогов).
/// </summary>
public sealed partial class WindowChrome : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<WindowChrome, string?>(nameof(Title));

    public static readonly StyledProperty<bool> ShowMinimizeProperty =
        AvaloniaProperty.Register<WindowChrome, bool>(nameof(ShowMinimize), true);

    public static readonly StyledProperty<bool> ShowMaximizeProperty =
        AvaloniaProperty.Register<WindowChrome, bool>(nameof(ShowMaximize), true);

    private IDisposable? _windowStateSubscription;

    public WindowChrome()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) =>
        {
            ApplyRoundedCorners();
            if (HostWindow is { } window)
            {
                _windowStateSubscription = window
                    .GetObservable(Window.WindowStateProperty)
                    .Subscribe(new WindowStateObserver(UpdateRestoreGlyph));
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _windowStateSubscription?.Dispose();
            _windowStateSubscription = null;
        };
    }

    // ── зоны изменения размера окна ──────────────────────────────────────
    // SystemDecorations="None" убирает системные рамки, поэтому тонкие
    // прозрачные зоны по краям окна (см. MainWindow.axaml) начинают
    // штатную системную перетаскивку размера через BeginResizeDrag.

    // Строковое значение ("West", "NorthEast", … — имена WindowEdge):
    // XamlIl надёжно конвертирует строку в attached-свойство, а разбор
    // в WindowEdge происходит при нажатии.
    public static readonly AttachedProperty<string?> EdgeProperty =
        AvaloniaProperty.RegisterAttached<WindowChrome, Control, string?>("Edge");

    static WindowChrome()
    {
        EdgeProperty.Changed.AddClassHandler<Control>(OnEdgePropertyChanged);
    }

    public static string? GetEdge(Control element) => element.GetValue(EdgeProperty);

    public static void SetEdge(Control element, string? value) => element.SetValue(EdgeProperty, value);

    private static readonly EventHandler<PointerPressedEventArgs> ResizePressedHandler = OnGripPressed;

    private static void OnEdgePropertyChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        // Один обработчик на элемент: сначала снимаем, затем добавляем повторно.
        control.RemoveHandler(InputElement.PointerPressedEvent, ResizePressedHandler);
        if (e.GetNewValue<string?>() is not null)
            control.AddHandler(InputElement.PointerPressedEvent, ResizePressedHandler);
    }

    private static void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.GetValue(EdgeProperty) is not { } edgeName) return;
        if (!Enum.TryParse<WindowEdge>(edgeName, ignoreCase: false, out var edge)) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        if (TopLevel.GetTopLevel(control) is not Window window) return;
        if (!window.CanResize || window.WindowState == WindowState.Maximized) return;

        window.BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    /// <summary>Заголовок окна, отображаемый рядом со знаком приложения.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Показывать ли кнопку «Свернуть».</summary>
    public bool ShowMinimize
    {
        get => GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    /// <summary>Показывать ли кнопку «Развернуть» (двойной щелчок тоже разворачивает).</summary>
    public bool ShowMaximize
    {
        get => GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    /// <summary>
    /// Перетаскивание окна; двойной щелчок разворачивает/восстанавливает,
    /// если разрешён резерв под кнопку «Развернуть».
    /// </summary>
    private void OnDragAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Window? window = HostWindow;
        if (window == null) return;

        if (e.ClickCount == 2 && ShowMaximize)
        {
            ToggleMaximize(window);
            e.Handled = true;
            return;
        }

        window.BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window) window.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window && ShowMaximize) ToggleMaximize(window);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window) window.Close();
    }

    private static void ToggleMaximize(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void UpdateRestoreGlyph(WindowState state)
    {
        if (MaxGlyph == null || RestoreGlyph == null) return;
        bool maximized = state == WindowState.Maximized;
        MaxGlyph.IsVisible = !maximized;
        RestoreGlyph.IsVisible = maximized;
    }

    /// <summary>
    /// Скруглённые углы окна через DWM (Windows 11); на старых системах вызов
    /// просто завершается ошибкой и окно остаётся с прямыми углами.
    /// </summary>
    private void ApplyRoundedCorners()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try
        {
            nint handle = HostWindow?.TryGetPlatformHandle()?.Handle ?? 0;
            if (handle == 0) return;
            int preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // DWM недоступен — оставляем обычные углы.
        }
    }

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>Минимальный наблюдатель состояния окна без зависимости от Rx.</summary>
    private sealed class WindowStateObserver : IObserver<WindowState>
    {
        private readonly Action<WindowState> _onNext;
        public WindowStateObserver(Action<WindowState> onNext) => _onNext = onNext;
        public void OnNext(WindowState value) => _onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
