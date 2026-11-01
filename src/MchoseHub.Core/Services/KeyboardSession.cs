using MchoseHub.Core.Protocol;
using MchoseHub.Core.Transport;

namespace MchoseHub.Core.Services;

/// <summary>
/// 键盘会话：走 feature report 通道。与音箱/耳机那套 output report 协议完全不同。
/// </summary>
public sealed class KeyboardSession : IDisposable
{
    private readonly IHidSession _session;
    private readonly Action<string>? _log;
    private readonly object _idGate = new();
    private readonly KeyboardWirelessChannel? _wireless;
    private readonly byte _wiredReportId;
    private string? _wirelessModel;

    /// <summary>
    /// 重试前的退避基数（毫秒）。残留缓冲区本质是「设备还没把上一次的活干完」，
    /// 紧接着重发多半还是撞上同一份残留；隔几十毫秒再来，命中率明显更高。
    /// </summary>
    private const int RetryBackoffMs = 120;

    /// <summary>
    /// 一条事务最多发几次。第 4 次是留给「刚打开句柄、设备还在恢复期」的：
    /// 那会儿设备常常把上一次（探测）的缓冲区原样丢回来，多给一轮就能过去。
    /// </summary>
    private const int MaxAttempts = 4;

    /// <summary>
    /// 按设备型号选择有线报告 ID，接收器使用独立的无线 output/input 通道。
    /// </summary>

    public KeyboardSession(IHidSession session, Action<string>? log = null)
    {
        _session = session;
        _log = log;
        if (LooksLikeWirelessChannel(session.Device)) _wireless = new KeyboardWirelessChannel(session, log);
        _wiredReportId = WiredReportIdFor(session.Device.ProductName);
    }

    public HidDeviceInfo Device => _session.Device;
    public byte ReportId { get; private set; }
    public bool IsReportIdResolved { get; private set; }
    public bool IsWireless => _wireless is not null;

    public string ResolveWirelessModelKey() => Execute(() => _wirelessModel ??= _wireless?.ResolveModel()
        ?? throw new InvalidOperationException("当前设备是有线键盘。"));
    internal void SetModel(KeyboardModelSchema model)
    {
        if (_wireless is not null)
        {
            if (ResolveWirelessModelKey() != model.Model) throw new InvalidDataException("无线接收器的型号标识与配置布局不匹配。");
            _wireless.Model = model;
        }
    }
    internal void WriteWirelessMacroBank(byte[] bank) => Execute(() =>
    {
        if (_wireless is null) throw new InvalidOperationException("当前设备是有线键盘。");
        WaitForWriteSlot(); _wireless.Write(3, bank); return true;
    });

    private static byte WiredReportIdFor(string? name)
    {
        try
        {
            return KeyboardSettingsSession.ResolveModelKey(name) is "G98_Pro" or "G98_V2" or "UT98" or "Z75" or "Z75S" or "G87_V2"
                ? (byte)9 : KeyboardProtocol.ReportId;
        }
        catch (ArgumentException) { return KeyboardProtocol.ReportId; }
    }

    /// <summary>
    /// 底层句柄是否还开着。看门狗用它判断「设备是不是已经掉了」，
    /// 注意它只反映 Windows 这边的句柄状态：USB 被拔掉时句柄不一定立刻失效，
    /// 所以还要配合「连续两次读不到应答」一起判断。
    /// </summary>
    public bool IsOpen => _session.IsOpen;

    /// <summary>配置通道没准备好时抛这个，UI 直接显示 Message 就行。</summary>
    public sealed class NotReadyException(string message) : InvalidOperationException(message);

    /// <summary>
    /// 探测的退避节奏（毫秒）。设备刚被上一个句柄放开时，键盘固件会有
    /// 1～3 秒的恢复期，这段时间它会对任何 feature 请求回 Win32 31（设备无响应）。
    /// 实测「打开→关闭→再打开」到第 4 次就会撞上，硬冲只会越冲越糟，必须等。
    /// </summary>
    private static readonly int[] ProbeBackoffMs = [150, 300, 600, 1000, 1800, 2800];

