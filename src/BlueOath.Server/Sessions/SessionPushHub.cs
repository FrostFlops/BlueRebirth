using System.Collections.Concurrent;
using BlueOath.Protocol;

namespace BlueOath.Server.Sessions;

/// <summary>
/// 让会话之外的代码（GM 控制台的存档编辑）给在线客户端补发同步推送。
/// 每个游戏连接一个 <see cref="SessionChannel"/>，登录后按账号登记；同一账号只保留最新的连接。
/// </summary>
internal sealed class SessionPushHub
{
    private readonly ConcurrentDictionary<string, SessionChannel> _byProfile = new(StringComparer.Ordinal);

    /// <summary>为一个新连接创建写出通道（会话循环的所有写出都要经过它）。</summary>
    internal SessionChannel Open(Stream stream) => new(stream);

    /// <summary>player.Login 解析出账号后登记，之后的补发推送写到这个连接。</summary>
    internal void Bind(SessionChannel channel, string profileId)
    {
        if (channel.ProfileId is { } previous && previous != profileId)
            _byProfile.TryRemove(new KeyValuePair<string, SessionChannel>(previous, channel));
        channel.ProfileId = profileId;
        _byProfile[profileId] = channel;
    }

    /// <summary>连接结束时注销（只注销仍是该账号当前连接的那一个）。</summary>
    internal void Close(SessionChannel channel)
    {
        if (channel.ProfileId is { } profileId)
            _byProfile.TryRemove(new KeyValuePair<string, SessionChannel>(profileId, channel));
    }

    /// <summary>
    /// 请求给账号的在线连接补发同步推送。连接空闲时立即写出；正在处理请求时，等该请求的应答与推送写完再写，
    /// 并且推送在写出那一刻才由 <paramref name="build"/> 按最新存档生成，不会被请求里基于旧存档的推送覆盖。
    /// 账号不在线时返回 false（下次登录时客户端会拿到完整数据）。
    /// </summary>
    internal async Task<bool> RequestResyncAsync(
        string profileId, Func<CancellationToken, Task<IReadOnlyList<byte[]>>> build, CancellationToken ct)
    {
        if (!_byProfile.TryGetValue(profileId, out SessionChannel? channel))
            return false;
        await channel.ResyncAsync(build, ct);
        return true;
    }
}

/// <summary>
/// 一个游戏连接的写出通道：串行化会话循环与外部补发推送的写出，保证帧不交错；
/// 会话处理请求期间（<see cref="BeginRequestAsync"/> 到 <see cref="EndRequestAsync"/>）外部补发推迟到请求结束。
/// </summary>
internal sealed class SessionChannel(Stream stream)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _busy;
    private Func<CancellationToken, Task<IReadOnlyList<byte[]>>>? _pendingResync;

    internal string? ProfileId { get; set; }

    /// <summary>写出一帧（服务端 → 客户端格式）。</summary>
    internal async Task WriteAsync(ReadOnlyMemory<byte> payload, byte type, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await NetSocketFrameCodec.WriteToClientAsync(stream, payload, type, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>会话开始处理一个请求：之后到达的补发推送推迟到 <see cref="EndRequestAsync"/>。</summary>
    internal async Task BeginRequestAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        _busy = true;
        _gate.Release();
    }

    /// <summary>请求的应答与推送都已写出：写出期间推迟的补发推送（按此刻的存档生成）。</summary>
    internal async Task EndRequestAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _busy = false;
            if (_pendingResync is { } build)
            {
                _pendingResync = null;
                await WriteAllAsync(await build(ct), ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ResyncAsync(Func<CancellationToken, Task<IReadOnlyList<byte[]>>> build, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // 推迟时只保留最新的一次：推送在写出时才按最新存档生成，较早的请求没有更多内容。
            if (_busy)
                _pendingResync = build;
            else
                await WriteAllAsync(await build(ct), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteAllAsync(IReadOnlyList<byte[]> pushes, CancellationToken ct)
    {
        foreach (byte[] push in pushes)
            await NetSocketFrameCodec.WriteToClientAsync(stream, push, NetSocketFrameCodec.TypeData, ct);
    }
}
