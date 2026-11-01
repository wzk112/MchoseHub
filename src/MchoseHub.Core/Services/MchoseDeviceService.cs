using MchoseHub.Core.Models;
using MchoseHub.Core.Protocol;
using MchoseHub.Core.Transport;

namespace MchoseHub.Core.Services;

/// <summary>扫描到的设备 + 是否在支持列表内。</summary>
public sealed record DetectedDevice(HidDeviceInfo Info, SupportedDevice? Definition)
{
    public bool IsSupported => Definition is not null;
    public string DisplayName => string.IsNullOrWhiteSpace(Info.ProductName) ? Definition?.ProductName ?? Info.DisplayName : Info.DisplayName;
}

/// <summary>通道类型：决定用哪套协议跟设备说话。</summary>
public enum ChannelKind
{
    /// <summary>键盘：feature report（519 字节）。</summary>
    Keyboard,
    /// <summary>音箱 / 耳机：output report。</summary>
    Audio,
    /// <summary>没找到合适的通道。</summary>
    Unknown,
}

/// <summary>
/// 一台物理设备。一个设备会暴露多个 HID 集合（键盘、鼠标、消费者控制、厂商自定义…），
/// 界面上只应该显示成一条，内部再自动挑出真正的配置通道。
/// </summary>
public sealed record DeviceGroup
{
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<DetectedDevice> Collections { get; init; }
    public required DetectedDevice Channel { get; init; }
    public required ChannelKind Kind { get; init; }
    public SupportedDevice? Definition => Channel.Definition;

    public string Id => Channel.Info.PhysicalId;
    public string KindText => Kind switch
    {
        ChannelKind.Keyboard => KeyboardSession.LooksLikeWirelessChannel(Channel.Info) ? "2.4G 键盘通道" : "有线键盘通道",
        ChannelKind.Audio => "音频通道",
        _ => "未识别通道",
    };
}

public sealed class MchoseDeviceService
{
    private readonly IHidTransport _transport;
    private readonly Action<string>? _log;

    public MchoseDeviceService(IHidTransport transport, Action<string>? log = null)
    {
        _transport = transport;
        _log = log;
    }

    /// <summary>
    /// 扫描设备。默认只返回迈从设备（按厂商 ID 或产品名判断）；
    /// includeAll 为 true 时返回全部 HID 设备，便于排查未知型号。
    /// </summary>
    public IReadOnlyList<DetectedDevice> Scan(bool includeAll = false)
    {
        // 必须枚举全部再过滤：键盘用的是 Apple 的 VID，按厂商 ID 预过滤会把它漏掉，
        // 而产品名字符串只有打开设备后才能拿到。
        var devices = _transport.Enumerate(null);
        var result = new List<DetectedDevice>(devices.Count);

        foreach (var device in devices)
        {
            var definition = DeviceCatalog.Match(device.VendorId, device.ProductId);
            bool isMchose = DeviceCatalog.LooksLikeMchose(device.VendorId, device.ProductId, device.ProductName);

            if (!includeAll && !isMchose)
            {
                continue;
            }

            result.Add(new DetectedDevice(device, definition));
        }

        _log?.Invoke(
            $"扫描完成：共枚举 {devices.Count} 个 HID 集合，" +
            $"匹配到迈从设备 {result.Count} 个（其中 {result.Count(d => d.IsSupported)} 个已收录）");

        return result
            .OrderByDescending(d => d.IsSupported)
            .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 按 Windows 容器或序列号聚合，同型号的两台设备保持独立。
    /// </summary>
    public IReadOnlyList<DeviceGroup> ScanGroups(bool includeAll = false)
    {
        var devices = Scan(includeAll);

        return devices
            .GroupBy(d => d.Info.PhysicalId)
            .Select(group =>
            {
                var collections = group.ToList();
                var channel = PickChannel(collections);
                return new DeviceGroup
                {
                    VendorId = channel.Info.VendorId,
                    ProductId = channel.Info.ProductId,
                    Name = channel.DisplayName,
                    Collections = collections,
                    Channel = channel,
                    Kind = ClassifyChannel(channel),
                };
            })
            .OrderByDescending(g => g.Kind != ChannelKind.Unknown)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>从多个 HID 集合里挑出配置通道。</summary>
    private static DetectedDevice PickChannel(IReadOnlyList<DetectedDevice> collections)
    {
        // 键盘：厂商自定义集合 + 519 字节 feature report
        var keyboard = collections.FirstOrDefault(IsKeyboardChannel);
        if (keyboard is not null)
        {
            return keyboard;
        }

        // 音箱 / 耳机：必须有 output report
        var audio = collections
            .Where(IsAudioChannel)
            .OrderByDescending(c => c.Info.OutputReportLength)
            .FirstOrDefault();
        if (audio is not null)
        {
            return audio;
        }

        // 退化：报告长度最大的那个
        return collections
            .OrderByDescending(c => c.Info.InputReportLength + c.Info.FeatureReportLength + c.Info.OutputReportLength)
            .First();
    }

    private static ChannelKind ClassifyChannel(DetectedDevice channel)
    {
        if (IsKeyboardChannel(channel))
        {
            return ChannelKind.Keyboard;
        }
        if (IsAudioChannel(channel))
        {
            return ChannelKind.Audio;
        }
        return ChannelKind.Unknown;
    }

    private static bool IsAudioChannel(DetectedDevice device) => device.Info.OutputReportLength >= 15
        && device.Definition?.DeviceType is DeviceType.Headset or DeviceType.HeadsetDock or DeviceType.Speaker;

    public MchoseConnection Connect(DetectedDevice detected)
    {
        _log?.Invoke($"打开设备：{detected.DisplayName}（{detected.Info.Path}）");
        var session = _transport.Open(detected.Info);
        return new MchoseConnection(session, detected.Definition, _log);
    }

    /// <summary>以键盘模式打开设备（feature report 通道）。</summary>
    public KeyboardSession ConnectKeyboard(DetectedDevice detected)
    {
        _log?.Invoke($"打开键盘通道：{detected.DisplayName}（{detected.Info.Path}）");
        var session = _transport.Open(detected.Info);
        return new KeyboardSession(session, _log);
    }

    /// <summary>在扫描结果里找出键盘的配置通道（厂商自定义集合 + 519 字节 feature report）。</summary>
    public static bool IsKeyboardChannel(DetectedDevice detected) =>
        KeyboardSession.LooksLikeKeyboardChannel(detected.Info);
}
