using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace SyncLed;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);
    public int Max => Math.Max(R, Math.Max(G, B));
}

public sealed record LedDeviceInfo(string ProductId, int LampCount, string Firmware, string Uuid, int DisplaySize);

/// <summary>Один USB-HID контроллер ленты (Robobloq / SyncLight, VID 1A86 PID FE07).
/// Протокол восстановлен из официального приложения: пакеты "RB" (static section) и "SC" (sync screen),
/// каждый пакет режется на HID-репорты по 64 байта с ведущим report id 0.
/// Вся работа с устройством идёт через overlapped-ввод-вывод с таймаутами: если контроллер
/// перестаёт принимать данные, приложение не зависает, а закрывает и заново открывает ленту.
/// Параллельно работает поток, вычитывающий входные отчёты — иначе буфер устройства
/// переполняется и прошивка блокирует приём новых пакетов.
/// </summary>
public sealed class LedStrip : ILedStrip
{
    private const byte ActionSetSectionLed = 134;
    private const byte ActionSetSyncScreen = 128;
    private const byte ActionSetOpenUrl = 147;
    private const byte ActionReadDeviceInfo = 130;
    private const int MaxChunk = 64;
    private const int WriteTimeoutMs = 600;
    private const int OffWaitTimeoutMs = WriteTimeoutMs + 1000;
    private const int ReadTimeoutMs = 1000;

    /// <summary>
    /// Прошивка контроллера не справляется с потоком мелких сегментов (зависает при ~10 КБ/с).
    /// Поэтому кадр ужимается до 24 записей (соседние зоны усредняются), а суммарный трафик
    /// на ленту ограничивается — лишние кадры пропускаются.
    /// </summary>
    private const int MaxRecordsPerFrame = 24;
    // Сбалансированный режим: ~5.5 КБ/с дают запас до ~40 FPS и остаются
    // заметно ниже области, на которой прошивка 1.7.x начинает зависать.
    private const double MaxBytesPerSecond = 5500;

    private readonly object _sync = new();
    private SafeFileHandle? _handle;
    private IntPtr _readEvent;
    private IntPtr _writeEvent;
    private Thread? _reader;
    private volatile bool _readerStop;
    private byte _messageId = 10;
    private int _reportLength = 65;
    private int _inputReportLength = 65;

    private enum ActionType { None, Static, Sync, Off }
    private ActionType _nextAction = ActionType.None;
    private Rgb _nextStaticColor;
    private List<Rgb>? _nextSyncColors;
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _writer;
    private volatile bool _writerStop;
    private TaskCompletionSource<Exception?>? _offCompletion;

    // Кэш последнего отправленного кадра: статичную картинку не нужно гонять 30 раз в секунду.
    private List<(int Start, Rgb Color, int End)>? _lastRecords;
    private long _lastSendTicks;
    private long _nextSendTicks;

    // Диагностика: сколько кадров ушло, сколько отбросил ограничитель, объём и ошибки.
    private long _statSent;
    private long _statSkipped;
    private long _statBytes;
    private long _statErrors;

    public string Path { get; }
    public ushort Vid { get; }
    public ushort Pid { get; }
    public string ProductName { get; }
    public string Manufacturer { get; }
    public string Uuid { get; internal set; } = string.Empty;
    public string ProductId { get; internal set; } = "000000";
    public string Firmware { get; internal set; } = "?";
    public int LampCount { get; internal set; } = 96;
    public int DisplaySize { get; internal set; }
    public bool IsOpen { get; private set; }
    public string? LastError { get; internal set; }

    /// <summary>Сколько входных отчётов вычитано (диагностика).</summary>
    public long InputReports { get; private set; }

    /// <summary>Стабильный (не зависящий от процесса) хеш пути устройства.</summary>
    public string PathHash => ShortPathHash(Path);

    public string Key => string.IsNullOrEmpty(Uuid) ? $"{ProductId}@{PathHash}" : Uuid;

    public string Transport => "USB";

    public string Detail => string.IsNullOrEmpty(Uuid)
        ? $"{Manufacturer} {Vid:X4}:{Pid:X4} · параметры не прочитаны"
        : $"{ProductId} · прошивка {Firmware} · {LampCount} LED";

