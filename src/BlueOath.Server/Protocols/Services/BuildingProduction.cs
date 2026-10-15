using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 基建生产的纯函数（不读时钟、不做 IO）：资源楼（type 3 石油精製工場→补给，type 4 真夜中居酒屋→资金）
/// 按时间产出，道具工厂（type 7）按配方队列产出，以及领取、下单、合成、加速、降级领取。
/// <para>
/// 产出公式复刻客户端 BuildingLogic:Produce / ProduceItem：基础产速 + 驻守舰娘在加成窗口内的额外产速
/// + 办公室驻守舰娘对资金楼与道具工厂的加成。TimeSettlement.Settle 在心情结算之前，用旧锚点与上次下发的
/// 加成窗口烘焙产出，然后把产出楼与有人驻守的楼的锚点统一推进到同一个 now（客户端计算办公室加成时以
/// office.LastUpdateTime 为区间起点，所以两者必须对齐）。服务端用精确秒数并结转小数，不复刻客户端的
/// 600 秒显示阈值；客户端显示按推送阶梯式更新。
/// </para>
/// <para>
/// 工人体力（货币 21，万分制）由 TimeSettlement.Settle 按客户端 GetCurStrengthReal 回复；配方原料 [5,21,n]、
/// 体力加速与建造/升级（BuildingService）扣减，不足时返回错误。体力加速还按实际用掉的加速秒数扣本楼驻守舰娘的心情。
/// 启动器作弊选项（SettlementRules.Omit*）分别让生产即时完成、体力不消耗、心情不消耗。
/// </para>
/// </summary>
internal static class BuildingProduction
{
    internal const int Idle = 1;
    internal const int Adding = 2;
    internal const int Working = 3;
    internal const int Upgrading = 4;

    /// <summary>客户端 BuildingTimeUnit。</summary>
    internal const int Unit = 600;

    internal const int SupplyId = 5;
    internal const int GoldId = 1;
    internal const int StrengthId = 21;
    internal const int CurrentVersion = PlayerAccountFactory.CurrentProductionVersion;

    /// <summary>工人体力的存储倍率（客户端 BuildingBase.Int）：存储值 = 显示值 × 10000。</summary>
    internal const int StrengthScale = 10_000;

    /// <summary>floor 容差：远小于任何配方或资源 1 秒的产量。</summary>
    private const double Eps = 1e-9;

    internal sealed record OfficeSnapshot(PlayerBuildingEntry Entry, ConfigBuildinginfo Cfg, IReadOnlySet<uint> Members);

    /// <summary>领取/下单/合成/加速/降级的结果。Rewards 写进 TReceiveRet.ItemInfo。</summary>
    internal sealed record Outcome(
        PlayerAccount Account,
        IReadOnlyList<CommonReward> Rewards,
        bool CurrencyChanged,
        bool BagChanged,
        int Err = 0,
        string ErrMsg = "",
        IReadOnlySet<uint>? ChangedHeroIds = null)
    {
        internal bool Success => Err == 0;

        /// <summary>本次操作改了心情的舰娘（体力加速扣心情），需要随建筑快照一起推送。</summary>
        internal IReadOnlySet<uint> HeroesChanged => ChangedHeroIds ?? EmptyHeroes;
    }

    private static readonly IReadOnlySet<uint> EmptyHeroes = new HashSet<uint>();

    internal enum ReceiveKind { Building, Item, Resource, All }

    internal static bool IsResource(ConfigBuildinginfo? cfg) =>
        cfg is { Type: 3 or 4, Productid: { Count: >= 2 } product } && (product[1] == SupplyId || product[1] == GoldId);

    internal static bool IsFactory(ConfigBuildinginfo? cfg) => cfg?.Type == 7;

    /// <summary>客户端 GetEffectDuration：窗口与区间 [start, end] 的重叠秒数（可为负）。</summary>
    private static long Overlap(HeroEffectWindow window, long start, long end) =>
        Math.Min(window.End, end) - Math.Max(window.Start, start);

    /// <summary>客户端 GetSingleHeroAddition / GetSingleRecipeAddition 的运算顺序。</summary>
    private static double Single(ConfigBuildinginfo cfg, int addition) =>
        (cfg.Heroaddition * 1e-4 + 1) * (1 + addition * 1e-4) - 1 + 1;

    private static int PerTick(Hero hero, SettlementRules rules) =>
        rules.NaturalAdd + (hero.MarryTime != 0 ? rules.MarryAdd : 0);

    // ───────────────────────── 产出烘焙 ─────────────────────────

