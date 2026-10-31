using System.Drawing;
using System.Drawing.Drawing2D;

namespace SyncLed;

/// <summary>
/// Знак приложения: тёмная плитка, монитор со светящейся LED-рамкой и подставка.
/// Один рисунок используется и в шапке окна, и при сборке app.ico, поэтому иконка
/// в интерфейсе, на панели задач, в трее и в диалогах выглядит одинаково.
/// </summary>
internal static class AppIconArt
{
    /// <summary>Рисует знак в квадрате <paramref name="rect"/>.</summary>
    /// <param name="glow">0 — обычное состояние, 1 — подсветка при наведении.</param>
    /// <param name="active">Идёт синхронизация: рамка светится в полную силу.</param>
    public static void Draw(Graphics g, RectangleF rect, float glow = 0f, bool active = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        float s = Math.Min(rect.Width, rect.Height);
        if (s < 8f) return;

        float x = rect.X + (rect.Width - s) / 2f;
        float y = rect.Y + (rect.Height - s) / 2f;
        bool small = s < 48f;

        glow = Math.Clamp(glow, 0f, 1f);
        float energy = active ? 1f : 0.72f + glow * 0.28f;

        // ── плитка ─────────────────────────────────────────────────────────
        var tile = new RectangleF(x, y, s, s);
        using (var tilePath = Theme.Rounded(tile, s * 0.225f))
        {
            using (var fill = new LinearGradientBrush(tile,
                       Theme.Mix(Color.FromArgb(38, 48, 69), Color.FromArgb(54, 67, 94), glow),
                       Color.FromArgb(9, 12, 18), 135f))
                g.FillPath(fill, tilePath);
            using (var edge = new Pen(Color.FromArgb(62, 76, 100), Math.Max(1f, s * 0.007f)))
                g.DrawPath(edge, tilePath);
        }

        // ── геометрия монитора ─────────────────────────────────────────────
        float mw = s * (small ? 0.66f : 0.60f);
        float mh = mw / 1.62f;
        float mx = x + (s - mw) / 2f;
        float my = y + s * (small ? 0.27f : 0.285f);
        var monitor = new RectangleF(mx, my, mw, mh);
        float radius = Math.Max(1.2f, s * 0.055f);

        // Цветное свечение вокруг монитора: мягкое, чтобы не заливать плитку.
        var haloRect = new RectangleF(mx - mw * 0.22f, my - mh * 0.34f, mw * 1.44f, mh * 1.72f);
        using (var haloPath = new GraphicsPath())
        {
            haloPath.AddEllipse(haloRect);
            using var halo = new PathGradientBrush(haloPath)
            {
                CenterPoint = new PointF(mx + mw / 2f, my + mh / 2f),
                CenterColor = Theme.Alpha(Theme.Accent, (int)((small ? 40 : 62) * energy)),
                SurroundColors = new[] { Theme.Alpha(Theme.Accent, 0) }
            };
            g.FillPath(halo, haloPath);
        }

        // Корпус.
        using (var body = Theme.Rounded(monitor, radius))
        {
            using (var fill = new LinearGradientBrush(monitor,
                       Color.FromArgb(38, 48, 67), Color.FromArgb(16, 21, 31), 90f))
                g.FillPath(fill, body);
            using (var edge = new Pen(Color.FromArgb(82, 97, 124), Math.Max(1f, s * 0.006f)))
                g.DrawPath(edge, body);
        }

        // ── экран и LED-рамка ─────────────────────────────────────────────
        float ringWidth = Math.Clamp(s * 0.055f, 1.6f, 14f);
        float ringInset = ringWidth * 0.5f + (small ? 0f : s * 0.008f);
        // На малых размерах рамка сама служит границей корпуса — так экран
        // остаётся заметным даже в 16 px.
        var screen = small
            ? monitor
            : new RectangleF(
                monitor.X + ringInset, monitor.Y + ringInset,
                Math.Max(1f, monitor.Width - ringInset * 2f),
                Math.Max(1f, monitor.Height - ringInset * 2f));

        using (var screenPath = Theme.Rounded(screen, Math.Max(0.8f, radius - ringInset * 0.55f)))
        {
            using (var fill = new LinearGradientBrush(screen,
                       Color.FromArgb(9, 14, 22), Color.FromArgb(25, 35, 52), 90f))
                g.FillPath(fill, screenPath);

            if (!small)
            {
                using var inner = new LinearGradientBrush(screen,
                    Theme.Alpha(Theme.Accent2, 0), Theme.Alpha(Theme.Accent2, 26), 90f);
                g.FillPath(inner, screenPath);
            }
        }

        var ring = new RectangleF(
            screen.X + ringWidth * 0.5f, screen.Y + ringWidth * 0.5f,
            Math.Max(1f, screen.Width - ringWidth),
            Math.Max(1f, screen.Height - ringWidth));
        using (var ringPath = Theme.Rounded(ring, Math.Max(0.8f, radius * 0.8f)))
        {
            // Свечение рисуем до заливки экрана: наружу оно выходит бликом на
            // корпусе, а внутрь не просачивается — экран остаётся тёмным.
            if (!small || s >= 22f)
            {
                using (var glowPen = new Pen(Theme.Alpha(Theme.Accent, (int)((small ? 52 : 70) * energy)),
                           ringWidth * (small ? 2.2f : 2.6f)) { LineJoin = LineJoin.Round })
                    g.DrawPath(glowPen, ringPath);

                if (!small)
                    using (var midPen = new Pen(Theme.Alpha(Theme.Accent2, (int)(92 * energy)),
                               ringWidth * 1.5f) { LineJoin = LineJoin.Round })
                        g.DrawPath(midPen, ringPath);
            }

            using (var screenPath = Theme.Rounded(screen, Math.Max(0.8f, radius - ringInset * 0.55f)))
            {
                using (var fill = new LinearGradientBrush(screen,
                           Color.FromArgb(9, 14, 22), Color.FromArgb(25, 35, 52), 90f))
                    g.FillPath(fill, screenPath);

                if (!small)
                {
                    using var inner = new LinearGradientBrush(screen,
                        Theme.Alpha(Theme.Accent2, 18), Theme.Alpha(Theme.Accent2, 0), 90f);
                    g.FillPath(inner, screenPath);
                }
            }

            if (s >= 96f)
                DrawLedSegments(g, ringPath, ringWidth, energy);
            else
                using (var pen = new Pen(RingBrush(ring, energy), ringWidth)
                       { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, ringPath);

            // Блик на рамке — «включённый» светодиод.
            if (!small)
            {
                float sparkRadius = Math.Max(0.9f, s * 0.011f);
                var spark = new PointF(ring.X + ring.Width * 0.20f, ring.Y + ringWidth * 0.1f);
                var sparkGlow = new RectangleF(spark.X - sparkRadius * 3.2f, spark.Y - sparkRadius * 3.2f,
                    sparkRadius * 6.4f, sparkRadius * 6.4f);
                using (var sparkPath = new GraphicsPath())
                {
                    sparkPath.AddEllipse(sparkGlow);
                    using var sparkBrush = new PathGradientBrush(sparkPath)
                    {
                        CenterColor = Theme.Alpha(Theme.Accent2, (int)(120 * energy)),
                        SurroundColors = new[] { Theme.Alpha(Theme.Accent2, 0) }
                    };
                    g.FillPath(sparkBrush, sparkPath);
                }
                using var white = new SolidBrush(Color.FromArgb(215, 240, 250, 255));
                g.FillEllipse(white, spark.X - sparkRadius, spark.Y - sparkRadius,
                    sparkRadius * 2f, sparkRadius * 2f);
            }
        }

        // ── подставка ─────────────────────────────────────────────────────
        float neckWidth = Math.Max(1.4f, s * 0.058f);
        float neckHeight = Math.Max(1.2f, s * 0.052f);
        float baseWidth = Math.Max(3f, s * (small ? 0.34f : 0.27f));
        float baseHeight = Math.Max(1.4f, s * 0.034f);
        float neckX = x + (s - neckWidth) / 2f;
        float neckY = monitor.Bottom - 0.5f;

        using (var neck = new SolidBrush(Color.FromArgb(146, 163, 192)))
            g.FillRectangle(neck, neckX, neckY, neckWidth, neckHeight);

        var foot = new RectangleF(x + (s - baseWidth) / 2f, neckY + neckHeight - 0.5f,
            baseWidth, baseHeight);
        using (var footPath = Theme.Rounded(foot, baseHeight * 0.5f))
        using (var footBrush = new LinearGradientBrush(foot,
                   Color.FromArgb(216, 228, 246), Color.FromArgb(112, 130, 158), 90f))
            g.FillPath(footBrush, footPath);
    }

    /// <summary>Градиент рамки: синий → голубой → фиолетовый → синий.</summary>
    private static LinearGradientBrush RingBrush(RectangleF rect, float energy)
    {
        var brush = new LinearGradientBrush(rect, Theme.Accent, Theme.Accent2, 35f);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = new[]
            {
                RingColor(0.00f, energy), RingColor(0.30f, energy),
                RingColor(0.62f, energy), RingColor(0.88f, energy), RingColor(0.00f, energy)
            },
            Positions = new[] { 0f, 0.3f, 0.62f, 0.88f, 1f }
        };
        return brush;
    }

