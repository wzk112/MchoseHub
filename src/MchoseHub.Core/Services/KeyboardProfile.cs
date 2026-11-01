using MchoseHub.Core.Protocol;

namespace MchoseHub.Core.Services;

public sealed record PayloadSnapshot(Dictionary<string, int> Numbers, Dictionary<string, byte[]> Buffers,
    Dictionary<string, List<byte[]>> Tables)
{
    public static PayloadSnapshot From(PayloadValues values) => new(
        values.Numbers.ToDictionary(p => p.Key, p => p.Value),
        values.Buffers.ToDictionary(p => p.Key, p => p.Value.ToArray()),
        values.Tables.ToDictionary(p => p.Key, p => p.Value.Select(b => b.ToArray()).ToList()));
    public PayloadValues ToValues()
    {
        var values = new PayloadValues();
        foreach (var (k, v) in Numbers) values.SetNumber(k, v);
        foreach (var (k, v) in Buffers) values.SetBuffer(k, v.ToArray());
        foreach (var (k, v) in Tables) values.SetTable(k, v.Select(b => b.ToArray()).ToList());
        return values;
    }
}

public sealed record KeyboardProfile(int Version, string Model, PayloadSnapshot Performance,
    PayloadSnapshot Lighting, PayloadSnapshot Diy, IReadOnlyList<PayloadSnapshot> KeyLayers);
