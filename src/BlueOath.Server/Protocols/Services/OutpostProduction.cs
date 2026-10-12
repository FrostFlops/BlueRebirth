using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>
/// アンブラ前哨的纯函数（不读时钟、不做 IO）：按时间结算（由 <see cref="TimeSettlement.Settle"/> 调用）、派驻、
/// 升级、自动入浴开关、加速与领取。
/// <para>
/// 客户端（MubarOutpostPage / MubarOutpostService / MubarOutpostLogic）不计算产出，只每 60 秒请求一次
/// outpost.GetOutPostInfo，从 TBaseBuildingInfo.ItemInfo 读可领取的产出，因此规则全部在服务端：
/// <list type="bullet">
/// <item>产出：config_outpost_level.reward 的第三项是倍率 100% 时每秒产量 × 1e8（例：资金 34722222 ≈ 每小时 1250、
/// 六殻 55555 ≈ 每小时 2）。只在有驻守舰娘且至少一人心情大于 0 的时段产出；不足 1 件的部分记在 Progress 里结转。
/// 倍率按客户端 GetDropByBattlePower 由驻守舰娘总战力查 drop_level，服务端算不出客户端的战力，固定按起始倍率 100%。</item>
/// <item>心情：驻守舰娘每 600 秒（config_parameter[207]）扣 config_outpost_info.mood_cost（10500 = 1.05），与基建工作楼相同；
/// 心情归零且开启了自动入浴（UseCoin）时消耗 config_parameter[406] 的 50 温泉币回复 60 心情。</item>
/// <item>状态（客户端 SetChapterInfo）：0 停止、1 工作中、其它为空闲；没有驻守舰娘的前哨编码为 2。</item>
/// <item>加速：每天所有前哨共 config_parameter[407] = 2 次（TOutPostInfo.SpeedUpTime 为已用次数），「真实消耗资源」开启时
/// 消耗 speedup_cost（燃料 1500），立即获得 speedup_reward = [倍率, 小时] 的产出（150% × 24 小时；倍率与 drop_level 最高档相同），直接进背包；
/// 只有工作中的前哨能加速。</item>
/// </list>
/// 「扫荡跳过时间」作弊（SettlementRules.OutpostInstant）下保持离线版原来的规则：不随时间产出、不扣心情，
/// 加速不限次数、不消耗，把一份 reward 原值放进 ItemInfo。
/// </para>
/// </summary>
internal static class OutpostProduction
{
    internal const int Stopped = 0;
    internal const int Working = 1;
    internal const int Idle = 2;
    internal const int MaxLevel = 6;

    /// <summary>reward 第三项的比例尺：每秒产量 × 1e8。</summary>
    internal const double RateScale = 1e8;

    /// <summary>客户端 GetDropByBattlePower 的起始倍率（总战力低于 drop_level 第一档时为 100%）。</summary>
    internal const int BaseRatePercent = 100;

    /// <summary>floor 容差：远小于任何产出项 1 秒的产量。</summary>
    private const double Eps = 1e-9;

    /// <summary>派驻、升级、加速、领取的结果。Rewards 写进 TOPReceiveRet.ItemInfo。</summary>
    internal sealed record Outcome(
        PlayerAccount Account,
        IReadOnlyList<OutpostItem> Rewards,
        bool CurrencyChanged = false,
        bool BagChanged = false,
        bool BuildingChanged = false,
        int Err = 0,
        string ErrMsg = "")
    {
        internal bool Success => Err == 0;
    }

    private static PlayerOutpost State(PlayerAccount account) => account.Outpost ?? PlayerAccountFactory.DefaultOutpost();

    private static Outcome Fail(PlayerAccount account, string message) => new(account, [], Err: 1, ErrMsg: message);

    // ───────────────────────── 按时间结算 ─────────────────────────

