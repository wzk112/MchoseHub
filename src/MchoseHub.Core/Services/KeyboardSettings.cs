using MchoseHub.Core.Protocol;

namespace MchoseHub.Core.Services;

/// <summary>回报率档位。</summary>
public enum PollingRate
{
    Hz250 = 1,
    Hz500 = 2,
    Hz1000 = 3,
}

/// <summary>低延迟模式。</summary>
public enum LatencyMode
{
    /// <summary>标准模式（latencySwitch = 2）。</summary>
    Standard = 0,
    /// <summary>速度模式（latencySwitch = 0）。</summary>
    Speed = 1,
    LowLatencyStandard = 2,
}

/// <summary>性能页能改的全部设置。</summary>
public sealed record PerformanceSettings
{
    public PollingRate Rate { get; init; } = PollingRate.Hz1000;

    /// <summary>休眠时间，单位分钟；0 表示不休眠。</summary>
    public int SleepMinutes { get; init; }

    public LatencyMode Latency { get; init; } = LatencyMode.Standard;

    public bool WinKeyLocked { get; init; }

    public bool MacMode { get; init; }

    /// <summary>当前灯效编号（0 关闭，1 常亮，2 呼吸，3 循环变色…）。</summary>
    public int LightMode { get; init; } = 1;

    /// <summary>
    /// 灯效组：0 = 常规灯效，1 = 自定义灯效（逐键调色）。
    /// 官方网页版在选中「自定义」时把 lightType 写成 1，选常规灯效写 0；
    /// 这个标志不对，设备就会把逐键颜色表当成没启用，于是「点了没反应」。
    /// </summary>
    public int LightType { get; init; }

    /// <summary>亮度 0–100。</summary>
    public int Brightness { get; init; } = 100;

    /// <summary>速度档位 0–10。</summary>
    public int Speed { get; init; }
    public bool MultiColor { get; init; }
}

/// <summary>灯光页能改的设置。</summary>
public sealed record LightingSettings
{
    public int LightMode { get; init; } = 1;
    public int Brightness { get; init; } = 100;
    public int Speed { get; init; }
    public byte Red { get; init; } = 0xFF;
    public byte Green { get; init; } = 0x00;
    public byte Blue { get; init; } = 0xFF;
}

/// <summary>一个键的当前映射（4 字节原始编码）。</summary>
public sealed record KeyMapping(string Key, string Layer, byte[] Value, string? TargetKey, bool IsDefault)
{
    public string Display => IsDefault
        ? "默认"
        : TargetKey is { Length: > 0 } ? $"→ {TargetKey}" : Convert.ToHexString(Value);
}

/// <summary>逐键灯光表：LED 序号 → 颜色。索引与按键表一致。</summary>
public sealed class DiyLightTable
{
    private readonly byte[] _red;
    private readonly byte[] _green;
    private readonly byte[] _blue;

    public DiyLightTable(int size)
    {
        _red = new byte[size];
        _green = new byte[size];
        _blue = new byte[size];
    }

    public int Size => _red.Length;

    public (byte R, byte G, byte B) this[int index]
    {
        get => index >= 0 && index < Size
            ? (_red[index], _green[index], _blue[index])
            : ((byte)0, (byte)0, (byte)0);
        set
        {
            if (index < 0 || index >= Size)
            {
                return;
            }

            _red[index] = value.R;
            _green[index] = value.G;
            _blue[index] = value.B;
        }
    }

    public byte[] Red => _red;
    public byte[] Green => _green;
    public byte[] Blue => _blue;
    public DiyLightTable Clone()
    {
        var copy = new DiyLightTable(Size);
        _red.CopyTo(copy._red, 0); _green.CopyTo(copy._green, 0); _blue.CopyTo(copy._blue, 0);
        return copy;
    }

    public void Fill(byte r, byte g, byte b)
    {
        Array.Fill(_red, r);
        Array.Fill(_green, g);
        Array.Fill(_blue, b);
    }
}

