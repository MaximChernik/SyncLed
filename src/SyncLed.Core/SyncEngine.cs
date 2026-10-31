using System.Diagnostics;
using System.Drawing;

namespace SyncLed;

public sealed record EngineStatus(
    string Key,
    string Title,
    bool Connected,
    string? Error,
    int Lamps,
    string Monitor,
    string Mode,
    int ColorCount);

/// <summary>Состояние одной ленты для окна диагностики.</summary>
public sealed record StripDiagnostics(
    string Key,
    string Title,
    string Transport,
    string Monitor,
    string Mode,
    bool Enabled,
    bool Connected,
    int Lamps,
    string? Error,
    bool Testing,
    StripStats Stats);

/// <summary>Снимок состояния движка и лент. Счётчики накопительные.</summary>
public sealed record DiagnosticsSnapshot(
    bool Running,
    double Fps,
    long Frames,
    double FrameMs,
    List<StripDiagnostics> Strips);

/// <summary>Поток синхронизации: захват мониторов → расчёт цветов → отправка в ленты.</summary>
public sealed class SyncEngine : IDisposable
{
    private sealed class Runtime
    {
        public required ILedStrip Strip { get; init; }
        public DeviceConfig Config = new();
        public FrameSampler? Sampler;
        public ZoneLayout? Layout;
        public int LayoutKey;
        public ZoneLayout? GroupLayout;
        public int GroupLayoutKey;
        public List<Rgb> Previous = new();
        public List<Rgb> Current = new();
        public MonitorChoice? Monitor;
        public DateTime LastOpenAttempt = DateTime.MinValue;
        public DateTime LastStaticSend = DateTime.MinValue;
        public Rgb LastStaticColor = new(255, 255, 255);
        public DateTime LastErrorLog = DateTime.MinValue;
        public DateTime TestUntil = DateTime.MinValue;
        public int OpenFailures;
        // Применённое состояние флажка «Лента включена»: null — ещё не проверяли.
        public bool? EnabledApplied;
        // После ошибки выключения контроллер нужно открыть ещё раз и повторить Off.
        public bool OffRetry;
    }

    private readonly AppConfig _config;
    private readonly List<Runtime> _runtimes = new();
    private readonly object _stateLock = new();
    private Dictionary<string, List<Rgb>> _preview = new();
    private List<MonitorChoice> _monitors = new();
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _stopping;
    private long _frames;
    private double _fps;
    private long _fpsWindowStart = Environment.TickCount64;
    private int _fpsWindowFrames;
    private double _lastFrameMs;

    public event Action<string>? Log;

    public bool IsRunning => _running;

    public SyncEngine(AppConfig config, IEnumerable<ILedStrip> strips)
    {
        _config = config;
        foreach (var strip in strips)
        {
            var device = config.GetOrCreateDevice(strip.Key, strip.ProductId, strip.LampCount, strip.PathHash);
            if (strip is BleLedStrip ble)
                ble.HardwareBrightness = config.ResolveHardwareBrightness(device);

            _runtimes.Add(new Runtime
            {
                Strip = strip,
                Config = device
            });
        }
    }

    public void SetMonitors(List<MonitorChoice> monitors)
    {
        lock (_stateLock) _monitors = monitors;
    }

    public void Start()
    {
        if (_running) return;
        _stopping = false;
        _fpsWindowStart = Environment.TickCount64;
        _fpsWindowFrames = 0;
        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "SyncLedCapture",
            Priority = ThreadPriority.Normal
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;

        // Даём потоку захвата плавно погасить ленты и только потом закрываем их.
        _stopping = true;
        try { _thread?.Join(1500); } catch { }
        _running = false;
        _stopping = false;
        try { _thread?.Join(500); } catch { }
        _thread = null;

        // SendOff синхронно ждёт завершения записи. Close вызываем даже для
        // ещё подключающейся BLE-ленты, чтобы отменить её power-on инициализацию.
        foreach (var rt in _runtimes)
        {
            rt.TestUntil = DateTime.MinValue;
            rt.EnabledApplied = null;
            try
            {
                if (rt.Strip.IsOpen) rt.Strip.SendOff();
            }
            catch (Exception ex)
            {
                EmitLog($"{AppConfig.DisplayName(rt.Config, rt.Strip.Title)}: не удалось выключить: {ex.Message}");
            }
            finally
            {
                rt.Strip.Close();
                rt.EnabledApplied = false;
                rt.OffRetry = false;
            }
        }
        lock (_stateLock) _preview = new Dictionary<string, List<Rgb>>();
    }

