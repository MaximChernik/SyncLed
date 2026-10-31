using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SyncLed;

namespace SyncLed.IconGen;

/// <summary>
/// Пересобирает assets/app.ico тем же рисунком, что и знак в интерфейсе.
/// Иконка больше не поддерживается вручную в редакторе: после правки AppIconArt
/// достаточно запустить этот проект (или build.bat), и app.ico обновится.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? previewDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--preview" || args[i] == "-p") && i + 1 < args.Length)
                previewDir = args[++i];
        }

        string root = FindRoot();
        string target = Path.Combine(root, "assets", "app.ico");

        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var entries = new (int Size, byte[] Data)[sizes.Length];
        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            using var bmp = Render(size, 0f, false);
            entries[i] = (size, size >= 64 ? Png(bmp) : Dib(bmp));
        }

        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)entries.Length);

            int offset = 6 + 16 * entries.Length;
            foreach (var (size, data) in entries)
            {
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(data.Length);
                writer.Write(offset);
                offset += data.Length;
            }

            foreach (var (_, data) in entries) writer.Write(data);
            File.WriteAllBytes(target, ms.ToArray());
            Console.WriteLine($"app.ico: {ms.Length} байт, размеров: {entries.Length}");
        }

        if (previewDir != null)
        {
            Directory.CreateDirectory(previewDir);
            for (int i = 0; i < sizes.Length; i++)
            {
                using var bmp = Render(sizes[i], 0f, false);
                bmp.Save(Path.Combine(previewDir, $"icon_{sizes[i]}.png"), ImageFormat.Png);
            }
            using (var logo = Render(42, 0f, false))
                logo.Save(Path.Combine(previewDir, "logo_42_normal.png"), ImageFormat.Png);
            using (var logo = Render(42, 1f, false))
                logo.Save(Path.Combine(previewDir, "logo_42_hover.png"), ImageFormat.Png);
            using (var logo = Render(42, 0f, true))
                logo.Save(Path.Combine(previewDir, "logo_42_active.png"), ImageFormat.Png);
            Console.WriteLine($"Превью: {previewDir}");
        }

        using var icon = new Icon(target);
        Console.WriteLine($"Проверка: {icon.Width}x{icon.Height}");
        return 0;
    }

    /// <summary>Корень репозитория — папка, где лежит Avalonia-проект.</summary>
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "SyncLed.Desktop", "SyncLed.Desktop.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static Bitmap Render(int size, float glow, bool active)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        AppIconArt.Draw(g, new RectangleF(0, 0, size, size), glow, active);
        return bmp;
    }

    private static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>32-битный DIB для маленьких размеров: его понимают старые оболочки Windows.</summary>
    private static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Marshal.Copy(data.Scan0 + (h - 1 - y) * data.Stride, pixels, y * w * 4, w * 4);
        bmp.UnlockBits(data);

        int maskStride = (w + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(40);
        writer.Write(w);
        writer.Write(h * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixels.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(pixels);
        writer.Write(new byte[maskStride * h]);
        return ms.ToArray();
    }
}