    /// <summary>
    /// 把一栋楼的产出烘焙到 now（last 为旧锚点，窗口为上次下发的版本）。不改 LastUpdateTime，由调用方统一推进。
    /// 资源楼只在 Working 时产出；道具工厂只在 Working 且有配方与剩余件数时产出。
    /// </summary>
    internal static PlayerBuildingEntry Bake(
        PlayerBuildingEntry building, ConfigBuildinginfo cfg, IReadOnlySet<uint> members, long last, long now,
        OfficeSnapshot? office, IReadOnlyDictionary<uint, Hero> heroes, SettlementRules rules, out bool produced)
    {
        produced = false;
        if (rules.OmitProductionTime)
        {
            // 「生产」作弊：资源楼直接满仓，道具工厂队列直接完工。
            if (IsResource(cfg) && building.Status is Working or Idle)
            {
                produced = true;
                return building with { ProductCount = checked((int)cfg.Productmax), Progress = 0, Status = Idle };
            }
            if (IsFactory(cfg) && building.Status == Working && building.ItemCount > 0)
            {
                produced = true;
                return AdvanceItems(building, building.Progress + building.ItemCount);
            }
            return building;
        }
        if (building.Status != Working) return building;
        if (IsResource(cfg))
        {
            produced = true;
            return now <= last ? building : BakeResource(building, cfg, members, last, now, office, heroes, rules);
        }
        if (IsFactory(cfg) && building.RecipeId > 0 && building.ItemCount > 0 &&
            rules.Recipe(building.RecipeId) is { Time: > 0 } recipe)
        {
            produced = true;
            return now <= last ? building : BakeFactory(building, cfg, recipe, members, last, now, office, heroes, rules);
        }
        return building;
    }

    private static PlayerBuildingEntry BakeResource(
        PlayerBuildingEntry building, ConfigBuildinginfo cfg, IReadOnlySet<uint> members, long last, long now,
        OfficeSnapshot? office, IReadOnlyDictionary<uint, Hero> heroes, SettlementRules rules)
    {
        bool gold = cfg.Productid![1] == GoldId;
        double ratio = (double)Unit / (gold ? rules.GoldUnit : rules.OilUnit);
        double baseSpeed = cfg.Productivity * 1e-4 * ratio;
        long delta = now - last;
        double total = (double)delta / Unit * baseSpeed;
        foreach (HeroEffectWindow window in building.HeroWindows ?? [])
        {
            if (!members.Contains(window.HeroId) || !heroes.TryGetValue(window.HeroId, out Hero? hero)) continue;
            long duration = Overlap(window, last, now);
            if (duration > 0)
                total += (double)duration / Unit *
                         (baseSpeed * (Single(cfg, rules.HeroAddition(hero.TemplateId, checked((int)cfg.Type))) - 1));
        }
        if (gold && office is not null)
        {
            foreach (HeroEffectWindow window in office.Entry.HeroWindows ?? [])
            {
                if (!office.Members.Contains(window.HeroId) || !heroes.TryGetValue(window.HeroId, out Hero? hero)) continue;
                long duration = Overlap(window, last, now);
                if (duration > 0)
                    total += (double)duration / Unit *
                             (baseSpeed * (Single(office.Cfg, rules.HeroAddition(hero.TemplateId, TimeSettlement.OfficeType)) - 1));
            }
        }
        double carried = building.Progress + total;
        long whole = (long)Math.Floor(carried + Eps);
        long count = building.ProductCount + whole;
        if (count >= cfg.Productmax)
            return building with { ProductCount = checked((int)cfg.Productmax), Progress = 0, Status = Idle };
        return building with { ProductCount = checked((int)count), Progress = Math.Max(0, carried - whole) };
    }

    private static PlayerBuildingEntry BakeFactory(
        PlayerBuildingEntry building, ConfigBuildinginfo cfg, ConfigRecipe recipe, IReadOnlySet<uint> members,
        long last, long now, OfficeSnapshot? office, IReadOnlyDictionary<uint, Hero> heroes, SettlementRules rules)
    {
        double time = recipe.Time;
        long delta = now - last;
        double total = building.Progress + delta / time;
        foreach (HeroEffectWindow window in building.HeroWindows ?? [])
        {
            if (!members.Contains(window.HeroId) || !heroes.TryGetValue(window.HeroId, out Hero? hero)) continue;
            long duration = Overlap(window, last, now);
            if (duration > 0)
                total += (Single(cfg, rules.RecipeAddition(hero.TemplateId, checked((int)recipe.Type))) - 1) * duration / time;
        }
        if (office is not null)
        {
            foreach (HeroEffectWindow window in office.Entry.HeroWindows ?? [])
            {
                if (!office.Members.Contains(window.HeroId) || !heroes.TryGetValue(window.HeroId, out Hero? hero)) continue;
                long duration = Overlap(window, last, now);
                if (duration > 0)
                    total += (Single(office.Cfg, rules.HeroAddition(hero.TemplateId, TimeSettlement.OfficeType)) - 1) * duration / time;
            }
        }
        return AdvanceItems(building, total);
    }

    /// <summary>整件结转：满剩余件数即完工（Idle），否则保留当前件进度。</summary>
    private static PlayerBuildingEntry AdvanceItems(PlayerBuildingEntry building, double total)
    {
        long done = (long)Math.Floor(total + Eps);
        if (done >= building.ItemCount)
            return building with
            {
                ProductCount = building.ProductCount + building.ItemCount,
                ItemCount = 0,
                Progress = 0,
                Status = Idle,
            };
        return building with
        {
            ProductCount = building.ProductCount + checked((int)done),
            ItemCount = building.ItemCount - checked((int)done),
            Progress = Math.Max(0, total - done),
        };
    }

    // ───────────────────────── 加成窗口与效率 ─────────────────────────

    /// <summary>舰娘在 t 时刻（只计自然恢复）的心情，与 Settle 的批量语义一致。</summary>
    internal static int EffectiveMood(Hero hero, long t, SettlementRules rules) =>
        TimeSettlement.ClampMood(TimeSettlement.ApplyNatural(
            hero.Mood, TimeSettlement.NaturalTicks(hero.UpdateTime, t, rules.TickMinutes).Ticks,
            PerTick(hero, rules), rules.NaturalLimit), rules);

