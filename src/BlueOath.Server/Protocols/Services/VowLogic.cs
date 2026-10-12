using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 祈愿墙使用的配置与查询（可注入，便于测试）。默认值为日服 1.4.0 实测：
/// config_parameter[84] 超级祈愿石 10008、[85] SSR 冷却上限、[86] SR 冷却上限、[511] UR 冷却上限。
/// </summary>
internal sealed record VowRules
{
    public int SuperItemId { get; init; } = 10_008;
    public int CapSsr { get; init; } = 777_600;
    public int CapSr { get; init; } = 194_400;
    public int CapUr { get; init; } = 1_036_800;
    public Func<int, ConfigShipMain?> ShipMain { get; init; } = ShipMainLoader.Get;
    public Func<int, int> MaxBreakLevel { get; init; } = ShipMainLoader.MaxBreakLevel;
    public Func<int, ConfigShipHandbook?> Handbook { get; init; } = ShipHandbookLoader.Get;
    public Func<int, ConfigShipInfo?> ShipInfo { get; init; } = _ => null;
    public Func<int, ConfigVow?> Vow { get; init; } = VowConfigLoader.GetVow;
    public Func<int, ConfigVowItem?> VowItem { get; init; } = VowConfigLoader.GetItem;

    /// <summary>启动器「许愿墙」作弊：祈愿后不进入冷却，冷却中也允许祈愿。</summary>
    public bool OmitCooldown { get; init; }

    public static VowRules FromConfig(IReadOnlyDictionary<int, ConfigShipInfo> shipInfos) => new()
    {
        SuperItemId = ParameterCatalogLoader.Get(84, 10_008),
        CapSsr = ParameterCatalogLoader.Get(85, 777_600),
        CapSr = ParameterCatalogLoader.Get(86, 194_400),
        CapUr = ParameterCatalogLoader.Get(511, 1_036_800),
        ShipInfo = id => shipInfos.GetValueOrDefault(id),
    };
}

internal enum VowFailure { None, Cooling, NoCandidate, DockFull }

/// <summary>一次祈愿的抽取结果。UR ムーバー 系图鉴得到碎片（FragmentItem × FragmentNum）而不是舰船。</summary>
internal sealed record VowPick(
    VowFailure Failure,
    IReadOnlyList<int> Wall,
    int ShipInfoId = 0,
    int TemplateId = 0,
    int FragmentItem = 0,
    int FragmentNum = 0)
{
    public bool IsFragment => FragmentItem > 0 && FragmentNum > 0;
}

internal sealed record VowDecResult(PlayerAccount Account, bool Changed);

/// <summary>
/// 祈愿墙纯函数（不读时钟、不做 IO）。冷却是绝对时间戳，客户端用 VowCoolTime − svrTime 自行倒计时，
/// 服务端只需在祈愿/用石时算好并持久化，并在每条 illustrate.IllustrateInfo 推送里带上。
/// 冷却公式复刻日服 WishLogic:GetFinalChargeTime：
/// min(Σ 未上墙候选的 ban 时间 + 结果品质加时, 品质上限)。
/// </summary>
internal static class VowLogic
{
    /// <summary>每日重置按 UTC+8 0 点（与 TaskService / DailyCopyService 一致）。</summary>
    internal const int DayOffsetSeconds = 8 * 60 * 60;

    /// <summary>冷却判定的容差（秒）：客户端在冷却刚好结束的同一秒发起时，服务端时钟可能还差 1 秒。</summary>
    internal const int CoolingToleranceSeconds = 2;

    private const int MubarCountry = 12;

    internal static int Day(long now) => checked((int)((now + DayOffsetSeconds) / 86_400));

    /// <summary>日服 CheckCanWish：图鉴存在、is_wish≠0、show_state≥1；is_wish=2 需活动已开放。</summary>
    internal static bool CanWish(int shipInfoId, long now, VowRules rules)
    {
        ConfigShipHandbook? handbook = rules.Handbook(shipInfoId);
        if (handbook is null || handbook.IsWish == 0 || handbook.ShowState < 1) return false;
        if (handbook.IsWish == 1) return true;
        if (handbook.IsWish == 2)
        {
            // 日服只有 activity 1101 一个活动，2020-11-17 起恒为开放。
            if (handbook.Activity > 0) return true;
            if (handbook.WishActivatetime > 0) return now > handbook.WishActivatetime;
        }
        return false;
    }

    /// <summary>SR/SSR，或 UR 且为ムーバー国家（日服 BaseCondition / BaseConditionCountry）。</summary>
    internal static bool QualityEligible(int shipInfoId, VowRules rules) =>
        rules.ShipInfo(shipInfoId) is { } info &&
        (info.Quality is 3 or 4 || (info.Quality == 5 && info.ShipCountry == MubarCountry));

    internal static bool IsWishable(int shipInfoId, long now, VowRules rules) =>
        CanWish(shipInfoId, now, rules) && QualityEligible(shipInfoId, rules);

