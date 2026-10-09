using BlueOath.Core;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 按经过时间结算所需的配置常量。默认值为日服 1.4.0 配置库实测值，
/// <see cref="FromConfig"/> 从 config_parameter / config_bathroom_item 读取并以默认值兜底。
/// </summary>
internal sealed record SettlementRules
{
    /// <summary>config_parameter[139] mood_addtime：自然恢复周期（分钟）。</summary>
    public int TickMinutes { get; init; } = 6;

    /// <summary>config_parameter[140] mood_normal_add：每个自然恢复周期增加的心情。</summary>
    public int NaturalAdd { get; init; } = 100;

    /// <summary>config_parameter[145] mood_marry_add：已誓约舰娘每周期额外增加的心情。</summary>
    public int MarryAdd { get; init; } = 100;

    /// <summary>config_parameter[144] mood_normal_limit：自然恢复只能涨到的上限（119）。</summary>
    public int NaturalLimit { get; init; } = 1_190_000;

    /// <summary>config_parameter[142] mood_bound 下限。</summary>
    public int MoodMin { get; init; } = 0;

    /// <summary>config_parameter[142] mood_bound 上限（150）。</summary>
    public int MoodMax { get; init; } = 1_500_000;

    /// <summary>config_parameter[206] addmoodtime：宿舍回复的计量周期（秒）。</summary>
    public int RecoverUnit { get; init; } = 600;

    /// <summary>config_parameter[207] moodcosttime：工作消耗的计量周期（秒）。</summary>
    public int CostUnit { get; init; } = 600;

    /// <summary>config_parameter[141] mood_bath_add：入浴期间每个自然恢复周期额外回复的心情。</summary>
    public int BathTickAdd { get; init; } = 40_000;

    /// <summary>config_parameter[212] ship_bath_mood_up：入浴时立即增加的心情（+30）。</summary>
    public int BathEnterAdd { get; init; } = 300_000;

    /// <summary>config_parameter[220] gift_add_mod：浴场送礼增加的心情（+60）。</summary>
    public int BathGiftAdd { get; init; } = 600_000;

    /// <summary>config_bathroom_item[90001].time：一张浴券的时长（秒）。</summary>
    public int BathTicketSeconds { get; init; } = 28_800;

    /// <summary>config_bathroom_item[90001].price：自动续券所需的温泉币（客户端以此判断能否续券）。</summary>
    public int BathPrice { get; init; } = 25;

    /// <summary>config_bathroom_item[90001].frequency：入浴经验的发放周期（秒）。</summary>
    public int BathExpFrequency { get; init; } = 300;

    /// <summary>config_bathroom_item[90001].once_exp：每个周期发放的经验。</summary>
    public int BathOnceExp { get; init; } = 55;

    /// <summary>config_parameter[150] affection_secretaryl_add1[0]：秘书舰好感增长周期（小时，客户端按小时解释）。</summary>
    public int AffTickHours { get; init; } = 6;

    /// <summary>config_parameter[150] affection_secretaryl_add1[1]：秘书舰每周期增加的好感。</summary>
    public int AffTickAdd { get; init; } = 7_500;

    /// <summary>config_parameter[152] affection_secretaryl_limit：秘书舰好感加成只能涨到此值以下。</summary>
    public int AffSecretaryLimit { get; init; } = 900_000;

    /// <summary>config_parameter[155] affection_normal_bound 上限。</summary>
    public int AffUnmarriedMax { get; init; } = PlayerAccountFactory.UnmarriedMaxAffection;

    /// <summary>config_parameter[156] affection_marry_bound 上限。</summary>
    public int AffMarriedMax { get; init; } = PlayerAccountFactory.MarriedMaxAffection;

    /// <summary>
    /// 居酒屋（type 4）的 reducecost 是否折减其它建筑的心情消耗。日服界面按折减后的值显示消耗，
    /// 但客户端外推不折减；这里按界面语义折减，两次同步之间客户端显示会略低于服务端。
    /// </summary>
    public bool ApplyTavernDiscount { get; init; } = true;

    /// <summary>按建筑 Tid 读取 config_buildinginfo（测试可注入）。</summary>
    public Func<int, ConfigBuildinginfo?> BuildingInfo { get; init; } = BuildingConfigLoader.GetInfo;

    /// <summary>舰娘模板对某建筑类型的性格加成（万分比，测试可注入）。</summary>
    public Func<int, int, int> HeroAddition { get; init; } = CharacterConfigLoader.BuildingAddition;

    /// <summary>舰娘模板对某配方类型的性格加成（万分比，测试可注入）。</summary>
    public Func<int, int, int> RecipeAddition { get; init; } = CharacterConfigLoader.RecipeAddition;

