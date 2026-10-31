using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using SyncLed.Avalonia.ViewModels;

namespace SyncLed.Avalonia.Views;

/// <summary>Окрашенная строка журнала: текст и семантический цвет.</summary>
public sealed record LogLine(string Text, IBrush Brush);

/// <summary>
/// Разбирает текст журнала на строки и подбирает каждому сообщению цвет по тем же
/// словам, что и в классической версии: ошибки — красные, успешные события —
/// зелёные, предупреждения — янтарные, всё прочее — нейтральное.
/// </summary>
public sealed class LogLinesConverter : IValueConverter
{
    // Кисти кешируются: строки пересобираются при каждом сообщении журнала.
    // Нейтральный тон классического журнала: 180, 190, 206.
    private static readonly IBrush Neutral = new SolidColorBrush(Color.FromRgb(180, 190, 206));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#FF7082"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#43D3A4"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F6BC60"));

    // Списки слов для цветовой классификации сообщений журнала.
    private static readonly string[] BadWords =
    {
        "ошиб", "не удалось", "не найден", "нет связи", "недоступ", "занят",
        "не подтверд", "не отвечает", "исключен", "провал", "сбой", "отключ"
    };

    private static readonly string[] GoodWords =
    {
        "подключен", "запущен", "найдено устройств", "сохран", "назначен",
        "установлен", "порядок устройств", "готово", "включен"
    };

    private static readonly string[] WarnWords =
    {
        "поиск", "ожидан", "тест", "проверь", "закройте", "предупрежд",
        "повтор", "обновлен", "яркост"
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || text.Length == 0)
            return Array.Empty<LogLine>();

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n', StringSplitOptions.None);
        var result = new List<LogLine>(lines.Length);
        foreach (string line in lines)
            result.Add(new LogLine(line, ToneFor(line)));
        return result;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Цветовой акцент строки журнала по её смыслу.</summary>
    private static IBrush ToneFor(string line)
    {
        string lower = line.ToLowerInvariant();
        foreach (string word in BadWords)
            if (lower.Contains(word, StringComparison.Ordinal)) return Bad;
        foreach (string word in GoodWords)
            if (lower.Contains(word, StringComparison.Ordinal)) return Good;
        foreach (string word in WarnWords)
            if (lower.Contains(word, StringComparison.Ordinal)) return Warn;
        return Neutral;
    }
}

/// <summary>Журнал событий: моноширинные строки с цветовыми акцентами и автопрокруткой.</summary>
public sealed partial class LogView : UserControl
{
    private MainWindowViewModel? _viewModel;

    public LogView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshEmptyState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.LogText)) return;
        RefreshEmptyState();
        ScrollToEnd();
    }

    /// <summary>Подсказка «Журнал пуст» видна, только пока не было ни одного сообщения.</summary>
    private void RefreshEmptyState()
    {
        if (EmptyHint != null)
            EmptyHint.IsVisible = string.IsNullOrWhiteSpace(_viewModel?.LogText);
    }

    /// <summary>Новые строки добавляются в конец — журнал придерживается низа.</summary>
    private void ScrollToEnd()
    {
        // Прокрутка откладывается: строки ItemsControl обновляются привязкой
        // одновременно с LogText, и пролистать нужно после пересчёта layout'а.
        Dispatcher.UIThread.Post(() => LogScroller?.ScrollToEnd(), DispatcherPriority.Background);
    }
}
