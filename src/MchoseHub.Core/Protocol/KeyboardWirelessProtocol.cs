namespace MchoseHub.Core.Protocol;

/// <summary>Official j3e output/input protocol: report 0x13, 14 data bytes per packet.</summary>
public static class KeyboardWirelessProtocol
{
    public const byte ReportId = 0x13;
    public const int PacketLength = 19;
    public const int ChunkLength = 14;
    public const int MaxDataLength = 255 * ChunkLength;

    private static readonly Dictionary<string, string> ReceiverModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["03000000021C"] = "G87",
        ["030000000077"] = "K99",
        ["0300000001DF"] = "K99_V2",
        ["030000000132"] = "G98",
        ["0300000000D6"] = "X75",
        ["0A0000000003"] = "G98_Pro",
        ["0300000001C6"] = "X75_V2",
        ["030000000115"] = "G75_Pro",
        ["0A0000000018"] = "G98_V2",
        ["0A000000002B"] = "UT98",
        ["0A0000000001"] = "Z75",
        ["0A0000000009"] = "Z75S",
        ["0300000001C7"] = "KX75",
        ["030000000174"] = "GX87",
        ["0300000001ED"] = "GX87_V2",
        ["030000000135"] = "K87",
        ["030000000335"] = "G75_V2",
        ["0A0000000034"] = "G87_V2",
    };

    public static string Identify(ReadOnlySpan<byte> signature) =>
        signature.Length == 6 && ReceiverModels.TryGetValue(Convert.ToHexString(signature), out var model)
            ? model : throw new InvalidDataException("接收器未返回已收录的键盘型号标识；已停止配置写入。");

    public static byte ReadCode(byte wiredCode) => wiredCode switch
    {
        0x87 => 0x4A,
        0x84 => 0x44,
        0x8A => 0x49,
        0x83 => 0x41,
        0x86 => 0x42,
        _ => throw new ArgumentException("无线读命令未收录。", nameof(wiredCode)),
    };

    public static byte[] ReadRequest(byte command, byte layer = 0)
    {
        if (layer > 2) throw new ArgumentOutOfRangeException(nameof(layer));
        var packet = new byte[PacketLength];
        packet[0] = command; packet[1] = 1;
        if (command == 0x41) packet[3] = (byte)(layer << 4);
        if (command == 0x43) { packet[3] = 0x10; packet[4] = 3; packet[5] = 2; }
        packet[^1] = Checksum(packet.AsSpan(0, PacketLength - 1));
        return packet;
    }

    public static IReadOnlyList<byte[]> WritePackets(byte command, byte[] data, byte layer = 0)
    {
        if (data.Length is < 1 or > MaxDataLength) throw new ArgumentException("无线配置最多 255 个分包（3570 字节）。", nameof(data));
        if (layer > 2) throw new ArgumentOutOfRangeException(nameof(layer));
        if (command is not (1 or 2 or 3 or 4 or 6 or 9)) throw new ArgumentException("无线写命令未收录。", nameof(command));
        int total = (data.Length + ChunkLength - 1) / ChunkLength;
        var packets = new List<byte[]>(total);
        for (int sequence = 0; sequence < total; sequence++)
        {
            var chunk = data.AsSpan(sequence * ChunkLength, Math.Min(ChunkLength, data.Length - sequence * ChunkLength));
            var packet = new byte[PacketLength]; packet[0] = command; packet[1] = (byte)total; packet[2] = (byte)sequence;
            int yy = command switch
            {
                1 => 14 + (layer << 4),
                2 => 14,
                3 => ((sequence * 14 / 512) << 4) | chunk.Length,
                _ => 14,
            };
            if (sequence == total - 1)
            {
                if (command == 4) yy = chunk.Length;
                else if (command is 1 or 6 or 9)
                {
                    int last = chunk.Length - 1; while (last >= 0 && chunk[last] == 0) last--;
                    yy = last < 0 ? 0 : 14 + (command == 1 ? layer << 4 : 0) - (chunk.Length - 1 - last);
                }
            }
            packet[3] = (byte)yy; chunk.CopyTo(packet.AsSpan(4)); packet[^1] = Checksum(packet.AsSpan(0, PacketLength - 1));
            packets.Add(packet);
        }
        return packets;
    }

    public static byte Checksum(ReadOnlySpan<byte> packet)
    {
        int sum = ReportId; foreach (byte b in packet) sum += b; return (byte)sum;
    }
}
