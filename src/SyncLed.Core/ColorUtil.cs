using System.Drawing;

namespace SyncLed;

public static class ColorUtil
{
    public static Rgb FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        double c = value * saturation;
        double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m = value - c;
        double r, g, b;
        if (hue < 60) { r = c; g = x; b = 0; }
        else if (hue < 120) { r = x; g = c; b = 0; }
        else if (hue < 180) { r = 0; g = c; b = x; }
        else if (hue < 240) { r = 0; g = x; b = c; }
        else if (hue < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return new Rgb(
            (byte)Math.Clamp(Math.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + m) * 255), 0, 255));
    }

    /// <summary>Яркость/насыщенность/гамма/поканальная калибровка.</summary>
    public static Rgb Process(Rgb color, double brightness, double saturation, double gamma,
        double gainR, double gainG, double gainB)
    {
        double r = color.R, g = color.G, b = color.B;

        // Насыщенность через яркость (Rec.709)
        if (Math.Abs(saturation - 1.0) > 0.001)
        {
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            r = luma + (r - luma) * saturation;
            g = luma + (g - luma) * saturation;
            b = luma + (b - luma) * saturation;
        }

        r *= brightness * gainR;
        g *= brightness * gainG;
        b *= brightness * gainB;

        if (Math.Abs(gamma - 1.0) > 0.001)
        {
            r = 255.0 * Math.Pow(Math.Clamp(r / 255.0, 0, 1), gamma);
            g = 255.0 * Math.Pow(Math.Clamp(g / 255.0, 0, 1), gamma);
            b = 255.0 * Math.Pow(Math.Clamp(b / 255.0, 0, 1), gamma);
        }

        return new Rgb(
            (byte)Math.Clamp(Math.Round(r), 0, 255),
            (byte)Math.Clamp(Math.Round(g), 0, 255),
            (byte)Math.Clamp(Math.Round(b), 0, 255));
    }

    public static Color ToColor(Rgb c) => Color.FromArgb(c.R, c.G, c.B);

    /// <summary>Пресеты палитры для статичного цвета ленты.</summary>
    public static readonly (string Name, Rgb Color)[] Palette =
    {
        ("Красный", new Rgb(255, 48, 48)),
        ("Оранжевый", new Rgb(255, 128, 32)),
        ("Янтарный", new Rgb(255, 176, 48)),
        ("Жёлтый", new Rgb(255, 230, 64)),
        ("Лаймовый", new Rgb(160, 230, 64)),
        ("Зелёный", new Rgb(48, 210, 96)),
        ("Изумрудный", new Rgb(32, 208, 176)),
        ("Бирюзовый", new Rgb(32, 200, 220)),
        ("Голубой", new Rgb(64, 176, 255)),
        ("Синий", new Rgb(48, 96, 255)),
        ("Индиго", new Rgb(104, 80, 255)),
        ("Фиолетовый", new Rgb(168, 72, 255)),
        ("Пурпурный", new Rgb(230, 64, 220)),
        ("Розовый", new Rgb(255, 96, 168)),
        ("Тёплый белый", new Rgb(255, 214, 170)),
        ("Белый", new Rgb(255, 255, 255))
    };

    public static Rgb ParseHex(string? hex, Rgb fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string value = hex.Trim().TrimStart('#');
        if (value.Length == 3)
            value = string.Concat(value.Select(c => new string(c, 2)));
        if (value.Length != 6 || !int.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int rgb))
            return fallback;
        return new Rgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
    }

    public static string NormalizeHex(string? hex, string fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string value = hex.Trim().TrimStart('#');
        if (value.Length == 3)
            value = string.Concat(value.Select(c => new string(c, 2)));
        if (value.Length != 6 ||
            !int.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return fallback;
        return "#" + value.ToUpperInvariant();
    }

    public static string ToHex(Rgb c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Цветовой круг, разложенный на count светодиодов (для проверки раскладки).</summary>
    public static List<Rgb> Gradient(int count)
    {
        count = Math.Clamp(count, 1, 254);
        var result = new List<Rgb>(count);
        for (int i = 0; i < count; i++)
            result.Add(FromHsv(360.0 * i / count, 1.0, 1.0));
        return result;
    }

    /// <summary>Смешивает два цвета: t=0 → a, t=1 → b.</summary>
    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        return new Rgb(
            (byte)Math.Clamp(Math.Round(a.R + (b.R - a.R) * t), 0, 255),
            (byte)Math.Clamp(Math.Round(a.G + (b.G - a.G) * t), 0, 255),
            (byte)Math.Clamp(Math.Round(a.B + (b.B - a.B) * t), 0, 255));
    }
}
