using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SyncLed.Avalonia.ViewModels;

namespace SyncLed.Avalonia.Views;

/// <summary>
/// Живая миниатюра ленты для карточки устройства. В режиме синхронизации —
/// тёмный монитор с LED-кольцом по периметру и пунктирной рамкой реальной
/// области захвата краёв экрана; для своего цвета и выключения — светящаяся
/// полоса. Состояние без данных нарисовано симметрично: центрированный
/// силуэт монитора, ровная пунктирная рамка захвата, тонкая равномерная
/// обводка LED и подпись «НЕТ ДАННЫХ» строго по центру экрана. Вся геометрия
/// кешируется по текущим границам/цветам, таймеров нет: контроль
/// перерисовывается только при изменении данных.
/// </summary>
public sealed class DevicePreviewControl : Control
{
    private const double ScreenAspect = 1.62;
    private const double Bezel = 5;

    private static readonly Color BodyTop = Color.Parse("#272D39");
    private static readonly Color BodyBottom = Color.Parse("#0D0F14");
    private static readonly Color FrameBorder = Color.Parse("#363E4E");
    private static readonly Color ScreenGlassTop = Color.Parse("#11141C");
    private static readonly Color ScreenGlassBottom = Color.Parse("#080A10");
    private static readonly Color InactiveRing = Color.Parse("#414A60");
    private static readonly Color MutedText = Color.Parse("#8490A4");
    private static readonly Color Accent = Color.Parse("#817BFF");
    private static readonly Color Accent2 = Color.Parse("#44CFFF");
    private static readonly Color StripOffFill = Color.Parse("#161921");
    private static readonly Color StripOffBorder = Color.Parse("#2A3344");
    private static readonly Color StandColor = Color.Parse("#2E3441");

    private static readonly Typeface UiFont = new("Segoe UI");
    private static readonly List<Rgb> EmptyColors = new();

