using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueOath.LocalServer;

/// <summary>套件头部里的一个条目。</summary>
public sealed class KitEntry
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("offset")] public long Offset { get; set; }
    [JsonPropertyName("length")] public long Length { get; set; }
    [JsonPropertyName("fileCount")] public int FileCount { get; set; }
    [JsonPropertyName("package")] public string? Package { get; set; }
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
}

/// <summary>套件头部（紧跟 magic 之后的 JSON）。</summary>
public sealed class KitHeader
{
    [JsonPropertyName("format")] public int Format { get; set; }
    [JsonPropertyName("clientVersion")] public string ClientVersion { get; set; } = "";
    [JsonPropertyName("createdUtc")] public string? CreatedUtc { get; set; }
    [JsonPropertyName("payloadLength")] public long PayloadLength { get; set; }
    [JsonPropertyName("entries")] public List<KitEntry> Entries { get; set; } = new();
}

/// <summary>文件表里的一条记录。</summary>
public readonly record struct KitTableFile(string Path, long Size, byte[] Md5);

/// <summary>
/// 安装套件（<c>.brk</c>，格式 BRKIT001）的顺序流式读取器。
///
/// 设计上**只做顺序读**：SAF 拿到的 content:// 流不保证可定位，因此不依赖 seek，
/// 需要跳过时靠读取丢弃（SkipAsync）。内存占用 O(1)，可处理数 GB 的套件。
/// </summary>
public static class InstallKit
{
    public const int SupportedFormat = 1;

    private const int MagicLength = 8;
    private const int FileRecordFixed = 1 + 2 + 4;   // kind + pathLen + dataLen
    private const int Md5Length = 16;
    private const byte KindFile = 0;
    private const byte KindEnd = 1;
    private const byte KindDir = 2;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BRKIT001");

    /// <summary>读取套件头部。返回头部与 payload 起始偏移。</summary>
    public static async Task<(KitHeader Header, long PayloadStart)> ReadHeaderAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[MagicLength + 8];
        await ReadExactAsync(s, head, ct);
        for (var i = 0; i < MagicLength; i++)
        {
            if (head[i] != Magic[i])
                throw new InvalidDataException("不是 BlueRebirth 安装套件（文件头不匹配）");
        }

        var headerLength = BitConverter.ToInt64(head, MagicLength);
        if (headerLength <= 0 || headerLength > 16 * 1024 * 1024)
            throw new InvalidDataException("套件头部长度异常：" + headerLength);

        var json = new byte[headerLength];
        await ReadExactAsync(s, json, ct);
        var header = JsonSerializer.Deserialize<KitHeader>(json)
                     ?? throw new InvalidDataException("套件头部解析失败");

        if (header.Format != SupportedFormat)
            throw new InvalidDataException($"套件格式 v{header.Format} 不受支持（本版本支持 v{SupportedFormat}）");
        if (header.Entries.Count == 0)
            throw new InvalidDataException("套件里没有任何内容");

