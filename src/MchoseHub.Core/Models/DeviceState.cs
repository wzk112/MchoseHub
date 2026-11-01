namespace MchoseHub.Core.Models;

public sealed class LightInfo
{
    public int ControlMode { get; set; }
    public int Brightness { get; set; }
    public int Cycle { get; set; }
    public int ColorIndex { get; set; }
    public int BrightnessSwitch { get; set; }
    public int BrightnessMax { get; set; }
    public int GroupsNums { get; set; }
    public int GroupsOn { get; set; }
    public byte[] CurrentColor { get; set; } = [0, 0, 0];

    public string ControlModeText => ControlMode switch
    {
        0 => "常亮",
        1 => "呼吸",
        2 => "流光",
        3 => "音乐律动",
        4 => "关闭",
        _ => $"未知({ControlMode})",
    };
}

/// <summary>设备状态。字段含义来自本地版 useK20HID 的回包解析。</summary>
public sealed class DeviceState
{
    public int WorkMode { get; set; }
    public int VoiceBroadcast { get; set; }
    public int Speaker { get; set; }
    public int Mic { get; set; }
    public int EqMode { get; set; }
    public LightInfo Light { get; set; } = new();

    /// <summary>解析第一代 15 字节回包。</summary>
    public static DeviceState? ParseLegacy(byte[] data)
    {
        if (data.Length < 15)
        {
            return null;
        }

        byte b1 = data[1];
        var state = new DeviceState
        {
            EqMode = data[2],
            VoiceBroadcast = b1 & 0x01,
            Speaker = (b1 >> 1) & 0x01,
            Mic = (b1 >> 2) & 0x01,
            WorkMode = (b1 >> 4) & 0x0F,
        };

        state.Light = new LightInfo
        {
            ControlMode = data[3],
            Brightness = data[4],
            Cycle = data[5],
            ColorIndex = data[6],
            BrightnessSwitch = data[7],
            BrightnessMax = data[8],
            GroupsNums = data[9],
            GroupsOn = data[10],
            CurrentColor = [data[12], data[13], data[14]],
        };
        return state;
    }
}
