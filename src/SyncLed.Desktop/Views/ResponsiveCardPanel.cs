using Avalonia;
using Avalonia.Controls;

namespace SyncLed.Avalonia.Views;

/// <summary>
/// Сетка карточек без фиксированной ширины: подбирает максимальное число колонок,
/// при котором карточка остаётся не уже <see cref="MinCardWidth"/> пикселей.
/// Все карточки в каждом ряду имеют строго одинаковую ширину колонки; «висячая»
/// карточка в неполном последнем ряду сохраняет ширину колонки и остаётся
/// выровненной по левому краю, а не растягивается на всю строку. Исключение —
/// единственная карточка во всём списке: она занимает всю доступную ширину.
/// </summary>
public sealed class ResponsiveCardPanel : Panel
{
    public static readonly StyledProperty<double> MinCardWidthProperty =
        AvaloniaProperty.Register<ResponsiveCardPanel, double>(nameof(MinCardWidth), 424.0);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ResponsiveCardPanel, double>(nameof(ColumnSpacing), 18.0);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ResponsiveCardPanel, double>(nameof(RowSpacing), 18.0);

    private double _measuredWidth;

    /// <summary>Число колонок, посчитанное при последнем измерении/раскладке.</summary>
    public int LastColumns { get; private set; } = 1;

    /// <summary>Округлённая ширина карточки колонки при последней раскладке.</summary>
    public double LastCardWidth { get; private set; } = 1;

    static ResponsiveCardPanel()
    {
        AffectsMeasure<ResponsiveCardPanel>(MinCardWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
        AffectsArrange<ResponsiveCardPanel>(MinCardWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    /// <summary>Минимальная ширина карточки (по умолчанию 424 px).</summary>
    public double MinCardWidth
    {
        get => GetValue(MinCardWidthProperty);
        set => SetValue(MinCardWidthProperty, value);
    }

    /// <summary>Горизонтальный зазор между карточками.</summary>
    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>Вертикальный зазор между рядами.</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width)
            : Math.Max(1, MinCardWidth);

        int columns = ComputeColumns(width);
        double cellWidth = ComputeCellWidth(width, columns, Children.Count);
        // Измеряем по той же округлённой ширине, по которой раскладываем:
        // перенос текста и высоты рядов тогда совпадают в Measure и Arrange.
        double cardWidth = Math.Max(1, Math.Round(cellWidth));
        LastColumns = columns;
        LastCardWidth = cardWidth;
        _measuredWidth = width;

        double totalHeight = 0;
        double rowHeight = 0;
        int inRow = 0;

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(cardWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            inRow++;

            if (inRow == columns || i == Children.Count - 1)
            {
                totalHeight += rowHeight + RowSpacing;
                rowHeight = 0;
                inRow = 0;
            }
        }

        if (Children.Count > 0) totalHeight -= RowSpacing;
        return new Size(width, Math.Max(0, totalHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = double.IsFinite(finalSize.Width) && finalSize.Width > 0
            ? finalSize.Width
            : _measuredWidth;

        int columns = ComputeColumns(width);
        double cellWidth = ComputeCellWidth(width, columns, Children.Count);
        // Единая ширина карточки для всех рядов: один округлённый размер,
        // чтобы колонки рядов стояли ровно друг под другом.
        double cardWidth = Math.Max(1, Math.Round(cellWidth));
        LastColumns = columns;
        LastCardWidth = cardWidth;

        double y = 0;
        int i = 0;
        while (i < Children.Count)
        {
            int rowEnd = Math.Min(i + columns, Children.Count);
            double rowHeight = 0;
            for (int j = i; j < rowEnd; j++)
                rowHeight = Math.Max(rowHeight, Children[j].DesiredSize.Height);

            for (int j = i; j < rowEnd; j++)
            {
                // Карточка последнего неполного ряда сохраняет ширину колонки.
                int index = j - i;
                double x = Math.Round(index * (cardWidth + ColumnSpacing));
                Children[j].Arrange(new Rect(x, y, cardWidth, rowHeight));
            }

            y += rowHeight + RowSpacing;
            i = rowEnd;
        }

        double height = Children.Count > 0 ? Math.Max(0, y - RowSpacing) : Math.Max(0, finalSize.Height);
        return new Size(width, height);
    }

    // ── геометрия слотов: нужна для перетаскивания карточек ────────────────

    /// <summary>Естественный (неподвинутый) прямоугольник слота карточки.</summary>
    public Rect GetSlotBounds(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, Children.Count - 1));
        int columns = Math.Max(1, LastColumns);
        int col = index % columns;
        int row = index / columns;

        double y = 0;
        for (int r = 0; r < row; r++)
            y += RowHeightAt(r) + RowSpacing;

        double x = Math.Round(col * (LastCardWidth + ColumnSpacing));
        return new Rect(x, y, LastCardWidth, RowHeightAt(row));
    }

    /// <summary>
    /// Индекс вставки для точки в координатах панели: позиция, на которую
    /// встанет карточка при перетаскивании (0…Children.Count).
    /// </summary>
    public int GetInsertionIndex(Point position)
    {
        int count = Children.Count;
        if (count == 0) return 0;

        int columns = Math.Max(1, LastColumns);
        int rows = (count + columns - 1) / columns;

        int row = rows - 1;
        for (int r = 0; r < rows; r++)
        {
            var band = GetSlotBounds(r * columns);
            if (position.Y < band.Bottom + RowSpacing * 0.5)
            {
                row = r;
                break;
            }
        }

        int rowStart = row * columns;
        int rowLength = Math.Min(columns, count - rowStart);

        int col;
        if (columns == 1)
        {
            col = 0;
        }
        else
        {
            double pitch = Math.Max(1, LastCardWidth + ColumnSpacing);
            col = (int)Math.Floor((position.X + ColumnSpacing * 0.5) / pitch);
            col = Math.Clamp(col, 0, rowLength);
        }

        return Math.Min(rowStart + col, count);
    }

    /// <summary>Высота ряда карточек (максимум DesiredSize детей ряда).</summary>
    private double RowHeightAt(int row)
    {
        int columns = Math.Max(1, LastColumns);
        int start = row * columns;
        int end = Math.Min(start + columns, Children.Count);
        double height = 0;
        for (int j = start; j < end; j++)
            height = Math.Max(height, Children[j].DesiredSize.Height);
        return height;
    }

    /// <summary>Максимум колонок, при котором карточка не уже <see cref="MinCardWidth"/>.</summary>
    private int ComputeColumns(double width)
    {
        double min = Math.Max(1, MinCardWidth);
        double spacing = Math.Max(0, ColumnSpacing);
        int columns = (int)((width + spacing) / (min + spacing));
        return Math.Max(1, columns);
    }

    /// <summary>
    /// Ширина колонки карточки: остаток ширины делится поровну между колонками.
    /// Единственная карточка во всём списке занимает всю доступную ширину.
    /// </summary>
    private double ComputeCellWidth(double width, int columns, int childCount)
    {
        if (childCount <= 1) return Math.Max(1, width);
        double spacing = Math.Max(0, ColumnSpacing);
        double inner = width - spacing * (Math.Max(1, columns) - 1);
        return Math.Max(1, inner / Math.Max(1, columns));
    }
}