    public string Title
    {
        get
        {
            string name = ProductId switch
            {
                "000503" => "Лента для монитора",
                "000000" => "Универсальная лента",
                _ => ""
            };
            if (name.Length == 0)
            {
                name = !string.IsNullOrWhiteSpace(ProductName) && ProductName.Length > 2
                    ? ProductName
                    : $"LED-лента {Vid:X4}:{Pid:X4}";
            }
            return $"{name} · {LampCount} LED";
        }
    }

    public LedStrip(string path, ushort vid = 0, ushort pid = 0,
        string productName = "", string manufacturer = "")
    {
        Path = path;
        Vid = vid;
        Pid = pid;
        ProductName = productName?.Trim() ?? "";
        Manufacturer = manufacturer?.Trim() ?? "";
    }

    /// <summary>Находит все подключённые контроллеры (включая совместимые устройства других производителей).</summary>
    public static List<ILedStrip> Discover(bool probeCompatible = true, bool scanBluetooth = true)
    {
        return LedDeviceRegistry.Discover(new DiscoveryOptions(probeCompatible, scanBluetooth));
    }

    public LedDeviceInfo? QueryInfo()
    {
        SafeFileHandle? h = null;
        IntPtr evt = IntPtr.Zero;
        try
        {
            h = NativeHid.CreateFile(Path,
                NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
                NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeHid.OPEN_EXISTING, NativeHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) return null;

            int reportLength = 65;
            int inputLength = 65;
            if (NativeHid.TryGetCaps(h, out var caps))
            {
                if (caps.OutputReportByteLength > 0) reportLength = caps.OutputReportByteLength;
                if (caps.InputReportByteLength > 0) inputLength = caps.InputReportByteLength;
            }

            evt = NativeHid.CreateIoEvent();
            if (evt == IntPtr.Zero) return null;

            var request = BuildReadInfoPacket(NextId());
            var report = new byte[reportLength];
            report[0] = 0;
            Array.Copy(request, 0, report, 1, Math.Min(request.Length, reportLength - 1));
            if (!NativeHid.Write(h, evt, report, report.Length, 800, out _, out _)) return null;

            var buffer = new byte[Math.Max(8, inputLength)];
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1400)
            {
                if (!NativeHid.Read(h, evt, buffer, buffer.Length, 300, out int read, out int err))
                {
                    if (err == NativeHid.ERROR_SEM_TIMEOUT) continue;
                    return null;
                }
                if (read < 6) continue;

                int p = IndexOfHeader(buffer, "RB", read);
                if (p < 0 || buffer[p + 4] != ActionReadDeviceInfo || read < p + 24) continue;

                string productId = $"{buffer[p + 5]:X2}{buffer[p + 6]:X2}{buffer[p + 7]:X2}".ToLowerInvariant();
                int displaySize = buffer[p + 8];
                int lamps = buffer[p + 11];
                string uuid = BitConverter.ToString(buffer, p + 12, 8).Replace("-", "").ToLowerInvariant();
                string firmware = $"{buffer[p + 21]}.{buffer[p + 22]}.{buffer[p + 23]}";
                return new LedDeviceInfo(productId, lamps, firmware, uuid, displaySize);
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (h != null)
            {
                if (!h.IsInvalid)
                {
                    try { NativeHid.CancelIoEx(h, IntPtr.Zero); } catch { }
                }
                try { h.Dispose(); } catch { }
            }
            NativeHid.CloseIoEvent(ref evt);
        }
    }

