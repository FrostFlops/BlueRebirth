using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 基地模块：建筑生命周期、舰娘派驻、物品生产/合成与持久化快照。
/// 每个请求先在账号锁内把心情按经过时间结算到 now 并落盘，再执行业务；
/// 结算结果（无论业务成功与否）都随应答前的推送同步给客户端。
/// </summary>
internal sealed class BuildingModule(BuildingService building, GameServices services) : IGameModule
{
    public IReadOnlyList<string> Prefixes => ["building"];

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        using var _ = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
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
                return ToResult(settled, mutation, now);
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
                    PostPushes = BathPushes(settled, now),
                };
            }
            case "building.ProduceItem":
            {
                var (bid, rid, cnt) = ProtocolDecoder.DecodeProduceItemArg(request.Args ?? []);
                BuildingService.ProduceResult produce = await building.ProduceItemAsync(
                    ctx.ProfileId, bid, rid, cnt, ctx.Now, ctx.Ct);
                return ToProduceResult(settled, produce, now);
            }
            case "building.ComposeItem":
            {
                var (bid, rid, cnt) = ProtocolDecoder.DecodeProduceItemArg(request.Args ?? []);
                BuildingService.ProduceResult compose = await building.ComposeItemAsync(
                    ctx.ProfileId, bid, rid, cnt, ctx.Now, ctx.Ct);
                return ToProduceResult(settled, compose, now);
            }
            default:
                // 资源领取、生产队列与剧情操作尚未实现；只同步本次结算产生的变化。
                return new ModuleResult
                {
                    PrePushes = GameServices.BuildMoodSyncPushes(
                        settled.Account, settled.ChangedHeroIds, settled.BuildingChanged, now),
                    PostPushes = BathPushes(settled, now),
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
        byte[]? ret = null) =>
        mutation.Success
            ? new ModuleResult
            {
                Ret = ret ?? [],
                // 客户端在应答回调中立即读取 buildingData，因此快照必须先于应答到达。
                PrePushes = GameServices.BuildMoodSyncPushes(mutation.Account, settled.ChangedHeroIds, true, now),
                PostPushes = BathPushes(settled, now),
            }
            : Failure(settled, now, mutation.Err, mutation.ErrMsg);

    private ModuleResult ToProduceResult(SettlementResult settled, BuildingService.ProduceResult produce, uint now)
    {
        if (!produce.Success) return Failure(settled, now, produce.Err, produce.ErrMsg);
        var pushes = new List<byte[]> { services.BuildBagPush(produce.Account, now) };
        pushes.AddRange(GameServices.BuildMoodSyncPushes(produce.Account, settled.ChangedHeroIds, true, now));
        return new ModuleResult
        {
            Ret = ProtocolEncoder.EncodeReceiveRet(produce.Rewards),
            PrePushes = pushes,
            PostPushes = BathPushes(settled, now),
        };
    }

    /// <summary>业务失败时仍要同步已落盘的结算结果，否则客户端会用旧数据配新锚点外推。</summary>
    private static ModuleResult Failure(SettlementResult settled, uint now, int err, string errMsg) =>
        new()
        {
            Err = err,
            ErrMsg = errMsg,
            PrePushes = GameServices.BuildMoodSyncPushes(
                settled.Account, settled.ChangedHeroIds, settled.BuildingChanged, now),
            PostPushes = BathPushes(settled, now),
        };

    /// <summary>结算处理了浴券到期或祈愿墙跨日重置时，补发浴场 / 祈愿快照。</summary>
    private static IReadOnlyList<byte[]> BathPushes(SettlementResult settled, uint now) =>
        GameServices.BuildSettlementPostPushes(settled, now);

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
