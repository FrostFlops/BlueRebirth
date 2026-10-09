using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>抽卡模块：buildship.*（BuildShip 等）+ 祈愿墙 illustrate.ModiVowHeroList / VowHero / VowDecTime。</summary>
internal sealed class BuildShipModule(BuildShipService buildShip, GameServices services, BuildPoolsConfig buildPoolsConfig) : IGameModule
{
    public IReadOnlyList<string> Prefixes => ["buildship", "illustrate"];

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        ModuleResult result;
        switch (request.Method)
        {
            case "buildship.BuildShip":
                var ret = await buildShip.BuildBuildShipRetAsync(request, ctx.ProfileId, ctx.Ct);
                // 应答前推送抽卡结果：抽到舰娘推船坞/图鉴；无论如何都推装备仓库。
                var pre = new List<byte[]>();
                if (ret.Length != 0)
                {
                    var account = await ctx.GetAccountAsync();
                    var newIds = services.GetLastBuildHeroIds();
                    var newHeroes = account.Dock.Heroes
                        .Where(h => newIds.Contains(h.HeroId))
                        // HeroGrid.Name is the player-defined nickname. Leave it empty for an
                        // unrenamed ship so the client falls back to its localized ship_name.
                        .Select(GameServices.ToHeroGrid)
                        .ToList();
                    var now = (uint)ctx.Now;
                    if (newHeroes.Count > 0)
                    {
                        pre.Add(TMessageCodec.EncodeResponse(new TResponse(
                            Method: "hero.UpdateHeroBagData",
                            Ret: PlayerDataCodec.Encode(new HeroBag(newHeroes, account.Dock.BagSize)),
                            Time: now)));
                        pre.Add(GameServices.BuildIllustratePush(
                            account,
                            newHeroes
                                .Select(h => GameServices.BuildUnlockedIllustrateInfo(
                                    GameServices.ToIllustrateId(h.TemplateId), now))
                                .ToList(),
                            now));
                    }
                    // 新舰娘自带默认装备（config_ship_info.equip1..equip6），纯装备抽卡也会新增
                    // EquipItem，推送完整装备仓库让客户端 equipdata 拿到新增装备。
                    pre.Add(services.BuildEquipPush(account, now));
                    // 卡池可能含道具（如伊168池），抽到道具走 AddBagItem 入背包，
                    // 必须推送 bag.UpdateBagData，否则客户端要重启才能看到新道具。
                    pre.Add(services.BuildBagPush(account, now));
                    // 推送最新累计抽数/领奖状态，客户端累计奖励 UI 无需重登即可刷新。
                    pre.Add(TMessageCodec.EncodeResponse(new TResponse(
                        Method: "buildship.BuildShipInfo",
                        Ret: ProtocolEncoder.EncodeBuildShipInfo(buildPoolsConfig.EnabledPoolIds, now, account.BuildState),
                        Time: now)));
                }
                result = new ModuleResult { Ret = ret, PrePushes = pre };
                break;
            case "buildship.BuildShipInfo":
                result = ModuleResult.Ok(new byte[] { 0x08, 0x00 }); // DrawInfo: empty
                break;
            case "illustrate.ModiVowHeroList":
                result = await ModiVowHeroListAsync(ctx, request);
                break;
            case "illustrate.VowHero":
                result = await VowHeroAsync(ctx, request);
                break;
            case "illustrate.VowDecTime":
                result = await VowDecTimeAsync(ctx, request);
                break;
            case "illustrate.AddBehaviour":
                result = ModuleResult.Ok(await buildShip.BuildAddBehaviourRetAsync(request, ctx.ProfileId, ctx.Ct));
                break;
            case "buildship.BuildShipBox":
                result = ModuleResult.Ok(await buildShip.BuildBuildShipBoxRetAsync(request, ctx.ProfileId, ctx.Ct));
                result = await AttachBuildRewardPushesAsync(result, ctx);
                break;
            case "buildship.BuildShipReward":
                result = ModuleResult.Ok(await buildShip.BuildBuildShipRewardRetAsync(request, ctx.ProfileId, ctx.Ct));
                result = await AttachBuildRewardPushesAsync(result, ctx);
                break;
            default:
                result = ModuleResult.Empty;
                break;
        }
        return result;
    }

    /// <summary>illustrate.ModiVowHeroList：持久化墙上的舰娘（客户端本地已是同一份列表，不额外推送）。</summary>
    private async Task<ModuleResult> ModiVowHeroListAsync(GameContext ctx, TRequest request)
    {
        using var accountLock = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
        long now = Math.Max(ctx.Now, settled.Account.LastSettleTime);
        uint pushTime = checked((uint)ctx.Now);
        List<int> wall = VowLogic.SanitizeWall(ProtocolDecoder.DecodeChooseHeroList(request.Args ?? []), services.VowRules);
        PlayerAccount account = settled.Account;
        PlayerVow vow = account.Vow ?? new PlayerVow(UseResetDay: VowLogic.Day(now));
        if (account.Vow is null || !(vow.HeroList ?? []).SequenceEqual(wall))
        {
            account = account with { Vow = vow with { HeroList = wall } };
            await services.SaveAccountAsync(account, ctx.Ct);
        }
        var post = new List<byte[]>();
        if (settled.BathChanged) post.Add(GameServices.BuildBathroomInfoPush(account, pushTime));
        if (settled.VowChanged) post.Add(GameServices.BuildIllustratePush(account, [], pushTime));
        return new ModuleResult
        {
            Ret = [],
            PrePushes = GameServices.BuildMoodSyncPushes(account, settled.ChangedHeroIds, settled.BuildingChanged, pushTime),
            PostPushes = post,
        };
    }

    /// <summary>
    /// illustrate.VowHero：在墙上可祈愿的舰娘中随机取一名（UR ムーバー 系得到碎片），计算并持久化冷却。
    /// 日服 _VowHero 不会在本地设置 VowHero，冷却舰娘与冷却时间只能来自应答前的 illustrate.IllustrateInfo，
    /// 缺失时祈愿结果页会因 GetShipInfoIdByTid(0) 报错，冷却也就被跳过。
    /// </summary>
    private async Task<ModuleResult> VowHeroAsync(GameContext ctx, TRequest request)
    {
        using var accountLock = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
        PlayerAccount account = settled.Account;
        long now = Math.Max(ctx.Now, account.LastSettleTime);
        uint pushTime = checked((uint)ctx.Now);
        VowRules rules = services.VowRules;
        VowPick pick = VowLogic.PickWish(
            account, ProtocolDecoder.DecodeChooseHeroList(request.Args ?? []), now, rules, Random.Shared.Next);
        var pre = new List<byte[]>(
            GameServices.BuildMoodSyncPushes(account, settled.ChangedHeroIds, settled.BuildingChanged, pushTime));
        var post = new List<byte[]>();
        if (settled.BathChanged) post.Add(GameServices.BuildBathroomInfoPush(account, pushTime));
        if (pick.Failure != VowFailure.None)
        {
            // 冷却中 / 无可选 / 船坞已满：不改档，推送当前快照让客户端自我修正。
            pre.Add(GameServices.BuildIllustratePush(account, [], pushTime));
            return new ModuleResult { Err = 1, ErrMsg = "vow " + pick.Failure, PrePushes = pre, PostPushes = post };
        }

        byte[] ret;
        uint heroId = 0;
        if (pick.IsFragment)
        {
            account = GameServices.AddBagItem(account, pick.FragmentItem, pick.FragmentNum);
            ret = ProtocolEncoder.EncodeVowHeroRet(GameServices.GoodsTypeItem, pick.FragmentItem, pick.FragmentNum, 0);
        }
        else
        {
            heroId = services.NextHeroId();
            account = services.AddShip(account, heroId, pick.TemplateId, checked((int)now));
            ret = ProtocolEncoder.EncodeVowHeroRet(GameServices.GoodsTypeShip, pick.TemplateId, 1, checked((int)heroId));
        }
        // 「许愿墙」作弊：冷却写 0（客户端 CheckCharge 视 ≤0 为不在冷却）；冷却舰娘照常写入，结果页要用它。
        long coolTime = rules.OmitCooldown
            ? 0
            : now + VowLogic.FinalChargeTime(account.Dock, pick.Wall, pick.ShipInfoId, now, rules);
        PlayerVow vow = account.Vow ?? new PlayerVow(UseResetDay: VowLogic.Day(now));
        account = account with { Vow = vow with { CoolTime = coolTime, CoolHero = pick.TemplateId, HeroList = pick.Wall } };
        await services.SaveAccountAsync(account, ctx.Ct);

        if (pick.IsFragment)
        {
            pre.Add(services.BuildBagPush(account, pushTime));
        }
        else
        {
            pre.Add(GameServices.BuildHeroPartialPush(account, [heroId], pushTime));
            // AddShip 会发放默认装备，装备仓库要同步。
            pre.Add(services.BuildEquipPush(account, pushTime));
        }
        pre.Add(GameServices.BuildIllustratePush(
            account,
            pick.IsFragment ? [] : [GameServices.BuildUnlockedIllustrateInfo(pick.ShipInfoId, now)],
            pushTime));
        // 日服祈愿页收到结果后会按客户端公式自己设一次冷却（wishpage _OpenGetHeroPage → SetChargeTime），
        // 作弊时在应答之后再推一次快照把它覆盖回 0。
        if (rules.OmitCooldown) post.Add(GameServices.BuildIllustratePush(account, [], pushTime));
        return new ModuleResult { Ret = ret, PrePushes = pre, PostPushes = post };
    }

    /// <summary>
    /// illustrate.VowDecTime：用祈愿石缩短冷却。客户端在应答回调里播放冷却滚动动画，
    /// 所以扣减后的冷却必须由应答前的 illustrate.IllustrateInfo 送达；Ret 始终非空。
    /// </summary>
    private async Task<ModuleResult> VowDecTimeAsync(GameContext ctx, TRequest request)
    {
        using var accountLock = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
        long now = Math.Max(ctx.Now, settled.Account.LastSettleTime);
        uint pushTime = checked((uint)ctx.Now);
        var (useInfo, _) = ProtocolDecoder.DecodeVowDecTimeArgs(request.Args ?? []);
        VowDecResult dec = VowLogic.DecTime(settled.Account, useInfo, now, services.VowRules);
        if (dec.Changed) await services.SaveAccountAsync(dec.Account, ctx.Ct);
        VowSnapshot snapshot = VowLogic.Snapshot(dec.Account.Vow, now);
        var pre = new List<byte[]>(
            GameServices.BuildMoodSyncPushes(dec.Account, settled.ChangedHeroIds, settled.BuildingChanged, pushTime))
        {
            services.BuildBagPush(dec.Account, pushTime),
            GameServices.BuildIllustratePush(dec.Account, [], pushTime),
        };
        return new ModuleResult
        {
            Ret = ProtocolEncoder.EncodeVowDecTimeRet(snapshot.Count, snapshot.CoolTime),
            PrePushes = pre,
            PostPushes = settled.BathChanged ? [GameServices.BuildBathroomInfoPush(dec.Account, pushTime)] : [],
        };
    }

    /// <summary>领奖后附加推送：新舰娘（抽卡宝箱可能出船）推船坞/图鉴/装备，并推送累计奖状态。</summary>
    private async Task<ModuleResult> AttachBuildRewardPushesAsync(ModuleResult result, GameContext ctx)
    {
        if (result.Ret.Length == 0) return result;

        var account = await ctx.GetAccountAsync();
        var now = (uint)ctx.Now;
        var newIds = services.GetLastBuildHeroIds();
        var newHeroes = account.Dock.Heroes
            .Where(h => newIds.Contains(h.HeroId))
            .Select(GameServices.ToHeroGrid)
            .ToList();

        var pushes = new List<byte[]>();
        if (newHeroes.Count > 0)
        {
            pushes.Add(TMessageCodec.EncodeResponse(new TResponse(
                Method: "hero.UpdateHeroBagData",
                Ret: PlayerDataCodec.Encode(new HeroBag(newHeroes, account.Dock.BagSize)),
                Time: now)));
            pushes.Add(GameServices.BuildIllustratePush(
                account,
                newHeroes.Select(h => new IllustrateInfo((h.TemplateId - 1) / 10, now, 0, false, null, 0)).ToList(),
                now));
        }
        pushes.Add(services.BuildEquipPush(account, now));
        // 抽卡宝箱/领奖可能含道具，推送背包让客户端立即看到新增素材。
        pushes.Add(services.BuildBagPush(account, now));
        // 推送最新累计抽数/领奖状态。
        pushes.Add(TMessageCodec.EncodeResponse(new TResponse(
            Method: "buildship.BuildShipInfo",
            Ret: ProtocolEncoder.EncodeBuildShipInfo(buildPoolsConfig.EnabledPoolIds, now, account.BuildState),
            Time: now)));

        return new ModuleResult { Ret = result.Ret, PrePushes = pushes };
    }

}