    /// <summary>
    /// 心情归零时刻：最早的 t，使得在 t 做一次结算时该舰娘心情恰好归零。心情已为 0 时返回 null（不下发窗口）。
    /// </summary>
    internal static long? MoodZeroTime(Hero hero, long start, double costPerSecond, SettlementRules rules)
    {
        int mood = EffectiveMood(hero, start, rules);
        if (mood <= 0) return null;
        if (costPerSecond <= 0) return int.MaxValue;
        long t = start + (long)Math.Ceiling(mood / costPerSecond);
        for (int i = 0; i < 64 && t < int.MaxValue; i++)
        {
            long next = start + (long)Math.Ceiling(EffectiveMood(hero, t, rules) / costPerSecond);
            if (next <= t) break;
            t = next;
        }
        return Math.Min(t, int.MaxValue);
    }

    /// <summary>
    /// 「心情」作弊开关在两次结算之间（服务端重启）被切换时，存档里的加成窗口是按旧规则算的：开着作弊时终点为
    /// int.MaxValue，关着时为心情归零时刻。烘焙停机这段时间的产出与体力之前，先按当前规则改正窗口终点，
    /// 让加成与这段时间的心情扣减口径一致。规则没变时窗口原样返回（同一实例）。
    /// </summary>
    internal static PlayerBuildingEntry NormalizeWindows(
        PlayerBuildingEntry building, ConfigBuildinginfo cfg, IReadOnlyDictionary<uint, Hero> heroes, double discount,
        SettlementRules rules)
    {
        if (building.HeroWindows is not { Count: > 0 } windows || cfg.Type == TimeSettlement.DormType) return building;
        double cost = rules.OmitMoodCost ? 0 : cfg.Moodcost * discount / rules.CostUnit;
        bool changed = false;
        var normalized = new List<HeroEffectWindow>(windows.Count);
        foreach (HeroEffectWindow window in windows)
        {
            bool endless = window.End >= int.MaxValue;
            bool expectEndless = cost <= 0;
            if (endless != expectEndless && heroes.TryGetValue(window.HeroId, out Hero? hero) &&
                MoodZeroTime(hero, window.Start, cost, rules) is long end)
            {
                normalized.Add(window with { End = end });
                changed = true;
            }
            else
            {
                normalized.Add(window);
            }
        }
        return changed ? building with { HeroWindows = normalized } : building;
    }

    /// <summary>非宿舍楼的加成窗口 [start, 心情归零时刻]；宿舍不下发（否则误触发「機嫌」通知）。</summary>
    internal static IReadOnlyList<HeroEffectWindow> Windows(
        IEnumerable<Hero> members, ConfigBuildinginfo cfg, long start, double discount, SettlementRules rules)
    {
        if (cfg.Type == TimeSettlement.DormType) return [];
        // 「心情」作弊下工作不消耗心情，窗口一直持续（终点 int.MaxValue）。
        double cost = rules.OmitMoodCost ? 0 : cfg.Moodcost * discount / rules.CostUnit;
        var windows = new List<HeroEffectWindow>();
        foreach (Hero hero in members)
            if (MoodZeroTime(hero, start, cost, rules) is long end)
                windows.Add(new HeroEffectWindow(hero.HeroId, start, end));
        return windows;
    }

    /// <summary>下发的效率（×1e4）= round(1e4 × GetTotalHeroAddition)，只统计心情大于 0 的舰娘。</summary>
    internal static int Productivity(ConfigBuildinginfo cfg, IEnumerable<Hero> members, long now, SettlementRules rules)
    {
        double total = 0;
        foreach (Hero hero in members)
        {
            if (EffectiveMood(hero, now, rules) <= 0) continue;
            int addition = rules.HeroAddition(hero.TemplateId, checked((int)cfg.Type));
            total = total + (cfg.Heroaddition + 10000.0) * (10000.0 + addition) / 10000.0 - 10000.0;
        }
        return checked((int)Math.Round(total + 10000.0));
    }

    /// <summary>按当前成员与心情重算加成窗口与效率（窗口起点为 start，应等于本楼新的 LastUpdateTime）。</summary>
    internal static PlayerBuildingEntry WithDerived(
        PlayerBuildingEntry building, ConfigBuildinginfo? cfg, IReadOnlyList<Hero> members, long start, double discount,
        SettlementRules rules) =>
        cfg is null || members.Count == 0
            ? building with { HeroWindows = [], Productivity = null }
            : building with
            {
                HeroWindows = Windows(members, cfg, start, discount, rules),
                Productivity = Productivity(cfg, members, start, rules),
            };

    /// <summary>全部建筑重算加成窗口与效率（居酒屋等级变化会改变所有工作楼的心情消耗速度）。前提：已结算到 now。</summary>
    internal static PlayerBuilding RefreshDerived(
        PlayerBuilding state, IReadOnlyDictionary<uint, Hero> heroes, long now, SettlementRules rules)
    {
        double discount = TimeSettlement.TavernDiscount(state, rules);
        return state with
        {
            Buildings = state.Buildings
                .Select(building => WithDerived(
                    building, rules.BuildingInfo(building.Tid),
                    building.HeroIds.Where(heroes.ContainsKey).Select(id => heroes[id]).ToList(),
                    Math.Max(building.LastUpdateTime, now), discount, rules))
                .ToArray(),
        };
    }

