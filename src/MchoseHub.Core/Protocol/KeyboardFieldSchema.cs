using System.Text.Json;
using System.Text.Json.Serialization;

namespace MchoseHub.Core.Protocol;

/// <summary>字段类型。</summary>
public enum FieldKind
{
    /// <summary>单字节。</summary>
    Uint8,
    /// <summary>半个字节（高位在前，两个凑一字节）。</summary>
    Bit4,
    /// <summary>固定长度字节数组（只用于读回包解析）。</summary>
    FixedArray,
    /// <summary>长度由数据决定的可变数组（只用于写命令编码）。</summary>
    VariableArray,
    /// <summary>按键表展开，每项一个定长数组。</summary>
    KeyTable,
}

public sealed record FieldSpec
{
    public required string Name { get; init; }
    public required FieldKind Kind { get; init; }
    public int Length { get; init; }
}

/// <summary>一条命令的字段布局（网页版 bundle 里的 parser schema）。</summary>
public sealed record CommandSchema
{
    public required string Command { get; init; }
    public required IReadOnlyList<FieldSpec> Fields { get; init; }

    public IEnumerable<FieldSpec> Scalars =>
        Fields.Where(f => f.Kind is FieldKind.Uint8 or FieldKind.Bit4);
}

/// <summary>一个机型的全部命令布局，键为零件的默认值。</summary>
public sealed record KeyboardModelSchema
{
    public required string Model { get; init; }
    public required IReadOnlyList<FieldSpec> KeyList { get; init; }

    /// <summary>逐键灯光的写入前缀（网页版 lightDiyObj.pre）。</summary>
    public required IReadOnlyList<byte> DiyPre { get; init; }

    /// <summary>按键标识 → 逐键灯光表里的 LED 序号。</summary>
    public required IReadOnlyDictionary<string, int> LedIndex { get; init; }

    /// <summary>键盘布局（含每个键的默认 / Fn / Fn2 四字节码）。</summary>
    public required IReadOnlyList<BoardKey> Board { get; init; }
    public IReadOnlyList<string> ReservedKeys { get; init; } = [];

    public required CommandSchema ReadPerformance { get; init; }
    public required CommandSchema WritePerformance { get; init; }
    public required CommandSchema ReadLightColor { get; init; }
    public required CommandSchema WriteLightColor { get; init; }
    public required CommandSchema ReadKeySetting { get; init; }
    public required CommandSchema WriteKeySetting { get; init; }
    public required CommandSchema ReadDiyLight { get; init; }
    public required CommandSchema WriteDiyLight { get; init; }

    /// <summary>逐键灯光表的容量（一般是 126）。</summary>
    public int DiyTableSize =>
        ReadDiyLight.Fields.FirstOrDefault(f => f.Name == "rTable")?.Length ?? 126;

    /// <summary>
    /// 写按键时要一起铺进去的公共字段（网页版 I8 里的 commonObj）：
    /// space1..space13、一长串 0 的 space15、结尾的 fix = 5A A5。
    /// 少了这段设备不会报错，但改键不生效。
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> KeyWriteCommon { get; init; } =
        new Dictionary<string, byte[]>();

    /// <summary>官方那份出厂逐键灯色（网页版 lightDiyObj 的 r/g/b 表），可能为空。</summary>
    public IReadOnlyList<(byte R, byte G, byte B)>? DefaultDiyColors { get; init; }

    /// <summary>
    /// 「自定义」灯效的编号。官方给两组机型分别用了 19 和 21，
    /// 网页版里的判断是 ["G98_Pro","G98_V2","G87_V2","UT98","Z75","Z75S"] ? 19 : 21。
    /// </summary>
    public int CustomLightMode => CustomModeGroup.Contains(Model, StringComparer.OrdinalIgnoreCase) ? 19 : 21;

    private static readonly string[] CustomModeGroup =
        ["G98_Pro", "G98_V2", "G87_V2", "UT98", "Z75", "Z75S"];

    public int? FindLedIndex(string key) => LedIndex.TryGetValue(key, out var index) ? index : null;

    /// <summary>
    /// 四字节码反查按键名。网页版的 baseKeyBoard 里带着每个键的
    /// 默认 / Fn / Fn2 编码，用它就能把设备上的映射翻成人看得懂的名字。
    /// </summary>
    public string? KeyNameForValue(IReadOnlyList<byte> value, string layer = "Default")
    {
        if (value.Count == 0 || value.All(b => b == 0))
        {
            return "默认";
        }

        foreach (var key in Board)
        {
            if (key.ValueFor(layer).SequenceEqual(value))
            {
                return key.Name;
            }
        }

        return null;
    }
}

