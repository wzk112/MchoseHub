namespace MchoseHub.Core.Protocol;

/// <summary>
/// 帧的构造与解析。等价于网页版的 vd() / ppe() / fpe() / tp()。
///
/// 帧结构（默认 63 字节）：
///   [0]     0xAA        起始字节
///   [1]     0x01        协议版本
///   [2]     flags       0x01 = 带校验
///   [3]     length      payload 长度 + 1
///   [4..5]  cmdType
///   [6]     cmd
///   [7..]   payload
///   [7+n]   checksum    flags ^ length ^ cmdType[0] ^ cmdType[1] ^ cmd ^ payload
/// </summary>
public static class MchoseFrameCodec
{
    public static byte Checksum(byte flags, byte length, byte[] cmdType, byte cmd, byte[] payload)
    {
        int acc = flags ^ length ^ cmdType[0] ^ cmdType[1];
        acc ^= cmd;
        for (int i = 0; i < payload.Length && i < length - 1; i++)
        {
            acc ^= payload[i];
        }
        return (byte)(acc & 0xFF);
    }

    public static byte[] Build(
        byte[] cmdType,
        byte cmd,
        byte[]? payload = null,
        bool check = false,
        int reportLength = MchoseProtocol.DefaultReportLength)
    {
        ArgumentNullException.ThrowIfNull(cmdType);
        if (cmdType.Length != 2)
        {
            throw new ArgumentException("cmdType 必须是长度 2 的数组", nameof(cmdType));
        }
        if (reportLength < MchoseProtocol.OffsetPayload + (payload?.Length ?? 0) + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(reportLength), "reportLength 太小，放不下 payload 与校验位");
        }

        payload ??= [];
        if (payload.Length > 254) throw new ArgumentOutOfRangeException(nameof(payload), "帧长度字段只有一个字节。");
        byte flags = (byte)(check ? 0x01 : 0x00);
        byte length = (byte)(payload.Length + 1);
        byte sum = check ? Checksum(flags, length, cmdType, cmd, payload) : (byte)0x00;

        var frame = new byte[reportLength];
        frame[MchoseProtocol.OffsetStart] = MchoseProtocol.StartByte;
        frame[MchoseProtocol.OffsetVersion] = MchoseProtocol.Version;
        frame[MchoseProtocol.OffsetFlags] = flags;
        frame[MchoseProtocol.OffsetLength] = length;
        frame[MchoseProtocol.OffsetCmdType] = cmdType[0];
        frame[MchoseProtocol.OffsetCmdType + 1] = cmdType[1];
        frame[MchoseProtocol.OffsetCmd] = cmd;
        Array.Copy(payload, 0, frame, MchoseProtocol.OffsetPayload, payload.Length);
        frame[MchoseProtocol.OffsetPayload + payload.Length] = sum;
        return frame;
    }

    /// <summary>解析一帧；结构不合法返回 null。</summary>
    public static MchoseFrame? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MchoseProtocol.OffsetPayload + 1)
        {
            return null;
        }
        if (bytes[MchoseProtocol.OffsetStart] != MchoseProtocol.StartByte || bytes[MchoseProtocol.OffsetVersion] != MchoseProtocol.Version)
        {
            return null;
        }

        byte flags = bytes[MchoseProtocol.OffsetFlags];
        if (flags > 1) return null;
        byte length = bytes[MchoseProtocol.OffsetLength];
        if (length < 1)
        {
            return null;
        }

        int payloadLength = length - 1;
        int sumIndex = MchoseProtocol.OffsetPayload + payloadLength;
        if (sumIndex >= bytes.Length)
        {
            return null;
        }

        var cmdType = new[] { bytes[MchoseProtocol.OffsetCmdType], bytes[MchoseProtocol.OffsetCmdType + 1] };
        byte cmd = bytes[MchoseProtocol.OffsetCmd];
        var payload = bytes.Slice(MchoseProtocol.OffsetPayload, payloadLength).ToArray();
        byte expected = Checksum(flags, length, cmdType, cmd, payload);
        bool valid = flags != 0x01 || bytes[sumIndex] == expected;

        return new MchoseFrame
        {
            Flags = flags,
            CmdType = cmdType,
            Cmd = cmd,
            Payload = payload,
            Checksum = bytes[sumIndex],
            ExpectedChecksum = expected,
            IsValid = valid,
            Raw = bytes.ToArray(),
        };
    }
}