    /// <summary>
    /// 旧档迁移，由 TimeSettlement.Settle 调用、经 SettleLockedAsync 落盘，不可回退（部署新版服务端前先备份 profiles.db）。
    /// <list type="bullet">
    /// <item>0 → 1：资源楼一律写成 Idle 的旧档改为 Working（未满仓时），保留存档里的 LastUpdateTime，
    /// 同一次结算按旧锚点追溯产出并封顶 productmax。</item>
    /// <item>1 → 2：此前工人体力总是按上限下发、从不扣减，迁移时补到办公室等级对应的上限，之后按时间回复与消耗。</item>
    /// </list>
    /// </summary>
    internal static PlayerBuilding Migrate(PlayerBuilding state, SettlementRules rules)
    {
        PlayerBuilding migrated = state;
        if (state.ProductionVersion < 1)
        {
            migrated = migrated with
            {
                Buildings = migrated.Buildings
                    .Select(building => rules.BuildingInfo(building.Tid) is { } cfg && IsResource(cfg) &&
                                        building.Status == Idle && building.ProductCount < cfg.Productmax
                        ? building with { Status = Working, Progress = 0 }
                        : building)
                    .ToArray(),
            };
        }
        if (state.ProductionVersion < 2)
        {
            long max = MaxStrengthRaw(migrated, rules);
            if (migrated.WorkerStrength < max)
                migrated = migrated with { WorkerStrength = checked((int)max), WorkerStrengthCarry = 0 };
        }
        return migrated with { ProductionVersion = CurrentVersion };
    }

    // ───────────────────────── 工人体力 ─────────────────────────

    /// <summary>办公室等级（决定体力上限）；没有办公室时按 1 级。</summary>
    internal static int OfficeLevel(PlayerBuilding state, SettlementRules rules) =>
        state.Buildings.FirstOrDefault(building => rules.BuildingInfo(building.Tid)?.Type == TimeSettlement.OfficeType)?.Level ?? 1;

    /// <summary>体力上限（存储值）= GetMaxWorkerByLv(办公室等级) × 10000。</summary>
    internal static long MaxStrengthRaw(PlayerBuilding state, SettlementRules rules) =>
        (long)rules.MaxWorkerStrength(OfficeLevel(state, rules)) * StrengthScale;

    /// <summary>体力是否在回复：未满且有回复来源（基础回复或任一电力室；与客户端一样不看状态与驻守）。</summary>
    internal static bool StrengthRecovering(PlayerBuilding state, SettlementRules rules) =>
        state.WorkerStrength < MaxStrengthRaw(state, rules) &&
        (rules.WorkerBaseRecover > 0 ||
         state.Buildings.Any(building => rules.BuildingInfo(building.Tid)?.Type == TimeSettlement.ElectricFactoryType));

    /// <summary>
    /// 逐位复刻客户端 GetCurStrengthReal：从 w 回复到 now。电力室加成窗口用 state 里保存的（即上次下发给客户端的）
    /// HeroWindows，成员为 membersAt[i]（与 state.Buildings 对齐）。已达上限时原样返回；向下取整后余下的小数结转。
    /// </summary>
    internal static (int Strength, double Carry) BakeStrength(
        PlayerBuilding state, IReadOnlyList<uint[]> membersAt, long w, long now,
        IReadOnlyDictionary<uint, Hero> heroes, SettlementRules rules)
    {
        long max = MaxStrengthRaw(state, rules);
        if (state.WorkerStrength >= max || now <= w) return (state.WorkerStrength, state.WorkerStrengthCarry);
        long delta = now - w;
        double ratio = (double)Unit / rules.WorkerRecoverUnit;
        double total = state.WorkerStrengthCarry + (double)delta / Unit * (rules.WorkerBaseRecover * ratio);
        for (int index = 0; index < state.Buildings.Count; index++)
        {
            PlayerBuildingEntry building = state.Buildings[index];
            ConfigBuildinginfo? cfg = rules.BuildingInfo(building.Tid);
            if (cfg?.Type != TimeSettlement.ElectricFactoryType) continue;
            double speed = cfg.Addworkerhp;
            total += (double)delta / Unit * speed;
            foreach (HeroEffectWindow window in building.HeroWindows ?? [])
            {
                if (!membersAt[index].Contains(window.HeroId) || !heroes.TryGetValue(window.HeroId, out Hero? hero)) continue;
                long duration = Overlap(window, w, now);
                if (duration > 0)
                    total += (double)duration / Unit *
                             (speed * (Single(cfg, rules.HeroAddition(hero.TemplateId, TimeSettlement.ElectricFactoryType)) - 1));
            }
        }
        long points = (long)Math.Floor(total + Eps);
        long strength = state.WorkerStrength + points;
        return strength >= max ? (checked((int)max), 0) : (checked((int)strength), Math.Max(0, total - points));
    }