    /// <summary>
    /// 确定 report id。会重试：设备刚打开时第一笔事务常常落空，
    /// 只试一次会把好设备判成连不上（实测第二次打开就踩过这个坑）。
    /// </summary>
    public byte? ResolveReportId()
    {
        lock (_idGate)
        {
            if (IsReportIdResolved)
            {
                return ReportId;
            }

            if (!_session.IsOpen)
            {
                _log?.Invoke("配置通道已经关闭，无法探测 report id");
                return null;
            }

            var battery = KeyboardProtocol.FindReadCommand("getBattery")
                          ?? throw new NotReadyException("缺少 getBattery 命令定义。");
            var payload = KeyboardProtocol.BuildReadPayload(battery);
            if (_wireless is not null)
            {
                _wireless.Transact(payload);
                return Accept(KeyboardWirelessProtocol.ReportId);
            }

            // 第一轮：候选 id 各试 3 次
            if (ProbeCandidates(payload, attempts: 3))
            {
                return ReportId;
            }

            // 后面几轮：退避等待，让设备把上一笔会话收干净
            foreach (int delay in ProbeBackoffMs)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return null;
                }

                Thread.Sleep(delay);
                _log?.Invoke($"设备还没应答，{delay}ms 后重试");
                if (ProbeCandidates(payload, attempts: 2))
                {
                    return ReportId;
                }
            }

