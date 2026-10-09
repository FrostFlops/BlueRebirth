using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>config_refresh 中商店用到的日历规则：DAY(4) / WEEK(2) / MONTH(5)，刷新时刻为 p4:p5:p6。</summary>
internal sealed record ShopRefreshRule(int Id, int Type, int P1, int P3, int Hour, int Minute, int Second);

/// <summary>config_shop_goods 中库存与随机陈列用到的字段。Stock 为 -1 表示不限量。</summary>
internal sealed record ShopGoodRule(
    int Id,
    int ShelfId,
    long Stock,
    long Weight,
    bool Delisted,
    bool HasPeriod,
    IReadOnlyList<int> RandomLimits,
    bool ManualRefreshStock);

/// <summary>config_game_limits 一行（config_shop_goods.random_limits 引用）。</summary>
internal sealed record ShopLimitRule(int Id, int Type, string Script, IReadOnlyList<long> Params);

/// <summary>config_shop 中陈列与刷新用到的字段。</summary>
internal sealed record ShopRule(
    int ShopId,
    bool IsRandom,
    int GridCount,
    IReadOnlyList<int> ShelfList,
    IReadOnlyList<ShopRefreshRule> Refresh,
    int InitTimes,
    int RecoveryTime,
    int CumulateTimes,
    int MaxCount,
    IReadOnlyList<long> Price,
    int CurrencyType)
{
    /// <summary>有免费刷新（每日 init_times 或按 recovery_time 回复）。</summary>
    public bool HasFreeRefresh => InitTimes > 0 || RecoveryTime > 0;

    /// <summary>有付费刷新（客户端 _ClickRefresh：price 非空且 max_count &gt; 0）。</summary>
    public bool HasPaidRefresh => Price.Count > 0 && MaxCount > 0;

    /// <summary>客户端显示刷新按钮的条件（ShopItemShow._SetTitle）。</summary>
    public bool CanManualRefresh => HasFreeRefresh || HasPaidRefresh;

    /// <summary>有定时刷新（config_shop.refresh_time）。</summary>
    public bool HasSchedule => Refresh.Count > 0;

    /// <summary>需要预先建立状态：随机陈列、定时刷新或手动刷新。其它商店只在第一次购买时建立。</summary>
    public bool NeedsState => IsRandom || HasSchedule || CanManualRefresh;
}

/// <summary>「商店真实库存」使用的不可变商店目录（由 <see cref="ShopCatalogLoader"/> 从客户端配置构建，测试可自建）。</summary>
internal sealed class ShopCatalog
{
    private readonly Dictionary<int, ShopRule> _shops;
    private readonly Dictionary<int, ShopGoodRule> _goods;
    private readonly Dictionary<int, ShopLimitRule> _limits;
    private readonly Dictionary<int, IReadOnlyList<ShopGoodRule>> _shelves;