    /// <summary>
    /// 把体力回复结算到 anchor 并把 WorkerUpdateTime 推进到 anchor。改办公室等级（上限）或电力室（回复速度）、扣体力之前调用：
    /// 客户端总用当前建筑与当前锚点回溯整段 [W, now]。前提：本请求已 Settle 到 anchor（电力室有人时 W 已等于 anchor，
    /// 不会漏结算电力室心情）。
    /// </summary>
    internal static PlayerBuilding AdvanceWorker(
        PlayerBuilding state, long anchor, IReadOnlyDictionary<uint, Hero> heroes, SettlementRules rules)
    {
        long w = state.WorkerUpdateTime == 0 ? anchor : state.WorkerUpdateTime;
        if (anchor <= w) return WithCheatStrength(state with { WorkerUpdateTime = w }, rules);
        (int strength, double carry) = BakeStrength(
            state, state.Buildings.Select(building => building.HeroIds.ToArray()).ToList(), w, anchor, heroes, rules);
        return WithCheatStrength(
            state with { WorkerStrength = strength, WorkerStrengthCarry = carry, WorkerUpdateTime = anchor }, rules);
    }

    /// <summary>「体力」作弊：体力保持不低于当前上限（客户端在体力已满时不外推，界面始终显示满值）。</summary>
    internal static PlayerBuilding WithCheatStrength(PlayerBuilding state, SettlementRules rules)
    {
        if (!rules.OmitWorkerStrength) return state;
        long max = MaxStrengthRaw(state, rules);
        return state.WorkerStrength >= max ? state : state with { WorkerStrength = checked((int)max), WorkerStrengthCarry = 0 };
    }

    /// <summary>
    /// 扣工人体力 points（显示值）：先 AdvanceWorker 到 now，不足返回 false。「体力」作弊时不扣，只保持上限。
    /// </summary>
    internal static bool TryChargeStrength(
        PlayerAccount account, long points, long now, SettlementRules rules, out PlayerAccount charged)
    {
        PlayerBuilding state = AdvanceWorker(account.Building!, now, HeroMap(account), rules);
        charged = account with { Building = state };
        if (rules.OmitWorkerStrength || points <= 0) return true;
        long cost = points * StrengthScale;
        if (state.WorkerStrength < cost) return false;
        charged = account with { Building = state with { WorkerStrength = checked((int)(state.WorkerStrength - cost)) } };
        return true;
    }

    /// <summary>建成/升级/降级后资源楼与道具工厂应处的状态。</summary>
    internal static int StatusAfterLevelChange(PlayerBuildingEntry building, ConfigBuildinginfo target) =>
        IsResource(target)
            ? (building.ProductCount >= target.Productmax ? Idle : Working)
            : IsFactory(target)
                ? (building.ItemCount > 0 ? Working : Idle)
                : Idle;

    // ───────────────────────── 领取 ─────────────────────────

    /// <summary>
    /// 领取产出。资源楼按 productid 发放货币（燃油在补给已达上限时不领，单楼领取返回错误、一键领取跳过）；
    /// 满仓（Idle）的资源楼领取后恢复 Working 并以 now 为新锚点；道具工厂按配方发放已完成件数。
    /// 没有可领的东西时返回成功且无奖励（客户端各页面先判断 ItemInfo 非空）。
    /// </summary>
    internal static Outcome Receive(PlayerAccount account, ReceiveKind kind, int arg, long now, SettlementRules rules)
    {
        if (account.Building is not { } state) return Fail(account, "No buildings");
        var buildings = state.Buildings.ToList();
        var rewards = new List<CommonReward>();
        bool currency = false, bag = false;
        bool oilBlocked = account.Character.Supply >= rules.SupplyMax(account.Character.Level);
        if (kind == ReceiveKind.Resource && arg is not (SupplyId or GoldId)) return Fail(account, "Invalid resource id");
        if (kind == ReceiveKind.Resource && arg == SupplyId && oilBlocked) return Fail(account, "Supply is at its limit");

        int[] order;
        if (kind is ReceiveKind.Building or ReceiveKind.Item)
        {
            int index = buildings.FindIndex(building => building.Id == arg);
            if (index < 0) return Fail(account, $"Unknown building {arg}");
            order = [index];
        }
        else
        {
            order = Enumerable.Range(0, buildings.Count).OrderBy(i => buildings[i].Id).ToArray();
        }

        PlayerAccount after = account;
        foreach (int i in order)
        {
            PlayerBuildingEntry building = buildings[i];
            ConfigBuildinginfo? cfg = rules.BuildingInfo(building.Tid);
            if (IsResource(cfg) && kind != ReceiveKind.Item)
            {
                int currencyId = checked((int)cfg!.Productid![1]);
                if (kind == ReceiveKind.Resource && currencyId != arg) continue;
                if (currencyId == SupplyId && oilBlocked)
                {
                    if (kind == ReceiveKind.Building) return Fail(account, "Supply is at its limit");
                    continue;
                }
                if (building.ProductCount <= 0) continue;
                after = GameServices.AddCurrency(after, currencyId, building.ProductCount);
                currency = true;
                Merge(rewards, new CommonReward(GameServices.GoodsTypeCurrency, currencyId, building.ProductCount));
                bool wasFull = building.Status == Idle;
                buildings[i] = rules.OmitProductionTime
                    // 「生产」作弊：领完立即补满，快照里始终满仓。
                    ? building with
                    {
                        ProductCount = checked((int)cfg.Productmax),
                        Progress = 0,
                        Status = Idle,
                        LastUpdateTime = Math.Max(building.LastUpdateTime, now),
                    }
                    : building with
                    {
                        ProductCount = 0,
                        Status = wasFull ? Working : building.Status,
                        LastUpdateTime = wasFull ? Math.Max(building.LastUpdateTime, now) : building.LastUpdateTime,
                    };
            }
            else if (IsFactory(cfg) && kind != ReceiveKind.Resource)
            {
                if (building.ProductCount <= 0 || rules.Recipe(building.RecipeId) is not { Item.Count: >= 3 } recipe) continue;
                (after, bool c, bool b, bool granted) = Grant(after, recipe.Item, building.ProductCount, rewards);
                if (!granted) continue; // 发不出去的产物留在楼里，不能清零
                currency |= c;
                bag |= b;
                buildings[i] = building with { ProductCount = 0 };
            }
            else if (kind is ReceiveKind.Building or ReceiveKind.Item)
            {
                return Fail(account, "This building has nothing to receive");
            }
        }
        if (rewards.Count == 0) return new Outcome(account, [], false, false);
        return new Outcome(after with { Building = after.Building! with { Buildings = buildings } }, rewards, currency, bag);
    }

