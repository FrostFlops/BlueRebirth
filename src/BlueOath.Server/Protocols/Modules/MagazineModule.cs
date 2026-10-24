using BlueOath.Core;
using BlueOath.Protocol;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 杂志模块（magazine.*），只读：
/// <list type="bullet">
/// <item>magazine.UnLock：「杂志全部解锁」作弊关闭时用 1 张栞解锁一期往期（<see cref="MagazineLogic.TryUnlock"/>），
/// 应答前推背包与 magazine.GetMagazine（客户端的应答处理只弹提示 4000035，状态全靠推送）。</item>
/// <item>magazine.FetchMagazineReward / AddHero / Vote：离线不实现印章、趋势与投票，一律 Err，不发放任何东西。
/// 原来落到 OfflineStubModule 的空成功应答会让客户端弹出从未发放的奖励（FetchMagazineReward）或投票成功提示。</item>
/// <item>其余（客户端只会发 magazine.Magazine，活动期内的计时器用）：应答成功并推送 magazine.GetMagazine。</item>
/// </list>
/// 每个请求先在账号锁内结算并落盘；失败时也在应答前重推背包与 magazine.GetMagazine 纠正客户端，ErrMsg 总是非空
/// （客户端失败回调直接拼接 errmsg）。
/// </summary>
internal sealed class MagazineModule(GameServices services) : IGameModule
{
    /// <summary>一次请求的公共上下文：结算结果、单调的 now（不早于 LastSettleTime）与推送时间。</summary>
    private sealed record Call(GameContext Ctx, SettlementResult Settled, long Now, uint PushTime);

    public IReadOnlyList<string> Prefixes => ["magazine"];

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        using var accountLock = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
        var call = new Call(ctx, settled, Math.Max(ctx.Now, settled.Account.LastSettleTime), checked((uint)ctx.Now));
        byte[] args = request.Args ?? [];
        return request.Method switch
        {
            "magazine.UnLock" => await UnlockAsync(call, MagazineProtocolCodec.DecodeMagazineId(args)),
            "magazine.FetchMagazineReward" => RejectFetch(call, args),
            "magazine.AddHero" => Reject(call, request.Method,
                $"trend heroes are only set during an issue's event (magazine {MagazineProtocolCodec.DecodeAddHero(args).MagazineId})"),
            "magazine.Vote" => Reject(call, request.Method,
                $"voting is only open during an issue's event (magazine {MagazineProtocolCodec.DecodeMagazineId(args)})"),
            _ => Finish(call, settled.Account, [SnapshotPush(settled.Account, call)]),
        };
    }

    private async Task<ModuleResult> UnlockAsync(Call call, int magazineId)
    {
        PlayerAccount account = call.Settled.Account;
        MagazineUnlockOutcome outcome = MagazineLogic.TryUnlock(
            account, services.Magazines, magazineId, call.Now, services.Cheats.Magazine);
        if (!outcome.Success) return Reject(call, "magazine.UnLock", outcome.Error);

        account = outcome.Account;
        await services.SaveAccountAsync(account, call.Ctx.Ct);
        services.FileLogger.LogInformation(
            "magazine.UnLock magazine={MagazineId} cost={Type}:{Id}x{Num}",
            magazineId, services.Magazines.UnlockCost.Type, services.Magazines.UnlockCost.Id, services.Magazines.UnlockCost.Num);
        var pre = new List<byte[]>(3);
        if (outcome.CurrencyChanged) pre.Add(GameServices.BuildUpdateUserInfoPush(account, call.PushTime));
        if (outcome.BagChanged) pre.Add(services.BuildBagPush(account, call.PushTime));
        pre.Add(SnapshotPush(account, call));
        return Finish(call, account, pre);
    }

    /// <summary>印章箱离线不实现：不论印章够不够都拒绝，客户端不会弹出奖励。</summary>
    private ModuleResult RejectFetch(Call call, byte[] args)
    {
        (int magazineId, int index) = MagazineProtocolCodec.DecodeArg(args);
        return Reject(call, "magazine.FetchMagazineReward",
            $"stamp boxes are not available offline (magazine {magazineId}, box {index})");
    }

    /// <summary>拒绝（Err = 1）：重推背包（栞、印章的数量）与 magazine.GetMagazine，存档不变。</summary>
    private ModuleResult Reject(Call call, string method, string error)
    {
        PlayerAccount account = call.Settled.Account;
        services.FileLogger.LogInformation("{Method} rejected: {Error}", method, error);
        var pre = new List<byte[]>(3);
        if (services.Magazines.UnlockCost.Type == GameServices.GoodsTypeCurrency)
            pre.Add(GameServices.BuildUpdateUserInfoPush(account, call.PushTime));
        pre.Add(services.BuildBagPush(account, call.PushTime));
        pre.Add(SnapshotPush(account, call));
        return Finish(call, account, pre, err: 1, errMsg: error);
    }

    private byte[] SnapshotPush(PlayerAccount account, Call call) =>
        MagazineLogic.BuildSnapshotPush(account, services.Magazines, services.Cheats.Magazine, call.PushTime);

    /// <summary>
    /// 组装结果：应答前是业务推送，再按 [建筑, 舰娘, 建筑] 推结算；应答后补发结算的浴场 / 祈愿 / 商店 / 前哨快照。
    /// 业务失败时同样推送结算结果。应答没有内容（客户端没有为这些方法注册应答类型）。
    /// </summary>
    private static ModuleResult Finish(Call call, PlayerAccount account, IEnumerable<byte[]> pre, int err = 0, string errMsg = "")
    {
        var prePushes = new List<byte[]>(pre);
        prePushes.AddRange(GameServices.BuildMoodSyncPushes(
            account, call.Settled.ChangedHeroIds, call.Settled.BuildingChanged, call.PushTime));
        return new ModuleResult
        {
            Err = err,
            ErrMsg = errMsg,
            PrePushes = prePushes,
            PostPushes = GameServices.BuildSettlementPostPushes(call.Settled, call.PushTime, current: account),
        };
    }
}
