using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SyncLed.Avalonia.ViewModels;

namespace SyncLed.Avalonia.Views;

/// <summary>Карточка одной ленты: имя, миниатюра, режим, группа, цвет и тесты.</summary>
public sealed partial class DeviceCardView : UserControl
{
    private DeviceViewModel? _device;
    private bool _syncingBoxes;
    private bool _renaming;

    /// <summary>Максимум «LED по горизонтали» — как в ядре (0…126, 0 — авто).</summary>
    private const double HorizontalMax = 126;

    /// <summary>Порог в пикселях, после которого нажатие на ручку становится перетаскиванием.</summary>
    private const double DragThreshold = 4;

    /// <summary>Зона прокрутки у краёв списка во время перетаскивания.</summary>
    private const double AutoScrollZone = 40;

    /// <summary>Активное перетаскивание карточки (null — перетаскивания нет).</summary>
    private DragSession? _drag;

    public DeviceCardView()
    {
        InitializeComponent();

        // Поля и ползунки пишут в вьюмодель из код-бихайнда; PullFromDevice
        // возвращает значения обратно с защитой от петли (_syncingBoxes).
        ShiftSlider.ValueChanged += (_, _) =>
        {
            ShiftValueText.Text = BoxValue(ShiftSlider).ToString();
            WriteIfReady(vm => vm.Shift = BoxValue(ShiftSlider));
        };
        HorizontalSlider.ValueChanged += (_, _) =>
        {
            HorizontalValueText.Text = BoxValue(HorizontalSlider).ToString();
            WriteIfReady(vm => vm.Horizontal = BoxValue(HorizontalSlider));
        };
        GainRBox.ValueChanged += (_, _) =>
            WriteIfReady(vm => vm.GainR = BoxValue(GainRBox));
        GainGBox.ValueChanged += (_, _) =>
            WriteIfReady(vm => vm.GainG = BoxValue(GainGBox));
        GainBBox.ValueChanged += (_, _) =>
            WriteIfReady(vm => vm.GainB = BoxValue(GainBBox));

        // Перетаскивание за ручку ⠿: карточка следует за курсором, остальные
        // показывают разрыв в точке вставки, при отпускании порядок сохраняется.
        // Обработчики — на самой ручке: она захватывает указатель, поэтому
        // DataContext карточки ни разу не меняется и раскладка не пересоздаётся.
        DragHandle.AddHandler(InputElement.PointerPressedEvent, OnDragHandlePressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        DragHandle.PointerMoved += OnDragHandlePointerMoved;
        // Кнопка помечает PointerReleased как обработанный, поднимаем флаг.
        DragHandle.AddHandler(InputElement.PointerReleasedEvent, OnDragHandlePointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
        DragHandle.AddHandler(InputElement.PointerCaptureLostEvent, OnDragHandleCaptureLost,
            RoutingStrategies.Bubble, handledEventsToo: true);
        DragHandle.KeyDown += OnDragHandleKeyDown;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_device != null) _device.PropertyChanged -= OnDevicePropertyChanged;
        _device = DataContext as DeviceViewModel;
        if (_device != null) _device.PropertyChanged += OnDevicePropertyChanged;

        CancelRename();
        PullFromDevice();
        BuildPalette();
        BuildSavedColors();
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DeviceViewModel.Shift):
            case nameof(DeviceViewModel.Horizontal):
            case nameof(DeviceViewModel.GainR):
            case nameof(DeviceViewModel.GainG):
            case nameof(DeviceViewModel.GainB):
                // Вьюмодель ограничивает значения диапазонами ленты — возвращаем их в поля.
                PullFromDevice();
                break;
            case nameof(DeviceViewModel.SelectedColorHex):
                UpdatePaletteSelection();
                break;
            case nameof(DeviceViewModel.SavedColors):
                BuildSavedColors();
                break;
            case nameof(DeviceViewModel.Title):
                if (_renaming) CancelRename();
                break;
        }
    }

    /// <summary>Переносит значения из вьюмодели в числовые поля и ползунки (с защитой от петли).</summary>
    private void PullFromDevice()
    {
        var device = _device;
        if (device == null) return;

        _syncingBoxes = true;
        try
        {
            // Диапазон сдвига зависит от длины ленты; ползунки — с шагом 1 LED.
            ShiftSlider.Maximum = Math.Max(1, device.Device.LampCount - 1);
            ShiftSlider.Value = Math.Clamp(device.Shift, 0, (int)ShiftSlider.Maximum);
            ShiftValueText.Text = ((int)ShiftSlider.Value).ToString();

            HorizontalSlider.Maximum = HorizontalMax;
            HorizontalSlider.Value = Math.Clamp(device.Horizontal, 0, (int)HorizontalMax);
            HorizontalValueText.Text = ((int)HorizontalSlider.Value).ToString();

            GainRBox.Value = device.GainR;
            GainGBox.Value = device.GainG;
            GainBBox.Value = device.GainB;
        }
        finally
        {
            _syncingBoxes = false;
        }
    }

    private void WriteIfReady(Action<DeviceViewModel> write)
    {
        if (_syncingBoxes || _device == null) return;
        write(_device);
    }

    private static int BoxValue(Slider slider) => (int)Math.Round(slider.Value);

    private static int BoxValue(NumericUpDown box) => (int)(box.Value ?? 0);

    // ── переименование в шапке ─────────────────────────────────────────────

    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_device == null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new DeviceSettingsWindow(_device.Device, _device.Config);
        window.Closed += (_, _) => _device.NotifySettingsChanged();
        window.Show(owner);
    }

    private void OnRenameClick(object? sender, RoutedEventArgs e) => BeginRename();

    private void OnTitleTapped(object? sender, TappedEventArgs e) => BeginRename();

    private void BeginRename()
    {
        if (_device == null || _renaming) return;

        _renaming = true;
        string current = _device.Device.Name;
        RenameBox.Text = string.IsNullOrWhiteSpace(current) ? _device.Title : current;

        TitleText.IsVisible = false;
        RenameButton.IsVisible = false;
        RenameBox.IsVisible = true;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    private void CommitRename()
    {
        if (!_renaming) return;
        string text = RenameBox.Text ?? "";
        EndRename();
        _device?.Rename(text);
    }

    private void CancelRename()
    {
        if (!_renaming) return;
        EndRename();
    }

    private void EndRename()
    {
        _renaming = false;
        RenameBox.IsVisible = false;
        TitleText.IsVisible = true;
        RenameButton.IsVisible = true;
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e) => CommitRename();

    // ── палитра ─────────────────────────────────────────────────────────────

    private void BuildPalette()
    {
        PaletteHost.Children.Clear();
        var device = _device;
        if (device == null) return;

        foreach (var choice in device.Palette)
        {
            string hex = choice.Hex;
            var swatch = CreateSwatch(hex, choice.Name);
            swatch.Click += (_, _) =>
            {
                if (_device != null) _device.SelectedColorHex = hex;
            };
            PaletteHost.Children.Add(swatch);
        }

        UpdatePaletteSelection();
    }

    /// <summary>Перестраивает ряд «Мои цвета» — общий для всех лент.</summary>
    private void BuildSavedColors()
    {
        SavedColorHost.Children.Clear();
        var device = _device;

        if (device != null)
        {
            foreach (string hex in device.SavedColors)
            {
                string saved = hex;
                var swatch = CreateSwatch(saved, saved + "  ·  ПКМ — удалить");
                swatch.Click += (_, _) => _device?.SelectSavedColor(saved);

                // ПКМ — удалить цвет из «Моих цветов»; handledEventsToo, потому что
                // кнопка помечает PointerReleased как обработанный.
                swatch.AddHandler(PointerReleasedEvent,
                    (_, args) =>
                    {
                        if (args.InitialPressMouseButton != MouseButton.Right) return;
                        args.Handled = true;
                        _device?.RemoveSavedColor(saved);
                    },
                    RoutingStrategies.Bubble,
                    handledEventsToo: true);

                SavedColorHost.Children.Add(swatch);
            }
        }

        bool hasSaved = SavedColorHost.Children.Count > 0;
        SavedHeader.IsVisible = hasSaved;
        SavedHint.IsVisible = !hasSaved;
        UpdatePaletteSelection();
    }

    private static Button CreateSwatch(string hex, string tip)
    {
        var swatch = new Button
        {
            Content = " ",
            Background = ParseBrush(hex),
            Tag = hex
        };
        swatch.Classes.Add("swatch");
        ToolTip.SetTip(swatch, tip);
        return swatch;
    }

    /// <summary>Отмечает выбранный образец кольцом — и в палитре, и в «Моих цветах».</summary>
    private void UpdatePaletteSelection()
    {
        string? selected = _device?.SelectedColorHex;
        MarkSelection(PaletteHost, selected);
        MarkSelection(SavedColorHost, selected);
    }

    private static void MarkSelection(WrapPanel host, string? selected)
    {
        foreach (var child in host.Children)
        {
            if (child is not Button button || button.Tag is not string hex) continue;

            bool isSelected = !string.IsNullOrEmpty(selected) &&
                string.Equals(selected, hex, StringComparison.OrdinalIgnoreCase);

            if (isSelected)
            {
                if (!button.Classes.Contains("selected")) button.Classes.Add("selected");
            }
            else
            {
                button.Classes.Remove("selected");
            }
        }
    }

    private static SolidColorBrush ParseBrush(string hex) =>
        new(Color.Parse(hex));

    // ── свой цвет и «Мои цвета» ─────────────────────────────────────────────

    private void OnSaveColorClick(object? sender, RoutedEventArgs e) => _device?.SaveCurrentColor();

    private async void OnPickColorClick(object? sender, RoutedEventArgs e)
    {
        var device = _device;
        if (device == null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new CustomColorWindow(device.CurrentColorHex);
        await dialog.ShowDialog(owner);
        if (dialog.Accepted) device.ApplyColor(dialog.SelectedHex ?? "");
    }

    // ── тесты ленты ─────────────────────────────────────────────────────────

    private void OnTestClick(object? sender, RoutedEventArgs e)
    {
        if (_device == null || sender is not Control control || control.Tag is not string kind)
            return;
        _device.RunTest(kind);
    }

    // ── перетаскивание карточки за ручку ⠿ ─────────────────────────────────

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_renaming || _drag != null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var itemsControl = this.FindAncestorOfType<ItemsControl>();
        if (itemsControl?.ItemsPanelRoot is not ResponsiveCardPanel panel) return;

        var container = this.FindAncestorOfType<ContentPresenter>();
        if (container == null) return;
        int index = panel.Children.IndexOf(container);
        if (index < 0) return;

        Point pointer = e.GetPosition(panel);
        _drag = new DragSession
        {
            Panel = panel,
            Scroller = this.FindAncestorOfType<ScrollViewer>(),
            Container = container,
            SourceIndex = index,
            PointerStart = pointer,
            GrabOffset = pointer - container.Bounds.Position,
            TargetIndex = index,
            OriginalZIndex = container.GetValue(Panel.ZIndexProperty),
            OriginalTransform = container.RenderTransform
        };

        e.Pointer.Capture(DragHandle);
        e.Handled = true;
    }

    private void OnDragHandlePointerMoved(object? sender, PointerEventArgs e)
    {
        var drag = _drag;
        if (drag == null) return;

        Point pointer = e.GetPosition(drag.Panel);

        if (drag.Pending)
        {
            double dx = pointer.X - drag.PointerStart.X;
            double dy = pointer.Y - drag.PointerStart.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < DragThreshold) return;
            drag.Pending = false;
        }

        if (drag.Scroller != null)
            AutoScrollForDrag(drag, e.GetPosition(drag.Scroller));

        int target = drag.Panel.GetInsertionIndex(pointer);
        if (target != drag.TargetIndex)
        {
            drag.TargetIndex = target;
            ApplyGapShift(drag, target);
        }

        // Перетащенная карточка следует за курсором с сохранением точки захвата
        // и «приподнимается» над остальными (ZIndex + тень в Theme.axaml).
        Point origin = pointer - drag.GrabOffset;
        Rect slot = drag.Panel.GetSlotBounds(drag.SourceIndex);
        drag.Container.RenderTransform = new TranslateTransform(
            origin.X - slot.X, origin.Y - slot.Y);
    }

    private void OnDragHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var drag = _drag;
        if (drag == null) return;

        _drag = null;
        e.Pointer.Capture(null);

        bool moved = !drag.Pending && drag.TargetIndex != drag.SourceIndex;
        ResetDragVisuals(drag);

        if (!moved) return;

        int to = drag.TargetIndex > drag.SourceIndex
            ? drag.TargetIndex - 1
            : drag.TargetIndex;
        if (to == drag.SourceIndex) return;

        if (this.FindAncestorOfType<ItemsControl>()?.DataContext is MainWindowViewModel viewModel)
            viewModel.MoveDevice(drag.SourceIndex, to);
    }

    private void OnDragHandleCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Потеря захвата (Esc, свернутое окно): отменяем без изменения порядка.
        var drag = _drag;
        if (drag == null) return;
        _drag = null;
        ResetDragVisuals(drag);
    }

    private void OnDragHandleKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc отменяет перетаскивание; порядок не меняется, захват отпустится
        // при отпускании кнопки мыши (_drag уже сброшен — хуки ничего не сделают).
        if (e.Key != Key.Escape) return;
        var drag = _drag;
        if (drag == null) return;
        _drag = null;
        ResetDragVisuals(drag);
        e.Handled = true;
    }

    /// <summary>Подсвечивает разрыв: соседние карточки сдвигаются на слот в сторону цели.</summary>
    private void ApplyGapShift(DragSession drag, int targetIndex)
    {
        var panel = drag.Panel;
        int columns = Math.Max(1, panel.LastColumns);
        int from = drag.SourceIndex;
        int to = targetIndex > from ? targetIndex - 1 : targetIndex;

        for (int j = 0; j < panel.Children.Count; j++)
        {
            if (j == from) continue;
            var child = panel.Children[j];

            // Куда встанет карточка после удаления источника и вставки в to.
            int shifted = j;
            if (j > from) shifted--;
            if (shifted >= to) shifted++;

            if (shifted == j)
            {
                child.RenderTransform = null;
                continue;
            }

            int oldCol = j % columns;
            int newCol = shifted % columns;

            Rect oldSlot = panel.GetSlotBounds(j);
            Rect newSlot = panel.GetSlotBounds(
                Math.Min(shifted, panel.Children.Count - 1));
            double dx = (newCol - oldCol) * (panel.LastCardWidth + panel.ColumnSpacing);
            double dy = newSlot.Y - oldSlot.Y;

            if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
                child.RenderTransform = null;
            else
                child.RenderTransform = new TranslateTransform(dx, dy);
        }

        // «Поднятая» карточка — поверх остальных.
        drag.Container.SetValue(Panel.ZIndexProperty, 1000);
        CardBorder.Classes.Add("dragging");
    }

    /// <summary>Автопрокрутка списка у краёв viewport во время перетаскивания.</summary>
    private static void AutoScrollForDrag(DragSession drag, Point pointerInScroller)
    {
        var scroller = drag.Scroller;
        if (scroller == null || scroller.Viewport.Height < 1) return;

        double step = 14;
        double offset = scroller.Offset.Y;
        if (pointerInScroller.Y < AutoScrollZone)
            offset -= step;
        else if (pointerInScroller.Y > scroller.Viewport.Height - AutoScrollZone)
            offset += step;
        else
            return;

        double max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(offset, 0, max));
    }

    /// <summary>Снимает визуальные эффекты перетаскивания, порядок уже применён (или отменён).</summary>
    private void ResetDragVisuals(DragSession drag)
    {
        foreach (var child in drag.Panel.Children)
        {
            child.RenderTransform = null;
            child.SetValue(Panel.ZIndexProperty, 0);
        }
        drag.Container.SetValue(Panel.ZIndexProperty, drag.OriginalZIndex);
        drag.Container.RenderTransform = drag.OriginalTransform;
        CardBorder.Classes.Remove("dragging");
    }

    /// <summary>Состояние одного перетаскивания карточки.</summary>
    private sealed class DragSession
    {
        public required ResponsiveCardPanel Panel { get; init; }
        public ScrollViewer? Scroller { get; init; }
        public required ContentPresenter Container { get; init; }
        public int SourceIndex { get; init; }
        public int TargetIndex { get; set; }
        public Point PointerStart { get; init; }
        public Vector GrabOffset { get; init; }
        public int OriginalZIndex { get; init; }
        public ITransform? OriginalTransform { get; init; }
        public bool Pending { get; set; } = true;
    }
}
