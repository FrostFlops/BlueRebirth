using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BlueOath.LocalServer;

/// <summary>
/// 明文 HTTP 的「热更下载」服务器（角色等同 PC 方案里的 TLS 代理，但反向）：
/// 主端口 19090 是自签 TLS（SDK 引导用），而客户端热更下载走原生 libcurl+OpenSSL，
/// 自带证书校验、不会接受自签证书，所以另外开一个**明文**端口专门供下载。
///
/// 客户端有**两套**下载 URL（2026-10-11 定位，勿删）：
///   ① 普通文件：<c>GET /windows_android/&lt;rel&gt;_&lt;crc32&gt;</c>          → 直接给原文件；
///   ② 压缩文件：<c>GET /windows_android/compressed/&lt;rel&gt;_&lt;MD5(大写)&gt;</c> → CDN 上存的是
///      **gzip 压缩块**（assetmap 里该条目的第 4 字段就是压缩后大小），客户端下载后自己解压、
///      再按 MD5 校验解压结果。
///
/// ★ 哪些文件走 ② 由 assetmap 决定（压缩后大小 &gt; 0 的条目，本项目里是 config/*.db）。
///   包内若没有 <c>compressed/</c> 那一份，客户端会对每个文件重试 10 次后**整轮热更失败**
///   —— 现象是进度停在 76%、「剩余 750MB」时弹「ダウンロードに失敗しました」。
///   所以：优先给包里真实的压缩块；没有再**现场 gzip 兜底**（字节数可能与 CDN 略有出入，
///   但比 404 强得多；真实块由 <c>android/tools/build-compressed-blobs.py</c> 生成）。
///
/// 另外：请求名带后缀（<c>_&lt;crc32&gt;</c> / <c>_&lt;MD5&gt;</c>），真实文件不含后缀，
/// 故逐个剥掉最后一段下划线后缀再试，取第一个真实存在的文件。
/// </summary>
public sealed class BundleHttpServer
{
    private const string UrlPrefix = "/windows_android/";
    private const string CompressedPrefix = "compressed/";

    private readonly int _port;
    private readonly string _root;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public BundleHttpServer(int port, string root)
    {
        _port = port;
        _root = Path.GetFullPath(root);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch (Exception)
        {
            // ignore
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        ServerLog.Info($"bundle(plain http) listening 127.0.0.1:{_port} -> {_root}");
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (_cts is { IsCancellationRequested: false })
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream);
                if (head is null)
                    return;

                var requestLine = head.Split("\r\n")[0];
                var (status, reason, body) = Resolve(requestLine);
                ServerLog.Info($"bundle-req {requestLine.Trim()} -> {status} ({body.Length}B)");

                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} {reason}\r\n" +
                    "Content-Type: application/octet-stream\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(header);
                if (body.Length > 0)
                    await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
        }
        catch (Exception)
        {
            // 连接级异常直接丢弃该连接
        }
    }

    /// <summary>读请求头（到 \r\n\r\n 为止），最多 16KB。</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        while (sb.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
                return null;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                return sb.ToString();
        }

        return sb.ToString();
    }

    private (int, string, byte[]) Resolve(string requestLine)
    {
        var space = requestLine.IndexOf(' ');
        var path = space >= 0 ? requestLine[(space + 1)..] : requestLine;
        var end = path.IndexOf(' ');
        if (end >= 0) path = path[..end];
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        path = Uri.UnescapeDataString(path);

        if (!path.StartsWith(UrlPrefix, StringComparison.Ordinal))
            return (404, "Not Found", Array.Empty<byte>());

        var relative = path[UrlPrefix.Length..];
        if (relative.Length == 0)
            return (404, "Not Found", Array.Empty<byte>());

        // ① 包内真实文件（含 compressed/ 下真实的 gzip 块）。
        foreach (var candidate in Candidates(relative))
        {
            var combined = SafeCombine(candidate);
            if (combined is null)
                return (404, "Not Found", Array.Empty<byte>());
            if (!File.Exists(combined))
                continue;

            try
            {
                return (200, "OK", File.ReadAllBytes(combined));
            }
            catch (Exception ex)
            {
                return (500, "Error", Encoding.UTF8.GetBytes(ex.Message));
            }
        }

        // ② compressed/ 兜底：包里没有压缩块，就用原文件现场 gzip（客户端会自己解压）。
        if (relative.StartsWith(CompressedPrefix, StringComparison.Ordinal))
        {
            var plain = relative[CompressedPrefix.Length..];
            foreach (var candidate in Candidates(plain))
            {
                var combined = SafeCombine(candidate);
                if (combined is null)
                    continue;
                if (!File.Exists(combined))
                    continue;

                try
                {
                    var gz = Gzip(File.ReadAllBytes(combined));
                    ServerLog.Warn($"compressed 块缺失，已现场 gzip：{candidate} " +
                                   $"({gz.Length} 字节) —— 建议重新打包资源包以带上 compressed/");
                    return (200, "OK", gz);
                }
                catch (Exception ex)
                {
                    return (500, "Error", Encoding.UTF8.GetBytes(ex.Message));
                }
            }
        }

        return (404, "Not Found", Encoding.UTF8.GetBytes("bundle not found: " + relative));
    }

    /// <summary>逐个剥掉最后一段下划线后缀，得到可能存在的真实文件名（最多尝试 3 层）。</summary>
    private static List<string> Candidates(string relative)
    {
        var candidates = new List<string> { relative };
        var probe = relative;
        for (var i = 0; i < 3; i++)
        {
            var slashIdx = probe.LastIndexOf('/');
            var seg = slashIdx >= 0 ? probe[(slashIdx + 1)..] : probe;
            var us = seg.LastIndexOf('_');
            if (us <= 0)
                break;
            var stripped = seg[..us];
            probe = slashIdx >= 0 ? probe[..(slashIdx + 1)] + stripped : stripped;
            candidates.Add(probe);
        }

        return candidates;
    }

    /// <summary>拼到 BundleRoot 下并做目录穿越防护；越界返回 null。</summary>
    private string? SafeCombine(string relative)
    {
        var combined = Path.GetFullPath(
            Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        return combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            ? combined
            : null;
    }

    /// <summary>现场 gzip（只在包内缺 compressed/ 块时兜底用）。</summary>
    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return output.ToArray();
    }
}
