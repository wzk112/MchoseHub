namespace MchoseHub.Core.Protocol;

/// <summary>
/// 常用命令构造。第二代走结构化帧，第一代是裸字节（老机型仍在用）。
/// </summary>
public static class MchoseCommands
{
    // ---- 第二代 ----

    /// <summary>读取设备状态：Request cmd=0</summary>
    public static byte[] GetBaseState() =>
        MchoseFrameCodec.Build(MchoseProtocol.Request, 0x00);

    /// <summary>切换控制模式：Set cmd=7，payload [mode]（兼容第一代 0x21 06）</summary>
    public static byte[] SetControlMode(byte mode) =>
        MchoseFrameCodec.Build(MchoseProtocol.Set, 0x07, [mode]);

    /// <summary>灯效细节：Set cmd=3，payload [mode, brightness, cycle, 0]（兼容第一代 0x21 03）</summary>
    public static byte[] SetControlDetail(byte mode, byte brightness, byte cycle) =>
        MchoseFrameCodec.Build(MchoseProtocol.Set, 0x03, [mode, brightness, cycle, 0x00]);

    /// <summary>灯组开关：Set cmd=5，payload [groupsNum, groupsOn]（兼容第一代 0x21 05）</summary>
    public static byte[] SetLightGroups(byte groupsNum, byte groupsOn) =>
        MchoseFrameCodec.Build(MchoseProtocol.Set, 0x05, [groupsNum, groupsOn]);

    /// <summary>灯珠取色：Set cmd=14，payload [index, 1, g, r, b]（兼容第一代 0x22）</summary>
    public static byte[] SetColorMap(byte index, byte r, byte g, byte b) =>
        MchoseFrameCodec.Build(MchoseProtocol.Set, 0x0E, [index, 0x01, g, r, b]);

    /// <summary>恢复出厂设置：Set cmd=7 属于控制模式，恢复出厂为独立命令（第一代 0x21 07）</summary>
    public static byte[] RestoreFactory() =>
        LegacyRestore();

    // ---- 第一代（15 字节，reportId 0xBC）----

    private const int LegacyLength = 15;

    private static byte[] Legacy(params byte[] head)
    {
        var frame = new byte[LegacyLength];
        Array.Copy(head, frame, Math.Min(head.Length, LegacyLength));
        return frame;
    }

    public static byte[] LegacyGetState() => Legacy(0x11, 0x00);
    public static byte[] LegacySetEqMode(byte mode) => Legacy(0x21, 0x01, mode);
    public static byte[] LegacySetVoiceBroadcast(bool on) => Legacy(0x21, 0x02, (byte)(on ? 1 : 0));
    public static byte[] LegacySetControlDetail(byte mode, byte brightness, byte cycle) => Legacy(0x21, 0x03, mode, brightness, cycle, 0x00);
    public static byte[] LegacySetLightGroups(byte groupsNum, byte groupsOn) => Legacy(0x21, 0x05, groupsNum, groupsOn);
    public static byte[] LegacySetControlMode(byte mode) => Legacy(0x21, 0x06, mode);
    public static byte[] LegacyRestore() => Legacy(0x21, 0x07, 0x01);
    public static byte[] LegacySetColorMap(byte index, byte r, byte g, byte b) => Legacy(0x22, index, 0x01, g, r, b);

    // ---- 耳机（reportId 0x55，63 字节）----

    public static byte[] HeadsetGetInfo()
    {
        var frame = new byte[63];
        frame[0] = 0x65;
        frame[1] = 0x01;
        return frame;
    }
}