    public List<EngineStatus> GetStatus()
    {
        var result = new List<EngineStatus>();
        lock (_stateLock)
        {
            foreach (var rt in _runtimes)
            {
                result.Add(new EngineStatus(
                    rt.Strip.Key,
                    AppConfig.DisplayName(rt.Config, rt.Strip.Title),
                    rt.Strip.IsOpen,
                    rt.Strip.LastError,
                    rt.Config.LampCount,
                    rt.Monitor?.Title ?? "не выбран",
                    _config.ResolveMode(rt.Config),
                    rt.Current.Count));
            }
        }
        return result;
    }

    public bool TryGetPreview(string uuid, out List<Rgb> colors)
    {
        lock (_stateLock)
        {
            if (_preview.TryGetValue(uuid, out var list))
            {
                colors = list;
                return true;
            }
        }
        colors = new List<Rgb>();
        return false;
    }

    /// <summary>Снимок счётчиков движка и лент для окна диагностики.</summary>
    public DiagnosticsSnapshot GetDiagnostics()
    {
        lock (_stateLock)
        {
            var strips = new List<StripDiagnostics>(_runtimes.Count);
            foreach (var rt in _runtimes)
            {
                strips.Add(new StripDiagnostics(
                    rt.Strip.Key,
                    AppConfig.DisplayName(rt.Config, rt.Strip.Title),
                    rt.Strip.Transport,
                    rt.Monitor?.Title ?? "не выбран",
                    _config.ResolveMode(rt.Config),
                    rt.Config.Enabled,
                    rt.Strip.IsOpen,
                    rt.Config.LampCount,
                    rt.Strip.LastError,
                    rt.TestUntil > DateTime.UtcNow,
                    rt.Strip.GetStats()));
            }

            return new DiagnosticsSnapshot(
                _running, _fps, Interlocked.Read(ref _frames), _lastFrameMs, strips);
        }
    }

