using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace SyncLed;

/// <summary>
/// Bluetooth LE лента на контроллере FTY/CCHIP (сервис 0xFF01, запись в 0xFF03,
/// уведомления в 0xFF02).
///
/// Формат пакета: [0xA0, команда, длина данных + 3, данные…, CRC16 lo, CRC16 hi].
/// CRC16 — Modbus (полином 0xA001, начальное значение 0xFFFF) по всем байтам,
/// кроме двух последних.
///
/// Перед командами обязательно рукопожатие «CCHIP»:
///   1. A0 00 08 'C''C''H''I''P' crc
///   2. контроллер отвечает A1 00 … 'C''C''H''I''P' XX YY crc
///   3. A0 01 0A 'C''C''H''I''P' (XX^0x24) (YY^0x4C) crc
///   4. контроллер отвечает A1 01 … 00 crc — можно управлять.
///
/// Команды: 0x11 включить (1/0), 0x12 режим (1,1 = статический цвет),
/// 0x13 яркость (0…100), 0x15 цвет R G B 0xFF.
/// </summary>
public sealed class BleLedStrip : ILedStrip
{
    /// <summary>Значение ProductId в конфиге — по нему находим Bluetooth-записи.</summary>
    public const string ProductIdValue = "FTY BLE";

    private const byte Header = 0xA0;
    private const byte CmdShakeHands = 0x00;
    private const byte CmdShakeHandsConfirm = 0x01;
    private const byte CmdPower = 0x11;
    private const byte CmdMode = 0x12;
    private const byte CmdBrightness = 0x13;
    private const byte CmdColor = 0x15;
    private const byte CmdStatus = 0x10;

    private const int StatusPollMs = 4000;

    private const byte ModeColor = 0x01;
    private const byte CustomColorFlag = 0xFF;

    /// <summary>Минимальный интервал между пакетами цвета (~40 кадров в секунду).</summary>
    private const int SendIntervalMs = 25;

    private const int HandshakeTimeoutMs = 2000;
    private const int ReopenDelayMs = 3000;
    private const int MaxPendingFrames = 32;

    private readonly object _sync = new();
    private readonly List<byte[]> _inbox = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly BluetoothAddressType _addressType;

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private volatile bool _isOpen;
    private volatile bool _connecting;
    private int _generation;
    private long _nextSendTicks;
    private long _lastOpenAttemptTicks = -ReopenDelayMs;
    private Rgb _lastSent;
    private bool _hasSent;
    private int _appliedBrightness = -1;
    private volatile bool _stripOff;
    private long _nextPowerOnTicks;
    private long _nextStatusTicks;
    private int _powerOnFailures;

    // Диагностика обмена (кадры цвета, пропуски ограничителя, байты, ошибки).
    private long _statSent;
    private long _statSkipped;
    private long _statBytes;
    private long _statErrors;

    /// <summary>
    /// Аппаратная яркость контроллера (0…100). На дешёвых контроллерах при
    /// яркости выше ~30 срабатывает защита по току: лента вспыхивает и гаснет
    /// через секунду. Поэтому по умолчанию ограничиваемся безопасным уровнем.
    /// </summary>
    public int HardwareBrightness { get; set; } = 20;

    public BleLedStrip(ulong address, BluetoothAddressType addressType, string name)
    {
        Address = address;
        _addressType = addressType;
        Uuid = address.ToString("X12");
        Path = "ble:" + Uuid;
        ProductName = string.IsNullOrWhiteSpace(name) ? "BLE-лента" : name.Trim();
    }

    public ulong Address { get; }
    public string ProductName { get; }

    public string Transport => "Bluetooth";
    public string Path { get; }
    public string PathHash => ShortPathHash(Path);
    public string Key => Uuid;
    public string Uuid { get; }
    public string ProductId { get; internal set; } = ProductIdValue;
    public string Firmware { get; internal set; } = "?";
    public string Manufacturer => "Bluetooth LE";
    public ushort Vid => 0;
    public ushort Pid => 0;
    public int LampCount { get; set; } = 96;

    public string Title => $"{ProductName} · {LampCount} LED";
    public string Detail => $"Bluetooth LE · {Uuid} · {LampCount} LED";

    public bool IsOpen => _isOpen;
    public string? LastError { get; internal set; }

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

    /// <summary>
    /// Запускает подключение в фоне и сразу возвращает текущее состояние:
    /// медленный поиск недоступной ленты не должен задерживать кадры остальных лент.
    /// </summary>
    public bool Open()
    {
        if (_isOpen) return true;
        if (_connecting) return false;

        long now = _clock.ElapsedMilliseconds;
        if (now - _lastOpenAttemptTicks < ReopenDelayMs) return false;
        _lastOpenAttemptTicks = now;

        _connecting = true;
        int generation = Volatile.Read(ref _generation);
        _ = Task.Run(() => ConnectCore(generation));
        return false;
    }

