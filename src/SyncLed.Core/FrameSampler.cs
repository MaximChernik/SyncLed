using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SyncLed;

/// <summary>Быстрый захват области экрана через GDI StretchBlt сразу в уменьшенный кадр.</summary>
public sealed class FrameSampler : IDisposable
{
    private const int SRCCOPY = 0x00CC0020;
    // COLORONCOLOR: быстрый режим StretchBlt для захвата двух мониторов.
    // HALFTONE даёт чуть более гладкую интерполяцию, но стоит десятки мс на кадр.
    private const int COLORONCOLOR = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, out POINT previous);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private readonly byte[] _pixels;
    private readonly int _stride;

    public int Width { get; }
    public int Height { get; }

    public FrameSampler(int width, int height)
    {
        Width = Math.Max(32, width);
        Height = Math.Max(18, height);
        _bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
        _pixels = new byte[Width * Height * 4];
        _stride = Width * 4;
    }

    /// <summary>Захватывает прямоугольник экрана. false — если захват не удался.</summary>
    public bool Capture(Rectangle source)
    {
        if (source.Width <= 0 || source.Height <= 0) return false;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        IntPtr destDc = _graphics.GetHdc();
        try
        {
            SetStretchBltMode(destDc, COLORONCOLOR);
            SetBrushOrgEx(destDc, 0, 0, out _);
            return StretchBlt(destDc, 0, 0, Width, Height, screenDc,
                source.X, source.Y, source.Width, source.Height, SRCCOPY);
        }
        catch
        {
            return false;
        }
        finally
        {
            _graphics.ReleaseHdc(destDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Копирует захваченный кадр в буфер BGRA (после успешного Capture).</summary>
    public byte[] ReadPixels()
    {
        BitmapData data = _bitmap.LockBits(new Rectangle(0, 0, Width, Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int absStride = Math.Abs(data.Stride);
            if (absStride == _stride)
            {
                Marshal.Copy(data.Scan0, _pixels, 0, _pixels.Length);
            }
            else
            {
                for (int y = 0; y < Height; y++)
                {
                    IntPtr row = data.Stride >= 0
                        ? IntPtr.Add(data.Scan0, y * data.Stride)
                        : IntPtr.Add(data.Scan0, (Height - 1 - y) * absStride);
                    Marshal.Copy(row, _pixels, y * Width * 4, Width * 4);
                }
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
        return _pixels;
    }

    /// <summary>
    /// Медиана по каждому каналу вместо среднего арифметического. Мелкий
    /// яркий элемент (курсор, иконка, уведомление, бегущая строка), занимающий
    /// меньше половины зоны, не сдвигает её цвет — лента не мерцает от мелочей.
    /// Полноценная смена картинки проходит как есть: медиана следует за
    /// большинством пикселей зоны.
    /// </summary>
    public Rgb Median(byte[] pixels, Rectangle rect)
    {
        int x0 = Math.Clamp(rect.Left, 0, Width);
        int y0 = Math.Clamp(rect.Top, 0, Height);
        int x1 = Math.Clamp(rect.Right, x0 + 1, Width);
        int y1 = Math.Clamp(rect.Bottom, y0 + 1, Height);

        int count = (x1 - x0) * (y1 - y0);
        if (count <= 0) return Rgb.Black;

        // Обычные зоны — сотни пикселей, укладываются в стек; большие
        // (при малом числе LED) уходят в кучу, чтобы не резать стек потока.
        Span<byte> rs = count <= 4096 ? stackalloc byte[count] : new byte[count];
        Span<byte> gs = count <= 4096 ? stackalloc byte[count] : new byte[count];
        Span<byte> bs = count <= 4096 ? stackalloc byte[count] : new byte[count];

        int i = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * _stride;
            for (int x = x0; x < x1; x++)
            {
                int o = row + x * 4;
                bs[i] = pixels[o];
                gs[i] = pixels[o + 1];
                rs[i] = pixels[o + 2];
                i++;
            }
        }

        rs.Sort();
        gs.Sort();
        bs.Sort();
        return new Rgb(Middle(rs), Middle(gs), Middle(bs));
    }

    private static byte Middle(Span<byte> sorted)
    {
        int n = sorted.Length;
        return n % 2 == 1
            ? sorted[n / 2]
            : (byte)((sorted[n / 2 - 1] + sorted[n / 2]) / 2);
    }

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}