    public ShopCatalog(IEnumerable<ShopRule> shops, IEnumerable<ShopGoodRule> goods, IEnumerable<ShopLimitRule> limits)
    {
        _shops = shops.ToDictionary(shop => shop.ShopId);
        _goods = goods.ToDictionary(good => good.Id);
        _limits = limits.ToDictionary(limit => limit.Id);
        _shelves = _goods.Values
            .GroupBy(good => good.ShelfId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ShopGoodRule>)group.OrderBy(good => good.Id).ToList());
        ShopIds = _shops.Keys.OrderBy(id => id).ToArray();
    }

    public static ShopCatalog Empty { get; } = new([], [], []);

    /// <summary>全部商店 id（升序）。</summary>
    public IReadOnlyList<int> ShopIds { get; }

    public ShopRule? Shop(int shopId) => _shops.TryGetValue(shopId, out ShopRule? shop) ? shop : null;

    public ShopGoodRule? Good(int goodId) => _goods.TryGetValue(goodId, out ShopGoodRule? good) ? good : null;

    public ShopLimitRule? Limit(int limitId) => _limits.TryGetValue(limitId, out ShopLimitRule? limit) ? limit : null;

    /// <summary>货架上的全部商品（config_shop_goods.shelf_id 等于该货架 id，按商品 id 升序）。</summary>
    public IReadOnlyList<ShopGoodRule> Shelf(int shelfId) =>
        _shelves.TryGetValue(shelfId, out IReadOnlyList<ShopGoodRule>? goods) ? goods : [];

    /// <summary>从客户端配置表构建。refresh_time 里不认识的 config_refresh id 直接忽略（视为不定时刷新）。</summary>
    public static ShopCatalog FromConfig(
        IReadOnlyDictionary<int, ConfigShop> shops,
        IReadOnlyDictionary<int, ConfigShopGoods> goods,
        IReadOnlyDictionary<int, ConfigRefresh> refresh,
        IReadOnlyDictionary<int, ConfigGameLimits> limits)
    {
        static int Int(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        static ShopRefreshRule ToRefresh(int id, ConfigRefresh row) =>
            new(id, Int(row.Type), Int(row.P1), Int(row.P3), Int(row.P4), Int(row.P5), Int(row.P6));

        IEnumerable<ShopRule> shopRules = shops.Select(kv => new ShopRule(
            kv.Key,
            kv.Value.IsRandom != 0,
            Int(kv.Value.GridCount),
            (kv.Value.ShelfList ?? []).Select(Int).ToList(),
            (kv.Value.RefreshTime ?? [])
                .Where(id => refresh.ContainsKey(Int(id)))
                .Select(id => ToRefresh(Int(id), refresh[Int(id)]))
                .ToList(),
            Int(kv.Value.InitTimes),
            Int(kv.Value.RecoveryTime),
            Int(kv.Value.CumulateTimes),
            Int(kv.Value.MaxCount),
            kv.Value.Price ?? [],
            Int(kv.Value.CurrencyType)));
        IEnumerable<ShopGoodRule> goodRules = goods.Select(kv => new ShopGoodRule(
            kv.Key,
            Int(kv.Value.ShelfId),
            kv.Value.Stock,
            kv.Value.Weight,
            kv.Value.Undercarriage != 0,
            kv.Value.PeriodBuy is { Count: > 0 },
            (kv.Value.RandomLimits ?? []).Select(Int).ToList(),
            kv.Value.ManualRefreshStock != 0));
        IEnumerable<ShopLimitRule> limitRules = limits.Select(kv => new ShopLimitRule(
            kv.Key, Int(kv.Value.LimitType), kv.Value.ScriptName ?? "", kv.Value.LimitParam ?? []));
        return new ShopCatalog(shopRules, goodRules, limitRules);
    }
}

/// <summary>
/// 商店用的客户端日历。日服客户端的 isSameDay / os.date / PeriodManager 都按 SDK gethash 下发的 offset 换算时区，
/// 本地服务端（BootstrapHttpResponder）下发的是 "0"，所以客户端日历就是 UTC：每日在 UTC 0 点、每周在周一 UTC 0 点刷新。
/// 服务端其它按日重置（祈愿墙、任务、每日副本）用的是 UTC+8，与此无关；改 gethash offset 时必须同步 <see cref="ClientTimeZoneOffsetSeconds"/>。
/// </summary>
internal static class ShopCalendar
{
    /// <summary>客户端时区偏移（秒，custom_time.lua 约定：本地时间 = UTC − 偏移，JST 为 −32400）。gethash offset "0" → 0。</summary>
    internal const int ClientTimeZoneOffsetSeconds = 0;

    /// <summary>config_refresh.type：每周（p1 = 星期，1 = 周一）。</summary>
    internal const int TypeWeek = 2;

    /// <summary>config_refresh.type：每日。</summary>
    internal const int TypeDay = 4;

    /// <summary>config_refresh.type：每月（p3 = 日）。</summary>
    internal const int TypeMonth = 5;

    /// <summary>客户端日历的日序号（isSameDay 的 floor((t − offset) / 86400)）。</summary>
    internal static int DayIndex(long now) =>
        checked((int)Math.Floor((now - ClientTimeZoneOffsetSeconds) / 86400d));

