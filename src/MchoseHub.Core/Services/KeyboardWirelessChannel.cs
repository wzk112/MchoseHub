using System.Collections.Concurrent;
using System.Diagnostics;
using MchoseHub.Core.Protocol;
using MchoseHub.Core.Transport;

namespace MchoseHub.Core.Services;

internal sealed class KeyboardWirelessChannel : IDisposable
{
    private readonly IHidSession _session;
    private readonly Action<string>? _log;
    private readonly ConcurrentQueue<byte[]> _inbox = new();
    private readonly AutoResetEvent _received = new(false);
    private readonly object _eventGate = new();
    private bool _disposed;
    private volatile bool _closing;
    public KeyboardModelSchema? Model { get; set; }

    public KeyboardWirelessChannel(IHidSession session, Action<string>? log)
    {
        _session = session; _log = log; session.InputReportReceived += OnInput;
    }

    private void OnInput(object? sender, HidInputReport report)
    {
        if (report.ReportId != KeyboardWirelessProtocol.ReportId || report.Data.Length < KeyboardWirelessProtocol.PacketLength) return;
        lock (_eventGate)
        {
            if (_disposed) return;
            _inbox.Enqueue(report.Data[..KeyboardWirelessProtocol.PacketLength]);
            while (_inbox.Count > 512) _inbox.TryDequeue(out _);
            _received.Set();
        }
    }

    public string ResolveModel() => KeyboardWirelessProtocol.Identify(ReadData(5, 0, 6).AsSpan(0, 6));

    public byte[] Transact(byte[] wiredPayload)
    {
        if (wiredPayload.Length < 7) throw new ArgumentException("配置报文缺少头部。");
        byte command = wiredPayload[0];
        if (command >= 0x80)
        {
            byte code = command == 0x82 ? (wiredPayload[1] == 1 ? (byte)5 : (byte)0x43) : KeyboardWirelessProtocol.ReadCode(command);
            byte layer = command == 0x83 ? wiredPayload[1] : (byte)0;
            int minimum = command switch
            {
                0x87 => 2,
                0x82 => code == 5 ? 6 : 10,
                0x84 => SchemaSize(Model?.ReadPerformance),
                0x8A => SchemaSize(Model?.ReadLightColor),
                0x86 => 378,
                0x83 => Model?.KeyList.Sum(k => k.Length) ?? 504,
                _ => throw new ArgumentException("无线读命令未收录。"),
            };
            var data = ReadData(code, layer, minimum);
            var canonical = new byte[KeyboardProtocol.WiredPayloadLength + 1]; canonical[0] = KeyboardWirelessProtocol.ReportId;
            wiredPayload.AsSpan(0, 7).CopyTo(canonical.AsSpan(1));
            // Wireless macro uses the same six-byte directory parser header as wired.
            int offset = code == 0x43 ? 2 : 8;
            data.AsSpan(0, Math.Min(data.Length, canonical.Length - offset)).CopyTo(canonical.AsSpan(offset));
            return canonical;
        }
        if (Model is null) throw new InvalidOperationException("无线键盘型号尚未确认，不能写配置。");
        byte writeCode = command switch { 3 => 1, 4 => 4, 6 => 2, 10 => 9, _ => throw new ArgumentException("此无线写入请使用宏库上传接口。") };
        byte writeLayer = command == 3 ? wiredPayload[1] : (byte)0;
        Write(writeCode, wiredPayload[7..], writeLayer);
        return new byte[KeyboardProtocol.WiredPayloadLength + 1];
    }

    private int SchemaSize(CommandSchema? schema) => schema is null
        ? throw new InvalidOperationException("请先确认无线键盘型号。")
        : KeyboardPayloadCodec.ExpectedSize(schema) - 6;

    private byte[] ReadData(byte command, byte layer, int minimum)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ClearInbox();
            _session.SendOutput(KeyboardWirelessProtocol.ReportId, KeyboardWirelessProtocol.ReadRequest(command, layer));
            var packets = new Dictionary<byte, byte[]>(); int total = 0;
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                var packet = Next(TimeSpan.FromSeconds(10) - timer.Elapsed);
                if (packet is null) break;
                if (packet[0] != command) continue;
                if (packet[1] == 0 || packet[2] >= packet[1]) throw new InvalidDataException("无线回包的分包编号无效。");
                if (total != 0 && total != packet[1]) throw new InvalidDataException("无线回包的分包总数发生变化。");
                total = packet[1];
                if (packets.TryGetValue(packet[2], out var previous) && !previous.SequenceEqual(packet))
                    throw new InvalidDataException("同一无线分包返回了不同内容。");
                packets[packet[2]] = packet;
                if (packets.Count != total) continue;
                var data = Enumerable.Range(0, total).SelectMany(i => packets[(byte)i].Skip(4).Take(14)).ToArray();
                if (data.Length < minimum) throw new InvalidDataException("无线配置回包不完整，已停止解析。");
                _log?.Invoke($"无线读 0x{command:X2}：{total} 个分包，{data.Length} 字节");
                return data;
            }
            _log?.Invoke($"无线读 0x{command:X2} 超时，重试 {attempt + 1}/3");
        }
        throw new TimeoutException("无线键盘没有返回完整配置，请确认键盘处于 2.4G 模式且接收器已经连接。");
    }

    public void Write(byte command, byte[] data, byte layer = 0)
    {
        var packets = KeyboardWirelessProtocol.WritePackets(command, data, layer);
        var deadline = Stopwatch.StartNew();
        foreach (var packet in packets)
        {
            bool ack = false;
            for (int attempt = 0; attempt < 3 && !ack; attempt++)
            {
                if (deadline.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException("无线写入超过两分钟，配置可能只写入了一部分。");
                ClearInbox(); _session.SendOutput(KeyboardWirelessProtocol.ReportId, packet);
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(5))
                {
                    var response = Next(TimeSpan.FromSeconds(5) - timer.Elapsed);
                    if (response is null) break;
                    if (response[0] == command && response[2] == packet[2]) { ack = true; break; }
                }
            }
            if (!ack) throw new TimeoutException($"无线分包 {packet[2] + 1}/{packets.Count} 未确认；配置可能只写入了一部分，请重试完整配置。");
        }
        _log?.Invoke($"无线写 0x{command:X2}：{packets.Count} 个分包均已确认");
    }

    private void ClearInbox() { while (_inbox.TryDequeue(out _)) { } }
    private byte[]? Next(TimeSpan remaining)
    {
        while (remaining > TimeSpan.Zero)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_inbox.TryDequeue(out var packet)) return packet;
            if (!_session.IsOpen) throw new IOException("无线接收器已经断开。");
            var timer = Stopwatch.StartNew(); _received.WaitOne((int)Math.Clamp(remaining.TotalMilliseconds, 1, 200)); remaining -= timer.Elapsed;
        }
        return null;
    }
    public void Cancel()
    {
        lock (_eventGate) { _closing = true; if (!_disposed) _received.Set(); }
    }
    public void Dispose()
    {
        lock (_eventGate)
        {
            _disposed = true; _session.InputReportReceived -= OnInput; _received.Dispose();
        }
    }
}