    /// <summary>客户端 GetShipInfoIdByTid：config_ship_main[templateId].ship_info_id。</summary>
    internal static int ShipInfoIdOf(int templateId, VowRules rules) =>
        rules.ShipMain(templateId) is { } main ? checked((int)main.ShipInfoId) : (templateId - 1) / 10;

    private static int BreakOf(int templateId, VowRules rules) =>
        rules.ShipMain(templateId) is { } main ? checked((int)main.BreakLevel) : 1;

    private static long SfOf(int shipInfoId, VowRules rules) =>
        rules.ShipInfo(shipInfoId)?.SfId is > 0 and var sf ? sf : shipInfoId;

    /// <summary>ban 候选：船坞中可上墙的图鉴，按首次出现顺序。</summary>
    internal static List<int> Candidates(HeroDock dock, long now, VowRules rules) =>
        dock.Heroes.Select(hero => ShipInfoIdOf(hero.TemplateId, rules))
            .Distinct()
            .Where(shipInfoId => IsWishable(shipInfoId, now, rules))
            .ToList();

    /// <summary>同 sf_id 的船坞舰娘（含改造形态）中最大的 break_level，至少为 1（日服 GetHeroMaxLvByillId）。</summary>
    internal static int MaxAdvance(int shipInfoId, HeroDock dock, VowRules rules)
    {
        long sf = SfOf(shipInfoId, rules);
        int advance = 1;
        foreach (Hero hero in dock.Heroes)
            if (SfOf(ShipInfoIdOf(hero.TemplateId, rules), rules) == sf)
                advance = Math.Max(advance, BreakOf(hero.TemplateId, rules));
        return advance;
    }

    /// <summary>未上墙的候选各加一段 ban 时间：满突破加 ban_full，否则加 ban_not_full。</summary>
    internal static long BanAddTime(HeroDock dock, IEnumerable<int> wall, long now, VowRules rules)
    {
        var onWall = wall.ToHashSet();
        long total = 0;
        foreach (int shipInfoId in Candidates(dock, now, rules))
        {
            if (onWall.Contains(shipInfoId)) continue;
            ConfigVow? vow = rules.Vow(checked((int)rules.ShipInfo(shipInfoId)!.Quality));
            if (vow is null) continue;
            total += MaxAdvance(shipInfoId, dock, rules) == rules.MaxBreakLevel(shipInfoId)
                ? vow.BanFullBreakAddTime
                : vow.BanNotFullBreakAddTime;
        }
        return total;
    }

    internal static long Cap(int quality, VowRules rules) => quality switch
    {
        3 => rules.CapSr,
        5 => rules.CapUr,
        _ => rules.CapSsr,
    };

    /// <summary>祈愿后的冷却秒数（结果品质 q）：min(ban + result_that_quality_add_time(q), Cap(q))。</summary>
    internal static long FinalChargeTime(HeroDock dock, IEnumerable<int> wall, int pickShipInfoId, long now, VowRules rules)
    {
        int quality = checked((int)rules.ShipInfo(pickShipInfoId)!.Quality);
        long total = BanAddTime(dock, wall, now, rules) + (rules.Vow(quality)?.ResultThatQualityAddTime ?? 0);
        return Math.Min(total, Cap(quality, rules));
    }

    /// <summary>用石减冷却的下限（result_that_quality_limit_time，日服全为 0）。</summary>
    internal static long LimitTime(int coolHero, VowRules rules) =>
        rules.ShipInfo(ShipInfoIdOf(coolHero, rules)) is { } info
            ? rules.Vow(checked((int)info.Quality))?.ResultThatQualityLimitTime ?? 0
            : 0;

    /// <summary>墙列表：去掉无效与不在图鉴中的 id，去重并保持顺序（不做可祈愿过滤，与客户端 SetPreHeroList 一致）。</summary>
    internal static List<int> SanitizeWall(IEnumerable<int> ids, VowRules rules) =>
        ids.Where(id => id > 0 && rules.Handbook(id) is not null).Distinct().ToList();

    /// <summary>在墙上可祈愿的图鉴中均匀随机取一名（952002「ランダムで1名」）。</summary>
    internal static VowPick PickWish(
        PlayerAccount account, IReadOnlyList<int> requested, long now, VowRules rules, Func<int, int> nextIndex)
    {
        List<int> wall = SanitizeWall(requested, rules);
        if (!rules.OmitCooldown && (account.Vow?.CoolTime ?? 0) > now + CoolingToleranceSeconds)
            return new(VowFailure.Cooling, wall);
        List<int> eligible = wall.Where(id => IsWishable(id, now, rules)).ToList();
        if (eligible.Count == 0) return new(VowFailure.NoCandidate, wall);
        int shipInfoId = eligible[Math.Clamp(nextIndex(eligible.Count), 0, eligible.Count - 1)];
        ConfigShipInfo info = rules.ShipInfo(shipInfoId)!;
        long dataId = rules.Handbook(shipInfoId)!.ShipDataId;
        int templateId = checked((int)(dataId > 0 ? dataId : shipInfoId * 10L + 1));
        if (info.VowItemMub > 0 && info.VowItemNumMub > 0)
            return new(VowFailure.None, wall, shipInfoId, templateId,
                checked((int)info.VowItemMub), checked((int)info.VowItemNumMub));
        if (account.Dock.Heroes.Count >= account.Dock.BagSize) return new(VowFailure.DockFull, wall);
        return new(VowFailure.None, wall, shipInfoId, templateId);
    }