    /// <summary>config_parameter[209]：燃油（补给）产出的计量周期（秒）。</summary>
    public int OilUnit { get; init; } = 600;

    /// <summary>config_parameter[210]：资金产出的计量周期（秒）。</summary>
    public int GoldUnit { get; init; } = 600;

    /// <summary>道具工厂配方（config_recipe，测试可注入）。</summary>
    public Func<int, ConfigRecipe?> Recipe { get; init; } = RecipeConfigLoader.GetProduce;

    /// <summary>即时合成配方（config_recipe_compose，测试可注入）。</summary>
    public Func<int, ConfigRecipeCompose?> Compose { get; init; } = RecipeConfigLoader.GetCompose;

    /// <summary>按玩家等级的补给上限（config_player_levelup.supply_max_limit）。</summary>
    public Func<int, int> SupplyMax { get; init; } = PlayerLevelupLoader.SupplyMax;

    /// <summary>config_parameter[205]：工人体力回复的计量周期（秒）。</summary>
    public int WorkerRecoverUnit { get; init; } = 600;

    /// <summary>config_worker[1].addworkerhp：不依赖电力室的基础体力回复（万分制 / 600 秒）。</summary>
    public int WorkerBaseRecover { get; init; }

    /// <summary>按办公室等级的工人体力上限（显示值，客户端 GetMaxWorkerByLv；测试可注入）。</summary>
    public Func<int, int> MaxWorkerStrength { get; init; } = BuildingConfigLoader.GetMaxWorkerStrength;

    /// <summary>建造/升级配置（config_buildinglevelup，costwork 为消耗的工人体力显示值；测试可注入）。</summary>
    public Func<int, ConfigBuildinglevelup?> LevelUp { get; init; } = BuildingConfigLoader.GetLevelUp;

    // ───── 启动器「作弊选项（跳过时间）」，默认全关，见 CheatOptions。 ─────

    /// <summary>生产：道具工厂下单即完成，资源楼始终满仓。</summary>
    public bool OmitProductionTime { get; init; }

    /// <summary>体力：工人体力不消耗并保持上限。</summary>
    public bool OmitWorkerStrength { get; init; }

    /// <summary>心情：基建工作与体力加速不消耗心情（自然、宿舍、浴场回复照常）。</summary>
    public bool OmitMoodCost { get; init; }

    /// <summary>许愿墙：结算时清除尚未结束的祈愿冷却。</summary>
    public bool OmitVowCooldown { get; init; }

    /// <summary>从已加载的配置表构造规则；缺失的项保留日服默认值。</summary>
    public static SettlementRules FromConfig()
    {
        var defaults = new SettlementRules();
        IReadOnlyList<long> moodBound = ParameterCatalogLoader.GetArray(142, [defaults.MoodMin, defaults.MoodMax]);
        IReadOnlyList<long> secretaryAdd = ParameterCatalogLoader.GetArray(150, [defaults.AffTickHours, defaults.AffTickAdd]);
        IReadOnlyList<long> unmarried = ParameterCatalogLoader.GetArray(155, [0, defaults.AffUnmarriedMax]);
        IReadOnlyList<long> married = ParameterCatalogLoader.GetArray(156, [0, defaults.AffMarriedMax]);
        ConfigBathroomItem? ticket = BathroomItemLoader.Ticket;
        return defaults with
        {
            TickMinutes = Positive(ParameterCatalogLoader.Get(139, defaults.TickMinutes), defaults.TickMinutes),
            NaturalAdd = ParameterCatalogLoader.Get(140, defaults.NaturalAdd),
            MarryAdd = ParameterCatalogLoader.Get(145, defaults.MarryAdd),
            NaturalLimit = ParameterCatalogLoader.Get(144, defaults.NaturalLimit),
            MoodMin = moodBound.Count >= 2 ? checked((int)moodBound[0]) : defaults.MoodMin,
            MoodMax = moodBound.Count >= 2 ? checked((int)moodBound[1]) : defaults.MoodMax,
            RecoverUnit = Positive(ParameterCatalogLoader.Get(206, defaults.RecoverUnit), defaults.RecoverUnit),
            CostUnit = Positive(ParameterCatalogLoader.Get(207, defaults.CostUnit), defaults.CostUnit),
            BathTickAdd = ParameterCatalogLoader.Get(141, defaults.BathTickAdd),
            BathEnterAdd = ParameterCatalogLoader.Get(212, defaults.BathEnterAdd),
            BathGiftAdd = ParameterCatalogLoader.Get(220, defaults.BathGiftAdd),
            BathTicketSeconds = Positive(checked((int)(ticket?.Time ?? 0)), defaults.BathTicketSeconds),
            BathPrice = ticket is null ? defaults.BathPrice : checked((int)ticket.Price),
            BathExpFrequency = Positive(checked((int)(ticket?.Frequency ?? 0)), defaults.BathExpFrequency),
            BathOnceExp = ticket is null ? defaults.BathOnceExp : checked((int)ticket.OnceExp),
            AffTickHours = secretaryAdd.Count >= 2 ? Positive(checked((int)secretaryAdd[0]), defaults.AffTickHours) : defaults.AffTickHours,
            AffTickAdd = secretaryAdd.Count >= 2 ? checked((int)secretaryAdd[1]) : defaults.AffTickAdd,
            AffSecretaryLimit = ParameterCatalogLoader.Get(152, defaults.AffSecretaryLimit),
            AffUnmarriedMax = unmarried.Count >= 2 ? checked((int)unmarried[1]) : defaults.AffUnmarriedMax,
            AffMarriedMax = married.Count >= 2 ? checked((int)married[1]) : defaults.AffMarriedMax,
            OilUnit = Positive(ParameterCatalogLoader.Get(209, defaults.OilUnit), defaults.OilUnit),
            GoldUnit = Positive(ParameterCatalogLoader.Get(210, defaults.GoldUnit), defaults.GoldUnit),
            WorkerRecoverUnit = Positive(ParameterCatalogLoader.Get(205, defaults.WorkerRecoverUnit), defaults.WorkerRecoverUnit),
            WorkerBaseRecover = BuildingConfigLoader.WorkerBaseRecover,
        };
    }