    /// <summary>
    /// Обнуляет счётчики диагностики: кадры движка и обмен каждой лентой.
    /// FPS не трогаем — он и так считается по скользящему окну.
    /// </summary>
    public void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _frames, 0);
        lock (_stateLock)
        {
            foreach (var rt in _runtimes)
                rt.Strip.ResetStats();
        }
    }

    /// <summary>Ручной тест ленты: "gradient", "runner" или "blink". Синхронизация на это время приостанавливается.</summary>
    public void RunTest(string key, string kind)
    {
        var rt = _runtimes.FirstOrDefault(r => r.Strip.Key == key);
        if (rt == null) return;

        var now = DateTime.UtcNow;
        if (rt.TestUntil > now) return; // тест уже идёт
        rt.TestUntil = now.AddSeconds(kind == "runner" ? 17 : kind == "blink" ? 5 : 7);
        rt.Previous.Clear();

        var thread = new Thread(() => TestLoop(rt, kind))
        {
            IsBackground = true,
            Name = "SyncLedTest"
        };
        thread.Start();
    }

    private void TestLoop(Runtime rt, string kind)
    {
        try
        {
            if (!WaitOpen(rt, 10000))
            {
                EmitLog($"{AppConfig.DisplayName(rt.Config, rt.Strip.Title)}: {rt.Strip.LastError ?? "нет связи"}");
                return;
            }

            if (kind == "runner")
            {
                int n = Math.Clamp(rt.Config.LampCount, 1, 254);
                var colors = new List<Rgb>(n);
                for (int i = 0; i < n; i++) colors.Add(Rgb.Black);

                var sw = Stopwatch.StartNew();
                int last = -1;
                while (sw.ElapsedMilliseconds < 14000 && rt.Strip.IsOpen)
                {
                    int pos = (int)(sw.ElapsedMilliseconds / 90) % n;
                    if (pos != last)
                    {
                        if (last >= 0) colors[last] = Rgb.Black;
                        colors[pos] = new Rgb(0, 200, 255);
                        last = pos;
                        Publish(rt, colors);
                        rt.Strip.SendColors(colors);
                    }
                    Thread.Sleep(8);
                }
            }
            else if (kind == "blink")
            {
                // Три белые вспышки: так проще всего понять, какая лента перед тобой.
                int n = Math.Clamp(rt.Config.LampCount, 1, 254);
                var on = new List<Rgb>(n);
                var off = new List<Rgb>(n);
                for (int i = 0; i < n; i++)
                {
                    on.Add(Rgb.White);
                    off.Add(Rgb.Black);
                }

                for (int i = 0; i < 3 && rt.Strip.IsOpen; i++)
                {
                    Publish(rt, on);
                    rt.Strip.SendColors(on);
                    Thread.Sleep(380);
                    Publish(rt, off);
                    rt.Strip.SendColors(off);
                    Thread.Sleep(320);
                }
                Publish(rt, off);
            }
            else
            {
                var colors = ColorUtil.Gradient(Math.Clamp(rt.Config.LampCount, 1, 254));
                Publish(rt, colors);
                rt.Strip.SendColors(colors);
                Thread.Sleep(5000);
            }
        }
        catch (Exception ex)
        {
            EmitLog("Тест ленты: " + ex.Message);
        }
        finally
        {
            rt.TestUntil = DateTime.MinValue;
            if (!_running) rt.Strip.Close();
        }
    }

    private void Publish(Runtime rt, List<Rgb> colors)
    {
        rt.Current = colors;
        lock (_stateLock) _preview[rt.Strip.Key] = new List<Rgb>(colors);
    }

    private void Loop()
    {
        while (_running && !_stopping)
        {
            var frameTimer = Stopwatch.StartNew();
            try
            {
                RenderFrame();
            }
            catch (Exception ex)
            {
                EmitLog("Ошибка синхронизации: " + ex.Message);
                Thread.Sleep(250);
            }

            Interlocked.Increment(ref _frames);
            _lastFrameMs = frameTimer.Elapsed.TotalMilliseconds;
            _fpsWindowFrames++;
            long now = Environment.TickCount64;
            long window = now - _fpsWindowStart;
            if (window >= 1000)
            {
                _fps = _fpsWindowFrames * 1000.0 / window;
                _fpsWindowFrames = 0;
                _fpsWindowStart = now;
            }

            int targetMs = Math.Max(10, 1000 / Math.Clamp(_config.Fps, 5, 60));
            int elapsed = (int)frameTimer.ElapsedMilliseconds;
            int delay = targetMs - elapsed;
            if (delay > 0)
            {
                // Небольшая компенсация дрейфа таймера
                Thread.Sleep(delay);
            }
            else if (elapsed > targetMs * 4)
            {
                Thread.Sleep(1);
            }
        }

        // Остановка: несколько кадров от последней картинки к чёрному.
        if (_stopping)
        {
            try { FadeOut(); } catch { }
        }
    }

    /// <summary>
    /// Плавное затухание перед выключением. Шаги редкие: трафик на ленту ограничен
    /// контроллером, поэтому кадры, не влезшие в лимит, просто пропускаются.
    /// </summary>
    private void FadeOut()
    {
        var active = _runtimes
            .Where(r => r.Strip.IsOpen && r.Current.Count > 0)
            .ToList();
        if (active.Count == 0) return;

        const int steps = 6;
        for (int step = 1; step <= steps; step++)
        {
            double k = 1.0 - step / (double)steps;
            foreach (var rt in active)
            {
                if (!rt.Strip.IsOpen) continue;

                // В статичном режиме Current хранит один цвет: растягиваем его на всю
                // ленту, иначе погас бы только первый LED.
                List<Rgb> source = rt.Current;
                if (source.Count == 1 && _config.ResolveMode(rt.Config) != "sync")
                {
                    int lamps = Math.Clamp(rt.Config.LampCount, 1, 254);
                    var filled = new List<Rgb>(lamps);
                    for (int i = 0; i < lamps; i++) filled.Add(source[0]);
                    source = filled;
                }

                var dim = new List<Rgb>(source.Count);
                foreach (var c in source)
                    dim.Add(new Rgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k)));
                try { rt.Strip.SendColors(dim); } catch { }
            }
            Thread.Sleep(40);
        }
    }

    private void RenderFrame()
    {
        var monitors = GetMonitorsSnapshot();
        var frames = new Dictionary<string, (FrameSampler Sampler, byte[] Pixels)>(StringComparer.OrdinalIgnoreCase);
        var preview = new Dictionary<string, List<Rgb>>();

        foreach (var rt in _runtimes)
        {
            try
            {
                RenderDevice(rt, monitors, frames, preview);
            }
            catch (Exception ex)
            {
                // Проблемная лента не должна останавливать вторую.
                rt.Previous.Clear();
                if ((DateTime.UtcNow - rt.LastErrorLog).TotalSeconds > 5)
                {
                    rt.LastErrorLog = DateTime.UtcNow;
                    EmitLog($"{AppConfig.DisplayName(rt.Config, rt.Strip.Title)}: {ex.Message}");
                }
            }
        }

        lock (_stateLock) _preview = preview;
    }

    private void RenderDevice(Runtime rt, List<MonitorChoice> monitors,
        Dictionary<string, (FrameSampler Sampler, byte[] Pixels)> frames,
        Dictionary<string, List<Rgb>> preview)
    {
        var cfg = rt.Config;
        var monitor = Monitors.FindByDeviceName(monitors, cfg.MonitorDeviceName)
                      ?? monitors.FirstOrDefault();
        rt.Monitor = monitor;

        if (!cfg.Enabled)
        {
            bool testing = rt.TestUntil > DateTime.UtcNow;
            if (!testing && rt.EnabledApplied != false)
            {
                // Флажок «Лента включена» снят: гасим ленту и отпускаем контроллер,
                // чтобы она не светила последним кадром до следующего запуска.
                try
                {
                    // Если предыдущая попытка Off не дошла, закрытый контроллер
                    // переоткрываем: просто закрыть handle недостаточно для погасшей ленты.
                    if (rt.OffRetry && !rt.Strip.IsOpen && !EnsureOpen(rt)) return;
                    if (rt.Strip.IsOpen) rt.Strip.SendOff();
                    rt.Strip.Close();
                    rt.OffRetry = false;
                    rt.EnabledApplied = false;
                    rt.Previous.Clear();
                    rt.Current = new List<Rgb>();
                    rt.LastStaticColor = Rgb.Black;
                    rt.LastStaticSend = DateTime.MinValue;
                    EmitLog($"{AppConfig.DisplayName(cfg, rt.Strip.Title)}: выключена");
                }
                catch (Exception ex)
                {
                    // Не фиксируем ложный успех: следующий кадр повторит выключение.
                    rt.OffRetry = true;
                    try { rt.Strip.Close(); } catch { }
                    if ((DateTime.UtcNow - rt.LastErrorLog).TotalSeconds > 5)
                    {
                        rt.LastErrorLog = DateTime.UtcNow;
                        EmitLog($"{AppConfig.DisplayName(cfg, rt.Strip.Title)}: не удалось выключить: {ex.Message}");
                    }
                }
            }
            return;
        }

        rt.EnabledApplied = true;
        rt.OffRetry = false;

        // Пока идёт ручной тест, поток синхронизации не должен перебивать цвета.
        if (rt.TestUntil > DateTime.UtcNow) return;

        if (!EnsureOpen(rt)) return;

        string mode = _config.ResolveMode(cfg);
        if (mode == "sync")
        {
            if (monitor == null) return;

            // Ленты одной группы могут делить периметр монитора между собой.
            if (TryRenderGroupSplit(rt, monitor, frames, preview)) return;

            if (!TryGetFrame(rt, monitor, frames, out var sampler, out var pixels)) return;

            var settings = _config.ResolveSettings(cfg);
            var layout = EnsureLayout(rt, sampler, monitor, settings);
            var orderedZones = layout.Ordered(cfg.Shift, cfg.Reverse);
            var target = BuildColors(cfg, settings, sampler, pixels, orderedZones);

            rt.Current = Smooth(rt, target, settings.Smoothing);
            rt.Strip.SendColors(rt.Current);
            preview[rt.Strip.Key] = rt.Current;
        }
        else
        {
            Rgb chosen = mode == "color" ? _config.ResolveStaticColor(cfg) : Rgb.Black;
            Rgb color = mode == "color" ? _config.ResolveStaticOutput(cfg) : Rgb.Black;

            bool changed = color != rt.LastStaticColor;
            if (changed || (DateTime.UtcNow - rt.LastStaticSend).TotalMilliseconds > 500)
            {
                rt.Strip.SendStatic(color);
                if (changed && mode == "color")
                {
                    EmitLog($"{AppConfig.DisplayName(cfg, rt.Strip.Title)}: " +
                            $"{ColorUtil.ToHex(chosen)} на ленту {ColorUtil.ToHex(color)}");
                }
                rt.LastStaticColor = color;
                rt.LastStaticSend = DateTime.UtcNow;
            }
            rt.Current = new List<Rgb> { color };
            preview[rt.Strip.Key] = rt.Current;
        }
    }

    /// <summary>
    /// Ленты одной группы на одном мониторе могут работать как одна длинная лента:
    /// периметр делится между участниками пропорционально числу LED.
    /// </summary>
    private bool TryRenderGroupSplit(Runtime rt, MonitorChoice monitor,
        Dictionary<string, (FrameSampler Sampler, byte[] Pixels)> frames,
        Dictionary<string, List<Rgb>> preview)
    {
        var group = _config.FindGroup(rt.Config.GroupId);
        if (group == null || !group.SplitPerimeter) return false;

        var peers = _runtimes.Where(r =>
                r.Config.Enabled
                && string.Equals(r.Config.GroupId, rt.Config.GroupId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Config.MonitorDeviceName, rt.Config.MonitorDeviceName,
                    StringComparison.OrdinalIgnoreCase)
                && _config.ResolveMode(r.Config) == "sync")
            .ToList();
        if (peers.Count < 2) return false;

        int index = peers.IndexOf(rt);
        if (index < 0) return false;

        if (!TryGetFrame(rt, monitor, frames, out var sampler, out var pixels))
            return true;

        int rawTotal = 0;
        int startLed = 0;
        for (int i = 0; i < peers.Count; i++)
        {
            int lamps = Math.Clamp(peers[i].Config.LampCount, 8, 254);
            if (i < index) startLed += lamps;
            rawTotal += lamps;
        }
        if (rawTotal <= 0) return false;

        int totalLamps = Math.Clamp(rawTotal, 8, 254);
        var groupSettings = _config.ResolveSettings(peers[0].Config);
        double aspect = monitor.Bounds.Width / (double)Math.Max(1, monitor.Bounds.Height);
        int key = HashCode.Combine(group.Id, monitor.DeviceName, totalLamps,
            sampler.Width, sampler.Height, groupSettings.EdgeDepthPercent,
            peers[0].Config.HorizontalOverride);
        if (rt.GroupLayout == null || rt.GroupLayoutKey != key)
        {
            rt.GroupLayout = ZoneLayout.Build(sampler.Width, sampler.Height, totalLamps,
                aspect, groupSettings.EdgeDepthPercent, peers[0].Config.HorizontalOverride);
            rt.GroupLayoutKey = key;
            rt.Previous.Clear();
        }

        var zones = rt.GroupLayout.Ordered(0, false);
        int zoneCount = zones.Count;
        if (zoneCount == 0) return false;

        int lampCount = Math.Clamp(rt.Config.LampCount, 8, 254);
        var orderedZones = new List<Rectangle>(lampCount);
        for (int j = 0; j < lampCount; j++)
        {
            int globalLed = (int)((long)(startLed + j) * totalLamps / rawTotal);
            int zoneIndex = (int)((long)globalLed * zoneCount / totalLamps);
            orderedZones.Add(zones[Math.Clamp(zoneIndex, 0, zoneCount - 1)]);
        }

        var settings = _config.ResolveSettings(rt.Config);
        var target = BuildColors(rt.Config, settings, sampler, pixels, orderedZones);
        rt.Current = Smooth(rt, target, settings.Smoothing);
        rt.Strip.SendColors(rt.Current);
        preview[rt.Strip.Key] = rt.Current;
        return true;
    }

    internal static List<Rgb> BuildColors(DeviceConfig cfg, EffectiveSettings settings,
        FrameSampler sampler, byte[] pixels, IReadOnlyList<Rectangle> orderedZones)
    {
        double brightness = settings.Brightness / 100.0;
        double saturation = settings.Saturation / 100.0;
        double gamma = settings.GammaPercent / 100.0;
        double gainR = cfg.GainR / 100.0;
        double gainG = cfg.GainG / 100.0;
        double gainB = cfg.GainB / 100.0;

        var target = new List<Rgb>(orderedZones.Count);
        foreach (var zone in orderedZones)
        {
            Rgb raw = sampler.Average(pixels, zone);
            if (raw.Max < settings.BlackThreshold) raw = Rgb.Black;
            target.Add(ColorUtil.Process(raw, brightness, saturation, gamma, gainR, gainG, gainB));
        }
        return target;
    }

    private List<MonitorChoice> GetMonitorsSnapshot()
    {
        lock (_stateLock) return _monitors;
    }

    private bool TryGetFrame(Runtime rt, MonitorChoice monitor,
        Dictionary<string, (FrameSampler Sampler, byte[] Pixels)> frames,
        out FrameSampler sampler, out byte[] pixels)
    {
        string key = monitor.DeviceName;
        if (frames.TryGetValue(key, out var cached))
        {
            sampler = cached.Sampler;
            pixels = cached.Pixels;
            return true;
        }

        // Сбалансированный режим: 320 px дают заметно меньший GDI-захват,
        // при этом зоны по периметру и диагностика остаются читаемыми.
        int capWidth = 320;
        double aspect = monitor.Bounds.Width / (double)Math.Max(1, monitor.Bounds.Height);
        int capHeight = Math.Max(32, (int)Math.Round(capWidth / Math.Max(0.2, aspect)));

        if (rt.Sampler == null || rt.Sampler.Width != capWidth || rt.Sampler.Height != capHeight)
        {
            rt.Sampler?.Dispose();
            rt.Sampler = new FrameSampler(capWidth, capHeight);
        }

        if (!rt.Sampler.Capture(monitor.Bounds))
        {
            sampler = rt.Sampler;
            pixels = Array.Empty<byte>();
            return false;
        }

        pixels = rt.Sampler.ReadPixels();
        sampler = rt.Sampler;
        frames[key] = (sampler, pixels);
        return true;
    }

    private ZoneLayout EnsureLayout(Runtime rt, FrameSampler sampler, MonitorChoice monitor,
        EffectiveSettings settings)
    {
        double aspect = monitor.Bounds.Width / (double)Math.Max(1, monitor.Bounds.Height);
        int key = HashCode.Combine(rt.Config.LampCount, sampler.Width, sampler.Height,
            settings.EdgeDepthPercent, rt.Config.HorizontalOverride);
        if (rt.Layout == null || rt.LayoutKey != key)
        {
            rt.Layout = ZoneLayout.Build(sampler.Width, sampler.Height, rt.Config.LampCount,
                aspect, settings.EdgeDepthPercent, rt.Config.HorizontalOverride);
            rt.LayoutKey = key;
            rt.Previous.Clear();
        }
        return rt.Layout;
    }

    private List<Rgb> Smooth(Runtime rt, List<Rgb> target, int smoothing)
    {
        if (rt.Previous.Count != target.Count || smoothing <= 0)
        {
            rt.Previous = new List<Rgb>(target);
            return target;
        }

        double alpha = 1.0 - smoothing / 100.0;
        var result = new List<Rgb>(target.Count);
        for (int i = 0; i < target.Count; i++)
        {
            Rgb c = ColorUtil.Lerp(rt.Previous[i], target[i], alpha);
            if (target[i].Max == 0) c = Rgb.Black;
            result.Add(c);
        }
        rt.Previous = result;
        return result;
    }

    /// <summary>
    /// Ожидает подключения ленты. Bluetooth-лента подключается в фоне, поэтому
    /// тесту нужно дождаться готовности, а не считать первый отказ окончательным.
    /// </summary>
    private bool WaitOpen(Runtime rt, int timeoutMs)
    {
        if (rt.Strip.IsOpen) return true;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            rt.Strip.Open();
            if (rt.Strip.IsOpen) return true;
            Thread.Sleep(120);
        }
        while (DateTime.UtcNow < deadline);
        return false;
    }

    private bool EnsureOpen(Runtime rt)    {
        // Настройки могли измениться на лету — лента применит новую яркость сама.
        if (rt.Strip is BleLedStrip ble) ble.HardwareBrightness = _config.ResolveHardwareBrightness(rt.Config);

        if (rt.Strip.IsOpen) return true;

        var now = DateTime.UtcNow;
        // Пауза между попытками: быстрая в начале (после переподключения USB лента
        // должна подхватиться сама), но не чаще, чем раз в 8 секунд.
        int delayMs = rt.OpenFailures <= 0
            ? 2000
            : Math.Min(8000, 2000 * (1 << Math.Min(3, rt.OpenFailures)));
        if ((now - rt.LastOpenAttempt).TotalMilliseconds < delayMs) return false;
        rt.LastOpenAttempt = now;

        if (rt.Strip.Open())
        {
            rt.OpenFailures = 0;
            EmitLog($"{AppConfig.DisplayName(rt.Config, rt.Strip.Title)}: подключено");
            rt.Previous.Clear();
            return true;
        }

        rt.OpenFailures++;
        if ((now - rt.LastErrorLog).TotalSeconds > 10)
        {
            rt.LastErrorLog = now;
            EmitLog($"{AppConfig.DisplayName(rt.Config, rt.Strip.Title)}: {rt.Strip.LastError ?? "нет связи"}");
        }
        return false;
    }

    private void EmitLog(string message)
    {
        // Время добавляет UI-поток (Enqueue), чтобы в журнале не было двойных отметок.
        try { Log?.Invoke(message); } catch { }
    }

    public void Dispose() => Stop();
}