    // ───────────────────────── 下单（ProduceItem）─────────────────────────

    /// <summary>
    /// 道具工厂下单。Count 是剩余待产的绝对件数：0 表示取消同配方队列；换配方时先自动领取旧成品（作为奖励返回），
    /// 旧队列与进度作废、不退料；Count 截断到仓库剩余容量；只为新增件数扣原料（含工人体力原料 [5,21,n]）。
    /// 「生产」作弊时扣完原料直接发放本单产物（写进应答的 ItemInfo，客户端弹奖励），队列保持空闲。
    /// </summary>
    internal static Outcome Order(PlayerAccount account, int buildingId, int recipeId, int count, long now, SettlementRules rules)
    {
        PlayerBuildingEntry? building = account.Building?.Buildings.FirstOrDefault(item => item.Id == buildingId);
        if (building is null) return Fail(account, $"Unknown building {buildingId}");
        ConfigBuildinginfo? cfg = rules.BuildingInfo(building.Tid);
        if (!IsFactory(cfg) || building.Status is Adding or Upgrading) return Fail(account, "Not an item factory");
        ConfigRecipe? recipe = rules.Recipe(recipeId);
        if (recipe is not { Item.Count: >= 3, Time: > 0 } || recipe.Hide == 1 || recipe.Unlocklevel > building.Level ||
            count < 0 || (cfg!.Recipeid is { Count: > 0 } ids && !ids.Contains(recipeId)))
            return Fail(account, $"Recipe {recipeId} is unavailable");

        bool same = building.RecipeId == recipeId;
        int remaining = building.Status == Working ? building.ItemCount : 0;
        if (count == 0)
            return same && remaining > 0
                ? new Outcome(Replace(account, building with
                {
                    ItemCount = 0, Progress = 0, Status = Idle,
                    LastUpdateTime = Math.Max(building.LastUpdateTime, now),
                }), [], false, false)
                : Fail(account, "Nothing to cancel");

        var rewards = new List<CommonReward>();
        bool currency = false, bag = false;
        PlayerAccount after = account;
        if (building.RecipeId != 0 && !same)
        {
            if (building.ProductCount > 0 && rules.Recipe(building.RecipeId) is { Item.Count: >= 3 } old)
            {
                (after, currency, bag, bool granted) = Grant(after, old.Item, building.ProductCount, rewards);
                if (!granted) return Fail(account, "The finished products cannot be received");
            }
            building = building with { ProductCount = 0, ItemCount = 0, Progress = 0 };
            remaining = 0;
        }
        int capacity = checked((int)cfg.Productmax) - building.ProductCount;
        if (capacity <= 0) return Fail(account, "The warehouse is full");
        count = Math.Min(count, capacity);
        int delta = count - remaining;
        if (delta > 0)
        {
            Outcome paid = Pay(after, [recipe.Rawmaterial1, recipe.Rawmaterial2], delta, now, rules);
            if (!paid.Success) return Fail(account, paid.ErrMsg);
            after = paid.Account;
            currency |= paid.CurrencyChanged;
            bag |= paid.BagChanged;
        }
        if (rules.OmitProductionTime)
        {
            (after, bool c, bool b, bool granted) = Grant(after, recipe.Item, count, rewards);
            if (!granted) return Fail(account, "The products cannot be granted");
            building = building with
            {
                RecipeId = recipeId,
                ItemCount = 0,
                Progress = 0,
                Status = Idle,
                LastUpdateTime = Math.Max(building.LastUpdateTime, now),
            };
            return new Outcome(Replace(after, building), rewards, currency || c, bag || b);
        }
        bool keepProgress = same && building.Status == Working;
        building = building with
        {
            RecipeId = recipeId,
            ItemCount = count,
            Progress = keepProgress ? building.Progress : 0,
            Status = Working,
            LastUpdateTime = Math.Max(building.LastUpdateTime, now),
        };
        return new Outcome(Replace(after, building), rewards, currency, bag);
    }

