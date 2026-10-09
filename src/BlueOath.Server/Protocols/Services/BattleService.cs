using System.Text;
using System.Text.Json;
using System.Linq;
using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server.Configs;
using BlueOath.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>关卡/战斗服务：copy.StartBase / copy.PassBase 的领域逻辑。</summary>
internal sealed class BattleService(GameServices services, DailyCopyService dailyCopy)
{
    /// <summary>
    /// copy.StartBase。开启「真实消耗资源」时在出击时扣燃料或共闘RP（<see cref="SortieCost"/>），一次出击只扣一次，撤退/失败不退。
    /// 余额不足时只扣到 0 并记日志、不拒绝：客户端出击前已按同一公式预检，拒绝会让出击流程卡住。
    /// 返回应答与需要放在应答前的推送（扣费后的玩家信息）。
    /// </summary>
    internal async Task<(byte[] Ret, IReadOnlyList<byte[]> PrePushes)> BuildStartBaseRetAsync(
        TRequest request, string profileId, int now, CancellationToken ct)
    {
        try
        {
            using var accountLock = await services.LockAccountAsync(profileId, ct);
            PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
            byte[] args = request.Args ?? [];
            StartBaseArg arg = ProtocolDecoder.DecodeStartBaseArg(args);
            var pre = new List<byte[]>();
            if (services.Cheats.RealResourceCost)
            {
                int chargedCopyId = SortieCostLoader.ChargedCopyId(arg.ChapterId, arg.CopyId, arg.IsRunningFight);
                (int currency, long cost) = SortieCost.Cost(SortieCostLoader.Get(chargedCopyId), arg.BattleMode, arg.IsPvePtMode,
                    arg.ShipCountsByList, ParameterCatalogLoader.Get(527, 1));
                if (cost > 0)
                {
                    GameServices.TryGetCurrency(account, currency, out int balance);
                    int charged = checked((int)Math.Min(cost, Math.Max(0, balance)));
                    account = GameServices.AddCurrency(account, currency, -charged);
                    await services.SaveAccountAsync(account, ct);
                    pre.Add(GameServices.BuildUpdateUserInfoPush(account, checked((uint)now)));
                    services.FileLogger.LogInformation(
                        "copy.StartBase cost copyId={CopyId} chargedCopyId={ChargedCopyId} mode={Mode} ships={Ships} pvePt={PvePt} currency={Currency} cost={Cost} charged={Charged}",
                        arg.CopyId, chargedCopyId, arg.BattleMode, string.Join("+", arg.ShipCountsByList ?? []), arg.IsPvePtMode,
                        currency, cost, charged);
                }
            }
            services.FileLogger.LogInformation(
                "copy.StartBase argsLen={Len} hex={Hex} copyId={CopyId} deployHeroIds={Deploy} isRunningFight={IsRunning}",
                args.Length, Convert.ToHexString(args), arg.CopyId,
                arg.DeployHeroIds is null ? "<null>" : string.Join(",", arg.DeployHeroIds), arg.IsRunningFight);
            List<Hero> heroList = account.Dock.Heroes.ToList();
            // 关卡出战舰队必须回环客户端请求里的 HeroList（剧情关限制），
            // 而不是从玩家编队猜。请求未带时回退到全部船。
            services.CopyRandomFactors.TryGetValue(arg.CopyId, out List<RandomFactorEntry>? randomFactors);
            return (ProtocolEncoder.EncodeStartBaseRet(arg.CopyId, heroList, account.Character, arg.DeployHeroIds, arg.IsRunningFight,
                arg.BattleMode, arg.MatchType, randomFactors, account.Equip), pre);
        }
        catch (Exception ex)
        {
            services.FileLogger.LogError(ex, "BuildStartBaseRetAsync failed");
            return ([], []);
        }
    }

