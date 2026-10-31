using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SyncLed;

/// <summary>Настройки одной ленты. Часть параметров может наследоваться от группы или общих настроек.</summary>
public sealed class DeviceConfig
{
    public string Uuid { get; set; } = "";
    public string ProductId { get; set; } = "000000";
    public int LampCount { get; set; } = 96;
    public bool Enabled { get; set; } = true;

    /// <summary>Пользовательское имя устройства; пусто — стандартное название ленты.</summary>
    public string Name { get; set; } = "";

    public string MonitorDeviceName { get; set; } = "";

    /// <summary>Группа устройств (пусто — без группы).</summary>
    public string GroupId { get; set; } = "";

    /// <summary>sync | color | off | inherit (как в группе).</summary>
    public string Mode { get; set; } = "inherit";

    /// <summary>Цвет для режима "color" в формате #RRGGBB; пусто — цвет группы.</summary>
    public string StaticColor { get; set; } = "";

    // null означает «наследовать от группы, затем от общих настроек».
    public int? Brightness { get; set; }
    public int? Saturation { get; set; }
    public int? GammaPercent { get; set; }
    public int? Smoothing { get; set; }
    public int? BlackThreshold { get; set; }
    public int? EdgeDepthPercent { get; set; }

    /// <summary>
    /// Аппаратная яркость BLE-контроллера (1…100). null — использовать общую
    /// настройку <see cref="AppConfig.BluetoothBrightness"/>.
    /// </summary>
    public int? HardwareBrightness { get; set; }

    public int Shift { get; set; }
    public bool Reverse { get; set; }
    public int HorizontalOverride { get; set; }
    public int GainR { get; set; } = 100;
    public int GainG { get; set; } = 100;
    public int GainB { get; set; } = 100;
}

/// <summary>Группа лент: общие настройки, режим и цвет для всех участников.</summary>
public sealed class DeviceGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Группа";

    // null означает «наследовать от общих настроек приложения».
    public int? Brightness { get; set; }
    public int? Saturation { get; set; }
    public int? GammaPercent { get; set; }
    public int? Smoothing { get; set; }
    public int? BlackThreshold { get; set; }
    public int? EdgeDepthPercent { get; set; }

    /// <summary>sync | color | off.</summary>
    public string Mode { get; set; } = "sync";
    public string StaticColor { get; set; } = "#FFFFFF";

    /// <summary>Делить периметр монитора между лентами группы, а не дублировать его.</summary>
    public bool SplitPerimeter { get; set; }
}

/// <summary>
/// Именованный набор общих настроек. Пресет меняет базовые значения; собственные
/// настройки лент и групп при этом не трогаются.
/// </summary>
public sealed class SettingsPreset
{
    public string Name { get; set; } = "Пресет";
    public int Brightness { get; set; } = 100;
    public int Saturation { get; set; } = 100;
    public int GammaPercent { get; set; } = 100;
    public int Smoothing { get; set; } = 35;
    public int BlackThreshold { get; set; } = 6;
    public int EdgeDepthPercent { get; set; } = 12;
}

public readonly record struct EffectiveSettings(
    int Brightness,
    int Saturation,
    int GammaPercent,
    int Smoothing,
    int BlackThreshold,
    int EdgeDepthPercent);