    /// <summary>
    /// 不晚于 now 的最近一次刷新时刻，移植 PeriodManager.calTime / prev：先取本日 / 本周 / 本月的刷新点，晚于 now 时退一个周期。
    /// 不支持的类型返回 0（不定时刷新）。
    /// </summary>
    internal static long LastBoundary(ShopRefreshRule rule, long now)
    {
        DateTime local = DateTime.UnixEpoch.AddSeconds(now - ClientTimeZoneOffsetSeconds);
        TimeSpan at = new(Math.Max(0, rule.Hour), Math.Max(0, rule.Minute), Math.Max(0, rule.Second));
        DateTime boundary;
        switch (rule.Type)
        {
            case TypeDay:
                boundary = local.Date + at;
                if (local < boundary) boundary = boundary.AddDays(-1);
                break;
            case TypeWeek:
                // Lua os.date 的 wday：周日 = 1；PeriodManager 取 wday = p1 + 1。
                int wday = (int)local.DayOfWeek + 1;
                boundary = local.Date.AddDays(rule.P1 + 1 - wday) + at;
                if (local < boundary) boundary = boundary.AddDays(-7);
                break;
            case TypeMonth:
                // os.time 会把越界的日期顺延到下个月，AddDays 的效果相同。
                DateTime first = new(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                boundary = first.AddDays(rule.P3 - 1) + at;
                if (local < boundary) boundary = first.AddMonths(-1).AddDays(rule.P3 - 1) + at;
                break;
            default:
                return 0;
        }
        return (long)(boundary - DateTime.UnixEpoch).TotalSeconds + ClientTimeZoneOffsetSeconds;
    }

    /// <summary>多个刷新规则取最近的一次；没有规则时为 0。</summary>
    internal static long LastBoundary(IReadOnlyList<ShopRefreshRule> rules, long now)
    {
        long last = 0;
        foreach (ShopRefreshRule rule in rules) last = Math.Max(last, LastBoundary(rule, now));
        return last;
    }
}

/// <summary>手动刷新的支付方式（与客户端 _ClickRefresh 的顺序相同：免费 → 入荷指令 → 钻石）。</summary>
internal enum ShopRefreshPayment
{
    None,
    Free,
    Ticket,
    Currency,
}

/// <summary>购买的库存检查结果。Ok 为 false 时 State 是原状态。</summary>
internal sealed record ShopBuyCheck(bool Ok, PlayerShopState State, string Error = "");

/// <summary>手动刷新的计划。Ok 时 State 已计入次数并清空可刷新商品的已购数（随机商店的 Lineup 置 null 待重抽）；扣费由调用方按 Payment 决定。</summary>
internal sealed record ShopRefreshPlan(
    bool Ok, PlayerShopState State, ShopRefreshPayment Payment = ShopRefreshPayment.None, long Cost = 0, string Error = "");

/// <summary>
/// 「商店真实库存」选项（--real-shop-stock）的纯函数：定时刷新、每日刷新次数、免费刷新回复、随机陈列、购买库存与手动刷新。
/// 不读时钟、不做 IO；随机数由调用方注入。
/// <para>
/// 两处按推断实现（日服客户端不读这些字段，没有字节码可证）：定时刷新（config_refresh）清空该商店所有商品的已购数，
/// manual_refresh_stock 只决定手动刷新时哪些商品补货；init_times 是每个客户端日的免费次数。
/// </para>
/// </summary>
internal static class ShopStock
{
    /// <summary>config_game_limits 中按玩家等级限制的类型（limit_type 1000 / script PlayerLevel，limit_param = [下限, 上限]）。</summary>
    internal const int PlayerLevelLimitType = 1000;

    /// <summary>可用的免费刷新次数：FRefreshNum + max(0, init_times − UsedFRefreshNum)（客户端 _ClickRefresh / _SetFreeRefresh）。</summary>
    internal static int AvailableFree(PlayerShopState state, ShopRule shop) =>
        state.FRefreshNum + Math.Max(0, shop.InitTimes - state.UsedFRefreshNum);

    /// <summary>新建状态：定时刷新锚点取当前周期，日序号取今天，免费刷新回复从 now 开始计时。</summary>
    internal static PlayerShopState NewState(ShopRule shop, long now) => new(
        shop.ShopId,
        RefreshAnchor: ShopCalendar.LastBoundary(shop.Refresh, now),
        DayIndex: shop.CanManualRefresh ? ShopCalendar.DayIndex(now) : 0,
        FRefreshTime: shop.RecoveryTime > 0 ? now : 0);

