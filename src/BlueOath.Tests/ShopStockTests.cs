using System.Text.Json;
using System.Text.Json.Nodes;
using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server;
using BlueOath.Server.Protocols;
using BlueOath.Storage;
using static TestSupport;

/// <summary>
/// 「商店真实库存」（--real-shop-stock）的测试：<see cref="PureTest"/> 覆盖日历、定时 / 每日 / 免费刷新、库存、
/// 随机陈列、编码与存档；<see cref="ModuleTest"/> 走 ShopModule 协议（只开库存、库存 + 真实消耗、全关三种）。
/// 由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class ShopStockTests
{
    // Program.cs 的顶层函数 Assert / FindRepositoryRoot 会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private static string FindRepositoryRoot() => TestSupport.FindRepositoryRoot();

    private sealed record GoodView(int Id, int Num, int Status);

    private sealed record ShopView(
        int ShopId, List<GoodView> Goods, bool HasRefreshNum, int RefreshNum, int Used, int Free, int FRefreshTime)
    {
        public List<int> Ids => Goods.Select(good => good.Id).ToList();
    }

    public static Task PureTest()
    {
        string configDir = ConfigDbLoader.BuildConfigDir(Path.Combine(FindRepositoryRoot(), "blueoath", "blueoath"));
        ShopCatalogLoader.Load(configDir);
        ShopCatalog catalog = ShopCatalogLoader.Catalog;

        // 目录：104 个商店；随机商店 1/5；定时刷新 3（每周）、18（每周 21000）、19/20（每月）。
        Assert(catalog.ShopIds.Count == 104 && catalog.ShopIds.SequenceEqual(ShopCatalogLoader.GetAllShopIds()),
            "the shop catalog does not cover every config_shop id");
        ShopRule shop1 = catalog.Shop(1)!, shop3 = catalog.Shop(3)!, shop5 = catalog.Shop(5)!;
        ShopRule shop19 = catalog.Shop(19)!, shop20 = catalog.Shop(20)!;
        Assert(shop1 is { IsRandom: true, GridCount: 10, InitTimes: 3, RecoveryTime: 14_400, CumulateTimes: 6, MaxCount: 10, CurrencyType: 2, HasSchedule: false } &&
               shop1.Price.SequenceEqual(new long[] { 20, 50, 50, 50, 100, 100, 100, 100, 200, 200 }) &&
               shop1.ShelfList.SequenceEqual([100]),
            "shop 1 refresh settings were not loaded from config_shop");
        Assert(shop5 is { IsRandom: true, GridCount: 10, CanManualRefresh: true } &&
               shop5.ShelfList.SequenceEqual([805, 805, 805, 805, 805, 806, 807, 808, 809, 810]),
            "shop 5 shelf slots were not loaded");
        Assert(shop3 is { IsRandom: false, CanManualRefresh: false } &&
               shop3.Refresh.Single() is { Id: 4, Type: ShopCalendar.TypeWeek, P1: 1, Hour: 0, Minute: 0, Second: 0 } &&
               catalog.Shop(18)!.Refresh.Single() is { Id: 21000, Type: ShopCalendar.TypeWeek, P1: 1 } &&
               shop19.Refresh.Single() is { Id: 3, Type: ShopCalendar.TypeMonth, P3: 1 } &&
               shop20.Refresh.Single() is { Type: ShopCalendar.TypeMonth },
            "scheduled refreshes were not resolved through config_refresh");
        Assert(catalog.Shelf(100).Count == 106 && catalog.Shelf(805).Count == 26 &&
               catalog.Good(61003) is { Stock: 5 } && catalog.Good(100000) is { Stock: -1 },
            "shelves or stock were not loaded from config_shop_goods");

        // 日历（UTC，PeriodManager）：周一 0 点、每月 1 日 0 点、每日 p4 点；不支持的类型不定时刷新。
        long monday = Utc(2026, 10, 5);
        Assert(DateTimeOffset.FromUnixTimeSeconds(monday).DayOfWeek == DayOfWeek.Monday, "test calendar is off");
        ShopRefreshRule weekly = shop3.Refresh[0], monthly = shop19.Refresh[0];
        Assert(ShopCalendar.LastBoundary(weekly, monday) == monday &&
               ShopCalendar.LastBoundary(weekly, monday - 1) == monday - 7 * 86_400 &&
               ShopCalendar.LastBoundary(weekly, Utc(2026, 10, 9, 12)) == monday &&
               ShopCalendar.LastBoundary(weekly, Utc(2026, 10, 11, 23, 59, 59)) == monday,
            "weekly boundaries do not follow PeriodManager WEEK");
        Assert(ShopCalendar.LastBoundary(monthly, Utc(2026, 10, 1)) == Utc(2026, 10, 1) &&
               ShopCalendar.LastBoundary(monthly, Utc(2026, 10, 31, 23)) == Utc(2026, 10, 1) &&
               ShopCalendar.LastBoundary(monthly, Utc(2026, 10, 1) - 1) == Utc(2026, 9, 1) &&
               ShopCalendar.LastBoundary(monthly, Utc(2026, 1, 1) - 1) == Utc(2025, 12, 1),
            "monthly boundaries do not follow PeriodManager MONTH");
        var daily6 = new ShopRefreshRule(40600, ShopCalendar.TypeDay, -1, -1, 6, 0, 0);
        Assert(ShopCalendar.LastBoundary(daily6, Utc(2026, 10, 9, 5, 59, 59)) == Utc(2026, 10, 8, 6) &&
               ShopCalendar.LastBoundary(daily6, Utc(2026, 10, 9, 6)) == Utc(2026, 10, 9, 6) &&
               ShopCalendar.LastBoundary(shop1.Refresh, monday) == 0 &&
               ShopCalendar.LastBoundary(new ShopRefreshRule(5, 1, 2019, 23, 20, 10, 10), monday) == 0,
            "daily or unsupported refresh rules mismatch");
        Assert(ShopCalendar.DayIndex(Utc(2026, 10, 9)) == ShopCalendar.DayIndex(Utc(2026, 10, 9, 23, 59, 59)) &&
               ShopCalendar.DayIndex(Utc(2026, 10, 10)) == ShopCalendar.DayIndex(Utc(2026, 10, 9)) + 1,
            "client day index is not the UTC day");

        // 库存：Num 累加，达到 stock 时 Status = 1；超量、不在陈列、数量非正都拒绝；shop 19 买空后全部 Status = 1。
        long wed = Utc(2026, 10, 7, 12);
        IReadOnlyList<int> lineup19 = [61000, 61002, 61003];
        PlayerShopState s19 = ShopStock.NewState(shop19, wed);
        Assert(s19 is { RefreshAnchor: var anchor19, DayIndex: 0, FRefreshTime: 0, Bought: null } && anchor19 == Utc(2026, 10, 1),
            "a monthly shop state did not start at the current period");
        s19 = Check(ShopStock.TryBuy(s19, lineup19, catalog, 61003, 5), "buying 61003 x5");
        Assert(ShopStock.ToShopInfo(19, lineup19, s19, catalog).ShopGoodsData!.SequenceEqual(
                [new ShopGoodsData(61000, 0, 0), new ShopGoodsData(61002, 0, 0), new ShopGoodsData(61003, 5, 1)]),
            "Num/Status did not report the bought count and the sold-out state");
        Assert(!ShopStock.TryBuy(s19, lineup19, catalog, 61003, 1).Ok &&
               !ShopStock.TryBuy(s19, lineup19, catalog, 61000, 11).Ok &&
               !ShopStock.TryBuy(s19, lineup19, catalog, 62000, 1).Ok &&
               !ShopStock.TryBuy(s19, lineup19, catalog, 61000, 0).Ok,
            "over-stock, unlisted or empty purchases were accepted");
        s19 = Check(ShopStock.TryBuy(s19, lineup19, catalog, 61000, 4), "buying 61000 x4");
        s19 = Check(ShopStock.TryBuy(s19, lineup19, catalog, 61000, 6), "buying 61000 x6");
        s19 = Check(ShopStock.TryBuy(s19, lineup19, catalog, 61002, 10), "buying 61002 x10");
        RetShopInfo boughtOut = ShopStock.ToShopInfo(19, lineup19, s19, catalog);
        Assert(boughtOut.ShopGoodsData!.All(good => good.Status == 1) &&
               boughtOut.ShopGoodsData!.Select(good => good.Num).SequenceEqual([10, 10, 5]) &&
               ShopStock.BoughtCount(new PlayerShop([s19]), 19, 61000) == 10 &&
               ShopStock.BoughtCount(new PlayerShop([s19]), 20, 61000) == 0,
            "buying out shop 19 did not mark every good sold out (shop 20 stays locked)");

        IReadOnlyList<int> lineup3 = [100000, 100011, 110001];
        PlayerShopState s3 = ShopStock.NewState(shop3, wed);
        s3 = Check(ShopStock.TryBuy(s3, lineup3, catalog, 100000, 999), "buying unlimited 100000");
        s3 = Check(ShopStock.TryBuy(s3, lineup3, catalog, 100011, 1), "buying 100011");
        Assert(ShopStock.ToShopInfo(3, lineup3, s3, catalog).ShopGoodsData!.SequenceEqual(
                [new ShopGoodsData(100000, 999, 0), new ShopGoodsData(100011, 1, 1), new ShopGoodsData(110001, 0, 0)]),
            "an unlimited good was reported sold out");

        // 每周刷新（shop 3）：周内不变；跨周一 0 点清空已购；同一 now 再算不变；时钟回拨不会再次重置。
        Assert(ReferenceEquals(ShopStock.Normalize(s3, shop3, wed + 3_600), s3), "a weekly shop changed within its week");
        long nextMonday = Utc(2026, 10, 12);
        PlayerShopState reset3 = ShopStock.Normalize(s3, shop3, nextMonday);
        Assert(reset3.Bought is null && reset3.RefreshAnchor == nextMonday, "the weekly boundary did not restock shop 3");
        Assert(ReferenceEquals(ShopStock.Normalize(reset3, shop3, nextMonday), reset3) &&
               ReferenceEquals(ShopStock.Normalize(reset3, shop3, nextMonday - 3_600), reset3) &&
               ReferenceEquals(ShopStock.Normalize(reset3, shop3, nextMonday + 6 * 86_400), reset3),
            "the weekly reset is not idempotent");
        PlayerShopState rebought = Check(ShopStock.TryBuy(reset3, lineup3, catalog, 100011, 1), "re-buying 100011");
        Assert(ReferenceEquals(ShopStock.Normalize(rebought, shop3, nextMonday - 86_400), rebought) &&
               ShopStock.Normalize(rebought, shop3, nextMonday + 7 * 86_400).Bought is null,
            "a clock rollback reset the weekly stock twice");

        // 每月刷新（shop 19/20）：1 日 0 点清空。
        Assert(ReferenceEquals(ShopStock.Normalize(s19, shop19, Utc(2026, 10, 31, 23, 59, 59)), s19) &&
               ShopStock.Normalize(s19, shop19, Utc(2026, 11, 1)) is { Bought: null } november &&
               ShopStock.ToShopInfo(19, lineup19, november, catalog).ShopGoodsData!.All(good => good is { Num: 0, Status: 0 }),
            "the monthly boundary did not restock shop 19");
        PlayerShopState s20 = Check(ShopStock.TryBuy(ShopStock.NewState(shop20, wed), [62000], catalog, 62000, 1), "buying 62000");
        Assert(ShopStock.Normalize(s20, shop20, Utc(2026, 11, 1)).Bought is null, "the monthly boundary did not restock shop 20");

        // 随机陈列：shop 5 Lv80 → 10 个不重复，前 5 格来自货架 805 且只有 192161（50～200 级）的商品，后 5 格依次来自 806～810。
        IReadOnlyList<int> draw5 = ShopStock.DrawLineup(shop5, catalog, 80, new Random(20_261_009));
        Assert(draw5.Count == 10 && draw5.Distinct().Count() == 10 &&
               draw5.Take(5).All(id => catalog.Good(id) is { ShelfId: 805 } good && good.RandomLimits.SequenceEqual([192161])) &&
               draw5.Skip(5).Select(id => catalog.Good(id)!.ShelfId).SequenceEqual([806, 807, 808, 809, 810]),
            $"shop 5 draw at level 80 is wrong: {string.Join(",", draw5)}");
        Assert(ShopStock.DrawLineup(shop5, catalog, 80, new Random(20_261_009)).SequenceEqual(draw5),
            "a seeded draw is not deterministic");
        var unknownLimits = new HashSet<int>();
        IReadOnlyList<int> draw1 = ShopStock.DrawLineup(shop1, catalog, 30, new Random(7), unknownLimits);
        Assert(draw1.Count == 10 && draw1.Distinct().Count() == 10 && unknownLimits.Count == 0 &&
               draw1.All(id => catalog.Good(id) is { ShelfId: 100, Weight: > 0, Delisted: false, HasPeriod: false } good &&
                               !good.RandomLimits.Contains(192155)),
            $"shop 1 draw at level 30 contains weight-0, level 50+ or delisted goods: {string.Join(",", draw1)}");
        Assert(ShopStock.DrawLineup(shop1, catalog, 80, new Random(7)).All(id => !catalog.Good(id)!.RandomLimits.Contains(192153)) &&
               Enumerable.Range(0, 20)
                   .Select(seed => string.Join(",", ShopStock.DrawLineup(shop1, catalog, 80, new Random(seed))))
                   .Distinct().Count() > 1,
            "shop 1 draw at level 80 ignored the level limit or never varied");
        var custom = new ShopCatalog(
            [new ShopRule(9001, true, 3, [1], [], 0, 0, 0, 0, [], 0)],
            [new ShopGoodRule(1, 1, 1, 10, false, false, [777], true), new ShopGoodRule(2, 1, 1, 10, false, false, [], true)],
            []);
        var customUnknown = new HashSet<int>();
        IReadOnlyList<int> drawnCustom = ShopStock.DrawLineup(custom.Shop(9001)!, custom, 1, new Random(1), customUnknown);
        Assert(drawnCustom.OrderBy(id => id).SequenceEqual([1, 2]) && customUnknown.SetEquals([777]),
            "an unknown random limit was not treated as satisfied and reported (or an empty slot was not skipped)");

        // 免费刷新回复：每 4 小时 +1，可用次数到 6 为止；用掉满额中的一次后从当时重新计时。
        long d0 = Utc(2026, 10, 5, 0, 10);
        PlayerShopState fresh = ShopStock.NewState(shop1, d0);
        Assert(ShopStock.AvailableFree(fresh, shop1) == 3 && fresh.FRefreshTime == d0 && fresh.DayIndex == ShopCalendar.DayIndex(d0) &&
               ReferenceEquals(ShopStock.Normalize(fresh, shop1, d0 + 14_399), fresh) &&
               ShopStock.Normalize(fresh, shop1, d0 + 14_400) is { FRefreshNum: 1, FRefreshTime: var recoveredAt } && recoveredAt == d0 + 14_400,
            "free refreshes did not recover one per recovery_time");
        PlayerShopState capped = ShopStock.Normalize(fresh, shop1, d0 + 13 * 3_600);
        Assert(capped.FRefreshNum == 3 && capped.FRefreshTime == d0 + 3 * 14_400 && ShopStock.AvailableFree(capped, shop1) == 6 &&
               ReferenceEquals(ShopStock.Normalize(capped, shop1, d0 + 23 * 3_600), capped),
            "free refreshes did not stop at cumulate_times");
        ShopRefreshPlan spend = ShopStock.TryManualRefresh(capped, shop1, catalog, d0 + 13 * 3_600, hasTicket: true);
        Assert(spend is { Ok: true, Payment: ShopRefreshPayment.Free } &&
               spend.State is { UsedFRefreshNum: 1, FRefreshNum: 3, Lineup: null } && spend.State.FRefreshTime == d0 + 13 * 3_600,
            "a free refresh did not use the daily allowance first or did not restart the timer at the cap");
        Assert(ReferenceEquals(ShopStock.Normalize(spend.State, shop1, d0 + 17 * 3_600 - 1), spend.State) &&
               ShopStock.Normalize(spend.State, shop1, d0 + 17 * 3_600) is { FRefreshNum: 4 },
            "the restarted recovery timer is off");
        // 跨日补回 3 次每日免费次数时，可用总数仍不超过 cumulate_times（6）。
        long recentRecovery = Utc(2026, 10, 6, 0, 30);
        PlayerShopState hoarded = capped with { UsedFRefreshNum = 3, FRefreshNum = 6, FRefreshTime = recentRecovery };
        Assert(ShopStock.Normalize(hoarded, shop1, Utc(2026, 10, 6, 1)) is { FRefreshNum: 3, UsedFRefreshNum: 0 } afterMidnight &&
               ShopStock.AvailableFree(afterMidnight, shop1) == 6 &&
               ShopStock.Normalize(capped with { UsedFRefreshNum = 3, FRefreshNum = 1, FRefreshTime = recentRecovery }, shop1,
                   Utc(2026, 10, 6, 1)).FRefreshNum == 1,
            "the daily reset pushed free refreshes past cumulate_times");
        Assert(ShopStock.TryManualRefresh(capped with { UsedFRefreshNum = 3 }, shop1, catalog, d0, false) is
               { Ok: true, Payment: ShopRefreshPayment.Free, State.FRefreshNum: 2 },
            "a recovered free refresh was not consumed after the daily allowance");

        // 手动刷新顺序：免费 3 次 → 有入荷指令先用道具 → 钻石按 price[RefreshNum] 分档 → 第 11 次付费刷新拒绝。
        PlayerShopState r = ShopStock.NewState(shop1, d0) with { Lineup = draw1, Bought = [new ShopGoodCount(draw1[0], 1)] };
        for (int i = 1; i <= 3; i++)
        {
            ShopRefreshPlan free = ShopStock.TryManualRefresh(r, shop1, catalog, d0, hasTicket: true);
            Assert(free is { Ok: true, Payment: ShopRefreshPayment.Free, State: { RefreshNum: 0, Lineup: null, Bought: null } } &&
                   free.State.UsedFRefreshNum == i,
                $"free refresh {i} did not reset the lineup and the bought counts");
            r = free.State with { Lineup = draw1 };
        }
        ShopRefreshPlan ticket = ShopStock.TryManualRefresh(r, shop1, catalog, d0, hasTicket: true);
        Assert(ticket is { Ok: true, Payment: ShopRefreshPayment.Ticket, Cost: 1, State.RefreshNum: 1 },
            "the refresh ticket was not used before diamonds");
        r = ticket.State;
        var diamondCosts = new List<long>();
        for (int i = 0; i < 9; i++)
        {
            ShopRefreshPlan paid = ShopStock.TryManualRefresh(r, shop1, catalog, d0, hasTicket: false);
            Assert(paid is { Ok: true, Payment: ShopRefreshPayment.Currency }, $"paid refresh {i + 2} was rejected");
            diamondCosts.Add(paid.Cost);
            r = paid.State;
        }
        Assert(diamondCosts.SequenceEqual(new long[] { 50, 50, 50, 100, 100, 100, 100, 200, 200 }) && r.RefreshNum == 10 &&
               !ShopStock.TryManualRefresh(r, shop1, catalog, d0, hasTicket: true).Ok,
            $"paid refresh tiers or the daily limit mismatch: {string.Join(",", diamondCosts)}");
        PlayerShopState nextDay = ShopStock.Normalize(r, shop1, Utc(2026, 10, 6));
        Assert(nextDay is { RefreshNum: 0, UsedFRefreshNum: 0 } && nextDay.DayIndex == r.DayIndex + 1 &&
               ReferenceEquals(ShopStock.Normalize(nextDay, shop1, Utc(2026, 10, 5, 12)), nextDay) &&
               ShopStock.TryManualRefresh(nextDay with { UsedFRefreshNum = 3, FRefreshNum = 0 }, shop1, catalog, Utc(2026, 10, 6), false) is
                   { Ok: true, Payment: ShopRefreshPayment.Currency, Cost: 20 },
            "the daily reset of refresh counters is wrong");
        Assert(!ShopStock.TryManualRefresh(ShopStock.NewState(shop3, wed), shop3, catalog, wed, true).Ok,
            "a shop without refresh settings was refreshed");
        var mixed = new ShopCatalog(
            [new ShopRule(9002, false, 0, [], [], 1, 0, 0, 0, [], 0)],
            [new ShopGoodRule(10, 0, 3, 0, false, false, [], false), new ShopGoodRule(11, 0, 3, 0, false, false, [], true)],
            []);
        ShopRefreshPlan restock = ShopStock.TryManualRefresh(
            new PlayerShopState(9002, Bought: [new ShopGoodCount(10, 2), new ShopGoodCount(11, 2)]), mixed.Shop(9002)!, mixed, wed, false);
        Assert(restock is { Ok: true, Payment: ShopRefreshPayment.Free } && restock.State.Bought!.SequenceEqual([new ShopGoodCount(10, 2)]),
            "a manual refresh restocked a good with manual_refresh_stock = 0");

        // 结算：只在 RealShopStock 时推进商店，报告变化的商店 id；同一 now 与回拨不再变化。
        PlayerAccount account = PlayerAccountFactory.CreateDefault("shop-settle", checked((int)wed)) with
        {
            Building = null,
            Bath = null,
            Shop = new PlayerShop([s3, s19]),
        };
        var rules = new SettlementRules { RealShopStock = true, Shops = () => catalog };
        SettlementResult quiet = TimeSettlement.Settle(account, wed + 60, rules);
        Assert(!quiet.Changed && ReferenceEquals(quiet.Account, account), "settlement changed an unchanged shop");
        SettlementResult weeklySettle = TimeSettlement.Settle(account, nextMonday, rules);
        Assert(weeklySettle.Changed && weeklySettle.ShopChangedIds!.SetEquals([3]) &&
               ShopStock.Find(weeklySettle.Account.Shop, 3)!.Bought is null &&
               ReferenceEquals(ShopStock.Find(weeklySettle.Account.Shop, 19), s19) &&
               weeklySettle.Account.LastSettleTime == nextMonday,
            "settlement did not restock the weekly shop alone");
        Assert(!TimeSettlement.Settle(weeklySettle.Account, nextMonday, rules).Changed &&
               !TimeSettlement.Settle(weeklySettle.Account, nextMonday - 86_400, rules).Changed &&
               !TimeSettlement.Settle(account, nextMonday, rules with { RealShopStock = false }).Changed,
            "shop settlement is not idempotent or ran with the option off");

        // 编码：字段 2（RefreshNum）始终存在；刷新参数解码。
        Assert(PlayerDataCodec.Encode(new RetShopInfo(5)).AsSpan(0, 4).SequenceEqual(new byte[] { 0x08, 0x05, 0x10, 0x00 }) &&
               PlayerDataCodec.Encode(new RetShopInfo(1, RefreshNum: 3)).AsSpan(2, 2).SequenceEqual(new byte[] { 0x10, 0x03 }) &&
               DecodeShop(PlayerDataCodec.Encode(new RetShopInfo(7))) is { HasRefreshNum: true, RefreshNum: 0, Goods.Count: 0 },
            "TRetShopInfo field 2 (RefreshNum) was not always encoded");
        Assert(PlayerDataCodec.DecodeShopRefreshArg(new ProtocolPackage().Write(0x08, 5UL).Write(0x10, 2UL).Write(0x18, 1UL).ToArray()) ==
               new ShopRefreshArg(5, 2, true) &&
               PlayerDataCodec.DecodeShopRefreshArg([]) == new ShopRefreshArg(),
            "TShopRefreshArg was not decoded");

        // 存档：带 Shop 的账号 JSON 往返；没有 shop 字段的旧档读成 null。
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PlayerAccount withShop = account with { Shop = new PlayerShop([s19, rebought, r with { Lineup = draw1 }]) };
        PlayerAccount back = JsonSerializer.Deserialize<PlayerAccount>(JsonSerializer.Serialize(withShop, json), json)!;
        Assert(SameShop(back.Shop, withShop.Shop), "PlayerAccount.Shop did not survive a JSON round-trip");
        JsonObject legacy = JsonNode.Parse(JsonSerializer.Serialize(account with { Shop = null }, json))!.AsObject();
        Assert(legacy.Remove("shop") && JsonSerializer.Deserialize<PlayerAccount>(legacy.ToJsonString(), json)!.Shop is null,
            "a legacy save without a shop property did not load");
        return Task.CompletedTask;
    }

    public static async Task ModuleTest()
    {
        string root = FindRepositoryRoot();
        string clientPath = Path.Combine(root, "blueoath", "blueoath");
        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-shop-stock-" + Guid.NewGuid().ToString("N"));
        long t0 = Utc(2026, 10, 7, 12);
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            foreach (string profile in new[] { "shop-off", "shop-stock", "shop-both" })
            {
                PlayerAccount seed = PlayerAccountFactory.CreateDefault(profile, checked((int)t0));
                await repo.SaveAccountAsync(seed with
                {
                    Bag = new PlayerBag([new BagItem(13000, 100), new BagItem(10303, 1)]),
                    Character = seed.Character with { Diamond = 60 },
                });
            }
            using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
                Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            GameServices Services(string profile, params string[] flags) => new(repo, ServerOptions.Parse(
                [.. new[] { "--no-cheats", "--real-resource-cost=off", "--data=" + dataRoot, "--client-path=" + clientPath, "--profile-id=" + profile }, .. flags]), loggerFactory);
            async Task<PlayerAccount> Load(string profile) =>
                await repo.LoadAccountAsync(profile) ?? throw new InvalidDataException("account missing");
            static int Bag(PlayerAccount account, int templateId) =>
                account.Bag?.Items.Where(item => item.TemplateId == templateId).Sum(item => item.Num) ?? 0;

            // ── 全关（默认）：与改动前相同的 GM 全量目录，Num/Status/刷新次数恒为 0，购买不限量，RefreshShop 空应答。
            GameServices off = Services("shop-off");
            var offModule = new ShopModule(new ShopService(off), off);
            ModuleResult offInfo = await offModule.HandleAsync(At(off, "shop-off", t0), new TRequest("shop.GetShopsInfo"));
            Dictionary<int, ShopView> offShops = DecodeShops(offInfo.Ret);
            int[] allShopIds = ShopCatalogLoader.GetAllShopIds();
            Assert(offInfo.Err == 0 && offInfo.PrePushes.Count == 0 && offShops.Keys.SequenceEqual(allShopIds) &&
                   offShops.Values.All(shop => shop is { HasRefreshNum: true, RefreshNum: 0, Used: 0, Free: 0, FRefreshTime: 0 } &&
                                               shop.Goods.All(good => good is { Num: 0, Status: 0 })),
                "the default shop list changed beyond the always-encoded RefreshNum");
            foreach (int shopId in allShopIds)
            {
                List<int> gm = off.GmGoodsMap.Values.Where(good => good.ShopId == shopId).Select(good => good.GoodId).ToList();
                Assert(offShops[shopId].Ids.SequenceEqual(gm) && GameServices.GmShopLineup(shopId).SequenceEqual(gm),
                    $"shop {shopId} does not list the GM catalog in order");
            }
            byte[] expectedOff = PlayerDataCodec.Encode(new RetShopsInfo(ShopInfo: allShopIds
                .Select(id => GameServices.GmShopLineup(id) is { Count: > 0 } lineup
                    ? new RetShopInfo(id, lineup.Select(goodId => new ShopGoodsData(goodId, 0, 0)).ToList())
                    : new RetShopInfo(id))
                .ToList()));
            Assert(offInfo.Ret.SequenceEqual(expectedOff), "the default shop list is not the GM catalog encoding");
            for (int i = 0; i < 2; i++)
                Assert((await offModule.HandleAsync(At(off, "shop-off", t0 + i), Buy(19, 61003, 5))).Err == 0,
                    "the default rules limited a purchase");
            Assert((await Load("shop-off")).Shop is null &&
                   ReferenceEquals(await offModule.HandleAsync(At(off, "shop-off", t0 + 5), Refresh(1)), ModuleResult.Empty),
                "the default rules recorded shop state or refreshed a shop");
            IReadOnlyList<byte[]> offSync = await off.BuildSyncPushesAsync("shop-off", checked((uint)(t0 + 6)), CancellationToken.None);
            Assert(PushRet(offSync.Single(push => Method(push) == "shop.UpdateShopInfo")).SequenceEqual(expectedOff),
                "the default login shop push changed");

            // ── 只开「商店真实库存」：GetShopsInfo 先推送；随机商店 10 格；非随机商店与 GM 目录一致。
            GameServices stock = Services("shop-stock", "--real-shop-stock");
            ShopCatalog catalog = ShopCatalogLoader.Catalog;
            var module = new ShopModule(new ShopService(stock, new Random(20_261_009)), stock);
            ModuleResult info = await module.HandleAsync(At(stock, "shop-stock", t0), new TRequest("shop.GetShopsInfo"));
            Assert(info.Err == 0 && info.PrePushes.Count == 1 && Method(info.PrePushes[0]) == "shop.UpdateShopInfo" &&
                   PushRet(info.PrePushes[0]).SequenceEqual(info.Ret),
                "shop.GetShopsInfo did not push the shop list before its response");
            Dictionary<int, ShopView> shops = DecodeShops(info.Ret);
            Assert(shops.Keys.SequenceEqual(allShopIds) && shops.Values.All(shop => shop.HasRefreshNum),
                "the stock shop list does not cover every config shop");
            Assert(shops[1].Goods.Count == 10 && shops[1].Ids.Distinct().Count() == 10 &&
                   shops[1].Ids.All(id => catalog.Good(id)?.ShelfId == 100) && !shops[1].Ids.Contains(5999) &&
                   shops[5].Goods.Count == 10 && shops[5].Ids.Distinct().Count() == 10,
                "random shops 1/5 were not drawn into 10 distinct shelf goods");
            Assert(shops[3].Ids.SequenceEqual(offShops[3].Ids) && shops[7].Ids.SequenceEqual(offShops[7].Ids) &&
                   shops[19].Ids.SequenceEqual([61000, 61002, 61003]) &&
                   shops.Keys.Where(id => catalog.Shop(id) is { IsRandom: false }).All(id => shops[id].Ids.SequenceEqual(offShops[id].Ids)),
                "non-random shops do not keep the GM catalog membership and order");
            Assert(shops[1] is { Used: 0, Free: 0, RefreshNum: 0 } && shops[1].FRefreshTime == t0,
                "shop 1 refresh counters did not start fresh");
            PlayerAccount saved = await Load("shop-stock");
            Assert(ShopStock.Find(saved.Shop, 1)?.Lineup?.SequenceEqual(shops[1].Ids) == true &&
                   ShopStock.Find(saved.Shop, 3) is { RefreshAnchor: var anchor3 } && anchor3 == Utc(2026, 10, 5) &&
                   ShopStock.Find(saved.Shop, 6) is null,
                "shop states were not created lazily for random, scheduled and refreshable shops");

            // 购买：库存递减，推送该商店；超量 / 不在陈列拒绝并重推；shop 19 买空后全部 Status = 1。
            ModuleResult bought = await module.HandleAsync(At(stock, "shop-stock", t0 + 10), Buy(19, 61003, 5));
            List<string> boughtMethods = bought.PrePushes.Select(Method).ToList();
            Assert(bought.Err == 0 && boughtMethods[^1] == "shop.UpdateShopInfo" &&
                   boughtMethods.Contains("user.UpdateUserInfo") && boughtMethods.Contains("bag.UpdateBagData"),
                "a stock purchase did not push the buy data and the shop before its response");
            Dictionary<int, ShopView> afterBuy = DecodeShops(PushRet(bought.PrePushes[^1]));
            Assert(afterBuy.Keys.SequenceEqual([19]) && afterBuy[19].Goods.Single(good => good.Id == 61003) == new GoodView(61003, 5, 1),
                "the shop push did not carry the bought count and the sold-out state");
            ModuleResult over = await module.HandleAsync(At(stock, "shop-stock", t0 + 20), Buy(19, 61003, 1));
            Assert(over.Err == 1 && over.PrePushes.Select(Method).SequenceEqual(["shop.UpdateShopInfo", "user.UpdateUserInfo", "bag.UpdateBagData"]),
                "an over-stock purchase was not rejected with resync pushes");
            Assert((await module.HandleAsync(At(stock, "shop-stock", t0 + 21), Buy(19, 62000, 1))).Err == 1,
                "a good from another shop was sold in shop 19");
            Assert((await module.HandleAsync(At(stock, "shop-stock", t0 + 22), Buy(19, 61000, 10))).Err == 0, "buying 61000 x10 failed");
            ModuleResult lastBuy = await module.HandleAsync(At(stock, "shop-stock", t0 + 23), Buy(19, 61002, 10));
            Assert(lastBuy.Err == 0 && DecodeShops(PushRet(lastBuy.PrePushes[^1]))[19].Goods.All(good => good.Status == 1),
                "buying out shop 19 did not mark every good sold out");

            // 快速购买（shop 1）：两件各买 1，再买拒绝（库存 1）。
            int[] pick = shops[1].Ids.Take(2).ToArray();
            ModuleResult quick = await module.HandleAsync(At(stock, "shop-stock", t0 + 30), QualityBuy(1, pick));
            Assert(quick.Err == 0 &&
                   DecodeShops(PushRet(quick.PrePushes[^1]))[1].Goods.Where(good => pick.Contains(good.Id)).All(good => good is { Num: 1, Status: 1 }),
                "shop.QualityBuyGoods did not count both goods");
            Assert((await module.HandleAsync(At(stock, "shop-stock", t0 + 31), QualityBuy(1, [pick[0]]))).Err == 1,
                "a sold-out quick-buy good was sold again");

            // 刷新 shop 1：免费 → 新陈列、已购清零、UsedFRefreshNum = 1；不能刷新的商店 Err = 1。
            ModuleResult refreshed = await module.HandleAsync(At(stock, "shop-stock", t0 + 40), Refresh(1));
            ShopView refreshedShop = DecodeShop(refreshed.Ret);
            Assert(refreshed.Err == 0 && refreshed.PrePushes.Select(Method).SequenceEqual(["shop.UpdateShopInfo"]) &&
                   refreshedShop is { ShopId: 1, Used: 1, RefreshNum: 0, Goods.Count: 10 } &&
                   refreshedShop.Goods.All(good => good.Num == 0) && !refreshedShop.Ids.SequenceEqual(shops[1].Ids),
                "a free refresh did not redraw shop 1");
            Assert((await module.HandleAsync(At(stock, "shop-stock", t0 + 41), Refresh(3))).Err == 1, "shop 3 was refreshed");
            for (int i = 0; i < 2; i++)
                Assert((await module.HandleAsync(At(stock, "shop-stock", t0 + 42 + i), Refresh(1))).Err == 0, "a free refresh failed");
            ModuleResult unpaid = await module.HandleAsync(At(stock, "shop-stock", t0 + 50), Refresh(1));
            PlayerAccount afterUnpaid = await Load("shop-stock");
            Assert(unpaid.Err == 0 && unpaid.PrePushes.Select(Method).SequenceEqual(["shop.UpdateShopInfo"]) &&
                   DecodeShop(unpaid.Ret) is { Used: 3, RefreshNum: 1 } &&
                   Bag(afterUnpaid, 10303) == 1 && afterUnpaid.Character.Diamond == 60,
                "a paid refresh without the real-cost option charged or did not count");

            // 登录同步推送带库存数据。
            IReadOnlyList<byte[]> sync = await stock.BuildSyncPushesAsync("shop-stock", checked((uint)(t0 + 60)), CancellationToken.None);
            Dictionary<int, ShopView> syncShops = DecodeShops(PushRet(sync.Single(push => Method(push) == "shop.UpdateShopInfo")));
            Assert(syncShops.Keys.SequenceEqual(allShopIds) && syncShops[19].Goods.All(good => good.Status == 1) &&
                   syncShops[1] is { Used: 3, RefreshNum: 1 },
                "the login shop push did not carry the stock state");

            // 每月 1 日：shop 19 补货；跨日后刷新次数归零，免费次数回复到上限。
            ModuleResult november = await module.HandleAsync(At(stock, "shop-stock", Utc(2026, 11, 1)), new TRequest("shop.GetShopsInfo"));
            Dictionary<int, ShopView> nov = DecodeShops(PushRet(november.PrePushes[0]));
            Assert(nov[19].Goods.All(good => good is { Num: 0, Status: 0 }) && nov[1] is { Used: 0, RefreshNum: 0, Free: 3 },
                "the monthly boundary or the daily reset did not apply on the next shop request");

            // 其它协议的结算：跨周一 0 点时补发变化的商店（非随机商店仍是 GM 陈列）。
            long nextMonday = Utc(2026, 11, 2);
            SettlementResult weekly = await stock.SettleAsync("shop-stock", checked((int)nextMonday), CancellationToken.None);
            byte[]? weeklyPush = GameServices.BuildSettlementPostPushes(weekly, checked((uint)nextMonday))
                .SingleOrDefault(push => Method(push) == "shop.UpdateShopInfo");
            Assert(weekly.ShopChangedIds is { } weeklyIds && weeklyIds.Contains(3) && weeklyIds.Contains(18) && !weeklyIds.Contains(19) &&
                   weeklyPush is not null && DecodeShops(PushRet(weeklyPush)) is var weeklyShops &&
                   weeklyShops.Keys.ToHashSet().SetEquals(weeklyIds) && weeklyShops[3].Ids.SequenceEqual(offShops[3].Ids),
                "a weekly boundary crossed by another protocol was not pushed");

            // ── 库存 + 真实消耗：分档价格随已购数前进（5998：15、30、45、60…），刷新按 免费 → 入荷指令 → 钻石 扣费。
            GameServices both = Services("shop-both", "--real-shop-stock", "--real-resource-cost");
            var bothModule = new ShopModule(new ShopService(both, new Random(1)), both);
            for (int i = 0; i < 3; i++)
                Assert((await bothModule.HandleAsync(At(both, "shop-both", t0 + i), Buy(6, 5998, 1))).Err == 0,
                    $"tiered purchase {i + 1} failed");
            PlayerAccount afterTiers = await Load("shop-both");
            Assert(Bag(afterTiers, 13000) == 100 - 15 - 30 - 45 && ShopStock.BoughtCount(afterTiers.Shop, 6, 5998) == 3,
                $"tiered prices did not advance with the bought count (13000 left: {Bag(afterTiers, 13000)})");
            ModuleResult tooPoor = await bothModule.HandleAsync(At(both, "shop-both", t0 + 3), Buy(6, 5998, 1));
            PlayerAccount afterPoor = await Load("shop-both");
            Assert(tooPoor.Err == 1 && Bag(afterPoor, 13000) == 10 && ShopStock.BoughtCount(afterPoor.Shop, 6, 5998) == 3,
                "a purchase priced above the balance was charged or counted");
            for (int i = 0; i < 3; i++)
                Assert((await bothModule.HandleAsync(At(both, "shop-both", t0 + 10 + i), Refresh(1))) is
                       { Err: 0 } free && free.PrePushes.Select(Method).SequenceEqual(["shop.UpdateShopInfo"]),
                    "a free refresh charged something");
            ModuleResult ticketRefresh = await bothModule.HandleAsync(At(both, "shop-both", t0 + 20), Refresh(1));
            PlayerAccount afterTicket = await Load("shop-both");
            Assert(ticketRefresh.Err == 0 && ticketRefresh.PrePushes.Select(Method).SequenceEqual(["shop.UpdateShopInfo", "bag.UpdateBagData"]) &&
                   Bag(afterTicket, 10303) == 0 && afterTicket.Character.Diamond == 60,
                "the refresh ticket was not spent before diamonds");
            ModuleResult diamondRefresh = await bothModule.HandleAsync(At(both, "shop-both", t0 + 21), Refresh(1));
            PlayerAccount afterDiamond = await Load("shop-both");
            Assert(diamondRefresh.Err == 0 && diamondRefresh.PrePushes.Select(Method).SequenceEqual(["shop.UpdateShopInfo", "user.UpdateUserInfo"]) &&
                   afterDiamond.Character.Diamond == 10 && ShopStock.Find(afterDiamond.Shop, 1)!.RefreshNum == 2,
                "the second paid refresh did not cost price[1] = 50 diamonds");
            ModuleResult brokeRefresh = await bothModule.HandleAsync(At(both, "shop-both", t0 + 22), Refresh(1));
            PlayerAccount afterBroke = await Load("shop-both");
            Assert(brokeRefresh.Err == 1 && afterBroke.Character.Diamond == 10 && ShopStock.Find(afterBroke.Shop, 1)!.RefreshNum == 2,
                "a refresh without enough diamonds was not rejected cleanly");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private static long Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds();

    private static GameContext At(GameServices services, string profileId, long now) =>
        new() { ProfileId = profileId, Now = checked((int)now), Ct = CancellationToken.None, Services = services };

    private static TRequest Buy(int shopId, int goodId, int num) => new("shop.BuyGoods",
        new ProtocolPackage().Write(0x08, (ulong)shopId).Write(0x10, (ulong)goodId).Write(0x18, (ulong)num).ToArray());

    private static TRequest QualityBuy(int shopId, IEnumerable<int> goodIds)
    {
        ProtocolPackage package = new ProtocolPackage().Write(0x08, (ulong)shopId);
        foreach (int goodId in goodIds) package.Write(0x10, (ulong)goodId);
        return new TRequest("shop.QualityBuyGoods", package.ToArray());
    }

    private static TRequest Refresh(int shopId) =>
        new("shop.RefreshShop", new ProtocolPackage().Write(0x08, (ulong)shopId).ToArray());

    private static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;

    private static byte[] PushRet(byte[] push) => TMessageCodec.DecodeResponse(push).Ret ?? [];

    private static PlayerShopState Check(ShopBuyCheck check, string what)
    {
        Assert(check.Ok, $"{what} was rejected: {check.Error}");
        return check.State;
    }

    private static bool SameShop(PlayerShop? a, PlayerShop? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Version == b.Version && a.Shops.Count == b.Shops.Count && a.Shops.Zip(b.Shops).All(pair =>
            pair.First.ShopId == pair.Second.ShopId &&
            (pair.First.Lineup ?? []).SequenceEqual(pair.Second.Lineup ?? []) &&
            (pair.First.Lineup is null) == (pair.Second.Lineup is null) &&
            (pair.First.Bought ?? []).SequenceEqual(pair.Second.Bought ?? []) &&
            pair.First.RefreshAnchor == pair.Second.RefreshAnchor && pair.First.DayIndex == pair.Second.DayIndex &&
            pair.First.RefreshNum == pair.Second.RefreshNum && pair.First.UsedFRefreshNum == pair.Second.UsedFRefreshNum &&
            pair.First.FRefreshNum == pair.Second.FRefreshNum && pair.First.FRefreshTime == pair.Second.FRefreshTime);
    }

    /// <summary>解码 TRetShopsInfo（字段 1 为 TRetShopInfo）。</summary>
    private static Dictionary<int, ShopView> DecodeShops(byte[] payload)
    {
        var shops = new Dictionary<int, ShopView>();
        foreach ((int field, int wire, _, byte[] bytes) in Fields(payload))
        {
            if (field != 1 || wire != 2) continue;
            ShopView shop = DecodeShop(bytes);
            shops[shop.ShopId] = shop;
        }
        return shops;
    }

    /// <summary>解码 TRetShopInfo{ShopId=1, RefreshNum=2, ShopGoodsData=3, UsedFRefreshNum=4, FRefreshNum=5, FRefreshTime=6}。</summary>
    private static ShopView DecodeShop(byte[] payload)
    {
        int shopId = 0, refreshNum = 0, used = 0, free = 0, refreshTime = 0;
        bool hasRefreshNum = false;
        var goods = new List<GoodView>();
        foreach ((int field, int wire, ulong value, byte[] bytes) in Fields(payload))
        {
            switch (field)
            {
                case 1 when wire == 0: shopId = (int)value; break;
                case 2 when wire == 0: hasRefreshNum = true; refreshNum = (int)value; break;
                case 3 when wire == 2:
                    int id = 0, num = 0, status = 0;
                    foreach ((int goodField, int goodWire, ulong goodValue, _) in Fields(bytes))
                    {
                        if (goodWire != 0) continue;
                        if (goodField == 1) id = (int)goodValue;
                        else if (goodField == 2) num = (int)goodValue;
                        else if (goodField == 3) status = (int)goodValue;
                    }
                    // 空商店的占位元素（空消息）不算商品。
                    if (id != 0) goods.Add(new GoodView(id, num, status));
                    break;
                case 4 when wire == 0: used = (int)value; break;
                case 5 when wire == 0: free = (int)value; break;
                case 6 when wire == 0: refreshTime = (int)value; break;
            }
        }
        return new ShopView(shopId, goods, hasRefreshNum, refreshNum, used, free, refreshTime);
    }
}
