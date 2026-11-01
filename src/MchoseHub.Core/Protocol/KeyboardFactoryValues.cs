using System.Text.Json;

namespace MchoseHub.Core.Protocol;

/// <summary>Official I8 otherObj/lightRgbObj/lightDiyObj defaults; no guessed fallback.</summary>
public static class KeyboardFactoryValues
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, PayloadValues>>> Cache = new(Load);
    public static PayloadValues Get(string model, string section) => Cache.Value[model][section].Clone();
    private static Dictionary<string, Dictionary<string, PayloadValues>> Load()
    {
        using var stream = typeof(KeyboardFactoryValues).Assembly.GetManifestResourceStream("MchoseHub.Core.Resources.keyboard-factory.json")
            ?? throw new InvalidOperationException("出厂数据资源缺失。");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("models").EnumerateObject().ToDictionary(m => m.Name, m => m.Value.EnumerateObject().ToDictionary(s => s.Name, s =>
        {
            var values = new PayloadValues(); foreach (var field in s.Value.EnumerateObject())
            {
                if (field.Value.ValueKind == JsonValueKind.Number) values.SetNumber(field.Name, field.Value.GetInt32());
                else if (field.Value.ValueKind == JsonValueKind.Array) values.SetBuffer(field.Name, field.Value.EnumerateArray().Select(v => v.GetByte()).ToArray());
            }
            return values;
        }));
    }
}