    private static int Positive(int value, int fallback) => value > 0 ? value : fallback;
}

/// <summary>
/// 一次结算的结果。没有任何变化时 <see cref="Account"/> 与输入是同一个实例，调用方据此不存档、不推送。
/// </summary>
internal sealed record SettlementResult(
    PlayerAccount Account,
    IReadOnlySet<uint> ChangedHeroIds,
    bool BuildingChanged,
    bool BathChanged,
    bool VowChanged = false)
{
    public bool Changed => ChangedHeroIds.Count > 0 || BuildingChanged || BathChanged || VowChanged;

    public static SettlementResult Unchanged(PlayerAccount account) =>
        new(account, new HashSet<uint>(), false, false);
}

/// <summary>
/// 按经过时间结算舰娘心情（自然恢复、建筑工作消耗、宿舍回复、入浴回复）、基建产出（BuildingProduction）、
/// 工人体力回复、浴券到期、秘书舰好感与祈愿墙每日重置。
/// <para>
/// 公式逐位复刻客户端：自然恢复 = MarryLogic:GetMoodNum，建筑增减 = BuildingLogic:CheckoutHeroMoodChange，
/// 宿舍速度 = BuildingLogic:GetMoodRecoverSpeed，体力回复 = BuildingLogic:GetCurStrengthReal，
/// 秘书舰好感 = MarryLogic:GetLoveNum。结算后把 Hero.UpdateTime 推进到最后一个已跨过的自然恢复边界、
/// 把有人驻守或在生产的建筑 LastUpdateTime（电力室与体力为 WorkerUpdateTime）推进到 now，并原样下发这些锚点，
/// 客户端从同一锚点继续外推，显示保持连续。
/// </para>
/// <para>
/// 启动器作弊选项（SettlementRules.Omit*，默认全关）在这里生效：生产即时完成、体力保持上限、心情不因工作消耗、
/// 祈愿冷却清零。
/// </para>
/// <para>全部是纯函数：不读时钟、不做 IO，便于单元测试。同一个 now 重复调用不会重复结算。</para>
/// </summary>
internal static class TimeSettlement
{
    /// <summary>浴场浴位数（客户端 BathFleetPage 固定 6 个）。</summary>
    internal const int BathSlotCount = 6;

    /// <summary>温泉币货币类型（CurrencyType 13）。</summary>
    internal const int BathCurrencyType = 13;

    internal const int OfficeType = 1;
    internal const int ElectricFactoryType = 2;
    internal const int TavernType = 4;
    internal const int DormType = 5;

    /// <summary>
    /// 自然恢复周期数：边界为 UpdateTime 所在分钟之后的下一个 TickMinutes 整数倍分钟（保留 UpdateTime 的秒数），
    /// 之后每 TickMinutes 分钟一个。返回跨过的边界数与最后一个已跨过的边界（新锚点）；未跨过时锚点不变。
    /// 客户端用本地时间的分钟数，整点时区（JST/CST）与 UTC 的分钟数相同。
    /// </summary>
    internal static (long Ticks, long Anchor) NaturalTicks(long updateTime, long now, int tickMinutes)
    {
        long tick = (long)tickMinutes * 60;
        if (tick <= 0) return (0, updateTime);
        long minute = ((updateTime % 3600) + 3600) % 3600 / 60;
        long next = (minute / tickMinutes + 1) * tickMinutes;
        long near = updateTime + (next - minute) * 60;
        if (now < near) return (0, updateTime);
        long ticks = (now - near) / tick + 1;
        return (ticks, near + (ticks - 1) * tick);
    }

