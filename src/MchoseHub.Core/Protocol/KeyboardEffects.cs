namespace MchoseHub.Core.Protocol;

/// <summary>
/// 一个键盘灯效。
///
/// 名字取自官方网页版语言包（theme chunk 里的 <c>keyboard.*</c> 键），
/// 可用性标志取自灯效列表里每个条目的 <c>brightnessDisabled / speedDisabled /
/// colorDisabled</c>——官方 UI 就是按这几个标志决定哪些滑杆可以拖的，
/// 比如「自定义（逐键调色）」的亮度和速度是灰的，颜色才是可选的。
/// </summary>
public sealed record KeyboardEffect(
    int Id,
    string Name,
    bool BrightnessDisabled = false,
    bool SpeedDisabled = false,
    bool ColorDisabled = false)
{
    /// <summary>逐键调色用的那个灯效（编号随机型不同：19 或 21）。</summary>
    public bool IsCustom { get; init; }
}

/// <summary>键盘灯效编号表。</summary>
public static class KeyboardEffectTable
{
    /// <summary>不带「自定义」的常规灯效。6 号（彩虹轮盘）只有 G75_V2 用。</summary>
    public static IReadOnlyList<KeyboardEffect> Normal { get; } =
    [
        new(0, "关闭", BrightnessDisabled: true, SpeedDisabled: true, ColorDisabled: true),
        new(1, "常亮模式", SpeedDisabled: true),
        new(2, "呼吸模式"),
        new(3, "梦幻彩虹", ColorDisabled: true),
        new(4, "一触即发"),
        new(5, "雨中漫步"),
        new(6, "彩虹轮盘", ColorDisabled: true),
        new(7, "涟漪扩散"),
        new(8, "繁星点点"),
        new(10, "川流不息"),
        new(11, "随波逐流"),
        new(12, "如影随形"),
        new(13, "正弦光波"),
        new(14, "正弦光波"),
        new(15, "旋转风车", BrightnessDisabled: true, SpeedDisabled: true, ColorDisabled: true),
        new(16, "七彩瀑布", ColorDisabled: true),
        new(17, "花开富贵", BrightnessDisabled: true, SpeedDisabled: true, ColorDisabled: true),
    ];

    /// <summary>常用灯效：界面上默认只列这几个，其余收进「显示全部」。</summary>
    public static IReadOnlyList<int> Common { get; } = [0, 1, 2, 3];

    public static KeyboardEffect? Find(int id) =>
        Normal.FirstOrDefault(e => e.Id == id);

    /// <summary>完整列表：常规灯效 + 该机型的「自定义（逐键调色）」。</summary>
    public static IReadOnlyList<KeyboardEffect> ForModel(int customLightMode, string model = "K87")
    {
        var list = Normal.Where(e => e.Id != (customLightMode == 19 ? 13 : 14)
            && (e.Id != 6 || model == "G75_V2")).ToList();
        if (list.All(e => e.Id != customLightMode))
        {
            list.Add(new KeyboardEffect(
                customLightMode,
                "自定义（逐键调色）",
                BrightnessDisabled: true,
                SpeedDisabled: true)
            { IsCustom = true });
        }

        return list.OrderBy(e => e.Id).ToList();
    }
}