    private void ConnectCore(int generation)
    {
        BluetoothLEDevice? device = null;
        GattCharacteristic? write = null;
        GattCharacteristic? notify = null;
        try
        {
            device = BluetoothLEDevice
                .FromBluetoothAddressAsync(Address, _addressType).AsTask().Result;
            if (device == null)
            {
                Fail("устройство не найдено");
                return;
            }

            var services = device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().Result;
            if (services.Status != GattCommunicationStatus.Success)
            {
                Fail("сервисы недоступны: " + services.Status);
                return;
            }

            foreach (var service in services.Services)
            {
                var chars = service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().Result;
                if (chars.Status != GattCommunicationStatus.Success) continue;
                foreach (var c in chars.Characteristics)
                {
                    string uuid = c.Uuid.ToString();
                    if (uuid.StartsWith("0000ff03", StringComparison.OrdinalIgnoreCase)) write = c;
                    else if (uuid.StartsWith("0000ff02", StringComparison.OrdinalIgnoreCase)) notify = c;
                }
            }

            if (write == null)
            {
                Fail("не найдена характеристика записи 0xFF03");
                return;
            }

            if (notify != null)
            {
                notify.ValueChanged += OnNotify;
                var status = notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().Result;
                if (status != GattCommunicationStatus.Success)
                {
                    notify.ValueChanged -= OnNotify;
                    notify = null;
                    Fail("не удалось включить уведомления: " + status);
                    return;
                }
            }

            lock (_inbox) _inbox.Clear();
            if (!Handshake(write))
            {
                Fail("контроллер не подтвердил рукопожатие");
                return;
            }

            // Close мог отменить это подключение, пока выполнялось рукопожатие.
            // Не посылаем power-on от уже недействительного поколения.
            if (generation != Volatile.Read(ref _generation)) return;

            // Лента могла быть выключена предыдущим сеансом: включаем и
            // переводим в режим статического цвета.
            WriteRaw(write, Pack(CmdPower, 0x01));
            Thread.Sleep(60);
            if (generation != Volatile.Read(ref _generation))
            {
                SendBlackAfterCancel(write);
                return;
            }
            WriteRaw(write, Pack(CmdMode, ModeColor, 0x01));
            Thread.Sleep(60);
            if (generation != Volatile.Read(ref _generation))
            {
                SendBlackAfterCancel(write);
                return;
            }

            int brightness = Math.Clamp(HardwareBrightness, 1, 100);
            WriteRaw(write, Pack(CmdBrightness, (byte)brightness));
            Thread.Sleep(60);
            if (generation != Volatile.Read(ref _generation))
            {
                SendBlackAfterCancel(write);
                return;
            }

            long now = _clock.ElapsedMilliseconds;

            lock (_sync)
            {
                if (generation != _generation)
                {
                    // Пока подключались, устройство закрыли (обновление списка, выход).
                    if (notify != null) notify.ValueChanged -= OnNotify;
                    SendBlackAfterCancel(write);
                    return;
                }
                _device = device;
                _writeChar = write;
                _notifyChar = notify;
                _hasSent = false;
                _appliedBrightness = brightness;
                _stripOff = false;
                _powerOnFailures = 0;
                _nextPowerOnTicks = 0;
                _nextStatusTicks = now + StatusPollMs;
                LastError = null;
                _isOpen = true;
                device = null; // владение перешло ленте
            }
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            try { device?.Dispose(); } catch { }
            _connecting = false;
        }
    }

    private static void SendBlackAfterCancel(GattCharacteristic write)
    {
        // Отмена могла прийти сразу после power-on: не оставляем ленту гореть
        // при закрытии недействительного подключения.
        try { WriteRaw(write, Pack(CmdColor, 0, 0, 0, CustomColorFlag)); } catch { }
    }

    private void Fail(string message)
    {
        LastError = message;
        _isOpen = false;
    }

    public void Close()
    {
        lock (_sync) CloseCore();
    }

    private void CloseCore()
    {
        _generation++;
        if (_notifyChar != null)
        {
            try { _notifyChar.ValueChanged -= OnNotify; } catch { }
            _notifyChar = null;
        }
        try { _device?.Dispose(); } catch { }
        _device = null;
        _writeChar = null;
        _appliedBrightness = -1;
        _stripOff = false;
        _powerOnFailures = 0;
        _nextPowerOnTicks = 0;
        _nextStatusTicks = 0;
        lock (_inbox) _inbox.Clear();
        _isOpen = false;
        _lastOpenAttemptTicks = -ReopenDelayMs;
    }