    /// <summary>自然恢复：超过 NaturalLimit 时，原值不高于上限则取上限，否则保持原值（与客户端一致）。</summary>
    internal static int ApplyNatural(int mood, long ticks, int perTick, int naturalLimit)
    {
        if (ticks <= 0) return mood;
        long candidate = mood + ticks * perTick;
        if (candidate > naturalLimit) return mood <= naturalLimit ? naturalLimit : mood;
        return checked((int)candidate);
    }

    /// <summary>
    /// 宿舍心情回复速度（每 RecoverUnit 秒，万分制）：floor(addmood × 加成)，加成只统计心情大于 0 的舰娘，
    /// 计算顺序与客户端 GetTotalHeroAddition 相同。
    /// </summary>
    internal static int DormSpeed(ConfigBuildinginfo cfg, IEnumerable<Hero> heroes, SettlementRules rules)
    {
        double total = 0;
        foreach (Hero hero in heroes)
        {
            if (hero.Mood <= 0) continue;
            int addition = rules.HeroAddition(hero.TemplateId, checked((int)cfg.Type));
            total = total + (cfg.Heroaddition + 10000.0) * (10000.0 + addition) / 10000.0 - 10000.0;
        }
        total = (total + 10000.0) / 10000.0;
        return checked((int)Math.Floor(cfg.Addmood * total));
    }

    /// <summary>
    /// 对单个舰娘做自然恢复并推进 UpdateTime；秘书舰在推进锚点前先结算好感。
    /// 适用于即将改变所处环境（入浴、换秘书舰等）的舰娘。
    /// </summary>
    internal static Hero ApplyNaturalToHero(Hero hero, long now, SettlementRules rules, bool isSecretary)
    {
        (long ticks, long anchor) = NaturalTicks(hero.UpdateTime, now, rules.TickMinutes);
        if (ticks == 0) return hero;
        Hero result = isSecretary ? BakeSecretaryAffection(hero, now, rules) : hero;
        int perTick = rules.NaturalAdd + (hero.MarryTime != 0 ? rules.MarryAdd : 0);
        return result with
        {
            Mood = ClampMood(ApplyNatural(hero.Mood, ticks, perTick, rules.NaturalLimit), rules),
            UpdateTime = checked((int)anchor),
        };
    }

    /// <summary>
    /// 秘书舰好感：首个周期在锚点 + AffTickHours 小时，此后每 AffTickHours 小时 +AffTickAdd；
    /// 超过未誓约/誓约上限时按客户端规则截断；结果达到 AffSecretaryLimit 时本次不增长（与 GetLoveNum 一致）。
    /// 锚点为 AffectionSettledAt，旧档为 0 时回落为 UpdateTime（客户端外推的锚点）。
    /// </summary>
    internal static Hero BakeSecretaryAffection(Hero hero, long now, SettlementRules rules)
    {
        long period = (long)rules.AffTickHours * 3600;
        if (period <= 0) return hero;
        long anchor = hero.AffectionSettledAt != 0 ? hero.AffectionSettledAt : hero.UpdateTime;
        // 还不满一个周期时也要钉住锚点：调用方随后会推进 UpdateTime，若锚点继续回落到新的 UpdateTime，
        // 频繁结算会让第一个周期永远凑不满，秘书舰好感不再增长。
        if (now < anchor + period)
            return hero.AffectionSettledAt == 0 ? hero with { AffectionSettledAt = anchor } : hero;
        long ticks = (now - anchor - period) / period + 1;
        long candidate = hero.Affection + ticks * rules.AffTickAdd;
        int max = hero.MarryTime == 0 ? rules.AffUnmarriedMax : rules.AffMarriedMax;
        long affection = candidate > max
            ? (hero.Affection <= max ? max : hero.Affection)
            : candidate;
        if (affection >= rules.AffSecretaryLimit) affection = hero.Affection;
        return hero with
        {
            Affection = checked((int)affection),
            AffectionSettledAt = anchor + ticks * period,
        };
    }

    internal static int ClampMood(long mood, SettlementRules rules) =>
        checked((int)Math.Clamp(mood, rules.MoodMin, rules.MoodMax));