public sealed class AppConfig
{
    public int Version { get; set; } = 2;
    public int Fps { get; set; } = 15;
    public int Brightness { get; set; } = 100;
    public int Saturation { get; set; } = 100;
    public int GammaPercent { get; set; } = 100;
    public int Smoothing { get; set; } = 35;
    public int BlackThreshold { get; set; } = 6;
    public int EdgeDepthPercent { get; set; } = 12;
    public bool StartSyncOnLaunch { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// Приостанавливать синхронизацию, когда экран заблокирован, монитор выключен
    /// или система уходит в сон: лента не светит всю ночь и не тратит CPU.
    /// </summary>
    public bool PauseWhenInactive { get; set; } = true;

    /// <summary>Искать устройства других производителей, совместимые с протоколом RB/SC.</summary>
    public bool ProbeCompatibleDevices { get; set; } = true;

    /// <summary>Искать Bluetooth LE ленты (FTY/CCHIP).</summary>
    public bool ScanBluetooth { get; set; } = true;

    /// <summary>
    /// Аппаратная яркость Bluetooth-лент (0…100). Выше ~30 дешёвые контроллеры
    /// уходят в защиту по току: лента вспыхивает и гаснет.
    /// </summary>
    public int BluetoothBrightness { get; set; } = 20;

    /// <summary>Свои цвета (#RRGGBB), доступные в палитре каждой ленты.</summary>
    public List<string> SavedColors { get; set; } = new();

    /// <summary>Пресеты общих настроек (яркость, насыщенность, гамма, сглаживание, чёрный, края).</summary>
    public List<SettingsPreset> Presets { get; set; } = DefaultPresets();

    /// <summary>
    /// Имя последнего применённого или сохранённого пресета: оно показывает,
    /// какой набор был активен, даже если текущие настройки уже изменены.
    /// </summary>
    public string ActivePresetName { get; set; } = "";

    public List<DeviceGroup> Groups { get; set; } = new();
    public List<DeviceConfig> Devices { get; set; } = new();

    private static List<SettingsPreset> DefaultPresets() => new()
    {
        new()
        {
            Name = "Кино", Brightness = 88, Saturation = 120, GammaPercent = 95,
            Smoothing = 60, BlackThreshold = 8, EdgeDepthPercent = 14
        },
        new()
        {
            Name = "Игры", Brightness = 100, Saturation = 140, GammaPercent = 100,
            Smoothing = 20, BlackThreshold = 4, EdgeDepthPercent = 10
        },
        new()
        {
            Name = "Работа", Brightness = 70, Saturation = 90, GammaPercent = 110,
            Smoothing = 45, BlackThreshold = 6, EdgeDepthPercent = 12
        }
    };

    /// <summary>Добавляет цвет в «Мои цвета»; false — цвет уже сохранён.</summary>
    public bool AddSavedColor(string hex)
    {
        hex = ColorUtil.NormalizeHex(hex, "");
        if (hex.Length == 0) return false;
        if (SavedColors.Any(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase)))
            return false;

        SavedColors.Insert(0, hex);
        while (SavedColors.Count > MaxSavedColors)
            SavedColors.RemoveAt(SavedColors.Count - 1);
        return true;
    }

    public void RemoveSavedColor(string hex)
    {
        SavedColors.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
    }

    private const int MaxSavedColors = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static string? _configPath;

    [JsonIgnore]
    public static string ConfigPath => _configPath ??= ResolveConfigPath();

