using MchoseHub.Core.Models;
using MchoseHub.Core.Protocol;
using MchoseHub.Core.Transport;

namespace MchoseHub.Core.Services;

/// <summary>
/// 一条设备连接：负责协议代次探测、状态读取、控制指令下发。
/// </summary>
public sealed class MchoseConnection : IDisposable
{
    private readonly IHidSession _session;
    private readonly List<byte[]> _inbox = [];
    private readonly object _gate = new();

    public MchoseConnection(IHidSession session, SupportedDevice? definition, Action<string>? log = null)
    {
        _session = session;
        Definition = definition;
        _log = log;
        _session.InputReportReceived += OnInputReport;
    }

    private readonly Action<string>? _log;

    public SupportedDevice? Definition { get; }
    public HidDeviceInfo Device => _session.Device;
    public ProtocolGeneration Generation { get; private set; } = ProtocolGeneration.Auto;
    public bool IsOpen => _session.IsOpen;

    public string DisplayName => Definition?.ProductName ?? Device.DisplayName;

    private void OnInputReport(object? sender, HidInputReport report)
    {
        lock (_gate)
        {
            _inbox.Add([report.ReportId, .. report.Data]);
            if (_inbox.Count > 64)
            {
                _inbox.RemoveAt(0);
            }
        }
        _log?.Invoke($"RX  [{report.ReportId:X2}] {Convert.ToHexString(report.Data)}");
    }

    /// <summary>发送 output report，并记录日志。</summary>
    public void Send(byte reportId, byte[] payload)
    {
        _log?.Invoke($"TX  [{reportId:X2}] {Convert.ToHexString(payload)}");
        _session.SendOutput(reportId, payload);
    }

    /// <summary>等待一条匹配前缀的回包。</summary>
    private async Task<byte[]?> WaitForReportAsync(
        byte reportId,
        byte[]? prefix,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[]? hit = null;
            lock (_gate)
            {
                for (int i = 0; i < _inbox.Count; i++)
                {
                    var candidate = _inbox[i];
                    if (candidate.Length == 0 || candidate[0] != reportId)
                    {
                        continue;
                    }
                    if (prefix is { Length: > 0 } && !Matches(candidate, prefix))
                    {
                        continue;
                    }
                    hit = candidate;
                    _inbox.RemoveAt(i);
                    break;
                }
            }

            if (hit is not null)
            {
                return hit;
            }
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static bool Matches(byte[] candidate, byte[] prefix)
    {
        // candidate[0] 是 report id，payload 从 1 开始
        if (candidate.Length < prefix.Length + 1)
        {
            return false;
        }
        for (int i = 0; i < prefix.Length; i++)
        {
            if (candidate[i + 1] != prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>探测协议代次：先试第二代结构化帧，失败再试第一代裸字节。</summary>
    public async Task<ProtocolGeneration> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (Definition?.Protocol is ProtocolGeneration.Legacy or ProtocolGeneration.Structured)
        {
            Generation = Definition.Protocol;
            return Generation;
        }

        lock (_gate)
        {
            _inbox.Clear();
        }
        Send(MchoseProtocol.ReportId, MchoseCommands.GetBaseState());
        var structured = await WaitForReportAsync(MchoseProtocol.ReportId, [MchoseProtocol.StartByte], TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
        if (structured is not null && MchoseFrameCodec.Parse(structured.AsSpan(1)) is { IsValid: true })
        {
            Generation = ProtocolGeneration.Structured;
            _log?.Invoke("协议探测：第二代结构化帧");
            return Generation;
        }

        lock (_gate)
        {
            _inbox.Clear();
        }
        Send(MchoseProtocol.ReportId, MchoseCommands.LegacyGetState());
        var legacy = await WaitForReportAsync(MchoseProtocol.ReportId, null, TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
        Generation = legacy is not null ? ProtocolGeneration.Legacy : ProtocolGeneration.Auto;
        _log?.Invoke(legacy is not null ? "协议探测：第一代裸字节" : "协议探测失败：设备无响应");
        return Generation;
    }

    public async Task<DeviceState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (Generation == ProtocolGeneration.Auto)
        {
            await ProbeAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _inbox.Clear();
        }

        if (Generation == ProtocolGeneration.Structured)
        {
            Send(MchoseProtocol.ReportId, MchoseCommands.GetBaseState());
            var report = await WaitForReportAsync(MchoseProtocol.ReportId, null, TimeSpan.FromMilliseconds(3000), cancellationToken).ConfigureAwait(false);
            if (report is null)
            {
                return null;
            }
            var frame = MchoseFrameCodec.Parse(report.AsSpan(1));
            if (frame is null || !frame.IsValid)
            {
                _log?.Invoke("结构化回包解析失败");
                return null;
            }
            _log?.Invoke($"回包解析：{frame}");
            _log?.Invoke("此代音频协议的状态布局尚未实现，请查看原始回包。");
            return null;
        }

        Send(MchoseProtocol.ReportId, MchoseCommands.LegacyGetState());
        var legacyReport = await WaitForReportAsync(MchoseProtocol.ReportId, [0x11], TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
        if (legacyReport is null || legacyReport.Length < 16)
        {
            return null;
        }
        return DeviceState.ParseLegacy(legacyReport.AsSpan(1).ToArray());
    }

    public async Task ApplyLightingAsync(
        byte controlMode,
        byte brightness,
        byte cycle,
        byte index,
        byte r,
        byte g,
        byte b,
        bool voiceBroadcast,
        CancellationToken cancellationToken = default)
    {
        if (Generation == ProtocolGeneration.Auto)
        {
            await ProbeAsync(cancellationToken).ConfigureAwait(false);
        }

        bool structured = Generation == ProtocolGeneration.Structured;
        Send(MchoseProtocol.ReportId, structured
            ? MchoseCommands.SetControlDetail(controlMode, brightness, cycle)
            : MchoseCommands.LegacySetControlDetail(controlMode, brightness, cycle));
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);

        Send(MchoseProtocol.ReportId, structured
            ? MchoseCommands.SetColorMap(index, r, g, b)
            : MchoseCommands.LegacySetColorMap(index, r, g, b));
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);

        Send(MchoseProtocol.ReportId, structured
            ? MchoseCommands.SetControlMode(controlMode)
            : MchoseCommands.LegacySetControlMode(controlMode));
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);

        Send(MchoseProtocol.ReportId, structured
            ? MchoseCommands.SetLightGroups(3, 1)
            : MchoseCommands.LegacySetLightGroups(3, 1));
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
    }

    public Task SetVoiceBroadcastAsync(bool on, CancellationToken cancellationToken = default) =>
        Task.Run(() => Send(MchoseProtocol.ReportId, MchoseCommands.LegacySetVoiceBroadcast(on)), cancellationToken);

    public Task SetEqModeAsync(byte mode, CancellationToken cancellationToken = default) =>
        Task.Run(() => Send(MchoseProtocol.ReportId, MchoseCommands.LegacySetEqMode(mode)), cancellationToken);

    /// <summary>恢复出厂设置。调用方必须先向用户确认。</summary>
    public Task RestoreFactoryAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Send(MchoseProtocol.ReportId, MchoseCommands.LegacyRestore()), cancellationToken);

    public void Dispose()
    {
        _session.InputReportReceived -= OnInputReport;
        _session.Dispose();
    }
}