    /// <summary>
    /// 浴位规范化：Pos 必须在 1..6 且唯一，越界或重复的舰娘移到第一个空浴位；船坞里已不存在的舰娘移除。
    /// </summary>
    internal static List<BathHero> NormalizeBath(IEnumerable<BathHero> list, ISet<uint> dockIds)
    {
        var result = new List<BathHero>();
        var used = new HashSet<int>();
        var seen = new HashSet<uint>();
        var pending = new List<BathHero>();
        foreach (BathHero bath in list)
        {
            if (!dockIds.Contains(bath.HeroId) || !seen.Add(bath.HeroId)) continue;
            if (bath.Pos is >= 1 and <= BathSlotCount && used.Add(bath.Pos)) result.Add(bath);
            else pending.Add(bath);
        }
        foreach (BathHero bath in pending)
        {
            int free = Enumerable.Range(1, BathSlotCount).FirstOrDefault(pos => !used.Contains(pos));
            if (free == 0) continue;
            used.Add(free);
            result.Add(bath with { Pos = free });
        }
        return result;
    }

    /// <summary>
    /// 结算整个账号到 now：建筑驻守舰娘的工作消耗/宿舍回复（含自然恢复）、浴场舰娘的入浴回复与浴券到期、
    /// 秘书舰好感。不在建筑也不在浴场的舰娘不处理（客户端按 UpdateTime 自行外推）。
    /// </summary>
    internal static SettlementResult Settle(PlayerAccount account, long nowRaw, SettlementRules rules)
    {
        long now = Math.Max(nowRaw, account.LastSettleTime);
        var heroes = new Dictionary<uint, Hero>();
        foreach (Hero hero in account.Dock.Heroes) heroes.TryAdd(hero.HeroId, hero);
        var dockIds = new HashSet<uint>(heroes.Keys);
        uint secretaryId = account.Character.SecretaryId;
        var changedHeroes = new HashSet<uint>();

        // 0) 浴场规范化：浴场优先，同时出现在浴场与建筑中的舰娘从建筑撤下。
        IReadOnlyList<BathHero> originalBath = account.Bath?.HeroList ?? [];
        List<BathHero> bath = NormalizeBath(originalBath, dockIds);
        bool bathChanged = !bath.SequenceEqual(originalBath);
        var bathIds = bath.Select(item => item.HeroId).ToHashSet();

        // 1) 建筑
        PlayerBuilding? state = account.Building;
        PlayerBuilding? newState = state;
        bool buildingChanged = false;
        if (state is not null)
        {
            PlayerBuilding original = state;
            if (state.ProductionVersion < BuildingProduction.CurrentVersion)
                state = BuildingProduction.Migrate(state, rules);
            long worker = state.WorkerUpdateTime == 0 ? now : state.WorkerUpdateTime;
            bool workerOccupied = false;
            double discount = TavernDiscount(state, rules);
            // 「心情」作弊开关切换后的第一次结算：先按当前规则改正存档里的加成窗口终点，再烘焙停机期间的产出与体力。
            PlayerBuilding windowsSource = state;
            state = state with
            {
                Buildings = state.Buildings
                    .Select(building => rules.BuildingInfo(building.Tid) is { } cfg
                        ? BuildingProduction.NormalizeWindows(building, cfg, heroes, discount, rules)
                        : building)
                    .ToArray(),
            };
            if (state.Buildings.SequenceEqual(windowsSource.Buildings, ReferenceEqualityComparer.Instance)) state = windowsSource;

            // 成员规范化提前算好：产出烘焙要读办公室的旧成员与旧加成窗口。
            var assigned = new HashSet<uint>();
            uint[][] membersAt = state.Buildings
                .Select(building => building.HeroIds
                    .Where(id => dockIds.Contains(id) && !bathIds.Contains(id) && assigned.Add(id))
                    .ToArray())
                .ToArray();
            int officeIndex = state.Buildings.ToList().FindIndex(building => rules.BuildingInfo(building.Tid)?.Type == OfficeType);
            BuildingProduction.OfficeSnapshot? office =
                officeIndex >= 0 && rules.BuildingInfo(state.Buildings[officeIndex].Tid) is { } officeCfg
                    ? new BuildingProduction.OfficeSnapshot(state.Buildings[officeIndex], officeCfg, membersAt[officeIndex].ToHashSet())
                    : null;

            // 工人体力：用旧锚点 W 与电力室旧加成窗口复刻客户端 GetCurStrengthReal，必须在下面重算窗口之前。
            bool recovering = BuildingProduction.StrengthRecovering(state, rules);
            (int strength, double carry) = recovering
                ? BuildingProduction.BakeStrength(state, membersAt, worker, now, heroes, rules)
                : (state.WorkerStrength, state.WorkerStrengthCarry);

            var buildings = new List<PlayerBuildingEntry>(state.Buildings.Count);
            for (int index = 0; index < state.Buildings.Count; index++)
            {
                PlayerBuildingEntry building = state.Buildings[index];
                uint[] members = membersAt[index];
                bool membershipChanged = !members.SequenceEqual(building.HeroIds);
                long last = building.LastUpdateTime == 0 ? now : building.LastUpdateTime;
                ConfigBuildinginfo? cfg = rules.BuildingInfo(building.Tid);
                int type = cfg is null ? 0 : checked((int)cfg.Type);
                if (type == ElectricFactoryType && (members.Length > 0 || membershipChanged)) workerOccupied = true;

                // a) 产出：用旧锚点与上次下发的加成窗口烘焙，不改锚点（心情结算仍要用旧锚点）。
                bool produced = false;
                PlayerBuildingEntry entry = cfg is null
                    ? building
                    : BuildingProduction.Bake(building, cfg, members.ToHashSet(), last, now, office, heroes, rules, out produced);

                if (cfg is null || members.Length == 0)
                {
                    buildings.Add(entry with
                    {
                        HeroIds = membershipChanged ? members : building.HeroIds,
                        LastUpdateTime = membershipChanged || produced ? Math.Max(last, now) : last,
                        HeroWindows = entry.HeroWindows is { Count: > 0 } ? [] : entry.HeroWindows,
                        Productivity = membershipChanged ? null : entry.Productivity,
                    });
                    continue;
                }

                int speed = building.MoodSpeed ?? DormSpeed(cfg, members.Select(id => heroes[id]), rules);
                long elapsed = Math.Max(0, now - (type == ElectricFactoryType ? worker : last));
                foreach (uint heroId in members)
                {
                    Hero before = heroes[heroId];
                    Hero natural = ApplyNaturalToHero(before, now, rules, heroId == secretaryId);
                    double delta = type == DormType
                        ? (double)elapsed * speed / rules.RecoverUnit
                        : rules.OmitMoodCost ? 0 : -(double)elapsed * cfg.Moodcost * discount / rules.CostUnit;
                    Hero after = natural with { Mood = ClampMood((long)Math.Floor(natural.Mood + delta), rules) };
                    if (after != before)
                    {
                        heroes[heroId] = after;
                        changedHeroes.Add(heroId);
                    }
                }

                int? moodSpeed = type == DormType
                    ? DormSpeed(cfg, members.Select(id => heroes[id]), rules)
                    : building.MoodSpeed;
                long newLast = Math.Max(last, now);
                // c) 统一推进锚点，并按结算后的心情重算加成窗口 [newLast, 心情归零时刻] 与效率。
                buildings.Add(BuildingProduction.WithDerived(entry with
                {
                    HeroIds = membershipChanged ? members : building.HeroIds,
                    LastUpdateTime = newLast,
                    MoodSpeed = moodSpeed,
                }, cfg, members.Select(id => heroes[id]).ToList(), newLast, discount, rules));
            }

            // 体力在回复或电力室有人时推进 W；已满且电力室无人时客户端不看 W，保持不动以免每次请求都改档。
            long newWorker = workerOccupied || recovering ? Math.Max(worker, now) : worker;
            PlayerBuilding candidate = BuildingProduction.WithCheatStrength(state with
            {
                Buildings = buildings,
                WorkerUpdateTime = newWorker,
                WorkerStrength = strength,
                WorkerStrengthCarry = carry,
            }, rules);
            if (candidate.WorkerUpdateTime != original.WorkerUpdateTime ||
                candidate.WorkerStrength != original.WorkerStrength ||
                candidate.WorkerStrengthCarry != original.WorkerStrengthCarry ||
                candidate.ProductionVersion != original.ProductionVersion ||
                !candidate.Buildings.SequenceEqual(original.Buildings, BuildingEntryComparer.Instance))
            {
                newState = candidate;
                buildingChanged = true;
            }
        }

        // 2) 浴场：浴券到期 / 自动续券，入浴期间每个自然恢复边界额外 +BathTickAdd。
        bool allAuto = account.Bath?.IsAllAuto == 1;
        GameServices.TryGetCurrency(account, BathCurrencyType, out int bathCoins);
        for (int i = 0; i < bath.Count; i++)
        {
            BathHero entry = bath[i];
            long enter = entry.EnterTime != 0 ? entry.EnterTime : entry.StartTime;
            long start = entry.StartTime;
            long finished = entry.FinishedAt;
            long bathTime = entry.BathTime;
            int ticket = rules.BathTicketSeconds;
            if (start != 0 && now >= start + ticket)
            {
                if ((entry.IsAuto == 1 || allAuto) && bathCoins >= rules.BathPrice)
                {
                    // 续券条件与客户端 BathTimeControl 相同（温泉币 ≥ BathPrice）；入浴与续券暂不扣温泉币，只有送礼扣。
                    start += (now - start) / ticket * ticket;
                }
                else
                {
                    finished = start + ticket;
                    start = 0;
                    bathTime = Math.Max(1, finished - enter);
                }
            }

            long bathEnd = finished != 0 ? finished : (start == 0 ? enter : now);
            Hero before = heroes[entry.HeroId];
            long bathTicks = enter == 0 ? 0 : Math.Max(0,
                NaturalTicks(before.UpdateTime, Math.Min(bathEnd, now), rules.TickMinutes).Ticks -
                NaturalTicks(before.UpdateTime, enter, rules.TickMinutes).Ticks);
            Hero after = ApplyNaturalToHero(before, now, rules, entry.HeroId == secretaryId);
            if (bathTicks > 0)
                after = after with { Mood = ClampMood(after.Mood + bathTicks * rules.BathTickAdd, rules) };
            if (after != before)
            {
                heroes[entry.HeroId] = after;
                changedHeroes.Add(entry.HeroId);
            }

            BathHero updated = entry with { StartTime = start, FinishedAt = finished, BathTime = bathTime, EnterTime = enter };
            if (updated != entry)
            {
                bath[i] = updated;
                bathChanged = true;
            }
        }

        // 3) 祈愿墙每日重置（UTC+8 0 点）：只清当日用石数，不碰冷却与任何心情/建筑锚点。
        PlayerVow? vow = VowLogic.NormalizeDaily(account.Vow, now);
        // 「许愿墙」作弊：清除尚未结束的冷却（已结束的不动），同时随结算补发祈愿快照。
        if (rules.OmitVowCooldown && vow is { CoolTime: > 0 } cooling && cooling.CoolTime > now)
            vow = cooling with { CoolTime = 0 };
        bool vowChanged = !ReferenceEquals(vow, account.Vow);

        if (changedHeroes.Count == 0 && !buildingChanged && !bathChanged && !vowChanged)
            return SettlementResult.Unchanged(account);

        HeroDock dock = account.Dock with
        {
            Heroes = account.Dock.Heroes.Select(hero => heroes.TryGetValue(hero.HeroId, out Hero? updated) && changedHeroes.Contains(hero.HeroId) ? updated : hero).ToList(),
        };
        PlayerAccount settled = account with
        {
            Dock = dock,
            Building = newState,
            Bath = account.Bath is null && bath.Count == 0 ? account.Bath : new PlayerBath(bath, account.Bath?.IsAllAuto ?? 0),
            Vow = vow,
            LastSettleTime = now,
        };
        return new SettlementResult(settled, changedHeroes, buildingChanged, bathChanged, VowChanged: vowChanged);
    }