    public void Dispose() => Close();

    public void SendColors(IReadOnlyList<Rgb> colors)
    {
        if (colors.Count == 0) return;
        SendColor(Blend(colors), force: false);
    }

    public void SendStatic(Rgb color) => SendColor(color, force: true);

    public void SendOff()
    {
        if (!_isOpen) return;
        if (!SendColor(Rgb.Black, force: true))
            throw new IOException(LastError ?? "не удалось выключить Bluetooth-ленту");
    }

    public StripStats GetStats() => new(
        Interlocked.Read(ref _statSent),
        Interlocked.Read(ref _statSkipped),
        Interlocked.Read(ref _statBytes),
        Interlocked.Read(ref _statErrors));

    /// <summary>
    /// Лента неадресуемая: сводим цвета всех ламп к одному. Вес цвета — его
    /// яркость, чтобы тёмный фон не «съедал» насыщенность картинки.
    /// </summary>
    private static Rgb Blend(IReadOnlyList<Rgb> colors)
    {
        double r = 0, g = 0, b = 0, wsum = 0;
        foreach (var c in colors)
        {
            double weight = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0 + 0.02;
            r += c.R * weight;
            g += c.G * weight;
            b += c.B * weight;
            wsum += weight;
        }
        if (wsum <= 0) return Rgb.Black;
        return new Rgb(
            (byte)Math.Clamp((int)Math.Round(r / wsum), 0, 255),
            (byte)Math.Clamp((int)Math.Round(g / wsum), 0, 255),
            (byte)Math.Clamp((int)Math.Round(b / wsum), 0, 255));
    }

    private bool SendColor(Rgb color, bool force)
    {
        if (!_isOpen) return false;

        long now = _clock.ElapsedMilliseconds;
        if (!force)
        {
            if (now < _nextSendTicks)
            {
                Interlocked.Increment(ref _statSkipped);
                return false;
            }
            if (_hasSent && color == _lastSent) return false;
        }

        lock (_sync)
        {
            if (!_isOpen || _writeChar == null) return false;
            _nextSendTicks = now + SendIntervalMs;
            long stamp = _clock.ElapsedMilliseconds;

            // Раз в несколько секунд спрашиваем состояние контроллера.
            if (stamp >= _nextStatusTicks)
            {
                _nextStatusTicks = stamp + StatusPollMs;
                WriteRaw(_writeChar, Pack(CmdStatus, 0x01));
            }

            // Контроллер ушёл в защиту по току (onOff = 0): повторяем включение
            // с нарастающей паузой, чтобы не устраивать мигание каждую секунду.
            if (_stripOff && color.Max > 0 && stamp >= _nextPowerOnTicks)
            {
                WriteRaw(_writeChar, Pack(CmdPower, 0x01));
                WriteRaw(_writeChar, Pack(CmdMode, ModeColor, 0x01));
                _appliedBrightness = -1;
                _powerOnFailures = Math.Min(5, _powerOnFailures + 1);
                _nextPowerOnTicks = stamp + (1500L << Math.Min(4, _powerOnFailures - 1));
            }

            // Пользователь мог изменить яркость в настройках на лету.
            int brightness = Math.Clamp(HardwareBrightness, 1, 100);
            if (brightness != _appliedBrightness)
            {
                if (WriteRaw(_writeChar, Pack(CmdBrightness, (byte)brightness)))
                    _appliedBrightness = brightness;
            }

            if (!WriteRaw(_writeChar, Pack(CmdColor, color.R, color.G, color.B, CustomColorFlag)))
            {
                // Связь потеряна: движок увидит IsOpen == false и переподключится.
                LastError = "нет связи";
                Interlocked.Increment(ref _statErrors);
                _isOpen = false;
                return false;
            }
            Interlocked.Increment(ref _statSent);
            Interlocked.Add(ref _statBytes, 9);
            _lastSent = color;
            _hasSent = true;
            return true;
        }
    }

    private bool Handshake(GattCharacteristic write)
    {
        lock (_inbox) _inbox.Clear();
        if (!WriteRaw(write, Pack(CmdShakeHands, 0x43, 0x43, 0x48, 0x49, 0x50))) return false;

        var hello = WaitFor(CmdShakeHands, HandshakeTimeoutMs);
        if (hello == null) return false;
        var data = DataOf(hello);
        if (data.Length < 7) return false;

        byte a = (byte)(data[5] ^ 0x24);
        byte b = (byte)(data[6] ^ 0x4C);
        if (!WriteRaw(write, Pack(CmdShakeHandsConfirm, 0x43, 0x43, 0x48, 0x49, 0x50, a, b))) return false;

        var ack = WaitFor(CmdShakeHandsConfirm, HandshakeTimeoutMs);
        if (ack == null) return false;
        var ackData = DataOf(ack);
        return ackData.Length >= 6 && ackData[5] == 0;
    }