    internal async Task<byte[]> BuildPassBaseRetAsync(TRequest request, string profileId, CancellationToken ct)
    {
        byte[] args = request.Args ?? [];
        PassBaseArg passArg = ProtocolDecoder.DecodePassBaseArgAll(args);
        int copyId = passArg.BaseId;
        int grade = passArg.Grade;
        int battleTime = passArg.BattleTime;
        if (copyId == 0) return ProtocolEncoder.EncodePassBaseRet(0, 0, 0, 0);

        using IDisposable accountLock = await services.LockAccountAsync(profileId, ct);
        PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
        int now = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        int passTime = battleTime > 0 ? battleTime : 60;
        int copyType = ChapterCopyLoader.GetCopyType(copyId);

        // 保存战斗结束后的角色生命值（客户端回传 HerosInfo.Hp，与 StartBase 的 HpCoefficient 同尺度）。
        // 无论胜负都要落盘——战败也会掉血。
        account = SaveHeroHp(account, passArg.HerosInfo);

        // 评级（config_copy_grade_type）：SSS=1..E=8，F=9 为失败。失败不结算战利品、不记录通关进度。
        // 货物副本（物资大作战，copyType 10）是伤害测试器：敌人血量极高、必定超时，
        // 超时即无条件胜利，按最终伤害发放报酬。因此 copyType 10 强制视为胜利。
        bool isGoodsCopy = copyType == 10;
        bool isVictory = grade < 9 || isGoodsCopy;
        if (!isVictory)
        {
            await services.SaveAccountAsync(account, ct);
            return ProtocolEncoder.EncodePassBaseRet(copyId, grade, 0, passTime);
        }

        // 关卡可能包含多支敌舰队：客户端每击破一支就会发一次 copy.PassBase。
        // 只有击破最后一支（config_fleet.is_last_fleet == 1）才算通关，届时才结算
        // 奖励与通关进度；否则仅落盘血量并返回空结果，避免第一支敌舰队就发奖励。
        // 货物副本（10）是单场伤害测试，按原逻辑即时结算，不做多舰队延迟。
        int enemyFleetId = passArg.FleetInfo is { Count: > 0 } fleetInfo ? fleetInfo[0].EnemyId : 0;
        bool isLastFleet = FleetDropLoader.IsLastFleet(enemyFleetId);
        services.FileLogger.LogInformation(
            "copy.PassBase copyId={CopyId} type={CopyType} enemyFleet={EnemyFleetId} isLastFleet={IsLastFleet} grade={Grade}",
            copyId, copyType, enemyFleetId, isLastFleet, grade);
        if (!isGoodsCopy && !isLastFleet)
        {
            await services.SaveAccountAsync(account, ct);
            return ProtocolEncoder.EncodePassBaseRet(copyId, grade, 0, passTime);
        }

        if (copyType == 10)
        {
            // 物资大作战：超时无条件胜利，奖励来自 config_copy_display.drop_info_id 掉落池。
            // 客户端回传 grade=9（F，超时），但伤害测试器应视为胜利；回传一个成功评级
            // （grade 6=D 及以上即胜），避免客户端 SettlementPage 按 grade==9 判失败。
            int winGrade = grade >= 9 || grade <= 0 ? 6 : grade;
            (account, List<CommonReward> goodsRewards) = GrantCopyRewards(account, copyId, true, now);
            await services.SaveAccountAsync(account, ct);
            // 从 PassBaseArg.Evaluate 提取伤害：货物副本按 config_parameter[172]
            // wuzidazuozhan_min_damage=500 作为缩放基数，CurReward = floor(总伤害 / 500)。
            // Evaluate 是 TPassEvaluate{Type,Value} 列表，取所有 Value 之和作为总伤害。
            long totalDamage = passArg.Evaluate?.Sum(e => (long)e.Value) ?? 0;
            long minDamage = services.Parameter(172, 500);
            int rewardCount = totalDamage > 0 ? (int)Math.Max(1, totalDamage / Math.Max(1, minDamage)) : goodsRewards.Sum(r => r.Num);
            // ExReward 供 GoodsCopyResultPage 显示。RankPercent 必须非 nil（页面按 ==-1 判无排名，
            // nil 会触发 RankPercent/100 算术崩溃）。
            List<CommonExtraReward> exReward =
            [
                new CommonExtraReward("RankPercent", -1),
                new CommonExtraReward("CopyId", copyId),
                new CommonExtraReward("CurDamage", checked((int)totalDamage)),
                new CommonExtraReward("CurCopyMaxDamage", checked((int)totalDamage)),
                new CommonExtraReward("MaxDamage", checked((int)totalDamage)),
                new CommonExtraReward("CurReward", rewardCount),
                new CommonExtraReward("TotalReward", rewardCount),
                new CommonExtraReward("MonthCardBonus", 0),
            ];
            return ProtocolEncoder.EncodePassBaseRet(copyId, winGrade, 1, passTime, goodsRewards, exReward);
        }

        if (copyType == 2)
        {
            (List<CopyRecord> seaRecords, bool isFirstPass) =
                RecordPass(account.SeaProgress?.Records ?? [], copyId, grade, now, passTime);
            account = account with { SeaProgress = new PlayerSeaCopyProgress(seaRecords) };
            (account, List<CommonReward> seaRewards) = GrantSeaCopyRewards(
                account, copyId, isFirstPass, now, FleetDropLoader.GetBattleFleets(copyId, enemyFleetId));
            await services.SaveAccountAsync(account, ct);
            return ProtocolEncoder.EncodePassBaseRet(copyId, grade, isFirstPass ? 1 : 0, passTime, seaRewards);
        }

        if (copyType == 9)
        {
            DailyCopyPassMutation mutation = dailyCopy.RecordPass(account, copyId, grade, now);
            account = mutation.Account;
            await services.SaveAccountAsync(account, ct);
            return ProtocolEncoder.EncodePassBaseRet(
                copyId, grade, mutation.FirstPass ? 1 : 0, passTime, mutation.Rewards);
        }

        // ムーボー防卫圈（Tower）：关卡不在 config_chapter.level_list，单独记录到
        // PlayerTowerProgress.SavePassCopyId；客户端据此判断已通关并解锁下一关。
        if (TowerCatalogLoader.IsTowerCopy(copyId))
        {
            (account, bool towerFirstPass) = RecordTowerPass(account, copyId);
            (account, List<CommonReward> towerRewards) = GrantCopyRewards(account, copyId, towerFirstPass, now);
            await services.SaveAccountAsync(account, ct);
            return ProtocolEncoder.EncodePassBaseRet(copyId, grade, towerFirstPass ? 1 : 0, passTime, towerRewards);
        }

        (List<CopyRecord> records, bool isPlotFirstPass) =
            RecordPass(account.CopyProgress?.Records ?? [], copyId, grade, now, passTime);
        account = AdvancePlotChapter(account with { CopyProgress = new PlayerCopyProgress(records) }, copyId);
        (account, List<CommonReward> plotRewards) = GrantCopyRewards(account, copyId, isPlotFirstPass, now);
        await services.SaveAccountAsync(account, ct);
        return ProtocolEncoder.EncodePassBaseRet(copyId, grade, isPlotFirstPass ? 1 : 0, passTime, plotRewards);
    }

