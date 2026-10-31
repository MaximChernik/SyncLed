namespace SyncLed;

/// <summary>Накопительные счётчики обмена с контроллером (для диагностики).</summary>
public readonly record struct StripStats(long Sent, long Skipped, long Bytes, long Errors);

/// <summary>
/// Общий интерфейс ленты. Движок синхронизации и интерфейс не должны зависеть
/// от транспорта: сейчас это USB HID (RB/SC) и Bluetooth LE (FTY/CCHIP).
/// </summary>
public interface ILedStrip : IDisposable
{
    /// <summary>Идентификатор транспорта для журнала: USB или Bluetooth.</summary>
    string Transport { get; }

    string Path { get; }

    /// <summary>Стабильный (не зависящий от процесса) хеш пути устройства.</summary>
    string PathHash { get; }

    /// <summary>Стабильный ключ настройки: UUID/адрес, а если он не прочитан — модель + хеш пути.</summary>
    string Key { get; }

    /// <summary>Имя устройства для интерфейса.</summary>
    string Title { get; }

    /// <summary>Строка с техническими данными для карточки устройства.</summary>
    string Detail { get; }

    string Uuid { get; }
    string ProductId { get; }
    string Firmware { get; }
    string Manufacturer { get; }
    ushort Vid { get; }
    ushort Pid { get; }
    int LampCount { get; }

    bool IsOpen { get; }
    string? LastError { get; }

    bool Open();
    void Close();

    /// <summary>Цвета всех ламп ленты (для неадресуемых лент сводятся к одному цвету).</summary>
    void SendColors(IReadOnlyList<Rgb> colors);

    void SendStatic(Rgb color);
    void SendOff();

    /// <summary>Счётчики обмена с момента запуска приложения.</summary>
    StripStats GetStats();

    /// <summary>Обнуляет счётчики обмена — для замера за произвольный период.</summary>
    void ResetStats();
}
