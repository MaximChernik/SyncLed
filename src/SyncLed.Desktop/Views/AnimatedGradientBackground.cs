using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SyncLed.Avalonia.Views;

/// <summary>
/// Лёгкий анимированный фон: тёмный фиолетово-циановый градиент, медленно
/// вращающийся вокруг центра. Специально дешёвый в отрисовке:
///  • таймер ~30 FPS работает только пока контрол видим и прикреплён к дереву;
///  • в свёрнутом окне кадры не перерисовываются;
///  • <see cref="InputElement.IsHitTestVisible"/> = false — ввод не перехватывает;
///  • два прямоугольника на кадр поверх базового цвета, контраст минимален,
///    поэтому непрозрачные карточки и логотипы остаются читаемыми.
/// </summary>
public sealed class AnimatedGradientBackground : Control
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    private readonly DispatcherTimer _timer;
    private double _phase;
    private bool _running;

    public static readonly StyledProperty<Color> BaseColorProperty =
        AvaloniaProperty.Register<AnimatedGradientBackground, Color>(nameof(BaseColor),
            Color.Parse("#090B10"));

    /// <summary>Основной оттенок — тёмный фиолетовый.</summary>
    public static readonly StyledProperty<Color> PrimaryColorProperty =
        AvaloniaProperty.Register<AnimatedGradientBackground, Color>(nameof(PrimaryColor),
            Color.FromArgb(120, 74, 46, 160));

    /// <summary>Контрастный оттенок — тёмный циан.</summary>
    public static readonly StyledProperty<Color> SecondaryColorProperty =
        AvaloniaProperty.Register<AnimatedGradientBackground, Color>(nameof(SecondaryColor),
            Color.FromArgb(110, 20, 96, 128));

    static AnimatedGradientBackground()
    {
        AffectsRender<AnimatedGradientBackground>(
            BaseColorProperty, PrimaryColorProperty, SecondaryColorProperty);
        IsVisibleProperty.Changed.AddClassHandler<AnimatedGradientBackground>(
            (control, _) => control.UpdateTimer());
    }

    public AnimatedGradientBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        IsVisible = true;
        RenderTransform = null;

        _timer = new DispatcherTimer(FrameInterval, DispatcherPriority.Background, OnTick);
    }

    /// <summary>Базовый цвет фона под градиентом.</summary>
    public Color BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    /// <summary>Фиолетовый оттенок градиента.</summary>
    public Color PrimaryColor
    {
        get => GetValue(PrimaryColorProperty);
        set => SetValue(PrimaryColorProperty, value);
    }

    /// <summary>Циановый оттенок градиента.</summary>
    public Color SecondaryColor
    {
        get => GetValue(SecondaryColorProperty);
        set => SetValue(SecondaryColorProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        bool shouldRun = IsVisible && VisualRoot != null;
        if (shouldRun == _running) return;
        _running = shouldRun;
        if (shouldRun) _timer.Start();
        else _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Окно свёрнуто или скрыто (трей) — кадры не нужны.
        if (VisualRoot is Window window &&
            (window.WindowState == WindowState.Minimized || !window.IsVisible))
            return;

        var bounds = Bounds;
        if (bounds.Width < 1 || bounds.Height < 1) return;

        // ~0.16 рад/с при 30 FPS: полный оборот примерно за 40 секунд.
        _phase += 0.0052;
        if (_phase > Math.PI * 2) _phase -= Math.PI * 2;

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 1 || bounds.Height < 1) return;

        context.FillRectangle(new SolidColorBrush(BaseColor), bounds);

        // Диагональный градиент, повёрнутый вокруг центра на угол _phase.
        const double reach = 0.75;
        double cos = Math.Cos(_phase);
        double sin = Math.Sin(_phase);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5 - cos * reach, 0.5 - sin * reach, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5 + cos * reach, 0.5 + sin * reach, RelativeUnit.Relative)
        };
        brush.GradientStops.Add(new GradientStop(PrimaryColor, 0.0));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.5));
        brush.GradientStops.Add(new GradientStop(SecondaryColor, 1.0));

        context.FillRectangle(brush, bounds);
    }
}
