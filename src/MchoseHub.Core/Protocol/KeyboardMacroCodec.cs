using System.Buffers.Binary;
using System.Text;

namespace MchoseHub.Core.Protocol;

public enum MacroTrigger { Hold = 0, Toggle = 1, Once = 2 }
public sealed record MacroStep(string Key, bool Down, int DelayMs = 50, bool Mouse = false);
public sealed record KeyboardMacro(string Name, MacroTrigger Trigger, IReadOnlyList<MacroStep> Steps);

/// <summary>Official Ioe/Nlt/Eoe encoding: UTF-8 names, LE address table, four-byte events, 512-byte pages.</summary>
public static class KeyboardMacroCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Dictionary<string, byte> MouseCodes = new()
    { ["Left"] = 1, ["Middle"] = 4, ["Right"] = 2, ["Forward"] = 16, ["Back"] = 8 };
    private static readonly HashSet<byte> OrdinaryExtended = [79, 80, 81, 82, 98, 89, 90, 91, 92, 93, 94, 95, 96, 97, 87, 86, 85, 84, 99, 88, 76, 72, 73, 74, 75, 77, 78, 70, 71, 83];

    public static byte[] EncodeEvents(IReadOnlyList<MacroStep> steps)
    {
        if (steps.Count == 0 || steps.Count > 15000) throw new ArgumentException("宏须包含 1–15000 个事件。");
        var output = new byte[steps.Count * 4];
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.DelayMs is < 0 or > 60000) throw new ArgumentOutOfRangeException(nameof(steps), "延时须为 0–60000 毫秒。");
            byte code;
            if (!(step.Mouse ? MouseCodes.TryGetValue(step.Key, out code) : KeyboardFunctions.MacroKeys.TryGetValue(step.Key, out code)))
                throw new ArgumentException($"官方宏码表中没有 {step.Key}。");
            int type = step.Mouse ? 2 : code > 69 && !OrdinaryExtended.Contains(code) ? 1 : 0;
            output[i * 4] = (byte)((step.Down ? 0 : 0x80) | type << 4 | step.DelayMs >> 16);
            output[i * 4 + 1] = (byte)(step.DelayMs >> 8);
            output[i * 4 + 2] = (byte)step.DelayMs;
            output[i * 4 + 3] = code;
        }
        return output;
    }

    public static byte[] EncodeBank(IReadOnlyList<KeyboardMacro> macros)
    {
        if (macros.Count is < 1 or > 255) throw new ArgumentException("宏库须包含 1–255 个宏。");
        if (macros.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != macros.Count)
            throw new ArgumentException("宏名称不能重复。");
        var entries = macros.Select(m =>
        {
            if (!Enum.IsDefined(m.Trigger)) throw new ArgumentException("未知宏触发方式。");
            var name = Utf8.GetBytes(m.Name ?? "");
            if (name.Length is < 1 or > 255) throw new ArgumentException("宏名称须为 1–255 个 UTF-8 字节。");
            return new byte[] { (byte)name.Length }.Concat(name).Concat(EncodeEvents(m.Steps)).ToArray();
        }).ToArray();
        int size = checked(macros.Count * 4 + entries.Sum(e => e.Length));
        if (size > ushort.MaxValue) throw new ArgumentException("宏库超过 65535 字节。");
        var bank = new byte[size];
        int address = macros.Count * 4;
        for (int i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bank.AsSpan(i * 4), (ushort)address);
            BinaryPrimitives.WriteUInt16LittleEndian(bank.AsSpan(i * 4 + 2), (ushort)entries[i].Length);
            entries[i].CopyTo(bank, address);
            address += entries[i].Length;
        }
        return bank;
    }

    public static IReadOnlyList<byte[]> BuildPages(byte[] bank)
    {
        if (bank.Length is < 1 or > ushort.MaxValue) throw new ArgumentException("宏库长度无效。");
        int total = (bank.Length + 511) / 512;
        var pages = new List<byte[]>(total);
        for (int i = 0; i < total; i++)
        {
            int count = Math.Min(512, bank.Length - i * 512);
            var page = new byte[7 + count];
            page[0] = 5; page[3] = (byte)total; page[4] = (byte)i;
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(5), (ushort)count);
            bank.AsSpan(i * 512, count).CopyTo(page.AsSpan(7));
            pages.Add(page);
        }
        return pages;
    }

    public static byte[] Binding(MacroTrigger trigger, int index)
    {
        if (index is < 0 or > 254 || !Enum.IsDefined(trigger)) throw new ArgumentOutOfRangeException(nameof(index));
        return [3, trigger switch { MacroTrigger.Hold => 4, MacroTrigger.Toggle => 2, _ => 1 }, 1, (byte)index];
    }
}