    /// <summary>
    /// 按经过时间推进单个商店：
    /// (a) 跨过定时刷新点：清空全部已购数，随机商店的陈列置 null 待重抽；
    /// (b) 客户端日历跨日（仅可手动刷新的商店）：付费刷新次数与每日免费次数归零；已回复的次数截到
    ///     cumulate_times − init_times，使可用免费次数不超过 cumulate_times（客户端按可用总数与上限比较）；
    /// (c) 免费刷新每 recovery_time 回复 1 次，可用次数到 cumulate_times 为止，锚点按整周期推进。
    /// 所有比较都只增不减（时钟回拨不会重复重置）；没有变化时返回同一个实例。
    /// </summary>
    internal static PlayerShopState Normalize(PlayerShopState state, ShopRule shop, long now)
    {
        PlayerShopState result = state;
        long boundary = ShopCalendar.LastBoundary(shop.Refresh, now);
        if (boundary > result.RefreshAnchor)
        {
            result = result with
            {
                Bought = null,
                Lineup = shop.IsRandom ? null : result.Lineup,
                RefreshAnchor = boundary,
            };
        }
        if (shop.CanManualRefresh)
        {
            int today = ShopCalendar.DayIndex(now);
            if (today > result.DayIndex)
            {
                int recoveredCap = shop.CumulateTimes > 0 ? Math.Max(0, shop.CumulateTimes - shop.InitTimes) : result.FRefreshNum;
                result = result with
                {
                    RefreshNum = 0, UsedFRefreshNum = 0, DayIndex = today, FRefreshNum = Math.Min(result.FRefreshNum, recoveredCap),
                };
            }
        }
        if (shop.RecoveryTime > 0 && shop.CumulateTimes > 0)
        {
            if (result.FRefreshTime <= 0)
            {
                result = result with { FRefreshTime = now };
            }
            else
            {
                int available = AvailableFree(result, shop);
                long elapsed = now - result.FRefreshTime;
                if (available < shop.CumulateTimes && elapsed >= shop.RecoveryTime)
                {
                    long recovered = Math.Min(elapsed / shop.RecoveryTime, shop.CumulateTimes - available);
                    result = result with
                    {
                        FRefreshNum = checked(result.FRefreshNum + (int)recovered),
                        FRefreshTime = result.FRefreshTime + recovered * shop.RecoveryTime,
                    };
                }
            }
        }
        return result;
    }

    /// <summary>推进全部已保存的商店状态，把有变化的商店 id 写进 changedShopIds；全部无变化时返回同一个实例。</summary>
    internal static PlayerShop NormalizeAll(PlayerShop shop, ShopCatalog catalog, long now, ISet<int> changedShopIds)
    {
        List<PlayerShopState>? updated = null;
        for (int i = 0; i < shop.Shops.Count; i++)
        {
            PlayerShopState state = shop.Shops[i];
            if (catalog.Shop(state.ShopId) is not { } rule) continue;
            PlayerShopState next = Normalize(state, rule, now);
            if (ReferenceEquals(next, state)) continue;
            updated ??= shop.Shops.ToList();
            updated[i] = next;
            changedShopIds.Add(state.ShopId);
        }
        return updated is null ? shop : shop with { Shops = updated };
    }

    internal static PlayerShopState? Find(PlayerShop? shop, int shopId) =>
        shop?.Shops.FirstOrDefault(state => state.ShopId == shopId);

    /// <summary>写回（替换或追加）一个商店的状态。</summary>
    internal static PlayerShop Put(PlayerShop? shop, PlayerShopState state)
    {
        List<PlayerShopState> states = shop?.Shops.ToList() ?? [];
        int index = states.FindIndex(item => item.ShopId == state.ShopId);
        if (index >= 0) states[index] = state;
        else states.Add(state);
        return (shop ?? new PlayerShop([])) with { Shops = states };
    }

    internal static int Bought(PlayerShopState? state, int goodId) =>
        state?.Bought?.Where(item => item.GoodsId == goodId).Sum(item => item.Num) ?? 0;

    /// <summary>某商店某商品自上次重置以来的已购数量（决定分档价格与库存）。</summary>
    internal static int BoughtCount(PlayerShop? shop, int shopId, int goodId) => Bought(Find(shop, shopId), goodId);

    private static PlayerShopState AddBought(PlayerShopState state, int goodId, int num)
    {
        List<ShopGoodCount> bought = state.Bought?.Where(item => item.GoodsId != goodId).ToList() ?? [];
        bought.Add(new ShopGoodCount(goodId, checked(Bought(state, goodId) + num)));
        return state with { Bought = bought };
    }