            InvalidateReportId();
            _log?.Invoke("配置通道没有应答：report id 未能确定");
            return null;
        }
    }

    /// <summary>按「上次成功的 → 常见候选」的顺序试一遍。</summary>
    private bool ProbeCandidates(byte[] payload, int attempts)
    {
        byte[] candidates = [_wiredReportId];

        foreach (byte candidate in candidates)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            if (Probe(candidate, payload, quiet: false, attempts: attempts))
            {
                Accept(candidate);
                return true;
            }
        }

        return false;
    }

    private byte Accept(byte id)
    {
        ReportId = id;
        IsReportIdResolved = true;
        _log?.Invoke($"键盘 report id = 0x{id:X2}");
        return id;
    }

    /// <summary>
    /// 用一个已知会回显命令头的读命令来验证候选 report id。
    /// 判定标准是「回包把命令头原样回显」，只看有没有非零字节会误判。
    /// </summary>
    private bool Probe(byte candidate, byte[] payload, bool quiet, int attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            try
            {
                _session.SendFeature(candidate, payload);
                var response = _session.GetFeature(candidate, KeyboardProtocol.WiredPayloadLength);
                if (LooksLikeEcho(response, payload))
                {
                    return true;
                }

                if (!quiet && attempt == attempts - 1)
                {
                    _log?.Invoke($"键盘 report id 0x{candidate:X2} 没有回显命令头");
                }
            }
            catch (Exception ex)
            {
                if (!quiet && attempt == attempts - 1)
                {
                    _log?.Invoke($"键盘 report id 0x{candidate:X2} 失败：{Describe(ex)}");
                }
            }

            // 正式候选之间留一点间隔；全量扫描要试 256 次，不能每个都睡
            if (!quiet && attempt < attempts - 1)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    /// <summary>回包第 2、3 字节应当原样回显请求的命令头。</summary>
    private static bool LooksLikeEcho(byte[] response, byte[] payload) =>
        response.Length > 2 && payload.Length >= 2
        && response[1] == payload[0]
        && response[2] == payload[1];

    /// <summary>把 Win32 错误码翻成人话。</summary>
    private static string Describe(Exception ex) => ex switch
    {
        IOException io when io.Message.Contains("错误码 87")
            => "设备不接受这条报文（report id 或长度不对）",
        IOException io when io.Message.Contains("错误码 31")
            => "设备暂时没有响应（刚被上一个句柄放开，需要几百毫秒恢复）",
        IOException io when io.Message.Contains("错误码 1")
            => "设备已经断开或不可用",
        IOException io when io.Message.Contains("错误码 5")
            => "设备被其它程序占用",
        ObjectDisposedException => "设备已经断开，请重新打开",
        _ => ex.Message,
    };

    /// <summary>拿一个可用的 report id；拿不到就抛 NotReadyException，绝不拿 0 去硬发。</summary>
    private byte EnsureReportId()
    {
        if (IsReportIdResolved)
        {
            return ReportId;
        }

        if (!_session.IsOpen)
        {
            throw new NotReadyException("设备已经断开或尚未打开，请重新打开设备。");
        }

        return ResolveReportId() ?? throw new NotReadyException(
            "配置通道还没有应答，report id 未能确定。请确认设备没有被官方 MCHOSE HUB 占用，然后重新打开设备。");
    }

    /// <summary>
    /// 异步版探测。探测本身是「发报文 → 等设备回包」的阻塞操作，
    /// 必须扔到线程池——放到 UI 线程上会直接把界面卡住。
    /// </summary>
    public Task<byte?> ResolveReportIdAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ResolveReportId();
        }, cancellationToken);

    /// <summary>发送读命令并返回回包。</summary>
    public byte[] Read(string commandName)
    {
        var command = KeyboardProtocol.FindReadCommand(commandName)
                      ?? throw new ArgumentException($"未知的读命令：{commandName}", nameof(commandName));

        var payload = KeyboardProtocol.BuildReadPayload(command);
        lock (_idGate)
        {
            if (_wireless is not null)
            {
                EnsureReportId();
                if (payload[0] < 0x80) WaitForWriteSlot();
                return _wireless.Transact(payload);
            }
            var response = Transact(payload, $"{command.Name} → {Convert.ToHexString(command.Header)}", verifyEcho: true);
            _log?.Invoke($"键盘 RX [{ReportId:X2}] {KeyboardProtocol.Describe(response)}");
            return response;
        }
    }

    /// <summary>手工发送一条 feature report（用于试探尚未还原的命令）。</summary>
    public byte[] SendRaw(byte[] payload)
    {
        if (payload.Length == 0 || payload.Length > KeyboardProtocol.WiredPayloadLength)
            throw new ArgumentException("键盘报文须为 1–519 字节。", nameof(payload));
        if (_wireless is not null)
        {
            return Execute(() =>
            {
                EnsureReportId();
                if (payload[0] < 0x80) WaitForWriteSlot();
                return _wireless.Transact(payload);
            });
        }
        var buffer = new byte[KeyboardProtocol.WiredPayloadLength];
        Array.Copy(payload, buffer, Math.Min(payload.Length, buffer.Length));

        lock (_idGate)
        {
            // 命令号最高位为 0 的是写命令（0x03/0x04/0x05/0x06/0x0A），
            // 为 1 的是读命令（0x83/0x84/0x86/0x87/0x8A）。
            // 读命令只是把数据取回来，不碰配置存储，不需要排写入队。
            bool isWrite = buffer[0] < 0x80;
            // 写命令的回包头跟读命令不完全一致（有的回显读命令的字节），
            // 所以这里不做回声校验，靠调用方写完后回读确认。
            var response = Transact(buffer, $"写命令 0x{buffer[0]:X2}（{buffer.Length} 字节）",
                verifyEcho: !isWrite, retryBackoffMs: isWrite ? WriteRetryBackoffMs : RetryBackoffMs);
            _log?.Invoke($"键盘 RX(raw) {KeyboardProtocol.Describe(response)}");
            return response;
        }
    }

    /// <summary>
    /// 两次**写**命令之间必须留的间隔。
    ///
    /// 实测（真机复现）：`setPerformance`(0x04) 之后紧接着发 `setLightColor`(0x0A)，
    /// 第二条会被设备**直接拒收**，报 Win32 31「设备暂时没有响应」。
    /// 固件写完一次配置需要落盘时间，这期间它不接第二条写命令。
    /// 客户端一重试就变成连续砸设备，最后把固件写卡——「拖进度条键盘像死机、
    /// 灯不亮要拔插才恢复」就是这么来的。读命令不受影响（回包缓冲区是另一套）。
    ///
    /// 这条闸门放在传输层：不管界面、CLI 还是诊断页的手工发送，写命令一律排队。
    /// </summary>
    private static readonly TimeSpan WriteGap = TimeSpan.FromMilliseconds(1200);
    private static readonly object _writeGate = new();
    private static long _lastWriteTimestamp;

    /// <summary>写命令失败后的重试退避基数：写失败多半是设备还在恢复，冲上去只会更糟。</summary>
    private const int WriteRetryBackoffMs = 600;

    private static void WaitForWriteSlot()
    {
        lock (_writeGate)
        {
            var since = System.Diagnostics.Stopwatch.GetElapsedTime(_lastWriteTimestamp);
            if (_lastWriteTimestamp != 0 && since < WriteGap)
            {
                Thread.Sleep((int)(WriteGap - since).TotalMilliseconds);
            }

            _lastWriteTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// 一次完整的 feature 事务：确定 report id → 发 → 收。
    /// 所有事务都在 _idGate 里串行，探测和读写不会互相插队。
    ///
    /// 读命令会校验回包：设备固件只有一个 520 字节的报文缓冲区，忙的时候
    /// 会把**上一次的缓冲区原样丢回来**（实测见过数据区开头就是我们自己的
    /// 请求字节 `06 8A 00 00`）。这种回包拿去解出来的是一堆假值，再被写回
    /// 设备就会把设置改乱，所以一律重试。
    /// </summary>
    private byte[] Transact(byte[] payload, string label, bool verifyEcho, int retryBackoffMs = RetryBackoffMs)
    {
        var id = EnsureReportId();
        if (_wireless is not null) return _wireless.Transact(payload);
        _log?.Invoke($"键盘 TX [{id:X2}] {label}");

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                Thread.Sleep(retryBackoffMs * (attempt - 1));
            }

            try
            {
                if (payload[0] < 0x80) WaitForWriteSlot();
                _session.SendFeature(id, payload);
                var response = _session.GetFeature(id, KeyboardProtocol.WiredPayloadLength);

                if (!verifyEcho || IsPlausibleResponse(response, payload))
                {
                    return response;
                }

                _log?.Invoke($"键盘 RX 回包不可信（像是上一次的缓冲区残留），重试 {attempt}/{MaxAttempts}");
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                InvalidateReportId();
                _log?.Invoke($"键盘 {label} 失败（{Describe(ex)}），重新确定 report id 后重试 {attempt}/{MaxAttempts}");
                id = ResolveReportId() ?? throw new NotReadyException(
                    $"{label} 失败：{Describe(ex)}；重新探测 report id 也没有应答。" +
                    "请确认设备没有被官方 MCHOSE HUB 或其它程序占用，然后重新打开设备。");
            }
        }

        throw new NotReadyException(
            $"{label} 连续 {MaxAttempts} 次都没拿到可信的回包。设备可能正忙或固件状态异常，请稍后重试；" +
            "如果一直这样，把键盘拔插一次再试。");
    }

    /// <summary>
    /// 回包是否可信：
    /// 1）读命令必须逐字节回显请求头（response[1..7] == payload[0..6]）；
    /// 2）数据区开头不能又是我们自己的请求字节——那是设备缓冲区没换过的特征。
    /// </summary>
    private bool IsPlausibleResponse(byte[] response, byte[] payload)
    {
        if (response.Length != WiredReportLength || payload.Length < 7 || response[0] != _wiredReportId)
        {
            return false;
        }

        for (int i = 0; i < 7; i++)
        {
            if (response[1 + i] != payload[i])
            {
                return false;
            }
        }

        // 残留缓冲区：数据区前四字节 == 请求的前四字节
        return !(response[8] == payload[0]
                 && response[9] == payload[1]
                 && response[10] == payload[2]
                 && response[11] == payload[3]);
    }

    /// <summary>传输层故障：这类错误值得重新探测并重试一次。</summary>
    private static bool IsTransportFailure(Exception ex) =>
        ex is IOException or TimeoutException or ObjectDisposedException;

    private void InvalidateReportId()
    {
        IsReportIdResolved = false;
        ReportId = 0;
    }

    /// <summary>是否是该走键盘协议的设备：厂商自定义集合且 feature 长度达到 519。</summary>
    public static bool LooksLikeKeyboardChannel(HidDeviceInfo device)
    {
        if (LooksLikeWirelessChannel(device)) return true;
        if (device.UsagePage < 0xFF00 || device.FeatureReportLength != WiredReportLength) return false;
        try { KeyboardSettingsSession.ResolveModelKey(device.ProductName); return true; }
        catch (ArgumentException) { return false; }
    }

    public static bool LooksLikeWirelessChannel(HidDeviceInfo device) =>
        device.UsagePage >= 0xFF00 && device.InputReportLength >= 20 && device.OutputReportLength >= 20
        && (device.ProductName?.Contains("MCHOSE 2.4G Wireless", StringComparison.OrdinalIgnoreCase) == true
            || device.ProductName?.Contains("2.4G Wireless Receiver", StringComparison.OrdinalIgnoreCase) == true);

    private const int WiredReportLength = KeyboardProtocol.WiredPayloadLength + 1;

    public T Execute<T>(Func<T> action)
    {
        lock (_idGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return action();
        }
    }

    private int _disposed;

    /// <summary>关闭会话。重复调用安全——UI 上「关闭设备」和「切换设备」会各调一次。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _wireless?.Cancel();
        lock (_idGate)
        {
            _wireless?.Dispose();
            _session.Dispose();
        }
    }
}