        return (header, MagicLength + 8 + headerLength);
    }

    /// <summary>顺序丢弃 count 字节。</summary>
    public static async Task SkipAsync(Stream s, long count, CancellationToken ct)
    {
        if (count <= 0) return;
        if (s.CanSeek)
        {
            s.Seek(count, SeekOrigin.Current);
            return;
        }

        var buf = new byte[1 << 16];
        while (count > 0)
        {
            var want = (int)Math.Min(buf.Length, count);
            var read = await s.ReadAsync(buf.AsMemory(0, want), ct);
            if (read <= 0) throw new EndOfStreamException("套件数据不完整");
            count -= read;
        }
    }

    /// <summary>
    /// 解出 bundle 条目的「文件表」到 targetDir。
    ///
    /// 每条记录：u8 kind / u16 pathLen / u32 dataLen / path / [16]md5 / data。
    /// 已存在且大小一致的目标文件会跳过写入，但仍会校验 md5（所以中断后重跑是幂等的）。
    /// </summary>
    public static async Task<BundleExtractResult> ExtractBundleAsync(
        Stream s,
        long tableLength,
        string targetDir,
        Action<BundleExtractResult>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        var result = new BundleExtractResult();
        var consumed = 0L;
        var buffer = new byte[1 << 20];

        while (consumed < tableLength)
        {
            ct.ThrowIfCancellationRequested();

            var fixedHeader = new byte[FileRecordFixed];
            await ReadExactAsync(s, fixedHeader, ct);
            consumed += FileRecordFixed;

            var kind = fixedHeader[0];
            if (kind == KindEnd)
            {
                result.Ended = true;
                break;
            }

            var pathLength = (int)(fixedHeader[1] | (fixedHeader[2] << 8));
            var dataLength = (uint)(fixedHeader[3] | (fixedHeader[4] << 8) |
                                    (fixedHeader[5] << 16) | (fixedHeader[6] << 24));

            var pathBytes = new byte[pathLength];
            await ReadExactAsync(s, pathBytes, ct);
            consumed += pathLength;
            var relative = NormalizeRelativePath(Encoding.UTF8.GetString(pathBytes));

            if (kind == KindDir)
            {
                Directory.CreateDirectory(Path.Combine(targetDir, relative));
                result.Directories++;
                progress?.Invoke(result);
                continue;
            }

            var expected = new byte[Md5Length];
            await ReadExactAsync(s, expected, ct);
            consumed += Md5Length;

            var target = ResolveInside(targetDir, relative);
            var existingLength = File.Exists(target) ? new FileInfo(target).Length : -1;
            var skipWrite = existingLength == dataLength;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            FileStream? dst = null;
            if (!skipWrite)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                dst = File.Create(target);
            }

            try
            {
                var remaining = (long)dataLength;
                while (remaining > 0)
                {
                    var want = (int)Math.Min(buffer.Length, remaining);
                    var read = await s.ReadAsync(buffer.AsMemory(0, want), ct);
                    if (read <= 0) throw new EndOfStreamException("套件数据不完整");
                    hash.AppendData(buffer, 0, read);
                    consumed += read;
                    remaining -= read;
                    if (dst is not null)
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            finally
            {
                if (dst is not null)
                    await dst.DisposeAsync();
            }

            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(expected))
            {
                result.Mismatches++;
                if (result.Mismatches <= 10)
                    ServerLog.Warn("套件内文件 md5 不符：" + relative);
            }
            else if (skipWrite && existingLength >= 0)
            {
                result.Skipped++;
            }

            result.Files++;
            result.Bytes += dataLength;
            result.Written += skipWrite ? 0 : dataLength;
            progress?.Invoke(result);
        }

        return result;
    }

    /// <summary>计算流中接下来 count 字节的 md5（十六进制小写）。</summary>
    public static async Task<string> HashAsync(Stream s, long count, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[1 << 20];
        while (count > 0)
        {
            var want = (int)Math.Min(buffer.Length, count);
            var read = await s.ReadAsync(buffer.AsMemory(0, want), ct);
            if (read <= 0) throw new EndOfStreamException("套件数据不完整");
            hash.AppendData(buffer, 0, read);
            count -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>把数据原样抄到目标流（用于安装 APK 时直接写 PackageInstaller 会话）。</summary>
    public static async Task<long> CopyAsync(Stream src, Stream dst, long count,
        Action<long, long>? progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        var done = 0L;
        while (done < count)
        {
            var want = (int)Math.Min(buffer.Length, count - done);
            var read = await src.ReadAsync(buffer.AsMemory(0, want), ct);
            if (read <= 0) throw new EndOfStreamException("套件数据不完整");
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress?.Invoke(done, count);
        }

        return done;
    }

    /// <summary>防御目录穿越：剥掉前导分隔符与 .. 段。</summary>
    private static string NormalizeRelativePath(string raw)
    {
        var parts = raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == "." || part == "..")
                continue;
            safe.Add(part);
        }

        if (safe.Count == 0)
            throw new InvalidDataException("套件内路径非法：" + raw);
        return string.Join(Path.DirectorySeparatorChar, safe);
    }

    private static string ResolveInside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new InvalidDataException("套件内路径越界：" + relative);
        return full;
    }

    private static async Task ReadExactAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await s.ReadAsync(buffer.AsMemory(offset), ct);
            if (read <= 0)
                throw new EndOfStreamException("套件数据不完整");
            offset += read;
        }
    }
}

/// <summary>资源解包进度 / 统计。</summary>
public sealed class BundleExtractResult
{
    public int Files { get; set; }
    public int Directories { get; set; }
    public int Skipped { get; set; }
    public int Mismatches { get; set; }
    public long Bytes { get; set; }
    public long Written { get; set; }

    /// <summary>是否读到了文件表的结束标记（没读到说明表被截断）。</summary>
    public bool Ended { get; set; }
}
