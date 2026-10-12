using BlueOath.Core;
using BlueOath.Protocol;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 扫荡作战模块：mopUp.CheckSweep / mopUp.GetMopUpData / mopUp.StartSweep / mopUp.StopSweep。
/// <list type="bullet">
/// <item>每个请求先在账号锁内结算并落盘，结算结果随应答推送（心情 / 建筑在应答前，浴场 / 祈愿 / 商店在应答后）。</item>
/// <item>客户端只从 mopUp.GetMopUpData 推送（CopyService:BackSweepCopyInfo）读取扫荡状态，且每次整表替换，
/// 因此开始与领取后都推送完整的 TMopUpRet（舰队上限 + 全部扫荡），领取时再带上每轮的 TPassBaseRet 打开报酬页。</item>
/// <item>扫荡进度由客户端按开始 / 结束时刻外推；服务端在领取时用 <see cref="SweepLogic.CompletedRuns"/> 算完成轮数，
/// 每轮按 copy.PassBase 的非首通胜利结算（<see cref="BattleService.GrantSweepRun"/>）。</item>
/// <item>「真实消耗资源」（--real-resource-cost）下开始时预扣全部轮数的燃料，提前取消时退还未完成轮数的燃料；
/// 默认规则不扣燃料。</item>
/// </list>
/// </summary>
internal sealed class MopUpModule(BattleService battle, GameServices services) : IGameModule
{
    /// <summary>TMopUpArg{fleetId(1), copyId(2), sweepCounts(3)}。</summary>
    private readonly record struct MopUpArg(int FleetId, int CopyId, int SweepCounts);

    /// <summary>一次请求的公共上下文：结算结果、单调的 now（不早于 LastSettleTime）与推送时间。</summary>
    private sealed record Call(GameContext Ctx, SettlementResult Settled, long Now, uint PushTime);