/// <summary>
/// 把「人话」和协议字段互相翻译。所有字段名、档位换算都来自网页版 bundle，
/// 真机回写校验（读 → 原样写回 → 再读）已通过。
/// </summary>
public sealed class KeyboardSettings
{
    /// <summary>网页版下拉里的三档回报率。</summary>
    public static readonly int[] PollingRatesHz = [250, 500, 1000];

    /// <summary>可选的休眠分钟数。</summary>
    public static readonly int[] SleepOptionsMinutes = [0, 1, 2, 3, 5, 10, 15, 30];

    private readonly KeyboardSettingsSession _session;
    private PayloadValues _performance = new();
    private PayloadValues _light = new();
    private PayloadValues _diy = new();
    private bool _performanceLoaded;
    private bool _lightLoaded;
    private bool _diyLoaded;

    public KeyboardSettings(KeyboardSettingsSession session)
    {
        _session = session;
    }

    private void InvalidateCaches() => _performanceLoaded = _lightLoaded = _diyLoaded = false;

    private T ExecuteMutation<T>(Func<T> action) => _session.Execute(() =>
    {
        try { return action(); }
        catch
        {
            // A failed transaction can already have changed part of a device configuration.
            // The next edit must reread the device, never reuse a pre-write or unverified cache.
            InvalidateCaches();
            throw;
        }
    });

    public KeyboardModelSchema Model => _session.Model;
    public bool SupportsPollingRate => Model.WritePerformance.Fields.Any(f => f.Name == "rateVal");
    public int? ReadBattery() => _session.ReadBattery();

    public PerformanceSettings ReadPerformance()
    {
        return _session.Execute(() =>
        {
            _performanceLoaded = false;
            var values = _session.ReadPerformance();
            var mode = values.GetNumber("lightMode");

            // 设备忙的时候回的可能是残留缓冲区，解出来是一堆离谱的值
            // （实测见过 rateVal=1、lightMode=7、brightness1=255 这种）。
            // 这种数据既不显示也不许写回——写回去就是把键盘设置改乱。
            int rate = values.GetNumber("rateVal");
            int latency = values.GetNumber("latencySwitch");
            if ((values.Has("rateVal") && rate is < 1 or > 3)
                || latency is < 0 or > 3
                || values.GetNumber("winKeySwitch") > 1
                || values.GetNumber("macSwitch") > 3
                || values.GetNumber("lightType") > 1
                || mode > 21
                || (values.Has($"brightness{mode}") && values.GetNumber($"brightness{mode}") > 100))
            {
                throw new KeyboardSession.NotReadyException(
                    "设备这次回的性能设置数值不正常（多半是残留缓冲区），已忽略。请重试一次。");
            }

            _performance = values;
            _performanceLoaded = true;
            return new PerformanceSettings
            {
                Rate = values.Has("rateVal") ? (PollingRate)rate : PollingRate.Hz1000,
                SleepMinutes = values.GetNumber("sleepTimeVal") / 2,
                Latency = Model.Model == "Z75" && latency == 1 ? LatencyMode.LowLatencyStandard
                    : latency == 0 ? LatencyMode.Speed : LatencyMode.Standard,
                WinKeyLocked = values.GetNumber("winKeySwitch") == 1,
                MacMode = values.GetNumber("macSwitch") == _session.MacSwitchValue,
                LightMode = mode,
                LightType = values.GetNumber("lightType"),
                Brightness = values.GetNumber($"brightness{mode}"),
                Speed = values.GetNumber($"speed{mode}"),
                MultiColor = values.GetNumber($"multiColor{mode}") == 7,
            };

        });
    }

    public LightingSettings ReadLighting()
    {
        return _session.Execute(() =>
        {
            _lightLoaded = false;
            _light = _session.ReadLightColor();

            // 颜色是按当前灯效取的（lightModeColorN），所以得先知道灯效编号。
            // 没读过性能设置就自己补一次，否则会取到 0 号槽位。
            if (!_performanceLoaded)
            {
                ReadPerformance();
            }

            var mode = _performance.GetNumber("lightMode");
            var color = _light.GetBuffer($"lightModeColor{mode}");
            _lightLoaded = true;

            return new LightingSettings
            {
                LightMode = mode,
                Brightness = _performance.GetNumber($"brightness{mode}"),
                Speed = _performance.GetNumber($"speed{mode}"),
                Red = color.Length > 0 ? color[0] : (byte)0xFF,
                Green = color.Length > 1 ? color[1] : (byte)0x00,
                Blue = color.Length > 2 ? color[2] : (byte)0xFF,
            };

        });
    }

