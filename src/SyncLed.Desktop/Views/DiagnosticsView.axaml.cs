using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SyncLed.Avalonia.ViewModels;

namespace SyncLed.Avalonia.Views;

/// <summary>
/// Цвет статуса ленты в таблице диагностики: зелёный для «Подключено»,
/// красный для отсутствия связи и ошибок, янтарный для теста и паузы.
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#43D3A4"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#FF7082"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F6BC60"));
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#C7CFDE"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string status || status.Length == 0) return Neutral;
        string lower = status.ToLowerInvariant();

        if (lower.Contains("подключ", StringComparison.Ordinal)) return Good;
        if (lower.Contains("нет связ", StringComparison.Ordinal) ||
            lower.Contains("ошиб", StringComparison.Ordinal) ||
            lower.Contains("недоступ", StringComparison.Ordinal)) return Bad;
        if (lower.Contains("тест", StringComparison.Ordinal) ||
            lower.Contains("пауз", StringComparison.Ordinal)) return Warn;
        return Neutral;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Цвет счётчика ошибок: красный при ненулевом значении, приглушённый для нуля.</summary>
public sealed class ErrorsToBrushConverter : IValueConverter
{
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#FF7082"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8490A4"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool nonzero = value is int count
            ? count != 0
            : int.TryParse(value as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed != 0;
        return nonzero ? Bad : Muted;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Сводка движка, таблица лент и копирование отчёта в буфер обмена.</summary>
public sealed partial class DiagnosticsView : UserControl
{
    private MainWindowViewModel? _viewModel;

    public DiagnosticsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel != null) _viewModel.DiagnosticRows.CollectionChanged -= OnRowsChanged;
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null) _viewModel.DiagnosticRows.CollectionChanged += OnRowsChanged;
        ApplyStripes();
    }

    /// <summary>Перестройка строк: мягкая чередующаяся подсветка чётных строк.</summary>
    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(ApplyStripes, DispatcherPriority.Loaded);

    private void ApplyStripes()
    {
        var table = DiagnosticTable;
        if (table == null) return;

        int count = _viewModel?.DiagnosticRows.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            // Контейнер ItemsControl — ContentPresenter; класс alt включает фон строки.
            if (table.ContainerFromIndex(i) is Control container)
                container.Classes.Set("alt", i % 2 == 1);
        }
    }

    private async void OnCopyReportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        var report = new StringBuilder();
        report.AppendLine(viewModel.DiagnosticsSummary);
        report.AppendLine();
        report.AppendLine("Лента | Транспорт | Статус | Монитор | Режим | Отправлено | Пропущено | Трафик | Ошибки");
        foreach (var row in viewModel.DiagnosticRows)
        {
            report.Append(string.Join(" | ",
                row.Name, row.Transport, row.Status, row.Monitor, row.Mode,
                row.Sent, row.Skipped, row.Traffic, row.Errors));
            report.AppendLine();
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        try
        {
            await clipboard.SetTextAsync(report.ToString());
        }
        catch
        {
            // буфер обмена может быть недоступен — не мешаем работе приложения
        }
    }
}