    private static string ResolveConfigPath()
    {
        string exeDir = AppContext.BaseDirectory;
        string direct = Path.Combine(exeDir, "config.json");
        try
        {
            string probe = Path.Combine(exeDir, ".write-test");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return direct;
        }
        catch
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SyncLed");
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "config.json");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch
        {
            // повреждённый конфиг — начинаем с настроек по умолчанию
        }
        return new AppConfig();
    }

    public void Normalize()
    {
        Fps = Math.Clamp(Fps, 5, 60);
        Brightness = Math.Clamp(Brightness, 5, 100);
        Saturation = Math.Clamp(Saturation, 0, 200);
        GammaPercent = Math.Clamp(GammaPercent, 50, 250);
        Smoothing = Math.Clamp(Smoothing, 0, 95);
        BlackThreshold = Math.Clamp(BlackThreshold, 0, 60);
        EdgeDepthPercent = Math.Clamp(EdgeDepthPercent, 4, 30);
        BluetoothBrightness = Math.Clamp(BluetoothBrightness, 1, 100);
        SavedColors ??= new List<string>();
        var saved = new List<string>(SavedColors.Count);
        foreach (string color in SavedColors)
        {
            string hex = ColorUtil.NormalizeHex(color, "");
            if (hex.Length == 0) continue;
            if (saved.Contains(hex, StringComparer.OrdinalIgnoreCase)) continue;
            saved.Add(hex);
            if (saved.Count >= MaxSavedColors) break;
        }
        SavedColors = saved;
        Groups ??= new List<DeviceGroup>();
        Devices ??= new List<DeviceConfig>();

        // Пресеты: понятные имена без дубликатов, значения в допустимых пределах.
        Presets ??= new List<SettingsPreset>();
        var presetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presets = new List<SettingsPreset>(Presets.Count);
        foreach (var preset in Presets)
        {
            if (preset == null) continue;
            preset.Name = (preset.Name ?? "").Trim();
            if (preset.Name.Length == 0) preset.Name = "Пресет";
            if (preset.Name.Length > 40) preset.Name = preset.Name[..40].TrimEnd();
            if (!presetNames.Add(preset.Name)) continue;

            preset.Brightness = Math.Clamp(preset.Brightness, 5, 100);
            preset.Saturation = Math.Clamp(preset.Saturation, 0, 200);
            preset.GammaPercent = Math.Clamp(preset.GammaPercent, 50, 250);
            preset.Smoothing = Math.Clamp(preset.Smoothing, 0, 95);
            preset.BlackThreshold = Math.Clamp(preset.BlackThreshold, 0, 60);
            preset.EdgeDepthPercent = Math.Clamp(preset.EdgeDepthPercent, 4, 30);
            presets.Add(preset);
            if (presets.Count >= 24) break;
        }
        Presets = presets;

        // Активный пресет: корректное имя, и только если он реально есть в списке.
        ActivePresetName = (ActivePresetName ?? "").Trim();
        if (ActivePresetName.Length > 40) ActivePresetName = ActivePresetName[..40].TrimEnd();
        if (ActivePresetName.Length > 0 &&
            !Presets.Any(p => string.Equals(p.Name, ActivePresetName, StringComparison.OrdinalIgnoreCase)))
            ActivePresetName = "";

        // Группы: уникальные идентификаторы и понятные имена.
        var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Groups)
        {
            if (string.IsNullOrWhiteSpace(g.Id) || !groupIds.Add(g.Id))
                g.Id = NewGroupId(groupIds);
            g.Name = string.IsNullOrWhiteSpace(g.Name) ? "Группа" : g.Name.Trim();
            if (!groupNames.Add(g.Name))
            {
                string baseName = g.Name;
                int n = 2;
                while (!groupNames.Add($"{baseName} {n}")) n++;
                g.Name = $"{baseName} {n}";
            }

            g.Brightness = ClampNullable(g.Brightness, 5, 100);
            g.Saturation = ClampNullable(g.Saturation, 0, 200);
            g.GammaPercent = ClampNullable(g.GammaPercent, 50, 250);
            g.Smoothing = ClampNullable(g.Smoothing, 0, 95);
            g.BlackThreshold = ClampNullable(g.BlackThreshold, 0, 60);
            g.EdgeDepthPercent = ClampNullable(g.EdgeDepthPercent, 4, 30);
            if (g.Mode is not ("sync" or "color" or "off")) g.Mode = "sync";
            g.StaticColor = ColorUtil.NormalizeHex(g.StaticColor, "#FFFFFF");
        }

        var knownGroups = new HashSet<string>(Groups.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var d in Devices)
        {
            d.LampCount = Math.Clamp(d.LampCount, 8, 254);
            d.Name = (d.Name ?? "").Trim();
            if (d.Name.Length > 60) d.Name = d.Name[..60].TrimEnd();
            d.GainR = Math.Clamp(d.GainR, 20, 200);
            d.GainG = Math.Clamp(d.GainG, 20, 200);
            d.GainB = Math.Clamp(d.GainB, 20, 200);
            d.Shift = Math.Clamp(d.Shift, -254, 254);
            if (!string.IsNullOrEmpty(d.GroupId) && !knownGroups.Contains(d.GroupId)) d.GroupId = "";

            d.Brightness = ClampNullable(d.Brightness, 5, 100);
            d.Saturation = ClampNullable(d.Saturation, 0, 200);
            d.GammaPercent = ClampNullable(d.GammaPercent, 50, 250);
            d.Smoothing = ClampNullable(d.Smoothing, 0, 95);
            d.BlackThreshold = ClampNullable(d.BlackThreshold, 0, 60);
            d.EdgeDepthPercent = ClampNullable(d.EdgeDepthPercent, 4, 30);
            d.HardwareBrightness = ClampNullable(d.HardwareBrightness, 1, 100);

            // Совместимость со старыми конфигами: red/green/blue/white → color.
            switch (d.Mode)
            {
                case "red":
                    d.Mode = "color";
                    d.StaticColor = "#FF0000";
                    break;
                case "green":
                    d.Mode = "color";
                    d.StaticColor = "#00FF00";
                    break;
                case "blue":
                    d.Mode = "color";
                    d.StaticColor = "#0000FF";
                    break;
                case "white":
                    d.Mode = "color";
                    d.StaticColor = "#FFFFFF";
                    break;
                case "sync":
                case "color":
                case "off":
                case "inherit":
                    break;
                default:
                    d.Mode = "inherit";
                    break;
            }

            if (!string.IsNullOrWhiteSpace(d.StaticColor))
                d.StaticColor = ColorUtil.NormalizeHex(d.StaticColor, "#FFFFFF");
            else
                d.StaticColor = "";
        }

        Version = 2;
    }

    private static int? ClampNullable(int? value, int min, int max) =>
        value.HasValue ? Math.Clamp(value.Value, min, max) : null;

    private static string NewGroupId(HashSet<string> used)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N")[..8];
        } while (!used.Add(id));
        return id;
    }

    public void Save()
    {
        try
        {
            Normalize();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // не критично — приложение продолжает работать
        }
    }

    public DeviceGroup? FindGroup(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var group in Groups)
        {
            if (string.Equals(group.Id, id, StringComparison.OrdinalIgnoreCase)) return group;
        }
        return null;
    }

    /// <summary>Отображаемое имя устройства: пользовательское или стандартное название ленты.</summary>
    public static string DisplayName(DeviceConfig device, string fallback) =>
        string.IsNullOrWhiteSpace(device.Name) ? fallback : device.Name.Trim();

    public DeviceConfig GetOrCreateDevice(string uuid, string productId, int lampCount, string? pathHash = null)
    {
        string key = string.IsNullOrEmpty(uuid) && !string.IsNullOrWhiteSpace(pathHash)
            ? $"000000@{pathHash}"
            : uuid;

        var cfg = Devices.FirstOrDefault(d =>
            !string.IsNullOrEmpty(d.Uuid) && string.Equals(d.Uuid, key, StringComparison.OrdinalIgnoreCase));

        // Если в прошлый раз UUID прочитать не удалось, настройки лежат под ключом
        // "000000@<хеш пути>". Когда UUID появился, переносим запись на него.
        if (cfg == null && !string.IsNullOrEmpty(uuid) && !string.IsNullOrWhiteSpace(pathHash))
        {
            string fallback = $"000000@{pathHash}";
            var legacy = Devices.FirstOrDefault(d =>
                string.Equals(d.Uuid, fallback, StringComparison.OrdinalIgnoreCase));
            if (legacy != null)
            {
                legacy.Uuid = uuid;
                cfg = legacy;
            }
        }

        if (cfg == null)
        {
            if (string.IsNullOrEmpty(key)) return new DeviceConfig { Uuid = "", ProductId = productId, LampCount = lampCount };

            cfg = new DeviceConfig
            {
                Uuid = key,
                ProductId = productId,
                LampCount = lampCount,
                Mode = "inherit"
            };
            Devices.Add(cfg);
        }
        cfg.ProductId = productId;
        cfg.LampCount = lampCount;
        return cfg;
    }

    public EffectiveSettings ResolveSettings(DeviceConfig device)
    {
        var group = FindGroup(device.GroupId);
        return new EffectiveSettings(
            device.Brightness ?? group?.Brightness ?? Brightness,
            device.Saturation ?? group?.Saturation ?? Saturation,
            device.GammaPercent ?? group?.GammaPercent ?? GammaPercent,
            device.Smoothing ?? group?.Smoothing ?? Smoothing,
            device.BlackThreshold ?? group?.BlackThreshold ?? BlackThreshold,
            device.EdgeDepthPercent ?? group?.EdgeDepthPercent ?? EdgeDepthPercent);
    }

    /// <summary>
    /// Аппаратная яркость BLE-контроллера: собственная настройка устройства,
    /// иначе общая настройка приложения.
    /// </summary>
    public int ResolveHardwareBrightness(DeviceConfig device) =>
        Math.Clamp(device.HardwareBrightness ?? BluetoothBrightness, 1, 100);

    public string ResolveMode(DeviceConfig device)
    {
        string mode = device.Mode;
        if (string.IsNullOrWhiteSpace(mode) || mode == "inherit")
            mode = FindGroup(device.GroupId)?.Mode ?? "sync";
        return mode is "sync" or "color" or "off" ? mode : "sync";
    }

    public Rgb ResolveStaticColor(DeviceConfig device)
    {
        string? hex = device.StaticColor;
        if (string.IsNullOrWhiteSpace(hex))
            hex = FindGroup(device.GroupId)?.StaticColor;
        return ColorUtil.ParseHex(hex, Rgb.White);
    }

    /// <summary>
    /// Цвет, который реально уходит в ленту: к выбранному оттенку применяются
    /// яркость, насыщенность, гамма и баланс RGB — так же, как при синхронизации.
    /// Без этого статичный цвет выглядел бледнее, чем в режиме экрана.
    /// </summary>
    public Rgb ResolveStaticOutput(DeviceConfig device)
    {
        var settings = ResolveSettings(device);
        return ColorUtil.Process(ResolveStaticColor(device),
            settings.Brightness / 100.0,
            settings.Saturation / 100.0,
            settings.GammaPercent / 100.0,
            device.GainR / 100.0,
            device.GainG / 100.0,
            device.GainB / 100.0);
    }

    public DeviceGroup CreateGroup(string name)
    {
        var ids = new HashSet<string>(Groups.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
        var group = new DeviceGroup
        {
            Id = NewGroupId(ids),
            Name = string.IsNullOrWhiteSpace(name) ? "Новая группа" : name.Trim()
        };
        Groups.Add(group);
        Normalize();
        return group;
    }

    public static bool IsWindowsAutostartEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
        return key?.GetValue("SyncLed") != null;
    }

    public static void SetWindowsAutostart(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SyncLed.exe");
            key.SetValue("SyncLed", $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue("SyncLed", throwOnMissingValue: false);
        }
    }
}
