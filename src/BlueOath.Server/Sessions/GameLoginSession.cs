using System.Net.Sockets;
using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server.Infrastructure;
using BlueOath.Server.Protocols;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Sessions;

/// <summary>
/// 游戏登录 TCP 端点的每个连接处理器（承载 protobuf <c>TMessage</c> 请求/响应层的
/// NetSocket 帧）。会话内跟踪当前 profileId，并把所有请求交给 <see cref="MessageRouter"/>
/// 分发到对应模块；按「前置推送 → 应答 → 后置推送」写回客户端。
/// 所有写出经 <see cref="SessionChannel"/>；登录后在 <see cref="SessionPushHub"/> 登记，
/// 供 GM 存档编辑给在线客户端补发同步推送（处理请求期间到达的补发推迟到该请求写完）。
/// </summary>
internal sealed class GameLoginSession(
    MessageRouter router, ILoggerFactory loggerFactory, ServerOptions options, SessionPushHub pushHub)
{
    private readonly MessageRouter _router = router;
    private readonly ILogger _fileLogger = loggerFactory.CreateLogger(GameLoginFileLoggerProvider.Category);
    private readonly ILogger _messageLogger = loggerFactory.CreateLogger("SocketSession");

    public async Task HandleAsync(TcpClient client, int connectionId, CancellationToken ct)
    {
        using (client)
        {
            var profileId = options.ProfileId;
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            _fileLogger.LogInformation("game-login[{ConnectionId}] accepted remote={Remote}", connectionId, remote);
            SessionChannel? channel = null;
            try
            {
                var stream = client.GetStream();
                channel = pushHub.Open(stream);
                while (!ct.IsCancellationRequested)
                {
                    var frame = await NetSocketFrameCodec.ReadAsync(stream, ct);
                    if (frame is null)
                        break;
                    var (type, payload) = frame.Value;
                    _fileLogger.LogInformation(
                        "game-login[{ConnectionId}] netsocket type={Type} len={Length} preview={Preview}",
                        connectionId, type, payload.Length,
                        Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 16))));
                    // 心跳帧直接原样回 ping。
                    if (type == NetSocketFrameCodec.TypePing)
                    {
                        await channel.WriteAsync(ReadOnlyMemory<byte>.Empty, NetSocketFrameCodec.TypePing, ct);
                        continue;
                    }
                    if (payload.Length == 0)
                        continue;

                    var request = TMessageCodec.DecodeRequest(payload);
                    _messageLogger.Log(LogLevel.Information, "GameSession received request method={Method}", request.Method);

                    // player.Login 先解析 pid，更新会话的 profileId，后续请求按该账号读取。
                    if (request.Method == "player.Login")
                    {
                        profileId = _router.ResolveLoginProfileId(request);
                        pushHub.Bind(channel, profileId);
                    }

                    await channel.BeginRequestAsync(ct);
                    try
                    {
                        await HandleRequestAsync(channel, request, profileId, connectionId, ct);
                    }
                    finally
                    {
                        await channel.EndRequestAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _fileLogger.LogInformation("game-login[{ConnectionId}] failed: {Error}", connectionId, ex);
            }
            finally
            {
                if (channel is not null)
                    pushHub.Close(channel);
            }
        }
    }

    private async Task HandleRequestAsync(
        SessionChannel channel, TRequest request, string profileId, int connectionId, CancellationToken ct)
    {
        var result = await _router.DispatchAsync(request, profileId, ct);

        foreach (var push in result.PrePushes)
        {
            await channel.WriteAsync(push, NetSocketFrameCodec.TypeData, ct);
            _fileLogger.LogInformation(
                "game-login[{ConnectionId}] push (before response) bytes={Bytes}",
                connectionId, push.Length);
        }

        // 每个请求都回一个 TResponse 信封（即使 Ret 为空），客户端按方法名接收。
        var now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var response = new TResponse(Err: result.Err, ErrMsg: result.ErrMsg,
            Method: request.Method, Ret: result.Ret,
            CallbackHandler: request.CallbackHandler, Time: now,
            Token: request.Token, Seq: 0, IsResponse: 1);
        var encoded = TMessageCodec.EncodeResponse(response);
        await channel.WriteAsync(encoded, NetSocketFrameCodec.TypeData, ct);
        _fileLogger.LogInformation(
            "game-login[{ConnectionId}] response bytes={Bytes} hex={Hex}",
            connectionId, encoded.Length, Convert.ToHexString(encoded));

        foreach (var push in result.PostPushes)
        {
            await channel.WriteAsync(push, NetSocketFrameCodec.TypeData, ct);
            _fileLogger.LogInformation(
                "game-login[{ConnectionId}] push (after response) bytes={Bytes}",
                connectionId, push.Length);
        }
    }
}