    /// <summary>即时合成（ComposeItem，仅日服）：扣原料 × Count（含工人体力原料 [5,21,n]），发放产物 × Count。</summary>
    internal static Outcome Compose(
        PlayerAccount account, int buildingId, int composeId, int count, long now, SettlementRules rules)
    {
        PlayerBuildingEntry? building = account.Building?.Buildings.FirstOrDefault(item => item.Id == buildingId);
        ConfigBuildinginfo? cfg = building is null ? null : rules.BuildingInfo(building.Tid);
        ConfigRecipeCompose? recipe = rules.Compose(composeId);
        if (building is null || !IsFactory(cfg) || recipe is not { Item.Count: >= 3 } || recipe.Unlocklevel > building.Level ||
            count < 1 || (cfg!.RecipeCompose is { Count: > 0 } ids && !ids.Contains(composeId)))
            return Fail(account, "Compose recipe unavailable");
        Outcome paid = Pay(account, [recipe.Rawmaterial1, recipe.Rawmaterial2], count, now, rules);
        if (!paid.Success) return Fail(account, paid.ErrMsg);
        var rewards = new List<CommonReward>();
        (PlayerAccount after, bool c, bool b, bool granted) = Grant(paid.Account, recipe.Item, count, rewards);
        if (!granted) return Fail(account, "Compose product cannot be granted");
        return new Outcome(after, rewards, paid.CurrencyChanged || c, paid.BagChanged || b);
    }

    /// <summary>
    /// 工人体力加速（UseStrengthSpeedup）：扣 UseCount 点工人体力，每点相当于 recipe.time / (cost_energy × 1e-4) 秒的生产，
    /// 窗口内的驻守舰娘与办公室舰娘加成同样作用（客户端 ProduceNow）；满剩余件数即完工，多余部分作废。锚点不变。
    /// <para>
    /// 本楼驻守舰娘按实际用掉的加速秒数扣心情（与同样时长的工作相同：moodcost × 居酒屋折减 / 600 秒），
    /// 超出剩余所需时间的部分不计；不触发自然恢复，扣完按新心情重算本楼加成窗口与效率。
    /// 「体力」「心情」作弊分别跳过体力与心情的扣减。
    /// </para>
    /// </summary>
    internal static Outcome Speedup(PlayerAccount account, int buildingId, int useCount, long now, SettlementRules rules)
    {
        PlayerBuildingEntry? building = account.Building?.Buildings.FirstOrDefault(item => item.Id == buildingId);
        ConfigBuildinginfo? cfg = building is null ? null : rules.BuildingInfo(building.Tid);
        ConfigRecipe? recipe = building is null ? null : rules.Recipe(building.RecipeId);
        if (building is null || !IsFactory(cfg) || building.Status != Working || building.ItemCount <= 0 ||
            recipe is not { CostEnergy: > 0, Time: > 0 } || useCount <= 0)
            return Fail(account, "Speedup is unavailable");
        Dictionary<uint, Hero> heroes = HeroMap(account);
        double time = recipe.Time;
        double sub = time / (recipe.CostEnergy * 1e-4) * useCount;
        double extra = sub / time;
        double bonus = 0;
        foreach (HeroEffectWindow window in building.HeroWindows ?? [])
            if (building.HeroIds.Contains(window.HeroId) && now >= window.Start && now <= window.End &&
                heroes.TryGetValue(window.HeroId, out Hero? hero))
            {
                double single = Single(cfg!, rules.RecipeAddition(hero.TemplateId, checked((int)recipe.Type))) - 1;
                extra += single * sub / time;
                bonus += single;
            }
        PlayerBuildingEntry? office = account.Building!.Buildings
            .FirstOrDefault(item => rules.BuildingInfo(item.Tid)?.Type == TimeSettlement.OfficeType);
        if (office is not null && rules.BuildingInfo(office.Tid) is { } officeCfg)
            foreach (HeroEffectWindow window in office.HeroWindows ?? [])
                if (office.HeroIds.Contains(window.HeroId) && now >= window.Start && now <= window.End &&
                    heroes.TryGetValue(window.HeroId, out Hero? hero))
                {
                    double single = Single(officeCfg, rules.HeroAddition(hero.TemplateId, TimeSettlement.OfficeType)) - 1;
                    extra += single * sub / time;
                    bonus += single;
                }

        if (!TryChargeStrength(account, useCount, now, rules, out PlayerAccount after))
            return Fail(account, "Not enough worker strength");

        // 真正用于生产的加速秒数：以剩余件数按当前总速度所需的时间为上限（客户端 3200004 提示「浪费」的部分不计）。
        double used = Math.Min(sub, (building.ItemCount - building.Progress) * time / (1 + bonus));
        PlayerBuildingEntry advanced = AdvanceItems(building, building.Progress + extra);
        var changed = new HashSet<uint>();
        if (!rules.OmitMoodCost && used > 0 && cfg!.Moodcost > 0)
        {
            double discount = TimeSettlement.TavernDiscount(after.Building!, rules);
            double cost = used * cfg.Moodcost * discount / rules.CostUnit;
            List<Hero> dock = after.Dock.Heroes.ToList();
            for (int index = 0; index < dock.Count; index++)
            {
                Hero hero = dock[index];
                if (!building.HeroIds.Contains(hero.HeroId) || changed.Contains(hero.HeroId)) continue;
                int mood = TimeSettlement.ClampMood((long)Math.Floor(hero.Mood - cost), rules);
                if (mood == hero.Mood) continue;
                dock[index] = hero with { Mood = mood };
                changed.Add(hero.HeroId);
            }
            if (changed.Count > 0)
            {
                after = after with { Dock = after.Dock with { Heroes = dock } };
                Dictionary<uint, Hero> updated = HeroMap(after);
                advanced = WithDerived(
                    advanced, cfg, advanced.HeroIds.Where(updated.ContainsKey).Select(id => updated[id]).ToList(),
                    advanced.LastUpdateTime, discount, rules);
            }
        }
        return new Outcome(Replace(after, advanced), [], false, false, ChangedHeroIds: changed);
    }

