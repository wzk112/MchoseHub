namespace MchoseHub.Core.Protocol;

/// <summary>
/// 键盘（有线）协议。来源：网页版 purify.es chunk 里的命令表 cpe。
///
/// 通道：HID **feature report**，payload 固定填充到 519 字节（加 report id 共 520）。
/// 读取类命令发固定的请求头，再读回 feature report；
/// 写入类命令是「1 字节命令号 + 型号配置生成的参数」。
/// </summary>
public static class KeyboardProtocol
{
    /// <summary>有线设备的 feature report payload 长度。</summary>
    public const int WiredPayloadLength = 519;

    /// <summary>
    /// 配置通道用的 report id。网页版命令字符串的第一个字节（06）就是它，
    /// 传输层会把它作为 report id 前置，因此 payload 里不能再重复一次。
    /// </summary>
    public const byte ReportId = 0x06;

    /// <summary>读命令的请求头（12 字节），后面补 0 到 519。</summary>
    public sealed record ReadCommand(string Name, string DisplayName, byte[] Header);

    public static IReadOnlyList<ReadCommand> ReadCommands { get; } =
    [
        new("getBattery", "读取电量",            [0x87, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00]),
        new("getLightColor", "读取灯效配置",      [0x8A, 0x00, 0x00, 0x01, 0x00, 0x00, 0x02]),
        new("getPerformance", "读取性能设置",     [0x84, 0x00, 0x00, 0x01, 0x00, 0x80, 0x00]),
        new("getKeySetting", "读取按键设置",      [0x83, 0x00, 0x00, 0x01, 0x00, 0xF8, 0x01]),
        new("getDiyLight", "读取 DIY 灯效",       [0x86, 0x00, 0x00, 0x01, 0x00, 0x7A, 0x01]),
        new("getMacro", "读取宏",                [0x82, 0x00, 0x00, 0x01, 0x00, 0x04, 0x00, 0x00, 0x04, 0x00, 0x00]),
        new("getPassWord", "读取密码",            [0x82, 0x01, 0x00, 0x01, 0x00, 0x06, 0x00]),
    ];

    /// <summary>
    /// 写命令的命令号（报文首字节）。网页版把命令字符串当十进制数用，
    /// 所以 setLightColor 的 10 是 0x0A，不是 0x10。
    /// </summary>
    public static IReadOnlyDictionary<string, byte> WriteCommands { get; } =
        new Dictionary<string, byte>
        {
            ["setKeySetting"] = 0x03,
            ["setPerformance"] = 0x04,
            ["setReset"] = 0x04,
            ["setMacro"] = 0x05,
            ["setDiyLight"] = 0x06,
            ["setLightColor"] = 0x0A,
        };

    /// <summary>构造读命令的 519 字节 payload。</summary>
    public static byte[] BuildReadPayload(ReadCommand command)
    {
        var payload = new byte[WiredPayloadLength];
        Array.Copy(command.Header, payload, Math.Min(command.Header.Length, payload.Length));
        return payload;
    }

    /// <summary>
    /// 指定按键层的读命令头。官方三个层共用一个命令，
    /// 只把第二个字节换成 默认 00 / Fn 01 / Fn2 02。
    /// </summary>
    public static byte[] LayeredKeySettingHeader(byte layerCode) => layerCode <= 2
        ? [0x83, layerCode, 0x00, 0x01, 0x00, 0xF8, 0x01]
        : throw new ArgumentOutOfRangeException(nameof(layerCode));

    /// <summary>按键层名 → 层编号。</summary>
    public static byte LayerCode(string layer) => layer switch
    {
        "Fn" => 0x01,
        "Fn2" => 0x02,
        "Default" => 0x00,
        _ => throw new ArgumentException("按键层须为 Default / Fn / Fn2。", nameof(layer)),
    };

    public static ReadCommand? FindReadCommand(string name) =>
        ReadCommands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把回包整理成便于阅读的形式：去掉尾部大片 0，并给出十六进制。
    /// </summary>
    public static string Describe(byte[] response)
    {
        int last = response.Length - 1;
        while (last > 0 && response[last] == 0)
        {
            last--;
        }

        var meaningful = response.AsSpan(0, Math.Min(last + 1, 64)).ToArray();
        string hex = string.Join(' ', meaningful.Select(b => b.ToString("X2")));
        return $"有效长度 {last + 1} / {response.Length} 字节：{hex}" +
               (last + 1 > 64 ? " …" : string.Empty);
    }
}