    /// <summary>
    /// 把前哨结算到 now（TimeSettlement.Settle 第 5 步）。成员规范化：船坞里已没有、在浴场或基建里的舰娘，
    /// 以及在更靠前的前哨里重复出现、超出驻守人数的舰娘移出。随后按时间扣驻守舰娘心情、自动入浴、产出，
    /// 并把锚点推进到 now；加速次数跨日（UTC+8）归零。舰娘改动写回 heroes 并记入 changedHeroes。
    /// 没有变化时返回同一个实例。
    /// </summary>
    internal static PlayerOutpost Settle(
        PlayerOutpost outpost, Dictionary<uint, Hero> heroes, IReadOnlySet<uint> busy, uint secretaryId, int bathCoins,
        long now, SettlementRules rules, ISet<uint> changedHeroes, out bool visible, out bool touched, out int coinsSpent)
    {
        visible = false;
        touched = false;
        int coins = bathCoins;
        int refillBudget = rules.OutpostRefillLimit;
        var assigned = new HashSet<uint>();
        var buildings = new PlayerOutpostBuilding[outpost.Buildings.Count];
        for (int i = 0; i < outpost.Buildings.Count; i++)
        {
            PlayerOutpostBuilding building = outpost.Buildings[i];
            ConfigOutpostLevel? cfg = rules.OutpostLevel(building.Id, building.Level);
            uint[] members = Members(building, cfg, heroes, busy, assigned);
            IReadOnlyList<uint>? heroIds = members.SequenceEqual(building.HeroIds ?? []) ? building.HeroIds : members;
            long updateTime = building.UpdateTime;
            int state = building.State;
            IReadOnlyList<OutpostItem>? items = building.ItemInfo;
            IReadOnlyList<OutpostProgress>? progress = building.Progress;
            if (members.Length == 0)
            {
                updateTime = 0;
            }
            else if (rules.OutpostInstant)
            {
                updateTime = 0;
                state = Working;
            }
            else
            {
                long anchor = building.UpdateTime == 0 ? now : building.UpdateTime;
                long elapsed = Math.Max(0, now - anchor);
                long work = elapsed > 0
                    ? Drain(building, members, heroes, secretaryId, elapsed, now, rules, ref coins, ref refillBudget, changedHeroes)
                    : 0;
                if (work > 0 && cfg is not null)
                {
                    (List<OutpostItem> whole, progress) = Accrue(progress, cfg, work, BaseRatePercent);
                    if (whole.Count > 0) items = Merge(items, whole);
                }
                updateTime = Math.Max(anchor, now);
                state = IsWorking(building.Id, building.UseCoin, members, heroes, coins, now, rules) ? Working : Stopped;
            }

            bool shown = !ReferenceEquals(heroIds, building.HeroIds) || state != building.State ||
                         !ReferenceEquals(items, building.ItemInfo);
            bool changed = shown || updateTime != building.UpdateTime || !ReferenceEquals(progress, building.Progress);
            buildings[i] = changed
                ? building with { HeroIds = heroIds, State = state, ItemInfo = items, UpdateTime = updateTime, Progress = progress }
                : building;
            visible |= shown;
            touched |= changed;
        }

        coinsSpent = bathCoins - coins;
        int speedUpTime = outpost.SpeedUpTime;
        if (speedUpTime > 0 && (rules.OutpostInstant || VowLogic.Day(now) > outpost.SpeedUpDay))
        {
            speedUpTime = 0;
            visible = true;
            touched = true;
        }
        return touched ? outpost with { Buildings = buildings, SpeedUpTime = speedUpTime } : outpost;
    }

    /// <summary>有效驻守成员：在船坞、不在浴场与基建、没有被更靠前的前哨占用，按顺序取到驻守人数（ship_num）为止。</summary>
    private static uint[] Members(
        PlayerOutpostBuilding building, ConfigOutpostLevel? cfg, IReadOnlyDictionary<uint, Hero> heroes,
        IReadOnlySet<uint> busy, HashSet<uint> assigned)
    {
        long cap = cfg is null ? long.MaxValue : Math.Max(0, cfg.ShipNum);
        var result = new List<uint>();
        foreach (uint id in building.HeroIds ?? [])
        {
            if (result.Count >= cap) break;
            if (!heroes.ContainsKey(id) || busy.Contains(id) || assigned.Contains(id) || result.Contains(id)) continue;
            result.Add(id);
        }
        assigned.UnionWith(result);
        return result.ToArray();
    }