    /// <summary>扫荡作战按胜利结算时用的评级（SSS）：通关记录的评级取较大值，只用来满足发奖门槛（grade &gt; 0）。</summary>
    internal const int SweepGrade = 1;

    /// <summary>扫荡一轮的结算：Rewards 进 TPassBaseRet.Reward(1)，ChaseRewards（追击关卡的掉落）进 ExtraReward(9)。</summary>
    internal sealed record SweepRunResult(
        PlayerAccount Account, IReadOnlyList<CommonReward> Rewards, IReadOnlyList<CommonReward> ChaseRewards);

    /// <summary>
    /// 扫荡作战的一轮，等同 copy.PassBase 的一次非首通胜利：按关卡类型记一次通关（海域 / 剧情与活动关卡的通关记录、
    /// 防卫圈的已通关列表、每日副本的挑战与成功次数）并按同样的规则发放掉落；不发首通奖励，也不加经验（PassBase 同样不加）。
    /// 没有真实战斗，海域关卡按 config_copy.random_weight 抽一组敌舰队结算舰队掉落。
    /// <paramref name="chaseCopyId"/> 非 0 时另结算追击关卡的掉落（海域按追击关卡的舰队掉落，其余按追击关卡的掉落池），单独返回。
    /// </summary>
    internal SweepRunResult GrantSweepRun(PlayerAccount account, int copyId, int chaseCopyId, int now)
    {
        int copyType = ChapterCopyLoader.GetCopyType(copyId);
        if (copyType == 9)
        {
            DailyCopyPassMutation daily = dailyCopy.RecordPass(
                account, copyId, DailyCopyService.SweepGrade(account, copyId), now, grantFirstPass: false);
            return new SweepRunResult(daily.Account, daily.Rewards, []);
        }

        List<CommonReward> rewards;
        if (copyType == 10)
        {
            (account, rewards) = GrantCopyRewards(account, copyId, false, now);
        }
        else if (copyType == 2)
        {
            (List<CopyRecord> seaRecords, _) = RecordPass(account.SeaProgress?.Records ?? [], copyId, SweepGrade, now, null);
            account = account with { SeaProgress = new PlayerSeaCopyProgress(seaRecords) };
            (account, rewards) = GrantSeaCopyRewards(
                account, copyId, false, now, FleetDropLoader.PickBattleFleets(copyId, services.Rng));
        }
        else if (TowerCatalogLoader.IsTowerCopy(copyId))
        {
            (account, _) = RecordTowerPass(account, copyId);
            (account, rewards) = GrantCopyRewards(account, copyId, false, now);
        }
        else
        {
            (List<CopyRecord> records, _) = RecordPass(account.CopyProgress?.Records ?? [], copyId, SweepGrade, now, null);
            account = AdvancePlotChapter(account with { CopyProgress = new PlayerCopyProgress(records) }, copyId);
            (account, rewards) = GrantCopyRewards(account, copyId, false, now);
        }

        List<CommonReward> chaseRewards = [];
        if (chaseCopyId > 0)
            (account, chaseRewards) = copyType == 2
                ? GrantSeaCopyRewards(account, chaseCopyId, false, now, FleetDropLoader.PickBattleFleets(chaseCopyId, services.Rng))
                : GrantCopyRewards(account, chaseCopyId, false, now);
        return new SweepRunResult(account, rewards, chaseRewards);
    }