    /// <summary>降级前自动领取该楼存量（燃油被上限挡住时保留）；配方不在降级后列表中时清除配方。</summary>
    internal static Outcome CollectForDegrade(
        PlayerAccount account, int buildingId, ConfigBuildinginfo target, long now, SettlementRules rules)
    {
        Outcome received = Receive(account, ReceiveKind.Building, buildingId, now, rules);
        PlayerAccount after = received.Success ? received.Account : account;
        PlayerBuildingEntry building = after.Building!.Buildings.First(item => item.Id == buildingId);
        if (IsFactory(rules.BuildingInfo(building.Tid)) && building.ProductCount == 0 &&
            target.Recipeid is { } ids && !ids.Contains(building.RecipeId))
            building = building with { RecipeId = 0 };
        // 「生产」作弊下资源楼领完会按当前等级补满，降级后按新等级的容量满仓，否则降级必然因超容被拒。
        if (rules.OmitProductionTime && IsResource(target) && building.ProductCount > target.Productmax)
            building = building with { ProductCount = checked((int)target.Productmax), Progress = 0 };
        return new Outcome(
            Replace(after, building),
            received.Success ? received.Rewards : [],
            received.Success && received.CurrencyChanged,
            received.Success && received.BagChanged);
    }

    // ───────────────────────── 辅助 ─────────────────────────

    /// <summary>
    /// 扣原料 × times：[5,21,n] 扣工人体力（「体力」作弊时不扣）；[5,id,n] 扣货币；其余扣背包。
    /// 日服配方的 rawmaterial3 全为空。任一项不足时返回错误且不改账号。
    /// </summary>
    private static Outcome Pay(
        PlayerAccount account, IReadOnlyList<IReadOnlyList<long>?> raws, int times, long now, SettlementRules rules)
    {
        PlayerAccount after = account;
        bool currency = false, bag = false;
        foreach (IReadOnlyList<long>? raw in raws)
        {
            if (raw is not { Count: >= 3 } || raw[0] <= 0 || raw[2] <= 0) continue;
            int type = checked((int)raw[0]);
            int id = checked((int)raw[1]);
            long need = raw[2] * times;
            if (type == GameServices.GoodsTypeCurrency && id == StrengthId)
            {
                if (!TryChargeStrength(after, need, now, rules, out after))
                    return Fail(account, "Not enough worker strength");
                continue;
            }
            if (type == GameServices.GoodsTypeCurrency)
            {
                if (!GameServices.TryGetCurrency(after, id, out int have) || have < need)
                    return Fail(account, $"Not enough currency {id}");
                after = GameServices.AddCurrency(after, id, checked((int)-need));
                currency = true;
            }
            else
            {
                int have = after.Bag?.Items.FirstOrDefault(item => item.TemplateId == id)?.Num ?? 0;
                if (have < need) return Fail(account, $"Not enough material {id}");
                after = GameServices.AddBagItem(after, id, checked((int)-need));
                bag = true;
            }
        }
        return new Outcome(after, [], currency, bag);
    }

    /// <summary>
    /// 发放产物 × times。未知货币 id 不发（AddCurrency 遇到未知 id 会落到资金分支），此时 Granted=false，
    /// 调用方不得清零对应的存量。
    /// </summary>
    private static (PlayerAccount Account, bool Currency, bool Bag, bool Granted) Grant(
        PlayerAccount account, IReadOnlyList<long> item, int times, List<CommonReward> rewards)
    {
        int type = checked((int)item[0]);
        int id = checked((int)item[1]);
        int num = checked((int)item[2] * times);
        if (num <= 0 || (type == GameServices.GoodsTypeCurrency && !GameServices.TryGetCurrency(account, id, out _)))
            return (account, false, false, false);
        Merge(rewards, new CommonReward(type, id, num));
        return type == GameServices.GoodsTypeCurrency
            ? (GameServices.AddCurrency(account, id, num), true, false, true)
            : (GameServices.AddBagItem(account, id, num), false, true, true);
    }

    private static void Merge(List<CommonReward> rewards, CommonReward reward)
    {
        int index = rewards.FindIndex(item => item.Type == reward.Type && item.ConfigId == reward.ConfigId);
        if (index >= 0) rewards[index] = rewards[index] with { Num = rewards[index].Num + reward.Num };
        else rewards.Add(reward);
    }

    internal static PlayerAccount Replace(PlayerAccount account, PlayerBuildingEntry entry) =>
        account with
        {
            Building = account.Building! with
            {
                Buildings = account.Building.Buildings.Select(item => item.Id == entry.Id ? entry : item).ToArray(),
            },
        };

    internal static Dictionary<uint, Hero> HeroMap(PlayerAccount account)
    {
        var heroes = new Dictionary<uint, Hero>();
        foreach (Hero hero in account.Dock.Heroes) heroes.TryAdd(hero.HeroId, hero);
        return heroes;
    }

    private static Outcome Fail(PlayerAccount account, string message) => new(account, [], false, false, 1, message);
}
