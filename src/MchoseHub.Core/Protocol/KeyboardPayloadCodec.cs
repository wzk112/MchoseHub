namespace MchoseHub.Core.Protocol;

/// <summary>解码出来的一份配置：标量用数字，数组用字节。</summary>
public sealed class PayloadValues
{
    private readonly Dictionary<string, int> _numbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<byte[]>> _tables = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> Numbers => _numbers;
    public IReadOnlyDictionary<string, byte[]> Buffers => _buffers;
    public IReadOnlyDictionary<string, List<byte[]>> Tables => _tables;

    /// <summary>取值顺序：写入顺序即字段声明顺序。</summary>
    public IEnumerable<string> Keys => _numbers.Keys.Concat(_buffers.Keys).Concat(_tables.Keys);

    public int GetNumber(string name) => _numbers.TryGetValue(name, out var value) ? value : 0;
    public void SetNumber(string name, int value)
    {
        if (value is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(value));
        _numbers[name] = value;
    }

    public byte[] GetBuffer(string name) => _buffers.TryGetValue(name, out var value) ? value : [];
    public void SetBuffer(string name, byte[] value) => _buffers[name] = value;

    public List<byte[]> GetTable(string name) => _tables.TryGetValue(name, out var value) ? value : [];
    public void SetTable(string name, List<byte[]> value) => _tables[name] = value;

    public bool Has(string name) => _numbers.ContainsKey(name) || _buffers.ContainsKey(name) || _tables.ContainsKey(name);

    public void SetBytes(string name, byte[] value)
    {
        if (value.Length == 1)
        {
            SetNumber(name, value[0]);
        }
        else
        {
            SetBuffer(name, value);
        }
    }

    public PayloadValues Clone()
    {
        var copy = new PayloadValues();
        foreach (var (key, value) in _numbers) copy._numbers[key] = value;
        foreach (var (key, value) in _buffers) copy._buffers[key] = (byte[])value.Clone();
        foreach (var (key, value) in _tables) copy._tables[key] = value.Select(v => (byte[])v.Clone()).ToList();
        return copy;
    }
}

/// <summary>
/// 按网页版的字段布局编码 / 解码。语义与 bundle 里的 JY 编码器、binary-parser 解码器一致：
/// 半字节按「先声明的占高 4 位」打包，可变数组长度由数据本身决定。
/// </summary>
public static class KeyboardPayloadCodec
{
    /// <summary>解码回包。回包前两字节是 report id 和命令字，网页版也是从第 3 个字节开始解析。</summary>
    public static PayloadValues Decode(CommandSchema schema, ReadOnlySpan<byte> response, IReadOnlyList<FieldSpec>? keyList = null)
    {
        int required = 2 + ExpectedSize(schema) + schema.Fields
            .Where(f => f.Kind == FieldKind.KeyTable && f.Length == 0)
            .Sum(_ => (keyList ?? throw new ArgumentException("缺少按键布局。", nameof(keyList))).Sum(k => k.Length > 0 ? k.Length : 4));
        if (response.Length < required) throw new InvalidDataException($"{schema.Command} 回包被截断：{response.Length}/{required} 字节。");
        var values = new PayloadValues();
        int offset = 2;
        byte? pending = null;

        foreach (var field in schema.Fields)
        {
            if (field.Name == "command") continue;
            switch (field.Kind)
            {
                case FieldKind.Uint8:
                    values.SetNumber(field.Name, At(response, offset++));
                    break;

                case FieldKind.Bit4:
                    if (pending is null)
                    {
                        pending = At(response, offset++);
                        values.SetNumber(field.Name, (pending.Value >> 4) & 0x0F);
                    }
                    else
                    {
                        values.SetNumber(field.Name, pending.Value & 0x0F);
                        pending = null;
                    }

                    break;

                case FieldKind.FixedArray:
                    var take = Math.Min(field.Length, Math.Max(0, response.Length - offset));
                    values.SetBuffer(field.Name, response.Slice(offset, take).ToArray());
                    offset += field.Length;
                    break;

                case FieldKind.KeyTable:
                    var table = new List<byte[]>();
                    foreach (var key in keyList ?? [])
                    {
                        int length = key.Length > 0 ? key.Length : 4;
                        var item = new byte[length];
                        for (int i = 0; i < length && offset + i < response.Length; i++)
                        {
                            item[i] = response[offset + i];
                        }

                        table.Add(item);
                        offset += length;
                    }

                    values.SetTable(field.Name, table);
                    break;

                case FieldKind.VariableArray:
                    // 回包里没有长度信息，交给调用方按读 schema 处理
                    break;
            }
        }

        return values;
    }