    /// <summary>写性能设置。没读过的字段不会被动到，因为整份配置是从刚读回来的值改的。</summary>
    public void WritePerformance(PerformanceSettings settings)
    {
        ExecuteMutation(() =>
        {
            if (!Enum.IsDefined(settings.Rate) || !Enum.IsDefined(settings.Latency) || settings.SleepMinutes is < 0 or > 127
                || settings.LightMode is < 0 or > 21 || settings.Brightness is < 0 or > 100 || settings.Speed is < 0 or > 4)
                throw new ArgumentOutOfRangeException(nameof(settings), "性能或灯光参数不在设备支持范围内。");
            // 没读过就先把设备上的整份配置读回来：写命令是「整包覆盖」，
            // 拿一份空的去写等于把设备的设置全部清零。
            if (!_performanceLoaded)
            {
                ReadPerformance();
            }

            var values = _performance.Clone();
            if (SupportsPollingRate) values.SetNumber("rateVal", (int)settings.Rate);
            else if (settings.Rate != PollingRate.Hz1000) throw new ArgumentException("此机型的官方协议没有回报率设置字段。");
            values.SetNumber("sleepTimeVal", settings.SleepMinutes * 2);
            if (Model.Model == "Z75")
            {
                values.SetNumber("latencySwitch", settings.Latency switch { LatencyMode.Standard => 3, LatencyMode.LowLatencyStandard => 1, _ => 0 });
                values.SetNumber("mode", settings.Latency switch { LatencyMode.Standard => 10, LatencyMode.LowLatencyStandard => 3, _ => 0 });
            }
            else
            {
                if (settings.Latency == LatencyMode.LowLatencyStandard) throw new ArgumentException("此机型不支持 Z75 标准低延迟模式。");
                values.SetNumber("latencySwitch", settings.Latency == LatencyMode.Speed ? 0 : _performance.GetNumber("latencySwitch") == 3 ? 3 : 2);
            }
            values.SetNumber("winKeySwitch", !settings.MacMode && settings.WinKeyLocked ? 1 : 0);
            values.SetNumber("macSwitch", settings.MacMode ? _session.MacSwitchValue : 0);

            // 「自定义（逐键调色）」属于另一个灯效组：官方网页版选它时把 lightType 写成 1，
            // 选常规灯效写 0。少了这一步，设备收下逐键颜色表也不渲染。
            values.SetNumber("lightType", settings.LightMode == _session.Model.CustomLightMode ? 1 : 0);
            values.SetNumber("lightMode", settings.LightMode);

            int index = settings.LightMode;
            if (index is >= 1 and <= 17)
            {
                // 官方对这些灯效就没给亮度/速度（滑杆是灰的），这时不能拿界面上的
                // 数值去覆盖设备里的原值——那会把用户之前的设置改坏。
                var effect = KeyboardEffectTable.Find(index);
                if (effect is null || !effect.BrightnessDisabled)
                {
                    values.SetNumber($"brightness{index}", Math.Clamp(settings.Brightness, 0, 100));
                }
                else
                {
                    // 官方对「呼吸」这类不给亮度滑杆的灯效，写入时**固定给满亮度**
                    // （bundle 里就是 `{lightMode:2, speed2:T, brightness2:100}`）。
                    // 设备里这类槽位的出厂值非常暗（实测 3%），不覆盖就成了「切过去像没反应」。
                    values.SetNumber($"brightness{index}", 100);
                }

                if (effect is null || !effect.SpeedDisabled)
                {
                    // 固件的速度只有 0–4（官方界面是 1–5 档，写之前减一），写更大的值设备不认
                    values.SetNumber($"speed{index}", Math.Clamp(settings.Speed, 0, 4));
                }

                values.SetNumber($"multiColor{index}", settings.MultiColor ? 7 : 0);
            }

            _session.WritePerformance(values);
            _performance = values;

            return true;
        });
    }

