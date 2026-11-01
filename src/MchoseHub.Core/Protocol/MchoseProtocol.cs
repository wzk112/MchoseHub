namespace MchoseHub.Core.Protocol;

/// <summary>
/// 第二代帧协议常量。来源：网页版 mchose.com.cn 的 co / tJe / nJe 等常量。
/// </summary>
public static class MchoseProtocol
{
    public const byte StartByte = 0xAA;
    public const byte Version = 0x01;

    /// <summary>音箱 / 耳机类设备的 HID report id。</summary>
    public const byte ReportId = 0xBC;

    /// <summary>耳机状态通道的 report id。</summary>
    public const byte HeadsetReportId = 0x55;

    /// <summary>键鼠类设备使用的 report id（网页版另一族协议）。</summary>
    public const byte PeripheralReportId = 0xAF;

    public const int DefaultReportLength = 63;

    public static readonly byte[] Request = [0x00, 0x01];
    public static readonly byte[] Response = [0x00, 0x01];
    public static readonly byte[] Set = [0x00, 0x02];
    public static readonly byte[] Notify = [0x00, 0x04];
    public static readonly byte[] UpImg = [0x00, 0x03];
    public static readonly byte[] UpImgResponse = [0x00, 0x03];
    public static readonly byte[] OtaRequest = [0x00, 0x06];

    // 帧内偏移
    public const int OffsetStart = 0;
    public const int OffsetVersion = 1;
    public const int OffsetFlags = 2;
    public const int OffsetLength = 3;
    public const int OffsetCmdType = 4;
    public const int OffsetCmd = 6;
    public const int OffsetPayload = 7;
}

/// <summary>解析结果。</summary>
public sealed class MchoseFrame
{
    public required byte Flags { get; init; }
    public required byte[] CmdType { get; init; }
    public required byte Cmd { get; init; }
    public required byte[] Payload { get; init; }
    public required byte Checksum { get; init; }
    public required byte ExpectedChecksum { get; init; }
    public required bool IsValid { get; init; }

    public byte[] Raw { get; init; } = [];

    /// <summary>把 payload 当作 [cmd, ...] 结构取命令号（回包用）。</summary>
    public byte ResponseCommand => Payload.Length > 0 ? Payload[0] : (byte)0;

    public override string ToString() =>
        $"cmdType=[{CmdType[0]:X2} {CmdType[1]:X2}] cmd=0x{Cmd:X2} payload={Convert.ToHexString(Payload)} valid={IsValid}";
}