    public bool Open()
    {
        lock (_sync)
        {
            if (IsOpen) return true;
            LastError = null;
            try
            {
                _handle = NativeHid.CreateFile(Path,
                    NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
                    NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                    IntPtr.Zero, NativeHid.OPEN_EXISTING, NativeHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (_handle.IsInvalid)
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    LastError = err == 5
                        ? "устройство занято другой программой (закройте SyncLight)"
                        : $"не удалось открыть (код {err})";
                    CloseUnsafe();
                    return false;
                }

                if (NativeHid.TryGetCaps(_handle, out var caps))
                {
                    if (caps.OutputReportByteLength > 0) _reportLength = caps.OutputReportByteLength;
                    if (caps.InputReportByteLength > 0) _inputReportLength = caps.InputReportByteLength;
                }

                _readEvent = NativeHid.CreateIoEvent();
                _writeEvent = NativeHid.CreateIoEvent();
                if (_readEvent == IntPtr.Zero || _writeEvent == IntPtr.Zero)
                {
                    LastError = "не удалось создать событие ввода-вывода";
                    CloseUnsafe();
                    return false;
                }

                IsOpen = true;
                _lastRecords = null;
                _lastSendTicks = 0;
                _nextSendTicks = 0;

                // Поток-«сток»: постоянно вычитывает входные отчёты контроллера.
                _readerStop = false;
                _reader = new Thread(ReaderLoop)
                {
                    IsBackground = true,
                    Name = "SyncLedHidRead"
                };
                _reader.Start();

                // Фоновый поток записи, чтобы не блокировать SyncEngine
                _writerStop = false;
                _writer = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "SyncLedHidWrite"
                };
                _writer.Start();

                // Переводим контроллер в режим управления с ПК.
                TryWriteRaw(BuildOpenUrlPacket(0), throwOnError: false);
                return IsOpen;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CloseUnsafe();
                return false;
            }
        }
    }

    public void Close()
    {
        TaskCompletionSource<Exception?>? pendingOff;
        lock (_sync) pendingOff = _offCompletion;

        // SendOff ждёт физическую запись сам; это также защищает прямой вызов Close
        // от гонки с ещё выполняющимся выключением. Ошибка уже будет возвращена
        // вызывающему SendOff, поэтому закрытие всё равно должно освободить дескриптор.
        if (pendingOff != null)
        {
            try { WaitForOff(pendingOff); } catch { }
        }

        lock (_sync) CloseUnsafe();
    }

    private void CloseUnsafe()
    {
        IsOpen = false;
        _readerStop = true;
        _writerStop = true;
        _signal.Set();

        var pendingOff = _offCompletion;
        _offCompletion = null;
        pendingOff?.TrySetResult(new IOException("устройство закрыто до завершения выключения"));
        _nextAction = ActionType.None;
        _nextSyncColors = null;

        SafeFileHandle? h = _handle;
        if (h != null && !h.IsInvalid)
        {
            try { NativeHid.CancelIoEx(h, IntPtr.Zero); } catch { }
        }

        Thread? reader = _reader;
        _reader = null;
        if (reader != null && reader != Thread.CurrentThread)
        {
            try { reader.Join(1500); } catch { }
        }

        Thread? writer = _writer;
        _writer = null;
        if (writer != null && writer != Thread.CurrentThread)
        {
            try { writer.Join(1500); } catch { }
        }

        try { h?.Dispose(); } catch { }
        _handle = null;

        NativeHid.CloseIoEvent(ref _readEvent);
        NativeHid.CloseIoEvent(ref _writeEvent);

        _lastRecords = null;
        _nextSendTicks = 0;
    }

    private void ReaderLoop()
    {
        try
        {
            var buffer = new byte[Math.Max(8, _inputReportLength)];
            int errors = 0;
            while (!_readerStop)
            {
                SafeFileHandle? h = _handle;
                IntPtr evt = _readEvent;
                if (h == null || h.IsInvalid || evt == IntPtr.Zero) break;

                if (!NativeHid.Read(h, evt, buffer, buffer.Length, ReadTimeoutMs, out int read, out int err))
                {
                    if (_readerStop) break;
                    if (err == NativeHid.ERROR_SEM_TIMEOUT)
                    {
                        errors = 0; // устройство просто молчит — это нормально
                        continue;
                    }
                    if (err == NativeHid.ERROR_OPERATION_ABORTED ||
                        err == NativeHid.ERROR_INVALID_HANDLE ||
                        err == NativeHid.ERROR_DEVICE_NOT_CONNECTED)
                        break;

                    if (++errors >= 10) break;
                    Thread.Sleep(20);
                    continue;
                }

                errors = 0;
                if (read > 0) InputReports++;
            }
        }
        catch
        {
            // гонка с закрытием устройства — поток просто завершается
        }
    }

    private void WriterLoop()
    {
        while (!_writerStop)
        {
            _signal.WaitOne();
            if (_writerStop) break;

            ActionType action;
            Rgb staticCol;
            List<Rgb>? syncCols;
            TaskCompletionSource<Exception?>? offCompletion = null;

            lock (_sync)
            {
                action = _nextAction;
                staticCol = _nextStaticColor;
                syncCols = _nextSyncColors;
                if (action == ActionType.Off) offCompletion = _offCompletion;
                _nextAction = ActionType.None;
                _nextSyncColors = null;
            }

            try
            {
                switch (action)
                {
                    case ActionType.Static:
                        ExecuteSendStatic(staticCol);
                        break;
                    case ActionType.Sync:
                        if (syncCols != null) ExecuteSendColors(syncCols);
                        break;
                    case ActionType.Off:
                        ExecuteSendOff();
                        break;
                }
                if (offCompletion != null) CompleteOff(offCompletion, null);
            }
            catch (Exception ex)
            {
                if (offCompletion != null) CompleteOff(offCompletion, ex);
                LastError = ex.Message;
                Interlocked.Increment(ref _statErrors);
                if (IsOpen)
                {
                    lock (_sync) CloseUnsafe();
                }
            }
        }
    }

    private void ExecuteSendStatic(Rgb color)
    {
        int n = Math.Clamp(LampCount, 1, 254);
        var parts = new List<(int Start, Rgb Color, int End)>(2)
        {
            (1, color, n)
        };
        if (n < 254) parts.Add((n + 1, Rgb.Black, 254));

        byte[] packet = BuildSectionPacket(NextId(), parts);
        SendSignedInternal(packet, parts, repeatAfterMs: 3000, isSync: false);
    }

    private void ExecuteSendOff()
    {
        int n = Math.Clamp(LampCount, 1, 254);
        var parts = new List<(int Start, Rgb Color, int End)>(2)
        {
            (1, Rgb.Black, n)
        };
        if (n < 254) parts.Add((n + 1, Rgb.Black, 254));

        byte[] packet = BuildSectionPacket(NextId(), parts);
        SendSignedInternal(packet, parts, repeatAfterMs: 0, isSync: false, bypassRateLimit: true);
    }

    private void ExecuteSendColors(List<Rgb> colors)
    {
        if (colors.Count == 0) return;

        var records = new List<(int Start, Rgb Color, int End)>();
        int start = 0;
        for (int i = 1; i <= colors.Count; i++)
        {
            if (i < colors.Count && colors[i] == colors[start]) continue;
            records.Add((start + 1, colors[start], i));
            start = i;
        }

        records = MergeRecords(records, MaxRecordsPerFrame);
        byte[] packet = BuildSyncPacket(NextId(), records);
        SendSignedInternal(packet, records, repeatAfterMs: 2000, isSync: true);
    }

    /// <summary>Внутренняя реализация отправки, предназначенная для вызова из WriterLoop.</summary>
    private void SendSignedInternal(byte[] packet, List<(int Start, Rgb Color, int End)> signature,
        int repeatAfterMs, bool isSync, bool bypassRateLimit = false)
    {
        long now = Environment.TickCount64;
        if (!bypassRateLimit && _lastRecords != null && _lastSendTicks != 0
            && now - _lastSendTicks < repeatAfterMs
            && SameRecords(_lastRecords, signature))
            return;

        if (!bypassRateLimit && now < _nextSendTicks)
        {
            // Не выбрасываем кадр: он уже собран, а очередь writer-потока
            // коалесцирует новые цвета. Короткое ожидание убирает мерцание
            // из-за периодических пропусков на границе интервала.
            int wait = (int)Math.Min(250, _nextSendTicks - now);
            if (wait <= 0) return;
            Thread.Sleep(wait);
            if (_writerStop) return;
            now = Environment.TickCount64;
        }

        int chunks = Math.Max(1, (packet.Length + MaxChunk - 1) / MaxChunk);
        int wireBytes = chunks * 65;

        if (WritePacketUnsafe(packet, throwOnError: true))
        {
            Interlocked.Increment(ref _statSent);
            Interlocked.Add(ref _statBytes, wireBytes);
            _lastRecords = signature;
            _lastSendTicks = now;
            _nextSendTicks = bypassRateLimit ? now : now + MinIntervalMs(packet.Length, isSync);
        }
    }

    public void SendSegment(int start, int end, Rgb color)
    {
        lock (_sync)
        {
            var parts = new List<(int Start, Rgb Color, int End)> { (start, color, end) };
            byte[] packet = BuildSectionPacket(NextId(), parts);
            TryWriteRaw(packet, throwOnError: true);
        }
    }

    public void SendStatic(Rgb color)
    {
        lock (_sync)
        {
            _nextAction = ActionType.Static;
            _nextStaticColor = color;
            _signal.Set();
        }
    }

    public void SendOff()
    {
        TaskCompletionSource<Exception?> completion;
        lock (_sync)
        {
            if (!IsOpen) throw new IOException("устройство не открыто");

            completion = _offCompletion!;
            if (completion == null || completion.Task.IsCompleted)
            {
                completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _offCompletion = completion;
            }

            // Off заменяет ещё не отправленный цвет: после подтверждённого
            // выключения старый кадр не должен снова включить ленту.
            _nextAction = ActionType.Off;
            _nextSyncColors = null;
            _signal.Set();
        }

        WaitForOff(completion);
    }

    private static void WaitForOff(TaskCompletionSource<Exception?> completion)
    {
        if (!completion.Task.Wait(OffWaitTimeoutMs))
            throw new TimeoutException("истекло ожидание подтверждения выключения ленты");

        Exception? error = completion.Task.GetAwaiter().GetResult();
        if (error != null) throw new IOException("не удалось выключить ленту: " + error.Message, error);
    }

    private void CompleteOff(TaskCompletionSource<Exception?> completion, Exception? error)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_offCompletion, completion)) _offCompletion = null;
        }
        completion.TrySetResult(error);
    }

    public StripStats GetStats() => new(
        Interlocked.Read(ref _statSent),
        Interlocked.Read(ref _statSkipped),
        Interlocked.Read(ref _statBytes),
        Interlocked.Read(ref _statErrors));

    public void SendColors(IReadOnlyList<Rgb> colors)
    {
        lock (_sync)
        {
            _nextAction = ActionType.Sync;
            _nextSyncColors = new List<Rgb>(colors);
            _signal.Set();
        }
    }

    private void TryWriteRaw(byte[] packet, bool throwOnError)
    {
        if (!IsOpen || _handle == null || _handle.IsInvalid || _writeEvent == IntPtr.Zero)
        {
            LastError = "устройство не открыто";
            if (throwOnError) throw new IOException(LastError);
            return;
        }

        try
        {
            for (int offset = 0; offset < packet.Length; offset += MaxChunk)
            {
                int count = Math.Min(MaxChunk, packet.Length - offset);
                var report = new byte[_reportLength];
                report[0] = 0; // report id
                Array.Copy(packet, offset, report, 1, count);
                if (!NativeHid.Write(_handle, _writeEvent, report, report.Length, WriteTimeoutMs, out _, out int err))
                {
                    throw new IOException(err == NativeHid.ERROR_SEM_TIMEOUT
                        ? "контроллер не принимает данные (переподключите USB-кабель)"
                        : $"WriteFile failed, код {err}");
                }
            }
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Interlocked.Increment(ref _statErrors);
            if (throwOnError) throw;
        }
    }

    private static List<(int Start, Rgb Color, int End)> MergeRecords(
        List<(int Start, Rgb Color, int End)> records, int maxRecords)
    {
        if (records.Count <= maxRecords) return records;

        var work = new List<(int Start, Rgb Color, int End)>(records);
        while (work.Count > maxRecords)
        {
            int best = 0;
            long bestDistance = long.MaxValue;
            for (int i = 0; i + 1 < work.Count; i++)
            {
                long d = ColorDistance(work[i].Color, work[i + 1].Color);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }

            var a = work[best];
            var b = work[best + 1];
            int lenA = a.End - a.Start + 1;
            int lenB = b.End - b.Start + 1;
            long total = lenA + lenB;
            var blended = new Rgb(
                (byte)((a.Color.R * (long)lenA + b.Color.R * (long)lenB) / total),
                (byte)((a.Color.G * (long)lenA + b.Color.G * (long)lenB) / total),
                (byte)((a.Color.B * (long)lenA + b.Color.B * (long)lenB) / total));

            work[best] = (a.Start, blended, b.End);
            work.RemoveAt(best + 1);
        }
        return work;
    }

    private static long ColorDistance(Rgb a, Rgb b)
    {
        long dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }

    private static bool SameRecords(List<(int Start, Rgb Color, int End)> a, List<(int Start, Rgb Color, int End)> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Start != b[i].Start || a[i].End != b[i].End || a[i].Color != b[i].Color) return false;
        }
        return true;
    }

    private byte NextId()
    {
        unchecked
        {
            _messageId++;
            if (_messageId == 0 || _messageId >= 255) _messageId = 1;
        }
        return _messageId;
    }

    private static byte Checksum(byte[] data, int count)
    {
        int sum = 0;
        for (int i = 0; i < count; i++) sum += data[i];
        return (byte)(sum & 0xFF);
    }

    private static byte[] BuildReadInfoPacket(byte id)
    {
        var p = new byte[6];
        p[0] = (byte)'R'; p[1] = (byte)'B'; p[2] = 6; p[3] = id; p[4] = ActionReadDeviceInfo;
        p[5] = Checksum(p, 5);
        return p;
    }

    private static byte[] BuildOpenUrlPacket(byte value)
    {
        var p = new byte[7];
        p[0] = (byte)'R'; p[1] = (byte)'B'; p[2] = 7; p[3] = 1; p[4] = ActionSetOpenUrl; p[5] = value;
        p[6] = Checksum(p, 6);
        return p;
    }

    private static byte[] BuildSectionPacket(byte id, IReadOnlyList<(int Start, Rgb Color, int End)> segments)
    {
        int n = Math.Max(1, segments.Count);
        var p = new byte[6 + n * 5];
        p[0] = (byte)'R'; p[1] = (byte)'B'; p[2] = (byte)p.Length; p[3] = id; p[4] = ActionSetSectionLed;

        for (int i = 0; i < segments.Count; i++)
        {
            int o = 5 + i * 5;
            var s = segments[i];
            p[o + 0] = (byte)Math.Clamp(s.Start, 1, 254);
            p[o + 1] = s.Color.R;
            p[o + 2] = s.Color.G;
            p[o + 3] = s.Color.B;
            p[o + 4] = (byte)Math.Clamp(s.End, 1, 254);
        }
        p[^1] = Checksum(p, p.Length - 1);
        return p;
    }

    private static byte[] BuildSyncPacket(byte id, IReadOnlyList<(int Start, Rgb Color, int End)> records)
    {
        int n = records.Count;
        var p = new byte[7 + n * 5];
        p[0] = (byte)'S'; p[1] = (byte)'C';
        p[2] = (byte)(p.Length >> 8);
        p[3] = (byte)(p.Length & 0xFF);
        p[4] = id;
        p[5] = ActionSetSyncScreen;

        for (int i = 0; i < n; i++)
        {
            int o = 6 + i * 5;
            var r = records[i];
            p[o + 0] = (byte)Math.Clamp(r.Start, 1, 254);
            p[o + 1] = r.Color.R;
            p[o + 2] = r.Color.G;
            p[o + 3] = r.Color.B;
            p[o + 4] = (byte)Math.Clamp(r.End, 1, 254);
        }
        p[^1] = Checksum(p, p.Length - 1);
        return p;
    }

    private static int IndexOfHeader(byte[] data, string header, int length)
    {
        for (int i = 0; i + 1 < Math.Min(length, data.Length); i++)
        {
            if (data[i] == header[0] && data[i + 1] == header[1]) return i;
        }
        return -1;
    }

    private static int MinIntervalMs(int packetLength, bool isSync)
    {
        int chunks = Math.Max(1, (packetLength + MaxChunk - 1) / MaxChunk);
        int wireBytes = chunks * 65;
        int ms = (int)Math.Ceiling(wireBytes * 1000.0 / MaxBytesPerSecond);
        if (!isSync) ms = Math.Max(ms, 200);
        return ms;
    }

    private bool WritePacketUnsafe(byte[] packet, bool throwOnError)
    {
        if (!IsOpen || _handle == null || _handle.IsInvalid || _writeEvent == IntPtr.Zero)
        {
            LastError = "устройство не открыто";
            if (throwOnError) throw new IOException(LastError);
            return false;
        }

        try
        {
            for (int offset = 0; offset < packet.Length; offset += MaxChunk)
            {
                int count = Math.Min(MaxChunk, packet.Length - offset);
                var report = new byte[_reportLength];
                report[0] = 0; // report id
                Array.Copy(packet, offset, report, 1, count);
                if (!NativeHid.Write(_handle, _writeEvent, report, report.Length, WriteTimeoutMs, out _, out int err))
                {
                    throw new IOException(err == NativeHid.ERROR_SEM_TIMEOUT
                        ? "контроллер не принимает данные (переподключите USB-кабель)"
                        : $"WriteFile failed, код {err}");
                }
            }
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Interlocked.Increment(ref _statErrors);
            if (throwOnError) throw;
            return false;
        }
    }

    private static string ShortPathHash(string path)
    {
        uint hash = 2166136261;
        foreach (char c in path.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }
        return (hash & 0xFFFF).ToString("x4");
    }

    public void Dispose() => Close();
}