    public IReadOnlyList<string> Prefixes => ["mopUp"];

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        using var accountLock = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
        var call = new Call(ctx, settled, Math.Max(ctx.Now, settled.Account.LastSettleTime), checked((uint)ctx.Now));
        return request.Method switch
        {
            // CheckSweep 的应答只读 sweepFleetsNum；GetMopUpData 的应答与推送走同一个处理器。
            "mopUp.CheckSweep" or SweepLogic.DataMethod => Snapshot(call),
            "mopUp.StartSweep" => await StartAsync(call, DecodeArg(request.Args ?? [])),
            "mopUp.StopSweep" => await StopAsync(call, DecodeArg(request.Args ?? [])),
            _ => Finish(call, settled.Account, [], [], []),
        };
    }

    /// <summary>应答当前状态 TMopUpRet{舰队上限, 全部扫荡}。</summary>
    private ModuleResult Snapshot(Call call)
    {
        PlayerAccount account = call.Settled.Account;
        byte[] ret = SweepLogic.EncodeRet(FleetsNum(account), SweepLogic.Entries(account));
        return Finish(call, account, ret, [], []);
    }

    /// <summary>
    /// mopUp.StartSweep：校验后记一条扫荡（结束时刻 = 开始 + 轮数 × autobattle_time），应答与随后的推送都是完整的扫荡列表。
    /// 3 星、玩家等级、船坞 / 装备仓库已满等条件由客户端检查，服务端不重复（星级数据本来就是服务端伪造的）。
    /// </summary>
    private async Task<ModuleResult> StartAsync(Call call, MopUpArg arg)
    {
        PlayerAccount account = call.Settled.Account;
        bool realCost = services.Cheats.RealResourceCost;
        int fleetsNum = FleetsNum(account);
        IReadOnlyList<SweepEntry> entries = SweepLogic.Entries(account);
        int runSeconds = SortieCostLoader.Autobattle(arg.CopyId) is { } autobattle && autobattle.Open ? autobattle.RunSeconds : 0;
        string? error =
            runSeconds <= 0 ? $"copy {arg.CopyId} cannot be swept (missing or autobattle_open != 1)" :
            arg.SweepCounts is < SweepLogic.MinCounts or > SweepLogic.MaxCounts ? $"sweep count {arg.SweepCounts} is outside 1-10" :
            arg.FleetId <= 0 ? $"fleet {arg.FleetId} is invalid" :
            entries.Any(entry => entry.FleetId == arg.FleetId) ? $"fleet {arg.FleetId} is already sweeping" :
            entries.Any(entry => entry.CopyId == arg.CopyId) ? $"copy {arg.CopyId} is already being swept" :
            entries.Count >= fleetsNum ? $"all {fleetsNum} sweep fleets are busy" :
            null;
        if (error is not null) return RejectStart(call, account, fleetsNum, arg, error, resyncUser: false);

        int chapterId = SortieCostLoader.ChapterOf(arg.CopyId);
        long perRun = SweepLogic.SupplyPerRun(chapterId, arg.CopyId);
        long total = perRun * arg.SweepCounts;
        bool charged = realCost && total > 0;
        var pre = new List<byte[]>();
        if (charged)
        {
            PaymentResult payment = CostLogic.TryPay(
                account, [new CostItem(GameServices.GoodsTypeCurrency, SortieCost.SupplyCurrency, total)]);
            if (!payment.Ok)
                return RejectStart(call, account, fleetsNum, arg, $"not enough fuel ({payment.Shortfall})", resyncUser: true);
            account = payment.Account;
            pre.Add(GameServices.BuildUpdateUserInfoPush(account, call.PushTime));
        }

        long startTime = SweepLogic.StartTime(call.Now, arg.SweepCounts, runSeconds, services.Cheats.Sweep);
        var started = new SweepEntry(arg.FleetId, arg.CopyId, chapterId, startTime,
            SweepLogic.EndTime(startTime, arg.SweepCounts, runSeconds), arg.SweepCounts, runSeconds, perRun, charged);
        account = SweepLogic.Put(account, started);
        await services.SaveAccountAsync(account, call.Ctx.Ct);
        services.FileLogger.LogInformation(
            "mopUp.StartSweep fleet={FleetId} copy={CopyId} chapter={ChapterId} counts={Counts} run={RunSeconds}s start={Start} end={End} supplyPerRun={PerRun} charged={Charged} fuel={Fuel}",
            started.FleetId, started.CopyId, started.ChapterId, started.SweepCounts, started.RunSeconds, started.StartTime,
            started.EndTime, perRun, charged, account.Character.Supply);

        IReadOnlyList<SweepEntry> after = SweepLogic.Entries(account);
        return Finish(call, account, SweepLogic.EncodeRet(fleetsNum, after), pre,
            [SweepLogic.BuildDataPush(fleetsNum, after, call.PushTime)]);
    }

    /// <summary>拒绝开始（Err = 1）：推送当前扫荡列表纠正客户端；燃料不足时先推玩家信息纠正余额。</summary>
    private ModuleResult RejectStart(Call call, PlayerAccount account, int fleetsNum, MopUpArg arg, string error, bool resyncUser)
    {
        services.FileLogger.LogInformation(
            "mopUp.StartSweep rejected fleet={FleetId} copy={CopyId} counts={Counts}: {Error}",
            arg.FleetId, arg.CopyId, arg.SweepCounts, error);
        IReadOnlyList<byte[]> pre = resyncUser ? [GameServices.BuildUpdateUserInfoPush(account, call.PushTime)] : [];
        return Finish(call, account, [], pre,
            [SweepLogic.BuildDataPush(fleetsNum, SweepLogic.Entries(account), call.PushTime)], 1, error);
    }

    /// <summary>
    /// mopUp.StopSweep：到期后领取（「掃討作戦完了」）与提前取消共用。按 fleetId 找扫荡（关卡详情页发的 copyId 是页面自己的关卡，
    /// 不一定是被扫荡的关卡）；完成的每一轮按普通胜利结算（每日副本与手动通关一样不限当日次数）；预扣过燃料时退还未结算轮数的燃料。
    /// 应答为空，奖励与新的扫荡列表放在之后的 mopUp.GetMopUpData 推送里（每轮一个 TPassBaseRet）。
    /// </summary>
    private async Task<ModuleResult> StopAsync(Call call, MopUpArg arg)
    {
        PlayerAccount account = call.Settled.Account;
        SweepEntry? entry = SweepLogic.Entries(account).FirstOrDefault(existing => existing.FleetId == arg.FleetId);
        if (entry is null)
        {
            services.FileLogger.LogInformation(
                "mopUp.StopSweep fleet={FleetId} copy={CopyId}: no sweep for this fleet", arg.FleetId, arg.CopyId);
            int fleetsNum = FleetsNum(account);
            IReadOnlyList<SweepEntry> current = SweepLogic.Entries(account);
            return Finish(call, account, SweepLogic.EncodeRet(fleetsNum, current), [],
                [SweepLogic.BuildDataPush(fleetsNum, current, call.PushTime)]);
        }

        int now = checked((int)call.Now);
        int completed = SweepLogic.CompletedRuns(entry, call.Now);
        bool daily = ChapterCopyLoader.GetCopyType(entry.CopyId) == 9;
        int chaseCopyId = SweepLogic.ChaseCopyId(entry.ChapterId, entry.CopyId);
        var passRets = new List<byte[]>(completed);
        var granted = new List<CommonReward>();
        for (int i = 0; i < completed; i++)
        {
            int grade = DailyCopyService.SweepGrade(account, entry.CopyId);
            BattleService.SweepRunResult run = battle.GrantSweepRun(account, entry.CopyId, chaseCopyId, now);
            account = run.Account;
            granted.AddRange(run.Rewards);
            granted.AddRange(run.ChaseRewards);
            passRets.Add(ProtocolEncoder.EncodePassBaseRet(
                entry.CopyId, grade, firstPass: 0, passTime: entry.RunSeconds, rewards: run.Rewards, extraReward: run.ChaseRewards));
        }

        long refund = entry.Charged ? (long)(entry.SweepCounts - completed) * entry.SupplyPerRun : 0;
        if (refund > 0) account = GameServices.AddCurrency(account, SortieCost.SupplyCurrency, checked((int)refund));
        account = SweepLogic.RemoveFleet(account, entry.FleetId);
        await services.SaveAccountAsync(account, call.Ctx.Ct);
        services.FileLogger.LogInformation(
            "mopUp.StopSweep fleet={FleetId} copy={CopyId} chase={ChaseCopyId} completed={Completed}/{Counts} refund={Refund} fuel={Fuel} rewards=[{Rewards}]",
            entry.FleetId, entry.CopyId, chaseCopyId, completed, entry.SweepCounts, refund, account.Character.Supply,
            string.Join(",", granted.Select(reward => $"{reward.Type}:{reward.ConfigId}x{reward.Num}")));

        // 发放可能带来舰娘（增量推送，客户端按 HeroId 合并；新舰娘自带默认装备）与装备；货币与背包总是重推。
        List<uint> newHeroIds = granted
            .Where(reward => reward.Type == GameServices.GoodsTypeShip && reward.Id > 0)
            .Select(reward => (uint)reward.Id)
            .ToList();
        var pre = new List<byte[]>
        {
            GameServices.BuildUpdateUserInfoPush(account, call.PushTime),
            services.BuildBagPush(account, call.PushTime),
        };
        if (newHeroIds.Count > 0 || granted.Any(reward => reward.Type == GameServices.GoodsTypeEquip))
            pre.Add(services.BuildEquipPush(account, call.PushTime));
        if (daily && completed > 0) pre.Add(DailyCopyService.BuildUpdatePush(account.DailyCopy, call.PushTime));

        int fleetsAfter = FleetsNum(account);
        return Finish(call, account, [], pre,
            [SweepLogic.BuildDataPush(fleetsAfter, SweepLogic.Entries(account), call.PushTime, completed > 0 ? passRets : null)],
            extraHeroIds: newHeroIds);
    }

    /// <summary>
    /// 组装结果：应答前是业务推送，再按 [建筑, 舰娘, 建筑] 推结算（与本次新增的舰娘一起）；应答后是业务推送，再补发结算的浴场 / 祈愿 / 商店快照。
    /// 业务失败时同样推送结算结果。
    /// </summary>
    private static ModuleResult Finish(
        Call call, PlayerAccount account, byte[] ret, IEnumerable<byte[]> pre, IEnumerable<byte[]> post,
        int err = 0, string errMsg = "", IEnumerable<uint>? extraHeroIds = null)
    {
        var prePushes = new List<byte[]>(pre);
        prePushes.AddRange(GameServices.BuildMoodSyncPushes(
            account, call.Settled.ChangedHeroIds.Union(extraHeroIds ?? []), call.Settled.BuildingChanged, call.PushTime));
        var postPushes = new List<byte[]>(post);
        postPushes.AddRange(GameServices.BuildSettlementPostPushes(call.Settled, call.PushTime, current: account));
        return new ModuleResult { Ret = ret, Err = err, ErrMsg = errMsg, PrePushes = prePushes, PostPushes = postPushes };
    }

    private int FleetsNum(PlayerAccount account) => SweepLogic.FleetsNum(account, services.Cheats.RealResourceCost);

    /// <summary>解码 TMopUpArg；格式错误时返回全 0（随后按非法参数拒绝或找不到扫荡）。</summary>
    private static MopUpArg DecodeArg(byte[] args)
    {
        int fleetId = 0, copyId = 0, counts = 0;
        try
        {
            var reader = new ProtocolDecoder.ProtoReader(args);
            while (reader.TryReadField(out int field, out int wire))
            {
                if (wire != 0)
                {
                    reader.Skip(wire);
                    continue;
                }
                int value = unchecked((int)reader.ReadVarint());
                switch (field)
                {
                    case 1: fleetId = value; break;
                    case 2: copyId = value; break;
                    case 3: counts = value; break;
                }
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            return default;
        }
        return new MopUpArg(fleetId, copyId, counts);
    }
}
