namespace SyncLed.Avalonia.ViewModels;

/// <summary>
/// Всплывающее уведомление в правом нижнем углу главного окна.
/// <see cref="Tone"/> определяет цвет: info (по умолчанию), success, error.
/// </summary>
public sealed class ToastItem(string message, string tone = "info")
{
    public string Message { get; } = message;

    public string Tone { get; } = tone;

    public bool IsSuccess => string.Equals(Tone, "success", StringComparison.Ordinal);

    public bool IsError => string.Equals(Tone, "error", StringComparison.Ordinal);
}