    private void OnNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] frame = args.CharacteristicValue.ToArray();

        // Ответ на запрос состояния: data[0] — включена ли лента.
        if (frame.Length >= 5 && frame[0] == 0xA1 && frame[1] == CmdStatus)
        {
            var state = DataOf(frame);
            if (state.Length > 0)
            {
                _stripOff = state[0] == 0;
                if (!_stripOff)
                {
                    _powerOnFailures = 0;
                    _nextPowerOnTicks = 0;
                }
            }
            return;
        }

        lock (_inbox)
        {
            if (_inbox.Count >= MaxPendingFrames) _inbox.RemoveAt(0);
            _inbox.Add(frame);
        }
    }

    private byte[]? WaitFor(byte command, int timeoutMs)
    {
        long deadline = _clock.ElapsedMilliseconds + timeoutMs;
        while (_clock.ElapsedMilliseconds < deadline)
        {
            lock (_inbox)
            {
                for (int i = 0; i < _inbox.Count; i++)
                {
                    var frame = _inbox[i];
                    if (frame.Length >= 5 && frame[0] == 0xA1 && frame[1] == command)
                    {
                        _inbox.RemoveAt(i);
                        return frame;
                    }
                }
            }
            Thread.Sleep(20);
        }
        return null;
    }

    /// <summary>Данные кадра без заголовка и CRC16.</summary>
    private static byte[] DataOf(byte[] frame)
    {
        int length = frame.Length >= 3 ? frame[2] - 3 : 0;
        length = Math.Clamp(length, 0, Math.Max(0, frame.Length - 5));
        var data = new byte[length];
        Array.Copy(frame, 3, data, 0, length);
        return data;
    }

    private static bool WriteRaw(GattCharacteristic characteristic, byte[] packet)
    {
        try
        {
            var status = characteristic
                .WriteValueAsync(packet.AsBuffer(), GattWriteOption.WriteWithoutResponse)
                .AsTask().Result;
            return status == GattCommunicationStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Pack(byte command, params byte[] args)
    {
        var packet = new byte[args.Length + 5];
        packet[0] = Header;
        packet[1] = command;
        packet[2] = (byte)(args.Length + 3);
        Array.Copy(args, 0, packet, 3, args.Length);

        ushort crc = Crc16(packet, packet.Length - 2);
        packet[args.Length + 3] = (byte)(crc & 0xFF);
        packet[args.Length + 4] = (byte)(crc >> 8);
        return packet;
    }

    /// <summary>CRC-16 Modbus: полином 0xA001, начальное значение 0xFFFF.</summary>
    private static ushort Crc16(byte[] data, int count)
    {
        int crc = 0xFFFF;
        for (int i = 0; i < count; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return (ushort)crc;
    }
}

/// <summary>
/// Поиск BLE-лент семейства FTY/CCHIP: устройство должно объявлять сервис 0xFF01.
/// </summary>
public sealed class BleLedProvider : ILedDeviceProvider
{
    private static readonly Guid FtService = new("0000ff01-0000-1000-8000-00805f9b34fb");
    private const int ScanMs = 3500;

    public string Name => "FTY BLE";

    public List<ILedStrip> Discover(DiscoveryOptions options)
    {
        var result = new List<ILedStrip>();
        if (!options.ScanBluetooth) return result;

        var found = new Dictionary<ulong, (BluetoothAddressType Type, string Name, short Rssi)>();
        BluetoothLEAdvertisementWatcher? watcher = null;
        try
        {
            watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            watcher.Received += (_, e) =>
            {
                bool hasService = false;
                foreach (var uuid in e.Advertisement.ServiceUuids)
                {
                    if (uuid == FtService) { hasService = true; break; }
                }
                if (!hasService) return;

                lock (found)
                {
                    if (!found.ContainsKey(e.BluetoothAddress))
                        found[e.BluetoothAddress] = (e.BluetoothAddressType, e.Advertisement.LocalName, e.RawSignalStrengthInDBm);
                }
            };
            watcher.Start();
        }
        catch
        {
            // Нет Bluetooth-адаптера или выключен радио-модуль.
            return result;
        }

        try
        {
            Thread.Sleep(ScanMs);
        }
        finally
        {
            try { watcher.Stop(); } catch { }
            watcher = null;
        }

        lock (found)
        {
            foreach (var pair in found.OrderByDescending(p => p.Value.Rssi))
                result.Add(new BleLedStrip(pair.Key, pair.Value.Type, pair.Value.Name));
        }
        return result;
    }
}