    /// <summary>
    /// 驻守舰娘在 elapsed 秒里的心情消耗（先做自然恢复，与基建工作楼的结算顺序相同），返回前哨的工作秒数
    /// （任一舰娘心情大于 0 的时长）。心情不够扣时，开启自动入浴的前哨按需消耗温泉币，每次回复 OutpostRefillMood。
    /// </summary>
    private static long Drain(
        PlayerOutpostBuilding building, uint[] members, Dictionary<uint, Hero> heroes, uint secretaryId, long elapsed,
        long now, SettlementRules rules, ref int coins, ref int refillBudget, ISet<uint> changedHeroes)
    {
        int moodCost = rules.OutpostMoodCost(building.Id);
        if (rules.OmitMoodCost || moodCost <= 0) return elapsed;
        double perSecond = (double)moodCost / rules.CostUnit;
        double cost = elapsed * perSecond;
        long work = 0;
        foreach (uint heroId in members)
        {
            Hero before = heroes[heroId];
            Hero natural = TimeSettlement.ApplyNaturalToHero(before, now, rules, heroId == secretaryId);
            double mood = natural.Mood;
            if (mood - cost <= 0 && building.UseCoin == 1 && rules.OutpostRefillMood > 0)
            {
                long needed = (long)Math.Floor((cost - mood) / rules.OutpostRefillMood) + 1;
                long affordable = rules.OutpostRefillCoins > 0 ? coins / rules.OutpostRefillCoins : needed;
                int refills = (int)Math.Max(0, Math.Min(needed, Math.Min(affordable, refillBudget)));
                coins -= refills * Math.Max(0, rules.OutpostRefillCoins);
                refillBudget -= refills;
                mood += (double)refills * rules.OutpostRefillMood;
            }
            double left = mood - cost;
            long heroWork = left > 0 ? elapsed : Math.Clamp((long)Math.Floor(mood / perSecond), 0, elapsed);
            Hero after = natural with { Mood = TimeSettlement.ClampMood((long)Math.Floor(Math.Max(0, left)), rules) };
            if (after != before)
            {
                heroes[heroId] = after;
                changedHeroes.Add(heroId);
            }
            work = Math.Max(work, heroWork);
        }
        return work;
    }

    /// <summary>前哨是否工作中：有驻守舰娘，且有人心情大于 0（或不扣心情，或心情归零后还能自动消耗温泉币）。</summary>
    internal static bool IsWorking(
        int outpostId, int useCoin, IReadOnlyList<uint> members, IReadOnlyDictionary<uint, Hero> heroes, int bathCoins,
        long now, SettlementRules rules)
    {
        if (members.Count == 0) return false;
        if (rules.OutpostInstant || rules.OmitMoodCost || rules.OutpostMoodCost(outpostId) <= 0) return true;
        foreach (uint heroId in members)
            if (heroes.TryGetValue(heroId, out Hero? hero) && BuildingProduction.EffectiveMood(hero, now, rules) > 0)
                return true;
        return useCoin == 1 && rules.OutpostRefillMood > 0 && bathCoins >= Math.Max(0, rules.OutpostRefillCoins);
    }

    /// <summary>
    /// 把 seconds 秒、倍率 ratePercent% 的产出加到小数进度上，返回凑满的整件（按 reward 顺序）与新的小数进度。
    /// 按配置字面值计算（55555 × 3600 / 1e8 = 1.99998），不足 1 件的部分留到下一次。
    /// </summary>
    internal static (List<OutpostItem> Whole, IReadOnlyList<OutpostProgress> Progress) Accrue(
        IReadOnlyList<OutpostProgress>? progress, ConfigOutpostLevel cfg, double seconds, int ratePercent)
    {
        var carry = new Dictionary<(int Type, int ConfigId), double>();
        var order = new List<(int Type, int ConfigId)>();
        foreach (OutpostProgress item in progress ?? [])
            if (carry.TryAdd((item.Type, item.ConfigId), item.Amount)) order.Add((item.Type, item.ConfigId));
        var whole = new List<OutpostItem>();
        foreach (List<long> entry in cfg.Reward ?? [])
        {
            if (entry.Count < 3 || entry[2] <= 0) continue;
            (int Type, int ConfigId) key = (checked((int)entry[0]), checked((int)entry[1]));
            double amount = carry.GetValueOrDefault(key) + entry[2] * (ratePercent / 100.0) * seconds / RateScale;
            double count = Math.Floor(amount + Eps);
            if (!carry.ContainsKey(key)) order.Add(key);
            carry[key] = Math.Max(0, amount - count);
            if (count >= 1) whole.Add(new OutpostItem(key.Type, key.ConfigId, (int)Math.Min(count, int.MaxValue)));
        }
        return (whole, order.Where(key => carry[key] > 0).Select(key => new OutpostProgress(key.Type, key.ConfigId, carry[key])).ToList());
    }

