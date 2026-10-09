using BlueOath.Core;
using BlueOath.Protocol;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 基地模块：建筑生命周期、舰娘派驻、资源与道具领取、道具生产/合成/加速与持久化快照。
/// 每个请求先在账号锁内把心情与产出按经过时间结算到 now 并落盘，再执行业务；
/// 结算结果（无论业务成功与否）都随应答前的推送同步给客户端。
/// </summary>
internal sealed class BuildingModule(BuildingService building, GameServices services) : IGameModule
{
    public IReadOnlyList<string> Prefixes => ["building"];

    /// <summary>生产类请求的参数与结果写进 game-login.log（请求帧只记录 16 字节预览，排查客户端/服务端不一致时需要）。</summary>
    private readonly ILogger _diagnostics = services.FileLogger;

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        using var accountLock = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
        uint now = checked((uint)ctx.Now);

        switch (request.Method)
        {
            case "building.AddBuilding":
            {
                AddBuildingArg arg = PlayerDataCodec.DecodeAddBuildingArg(request.Args ?? []);
                BuildingService.Mutation mutation = await building.AddBuildingAsync(
                    ctx.ProfileId, arg, ctx.Now, ctx.Ct);
                return ToResult(settled, mutation, now, mutation.Success
                    ? PlayerDataCodec.EncodeAddBuildingRet(mutation.BuildingId)
                    : []);
            }
            case "building.UpgradeBuilding":
            {
                int buildingId = PlayerDataCodec.DecodeBuildingIdArg(request.Args ?? []);
                BuildingService.Mutation mutation = await building.UpgradeBuildingAsync(
                    ctx.ProfileId, buildingId, ctx.Now, ctx.Ct);
                return ToResult(settled, mutation, now);
            }
            case "building.FinishBuilding":
            {
                int buildingId = PlayerDataCodec.DecodeBuildingIdArg(request.Args ?? []);
                BuildingService.Mutation mutation = await building.FinishBuildingAsync(
                    ctx.ProfileId, buildingId, ctx.Now, ctx.Ct);
                return ToResult(settled, mutation, now);
            }
            case "building.DegradeBuilding":
            {
                int buildingId = PlayerDataCodec.DecodeBuildingIdArg(request.Args ?? []);
                BuildingService.Mutation mutation = await building.DegradeBuildingAsync(
                    ctx.ProfileId, buildingId, ctx.Now, ctx.Ct);
                // 客户端把降级应答按 TReceiveRet 解析：降级前自动领取的存量放进 ItemInfo。
                return ToResult(settled, mutation, now, mutation.Success
                    ? ProtocolEncoder.EncodeReceiveRet(mutation.Rewards)
                    : []);
            }
            case "building.SetHero":
            {
                SetBuildingHeroArg arg = PlayerDataCodec.DecodeSetBuildingHeroArg(request.Args ?? []);
                var assignments = new Dictionary<int, IReadOnlyList<uint>>
                {
                    [arg.BuildingId] = arg.HeroIds,
                };
                return await MutateAsync(ctx, settled, assignments, now);
            }
            case "building.SetBuildingListHero":
            {
                SetBuildingListHeroArg arg = PlayerDataCodec.DecodeSetBuildingListHeroArg(request.Args ?? []);
                if (!TrySplitAssignments(arg, out Dictionary<int, IReadOnlyList<uint>> assignments))
                    return Failure(settled, now, 1, "Invalid building assignment payload");
                return await MutateAsync(ctx, settled, assignments, now);
            }
            case "building.UpdateHeroAddition":
            {
                // 客户端打开基建页面时（节流 60 秒）发送，请求最新快照；结算后总是推送建筑快照。
                return new ModuleResult
                {
                    PrePushes = GameServices.BuildMoodSyncPushes(settled.Account, settled.ChangedHeroIds, true, now),
                    PostPushes = BathPushes(settled, now, settled.Account),
                };
            }
            case "building.ReceiveBuilding":
            {
                int bid = PlayerDataCodec.DecodeBuildingIdArg(request.Args ?? []);
                return ToReceive(request.Method, $"building={bid}", settled, await building.ReceiveAsync(
                    ctx.ProfileId, BuildingProduction.ReceiveKind.Building, bid, ctx.Now, ctx.Ct), now);
            }
            case "building.ReceiveItem":
            {
                int bid = PlayerDataCodec.DecodeBuildingIdArg(request.Args ?? []);
                return ToReceive(request.Method, $"building={bid}", settled, await building.ReceiveAsync(
                    ctx.ProfileId, BuildingProduction.ReceiveKind.Item, bid, ctx.Now, ctx.Ct), now);
            }
            case "building.ReceiveResource":
            {
                // TReceiveByResourceArg{1: ResourceId}，与 BuildingId 同为字段 1 的 varint。
                int resourceId = PlayerDataCodec.DecodeBuildingIdArg(request.Args ?? []);
                return ToReceive(request.Method, $"resource={resourceId}", settled, await building.ReceiveAsync(
                    ctx.ProfileId, BuildingProduction.ReceiveKind.Resource, resourceId, ctx.Now, ctx.Ct), now);
            }
            case "building.ReceiveAll":
                return ToReceive(request.Method, "", settled, await building.ReceiveAsync(
                    ctx.ProfileId, BuildingProduction.ReceiveKind.All, 0, ctx.Now, ctx.Ct), now);
            case "building.ProduceItem":
            {
                var (bid, rid, cnt) = ProtocolDecoder.DecodeProduceItemArg(request.Args ?? []);
                return ToReceive(request.Method, $"building={bid} recipe={rid} count={cnt}", settled,
                    await building.OrderAsync(ctx.ProfileId, bid, rid, cnt, ctx.Now, ctx.Ct), now);
            }
            case "building.ComposeItem":
            {
                var (bid, rid, cnt) = ProtocolDecoder.DecodeProduceItemArg(request.Args ?? []);
                return ToReceive(request.Method, $"building={bid} compose={rid} count={cnt}", settled,
                    await building.ComposeAsync(ctx.ProfileId, bid, rid, cnt, ctx.Now, ctx.Ct), now);
            }
            case "building.UseStrengthSpeedup":
            {
                // TUseStrengthSpeedupArg{1: BuildingId, 2: UseCount}；客户端只看 err。
                var (bid, useCount, _) = ProtocolDecoder.DecodeProduceItemArg(request.Args ?? []);
                return ToReceive(request.Method, $"building={bid} useCount={useCount}", settled,
                    await building.SpeedupAsync(ctx.ProfileId, bid, useCount, ctx.Now, ctx.Ct), now);
            }
            default:
                // 基建剧情等其它协议尚未实现；只同步本次结算产生的变化。
                return new ModuleResult
                {
                    PrePushes = GameServices.BuildMoodSyncPushes(
                        settled.Account, settled.ChangedHeroIds, settled.BuildingChanged, now),
                    PostPushes = BathPushes(settled, now, settled.Account),
                };
        }
    }

    private async Task<ModuleResult> MutateAsync(
        GameContext ctx,
        SettlementResult settled,
        IReadOnlyDictionary<int, IReadOnlyList<uint>> assignments,
        uint now)
    {
        BuildingService.Mutation mutation = await building.SetHeroesAsync(
            ctx.ProfileId, assignments, ctx.Now, ctx.Ct);
        return ToResult(settled, mutation, now);
    }

    private ModuleResult ToResult(
        SettlementResult settled,
        BuildingService.Mutation mutation,
        uint now,
        byte[]? ret = null)
    {
        if (!mutation.Success) return Failure(settled, now, mutation.Err, mutation.ErrMsg, forceBuilding: true);
        var pushes = new List<byte[]>();
        if (mutation.CurrencyChanged) pushes.Add(GameServices.BuildUpdateUserInfoPush(mutation.Account, now));
        if (mutation.BagChanged) pushes.Add(services.BuildBagPush(mutation.Account, now));
        // 客户端在应答回调中立即读取 buildingData，因此快照必须先于应答到达。
        pushes.AddRange(GameServices.BuildMoodSyncPushes(mutation.Account, settled.ChangedHeroIds, true, now));
        return new ModuleResult
        {
            Ret = ret ?? [],
            PrePushes = pushes,
            PostPushes = BathPushes(settled, now, mutation.Account),
        };
    }

    /// <summary>
    /// 领取/下单/合成/加速的统一出口：货币变化先推 user.UpdateUserInfo（3D 页回调末尾会 UpdateUI），
    /// 道具变化推背包，再按 [建筑, 舰娘, 建筑] 推快照（加速会扣驻守舰娘心情）；Ret 为 TReceiveRet。
    /// 失败时同样推建筑快照：体力不足等错误客户端只记日志，快照用于纠正客户端外推出的体力与进度。
    /// </summary>
    private ModuleResult ToReceive(
        string method, string args, SettlementResult settled, BuildingProduction.Outcome outcome, uint now)
    {
        LogProduction(method, args, outcome);
        if (!outcome.Success) return Failure(settled, now, outcome.Err, outcome.ErrMsg, forceBuilding: true);
        var pushes = new List<byte[]>();
        if (outcome.CurrencyChanged) pushes.Add(GameServices.BuildUpdateUserInfoPush(outcome.Account, now));
        if (outcome.BagChanged) pushes.Add(services.BuildBagPush(outcome.Account, now));
        pushes.AddRange(GameServices.BuildMoodSyncPushes(
            outcome.Account, settled.ChangedHeroIds.Union(outcome.HeroesChanged), true, now));
        return new ModuleResult
        {
            Ret = ProtocolEncoder.EncodeReceiveRet(outcome.Rewards),
            PrePushes = pushes,
            PostPushes = BathPushes(settled, now, outcome.Account),
        };
    }

    /// <summary>业务失败时仍要同步已落盘的结算结果，否则客户端会用旧数据配新锚点外推。</summary>
    private static ModuleResult Failure(
        SettlementResult settled, uint now, int err, string errMsg, bool forceBuilding = false) =>
        new()
        {
            Err = err,
            ErrMsg = errMsg,
            PrePushes = GameServices.BuildMoodSyncPushes(
                settled.Account, settled.ChangedHeroIds, settled.BuildingChanged || forceBuilding, now),
            PostPushes = BathPushes(settled, now, settled.Account),
        };

    /// <summary>生产类请求的诊断日志：参数、结果、工人体力（显示值）与改了心情的舰娘。</summary>
    private void LogProduction(string method, string args, BuildingProduction.Outcome outcome)
    {
        PlayerBuilding? state = outcome.Account.Building;
        string strength = state is null ? "-" : (state.WorkerStrength / BuildingProduction.StrengthScale).ToString();
        string rewards = string.Join(",", outcome.Rewards.Select(reward => $"{reward.Type}:{reward.ConfigId}x{reward.Num}"));
        _diagnostics.LogInformation(
            "{Method} {Args} err={Err} {ErrMsg} rewards=[{Rewards}] strength={Strength} moodHeroes=[{Heroes}]",
            method, args, outcome.Err, outcome.ErrMsg, rewards, strength, string.Join(",", outcome.HeroesChanged));
    }

    /// <summary>结算产生的浴场 / 祈愿 / 商店 / 前哨变化在应答后补发，按业务处理完的账号编码。</summary>
    private static IReadOnlyList<byte[]> BathPushes(SettlementResult settled, uint now, PlayerAccount current) =>
        GameServices.BuildSettlementPostPushes(settled, now, current: current);

    private static bool TrySplitAssignments(
        SetBuildingListHeroArg arg,
        out Dictionary<int, IReadOnlyList<uint>> assignments)
    {
        assignments = [];
        int cursor = 0;
        foreach (int buildingId in arg.BuildingIds)
        {
            var heroIds = new List<uint>();
            while (cursor < arg.HeroIds.Count && arg.HeroIds[cursor] != -1)
            {
                int heroId = arg.HeroIds[cursor++];
                if (heroId <= 0) return false;
                heroIds.Add(checked((uint)heroId));
            }
            if (cursor >= arg.HeroIds.Count || arg.HeroIds[cursor] != -1 || buildingId <= 0)
                return false;
            cursor++;
            if (!assignments.TryAdd(buildingId, heroIds)) return false;
        }
        return cursor == arg.HeroIds.Count;
    }
}