    // Статические кисти/пера: цвета корпуса, подставки и рамки неизменны.
    private static readonly IBrush BodyFillBrush = CreateVerticalBrush(BodyTop, BodyBottom);
    private static readonly IBrush ScreenGlassBrush = CreateVerticalBrush(ScreenGlassTop, ScreenGlassBottom);
    private static readonly IBrush StandBrush = new SolidColorBrush(StandColor);
    private static readonly Pen BodyPen = new(new SolidColorBrush(FrameBorder), 1);
    private static readonly Pen ShinePen = new(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1);
    private static readonly Pen StripShinePen = new(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)), 1);
    private static readonly Color CaptureColor = Mix(Accent2, Accent, 0.35);
    private static readonly IBrush CaptureFillBrush = new SolidColorBrush(WithAlpha(CaptureColor, 34));
    private static readonly Pen CaptureDashPen =
        new(new SolidColorBrush(WithAlpha(CaptureColor, 82)), 1,
            new DashStyle(new[] { 4.0, 4.0 }, 0), PenLineCap.Flat, PenLineJoin.Miter);
    private static readonly IBrush IdleLedPenBrush = new SolidColorBrush(InactiveRing);
    private static readonly Pen IdleLedPen =
        new(IdleLedPenBrush, 2, null, PenLineCap.Round, PenLineJoin.Round);
    private static readonly IBrush IdleAccentLineBrush =
        new SolidColorBrush(WithAlpha(Mix(Accent2, Accent, 0.5), 90));
    private static readonly Pen IdleAccentLinePen = new(IdleAccentLineBrush, 1.5, null, PenLineCap.Round);
    private static readonly IBrush StripOffBrush = new SolidColorBrush(StripOffFill);
    private static readonly Pen StripOffPen = new(new SolidColorBrush(StripOffBorder), 1);
    private static readonly IBrush NoDataTextBrush = new SolidColorBrush(MutedText);

    private DeviceViewModel? _device;

    // Кеши брашей и перьев по цвету: цвета меняются каждые ~220 мс.
    private readonly Dictionary<Color, SolidColorBrush> _fills = new();
    private readonly Dictionary<Color, Pen> _ringPens = new();
    private readonly Dictionary<(Color Color, byte Alpha), RadialGradientBrush> _haloBrushes = new();
    private readonly Dictionary<Color, IBrush> _innerGlowBrushes = new();
    private readonly Dictionary<Color, IBrush> _stripFillBrushes = new();

    // Кеш развёртки периметра монитора: пересобирается только при смене границ.
    private Rect _ringRect;
    private Point[] _ringPoints = System.Array.Empty<Point>();
    private double[] _ringCumulative = System.Array.Empty<double>();
    private double _ringTotal;
    private double _ringStart;
    private bool _ringValid;

    // Кеш геометрии рамки захвата (живой цвет) по прямоугольнику и глубине краёв.
    private Rect _bandKey;
    private int _bandDepthKey = -1;
    private StreamGeometry? _bandGeometry;

    // Кеш геометрии состояния «нет данных»: внутренняя граница полосы захвата.
    private readonly Dictionary<(Rect Rect, double Radius), StreamGeometry> _idleCache = new();

    // Кеш скруглённых прямоугольников (кольцо одного цвета, полоса, стекло).
    private readonly Dictionary<(Rect Rect, double Radius), StreamGeometry> _roundRectCache = new();

    // Кеш подписи «НЕТ ДАННЫХ»: текст пересоздаётся только при смене кегля.
    private FormattedText? _noDataText;
    private double _noDataTextSize = -1;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_device is INotifyPropertyChanged old)
            old.PropertyChanged -= OnDevicePropertyChanged;
        _device = DataContext as DeviceViewModel;
        if (_device is INotifyPropertyChanged current)
            current.PropertyChanged += OnDevicePropertyChanged;
        InvalidateVisual();
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? Math.Min(availableSize.Width, 400) : 400;
        double height = double.IsFinite(availableSize.Height) ? Math.Min(availableSize.Height, 150) : 150;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 40 || bounds.Height < 30) return;

        var device = _device;
        List<Rgb> colors = device?.PreviewColors ?? EmptyColors;
        string mode = device?.SelectedMode?.Value ?? "sync";
        if (mode is not ("sync" or "color" or "off")) mode = "sync";
        if (device is { Enabled: false }) mode = "off";

        switch (mode)
        {
            case "color":
            {
                Rgb solid = colors.Count > 0
                    ? colors[0]
                    : ColorUtil.ParseHex(device?.SelectedColorHex, Rgb.White);
                DrawStrip(context, ToColor(solid), filled: true, bounds);
                break;
            }
            case "off":
                DrawStrip(context, StripOffFill, filled: false, bounds);
                break;
            default:
                DrawMonitor(context, colors, DepthPercent(device), bounds);
                break;
        }
    }

    /// <summary>Глубина захвата краёв экрана: собственная настройка ленты или общая (12 %).</summary>
    private static int DepthPercent(DeviceViewModel? device) =>
        Math.Clamp(device?.Device.EdgeDepthPercent ?? 12, 4, 30);

    // ── монитор с подсветкой по периметру ───────────────────────────────────

    private void DrawMonitor(DrawingContext context, List<Rgb> colors, int depthPercent, Rect bounds)
    {
        double availableWidth = Math.Max(40, bounds.Width - 40);
        double availableHeight = Math.Max(30, bounds.Height - 40);
        double screenWidth = Math.Min(availableWidth, 300);
        double screenHeight = screenWidth / ScreenAspect;
        if (screenHeight > availableHeight)
        {
            screenHeight = availableHeight;
            screenWidth = screenHeight * ScreenAspect;
        }

        double standHeight = Math.Max(8, screenHeight * 0.12);
        double cx = bounds.Width / 2;
        // Симметрично по вертикали: без добавочных смещений.
        double top = (bounds.Height - screenHeight - standHeight) / 2;
        var screen = new Rect(cx - screenWidth / 2, top, screenWidth, screenHeight);

        // Средний оттенок кадра — один раз на кадр для всех свечений.
        Color? accent = colors.Count > 0 ? Average(colors) : null;

        // Мягкое цветное свечение вокруг монитора (только живые цвета).
        if (accent is { } haloColor)
        {
            var halo = new Rect(
                screen.X - screenWidth * 0.22,
                screen.Y - screenHeight * 0.3,
                screen.Width + screenWidth * 0.44,
                screen.Height + screenHeight * 0.6);
            context.DrawRectangle(GetHalo(haloColor, 46), null, halo);
        }

        // Корпус монитора.
        double bodyRadius = Math.Max(6, screenHeight * 0.1);
        context.DrawRectangle(BodyFillBrush, BodyPen, screen, bodyRadius, bodyRadius);

        if (accent is { } glowColor)
        {
            // Внутреннее свечение экрана от текущих цветов.
            var inner = new Rect(screen.X + 4, screen.Y + 4,
                Math.Max(1, screen.Width - 8), Math.Max(1, screen.Height - 8));
            double innerRadius = Math.Max(4, screenHeight * 0.08);
            context.DrawRectangle(GetInnerGlow(glowColor), null, inner, innerRadius, innerRadius);

            // Область захвата краёв экрана: её и показывает пунктирная рамка.
            DrawCaptureBands(context, screen, depthPercent);

            // Обводка LED по периметру корпуса.
            var ringRect = new Rect(screen.X + 3, screen.Y + 3,
                Math.Max(1, screen.Width - 6), Math.Max(1, screen.Height - 6));
            DrawRing(context, ringRect, Math.Max(4, screenHeight * 0.055), colors);
        }
        else
        {
            // Состояние без данных: выровненный силуэт и центрированная подпись.
            DrawIdleScreen(context, screen, bodyRadius, depthPercent);
        }

        // Блик по верхней кромке корпуса.
        context.DrawLine(ShinePen,
            new Point(screen.X + 12, screen.Y + 1.5),
            new Point(screen.Right - 12, screen.Y + 1.5));

        // Подставка (строго по центру).
        double neckWidth = Math.Max(10, screenWidth * 0.055);
        double baseWidth = Math.Max(34, screenWidth * 0.22);
        var neck = new Rect(cx - neckWidth / 2, screen.Bottom - 1, neckWidth, Math.Max(1, standHeight - 4));
        context.DrawRectangle(StandBrush, null, neck, 2, 2);
        var foot = new Rect(cx - baseWidth / 2, screen.Bottom + standHeight - 7, baseWidth, 4);
        context.DrawRectangle(StandBrush, null, foot, 2, 2);
    }

    /// <summary>
    /// Симметричное состояние «нет данных»: стекло экрана, ровная полоса захвата
    /// по глубине краёв, тонкая равномерная LED-обводка и подпись по центру.
    /// </summary>
    private void DrawIdleScreen(DrawingContext context, Rect screen, double bodyRadius, int depthPercent)
    {
        double cx = screen.X + screen.Width / 2;

        // Поверхность «стекла»: скруглённый прямоугольник корпуса.
        var glass = new Rect(screen.X + Bezel, screen.Y + Bezel,
            Math.Max(1, screen.Width - Bezel * 2), Math.Max(1, screen.Height - Bezel * 2));
        double glassRadius = Math.Max(3, bodyRadius - Bezel * 0.6);
        context.DrawRectangle(ScreenGlassBrush, null, glass, glassRadius, glassRadius);

        // Полоса захвата: одинаковая по всем краям, глубина — из настройки ленты.
        // Вся поверхность слегка тонируется, затем центр перекрывается стеклом —
        // остаётся ровная светящаяся рамка по периметру.
        Rect surface = IdleSurfaceRect(glass, depthPercent);
        double surfaceRadius = Math.Max(3, Math.Min(surface.Width, surface.Height) * 0.14);
        StreamGeometry surfaceGeometry = IdleSurfaceGeometry(surface, surfaceRadius);
        context.DrawRectangle(CaptureFillBrush, null, glass, glassRadius, glassRadius);
        context.DrawGeometry(ScreenGlassBrush, null, surfaceGeometry);
        context.DrawGeometry(null, CaptureDashPen, surfaceGeometry);

        // Тонкая равномерная LED-обводка по периметру корпуса.
        var ledRect = new Rect(screen.X + 3, screen.Y + 3,
            Math.Max(1, screen.Width - 6), Math.Max(1, screen.Height - 6));
        context.DrawGeometry(null, IdleLedPen, RoundRectGeometry(ledRect, Math.Max(3, bodyRadius - 3)));

        // Подпись строго по центру стекла + короткая акцентная черта под ней.
        FormattedText text = NoDataText(Math.Clamp(screen.Height * 0.12, 8, 12));
        double tx = cx - text.Width / 2;
        double ty = glass.Y + (glass.Height - text.Height) / 2;
        context.DrawText(text, new Point(tx, ty));

        double lineY = ty + text.Height + Math.Max(3, screen.Height * 0.045);
        double halfLine = Math.Clamp(text.Width * 0.25, 10, 22);
        context.DrawLine(IdleAccentLinePen, new Point(cx - halfLine, lineY), new Point(cx + halfLine, lineY));
    }

    /// <summary>Внутренняя граница полосы захвата: отступ глубины краёв со всех сторон.</summary>
    private static Rect IdleSurfaceRect(Rect glass, int depthPercent)
    {
        double depth = depthPercent / 100.0;
        double insetX = Math.Max(6, glass.Width * depth);
        double insetY = Math.Max(4, glass.Height * depth);
        return new Rect(glass.X + insetX, glass.Y + insetY,
            Math.Max(1, glass.Width - insetX * 2), Math.Max(1, glass.Height - insetY * 2));
    }

    /// <summary>Граница полосы захвата со скруглением, кеш по прямоугольнику и радиусу.</summary>
    private StreamGeometry IdleSurfaceGeometry(Rect surface, double radius)
    {
        var key = (surface, Math.Round(radius, 2));
        if (_idleCache.TryGetValue(key, out var cached)) return cached;

        var geometry = BuildRoundRect(surface, radius);
        if (_idleCache.Count > 8) _idleCache.Clear();
        _idleCache[key] = geometry;
        return geometry;
    }

    /// <summary>Подпись «НЕТ ДАННЫХ»: кешируется по кеглю.</summary>
    private FormattedText NoDataText(double fontSize)
    {
        if (_noDataText == null || Math.Abs(_noDataTextSize - fontSize) > 0.01)
        {
            _noDataTextSize = fontSize;
            _noDataText = new FormattedText("НЕТ ДАННЫХ", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, UiFont, fontSize, NoDataTextBrush);
        }
        return _noDataText;
    }

    /// <summary>
    /// Показывает реальную область захвата края экрана: чем выше глубина краёв,
    /// тем шире подсвеченная пунктирная рамка вокруг миниатюры монитора.
    /// Геометрия кешируется по прямоугольнику и глубине.
    /// </summary>
    private void DrawCaptureBands(DrawingContext context, Rect screen, int depthPercent)
    {
        if (_bandGeometry == null || _bandKey != screen || _bandDepthKey != depthPercent)
        {
            double depth = depthPercent / 100.0;
            double x = Math.Max(1, screen.Width * depth);
            double y = Math.Max(1, screen.Height * depth);

            var bands = new StreamGeometry();
            using (var geometry = bands.Open())
            {
                AddRect(geometry, new Rect(screen.X, screen.Y, screen.Width, y));
                AddRect(geometry, new Rect(screen.X, screen.Bottom - y, screen.Width, y));
                AddRect(geometry, new Rect(screen.X, screen.Y + y, x, Math.Max(1, screen.Height - y * 2)));
                AddRect(geometry, new Rect(screen.Right - x, screen.Y + y, x, Math.Max(1, screen.Height - y * 2)));
            }

            _bandKey = screen;
            _bandDepthKey = depthPercent;
            _bandGeometry = bands;
        }

        context.DrawGeometry(CaptureFillBrush, null, _bandGeometry);
        context.DrawGeometry(null, CaptureDashPen, _bandGeometry);
    }

    /// <summary>Рисует периметр скруглённого прямоугольника сегментами по числу LED.</summary>
    private void DrawRing(DrawingContext context, Rect rect, double thickness, List<Rgb> colors)
    {
        double radius = Math.Min(8, Math.Min(rect.Width, rect.Height) / 2);
        if (colors.Count <= 1)
        {
            var color = colors.Count == 0 ? InactiveRing : ToColor(colors[0]);
            var pen = GetRingPen(color, thickness);
            context.DrawGeometry(null, pen, RoundRectGeometry(rect, radius));
            return;
        }

        EnsureRingGeometry(rect, radius);
        Point[] points = _ringPoints;
        double[] cumulative = _ringCumulative;
        double total = _ringTotal;
        if (points.Length < 2 || total <= 0.5) return;

        int leds = colors.Count;
        double step = total / leds;
        for (int i = 0; i < leds; i++)
        {
            Point a = At(points, cumulative, _ringStart + i * step);
            Point b = At(points, cumulative, _ringStart + (i + 1) * step);
            context.DrawLine(GetRingPen(ToColor(colors[i]), thickness), a, b);
        }
    }

    /// <summary>
    /// Развёртка периметра в последовательность точек с накопленными длинами.
    /// Результат кешируется по прямоугольнику: повторный кадр не пересчитывает геометрию.
    /// </summary>
    private void EnsureRingGeometry(Rect rect, double radius)
    {
        if (_ringValid && _ringRect == rect) return;

        List<Point> points = FlattenRoundRect(rect, 2.5);
        int count = points.Count;
        var cumulative = new double[count + 1];
        for (int i = 0; i < count; i++)
            cumulative[i + 1] = cumulative[i] + Distance(points[i], points[(i + 1) % count]);

        _ringRect = rect;
        _ringPoints = points.ToArray();
        _ringCumulative = cumulative;
        _ringTotal = cumulative[count];
        // Начало — левый нижний угол, как в ZoneLayout: левая сторона снизу вверх.
        _ringStart = 0;
        _ringValid = true;
    }

    /// <summary>
    /// Точки по периметру скруглённого прямоугольника: начинаем у левого нижнего
    /// угла, идём вверх по левой стороне, затем по часовой стрелке по периметру.
    /// </summary>
    private static List<Point> FlattenRoundRect(Rect rect, double maxStep)
    {
        double r = Math.Clamp(Math.Min(8, Math.Min(rect.Width, rect.Height) / 2), 0.5,
            Math.Min(rect.Width, rect.Height) / 2);
        var points = new List<Point>();

        int edgeX = Math.Max(2, (int)Math.Ceiling(rect.Height / maxStep));
        int edgeY = Math.Max(2, (int)Math.Ceiling(rect.Width / maxStep));
        int arc = Math.Max(4, (int)Math.Ceiling((Math.PI / 2) * r / maxStep));

        // Левая сторона снизу вверх.
        for (int i = 0; i <= edgeX; i++)
        {
            double t = i / (double)edgeX;
            points.Add(new Point(rect.X, rect.Bottom - r - t * (rect.Height - 2 * r)));
        }

        // Дуга верхнего левого угла.
        AddArc(points, new Point(rect.X + r, rect.Y + r), r, Math.PI, 1.5 * Math.PI, arc);

        // Верхняя сторона слева направо.
        for (int i = 1; i <= edgeY; i++)
            points.Add(new Point(rect.X + r + (rect.Width - 2 * r) * i / edgeY, rect.Y));

        // Дуга верхнего правого угла.
        AddArc(points, new Point(rect.Right - r, rect.Y + r), r, 1.5 * Math.PI, 2 * Math.PI, arc);

        // Правая сторона сверху вниз.
        for (int i = 1; i <= edgeX; i++)
            points.Add(new Point(rect.Right, rect.Y + r + (rect.Height - 2 * r) * i / edgeX));

        // Дуга нижнего правого угла.
        AddArc(points, new Point(rect.Right - r, rect.Bottom - r), r, 0, 0.5 * Math.PI, arc);

        // Нижняя сторона справа налево.
        for (int i = 1; i <= edgeY; i++)
            points.Add(new Point(rect.Right - r - (rect.Width - 2 * r) * i / edgeY, rect.Bottom));

        // Дуга нижнего левого угла — возвращаемся к началу, замыкая периметр.
        AddArc(points, new Point(rect.X + r, rect.Bottom - r), r, 0.5 * Math.PI, Math.PI, arc);
        if (points.Count > 1 && Close(points[0], points[^1])) points.RemoveAt(points.Count - 1);
        return points;
    }

    private static void AddArc(List<Point> points, Point center, double radius, double from, double to, int steps)
    {
        for (int i = 1; i <= steps; i++)
        {
            double angle = from + (to - from) * i / steps;
            points.Add(new Point(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle)));
        }
    }

    private static void AddRect(StreamGeometryContext ctx, Rect rect)
    {
        ctx.BeginFigure(rect.TopLeft, true);
        ctx.LineTo(rect.TopRight);
        ctx.LineTo(rect.BottomRight);
        ctx.LineTo(rect.BottomLeft);
        ctx.EndFigure(true);
    }

    /// <summary>Скруглённый прямоугольник с кешем по прямоугольнику и радиусу.</summary>
    private StreamGeometry RoundRectGeometry(Rect rect, double radius)
    {
        var key = (rect, Math.Round(radius, 2));
        if (_roundRectCache.TryGetValue(key, out var cached)) return cached;

        var geometry = BuildRoundRect(rect, radius);
        if (_roundRectCache.Count > 16) _roundRectCache.Clear();
        _roundRectCache[key] = geometry;
        return geometry;
    }

    /// <summary>Строит замкнутый скруглённый прямоугольник против часовой стрелки.</summary>
    private static StreamGeometry BuildRoundRect(Rect rect, double radius)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double r = Math.Max(0.5, radius);
            ctx.BeginFigure(new Point(rect.X + r, rect.Y), true);
            ctx.LineTo(new Point(rect.Right - r, rect.Y));
            ctx.ArcTo(new Point(rect.Right - r, rect.Y + r), new Size(r, r),
                0, false, SweepDirection.Clockwise, true);
            ctx.LineTo(new Point(rect.Right, rect.Bottom - r));
            ctx.ArcTo(new Point(rect.Right - r, rect.Bottom - r), new Size(r, r),
                0, false, SweepDirection.Clockwise, true);
            ctx.LineTo(new Point(rect.X + r, rect.Bottom));
            ctx.ArcTo(new Point(rect.X + r, rect.Bottom - r), new Size(r, r),
                0, false, SweepDirection.Clockwise, true);
            ctx.LineTo(new Point(rect.X, rect.Y + r));
            ctx.ArcTo(new Point(rect.X + r, rect.Y + r), new Size(r, r),
                0, false, SweepDirection.Clockwise, true);
            ctx.EndFigure(true);
        }
        return geometry;
    }

    private static Point At(Point[] points, double[] cumulative, double distance)
    {
        int count = points.Length;
        double total = cumulative[count];
        distance %= total;
        if (distance < 0) distance += total;

        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (cumulative[mid] <= distance) lo = mid;
            else hi = mid - 1;
        }

        double span = cumulative[lo + 1] - cumulative[lo];
        double t = span <= 0.0001 ? 0 : (distance - cumulative[lo]) / span;
        Point a = points[lo];
        Point b = points[(lo + 1) % count];
        return new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    // ── светящаяся полоса для статичного цвета / выключения ─────────────────

    private void DrawStrip(DrawingContext context, Color color, bool filled, Rect bounds)
    {
        double barHeight = Math.Min(18, bounds.Height - 16);
        var area = new Rect(18, (bounds.Height - barHeight) / 2,
            Math.Max(1, bounds.Width - 36), barHeight);
        double radius = barHeight / 2;

        if (filled)
        {
            var halo = new Rect(area.X - 14, area.Y - 14, area.Width + 28, area.Height + 28);
            context.DrawRectangle(GetHalo(color, 55), null, halo);

            // Лёгкий объём без выбеливания: оттенок остаётся тем же.
            context.DrawRectangle(GetStripFill(color), null, area, radius, radius);

            var border = new Pen(GetFill(WithAlpha(Lighten(color, 0.22), 110)), 1);
            context.DrawGeometry(null, border, RoundRectGeometry(area, radius));

            context.DrawLine(StripShinePen,
                new Point(area.X + 8, area.Y + 2.2),
                new Point(area.Right - 8, area.Y + 2.2));
        }
        else
        {
            context.DrawRectangle(StripOffBrush, StripOffPen, area, radius, radius);

            var text = new FormattedText("выключено", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, UiFont, 11, GetFill(MutedText));
            context.DrawText(text, new Point(
                area.X + (area.Width - text.Width) / 2,
                area.Y + (area.Height - text.Height) / 2));
        }
    }

    // ── вспомогательные методы ──────────────────────────────────────────────

    private Pen GetRingPen(Color color, double thickness)
    {
        if (_ringPens.TryGetValue(color, out var pen) && Math.Abs(pen.Thickness - thickness) < 0.01)
            return pen;

        pen = new Pen(GetFill(color), thickness, null, PenLineCap.Round, PenLineJoin.Round);
        if (_ringPens.Count > 256) _ringPens.Clear();
        _ringPens[color] = pen;
        return pen;
    }

    /// <summary>Радиальное свечение вокруг монитора/полосы, кеш по цвету и альфе.</summary>
    private IBrush GetHalo(Color color, byte alpha)
    {
        var key = (color, alpha);
        if (_haloBrushes.TryGetValue(key, out var brush)) return brush;

        brush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithAlpha(color, alpha), 0),
                new GradientStop(WithAlpha(color, 0), 1)
            }
        };
        if (_haloBrushes.Count > 128) _haloBrushes.Clear();
        _haloBrushes[key] = brush;
        return brush;
    }

    /// <summary>Внутреннее свечение экрана от текущих цветов, кеш по цвету.</summary>
    private IBrush GetInnerGlow(Color color)
    {
        if (_innerGlowBrushes.TryGetValue(color, out var brush)) return brush;

        brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithAlpha(color, 40), 0),
                new GradientStop(WithAlpha(color, 6), 1)
            }
        };
        if (_innerGlowBrushes.Count > 64) _innerGlowBrushes.Clear();
        _innerGlowBrushes[color] = brush;
        return brush;
    }

    /// <summary>Градиентная заливка светящейся полосы, кеш по цвету.</summary>
    private IBrush GetStripFill(Color color)
    {
        if (_stripFillBrushes.TryGetValue(color, out var brush)) return brush;

        brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Lighten(color, 0.04), 0),
                new GradientStop(Darken(color, 0.06), 1)
            }
        };
        if (_stripFillBrushes.Count > 64) _stripFillBrushes.Clear();
        _stripFillBrushes[color] = brush;
        return brush;
    }

    private SolidColorBrush GetFill(Color color)
    {
        if (_fills.TryGetValue(color, out var fill)) return fill;
        fill = new SolidColorBrush(color);
        if (_fills.Count > 256) _fills.Clear();
        _fills[color] = fill;
        return fill;
    }

    private static IBrush CreateVerticalBrush(Color top, Color bottom) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(top, 0),
            new GradientStop(bottom, 1)
        }
    };

    private static Color ToColor(Rgb color) => new(255, color.R, color.G, color.B);

    private static Color WithAlpha(Color color, byte alpha) => new(alpha, color.R, color.G, color.B);

    private static Color Lighten(Color color, double t) => new(255,
        (byte)Math.Clamp(color.R + (255 - color.R) * t, 0, 255),
        (byte)Math.Clamp(color.G + (255 - color.G) * t, 0, 255),
        (byte)Math.Clamp(color.B + (255 - color.B) * t, 0, 255));

    private static Color Darken(Color color, double t) => new(255,
        (byte)(color.R * (1 - t)),
        (byte)(color.G * (1 - t)),
        (byte)(color.B * (1 - t)));

    private static Color Mix(Color a, Color b, double t) => new(255,
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static Color Average(List<Rgb> colors)
    {
        long r = 0, g = 0, b = 0;
        foreach (var color in colors)
        {
            r += color.R;
            g += color.G;
            b += color.B;
        }
        int n = Math.Max(1, colors.Count);
        return new Color(255, (byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static double Distance(Point a, Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool Close(Point a, Point b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;
}