    /// <summary>Цвет точки на условной ленте: плавный переход по трём акцентам.</summary>
    private static Color RingColor(float t, float energy)
    {
        Color a = Theme.Accent;
        Color b = Theme.Accent2;
        Color c = Color.FromArgb(167, 123, 255);

        float u = (t % 1f) * 3f;
        Color color = u < 1f ? Theme.Mix(a, b, u)
            : u < 2f ? Theme.Mix(b, c, u - 1f)
            : Theme.Mix(c, a, u - 2f);
        return Theme.Alpha(color, (int)Math.Clamp(255 * (0.55f + 0.45f * energy), 140, 255));
    }

    /// <summary>Рамка из отдельных светодиодов — для крупных размеров иконки.</summary>
    private static void DrawLedSegments(Graphics g, GraphicsPath path, float width, float energy)
    {
        using var flat = (GraphicsPath)path.Clone();
        flat.Flatten(null, 0.35f);
        PointF[] points = flat.PathPoints;
        if (points.Length < 2) return;

        var lengths = new float[points.Length];
        float total = 0f;
        for (int i = 1; i < points.Length; i++)
        {
            float dx = points[i].X - points[i - 1].X;
            float dy = points[i].Y - points[i - 1].Y;
            total += MathF.Sqrt(dx * dx + dy * dy);
            lengths[i] = total;
        }
        if (total <= 1f) return;

        int count = Math.Clamp((int)MathF.Round(total / (width * 3.4f)), 12, 44);
        float gap = MathF.Max(0.8f, width * 0.5f);
        float half = MathF.Max(0.4f, total / count * 0.5f - gap * 0.5f);

        using var pen = new Pen(Color.White, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        for (int i = 0; i < count; i++)
        {
            float mid = total * (i + 0.5f) / count;
            pen.Color = RingColor((float)i / count, energy);
            g.DrawLine(pen,
                PointAt(points, lengths, mid - half),
                PointAt(points, lengths, mid + half));
        }
    }

    private static PointF PointAt(PointF[] points, float[] lengths, float distance)
    {
        distance = Math.Clamp(distance, 0f, lengths[^1]);
        for (int i = 1; i < points.Length; i++)
        {
            if (lengths[i] < distance) continue;
            float span = lengths[i] - lengths[i - 1];
            float t = span <= 0.001f ? 0f : (distance - lengths[i - 1]) / span;
            return new PointF(
                points[i - 1].X + (points[i].X - points[i - 1].X) * t,
                points[i - 1].Y + (points[i].Y - points[i - 1].Y) * t);
        }
        return points[^1];
    }
}
