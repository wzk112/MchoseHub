using MchoseHub.Core.Protocol;

namespace MchoseHub.Core.Services;

/// <summary>Validates a complete restore plan without opening a device or sending reports.</summary>
public static class KeyboardProfileValidator
{
    public static void Validate(KeyboardProfile profile, KeyboardModelSchema model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (profile is null || profile.Version != 1 || profile.Model != model.Model || profile.KeyLayers is null || profile.KeyLayers.Count != 3)
            throw new InvalidDataException("配置版本、型号或按键层数量不匹配。");

        ValidateSnapshot(model, model.ReadPerformance, profile.Performance, [0, 0, 1, 0, 128, 0]);
        ValidateSnapshot(model, model.ReadLightColor, profile.Lighting, [0, 0, 1, 0, 0, 2]);
        ValidateSnapshot(model, model.ReadDiyLight, profile.Diy, [0, 0, 1, 0, 122, 1]);
        for (byte i = 0; i < 3; i++) ValidateSnapshot(model, model.ReadKeySetting, profile.KeyLayers[i], [i, 0, 1, 0, 248, 1]);

        var performance = profile.Performance.ToValues();
        if ((performance.Has("rateVal") && performance.GetNumber("rateVal") is < 1 or > 3) || performance.GetNumber("latencySwitch") > 3
            || performance.GetNumber("winKeySwitch") > 1 || performance.GetNumber("macSwitch") > 3
            || performance.GetNumber("lightMode") > 21 || performance.GetNumber("lightType") > 1)
            throw new InvalidDataException("备份中的性能参数无效。");
        foreach (var field in model.ReadPerformance.Scalars.Where(f => f.Name.StartsWith("brightness", StringComparison.Ordinal)))
            if (performance.GetNumber(field.Name) > 100) throw new InvalidDataException("备份中的亮度无效。");

        // Compile all writes before accepting any file for device restoration.
        KeyboardPayloadCodec.Encode(model.WritePerformance, performance, 4);
        string[] groupB = ["G98_Pro", "G98_V2", "G87_V2", "UT98", "Z75", "Z75S"];
        var tail = new byte[groupB.Contains(model.Model, StringComparer.OrdinalIgnoreCase) ? 29 : 22];
        tail[^6] = 0x5A; tail[^5] = 0xA5;
        KeyboardPayloadCodec.Encode(model.WriteLightColor, profile.Lighting.ToValues(), 10, tail);
        var diy = profile.Diy.ToValues(); diy.SetBuffer("pre", model.DiyPre.ToArray());
        KeyboardPayloadCodec.Encode(model.WriteDiyLight, diy, 6);
        foreach (var layer in profile.KeyLayers)
        {
            var values = layer.ToValues(); var table = values.GetTable("#keyList");
            foreach (var (name, bytes) in model.KeyWriteCommon) values.SetBuffer(name, bytes);
            for (int i = 0; i < model.KeyList.Count; i++) values.SetBuffer(model.KeyList[i].Name, table[i]);
            KeyboardPayloadCodec.Encode(model.WriteKeySetting, values, 3);
        }
    }

    private static void ValidateSnapshot(KeyboardModelSchema model, CommandSchema schema, PayloadSnapshot snapshot, byte[] header)
    {
        if (snapshot?.Numbers is null || snapshot.Buffers is null || snapshot.Tables is null)
            throw new InvalidDataException($"{schema.Command} 配置为空或缺少字段集合。");

        var fields = schema.Fields.Where(f => f.Name != "command").ToArray();
        var allowed = fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var keys = snapshot.Numbers.Keys.Concat(snapshot.Buffers.Keys).Concat(snapshot.Tables.Keys).ToArray();
        if (keys.Length != fields.Length || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Length || keys.Any(k => !allowed.Contains(k)))
            throw new InvalidDataException($"{schema.Command} 含缺失、重复或未知字段。");

        foreach (var field in fields)
        {
            switch (field.Kind)
            {
                case FieldKind.Uint8:
                case FieldKind.Bit4:
                    if (!snapshot.Numbers.TryGetValue(field.Name, out var number) || number < 0 || number > (field.Kind == FieldKind.Bit4 ? 15 : 255))
                        throw new InvalidDataException($"{field.Name} 数值或类型无效。");
                    break;
                case FieldKind.FixedArray:
                    if (!snapshot.Buffers.TryGetValue(field.Name, out var bytes) || bytes is null || bytes.Length != field.Length)
                        throw new InvalidDataException($"{field.Name} 长度或类型与机型不符。");
                    break;
                case FieldKind.KeyTable:
                    if (!snapshot.Tables.TryGetValue(field.Name, out var table) || table is null || table.Count != model.KeyList.Count || table.Any(b => b is null || b.Length != 4))
                        throw new InvalidDataException("按键表长度或类型无效。");
                    break;
                default:
                    throw new InvalidDataException($"不支持的备份字段类型：{field.Name}。");
            }
        }
        if (!snapshot.Buffers.TryGetValue("space0", out var actualHeader) || !actualHeader.SequenceEqual(header))
            throw new InvalidDataException("备份中的协议头无效。");
    }
}