    /// <summary>
    /// random_limits 是否全部满足。目前只认识 PlayerLevel（等级在 [下限, 上限] 内）；其它类型与找不到的 id 视为满足，
    /// 并写进 unknownLimits 供调用方记录日志。
    /// </summary>
    internal static bool LimitsPass(ShopGoodRule good, ShopCatalog catalog, int level, ISet<int>? unknownLimits)
    {
        foreach (int limitId in good.RandomLimits)
        {
            if (catalog.Limit(limitId) is { Type: PlayerLevelLimitType, Script: "PlayerLevel", Params.Count: >= 2 } limit)
            {
                if (level < limit.Params[0] || level > limit.Params[1]) return false;
                continue;
            }
            unknownLimits?.Add(limitId);
        }
        return true;
    }

    /// <summary>
    /// 随机商店抽陈列：第 i 格（0 起，共 grid_count 格）从货架 shelf_list[min(i, 末项)] 按 weight 抽一个，
    /// 候选为同货架、weight &gt; 0、未下架、没有限时购买期、满足 random_limits 且本次未抽到过的商品（同一陈列不重复，
    /// 购买按 GoodId 识别）。没有候选的格子跳过。
    /// </summary>
    internal static IReadOnlyList<int> DrawLineup(
        ShopRule shop, ShopCatalog catalog, int level, Random rng, ISet<int>? unknownLimits = null)
    {
        var lineup = new List<int>();
        if (shop.ShelfList.Count == 0) return lineup;
        var chosen = new HashSet<int>();
        int slots = shop.GridCount > 0 ? shop.GridCount : shop.ShelfList.Count;
        for (int slot = 0; slot < slots; slot++)
        {
            int shelfId = shop.ShelfList[Math.Min(slot, shop.ShelfList.Count - 1)];
            List<ShopGoodRule> candidates = catalog.Shelf(shelfId)
                .Where(good => good.Weight > 0 && !good.Delisted && !good.HasPeriod && !chosen.Contains(good.Id) &&
                               LimitsPass(good, catalog, level, unknownLimits))
                .ToList();
            if (candidates.Count == 0) continue;
            long roll = rng.NextInt64(candidates.Sum(good => good.Weight));
            ShopGoodRule picked = candidates[^1];
            foreach (ShopGoodRule candidate in candidates)
            {
                if (roll < candidate.Weight)
                {
                    picked = candidate;
                    break;
                }
                roll -= candidate.Weight;
            }
            chosen.Add(picked.Id);
            lineup.Add(picked.Id);
        }
        return lineup;
    }

    /// <summary>
    /// 建立缺失的商店状态（随机、定时刷新、可手动刷新的商店）并为没有陈列的随机商店抽陈列。
    /// changedShopIds 收集新建或重抽的商店；没有变化时返回同一个账号。
    /// </summary>
    internal static PlayerAccount EnsureState(
        PlayerAccount account, ShopCatalog catalog, long now, Random rng, ISet<int> changedShopIds,
        ISet<int>? unknownLimits = null)
    {
        List<PlayerShopState> states = account.Shop?.Shops.ToList() ?? [];
        bool changed = false;
        foreach (int shopId in catalog.ShopIds)
        {
            if (catalog.Shop(shopId) is not { NeedsState: true } rule) continue;
            int index = states.FindIndex(state => state.ShopId == shopId);
            PlayerShopState state = index >= 0 ? states[index] : NewState(rule, now);
            if (rule.IsRandom && state.Lineup is null)
                state = state with { Lineup = DrawLineup(rule, catalog, account.Character.Level, rng, unknownLimits) };
            if (index < 0) states.Add(state);
            else if (!ReferenceEquals(state, states[index])) states[index] = state;
            else continue;
            changed = true;
            changedShopIds.Add(shopId);
        }
        return changed ? account with { Shop = (account.Shop ?? new PlayerShop([])) with { Shops = states } } : account;
    }

    /// <summary>
    /// 购买的库存检查：商品必须在该商店当前陈列中、未下架、数量为正，且不超过库存（stock − 已购）。通过时已购数 += buyNum。
    /// </summary>
    internal static ShopBuyCheck TryBuy(
        PlayerShopState state, IReadOnlyList<int> lineup, ShopCatalog catalog, int goodId, int buyNum)
    {
        if (buyNum <= 0) return new(false, state, "purchase count must be positive");
        if (!lineup.Contains(goodId)) return new(false, state, $"good {goodId} is not on sale in shop {state.ShopId}");
        ShopGoodRule? good = catalog.Good(goodId);
        if (good is { Delisted: true }) return new(false, state, $"good {goodId} is delisted");
        long stock = good?.Stock ?? -1;
        int bought = Bought(state, goodId);
        if (stock >= 0 && bought + (long)buyNum > stock)
            return new(false, state, $"good {goodId} is sold out ({bought}/{stock}, buying {buyNum})");
        return new(true, AddBought(state, goodId, buyNum));
    }