    /// <summary>
    /// 把舰娘从所有建筑中撤下（入浴、退役时调用；调用前应已结算到 now）。被改动的建筑 LastUpdateTime 置为 now，
    /// 宿舍按剩余成员重算回复速度，电力室成员变化时同时推进 WorkerUpdateTime。
    /// </summary>
    internal static PlayerAccount RemoveFromBuildings(
        PlayerAccount account, IReadOnlySet<uint> heroIds, long now, SettlementRules rules, out bool changed)
    {
        changed = false;
        PlayerBuilding? state = account.Building;
        if (state is null || heroIds.Count == 0) return account;
        var heroes = account.Dock.Heroes.GroupBy(hero => hero.HeroId).ToDictionary(group => group.Key, group => group.First());
        double discount = TavernDiscount(state, rules);
        bool workerTouched = false;
        var buildings = new List<PlayerBuildingEntry>(state.Buildings.Count);
        foreach (PlayerBuildingEntry building in state.Buildings)
        {
            if (!building.HeroIds.Any(heroIds.Contains))
            {
                buildings.Add(building);
                continue;
            }
            uint[] members = building.HeroIds.Where(id => !heroIds.Contains(id)).ToArray();
            buildings.Add(RefreshOccupancy(building, members, now, rules, heroes, discount));
            if (rules.BuildingInfo(building.Tid)?.Type == ElectricFactoryType) workerTouched = true;
            changed = true;
        }
        if (!changed) return account;
        return account with
        {
            Building = state with
            {
                Buildings = buildings,
                WorkerUpdateTime = workerTouched ? Math.Max(state.WorkerUpdateTime, now) : state.WorkerUpdateTime,
            },
        };
    }

