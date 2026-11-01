namespace MchoseHub.Core.Protocol;

/// <summary>
/// 机型「灯效参数区」的出厂值。
///
/// 性能设置这条命令里，除了回报率、灯效编号这些用户能看到的字段，还有一大块
/// 固件内部参数：<c>space3 / space4 / space5 / space7 / space8..space11</c>，
/// 以及每个灯效各自的 <c>(brightnessN, speedN + multiColorN)</c> 槽位。
/// 这些字段网页版从不给用户改，但固件读它们来决定灯怎么画。
///
/// 它们一旦被写脏（例如早期版本按错位的布局回写过整包），表现就是
/// **「界面显示下发成功、设备也真的存下了，但键盘上什么反应都没有」**——
/// 因为固件拿到的参数区已经不是它认识的那张表了。
///
/// 这里的数值逐个抄自官方 bundle 里的机型配置对象（K87 = <c>otherObj</c>），
/// 只用来把参数区修回出厂状态，不碰用户自己的设置。
/// </summary>
public static class KeyboardFactoryLightProfiles
{
    public sealed record Profile(
        byte Space2,
        byte[] Space3,
        byte[] Space4,
        byte[] Space5,
        byte[] Space6,
        byte[] Space7,
        byte[] Space8,
        byte[] Space9,
        byte[] Space10,
        byte[] Space11,
        byte Brightness,
        byte Speed,
        byte MultiColor,
        int[] EffectSlots);

    /// <summary>K87 有亮度和速度槽位的灯效编号（6 / 9 / 14 藏在 space8/9/10 里，不在这里）。</summary>
    private static readonly int[] K87Slots = [1, 2, 3, 4, 5, 7, 8, 10, 11, 12, 13, 15, 16, 17];

    private static readonly Profile K87 = new(
        Space2: 3,
        Space3: [0x00, 0x00, 0x04, 0x04, 0x07],
        Space4: [0x20, 0x00, 0x00, 0x00],
        Space5: [0x00, 0x00, 0x01, 0x00, 0x04, 0x01, 0x00, 0xFF],
        Space6: [0x00, 0x00],
        Space7:
        [
            0x01, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF,
        ],
        Space8: [0x03, 0x37],
        Space9: [0x03, 0x37],
        Space10: [0x03, 0x37],
        Space11:
        [
            0x03, 0x37, 0x03, 0x37, 0x03, 0x37, 0x07, 0x47, 0x07, 0x47,
            0x07, 0x44, 0x07, 0x44, 0x07, 0x44, 0x07, 0x44, 0x07, 0x44,
            0x07, 0x44, 0x07, 0x44, 0x04, 0x03, 0x04, 0x04, 0x04, 0x04,
            0x04, 0x04, 0x04, 0x04, 0x5A, 0xA5,
        ],
        Brightness: 3,
        Speed: 3,
        MultiColor: 0,
        EffectSlots: K87Slots);

    public static Profile? Get(string? modelKey) =>
        string.Equals(modelKey, "K87", StringComparison.OrdinalIgnoreCase) ? K87 : null;

    /// <summary>
    /// 把出厂参数铺进一份**刚从设备读回来的**性能配置里。
    ///
    /// <paramref name="keepBrightnessFor"/> 是当前灯效编号：这个槽位的亮度保留用户现在的值
    /// （不然修完灯会变成出厂那份很暗的 3%），其余槽位按出厂值铺。
    /// 返回 false 表示这个机型还没收录出厂参数。
    /// </summary>
    public static bool Apply(string? modelKey, PayloadValues values, int? keepBrightnessFor = null)
    {
        var profile = Get(modelKey);
        if (profile is null)
        {
            return false;
        }

        values.SetNumber("space2", profile.Space2);
        SetIfPresent(values, "space3", profile.Space3);
        SetIfPresent(values, "space4", profile.Space4);
        SetIfPresent(values, "space5", profile.Space5);
        SetIfPresent(values, "space6", profile.Space6);
        SetIfPresent(values, "space7", profile.Space7);
        SetIfPresent(values, "space8", profile.Space8);
        SetIfPresent(values, "space9", profile.Space9);
        SetIfPresent(values, "space10", profile.Space10);
        SetIfPresent(values, "space11", profile.Space11);

        foreach (var slot in profile.EffectSlots)
        {
            if (!values.Has($"brightness{slot}"))
            {
                continue;
            }

            if (slot != keepBrightnessFor)
            {
                values.SetNumber($"brightness{slot}", profile.Brightness);
            }

            if (values.Has($"speed{slot}"))
            {
                values.SetNumber($"speed{slot}", profile.Speed);
            }

            if (values.Has($"multiColor{slot}"))
            {
                values.SetNumber($"multiColor{slot}", profile.MultiColor);
            }
        }

        return true;
    }

    /// <summary>参数区是否已经完全符合出厂值（用来做「写完了」的校验）。</summary>
    public static bool Matches(string? modelKey, PayloadValues values, int? ignoreBrightnessFor = null)
    {
        var profile = Get(modelKey);
        if (profile is null)
        {
            return false;
        }

        if (values.GetNumber("space2") != profile.Space2
            || !BufferEquals(values.GetBuffer("space3"), profile.Space3)
            || !BufferEquals(values.GetBuffer("space4"), profile.Space4)
            || !BufferEquals(values.GetBuffer("space5"), profile.Space5)
            || !BufferEquals(values.GetBuffer("space6"), profile.Space6)
            || !BufferEquals(values.GetBuffer("space7"), profile.Space7)
            || !BufferEquals(values.GetBuffer("space8"), profile.Space8)
            || !BufferEquals(values.GetBuffer("space9"), profile.Space9)
            || !BufferEquals(values.GetBuffer("space10"), profile.Space10)
            || !BufferEquals(values.GetBuffer("space11"), profile.Space11))
        {
            return false;
        }

        foreach (var slot in profile.EffectSlots)
        {
            if (!values.Has($"brightness{slot}"))
            {
                continue;
            }

            if (slot != ignoreBrightnessFor && values.GetNumber($"brightness{slot}") != profile.Brightness)
            {
                return false;
            }

            if (values.Has($"speed{slot}") && values.GetNumber($"speed{slot}") != profile.Speed)
            {
                return false;
            }

            if (values.Has($"multiColor{slot}") && values.GetNumber($"multiColor{slot}") != profile.MultiColor)
            {
                return false;
            }
        }

        return true;
    }

    private static void SetIfPresent(PayloadValues values, string name, byte[] bytes)
    {
        if (values.GetBuffer(name).Length > 0 || values.Has(name))
        {
            values.SetBuffer(name, bytes);
        }
    }

    private static bool BufferEquals(byte[] left, byte[] right) =>
        left.Length == right.Length && left.AsSpan().SequenceEqual(right);
}