    /// <summary>按 (Type, ConfigId) 合并到 ItemInfo，并按 (Type, ConfigId) 排序（与 reward 的配置顺序相同），
    /// 产出顺序不随结算的分段方式变化。</summary>
    internal static IReadOnlyList<OutpostItem> Merge(IReadOnlyList<OutpostItem>? items, IEnumerable<OutpostItem> add)
    {
        List<OutpostItem> list = (items ?? []).ToList();
        foreach (OutpostItem item in add)
        {
            int index = list.FindIndex(x => x.Type == item.Type && x.ConfigId == item.ConfigId);
            if (index >= 0)
                list[index] = list[index] with { Num = (int)Math.Min((long)list[index].Num + item.Num, int.MaxValue) };
            else
                list.Add(item);
        }
        return list.OrderBy(item => item.Type).ThenBy(item => item.ConfigId).ToList();
    }

    // ───────────────────────── 协议 ─────────────────────────

    /// <summary>
    /// outpost.SetHero（调用前已结算到 now）：整表替换某前哨的驻守舰娘。浴场中的舰娘与超出驻守人数时拒绝
    /// （客户端 CheckBuildingConditionCanSelect 对浴场舰娘只引导出浴，不发请求）；在基建或其它前哨里的舰娘
    /// （客户端确认 4600018 / 4600020 后照常发送）从原处撤下。
    /// </summary>
    internal static Outcome SetHero(PlayerAccount account, int buildingId, IReadOnlyList<uint> heroIds, long now, SettlementRules rules)
    {
        PlayerOutpost state = State(account);
        int index = state.Buildings.ToList().FindIndex(b => b.Id == buildingId);
        if (index < 0) return Fail(account, $"Unknown outpost {buildingId}");
        PlayerOutpostBuilding target = state.Buildings[index];
        uint[] ids = heroIds.Distinct().ToArray();
        var owned = account.Dock.Heroes.Select(hero => hero.HeroId).ToHashSet();
        if (ids.Any(id => id == 0 || !owned.Contains(id)))
            return Fail(account, "The assignment contains a hero not owned by this profile");
        if (account.Bath?.HeroList.Any(bath => ids.Contains(bath.HeroId)) == true)
            return Fail(account, "A hero in the bathroom cannot be stationed at an outpost");
        if (rules.OutpostLevel(target.Id, target.Level) is { } cfg && ids.Length > cfg.ShipNum)
            return Fail(account, $"Outpost {buildingId} level {target.Level} holds {cfg.ShipNum} heroes");

        var moving = ids.ToHashSet();
        PlayerAccount after = TimeSettlement.RemoveFromBuildings(account, moving, now, rules, out bool buildingChanged);
        Dictionary<uint, Hero> heroes = BuildingProduction.HeroMap(after);
        GameServices.TryGetCurrency(after, TimeSettlement.BathCurrencyType, out int coins);
        var buildings = state.Buildings.Select(building =>
        {
            IReadOnlyList<uint> members = building.Id == buildingId
                ? ids
                : (building.HeroIds ?? []).Where(id => !moving.Contains(id)).ToArray();
            if (building.Id != buildingId && members.Count == (building.HeroIds?.Count ?? 0)) return building;
            return Occupy(building, members, heroes, coins, now, rules);
        }).ToList();
        return new Outcome(after with { Outpost = state with { Buildings = buildings } }, [], BuildingChanged: buildingChanged);
    }

    /// <summary>成员变化后：有人时锚点从 now 起算（「扫荡跳过时间」下为 0），重算状态；没人时锚点清零（编码为空闲）。</summary>
    private static PlayerOutpostBuilding Occupy(
        PlayerOutpostBuilding building, IReadOnlyList<uint> members, IReadOnlyDictionary<uint, Hero> heroes, int coins,
        long now, SettlementRules rules)
    {
        if (members.Count == 0) return building with { HeroIds = [], UpdateTime = 0 };
        return building with
        {
            HeroIds = members.ToArray(),
            UpdateTime = rules.OutpostInstant ? 0 : Math.Max(building.UpdateTime, now),
            State = IsWorking(building.Id, building.UseCoin, members, heroes, coins, now, rules) ? Working : Stopped,
        };
    }

