using System.Buffers.Binary;

namespace BlueOath.Protocol;

/// <summary>
/// The game's NetSocket transport framing (outer layer): a 5-byte header
/// [payload-length (4 bytes, big-endian)][type (1 byte)], optionally followed by a
/// 16-byte MD5 hash (only when type == TypeDataWithHash), then the payload.
/// </summary>
public static class NetSocketFrameCodec
{
    public const int HeaderLength = 5;
    public const int HashLength = 16;
    public const byte TypeData = 0;
    public const byte TypeDataWithHash = 1;
    public const byte TypePing = 2;

    public static async Task<(byte Type, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[HeaderLength];
        if (!await ReadExactAsync(stream, header, ct)) return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        var type = header[4];
        if (length is < 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("Invalid NetSocket frame length");
        if (type == TypeDataWithHash)
        {
            var hash = new byte[HashLength];
            if (!await ReadExactAsync(stream, hash, ct))
                throw new EndOfStreamException("Truncated NetSocket hash");
        }
        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, ct))
            throw new EndOfStreamException("Truncated NetSocket payload");
        return (type, payload);
    }

    /// <summary>
    /// 写一帧发往游戏客户端的数据：长度字段 = 正文长度 + 5 字节帧头。
    /// <para>
    /// 客户端的收发不对称：NetSocket.Send 只写正文长度（服务端 <see cref="ReadAsync"/> 按此解析），
    /// 而 NetSocket.ReceiveCallback 把长度当作含帧头的整帧长度、只读 L - 5 字节正文（日服 GameAssembly
    /// 0x102A5203 lea eax,[esi-5]）。按正文长度写会让客户端每条消息少读末尾 5 字节，TResponse 最后的
    /// Time 与 IsResponse 被截断，客户端时钟（TimeUtil.SetServerDiffTime）因此从不与服务端同步，
    /// 一直用本机时间推算（夏令时下快一小时）。心跳回包同样按此写，长度 5、正文为空。
    /// </para>
    /// </summary>
    public static async Task WriteToClientAsync(Stream stream, ReadOnlyMemory<byte> payload, byte type = TypeData, CancellationToken ct = default)
    {
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length + HeaderLength);
        header[4] = type;
        await stream.WriteAsync(header, ct);
        if (!payload.IsEmpty) await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// 按客户端的语义读一帧服务端发来的数据（测试里的模拟客户端使用）：长度含 5 字节帧头，L ≤ 5 时正文为空。
    /// </summary>
    public static async Task<(byte Type, byte[] Payload)?> ReadFromServerAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[HeaderLength];
        if (!await ReadExactAsync(stream, header, ct)) return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        var type = header[4];
        if (length is < 0 or > 4 * 1024 * 1024 + HeaderLength)
            throw new InvalidDataException("Invalid NetSocket frame length");
        var payload = new byte[Math.Max(0, length - HeaderLength)];
        if (payload.Length > 0 && !await ReadExactAsync(stream, payload, ct))
            throw new EndOfStreamException("Truncated NetSocket payload");
        return (type, payload);
    }

    /// <summary>写一帧客户端发往服务端的数据（长度字段 = 正文长度，与客户端 NetSocket.Send 相同；测试客户端使用）。</summary>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, byte type = TypeData, CancellationToken ct = default)
    {
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        header[4] = type;
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