    /// <summary>
    /// 建筑成员或等级变化后（调用前已结算到 now）：LastUpdateTime 推进到 now，宿舍按新成员重算回复速度，
    /// 并按新成员重算加成窗口与效率。
    /// </summary>
    internal static PlayerBuildingEntry RefreshOccupancy(
        PlayerBuildingEntry building, IReadOnlyList<uint> members, long now, SettlementRules rules,
        IReadOnlyDictionary<uint, Hero> heroes, double discount)
    {
        ConfigBuildinginfo? cfg = rules.BuildingInfo(building.Tid);
        List<Hero> memberHeroes = members.Where(heroes.ContainsKey).Select(id => heroes[id]).ToList();
        int? speed = cfg?.Type == DormType ? DormSpeed(cfg, memberHeroes, rules) : building.MoodSpeed;
        long newLast = Math.Max(building.LastUpdateTime, now);
        return BuildingProduction.WithDerived(building with
        {
            HeroIds = members.ToArray(),
            LastUpdateTime = newLast,
            MoodSpeed = speed,
        }, cfg, memberHeroes, newLast, discount, rules);
    }

    /// <summary>居酒屋（第一栋 type 4）的 reducecost 对工作楼心情消耗的折减系数。</summary>
    internal static double TavernDiscount(PlayerBuilding state, SettlementRules rules)
    {
        if (!rules.ApplyTavernDiscount) return 1.0;
        ConfigBuildinginfo? tavern = state.Buildings
            .Select(item => rules.BuildingInfo(item.Tid))
            .FirstOrDefault(info => info?.Type == TavernType);
        return tavern is null ? 1.0 : Math.Max(0.0, 1.0 - tavern.Reducecost / 10000.0);
    }