    /// <summary>
    /// outpost.UpgradeBuilding：扣当前等级那一行的 item_cost（<see cref="UpgradeCosts.OutpostUpgrade"/>，全部够才扣）后
    /// 等级 +1，最高 6（与客户端 UpGradeOutpost 一致）。不够则拒绝，并标记货币与背包变化让调用方重推两者纠正客户端缓存。
    /// 协议总是扣（<see cref="OutpostService"/> 传 <paramref name="chargeCost"/> = true）；参数只留给不关心消耗的纯函数测试。
    /// </summary>
    internal static Outcome Upgrade(PlayerAccount account, int buildingId, bool chargeCost = false)
    {
        PlayerOutpost state = State(account);
        int index = state.Buildings.ToList().FindIndex(b => b.Id == buildingId);
        if (index < 0) return new Outcome(account, []);
        List<PlayerOutpostBuilding> buildings = state.Buildings.ToList();
        PaymentResult paid = new(true, account, false, false);
        if (chargeCost && buildings[index].Level < MaxLevel)
        {
            paid = CostLogic.TryPay(account,
                UpgradeCosts.OutpostUpgrade(OutpostLevelLoader.Get(buildingId, buildings[index].Level)));
            if (!paid.Ok)
                return new Outcome(account, [], CurrencyChanged: true, BagChanged: true, Err: 1,
                    ErrMsg: $"Not enough resources to upgrade outpost {buildingId}: {paid.Shortfall}");
        }
        buildings[index] = buildings[index] with { Level = Math.Min(buildings[index].Level + 1, MaxLevel) };
        return new Outcome(paid.Account with { Outpost = state with { Buildings = buildings } }, [],
            paid.CurrencyChanged, paid.BagChanged);
    }

    /// <summary>outpost.SetUseCoin：心情归零时是否自动消耗温泉币；有人驻守时按新设置重算状态。</summary>
    internal static Outcome SetUseCoin(PlayerAccount account, int buildingId, int useCoin, long now, SettlementRules rules)
    {
        PlayerOutpost state = State(account);
        int index = state.Buildings.ToList().FindIndex(b => b.Id == buildingId);
        if (index < 0) return new Outcome(account, []);
        List<PlayerOutpostBuilding> buildings = state.Buildings.ToList();
        PlayerOutpostBuilding building = buildings[index] with { UseCoin = useCoin };
        if (building.HeroIds is { Count: > 0 } members)
        {
            GameServices.TryGetCurrency(account, TimeSettlement.BathCurrencyType, out int coins);
            bool working = IsWorking(building.Id, useCoin, members, BuildingProduction.HeroMap(account), coins, now, rules);
            building = building with { State = working ? Working : Stopped };
        }
        buildings[index] = building;
        return new Outcome(account with { Outpost = state with { Buildings = buildings } }, []);
    }

