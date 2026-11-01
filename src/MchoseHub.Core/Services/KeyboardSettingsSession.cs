using MchoseHub.Core.Protocol;

namespace MchoseHub.Core.Services;

/// <summary>
/// 键盘配置读写。命令号、字段布局、尾部魔法值都照抄网页版 bundle，
/// 详见 outputs/MCHOSE-HUB-键盘命令协议.md。
/// </summary>
public sealed class KeyboardSettingsSession
{
    /// <summary>写命令的命令号（网页版把 wiredCommand 当十进制数用，这里保持一致）。</summary>
    private const byte CommandSetKeySetting = 3;
    private const byte CommandSetPerformance = 4;
    private const byte CommandSetDiyLight = 6;
    private const byte CommandSetLightColor = 10;

    /// <summary>setLightColor 尾部要补的固定字节，值随机型分组。</summary>
    private static readonly byte[] TailGroupA =
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x5A, 0xA5, 0, 0, 0, 0];

    private static readonly byte[] TailGroupB =
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x5A, 0xA5, 0, 0, 0, 0];

    private static readonly string[] TailGroupBModels =
        ["G98_Pro", "G98_V2", "G87_V2", "UT98", "Z75", "Z75S"];

    /// <summary>Mac 模式下 macSwitch 的取值，网页版 performanceSetting 里的表。</summary>
    private static readonly Dictionary<string, byte> MacSwitchOn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["G87"] = 2,
        ["K99"] = 1,
        ["K99_V2"] = 1,
        ["G98"] = 2,
        ["G98_Pro"] = 1,
        ["G98_V2"] = 1,
        ["G87_V2"] = 1,
        ["UT98"] = 1,
        ["Z75"] = 1,
        ["Z75S"] = 1,
        ["X75"] = 2,
        ["X75_V2"] = 1,
        ["G75_Pro"] = 2,
        ["G75_V2"] = 2,
        ["KX75"] = 2,
        ["GX87"] = 2,
        ["GX87_V2"] = 2,
        ["K87"] = 1,
    };

    private readonly KeyboardSession _session;
    private readonly Action<string>? _log;

    public KeyboardSettingsSession(KeyboardSession session, KeyboardModelSchema model, Action<string>? log = null)
    {
        _session = session;
        Model = model;
        session.SetModel(model);
        _log = log;
    }

    public KeyboardModelSchema Model { get; }
    public T Execute<T>(Func<T> action) => _session.Execute(action);
    public int? ReadBattery() => Execute<int?>(() =>
    {
        var report = _session.Read("getBattery");
        return report[8] <= 100 ? report[8] : null;
    });

    public KeyboardProfile BackupProfile() => Execute(() => new KeyboardProfile(1, Model.Model,
        PayloadSnapshot.From(ReadPerformance()), PayloadSnapshot.From(ReadLightColor()), PayloadSnapshot.From(ReadDiyLight()),
        Enumerable.Range(0, 3).Select(i => PayloadSnapshot.From(ReadKeySetting((byte)i))).ToList()));

    public KeyboardProfile CreateFactoryProfile(KeyboardProfile current)
    {
        ValidateProfile(current);
        PayloadSnapshot Merge(PayloadSnapshot original, string section)
        {
            var values = original.ToValues(); var defaults = KeyboardFactoryValues.Get(Model.Model, section);
            foreach (var (name, number) in defaults.Numbers) if (values.Has(name)) values.SetNumber(name, number);
            foreach (var (name, buffer) in defaults.Buffers) if (values.Has(name)) values.SetBuffer(name, buffer.ToArray());
            return PayloadSnapshot.From(values);
        }
        var layers = current.KeyLayers.Select(snapshot =>
        {
            var values = snapshot.ToValues(); var table = values.GetTable("#keyList");
            for (int i = 0; i < Model.KeyList.Count; i++)
            {
                string key = Model.KeyList[i].Name;
                if (Model.Board.Any(b => b.Key == key) && !Model.ReservedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) table[i] = [0, 0, 0, 0];
            }
            return PayloadSnapshot.From(values);
        }).ToArray();
        var profile = current with { Performance = Merge(current.Performance, "performance"), Lighting = Merge(current.Lighting, "lighting"), Diy = Merge(current.Diy, "diy"), KeyLayers = layers };
        ValidateProfile(profile);
        return profile;
    }

    public void RestoreFactory() => Execute(() => { RestoreProfile(CreateFactoryProfile(BackupProfile())); return true; });

    public void ValidateProfile(KeyboardProfile profile) => KeyboardProfileValidator.Validate(profile, Model);

    public void RestoreProfile(KeyboardProfile profile)
    {
        ValidateProfile(profile);
        Execute(() =>
        {
            // Switch lighting last, after all dependent tables have been restored.
            WriteLightColor(profile.Lighting.ToValues());
            var diy = profile.Diy.ToValues(); diy.SetBuffer("pre", Model.DiyPre.ToArray()); WriteDiyLight(diy);
            foreach (var layer in profile.KeyLayers) WriteKeySetting(layer.ToValues());
            WritePerformance(profile.Performance.ToValues());
            // Per-command checks cover normal edits; restoring a whole profile also needs
            // every inactive lighting slot and reserved field to match. This final pass is
            // read-only: a mismatch must never trigger another full restore automatically.
            VerifyRestoredProfile(profile, BackupProfile());
            return true;
        });
    }

    private static void VerifyRestoredProfile(KeyboardProfile expected, KeyboardProfile actual)
    {
        static void Require(bool equal, string path)
        {
            if (!equal) throw new InvalidDataException($"配置恢复后的回读与备份不一致：{path}。已停止，未自动重写；设备可能只应用了部分配置。");
        }
        static void Compare(PayloadSnapshot want, PayloadSnapshot got, string section)
        {
            foreach (string name in want.Numbers.Keys.Union(got.Numbers.Keys))
                Require(want.Numbers.TryGetValue(name, out int a) && got.Numbers.TryGetValue(name, out int b) && a == b, $"{section}.{name}");
            foreach (string name in want.Buffers.Keys.Union(got.Buffers.Keys))
            {
                // The only excluded field is the six-byte command header. DIY writes use
                // model.DiyPre while reads synthesize space0 from the read command. It is
                // validated before restoring and is not a user/device configuration value.
                if (name == "space0") continue;
                Require(want.Buffers.TryGetValue(name, out var a) && got.Buffers.TryGetValue(name, out var b) && a.SequenceEqual(b), $"{section}.{name}");
            }
            foreach (string name in want.Tables.Keys.Union(got.Tables.Keys))
            {
                Require(want.Tables.ContainsKey(name) && got.Tables.ContainsKey(name), $"{section}.{name}");
                var a = want.Tables[name]; var b = got.Tables[name];
                Require(a.Count == b.Count, $"{section}.{name}.Count");
                for (int i = 0; i < a.Count; i++) Require(a[i].SequenceEqual(b[i]), $"{section}.{name}[{i}]");
            }
        }
        Require(expected.Version == actual.Version && expected.Model == actual.Model, "Model/Version");
        Compare(expected.Performance, actual.Performance, "Performance");
        Compare(expected.Lighting, actual.Lighting, "Lighting");
        Compare(expected.Diy, actual.Diy, "Diy");
        Require(expected.KeyLayers.Count == actual.KeyLayers.Count, "KeyLayers.Count");
        string[] names = ["Default", "Fn", "Fn2"];
        for (int i = 0; i < expected.KeyLayers.Count; i++) Compare(expected.KeyLayers[i], actual.KeyLayers[i], names[i]);
    }

    /// <summary>Overwrites the official macro bank. The read command exposes only the first directory entry.</summary>
    public void WriteMacros(IReadOnlyList<KeyboardMacro> macros)
    {
        var bank = KeyboardMacroCodec.EncodeBank(macros);
        var pages = KeyboardMacroCodec.BuildPages(bank);
        _session.Execute(() =>
        {
            if (_session.IsWireless) _session.WriteWirelessMacroBank(bank);
            else foreach (var page in pages) _session.SendRaw(page);
            Thread.Sleep(250);
            var response = ReadTwice("getMacro");
            if (response.Length < 12 || !response.AsSpan(8, 4).SequenceEqual(bank.AsSpan(0, 4)))
                throw new KeyboardSession.NotReadyException("宏库目录回读不一致，尚未绑定任何新宏。请重新上传完整宏库。");
            return true;
        });
    }

    /// <summary>从产品名里认出机型代号，例如 “MCHOSE K87 Gaming Keyboard” → K87。</summary>
    public static string ResolveModelKey(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            throw new ArgumentException("设备未提供型号，无法选择键盘协议布局。", nameof(productName));
        }

        var normalized = new string(productName!
            .ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray());
        normalized = normalized.Replace("Z75_S", "Z75S", StringComparison.Ordinal);

        // 先试最长的机型名，避免 K99_V2 被 K99 抢走
        foreach (var candidate in KeyboardModelSchemas.Models
                     .Where(m => m is not "fullKeyCode")
                     .OrderByDescending(m => m.Length))
        {
            var key = candidate.ToUpperInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(normalized, $"(?:^|_){key}(?:_|$)"))
            {
                return candidate;
            }

        }

        throw new ArgumentException($"尚未收录型号：{productName}。", nameof(productName));
    }

    public byte MacSwitchValue => MacSwitchOn.TryGetValue(Model.Model, out var value) ? value : (byte)1;

    public PayloadValues ReadPerformance() =>
        KeyboardPayloadCodec.Decode(Model.ReadPerformance, ReadTwice("getPerformance"));

    public PayloadValues ReadLightColor() =>
        KeyboardPayloadCodec.Decode(Model.ReadLightColor, ReadTwice("getLightColor"));

    /// <summary>
    /// 把性能设置里的「灯效参数区」修回出厂值。
    ///
    /// 用户能看到的字段（回报率、灯效编号、亮度、颜色）都保留，只重铺固件内部那一块：
    /// space2..space11 和每个灯效的 (brightness, speed, multiColor) 槽位。
    /// 这一块被写脏之后的表现就是「界面说下发成功、设备也存下了，但灯就是不按设置走」。
    /// </summary>
    public byte[] RestoreFactoryLightProfile()
    {
        var values = ReadPerformance();
        int keepBrightnessFor = values.GetNumber("lightMode");

        if (!KeyboardFactoryLightProfiles.Apply(Model.Model, values, keepBrightnessFor))
        {
            throw new KeyboardSession.NotReadyException(
                $"{Model.Model} 还没有收录出厂灯效参数，不能自动修复。");
        }

        return SendVerified("还原出厂灯效参数", CommandSetPerformance, Model.WritePerformance, values,
            () => KeyboardFactoryLightProfiles.Matches(Model.Model, ReadPerformance(), keepBrightnessFor));
    }

    public PayloadValues ReadKeySetting() =>
        ReadKeySetting(0x00);

    /// <summary>按层读按键设置。layerCode：默认 00 / Fn 01 / Fn2 02。</summary>
    public PayloadValues ReadKeySetting(byte layerCode) =>
        KeyboardPayloadCodec.Decode(
            Model.ReadKeySetting,
            SendRawTwice(KeyboardProtocol.LayeredKeySettingHeader(layerCode)),
            Model.KeyList);

    public byte[] WritePerformance(PayloadValues values) =>
        SendVerified("setPerformance", CommandSetPerformance, Model.WritePerformance, values,
            () => PerformanceMatches(values, ReadPerformance()));

    public byte[] WriteLightColor(PayloadValues values)
    {
        var tail = TailGroupBModels.Contains(Model.Model, StringComparer.OrdinalIgnoreCase) ? TailGroupB : TailGroupA;
        return SendVerified("setLightColor", CommandSetLightColor, Model.WriteLightColor, values,
            () => LightMatches(values, ReadLightColor()), tail);
    }

    public byte[] WriteKeySetting(PayloadValues values) =>
        SendVerified("setKeySetting", CommandSetKeySetting, Model.WriteKeySetting, ExpandKeyTable(values),
            () => KeyMatches(values, ReadKeySetting(KeyLayerOf(values))));

    /// <summary>
    /// 写命令的布局是「一个键一个字段名」，而读回来的是按位序排的表，
    /// commonObj 只提供写报文需要的后缀和缺省占位；读回表中的每个槽位都必须保留。
    /// 部分固件在 space1..13 等保留槽内也存有非零值，不能用公共零值覆盖。
    /// </summary>
    private PayloadValues ExpandKeyTable(PayloadValues values)
    {
        var merged = values.Clone();
        var table = values.GetTable("#keyList");
        foreach (var (name, bytes) in Model.KeyWriteCommon)
        {
            merged.SetBuffer(name, bytes);
        }

        for (int i = 0; i < table.Count && i < Model.KeyList.Count; i++)
        {
            merged.SetBuffer(Model.KeyList[i].Name, table[i]);
        }

        return merged;
    }

    /// <summary>DIY 灯区颜色表：每键三通道各 126 字节。</summary>
    public byte[] WriteDiyLight(PayloadValues values) =>
        SendVerified("setDiyLight", CommandSetDiyLight, Model.WriteDiyLight, values,
            () => TableMatches(values, ReadDiyLight()));

    /// <summary>读逐键灯色表（rTable / gTable / bTable 各 126 字节）。</summary>
    public PayloadValues ReadDiyLight() =>
        KeyboardPayloadCodec.Decode(Model.ReadDiyLight, ReadTwice("getDiyLight"));

    // ---------------- 回包可信度 ----------------

    private byte[] ReadTwice(string commandName) => ReadStable(commandName, () => _session.Read(commandName));

    private byte[] SendRawTwice(byte[] payload) =>
        ReadStable(Guid.NewGuid().ToString("N")[..6], () => _session.SendRaw(payload));

    /// <summary>
    /// 反复读，直到连续两次回包一致。
    ///
    /// 设备固件只有一个报文缓冲区，忙的时候会把上一次的内容原样丢回来
    /// （实测见过数据区开头就是我们自己的请求字节）。这种回包解出来是假值，
    /// 再写回去就会把键盘设置改乱，所以凡是会参与写入的数据都要这样读稳了再用。
    ///
    /// 不接受与更早回包重复但和上一份不同的内容，避免 A/B/A 残留被当作稳定配置。
    /// </summary>
    private byte[] ReadStable(string label, Func<byte[]> read)
    {
        const int maxReads = 4;
        var seen = new List<byte[]>(maxReads) { read() };

        for (int i = 1; i < maxReads; i++)
        {
            // 设备只有一个报文缓冲区，连着读会拿到残留；先让它缓一口气再读第二遍
            Thread.Sleep(40);

            var next = read();
            if (seen[^1].AsSpan(2).SequenceEqual(next.AsSpan(2)))
            {
                return next;
            }

            _log?.Invoke(
                $"{label}: 第 {i + 1} 次回包与前几次都不一样" +
                $"（首个差异在第 {FirstDifference(seen[^1], next)} 字节），继续读");
            seen.Add(next);
        }

        throw new KeyboardSession.NotReadyException(
            $"{label} 连读 {maxReads} 次都没出现两份一致的回包（设备一直在给残留缓冲区）。" +
            "先别改设置，稍后重试一次。");
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        int limit = Math.Min(a.Length, b.Length);
        for (int i = 0; i < limit; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return limit;
    }

    // ---------------- 写完回读确认 ----------------

    /// <summary>
    /// 写 → 回读 → 比对，对不上就重写一次。
    ///
    /// 设备偶尔会「收下了但没存对」，或者我们拿到的回包是残留缓冲区；
    /// 静默失败会让用户以为设置生效了（甚至把乱数据写进设备），
    /// 所以这里必须验一次，实在存不对就如实报错。
    /// </summary>
    private byte[] SendVerified(
        string name, byte command, CommandSchema schema, PayloadValues values,
        Func<bool> verify, byte[]? tail = null)
    {
        var response = Send(name, command, schema, values, tail);

        for (int attempt = 0; attempt <= 2; attempt++)
        {
            // 写完先让固件喘一口气再回读：它要把这一包写进配置存储，
            // 立刻回读常常读到的是写之前的旧值（实测就是这个现象：
            // 「setPerformance 回读对不上，重写第 1 次」，加了这段之后第一次就对上了）。
            Thread.Sleep(250);

            if (verify())
            {
                return response;
            }

            if (attempt < 2)
            {
                _log?.Invoke($"键盘 {name} 回读对不上，重写第 {attempt + 1} 次");
                response = Send(name, command, schema, values, tail);
            }
        }

        throw new KeyboardSession.NotReadyException(
            $"{name} 没有按预期保存（写完回读对不上）。请重试一次；" +
            "如果一直这样，把键盘拔插一下再试。");
    }

    private static bool PerformanceMatches(PayloadValues want, PayloadValues got)
    {
        foreach (var name in new[]
                 {
                     "rateVal", "sleepTimeVal", "latencySwitch", "winKeySwitch",
                     "macSwitch", "lightType", "lightMode",
                 })
        {
            if (want.Has(name) && want.GetNumber(name) != got.GetNumber(name))
            {
                return false;
            }
        }

        // 当前灯效自己的亮度/速度槽位（该灯效有这个字段时才比）
        int mode = want.GetNumber("lightMode");
        foreach (var prefix in new[] { "brightness", "speed", "multiColor" })
        {
            var name = $"{prefix}{mode}";
            if (want.Has(name) && want.GetNumber(name) != got.GetNumber(name))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LightMatches(PayloadValues want, PayloadValues got)
    {
        foreach (var name in want.Keys.Where(k => k.StartsWith("lightModeColor", StringComparison.Ordinal)))
        {
            if (!want.GetBuffer(name).SequenceEqual(got.GetBuffer(name)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool KeyMatches(PayloadValues want, PayloadValues got)
    {
        var expected = want.GetTable("#keyList");
        var actual = got.GetTable("#keyList");
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (!expected[i].SequenceEqual(actual[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TableMatches(PayloadValues want, PayloadValues got)
    {
        foreach (var name in new[] { "rTable", "gTable", "bTable" })
        {
            if (!want.GetBuffer(name).SequenceEqual(got.GetBuffer(name)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>按键层写在报文头的第二个字节里，从读回来的 space0 取。</summary>
    private static byte KeyLayerOf(PayloadValues values)
    {
        var space0 = values.GetBuffer("space0");
        return space0.Length > 0 ? space0[0] : (byte)0;
    }

    private byte[] Send(string name, byte command, CommandSchema schema, PayloadValues values, byte[]? tail = null)
    {
        var payload = KeyboardPayloadCodec.Encode(schema, values, command, tail);

        // 残缺的写命令 = 把缺的字段全清零。宁可拒绝发送并报错，也不能发出去
        // ——实测这就是「键盘还亮着但灯全黑 / 设置被清空」的来源。
        int expected = 1 + KeyboardPayloadCodec.ExpectedSize(schema) + (tail?.Length ?? 0);
        if (payload.Length < expected || payload.Length > KeyboardProtocol.WiredPayloadLength)
        {
            throw new KeyboardSession.NotReadyException(
                $"拒绝发送不完整的 {name} 命令（只编出 {payload.Length} 字节，完整需要 {expected} 字节）。" +
                "这通常说明设置还没从设备读回来——直接写下去会把设备里对应的那块配置清零。" +
                "先点「读取当前设置」重读一次再改。");
        }

        _log?.Invoke($"键盘写 {name} 命令 {command:X2}，payload {payload.Length} 字节");
        return _session.SendRaw(payload);
    }
}