    /// <summary>
    /// 更换秘书舰（调用前账号已结算到 now）。旧秘书舰按好感公式结算到 now 并清除好感锚点；
    /// 新秘书舰先结算自然恢复（推进 UpdateTime），好感锚点设为新的 UpdateTime——客户端以 UpdateTime
    /// 为锚点外推秘书舰好感，首个周期在锚点 + AffTickHours 小时，两边从同一时刻起算。
    /// </summary>
    internal static PlayerAccount ChangeSecretary(
        PlayerAccount account, uint oldSecretaryId, uint newSecretaryId, long now, SettlementRules rules,
        out IReadOnlySet<uint> changedHeroIds)
    {
        var changed = new HashSet<uint>();
        changedHeroIds = changed;
        if (oldSecretaryId == newSecretaryId) return account;
        List<Hero> heroes = account.Dock.Heroes.ToList();
        for (int i = 0; i < heroes.Count; i++)
        {
            Hero hero = heroes[i];
            Hero updated = hero;
            if (hero.HeroId == oldSecretaryId)
            {
                updated = BakeSecretaryAffection(hero, now, rules) with { AffectionSettledAt = 0 };
            }
            else if (hero.HeroId == newSecretaryId)
            {
                Hero natural = ApplyNaturalToHero(hero, now, rules, isSecretary: false);
                updated = natural with { AffectionSettledAt = natural.UpdateTime };
            }
            if (updated == hero) continue;
            heroes[i] = updated;
            changed.Add(hero.HeroId);
        }
        return changed.Count == 0 ? account : account with { Dock = account.Dock with { Heroes = heroes } };
    }

    /// <summary>入浴结束时的经验：每满 BathExpFrequency 秒发放 BathOnceExp。</summary>
    internal static int BathExp(long bathTime, SettlementRules rules) =>
        rules.BathExpFrequency <= 0 || bathTime <= 0
            ? 0
            : checked((int)Math.Min(int.MaxValue, bathTime / rules.BathExpFrequency * rules.BathOnceExp));

    /// <summary>按值比较建筑条目（HeroIds 是列表，record 默认按引用比较）。</summary>
    private sealed class BuildingEntryComparer : IEqualityComparer<PlayerBuildingEntry>
    {
        public static readonly BuildingEntryComparer Instance = new();

        public bool Equals(PlayerBuildingEntry? x, PlayerBuildingEntry? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.Id == y.Id && x.Tid == y.Tid && x.Level == y.Level && x.Status == y.Status &&
                   x.LastUpdateTime == y.LastUpdateTime && x.LastBuildUpdateTime == y.LastBuildUpdateTime &&
                   x.MoodSpeed == y.MoodSpeed && x.HeroIds.SequenceEqual(y.HeroIds) &&
                   x.ProductCount == y.ProductCount && x.Progress.Equals(y.Progress) && x.RecipeId == y.RecipeId &&
                   x.ItemCount == y.ItemCount && x.Productivity == y.Productivity &&
                   (x.HeroWindows ?? []).SequenceEqual(y.HeroWindows ?? []);
        }

        public int GetHashCode(PlayerBuildingEntry obj) => HashCode.Combine(obj.Id, obj.Tid, obj.LastUpdateTime);
    }
}