    /// <summary>
    /// 手动刷新（shop.RefreshShop；日服只发 ShopId），顺序与客户端 _ClickRefresh 相同：
    /// 1) 有免费次数就用（先用每日 init_times，再用回复的 FRefreshNum；用之前已满 cumulate_times 时回复计时从 now 重新开始）；
    /// 2) 否则付费：当日付费次数达到 max_count 拒绝；有入荷指令（config_parameter 183）用 1 个，否则按 price[min(RefreshNum, 末档)]
    ///    扣 currency_type 货币；付费次数 +1。
    /// 之后清空 manual_refresh_stock = 1 的商品已购数，随机商店的陈列置 null 由调用方重抽。
    /// </summary>
    internal static ShopRefreshPlan TryManualRefresh(
        PlayerShopState state, ShopRule shop, ShopCatalog catalog, long now, bool hasTicket)
    {
        if (!shop.CanManualRefresh) return new(false, state, Error: $"shop {shop.ShopId} cannot be refreshed");
        PlayerShopState next;
        ShopRefreshPayment payment;
        long cost = 0;
        int available = AvailableFree(state, shop);
        if (shop.HasFreeRefresh && available > 0)
        {
            bool wasFull = shop.RecoveryTime > 0 && available >= shop.CumulateTimes;
            next = state.UsedFRefreshNum < shop.InitTimes
                ? state with { UsedFRefreshNum = state.UsedFRefreshNum + 1 }
                : state with { FRefreshNum = state.FRefreshNum - 1 };
            if (wasFull) next = next with { FRefreshTime = now };
            payment = ShopRefreshPayment.Free;
        }
        else if (shop.HasPaidRefresh)
        {
            if (state.RefreshNum >= shop.MaxCount)
                return new(false, state, Error: $"shop {shop.ShopId} reached its daily refresh limit {shop.MaxCount}");
            payment = hasTicket ? ShopRefreshPayment.Ticket : ShopRefreshPayment.Currency;
            cost = hasTicket ? 1 : shop.Price[Math.Min(state.RefreshNum, shop.Price.Count - 1)];
            next = state with { RefreshNum = state.RefreshNum + 1 };
        }
        else
        {
            return new(false, state, Error: $"shop {shop.ShopId} has no free refresh left");
        }

        List<ShopGoodCount>? kept = next.Bought?
            .Where(item => catalog.Good(item.GoodsId) is { ManualRefreshStock: false })
            .ToList();
        next = next with
        {
            Bought = kept is { Count: > 0 } ? kept : null,
            Lineup = shop.IsRandom ? null : next.Lineup,
        };
        return new(true, next, payment, cost);
    }

    /// <summary>
    /// 编码一个商店：Num = 已购数，Status = 1 表示售罄（stock ≥ 0 且已购 ≥ stock，客户端 IsUnLockBeforeShop 据此解锁下一页），
    /// 刷新相关的四个字段来自状态（没有状态时为 0）。
    /// </summary>
    internal static RetShopInfo ToShopInfo(int shopId, IReadOnlyList<int> lineup, PlayerShopState? state, ShopCatalog catalog)
    {
        List<ShopGoodsData> goods = lineup
            .Select(goodId =>
            {
                int bought = Bought(state, goodId);
                long stock = catalog.Good(goodId)?.Stock ?? -1;
                return new ShopGoodsData(goodId, bought, stock >= 0 && bought >= stock ? 1 : 0);
            })
            .ToList();
        return new RetShopInfo(
            shopId,
            goods,
            UsedFRefreshNum: state?.UsedFRefreshNum ?? 0,
            FRefreshNum: state?.FRefreshNum ?? 0,
            FRefreshTime: (int)Math.Clamp(state?.FRefreshTime ?? 0, 0, int.MaxValue),
            RefreshNum: state?.RefreshNum ?? 0);
    }
}