    /// <summary>
    /// outpost.SpeedUpProduction（调用前已结算到 now）。默认规则：当日次数未满、前哨工作中时，
    /// 立即获得 speedup_reward 的产出并直接进背包（客户端随后弹 GetRewardsPage）；<paramref name="chargeCost"/>
    /// （「真实消耗资源」）时还要扣 speedup_cost，不够则拒绝。
    /// 「扫荡跳过时间」：不限次数、不消耗，把一份 reward 原值加进 ItemInfo（离线版原来的规则）。
    /// </summary>
    internal static Outcome SpeedUp(PlayerAccount account, int buildingId, long now, SettlementRules rules, bool chargeCost = false)
    {
        PlayerOutpost state = State(account);
        int index = state.Buildings.ToList().FindIndex(b => b.Id == buildingId);
        List<PlayerOutpostBuilding> buildings = state.Buildings.ToList();
        if (rules.OutpostInstant)
        {
            if (index < 0) return new Outcome(account, []);
            ConfigOutpostLevel? raw = rules.OutpostLevel(buildingId, buildings[index].Level);
            List<OutpostItem> rewards = (raw?.Reward ?? [])
                .Where(entry => entry.Count >= 3)
                .Select(entry => new OutpostItem(checked((int)entry[0]), checked((int)entry[1]), checked((int)entry[2])))
                .ToList();
            buildings[index] = buildings[index] with { ItemInfo = Merge(buildings[index].ItemInfo, rewards) };
            return new Outcome(account with { Outpost = state with { Buildings = buildings } }, rewards);
        }

        if (index < 0) return Fail(account, $"Unknown outpost {buildingId}");
        PlayerOutpostBuilding building = buildings[index];
        if (rules.OutpostLevel(building.Id, building.Level) is not { } cfg)
            return Fail(account, $"Outpost {buildingId} level {building.Level} has no config");
        int used = VowLogic.Day(now) > state.SpeedUpDay ? 0 : state.SpeedUpTime;
        if (used >= rules.OutpostSpeedUpLimit)
            return Fail(account, $"Outpost speed-ups used up today ({used}/{rules.OutpostSpeedUpLimit})");
        if (building.Level < 1 || building.HeroIds is not { Count: > 0 } || building.State != Working)
            return Fail(account, $"Outpost {buildingId} is not working");
        PaymentResult paid = chargeCost && cfg.SpeedupCost is { Count: >= 3 } cost
            ? CostLogic.TryPay(account, [new CostItem(checked((int)cost[0]), checked((int)cost[1]), cost[2])])
            : new PaymentResult(true, account, false, false);
        if (!paid.Ok) return Fail(account, $"Not enough resources for an outpost speed-up: {paid.Shortfall}");

        int rate = cfg.SpeedupReward is { Count: >= 2 } reward ? checked((int)reward[0]) : BaseRatePercent;
        double hours = cfg.SpeedupReward is { Count: >= 2 } duration ? duration[1] : 0;
        (List<OutpostItem> whole, IReadOnlyList<OutpostProgress> progress) = Accrue(building.Progress, cfg, hours * 3600, rate);
        (PlayerAccount granted, bool currency, bool bag) = Grant(paid.Account, whole);
        buildings[index] = building with { Progress = progress };
        return new Outcome(
            granted with { Outpost = state with { Buildings = buildings, SpeedUpTime = used + 1, SpeedUpDay = VowLogic.Day(now) } },
            whole, currency || paid.CurrencyChanged, bag || paid.BagChanged);
    }

    /// <summary>outpost.ReceiveItem / ReceiveAll（buildingId 为 null）：把 ItemInfo 发进背包与货币并清空。</summary>
    internal static Outcome Receive(PlayerAccount account, int? buildingId)
    {
        PlayerOutpost state = State(account);
        if (buildingId is { } id && !state.Buildings.Any(b => b.Id == id)) return new Outcome(account, []);
        IReadOnlyList<OutpostItem> rewards = [];
        var buildings = new List<PlayerOutpostBuilding>(state.Buildings.Count);
        foreach (PlayerOutpostBuilding building in state.Buildings)
        {
            if (buildingId is { } only && building.Id != only)
            {
                buildings.Add(building);
                continue;
            }
            rewards = Merge(rewards, (building.ItemInfo ?? []).Where(item => item.Num > 0));
            buildings.Add(building with { ItemInfo = [] });
        }
        (PlayerAccount granted, bool currency, bool bag) = Grant(account, rewards);
        return new Outcome(granted with { Outpost = state with { Buildings = buildings } }, rewards, currency, bag);
    }

    /// <summary>按 TCommonReward 发放：Type=5 走货币，其余走背包。</summary>
    private static (PlayerAccount Account, bool Currency, bool Bag) Grant(PlayerAccount account, IReadOnlyList<OutpostItem> rewards)
    {
        bool currency = false, bag = false;
        foreach (OutpostItem item in rewards)
        {
            if (item.Num == 0) continue;
            if (item.Type == GameServices.GoodsTypeCurrency)
            {
                account = GameServices.AddCurrency(account, item.ConfigId, item.Num);
                currency = true;
            }
            else
            {
                account = GameServices.AddBagItem(account, item.ConfigId, item.Num);
                bag = true;
            }
        }
        return (account, currency, bag);
    }
}