    /// <summary>
    /// 跨日（UTC+8）重置当日用石数：各石头的用量置 0（不删除条目）、Count 置 0。保留 0 值条目是因为
    /// 客户端收到非空 UseInfo 时只按 tid 逐条覆盖，只有置 0 才能可靠覆盖它手里前一天的用量。
    /// 已经全为 0 时返回同一实例；按日序号单调比较，时钟回拨不会二次重置。
    /// </summary>
    internal static PlayerVow? NormalizeDaily(PlayerVow? vow, long now)
    {
        if (vow is null) return null;
        int day = Day(now);
        if (day <= vow.UseResetDay) return vow;
        if (vow.Count == 0 && (vow.UseInfo ?? []).All(use => use.ItemNum == 0)) return vow;
        return vow with
        {
            UseInfo = (vow.UseInfo ?? []).Select(use => use with { ItemNum = 0 }).ToList(),
            Count = 0,
            UseResetDay = day,
        };
    }

    /// <summary>推送用的祈愿快照（只读的跨日视图，不落盘）。</summary>
    internal static VowSnapshot Snapshot(PlayerVow? vow, long now)
    {
        PlayerVow view = NormalizeDaily(vow, now) ?? new PlayerVow();
        return new VowSnapshot(
            view.CoolTime,
            view.CoolHero,
            view.Count,
            view.HeroList ?? [],
            (view.UseInfo ?? []).Select(use => new VowItemUseInfo(use.ItemTid, use.ItemNum)).ToList());
    }

    /// <summary>
    /// 用祈愿石减冷却（illustrate.VowDecTime）。逐条按请求顺序：数量取 请求数、背包数、每日剩余（有上限时）
    /// 的最小值，按请求全额扣除（日服客户端在会浪费加速时间时先弹窗确认）；冷却已到下限的条目跳过。
    /// 超级祈愿石扣 1 颗并直接清零冷却。冷却未生效时不扣任何道具。
    /// </summary>
    internal static VowDecResult DecTime(
        PlayerAccount account, IReadOnlyList<(int ItemTid, int ItemNum)> request, long now, VowRules rules)
    {
        PlayerVow? start = NormalizeDaily(account.Vow, now);
        bool normalized = !ReferenceEquals(start, account.Vow);
        PlayerAccount baseline = normalized ? account with { Vow = start } : account;
        if (start is null || start.CoolTime <= now || request.Count == 0) return new(baseline, normalized);

        long floor = now + LimitTime(start.CoolHero, rules);
        int coolShipInfo = ShipInfoIdOf(start.CoolHero, rules);
        var used = (start.UseInfo ?? []).ToList();
        long cool = start.CoolTime;
        int count = start.Count;
        bool consumed = false;
        PlayerAccount after = baseline;
        foreach ((int tid, int want) in request)
        {
            if (want <= 0 || cool <= now) continue;
            int inBag = after.Bag?.Items.FirstOrDefault(item => item.TemplateId == tid)?.Num ?? 0;
            int num;
            if (tid == rules.SuperItemId)
            {
                if (inBag < 1) continue;
                num = 1;
                cool = now;
            }
            else
            {
                ConfigVowItem? cfg = rules.VowItem(tid);
                if (cfg is null || cfg.Time <= 0 || cool <= floor) continue;
                // 专属祈愿石只对绑定的舰娘生效（952009）。
                if (cfg.Type == 2 && !(cfg.ShipId?.Contains(coolShipInfo) ?? false)) continue;
                long n = want;
                if (cfg.DailyLimit > 0)
                    n = Math.Min(n, cfg.DailyLimit - (used.FirstOrDefault(use => use.ItemTid == tid)?.ItemNum ?? 0));
                n = Math.Min(n, inBag);
                if (n <= 0) continue;
                num = checked((int)n);
                cool = Math.Max(floor, cool - cfg.Time * n);
            }
            after = GameServices.AddBagItem(after, tid, -num);
            int index = used.FindIndex(use => use.ItemTid == tid);
            if (index >= 0) used[index] = used[index] with { ItemNum = used[index].ItemNum + num };
            else used.Add(new VowItemUse(tid, num));
            count += num;
            consumed = true;
        }
        if (!consumed) return new(baseline, normalized);
        return new(after with
        {
            Vow = start with { CoolTime = cool, Count = count, UseInfo = used, UseResetDay = Day(now) },
        }, true);
    }
}