    /// <summary>
    /// 记一次通关：首通追加记录，否则星级与评级取较大值、通关次数 +1。
    /// <paramref name="passTime"/> 为本次通关用时；为 null 时（扫荡，没有战斗）保留原记录的用时，新记录记 0。
    /// </summary>
    private static (List<CopyRecord> Records, bool FirstPass) RecordPass(
        IReadOnlyList<CopyRecord> source, int copyId, int grade, int now, int? passTime)
    {
        List<CopyRecord> records = source.ToList();
        int index = records.FindIndex(r => r.CopyId == copyId);
        int starLevel = grade > 0 ? 7 : 0;
        if (index < 0)
        {
            records.Add(new CopyRecord(copyId, starLevel, grade, now, passTime ?? 0, 1));
            return (records, true);
        }
        CopyRecord existing = records[index];
        records[index] = existing with
        {
            StarLevel = Math.Max(existing.StarLevel, starLevel),
            Grade = Math.Max(existing.Grade, grade),
            PassTime = passTime ?? existing.PassTime,
            PassCount = existing.PassCount + 1
        };
        return (records, false);
    }

    /// <summary>防卫圈：首次通关时把关卡追加到 SavePassCopyId。</summary>
    private static (PlayerAccount Account, bool FirstPass) RecordTowerPass(PlayerAccount account, int copyId)
    {
        PlayerTowerProgress tower = account.Tower ?? new PlayerTowerProgress([]);
        List<int> passList = tower.SavePassCopyId?.ToList() ?? [];
        bool firstPass = !passList.Contains(copyId);
        if (firstPass) passList.Add(copyId);
        return (account with { Tower = new PlayerTowerProgress(passList) }, firstPass);
    }

    /// <summary>剧情关卡通关后把 PlotChapterId 推进到该关所在章节（只增不减）。</summary>
    private static PlayerAccount AdvancePlotChapter(PlayerAccount account, int copyId)
    {
        PlayerCharacter c = account.Character;
        int bestChapter = GameServices.FindChapterForCopy(copyId, c.PlotChapterId);
        return bestChapter > c.PlotChapterId ? account with { Character = c with { PlotChapterId = bestChapter } } : account;
    }

    /// <summary>把客户端回传的战斗后生命值写回对应舰娘（HerosInfo.HeroId → Hero.CurHp）。</summary>
    private static PlayerAccount SaveHeroHp(PlayerAccount account, IReadOnlyList<BaseHeroInfo>? herosInfo)
    {
        if (herosInfo is null || herosInfo.Count == 0) return account;
        List<Hero> heroes = account.Dock.Heroes.ToList();
        bool changed = false;
        foreach (BaseHeroInfo info in herosInfo)
        {
            if (info.HeroId == 0) continue;
            int idx = heroes.FindIndex(h => h.HeroId == info.HeroId);
            if (idx < 0) continue;
            heroes[idx] = heroes[idx] with { CurHp = checked((long)info.Hp) };
            changed = true;
        }
        return changed ? account with { Dock = account.Dock with { Heroes = heroes } } : account;
    }

    /// <summary>
    /// 从 config_copy_display 读取首通奖励（first_reward → config_rewards）与掉落池
    /// （drop_info_id → config_drop_item），抽取并发放战利品，返回更新后的账号与奖励列表。
    /// 用于非海域副本（剧情/货物/防卫圈等）的既定行为。
    /// </summary>
    private (PlayerAccount Account, List<CommonReward> Rewards) GrantCopyRewards(
        PlayerAccount account, int copyId, bool isFirstPass, int now)
    {
        CopyDisplayLoader.CopyDropInfo? dropInfo = CopyDisplayLoader.Get(copyId);
        if (dropInfo is null) return (account, []);

        var pending = new List<DropEntry>();

        if (isFirstPass)
            foreach (int rewardId in dropInfo.FirstReward)
                AppendReward(rewardId, pending);

        foreach (int dropId in dropInfo.DropInfoId)
            pending.AddRange(DropPoolResolver.Resolve(dropId, services.DropItems, services.Rng));

        return ApplyPendingRewards(account, pending, now);
    }

