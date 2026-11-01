using MchoseHub.Core.Protocol;
using MchoseHub.Core.Transport;

namespace MchoseHub.Core.Services;

/// <summary>Isolated, volatile K87 preview. This transport has no access to physical devices.</summary>
public sealed class PreviewKeyboardSession : IHidSession
{
    private readonly KeyboardModelSchema _model = KeyboardModelSchemas.Get("K87");
    private readonly Dictionary<(byte Command, byte Layer), byte[]> _stored = [];
    private byte[] _last = [];
    private byte[] _macroDirectory = new byte[4];
    public HidDeviceInfo Device { get; } = new() { Path = "preview://K87", VendorId = 0x05AC, ProductId = 0x024F,
        ProductName = "MCHOSE K87", UsagePage = 0xFF00, FeatureReportLength = 520 };
    public bool IsOpen { get; private set; } = true;
    public event EventHandler<HidInputReport>? InputReportReceived { add { } remove { } }
    public void Dispose() => IsOpen = false;
    public void SendOutput(byte reportId, byte[] payload) => throw new NotSupportedException("离线体验只使用内存配置。");
    public void SendFeature(byte reportId, byte[] payload)
    {
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        _last = payload.ToArray();
        if (payload[0] >= 0x80) return;
        if (payload[0] == 5)
        {
            if (payload[4] == 0) _macroDirectory = payload[7..11];
            return;
        }
        byte layer = payload[0] == 3 ? payload[1] : (byte)0;
        _stored[((byte)(payload[0] | 0x80), layer)] = payload.ToArray();
    }
    public byte[] GetFeature(byte reportId, int length)
    {
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        var report = new byte[KeyboardProtocol.WiredPayloadLength + 1]; report[0] = reportId;
        byte command = _last[0], layer = command == 0x83 ? _last[1] : (byte)0;
        if (_stored.TryGetValue((command, layer), out var stored))
        {
            stored.CopyTo(report, 1); _last.AsSpan(0, 7).CopyTo(report.AsSpan(1)); return report;
        }
        var schema = command switch { 0x84 => _model.ReadPerformance, 0x8A => _model.ReadLightColor,
            0x86 => _model.ReadDiyLight, 0x83 => _model.ReadKeySetting, _ => null };
        _last.AsSpan(0, 7).CopyTo(report.AsSpan(1));
        if (schema is null)
        {
            if (command == 0x82) _macroDirectory.CopyTo(report, 8);
            return report;
        }
        var values = new PayloadValues();
        foreach (var field in schema.Fields)
        {
            switch (field.Kind)
            {
                case FieldKind.Uint8: case FieldKind.Bit4:
                    values.SetNumber(field.Name, field.Name switch { "rateVal" => 3, "latencySwitch" => 2,
                        "lightMode" => 1, _ => field.Name.StartsWith("brightness", StringComparison.Ordinal) ? 80 : 0 }); break;
                case FieldKind.FixedArray:
                    values.SetBuffer(field.Name, field.Name == "space0" ? _last[1..7] : new byte[field.Length]); break;
                case FieldKind.KeyTable:
                    values.SetTable(field.Name, _model.KeyList.Select(_ => new byte[4]).ToList()); break;
            }
        }
        KeyboardPayloadCodec.Encode(new CommandSchema { Command = "preview", Fields = schema.Fields }, values, command).CopyTo(report, 1);
        return report;
    }
}
