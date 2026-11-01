namespace MchoseHub.Core.Transport;

public sealed record HidDeviceInfo
{
    public required string Path { get; init; }
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }
    public string? ProductName { get; init; }
    public string? Manufacturer { get; init; }
    public string? SerialNumber { get; init; }
    public Guid? ContainerId { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public int InputReportLength { get; init; }
    public int OutputReportLength { get; init; }
    public int FeatureReportLength { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(ProductName) ? "未知设备" : ProductName!;
    public string Id => $"{VendorId:X4}:{ProductId:X4}";
    public string PhysicalId => ContainerId is { } container && container != Guid.Empty
        ? $"{Id}:{container:D}"
        : !string.IsNullOrWhiteSpace(SerialNumber)
            ? $"{Id}:serial:{SerialNumber.ToUpperInvariant()}"
            : $"{Id}:path:{Path.ToUpperInvariant()}";
}

/// <summary>收到一条 input report。</summary>
public sealed record HidInputReport(byte ReportId, byte[] Data);

/// <summary>
/// 平台传输层抽象。Windows 用 SetupAPI + hid.dll 实现，
/// Linux 走 /dev/hidraw，macOS 走 IOKit，三者实现同一个接口。
/// </summary>
public interface IHidTransport : IDisposable
{
    /// <summary>枚举 HID 设备，可按厂商 ID 过滤。</summary>
    IReadOnlyList<HidDeviceInfo> Enumerate(IReadOnlyCollection<ushort>? vendorIds = null);

    /// <summary>打开设备。返回的实例同样实现 IHidTransport 的会话部分。</summary>
    IHidSession Open(HidDeviceInfo device);
}

public interface IHidSession : IDisposable
{
    HidDeviceInfo Device { get; }
    bool IsOpen { get; }

    /// <summary>发送 output report。</summary>
    void SendOutput(byte reportId, byte[] payload);

    /// <summary>发送 feature report。</summary>
    void SendFeature(byte reportId, byte[] payload);

    /// <summary>读取 feature report。</summary>
    byte[] GetFeature(byte reportId, int length);

    /// <summary>收到 input report 时触发。</summary>
    event EventHandler<HidInputReport>? InputReportReceived;
}
