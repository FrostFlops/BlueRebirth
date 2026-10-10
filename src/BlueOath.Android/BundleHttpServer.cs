using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BlueOath.LocalServer;

/// <summary>
/// 明文 HTTP 的「热更下载」服务器（角色等同 PC 方案里的 TLS 代理，但反向）：
/// 主端口 19090 是自签 TLS（SDK 引导用），而客户端热更下载走原生 libcurl+OpenSSL，
/// 自带证书校验、不会接受自签证书，所以另外开一个**明文**端口专门供下载。
///
/// 只实现最小必要功能：GET /windows_android/&lt;rel&gt;_&lt;crc&gt; → BundleRoot 下的真实文件。
/// </summary>
public sealed class BundleHttpServer
{
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
        const string prefix = "/windows_android/";
        var space = requestLine.IndexOf(' ');
        var path = space >= 0 ? requestLine[(space + 1)..] : requestLine;
        var end = path.IndexOf(' ');
        if (end >= 0) path = path[..end];
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        path = Uri.UnescapeDataString(path);

        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return (404, "Not Found", Array.Empty<byte>());

        var relative = path[prefix.Length..];
        if (relative.Length == 0)
            return (404, "Not Found", Array.Empty<byte>());

        // 客户端请求名带后缀（`<rel>_<crc>` 或 `<rel>_<version>`，如 assetmap_1.4.140）。
        // 真实文件不含后缀，故逐个剥掉最后一段下划线后缀再试，取第一个真实存在的文件。
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

        foreach (var candidate in candidates)
        {
            var combined = Path.GetFullPath(Path.Combine(_root, candidate.Replace('/', Path.DirectorySeparatorChar)));
            var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
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

        return (404, "Not Found", Encoding.UTF8.GetBytes("bundle not found: " + relative));
    }
}