/// <summary>键盘布局里的一个键。</summary>
public sealed record BoardKey
{
    public required string Name { get; init; }
    public required string Key { get; init; }
    public required int Width { get; init; }
    public int Height { get; init; } = 42;
    public int MarginTop { get; init; }
    public int MarginLeft { get; init; }
    public int MarginRight { get; init; }
    public required byte[] DefaultVal { get; init; }
    public required byte[] FnVal { get; init; }
    public required byte[] Fn2Val { get; init; }

    public byte[] ValueFor(string layer) => layer switch
    {
        "Fn" => FnVal,
        "Fn2" => Fn2Val,
        _ => DefaultVal,
    };
}

/// <summary>
/// 从网页版 bundle 里抽出来的各机型字段布局。数据文件由
/// work/tools/extract-models.mjs 生成，作为嵌入资源随程序分发。
/// </summary>
public static class KeyboardModelSchemas
{
    private static readonly Lazy<Dictionary<string, KeyboardModelSchema>> Cache = new(Load);

    public static IReadOnlyCollection<string> Models => Cache.Value.Keys;

    public static KeyboardModelSchema Get(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model) && Cache.Value.TryGetValue(model!, out var exact))
        {
            return exact;
        }

        throw new ArgumentException($"未收录的键盘型号：{model}", nameof(model));
    }

    public static bool Has(string model) => Cache.Value.ContainsKey(model);

    private static Dictionary<string, KeyboardModelSchema> Load()
    {
        using var stream = typeof(KeyboardModelSchemas).Assembly
            .GetManifestResourceStream("MchoseHub.Core.Resources.keyboard-models.json")
            ?? throw new InvalidOperationException("嵌入资源 keyboard-models.json 缺失。");

        using var document = JsonDocument.Parse(stream);
        var payload = document.RootElement.Deserialize<SchemaFile>(JsonOptions)
            ?? throw new InvalidOperationException("keyboard-models.json 解析失败。");
        var layouts = ParseLayouts(document.RootElement);

        var result = new Dictionary<string, KeyboardModelSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, item) in payload.Models)
        {
            result[model] = new KeyboardModelSchema
            {
                Model = model,
                KeyList = Convert(item.KeyList),
                DiyPre = layouts.TryGetValue(model, out var layout) ? layout.Pre : [0, 0, 1, 0, 128, 1],
                LedIndex = BuildLedIndex(Convert(item.KeyList)),
                Board = layouts.TryGetValue(model, out var layout2)
                    ? layout2.Board
                    : [],
                ReadPerformance = Schema(model, "getPerformance", NormalizeNibbles(item.GetPerformance)),
                WritePerformance = Schema(model, "setPerformance", item.SetPerformance),
                ReadLightColor = Schema(model, "getLightColor", item.GetLightColor),
                WriteLightColor = Schema(model, "setLightColor", item.SetLightColor),
                ReadKeySetting = Schema(model, "getKeySetting", item.GetKeySetting),
                WriteKeySetting = Schema(model, "setKeySetting", item.SetKeySetting),
                ReadDiyLight = Schema(model, "getDiyLight", item.GetDiyLight),
                WriteDiyLight = Schema(model, "setDiyLight", item.SetDiyLight),
                DefaultDiyColors = layouts.TryGetValue(model, out var layout3) ? layout3.DefaultColors : null,
                KeyWriteCommon = layouts.TryGetValue(model, out var layout4) ? layout4.KeyWriteCommon : [],
                ReservedKeys = layouts.TryGetValue(model, out var layout5) ? layout5.ReservedKeys : [],
            };
        }

        return result;
    }

    /// <summary>键盘布局单独用 JsonDocument 读，避免和命令 schema 的类型互相干扰。</summary>
    private static Dictionary<string, KeyboardLayoutRaw> ParseLayouts(JsonElement root)
    {
        var result = new Dictionary<string, KeyboardLayoutRaw>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("layouts", out var layouts))
        {
            return result;
        }

        foreach (var entry in layouts.EnumerateObject())
        {
            var board = new List<BoardKey>();
            if (entry.Value.TryGetProperty("board", out var boardElement))
            {
                foreach (var item in boardElement.EnumerateArray())
                {
                    board.Add(new BoardKey
                    {
                        Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                        Key = item.TryGetProperty("key", out var key) ? key.GetString() ?? string.Empty : string.Empty,
                        Width = item.TryGetProperty("width", out var width) ? width.GetInt32() : 42,
                        Height = item.TryGetProperty("height", out var height) ? height.GetInt32() : 42,
                        MarginTop = item.TryGetProperty("top", out var top) ? top.GetInt32() : 0,
                        MarginLeft = item.TryGetProperty("left", out var left) ? left.GetInt32() : 0,
                        MarginRight = item.TryGetProperty("right", out var right) ? right.GetInt32() : 0,
                        DefaultVal = Bytes(item, "def"),
                        FnVal = Bytes(item, "fn"),
                        Fn2Val = Bytes(item, "fn2"),
                    });
                }
            }

            var pre = entry.Value.TryGetProperty("diyPre", out var preElement)
                ? preElement.EnumerateArray().Select(b => (byte)b.GetInt32()).ToList()
                : [0, 0, 1, 0, 128, 1];

            var red = Bytes(entry.Value, "diyRed");
            var green = Bytes(entry.Value, "diyGreen");
            var blue = Bytes(entry.Value, "diyBlue");
            List<(byte, byte, byte)>? defaults = null;
            if (red.Length > 0 && red.Length == green.Length && red.Length == blue.Length)
            {
                defaults = Enumerable.Range(0, red.Length).Select(i => (red[i], green[i], blue[i])).ToList();
            }

            var common = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (entry.Value.TryGetProperty("keyWriteCommon", out var commonElement))
            {
                foreach (var field in commonElement.EnumerateObject())
                {
                    common[field.Name] = Bytes(commonElement, field.Name);
                }
            }

            var reserved = entry.Value.TryGetProperty("reservedKeys", out var reservedElement)
                ? reservedElement.EnumerateArray().Select(e => e.GetString()!).ToList() : [];
            result[entry.Name] = new KeyboardLayoutRaw(board, pre, defaults, common, reserved);
        }

        return result;
    }

    private static byte[] Bytes(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray()
            : [];

    private sealed record KeyboardLayoutRaw(
        List<BoardKey> Board,
        List<byte> Pre,
        List<(byte R, byte G, byte B)>? DefaultColors,
        Dictionary<string, byte[]> KeyWriteCommon, List<string> ReservedKeys);

    private static CommandSchema Schema(string model, string command, List<RawField> fields) =>
        new() { Command = $"{model}/{command}", Fields = Convert(fields) };

    /// <summary>按键标识 → LED 序号：就是按键表里的位序，跳过后缀为占位的 spaceN。</summary>
    private static Dictionary<string, int> BuildLedIndex(List<FieldSpec> keyList)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < keyList.Count; i++)
        {
            map.TryAdd(keyList[i].Name, i);
        }

        return map;
    }

    /// <summary>
    /// 网页版自己的读、写布局在灯光档位的半字节顺序上是矛盾的：
    /// getPerformance 按 (multiColor, speed) 解，setPerformance 按 (speed, multiColor) 编。
    /// 真机只可能有一种布局，而能改变灯光行为的是写命令，所以以写为准，
    /// 把读布局里的 (multiColor, speed) 对调成 (speed, multiColor)。
    /// </summary>
    private static List<RawField> NormalizeNibbles(List<RawField> fields)
    {
        var output = new List<RawField>(fields);
        for (int i = 0; i + 1 < output.Count; i++)
        {
            bool isPair = output[i].K == "b4" && output[i + 1].K == "b4";
            if (!isPair)
            {
                continue;
            }

            if (output[i].N.StartsWith("multiColor", StringComparison.Ordinal)
                && output[i + 1].N.StartsWith("speed", StringComparison.Ordinal))
            {
                (output[i], output[i + 1]) = (output[i + 1], output[i]);
            }
        }

        return output;
    }

    private static List<FieldSpec> Convert(List<RawField> fields) =>
        fields.Select(f => new FieldSpec
        {
            Name = f.N,
            Kind = f.K switch
            {
                "u8" => FieldKind.Uint8,
                "b4" => FieldKind.Bit4,
                "arr" => FieldKind.FixedArray,
                "var" => FieldKind.VariableArray,
                "keys" => FieldKind.KeyTable,
                _ => throw new InvalidOperationException($"未知字段类型：{f.K}"),
            },
            Length = f.Len.ValueKind == JsonValueKind.Number ? f.Len.GetInt32() : 0,
        }).ToList();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record RawField
    {
        [JsonPropertyName("n")] public required string N { get; init; }
        [JsonPropertyName("k")] public required string K { get; init; }
        [JsonPropertyName("len")] public JsonElement Len { get; init; }
    }

    private sealed record RawItem
    {
        public required List<RawField> KeyList { get; init; }
        public required List<RawField> GetPerformance { get; init; }
        public required List<RawField> SetPerformance { get; init; }
        public required List<RawField> GetLightColor { get; init; }
        public required List<RawField> SetLightColor { get; init; }
        public required List<RawField> GetKeySetting { get; init; }
        public required List<RawField> SetKeySetting { get; init; }
        public required List<RawField> GetDiyLight { get; init; }
        public required List<RawField> SetDiyLight { get; init; }
    }

    private sealed record SchemaFile
    {
        public required Dictionary<string, RawItem> Models { get; init; }
    }
}
