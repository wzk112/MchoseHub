namespace MchoseHub.Core.Models;

public enum DeviceType
{
    Headset,
    HeadsetDock,
    Speaker,
    Mouse,
    Keyboard,
}

public enum AudioRuntime
{
    None,
    CMedia,
    Thx,
}

/// <summary>协议代次：老机型用裸字节，新机型用结构化帧。</summary>
public enum ProtocolGeneration
{
    /// <summary>未验证，连接时自动探测。</summary>
    Auto,
    /// <summary>第一代裸字节（0x11 / 0x21 / 0x22）。</summary>
    Legacy,
    /// <summary>第二代结构化帧（0xAA 帧 + XOR 校验）。</summary>
    Structured,
}

public sealed record SupportedDevice
{
    public required string ProductKey { get; init; }
    public required string ProductName { get; init; }
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }
    public required DeviceType DeviceType { get; init; }
    public AudioRuntime AudioRuntime { get; init; } = AudioRuntime.CMedia;
    public ProtocolGeneration Protocol { get; init; } = ProtocolGeneration.Auto;

    /// <summary>部分型号要求设备支持指定的 output report 才认（K20 PRO / K20 GT 为 188）。</summary>
    public byte? RequiredOutputReportId { get; init; }

    /// <summary>THX 机型的硬件 ID，来自网页版 THXConfig.hwid。</summary>
    public string? HardwareId { get; init; }

    /// <summary>是否已在真机上验证过。未验证的型号在界面上会标注。</summary>
    public bool Verified { get; init; }

    public string VendorHex => $"0x{VendorId:X4}";
    public string ProductHex => $"0x{ProductId:X4}";
    public bool RequiresAudioDriver => AudioRuntime == AudioRuntime.CMedia;
}

/// <summary>
/// 设备表。来源：网页版 main-DNTfMTZ3.js 的 supportedDevices 数组（程序化提取）。
/// </summary>
public static class DeviceCatalog
{
    public static IReadOnlyList<SupportedDevice> All { get; } =
    [
        new() { ProductKey = "MCHOSE S9 PRO", ProductName = "MCHOSE S9 PRO", VendorId = 0x291D, ProductId = 0x385D, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE S9 PRO", ProductName = "MCHOSE S9 PRO", VendorId = 0x41E4, ProductId = 0x3402, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE G9 PRO", ProductName = "MCHOSE G9 PRO", VendorId = 0x291D, ProductId = 0x385D, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE G9 PRO", ProductName = "MCHOSE G9 PRO", VendorId = 0x41E4, ProductId = 0x3502, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE V9", ProductName = "MCHOSE V9", VendorId = 0x291D, ProductId = 0x385D, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE V9", ProductName = "MCHOSE V9", VendorId = 0x41E4, ProductId = 0x3201, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE V9 PRO", ProductName = "MCHOSE V9 PRO", VendorId = 0x291D, ProductId = 0x385D, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE V9 PRO", ProductName = "MCHOSE V9 PRO", VendorId = 0x41E4, ProductId = 0x3202, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE X9", ProductName = "MCHOSE X9", VendorId = 0x3837, ProductId = 0x6045, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE V9 TURBO", ProductName = "MCHOSE V9 TURBO", VendorId = 0x3837, ProductId = 0x600D, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE V9 TURBO+", ProductName = "MCHOSE V9 TURBO+", VendorId = 0x3837, ProductId = 0x600A, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE V9 PRO 2", ProductName = "MCHOSE V9 PRO 2", VendorId = 0x3837, ProductId = 0x6011, DeviceType = DeviceType.Headset, AudioRuntime = AudioRuntime.Thx, HardwareId = "VID_3837&PID_6011&MI_00" },
        new() { ProductKey = "MCHOSE R9", ProductName = "MCHOSE R9", VendorId = 0x3837, ProductId = 0x6013, DeviceType = DeviceType.Headset, AudioRuntime = AudioRuntime.Thx, HardwareId = "VID_3837&PID_6013&MI_00" },
        new() { ProductKey = "MCHOSE V9 TURBO HEADSET", ProductName = "MCHOSE V9 TURBO HEADSET", VendorId = 0x3837, ProductId = 0x6009, DeviceType = DeviceType.HeadsetDock, AudioRuntime = AudioRuntime.None },
        new() { ProductKey = "MCHOSE G9 WIRED HEADSET", ProductName = "MCHOSE G9 WIRED HEADSET", VendorId = 0x3837, ProductId = 0x8080, DeviceType = DeviceType.Headset },
        new() { ProductKey = "MCHOSE G20", ProductName = "MCHOSE G20", VendorId = 0x0E6A, ProductId = 0x0111, DeviceType = DeviceType.Speaker },
        new() { ProductKey = "MCHOSE G20 PRO", ProductName = "MCHOSE G20 PRO", VendorId = 0x0E6A, ProductId = 0x0111, DeviceType = DeviceType.Speaker },
        new() { ProductKey = "MCHOSE K20", ProductName = "MCHOSE K20", VendorId = 0x3837, ProductId = 0x60C4, DeviceType = DeviceType.Speaker },
        new() { ProductKey = "MCHOSE K20 PRO", ProductName = "MCHOSE K20 PRO", VendorId = 0x3837, ProductId = 0x6003, DeviceType = DeviceType.Speaker, RequiredOutputReportId = 0xBC },
        new() { ProductKey = "MCHOSE K20 GT", ProductName = "MCHOSE K20 GT", VendorId = 0x3837, ProductId = 0x60C6, DeviceType = DeviceType.Speaker, RequiredOutputReportId = 0xBC },
        // 键盘：该型号使用 0x05AC（Apple 的厂商 ID，代工厂常用它换取 macOS 免驱识别）
        new() { ProductKey = "MCHOSE K87", ProductName = "MCHOSE K87 Gaming Keyboard", VendorId = 0x05AC, ProductId = 0x024F, DeviceType = DeviceType.Keyboard, AudioRuntime = AudioRuntime.None, Verified = false },
    ];

    /// <summary>迈从自有及已知代工厂的厂商 ID，用于快速过滤。</summary>
    public static IReadOnlyCollection<ushort> VendorIds { get; } =
        All.Where(d => d.DeviceType != DeviceType.Keyboard)
           .Select(d => d.VendorId)
           .Distinct()
           .ToArray();

    /// <summary>产品名里出现这些字样就认为可能是迈从设备（用于覆盖未知型号与共享 VID 的情况）。</summary>
    private static readonly string[] NameMarkers = ["MCHOSE", "MAICONG", "迈从", "2.4G Wireless Receiver"];

    public static SupportedDevice? Match(ushort vendorId, ushort productId) =>
        All.FirstOrDefault(d => d.VendorId == vendorId && d.ProductId == productId);

    /// <summary>
    /// 按厂商 ID 或产品名判断是否可能是迈从设备。
    /// 单靠 VID 不可靠：键盘用的是 Apple 的 0x05AC，而迈从自有 VID 也可能被其他品牌借用。
    /// </summary>
    public static bool LooksLikeMchose(ushort vendorId, ushort productId, string? productName)
    {
        if (Match(vendorId, productId) is not null)
        {
            return true;
        }

        if (VendorIds.Contains(vendorId))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(productName))
        {
            return false;
        }

        return NameMarkers.Any(marker =>
            productName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