    /// <summary>写灯效。颜色落在当前灯效对应的 lightModeColorN 上，跟网页版一致。</summary>
    public void WriteLighting(LightingSettings settings)
    {
        ExecuteMutation(() =>
        {
            // 颜色表是「每个灯效一格」的一整块，写的时候整包覆盖。
            // 没先读回来就等于拿一张空表去覆盖——键盘的灯会全部变成 #000000（黑的）。
            if (!_lightLoaded)
            {
                ReadLighting();
            }

            var values = _light.Clone();
            int index = settings.LightMode;
            if (index is >= 1 and <= 17)
            {
                values.SetBuffer($"lightModeColor{index}", [settings.Red, settings.Green, settings.Blue]);
            }

            _session.WriteLightColor(values);
            _light = values;

            return true;
        });
    }

    /// <summary>
    /// 修灯效参数区：把固件内部那块（space2..space11 + 每个灯效的亮度/速度/多彩槽位）
    /// 重铺成出厂值，用户自己的设置原样保留。
    ///
    /// 用在「改灯效/改颜色显示成功了但键盘没反应」上——那种情况八成是参数区被写脏，
    /// 固件已经不认识这张表了。当前灯效的亮度会保留，避免修完变成出厂那份很暗的亮度。
    /// </summary>
    public string RepairLightProfile()
    {
        return ExecuteMutation(() =>
        {
            if (KeyboardFactoryLightProfiles.Get(Model.Model) is null)
            {
                throw new KeyboardSession.NotReadyException(
                    $"{Model.Model} 还没有收录出厂灯效参数，暂时不能自动修复；可以先用官方 MCHOSE HUB 恢复一次默认。");
            }

            // 用户当前的设置（修完要写回去）；beforeRaw 用来数这次改了哪几处
            var userSettings = ReadPerformance();

            // 出厂那份亮度是 3%，肉眼就是全黑。修完如果当前灯效的亮度还落在「出厂默认」
            // 这个区间（1–10），直接给到 100——否则用户点完「修复灯效」看到的仍是一块
            // 黑键盘，会以为没修好。（0 是明确的「关灯」，保持不动。）
            bool liftedBrightness = userSettings.Brightness is > 0 and <= 10;
            if (liftedBrightness)
            {
                userSettings = userSettings with { Brightness = 100 };
            }

            var before = _session.ReadPerformance();
            _session.RestoreFactoryLightProfile();
            _performance = _session.ReadPerformance();
            _performanceLoaded = true;

            int fixedFields = CountRepairedFields(before);

            // 固件把「往里写整份出厂参数」当成一次出厂重置：写完以后回报率、休眠、
            // Mac 模式、灯效编号这些也会被带回出厂值。所以紧接着把用户自己那份写回去，
            // 修完参数区、设置也还在。
            WritePerformance(userSettings);

            _performance = _session.ReadPerformance();
            _performanceLoaded = true;

            return $"灯效参数区已重铺成出厂值（修正 {fixedFields} 处），你的回报率、休眠、Mac 模式、" +
                   $"灯效编号 {_performance.GetNumber("lightMode")} 都已写回。" +
                   (liftedBrightness ? "当前灯效亮度原先是出厂那份几乎看不见的 3%，已一并提到 100%。" : string.Empty);

        });
    }

    private static readonly string[] LightProfileFields =
    [
        "space2", "space3", "space4", "space5", "space6", "space7", "space8", "space9", "space10", "space11",
    ];