    /// <summary>
    /// 海域（含周回海域，class_type=2）通关结算：
    /// <list type="bullet">
    /// <item>首通：<c>first_reward</c>（config_rewards）；</item>
    /// <item>每次通关：<c>period_drop</c>（config_drop_item，周回海域的周期掉落）；</item>
    /// <item>本次出击击破的整组敌舰队（<paramref name="battleFleets"/>）各自的 <c>drop_id</c>/<c>settle_drop_ids</c>/<c>other_drop_ids</c>。</item>
    /// </list>
    /// <c>drop_info_id</c> 只是客户端预览数据（config_drop_info），不参与发放。
    /// </summary>
    private (PlayerAccount Account, List<CommonReward> Rewards) GrantSeaCopyRewards(
        PlayerAccount account, int copyId, bool isFirstPass, int now, IReadOnlyList<int> battleFleets)
    {
        var pending = new List<DropEntry>();

        CopyDisplayLoader.CopyDropInfo? dropInfo = CopyDisplayLoader.Get(copyId);
        if (isFirstPass && dropInfo is not null)
            foreach (int rewardId in dropInfo.FirstReward)
                AppendReward(rewardId, pending);

        if (dropInfo is { PeriodDrop: > 0 })
            pending.AddRange(DropPoolResolver.Resolve(dropInfo.PeriodDrop, services.DropItems, services.Rng));

        foreach (int fleetId in battleFleets)
        {
            FleetDropLoader.FleetDropInfo? fleet = FleetDropLoader.Get(fleetId);
            if (fleet is null) continue;
            foreach (int dropId in fleet.DropIds)
                pending.AddRange(DropPoolResolver.Resolve(dropId, services.DropItems, services.Rng));
            foreach (int dropId in fleet.SettleDropIds)
                pending.AddRange(DropPoolResolver.Resolve(dropId, services.DropItems, services.Rng));
            foreach (int dropId in fleet.OtherDropIds)
                pending.AddRange(DropPoolResolver.Resolve(dropId, services.DropItems, services.Rng));
        }

        return ApplyPendingRewards(account, pending, now);
    }

    /// <summary>把已抽出的掉落条目发放到账号，返回更新后的账号与已发放奖励。</summary>
    private (PlayerAccount Account, List<CommonReward> Rewards) ApplyPendingRewards(
        PlayerAccount account, List<DropEntry> pending, int now)
    {
        var rewards = new List<CommonReward>();
        foreach (DropEntry entry in pending)
        {
            int type = entry.Type;
            int configId = entry.ConfigId;
            int num = entry.Num;
            if (type == GameServices.GoodsTypeCurrency)
            {
                account = GameServices.AddCurrency(account, configId, num);
                rewards.Add(new CommonReward(type, configId, num));
            }
            else if (type == GameServices.GoodsTypeEquip)
            {
                for (int i = 0; i < num; i++)
                {
                    (account, uint equipId) = AddEquip(account, configId);
                    rewards.Add(new CommonReward(type, configId, 1, checked((int)equipId)));
                }
            }
            else if (type == GameServices.GoodsTypeShip)
            {
                uint heroId = services.NextHeroId();
                account = services.AddShip(account, heroId, configId, now);
                rewards.Add(new CommonReward(type, configId, 1, checked((int)heroId)));
            }
            else
            {
                account = GameServices.AddBagItem(account, configId, num);
                rewards.Add(new CommonReward(type, configId, num));
            }
        }
        return (account, rewards);
    }

    private static void AppendReward(int rewardId, List<DropEntry> pending)
    {
        if (rewardId <= 0 ||
            DailyCopyRewardCatalog.GetReward(rewardId) is not { Rewards: { } rewards })
            return;
        foreach (List<long> entry in rewards)
            if (entry.Count >= 3 && entry[2] > 0)
                pending.Add(new DropEntry(checked((int)entry[0]), checked((int)entry[1]), checked((int)entry[2])));
    }

    private (PlayerAccount Account, uint EquipId) AddEquip(PlayerAccount account, int templateId)
    {
        var equip = account.Equip ?? new PlayerEquip([], EquipBagSize: 2000);
        var items = equip.Items.ToList();
        uint equipId = services.NextEquipId();
        items.Add(new EquipItem(EquipId: equipId, TemplateId: templateId));
        account = account with { Equip = equip with { Items = items } };
        return (account, equipId);
    }
}
