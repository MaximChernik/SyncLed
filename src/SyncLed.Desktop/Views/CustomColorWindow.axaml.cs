using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace SyncLed.Avalonia.Views;

/// <summary>
/// Выбор произвольного цвета: слайдеры HSV (оттенок 0–360°, насыщенность и
/// яркость 0–100 %) и HEX-строка с живым предпросмотром. Синхронизация
/// двунаправленная и защищена флагом <c>_syncing</c>, чтобы события слайдеров
/// и текстового поля не зацикливались. Оттенок сохраняется для ахроматических
/// цветов (серый/чёрный/белый), чтобы ползунок H не прыгал в ноль.
/// «ОК» принимает цвет (<see cref="Accepted"/>/<see cref="SelectedHex"/>),
/// Esc и «Отмена» закрывают окно без изменений.
/// </summary>
public sealed partial class CustomColorWindow : Window
{
    private bool _syncing;
    private bool _closed;

    /// <summary>Текущий оттенок в градусах: удерживается для achromatic цветов.</summary>
    private double _hue;

    public CustomColorWindow() => InitializeComponent();

    public CustomColorWindow(string? initialHex) : this()
    {
        Rgb color = ColorUtil.ParseHex(initialHex, Rgb.White);
        (double h, double s, double v) = RgbToHsv(color);
        _hue = h;
        _syncing = true;
        try
        {
            HSlider.Value = h;
            SSlider.Value = s;
            VSlider.Value = v;
            HexBox.Text = ColorUtil.ToHex(color);
            ApplySliderTracks();
        }
        finally
        {
            _syncing = false;
        }
        UpdateChannelTexts(h, s, v);
        UpdatePreview(color);
    }

    /// <summary>Пользователь подтвердил выбор кнопкой «ОК» (или Enter).</summary>
    public bool Accepted { get; private set; }

    /// <summary>Выбранный цвет в формате #RRGGBB.</summary>
    public string SelectedHex { get; private set; } = "";

    private Rgb CurrentColor() => ColorUtil.FromHsv(
        HSlider.Value,
        Math.Clamp(SSlider.Value / 100.0, 0, 1),
        Math.Clamp(VSlider.Value / 100.0, 0, 1));

    /// <summary>Движение любого слайдера HSV: обновляем HEX и предпросмотр.</summary>
    private void OnChannelChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;

        if (ReferenceEquals(sender, HSlider)) _hue = HSlider.Value;
        double h = HSlider.Value;
        double s = SSlider.Value;
        double v = VSlider.Value;

        Rgb color = CurrentColor();
        UpdateChannelTexts(h, s, v);
        _syncing = true;
        try
        {
            HexBox.Text = ColorUtil.ToHex(color);
            ApplySliderTracks();
        }
        finally
        {
            _syncing = false;
        }
        UpdatePreview(color);
    }

    private void OnHexTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing) return;

        string hex = ColorUtil.NormalizeHex(HexBox.Text, "");
        if (hex.Length == 0) return; // ввод ещё не закончен — оставляем прежний цвет

        Rgb color = ColorUtil.ParseHex(hex, Rgb.White);
        (double h, double s, double v) = RgbToHsv(color);
        // Для серого/чёрного/белого оттенок в RGB не хранится — держим прежний.
        if (s > 0.0001 && v > 0.0001) _hue = h;
        else h = _hue;

        _syncing = true;
        try
        {
            HSlider.Value = h;
            SSlider.Value = s;
            VSlider.Value = v;
            ApplySliderTracks();
        }
        finally
        {
            _syncing = false;
        }
        UpdateChannelTexts(h, s, v);
        UpdatePreview(color);
    }

    /// <summary>Числовые подписи рядом со слайдерами (градусы и проценты).</summary>
    private void UpdateChannelTexts(double hue, double sat, double val)
    {
        if (HValue != null) HValue.Text = $"{Math.Round(hue):0}°";
        if (SValue != null) SValue.Text = $"{Math.Round(sat):0}%";
        if (VValue != null) VValue.Text = $"{Math.Round(val):0}%";
    }

    /// <summary>
    /// Заливки шкал: H — от красного к текущему оттенку, S — от серого к цвету,
    /// V — от чёрного к цвету (градиент всегда совпадает с заполненной частью шкалы).
    /// </summary>
    private void ApplySliderTracks()
    {
        double h = HSlider.Value;
        double s = SSlider.Value / 100.0;
        double v = VSlider.Value / 100.0;

        HSlider.Foreground = CreateTrackBrush(
            ToAvaloniaColor(ColorUtil.FromHsv(0, 1, 1)),
            ToAvaloniaColor(ColorUtil.FromHsv(h, 1, 1)));

        SSlider.Foreground = CreateTrackBrush(
            ToAvaloniaColor(ColorUtil.FromHsv(h, 0, v)),
            ToAvaloniaColor(ColorUtil.FromHsv(h, 1, v)));

        VSlider.Foreground = CreateTrackBrush(
            Colors.Black,
            ToAvaloniaColor(ColorUtil.FromHsv(h, s, 1)));
    }

    private static IBrush CreateTrackBrush(Color from, Color to) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(from, 0),
            new GradientStop(to, 1)
        }
    };

    /// <summary>RGB → HSV: H 0–360°, S и V 0–100 (слайдерные шкалы).</summary>
    private static (double H, double S, double V) RgbToHsv(Rgb color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue = 0;
        if (delta > 1e-9)
        {
            if (Math.Abs(max - r) < 1e-9) hue = 60.0 * ((g - b) / delta % 6.0);
            else if (Math.Abs(max - g) < 1e-9) hue = 60.0 * ((b - r) / delta + 2.0);
            else hue = 60.0 * ((r - g) / delta + 4.0);
            if (hue < 0) hue += 360.0;
        }

        double saturation = max <= 1e-9 ? 0 : delta / max;
        return (hue, saturation * 100.0, max * 100.0);
    }

    private static Color ToAvaloniaColor(Rgb color) => new(255, color.R, color.G, color.B);

    private void UpdatePreview(Rgb color)
    {
        string hex = ColorUtil.ToHex(color);
        Preview.Background = new SolidColorBrush(ToAvaloniaColor(color));

        // Тёмный или светлый текст — чтобы HEX читался на любом фоне.
        double luma = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
        PreviewHex.Foreground = new SolidColorBrush(luma > 140 ? Colors.Black : Colors.White);
        PreviewHex.Text = hex;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Finish(accepted: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(accepted: false);
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Finish(accepted: true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Finish(accepted: false);

    private void Finish(bool accepted)
    {
        if (_closed) return;
        _closed = true;

        if (accepted)
        {
            string hex = ColorUtil.NormalizeHex(HexBox.Text, "");
            SelectedHex = hex.Length > 0 ? hex : ColorUtil.ToHex(CurrentColor());
            Accepted = true;
        }
        else
        {
            Accepted = false;
        }
        Close();
    }
}