    /// <summary>数一下这次到底改了几个字段，让用户看得见修复做了什么。</summary>
    private int CountRepairedFields(PayloadValues before)
    {
        int count = 0;
        foreach (var name in LightProfileFields)
        {
            bool changed = name == "space2"
                ? before.GetNumber(name) != _performance.GetNumber(name)
                : !before.GetBuffer(name).AsSpan().SequenceEqual(_performance.GetBuffer(name));

            if (changed)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>按当前缓存把灯光再写一次（改完灯效后同步颜色用）。</summary>
    public void WriteLightingWithCurrentColor()
    {
        ExecuteMutation(() =>
        {
            if (!_lightLoaded) ReadLighting();
            _session.WriteLightColor(_light);

            return true;
        });
    }

    /// <summary>
    /// 取某个灯效在设备上另存的亮度与速度。
    ///
    /// 每个灯效在固件里各有各的 <c>brightnessN / speedN</c> 槽位，而「自定义」
    /// （19/21）这一组根本没有这两个字段。切灯效时如果不看这个，就会把上一个
    /// 灯效（或 0）的亮度带过去，把新灯效写成一团黑。
    /// 返回 false 表示该灯效没有这两个字段，调用方应当保持设备原值不动。
    /// </summary>
    public bool TryGetEffectLighting(int mode, out int brightness, out int speed)
    {
        brightness = 0;
        speed = 0;

        if (!_performanceLoaded
            || !_performance.Has($"brightness{mode}")
            || !_performance.Has($"speed{mode}"))
        {
            return false;
        }

        brightness = _performance.GetNumber($"brightness{mode}");
        speed = _performance.GetNumber($"speed{mode}");
        return true;
    }

    public List<KeyMapping> ReadKeyMappings(string layer = "Default")
    {
        return _session.Execute(() =>
        {
            var values = _session.ReadKeySetting(KeyboardProtocol.LayerCode(layer));
            var table = values.GetTable("#keyList");
            var result = new List<KeyMapping>(table.Count);

            for (int i = 0; i < table.Count; i++)
            {
                var name = i < Model.KeyList.Count ? Model.KeyList[i].Name : $"key{i}";
                var value = table[i];

                // 设备上的约定：全 0 表示「跟出厂」；方向键、PgUp/PgDn 这类键出厂就带显式码，
                // 所以还要跟机型自带的那份默认码比一次，两者都不满足才算真的改过。
                var expected = Model.Board
                    .FirstOrDefault(b => string.Equals(b.Key, name, StringComparison.OrdinalIgnoreCase))
                    ?.ValueFor(layer);
                bool isDefault = value.All(b => b == 0)
                    || (expected is { Length: > 0 } && expected.SequenceEqual(value));

                string? target = KeyboardFunctions.Describe(value) ?? Model.KeyNameForValue(value, layer);
                if (value.Length == 4 && value[0] == 3) target = $"宏 #{value[3] + 1}";
                result.Add(new KeyMapping(name, layer, value, target, isDefault));
            }

            return result;

        });
    }

    /// <summary>可选的映射目标：默认 + 该机型上所有实体键。</summary>
    public IReadOnlyList<string> RemapTargets =>
        ["默认", .. Model.Board.Select(b => b.Key), .. KeyboardFunctions.All.Select(f => f.Name).Except(Model.Board.Select(b => b.Key))];

    // ---------------- 逐键灯光 ----------------

    /// <summary>
    /// 读取逐键灯色。设备上第 i 号 LED 对应按键表第 i 项，跟网页版 keyBoard 组件一致。
    /// </summary>
    public DiyLightTable ReadDiyLight()
    {
        return _session.Execute(() =>
        {
            _diyLoaded = false;
            _diy = _session.ReadDiyLight();
            _diyLoaded = true;
            var table = new DiyLightTable(Model.DiyTableSize);
            CopyInto(table.Red, _diy.GetBuffer("rTable"));
            CopyInto(table.Green, _diy.GetBuffer("gTable"));
            CopyInto(table.Blue, _diy.GetBuffer("bTable"));
            return table;

        });
    }

    /// <summary>写回逐键灯色表（setDiyLight）。</summary>
    public void WriteDiyLight(DiyLightTable table)
    {
        ExecuteMutation(() =>
        {
            if (table.Size != Model.DiyTableSize) throw new ArgumentException("逐键灯色表大小与型号不符。", nameof(table));
            // 同理：逐键表也是整包写，缺的部分会被补 0
            if (!_diyLoaded)
            {
                ReadDiyLight();
            }

            var values = _diy.Clone();
            values.SetBuffer("pre", Model.DiyPre.ToArray());
            values.SetBuffer("rTable", table.Red.ToArray());
            values.SetBuffer("gTable", table.Green.ToArray());
            values.SetBuffer("bTable", table.Blue.ToArray());
            _session.WriteDiyLight(values);
            _diy = values;

            return true;
        });
    }

    /// <summary>把某个键设成指定颜色；返回 false 表示这个机型没有这个键。</summary>
    public static bool SetKeyColor(KeyboardModelSchema model, DiyLightTable table, string key, byte r, byte g, byte b)
    {
        var index = model.FindLedIndex(key);
        if (index is null)
        {
            return false;
        }

        table[index.Value] = (r, g, b);
        return true;
    }

    private static void CopyInto(byte[] target, byte[] source)
    {
        Array.Copy(source, target, Math.Min(target.Length, source.Length));
    }

    /// <summary>
    /// 改键。targetKey 传 null 或 "默认" 就是恢复默认（写四个 0），
    /// 否则用机型码表里那个键在本层的编码，把源键改成它的功能。
    /// </summary>
    public void RemapKey(string layer, string keyName, string? targetKey)
    {
        ExecuteMutation(() =>
        {
            var values = _session.ReadKeySetting(KeyboardProtocol.LayerCode(layer));
            var table = values.GetTable("#keyList");
            int index = IndexOfKey(keyName);
            if (index < 0 || index >= table.Count || !Model.Board.Any(b => string.Equals(b.Key, keyName, StringComparison.OrdinalIgnoreCase))
                || Model.ReservedKeys.Contains(keyName, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentOutOfRangeException(nameof(keyName), $"机型 {Model.Model} 没有这个键：{keyName}");
            }

            if (string.IsNullOrEmpty(targetKey) || targetKey == "默认")
            {
                table[index] = [0, 0, 0, 0];
            }
            else
            {
                var function = KeyboardFunctions.Find(targetKey);
                if (layer != "Default" && function?.Value[0] == 13) throw new ArgumentException("Fn / Fn2 层不支持层切换键。");
                var target = Model.Board.FirstOrDefault(b => string.Equals(b.Key, targetKey, StringComparison.OrdinalIgnoreCase));
                table[index] = function?.Value.ToArray() ?? target?.DefaultVal.ToArray()
                    ?? throw new ArgumentException($"没有目标功能：{targetKey}", nameof(targetKey));
            }

            values.SetTable("#keyList", table);
            _session.WriteKeySetting(values);

            return true;
        });
    }

    /// <summary>把某个键改回默认。</summary>
    public void ResetKey(string keyName, string layer = "Default") => RemapKey(layer, keyName, null);

    public void WriteMacros(IReadOnlyList<KeyboardMacro> macros) => _session.WriteMacros(macros);
    public KeyboardProfile BackupProfile() => _session.BackupProfile();
    public void ValidateProfile(KeyboardProfile profile) => _session.ValidateProfile(profile);
    public void RestoreFactory()
    {
        ExecuteMutation(() => { _session.RestoreFactory(); InvalidateCaches(); return true; });
    }
    public void RestoreProfile(KeyboardProfile profile)
    {
        ExecuteMutation(() =>
        {
            _session.RestoreProfile(profile);
            InvalidateCaches();

            return true;
        });
    }

    public void BindMacro(string layer, string key, MacroTrigger trigger, int macroIndex)
    {
        ExecuteMutation(() =>
        {
            var values = _session.ReadKeySetting(KeyboardProtocol.LayerCode(layer));
            var table = values.GetTable("#keyList");
            int index = IndexOfKey(key);
            if (index < 0 || !Model.Board.Any(b => b.Key == key) || Model.ReservedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("请选择可改的实体按键。", nameof(key));
            table[index] = KeyboardMacroCodec.Binding(trigger, macroIndex);
            _session.WriteKeySetting(values);

            return true;
        });
    }

    public int IndexOfKey(string keyName) =>
        Model.KeyList
            .Select((k, i) => (k, i))
            .Where(pair => string.Equals(pair.k.Name, keyName, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.i)
            .DefaultIfEmpty(-1)
            .First();
}
