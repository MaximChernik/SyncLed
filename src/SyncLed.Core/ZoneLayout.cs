using System.Drawing;

namespace SyncLed;

/// <summary>Раскладка светодиодов по периметру монитора и выборка цветов из кадра.</summary>
public sealed class ZoneLayout
{
    public int LampCount { get; }
    public int HorizontalLamps { get; }
    public int VerticalLamps { get; }
    public int CaptureWidth { get; }
    public int CaptureHeight { get; }
    public IReadOnlyList<Rectangle> Zones => _zones;

    private readonly List<Rectangle> _zones = new();

    private ZoneLayout(int lampCount, int horizontal, int vertical, int width, int height)
    {
        LampCount = lampCount;
        HorizontalLamps = horizontal;
        VerticalLamps = vertical;
        CaptureWidth = width;
        CaptureHeight = height;
    }

    /// <summary>Делит общее число LED на горизонтальные/вертикальные стороны по пропорции монитора.</summary>
    public static (int Horizontal, int Vertical) SplitLamps(int lampCount, double aspect)
    {
        lampCount = Math.Clamp(lampCount, 8, 254);
        aspect = aspect <= 0.1 ? 16.0 / 9.0 : aspect;

        int h = (int)Math.Round(lampCount * aspect / (2.0 * (1.0 + aspect)));
        h = Math.Clamp(h, 2, lampCount / 2 - 2);
        int remainder = lampCount - 2 * h;
        if (remainder % 2 != 0)
        {
            h++;
            remainder = lampCount - 2 * h;
        }
        int v = Math.Max(1, remainder / 2);
        // Страховка от арифметических крайностей
        while (2 * h + 2 * v > lampCount) v--;
        while (2 * h + 2 * v < lampCount) h++;
        return (h, v);
    }

    public static ZoneLayout Build(int captureWidth, int captureHeight, int lampCount,
        double aspect, double edgeDepthPercent, int horizontalOverride = 0)
    {
        lampCount = Math.Clamp(lampCount, 8, 254);
        var (h, v) = SplitLamps(lampCount, aspect);
        if (horizontalOverride > 1)
        {
            int cand = Math.Clamp(horizontalOverride, 2, lampCount / 2 - 1);
            int rem = lampCount - 2 * cand;
            if (rem >= 2 && rem % 2 == 0)
            {
                h = cand;
                v = rem / 2;
            }
        }

        var layout = new ZoneLayout(h * 2 + v * 2, h, v, captureWidth, captureHeight);

        double pct = Math.Clamp(edgeDepthPercent, 4, 35) / 100.0;
        int depthY = Math.Max(2, (int)Math.Round(captureHeight * pct));
        int depthX = Math.Max(2, (int)Math.Round(captureWidth * pct));

        double sideHeight = Math.Max(1, captureHeight - 2.0 * depthY) / v;
        double topWidth = Math.Max(1, (double)captureWidth) / h;

        // Порядок как у оригинального приложения: левая сторона снизу вверх,
        // верх слева направо, правая сверху вниз, низ справа налево.
        for (int i = 0; i < v; i++)
        {
            double y = captureHeight - depthY - (i + 1) * sideHeight;
            layout._zones.Add(Rect(0, y, depthX, sideHeight));
        }
        for (int i = 0; i < h; i++)
        {
            layout._zones.Add(Rect(i * topWidth, 0, topWidth, depthY));
        }
        for (int i = 0; i < v; i++)
        {
            double y = depthY + i * sideHeight;
            layout._zones.Add(Rect(captureWidth - depthX, y, depthX, sideHeight));
        }
        for (int i = 0; i < h; i++)
        {
            double x = captureWidth - (i + 1) * topWidth;
            layout._zones.Add(Rect(x, captureHeight - depthY, topWidth, depthY));
        }

        return layout;
    }

    private static Rectangle Rect(double x, double y, double w, double h)
    {
        int left = (int)Math.Round(x);
        int top = (int)Math.Round(y);
        int right = Math.Max(left + 1, (int)Math.Round(x + w));
        int bottom = Math.Max(top + 1, (int)Math.Round(y + h));
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    /// <summary>Порядок зон под конкретную ленту: сдвиг старта и направление.</summary>
    public List<Rectangle> Ordered(int shift, bool reverse)
    {
        int n = _zones.Count;
        var result = new List<Rectangle>(n);
        for (int i = 0; i < n; i++)
        {
            int j = reverse ? shift - i : shift + i;
            j %= n;
            if (j < 0) j += n;
            result.Add(_zones[j]);
        }
        return result;
    }
}