    /// <summary>
    /// 这条命令**完整**编码出来应该有多长（不含 report id、不含尾部补丁）。
    ///
    /// 用来挡住「残缺 payload」：写命令是把整包铺进设备配置的，缺字段的地方会补 0，
    /// 于是「只填了几个字段就发出去」等于**把其余字段全部清零**。实测踩过：
    /// 没先把颜色表读回来就写 setLightColor，只编出 26 字节，
    /// 设备那边整张灯效颜色表被清成 #000000——键盘照样能打字，
    /// 但灯是黑的（常亮 + 100% 亮度 + 颜色全黑）。
    /// </summary>
    public static int ExpectedSize(CommandSchema schema)
    {
        int size = 0;
        int pendingNibble = 0;

        foreach (var field in schema.Fields)
        {
            if (field.Name == "command") continue;
            switch (field.Kind)
            {
                case FieldKind.Uint8:
                    size += 1;
                    break;

                case FieldKind.Bit4:
                    // 两个 Bit4 挤在一个字节里；先出现的占高 4 位
                    if (++pendingNibble == 2)
                    {
                        pendingNibble = 0;
                        size += 1;
                    }

                    break;

                case FieldKind.FixedArray:
                case FieldKind.VariableArray:
                    size += field.Length;
                    break;

                case FieldKind.KeyTable:
                    size += field.Length;
                    break;
            }
        }

        if (pendingNibble > 0)
        {
            size += 1;
        }

        return size;
    }

    /// <summary>
    /// 编码写命令的 payload（不含 report id）。commandByte 由调用方给出，
    /// 其余字段按 schema 顺序取值，缺省补 0。
    /// </summary>
    public static byte[] Encode(CommandSchema schema, PayloadValues values, byte commandByte, byte[]? tail = null)
    {
        var output = new List<byte>(520) { commandByte };
        int? pending = null;

        foreach (var field in schema.Fields)
        {
            if (field.Name == "command")
            {
                continue;
            }

            if (!values.Has(field.Name)) throw new InvalidDataException($"{schema.Command} 缺少字段 {field.Name}，拒绝覆盖设备配置。");

            switch (field.Kind)
            {
                case FieldKind.Uint8:
                    output.Add((byte)values.GetNumber(field.Name));
                    break;

                case FieldKind.Bit4:
                    if (values.GetNumber(field.Name) > 15) throw new InvalidDataException($"{field.Name} 超过半字节范围。");
                    if (pending is null)
                    {
                        pending = values.GetNumber(field.Name) & 0x0F;
                    }
                    else
                    {
                        // 先声明的字段占高 4 位
                        output.Add((byte)((pending.Value << 4) | (values.GetNumber(field.Name) & 0x0F)));
                        pending = null;
                    }

                    break;

                case FieldKind.FixedArray:
                case FieldKind.VariableArray:
                    var buffer = values.GetBuffer(field.Name);
                    if (buffer.Length == 0 && values.Numbers.ContainsKey(field.Name))
                    {
                        buffer = [(byte)values.GetNumber(field.Name)];
                    }

                    if (buffer.Length == 0 || (field.Length > 0 && buffer.Length != field.Length))
                        throw new InvalidDataException($"{field.Name} 数组长度无效：{buffer.Length}。");

                    output.AddRange(buffer);
                    break;

                case FieldKind.KeyTable:
                    foreach (var entry in values.GetTable(field.Name))
                    {
                        output.AddRange(entry);
                    }

                    break;
            }
        }

        if (pending is not null)
        {
            output.Add((byte)(pending.Value << 4));
        }

        if (tail is not null)
        {
            output.AddRange(tail);
        }

        if (output.Count > KeyboardProtocol.WiredPayloadLength) throw new InvalidDataException("键盘配置超过 519 字节，拒绝截断。");
        return output.ToArray();
    }

    private static byte At(ReadOnlySpan<byte> buffer, int index) =>
        index >= 0 && index < buffer.Length ? buffer[index] : (byte)0;
}
