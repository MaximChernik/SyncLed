using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SyncLed.Avalonia.Views;

/// <summary>
/// Фон в стиле лава-лампы: шесть мягких светящихся капель медленно поднимаются,
/// опускаются, покачиваются и «дышат», перекрываясь тёплыми пятнами. Специально
/// дешёвый в отрисовке:
///  • таймер ~30 FPS работает только пока контрол видим и прикреплён к дереву;
///  • в свёрнутом окне кадры не перерисовываются;
///  • <see cref="InputElement.IsHitTestVisible"/> = false — ввод не перехватывает;
///  • на кадр — сплошная заливка базового цвета плюс шесть эллипсов с радиальным
///    градиентом (капли полупрозрачные, поэтому заголовки и карточки читаемы);
///  • движение считается от накопленного времени, а не от кадра, поэтому
///    скорость не зависит от частоты таймера.
/// </summary>
public sealed class AnimatedGradientBackground : Control
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>
    /// Капля лава-лампы. Позиция и радиус заданы в долях размера окна;
    /// цвет и альфа — сердцевина свечения (к краю альфа падает до нуля).
    /// </summary>
    private readonly record struct Blob(
        double BaseX, double BaseY,
        double DriftX, double FreqX, double PhaseX,
        double DriftY, double FreqY, double PhaseY,
        double Radius, double PulseFreq, double PulsePhase,
        Color Color, byte Alpha);

    /// <summary>
    /// Раскладка капель: подъём/спуск — медленный синус (период 25–40 с, как в
    /// лава-лампе), покачивание вбок и дыхание радиуса — быстрее, фазы разные,
    /// поэтому капли никогда не движутся синхронно.
    /// </summary>
    private static readonly Blob[] Blobs =
    {
        new(0.24, 0.34, 0.06, 0.11, 0.0, 0.17, 0.19, 1.2, 0.30, 0.31, 0.0,
            Color.Parse("#817BFF"), 112),
        new(0.74, 0.26, 0.07, 0.09, 2.1, 0.15, 0.24, 3.4, 0.26, 0.27, 1.7,
            Color.Parse("#44CFFF"), 94),
        new(0.48, 0.66, 0.05, 0.13, 4.0, 0.18, 0.16, 5.1, 0.34, 0.22, 2.6,
            Color.Parse("#6E5BE8"), 120),
        new(0.86, 0.72, 0.06, 0.10, 1.1, 0.16, 0.21, 0.4, 0.28, 0.34, 3.9,
            Color.Parse("#37B6E8"), 90),
        new(0.12, 0.78, 0.05, 0.12, 5.2, 0.14, 0.26, 2.7, 0.24, 0.29, 4.8,
            Color.Parse("#A17BFF"), 104),
        new(0.62, 0.48, 0.06, 0.08, 3.3, 0.19, 0.18, 4.6, 0.21, 0.25, 5.5,
            Color.Parse("#2FA8D8"), 96)
    };

    private readonly DispatcherTimer _timer;
    private double _time;
    private bool _running;

    public static readonly StyledProperty<Color> BaseColorProperty =
        AvaloniaProperty.Register<AnimatedGradientBackground, Color>(nameof(BaseColor),
            Color.Parse("#090B10"));

    static AnimatedGradientBackground()
    {
        AffectsRender<AnimatedGradientBackground>(BaseColorProperty);
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

    /// <summary>Основной цвет фона под каплями.</summary>
    public Color BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
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

        // Время, а не «шаг фазы»: 0.033 с на кадр при 30 FPS.
        _time += FrameInterval.TotalSeconds;
        if (_time > 3600) _time = 0;

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 1 || bounds.Height < 1) return;

        context.FillRectangle(new SolidColorBrush(BaseColor), bounds);

        double minSide = Math.Min(bounds.Width, bounds.Height);
        foreach (var blob in Blobs)
        {
            double x = (blob.BaseX + blob.DriftX * Math.Sin(blob.FreqX * _time + blob.PhaseX))
                       * bounds.Width;
            double y = (blob.BaseY + blob.DriftY * Math.Sin(blob.FreqY * _time + blob.PhaseY))
                       * bounds.Height;

            // Быстрый подъём или спуск вытягивает каплю по вертикали и сжимает
            // по горизонтали — тот самый «слипшийся» силуэт лава-лампы.
            double speed = Math.Cos(blob.FreqY * _time + blob.PhaseY);
            double stretch = 1.0 + 0.17 * Math.Abs(speed);
            double squeeze = 1.0 - 0.11 * Math.Abs(speed);

            // Дыхание радиуса — отдельный, более быстрый синус.
            double breathe = 1.0 + 0.12 * Math.Sin(blob.PulseFreq * _time + blob.PulsePhase);

            double radius = blob.Radius * minSide * breathe;
            double radiusX = radius * squeeze;
            double radiusY = radius * stretch;

            context.DrawEllipse(CreateBlobBrush(blob), null, new Point(x, y), radiusX, radiusY);
        }
    }

    /// <summary>
    /// Радиальный градиент капли: плотная светящаяся сердцевина, заметный
    /// край с мягкой ореольной растушёвкой — силуэт читается как у капли лавы.
    /// </summary>
    private static RadialGradientBrush CreateBlobBrush(Blob blob)
    {
        byte body = blob.Alpha;
        byte mid = (byte)(blob.Alpha * 55 / 100);
        byte rim = (byte)(blob.Alpha * 18 / 100);

        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        };
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(body, blob.Color.R, blob.Color.G, blob.Color.B), 0.0));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(mid, blob.Color.R, blob.Color.G, blob.Color.B), 0.62));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(rim, blob.Color.R, blob.Color.G, blob.Color.B), 0.86));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(0, blob.Color.R, blob.Color.G, blob.Color.B), 1.0));
        return brush;
    }
}
