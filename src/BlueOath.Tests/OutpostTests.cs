using System.Text.Json;
using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server;
using BlueOath.Server.Protocols;
using BlueOath.Storage;
using static TestSupport;

/// <summary>
/// アンブラ前哨（outpost.*）按时间产出与加速的测试：<see cref="PureTest"/> 覆盖配置、TimeSettlement 第 5 步
/// （产出、驻守心情、自动入浴、成员规范化、每日次数归零）、加速、领取、派驻、编码与存档；
/// <see cref="ModuleTest"/> 走 OutpostModule 协议（默认规则与「扫荡跳过时间」两种）。
/// 由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class OutpostTests
{
    // Program.cs 的顶层函数 Assert / FindRepositoryRoot 会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private static string FindRepositoryRoot() => TestSupport.FindRepositoryRoot();

    // 2027-01-15 08:00:00 UTC（UTC+8 16:00），整点；下一个 UTC+8 零点是 T0 + 28800。
    private const long T0 = 1_800_000_000;
    private const long NextDay = T0 + 28_800;
    private const int Gold = 1, Fuel = 5, Hexa = 11026, W66 = 11030;

    private sealed record BuildingView(int Id, int Level, List<uint> Heroes, int State, int UseCoin, List<OutpostItem> Items);

    public static Task PureTest()
    {
        string configDir = ConfigDbLoader.BuildConfigDir(Path.Combine(FindRepositoryRoot(), "blueoath", "blueoath"));
        OutpostLevelLoader.Load(configDir);
        ParameterCatalogLoader.Load(configDir);

        // 配置：日服 1.4.0 的 config_outpost_level / config_outpost_info / config_parameter。
        var level1 = OutpostLevelLoader.Get(1, 1);
        Assert(level1 is { ShipNum: 1, SpeedupCost: [5, 5, 1500], SpeedupReward: [150, 24] } &&
               level1.Reward!.Any(entry => entry.SequenceEqual(new long[] { 5, 1, 34_722_222 })) &&
               level1.Reward!.Any(entry => entry.SequenceEqual(new long[] { 1, Hexa, 55_555 })) &&
               level1.DropLevel![^1].SequenceEqual(new long[] { 200_000, 150 }),
            "config_outpost_level outpost 1 level 1 was not loaded");
        Assert(OutpostLevelLoader.Get(1, 0)?.ShipNum == 0 && OutpostLevelLoader.Get(1, 6)?.ShipNum == 6 &&
               OutpostLevelLoader.Get(10, 6) is { SpeedupReward: [600, 24] } && OutpostLevelLoader.Get(1, 7) is null,
            "outpost levels 0..6 or the outpost 10 speed-up rate mismatch");
        Assert(OutpostLevelLoader.MoodCost(1) == 10_500 && OutpostLevelLoader.MoodCost(10) == 10_500,
            "config_outpost_info.mood_cost was not loaded");
        SettlementRules config = SettlementRules.FromConfig();
        Assert(config is { OutpostRefillCoins: 50, OutpostRefillMood: 600_000, OutpostSpeedUpLimit: 2, OutpostRefillLimit: 999, OutpostInstant: false },
            "config_parameter 406 / 407 / 409 did not reach the settlement rules");
        SettlementRules rules = config with { BuildingInfo = _ => null, MaxWorkerStrength = _ => 100 };
        SettlementRules instant = rules with { OutpostInstant = true };

        // 新档：10 个前哨都没人驻守，结算不改档；编码为空闲（State 2）。
        PlayerAccount fresh = PlayerAccountFactory.CreateDefault("outpost-fresh", (int)T0) with { Building = null };
        SettlementResult freshSettled = TimeSettlement.Settle(fresh, T0 + 86_400, rules);
        Assert(!freshSettled.Changed && ReferenceEquals(freshSettled.Account, fresh), "an empty outpost changed the save");
        Assert(DecodeOutpost(ProtocolEncoder.EncodeOutPostInfo(fresh.Outpost)).Buildings.All(b => b.State == OutpostProduction.Idle && b.Level == 1),
            "outposts without heroes are not encoded as idle");

        // 产出：倍率 100% 下每秒 reward / 1e8，整件进 ItemInfo，小数结转；驻守舰娘每 600 秒扣 10500。
        PlayerAccount working = Account([NewHero(11, 1_500_000)], [Post(1, [11], updateTime: T0)]);
        SettlementResult hour = TimeSettlement.Settle(working, T0 + 3600, rules);
        PlayerOutpostBuilding post1 = PostOf(hour.Account, 1);
        Assert(post1.ItemInfo!.SequenceEqual([new OutpostItem(1, Hexa, 1), new OutpostItem(5, Gold, 1249)]),
            $"one hour of outpost 1 produced [{Describe(post1.ItemInfo)}]");
        Assert(post1 is { UpdateTime: T0 + 3600, State: OutpostProduction.Working } &&
               post1.Progress!.Single(p => p.ConfigId == Gold && p.Type == 5).Amount is > 0.9999 and < 1 &&
               post1.Progress!.Any(p => p.ConfigId == W66),
            "the outpost anchor, state or carried fractions are wrong after an hour");
        Assert(HeroOf(hour.Account, 11).Mood == 1_437_000 && hour.ChangedHeroIds.SetEquals([11u]) &&
               hour is { OutpostChanged: true, OutpostTouched: true, CurrencyChanged: false },
            "the stationed hero did not pay 10500 per 600 seconds");
        PlayerOutpostBuilding second = PostOf(TimeSettlement.Settle(hour.Account, T0 + 3601, rules).Account, 1);
        Assert(second.ItemInfo!.SequenceEqual([new OutpostItem(1, Hexa, 2), new OutpostItem(5, Gold, 1250)]),
            "the carried fractions were not paid out on the next second");

        // 幂等、时钟回拨与分段结算。
        SettlementResult again = TimeSettlement.Settle(hour.Account, T0 + 3600, rules);
        Assert(!again.Changed && ReferenceEquals(again.Account, hour.Account), "settling the same instant twice changed the outpost");
        Assert(!TimeSettlement.Settle(hour.Account, T0 + 100, rules).Changed, "a clock rollback produced outpost output");
        PlayerAccount twoStep = TimeSettlement.Settle(TimeSettlement.Settle(working, T0 + 1800, rules).Account, T0 + 3600, rules).Account;
        Assert(PostOf(twoStep, 1).ItemInfo!.SequenceEqual(post1.ItemInfo!) && HeroOf(twoStep, 11).Mood == 1_437_000,
            "piecewise outpost settlement diverged from a single settlement");

        // 心情耗尽：先自然恢复（20 个周期 +2000），再扣 7200 秒 × 17.5；只在心情大于 0 的 5828 秒里产出，之后停止。
        PlayerAccount tired = Account([NewHero(11, 100_000)], [Post(1, [11], updateTime: T0)], bathCoins: 30);
        SettlementResult drained = TimeSettlement.Settle(tired, T0 + 7200, rules);
        Assert(HeroOf(drained.Account, 11) is { Mood: 0, UpdateTime: (int)(T0 + 7200) } &&
               PostOf(drained.Account, 1) is { State: OutpostProduction.Stopped } stopped &&
               stopped.ItemInfo!.SequenceEqual([new OutpostItem(1, Hexa, 3), new OutpostItem(5, Gold, 2023)]),
            "an exhausted outpost kept producing or did not stop");
        Assert(DecodeOutpost(ProtocolEncoder.EncodeOutPostInfo(drained.Account.Outpost)).Buildings[0].State == OutpostProduction.Stopped,
            "a stopped outpost is not encoded as State 0");
        // 自动入浴：心情归零时扣 50 温泉币回复 60；温泉币不够时与关闭相同。
        PlayerAccount coined = tired with { Outpost = tired.Outpost! with { Buildings = [Post(1, [11], updateTime: T0, useCoin: 1), .. tired.Outpost!.Buildings.Skip(1)] } };
        SettlementResult refilled = TimeSettlement.Settle(coined with { Character = coined.Character with { Bath = 120 } }, T0 + 7200, rules);
        Assert(HeroOf(refilled.Account, 11).Mood == 576_000 && refilled.Account.Character.Bath == 70 && refilled.CurrencyChanged &&
               PostOf(refilled.Account, 1) is { State: OutpostProduction.Working } refilledPost &&
               refilledPost.ItemInfo!.SequenceEqual([new OutpostItem(1, Hexa, 3), new OutpostItem(5, Gold, 2499)]),
            "the bath-coin refill did not restore 60 mood for 50 coins");
        SettlementResult poor = TimeSettlement.Settle(coined, T0 + 7200, rules);
        Assert(HeroOf(poor.Account, 11).Mood == 0 && poor.Account.Character.Bath == 30 && !poor.CurrencyChanged &&
               PostOf(poor.Account, 1).State == OutpostProduction.Stopped,
            "a refill was paid without enough bath coins");
        // 传入业务后的账号时补发温泉币余额；不传时只推前哨快照（不能用业务前的账号覆盖应答前的货币推送）。
        Assert(GameServices.BuildSettlementPostPushes(refilled, (uint)(T0 + 7200), current: refilled.Account).Select(Method)
                   .SequenceEqual(["user.UpdateUserInfo", "outpost.UpdateOutPostInfo"]) &&
               GameServices.BuildSettlementPostPushes(refilled, (uint)(T0 + 7200)).Select(Method)
                   .SequenceEqual(["outpost.UpdateOutPostInfo"]) &&
               !GameServices.BuildSettlementPostPushes(refilled, (uint)(T0 + 7200), includeOutpost: false).Any(),
            "settlement post-pushes do not carry the outpost snapshot and the bath-coin balance");

        // 「心情」作弊：不扣心情，整段时间都在产出。
        SettlementResult noMood = TimeSettlement.Settle(tired, T0 + 7200, rules with { OmitMoodCost = true });
        Assert(HeroOf(noMood.Account, 11).Mood == 100_000 &&
               PostOf(noMood.Account, 1).ItemInfo!.Contains(new OutpostItem(5, Gold, 2499)),
            "the mood cheat still charged outpost mood");

        // 成员规范化：浴场、基建优先，船坞里没有的、重复的、超出驻守人数的移出。
        PlayerAccount crowded = Account(
            [NewHero(11, 1_500_000), NewHero(12, 1_500_000), NewHero(14, 1_500_000), NewHero(15, 1_500_000)],
            [Post(1, [12, 13, 14, 11], updateTime: T0), Post(2, [11, 15], updateTime: T0, level: 2)],
            building: new PlayerBuilding([new PlayerBuildingEntry(1, 5, 5, [14], LastUpdateTime: T0)], [], WorkerUpdateTime: T0,
                ProductionVersion: PlayerAccountFactory.CurrentProductionVersion),
            bath: new PlayerBath([new BathHero(12, 1, StartTime: T0)], 0));
        SettlementResult normalized = TimeSettlement.Settle(crowded, T0 + 60, rules);
        Assert(PostOf(normalized.Account, 1).HeroIds!.SequenceEqual([11u]) && PostOf(normalized.Account, 2).HeroIds!.SequenceEqual([15u]) &&
               normalized.OutpostChanged,
            "outpost members were not normalized against the bath, buildings, dock and other outposts");

        // 加速次数在 UTC+8 零点归零。
        PlayerAccount spent = working with { Outpost = working.Outpost! with { SpeedUpTime = 2, SpeedUpDay = VowLogic.Day(T0) } };
        Assert(TimeSettlement.Settle(spent, NextDay - 1, rules).Account.Outpost!.SpeedUpTime == 2 &&
               TimeSettlement.Settle(spent, NextDay, rules) is { OutpostChanged: true } reset && reset.Account.Outpost!.SpeedUpTime == 0,
            "the daily speed-up count did not reset at midnight UTC+8");

        // 加速（默认规则）：按 150% × 24 小时立即进背包；每天 2 次；只有工作中的前哨能加速；
        // 「真实消耗资源」（chargeCost）时扣燃料 1500，否则不扣。
        PlayerAccount ready = TimeSettlement.Settle(working, T0, rules).Account;
        Assert(OutpostProduction.SpeedUp(ready, 1, T0, rules) is { Success: true } freeSpeedUp &&
               freeSpeedUp.Account.Character.Supply == 9_999 && freeSpeedUp.Account.Outpost!.SpeedUpTime == 1,
            "a speed-up without real resource cost charged fuel or did not count");
        OutpostProduction.Outcome first = OutpostProduction.SpeedUp(ready, 1, T0, rules, chargeCost: true);
        Assert(first.Success && first.Account.Character.Supply == 9_999 - 1500 &&
               first.Account.Character.Gold == ready.Character.Gold + 44_999 && Bag(first.Account, Hexa) == 71 && Bag(first.Account, W66) == 17 &&
               first.Rewards.Contains(new OutpostItem(5, Gold, 44_999)) && first.Rewards.Contains(new OutpostItem(1, Hexa, 71)) &&
               first is { CurrencyChanged: true, BagChanged: true } &&
               first.Account.Outpost is { SpeedUpTime: 1 } afterFirst && afterFirst.SpeedUpDay == VowLogic.Day(T0) &&
               PostOf(first.Account, 1).ItemInfo is null or { Count: 0 },
            "the speed-up did not pay 150% × 24 hours into the bag for 1500 fuel");
        OutpostProduction.Outcome secondSpeed = OutpostProduction.SpeedUp(first.Account, 1, T0 + 1, rules, chargeCost: true);
        OutpostProduction.Outcome third = OutpostProduction.SpeedUp(secondSpeed.Account, 1, T0 + 2, rules, chargeCost: true);
        Assert(secondSpeed.Success && secondSpeed.Account.Outpost!.SpeedUpTime == 2 &&
               !third.Success && ReferenceEquals(third.Account, secondSpeed.Account),
            "the third speed-up of the day was not rejected");
        PlayerAccount nextDay = TimeSettlement.Settle(secondSpeed.Account, NextDay, rules).Account;
        Assert(OutpostProduction.SpeedUp(nextDay, 1, NextDay, rules) is { Success: true } renewed && renewed.Account.Outpost!.SpeedUpTime == 1,
            "speed-ups did not come back the next day");
        Assert(!OutpostProduction.SpeedUp(ready with { Character = ready.Character with { Supply = 1000 } }, 1, T0, rules, chargeCost: true).Success,
            "a speed-up without 1500 fuel was accepted");
        Assert(!OutpostProduction.SpeedUp(drained.Account, 1, T0 + 7200, rules).Success &&
               !OutpostProduction.SpeedUp(ready, 2, T0, rules).Success,
            "a stopped or empty outpost accepted a speed-up");

        // 「扫荡跳过时间」：不随时间产出、不扣心情、锚点清零；加速不限次数、不消耗，reward 原值进 ItemInfo；当日次数清零。
        SettlementResult frozen = TimeSettlement.Settle(spent, T0 + 3600, instant);
        Assert(PostOf(frozen.Account, 1) is { UpdateTime: 0, ItemInfo: null, State: OutpostProduction.Working } &&
               HeroOf(frozen.Account, 11).Mood == 1_500_000 && frozen.Account.Outpost!.SpeedUpTime == 0 &&
               !TimeSettlement.Settle(frozen.Account, T0 + 7200, instant).Changed,
            "the sweep cheat let the outpost produce over time");
        PlayerAccount free = frozen.Account;
        for (int i = 0; i < 3; i++)
        {
            OutpostProduction.Outcome raw = OutpostProduction.SpeedUp(free, 1, T0 + 3600 + i, instant);
            Assert(raw.Success && raw.Rewards.Contains(new OutpostItem(5, Gold, 34_722_222)) && !raw.CurrencyChanged, $"instant speed-up {i + 1} failed");
            free = raw.Account;
        }
        Assert(free.Character.Supply == 9_999 && free.Outpost!.SpeedUpTime == 0 &&
               PostOf(free, 1).ItemInfo!.Contains(new OutpostItem(5, Gold, 3 * 34_722_222)),
            "instant speed-ups were limited or charged");
        // 关掉作弊后从当时起按真实时间结算（锚点为 0 时从 now 起算）。
        SettlementResult resumed = TimeSettlement.Settle(TimeSettlement.Settle(free, T0 + 7200, rules).Account, T0 + 7200 + 3600, rules);
        Assert(PostOf(resumed.Account, 1).ItemInfo!.Contains(new OutpostItem(5, Gold, 3 * 34_722_222 + 1249)),
            "turning the sweep cheat off back-paid the time it was on");

        // 领取：单个前哨与全部前哨，ItemInfo 清空、小数进度保留。
        OutpostProduction.Outcome receive = OutpostProduction.Receive(hour.Account, 1);
        Assert(receive.Success && receive.Rewards.SequenceEqual(post1.ItemInfo!) &&
               receive.Account.Character.Gold == hour.Account.Character.Gold + 1249 && Bag(receive.Account, Hexa) == 1 &&
               PostOf(receive.Account, 1) is { ItemInfo.Count: 0, Progress.Count: > 0 },
            "receiving outpost 1 did not grant and clear its storage");
        PlayerAccount both = hour.Account with
        {
            Outpost = hour.Account.Outpost! with
            {
                Buildings = hour.Account.Outpost!.Buildings.Select(b => b.Id == 2 ? b with { ItemInfo = [new OutpostItem(5, Gold, 1)] } : b).ToList(),
            },
        };
        OutpostProduction.Outcome all = OutpostProduction.Receive(both, null);
        Assert(all.Rewards.SequenceEqual([new OutpostItem(1, Hexa, 1), new OutpostItem(5, Gold, 1250)]) &&
               all.Account.Outpost!.Buildings.All(b => b.ItemInfo is null or { Count: 0 }),
            "receive-all did not merge and clear every outpost");

        // 派驻：浴场舰娘与超员拒绝；基建与其它前哨的舰娘撤下；没人的前哨锚点清零（编码为空闲）。
        PlayerAccount roster = Account(
            [NewHero(11, 1_500_000), NewHero(12, 1_500_000), NewHero(14, 1_500_000)],
            [Post(1, [11], updateTime: T0)],
            building: new PlayerBuilding([new PlayerBuildingEntry(1, 5, 5, [14], LastUpdateTime: T0)], [], WorkerUpdateTime: T0,
                ProductionVersion: PlayerAccountFactory.CurrentProductionVersion),
            bath: new PlayerBath([new BathHero(12, 1, StartTime: T0)], 0));
        Assert(!OutpostProduction.SetHero(roster, 2, [12], T0, rules).Success &&
               !OutpostProduction.SetHero(roster, 2, [11, 14], T0, rules).Success &&
               !OutpostProduction.SetHero(roster, 2, [99], T0, rules).Success,
            "a bath hero, an unknown hero or too many heroes were stationed");
        OutpostProduction.Outcome moved = OutpostProduction.SetHero(roster, 2, [14], T0 + 10, rules);
        Assert(moved is { Success: true, BuildingChanged: true } && moved.Account.Building!.Buildings[0].HeroIds.Count == 0 &&
               PostOf(moved.Account, 2) is { HeroIds: [14u], UpdateTime: T0 + 10, State: OutpostProduction.Working },
            "a hero from a base building was not moved to the outpost");
        OutpostProduction.Outcome swapped = OutpostProduction.SetHero(moved.Account, 2, [11], T0 + 20, rules);
        Assert(PostOf(swapped.Account, 1) is { HeroIds.Count: 0, UpdateTime: 0 } && PostOf(swapped.Account, 2).HeroIds!.SequenceEqual([11u]) &&
               DecodeOutpost(ProtocolEncoder.EncodeOutPostInfo(swapped.Account.Outpost)).Buildings[0].State == OutpostProduction.Idle,
            "a hero moved between outposts left the old outpost running");
        OutpostProduction.Outcome upgraded = OutpostProduction.Upgrade(swapped.Account, 2);
        Assert(PostOf(upgraded.Account, 2).Level == 2 &&
               OutpostProduction.SetHero(upgraded.Account, 2, [11, 14], T0 + 30, rules).Success,
            "an upgrade did not raise the number of stationed heroes");
        Assert(OutpostProduction.SetUseCoin(tired, 1, 1, T0, rules).Account.Outpost!.Buildings[0].UseCoin == 1,
            "SetUseCoin did not store the switch");

        // 存档：新字段往返；旧档（没有这些字段）按默认值读入。
        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-outpost-pure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            PlayerAccount saved = first.Account with { ProfileId = "outpost-save" };
            repo.SaveAccountAsync(saved).GetAwaiter().GetResult();
            PlayerAccount loaded = repo.LoadAccountAsync("outpost-save").GetAwaiter().GetResult() ?? throw new InvalidDataException("account missing");
            PlayerOutpostBuilding savedPost = PostOf(saved, 1), loadedPost = PostOf(loaded, 1);
            Assert(loaded.Outpost!.SpeedUpTime == 1 && loaded.Outpost.SpeedUpDay == VowLogic.Day(T0) &&
                   loadedPost.UpdateTime == savedPost.UpdateTime && loadedPost.HeroIds!.SequenceEqual(savedPost.HeroIds!) &&
                   loadedPost.Progress!.SequenceEqual(savedPost.Progress!),
                "outpost anchors, fractions or the speed-up day did not survive a save");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
        PlayerOutpost legacy = JsonSerializer.Deserialize<PlayerOutpost>(
            """{"buildings":[{"id":3,"level":2,"heroIds":[5],"state":0,"useCoin":1,"itemInfo":[{"type":5,"configId":1,"num":7}]}],"speedUpTime":0}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert(legacy.SpeedUpDay == 0 && legacy.Buildings[0] is { Id: 3, UpdateTime: 0, Progress: null, UseCoin: 1 } &&
               legacy.Buildings[0].ItemInfo!.Single() == new OutpostItem(5, Gold, 7),
            "a legacy outpost save did not load with default anchors");
        return Task.CompletedTask;
    }

    public static async Task ModuleTest()
    {
        string root = FindRepositoryRoot();
        string clientPath = Path.Combine(root, "blueoath", "blueoath");
        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-outpost-" + Guid.NewGuid().ToString("N"));
        // 2026-10-07 04:00 UTC（UTC+8 12:00）；下一个 UTC+8 零点是 t0 + 43200。
        long t0 = new DateTimeOffset(2026, 10, 7, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            foreach (string profile in new[] { "outpost-off", "outpost-on" })
            {
                PlayerAccount seed = PlayerAccountFactory.CreateDefault(profile, checked((int)t0));
                PlayerBuilding building = seed.Building!;
                await repo.SaveAccountAsync(seed with
                {
                    Dock = new HeroDock([.. seed.Dock.Heroes, NewHero(11, 1_500_000, t0), NewHero(12, 1_500_000, t0)]),
                    Building = building with { Buildings = [building.Buildings[0] with { HeroIds = [12] }, .. building.Buildings.Skip(1)] },
                });
            }
            using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
                Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            GameServices Services(string profile, params string[] flags) => new(repo, ServerOptions.Parse(
                [.. new[] { "--no-cheats", "--data=" + dataRoot, "--client-path=" + clientPath, "--profile-id=" + profile }, .. flags]), loggerFactory);
            async Task<PlayerAccount> Load(string profile) =>
                await repo.LoadAccountAsync(profile) ?? throw new InvalidDataException("account missing");

            // ── 默认规则：按真实时间产出，加速每天 2 次、扣燃料。
            GameServices off = Services("outpost-off");
            Assert(off.SettlementRules.OutpostInstant == false, "the outpost runs instantly without --cheat-sweep");
            var offModule = new OutpostModule(new OutpostService(off), off);
            ModuleResult setHero = await offModule.HandleAsync(At(off, "outpost-off", t0), SetHero(1, 11));
            BuildingView first = Pushed(setHero.PrePushes, 1);
            Assert(setHero.Err == 0 && first is { State: OutpostProduction.Working } && first.Heroes.SequenceEqual([11u]) &&
                   Pushed(setHero.PrePushes, 2).State == OutpostProduction.Idle,
                "stationing a hero did not push a working outpost before the response");
            ModuleResult fromOffice = await offModule.HandleAsync(At(off, "outpost-off", t0 + 10), SetHero(2, 12));
            Assert(fromOffice.Err == 0 && fromOffice.PrePushes.Select(Method).Contains("building.UpdateBuildingInfo") &&
                   (await Load("outpost-off")).Building!.Buildings[0].HeroIds.Count == 0 &&
                   Pushed(fromOffice.PrePushes, 2).Heroes.SequenceEqual([12u]),
                "a hero taken from the office was not removed from it");

            ModuleResult info = await offModule.HandleAsync(At(off, "outpost-off", t0 + 3600), new TRequest("outpost.GetOutPostInfo"));
            List<OutpostItem> stored = Pushed(info.PrePushes, 1).Items;
            Assert(stored.SequenceEqual([new OutpostItem(1, Hexa, 1), new OutpostItem(5, Gold, 1249)]) &&
                   info.PrePushes.Select(Method).Contains("hero.UpdateHeroBagData") &&
                   (await Load("outpost-off")).Dock.Heroes.Single(h => h.HeroId == 11).Mood == 1_437_000,
                "an hour of outpost production or the hero mood was not settled and pushed");

            PlayerAccount beforeReceive = await Load("outpost-off");
            ModuleResult received = await offModule.HandleAsync(At(off, "outpost-off", t0 + 3600), Req("outpost.ReceiveItem", 1));
            PlayerAccount afterReceive = await Load("outpost-off");
            Assert(received.Err == 0 && DecodeRewards(received.Ret).SequenceEqual(stored) &&
                   afterReceive.Character.Gold == beforeReceive.Character.Gold + 1249 &&
                   Pushed(received.PrePushes, 1).Items.Count == 0 &&
                   received.PrePushes.Select(Method).Intersect(["user.UpdateUserInfo", "outpost.UpdateOutPostInfo"]).Count() == 2,
                "receiving the outpost did not grant, clear and push");

            int supply = afterReceive.Character.Supply;
            for (int i = 1; i <= 2; i++)
            {
                ModuleResult speed = await offModule.HandleAsync(At(off, "outpost-off", t0 + 3600 + i), Req("outpost.SpeedUpProduction", 1));
                List<OutpostItem> rewards = DecodeRewards(speed.Ret);
                PlayerAccount now = await Load("outpost-off");
                Assert(speed.Err == 0 && rewards.Any(r => r is { Type: 5, ConfigId: Gold, Num: >= 44_999 }) &&
                       rewards.Any(r => r is { Type: 1, ConfigId: Hexa, Num: >= 71 }) &&
                       now.Character.Supply == supply && now.Outpost!.SpeedUpTime == i &&
                       DecodeOutpost(PushRet(speed.PrePushes.Last(p => Method(p) == "outpost.UpdateOutPostInfo"))).SpeedUpTime == i,
                    $"speed-up {i} charged fuel without real resource cost, or did not pay the bag and count");
            }
            ModuleResult refused = await offModule.HandleAsync(At(off, "outpost-off", t0 + 3610), Req("outpost.SpeedUpProduction", 1));
            Assert(refused.Err != 0 && refused.Ret.Length == 0 && (await Load("outpost-off")).Character.Supply == supply &&
                   refused.PrePushes.Select(Method).Contains("outpost.UpdateOutPostInfo"),
                "a third speed-up in one day was accepted");
            ModuleResult tomorrow = await offModule.HandleAsync(At(off, "outpost-off", t0 + 43_200), new TRequest("outpost.GetOutPostInfo"));
            Assert(DecodeOutpost(PushRet(tomorrow.PrePushes.Last(p => Method(p) == "outpost.UpdateOutPostInfo"))).SpeedUpTime == 0,
                "the speed-up count did not reset at midnight UTC+8");

            // ── 「扫荡跳过时间」：不随时间产出，加速不限次数、不消耗。
            GameServices on = Services("outpost-on", "--cheat-sweep");
            Assert(on.SettlementRules.OutpostInstant, "--cheat-sweep did not reach the settlement rules");
            var onModule = new OutpostModule(new OutpostService(on), on);
            Assert((await onModule.HandleAsync(At(on, "outpost-on", t0), SetHero(1, 11))).Err == 0, "stationing failed under the sweep cheat");
            ModuleResult onInfo = await onModule.HandleAsync(At(on, "outpost-on", t0 + 3600), new TRequest("outpost.GetOutPostInfo"));
            Assert(Pushed(onInfo.PrePushes, 1) is { Items.Count: 0, State: OutpostProduction.Working } &&
                   (await Load("outpost-on")).Dock.Heroes.Single(h => h.HeroId == 11).Mood == 1_500_000,
                "the sweep cheat produced over time or charged mood");
            int onSupply = (await Load("outpost-on")).Character.Supply;
            for (int i = 0; i < 3; i++)
            {
                ModuleResult raw = await onModule.HandleAsync(At(on, "outpost-on", t0 + 3601 + i), Req("outpost.SpeedUpProduction", 1));
                Assert(raw.Err == 0 && DecodeRewards(raw.Ret).Contains(new OutpostItem(5, Gold, 34_722_222)), $"instant speed-up {i + 1} failed");
            }
            PlayerAccount onAccount = await Load("outpost-on");
            Assert(onAccount.Character.Supply == onSupply && onAccount.Outpost!.SpeedUpTime == 0 &&
                   PostOf(onAccount, 1).ItemInfo!.Contains(new OutpostItem(5, Gold, 3 * 34_722_222)),
                "instant speed-ups were limited, charged or not stored");
            long gold = onAccount.Character.Gold;
            ModuleResult takeAll = await onModule.HandleAsync(At(on, "outpost-on", t0 + 3700), new TRequest("outpost.ReceiveAll"));
            Assert(takeAll.Err == 0 && (await Load("outpost-on")).Character.Gold == gold + 3 * 34_722_222,
                "receive-all did not grant the instant speed-ups");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private static Hero NewHero(uint heroId, int mood, long at = T0) =>
        new(heroId, PlayerAccountFactory.DefaultHeroTemplateId, 1, CreateTime: (int)at, UpdateTime: (int)at, Mood: mood);

    private static PlayerOutpostBuilding Post(int id, IReadOnlyList<uint> heroes, long updateTime = 0, int level = 1, int useCoin = 0) =>
        new(id, level, heroes, State: heroes.Count > 0 ? OutpostProduction.Working : 0, UseCoin: useCoin, UpdateTime: updateTime);

    private static PlayerAccount Account(
        IReadOnlyList<Hero> heroes, IReadOnlyList<PlayerOutpostBuilding> outposts, int bathCoins = 0,
        PlayerBuilding? building = null, PlayerBath? bath = null)
    {
        PlayerAccount account = PlayerAccountFactory.CreateDefault("outpost-pure", (int)T0);
        List<PlayerOutpostBuilding> all = PlayerAccountFactory.DefaultOutpost().Buildings
            .Select(b => outposts.FirstOrDefault(o => o.Id == b.Id) ?? b)
            .ToList();
        return account with
        {
            Character = account.Character with { SecretaryId = 999, Bath = bathCoins, Supply = 9_999 },
            Dock = new HeroDock(heroes),
            Building = building,
            Bath = bath,
            Outpost = new PlayerOutpost(all),
        };
    }

    private static PlayerOutpostBuilding PostOf(PlayerAccount account, int id) => account.Outpost!.Buildings.Single(b => b.Id == id);

    private static Hero HeroOf(PlayerAccount account, uint heroId) => account.Dock.Heroes.Single(hero => hero.HeroId == heroId);

    private static int Bag(PlayerAccount account, int templateId) =>
        account.Bag?.Items.Where(item => item.TemplateId == templateId).Sum(item => item.Num) ?? 0;

    private static string Describe(IReadOnlyList<OutpostItem>? items) =>
        string.Join(",", (items ?? []).Select(item => $"{item.Type}:{item.ConfigId}x{item.Num}"));

    private static GameContext At(GameServices services, string profileId, long now) =>
        new() { ProfileId = profileId, Now = checked((int)now), Ct = CancellationToken.None, Services = services };

    private static TRequest SetHero(int buildingId, params uint[] heroIds)
    {
        ProtocolPackage package = new ProtocolPackage().Write(0x08, (ulong)buildingId);
        foreach (uint heroId in heroIds) package.Write(0x10, heroId);
        return new TRequest("outpost.SetHero", package.ToArray());
    }

    private static TRequest Req(string method, int buildingId) =>
        new(method, new ProtocolPackage().Write(0x08, (ulong)buildingId).ToArray());

    private static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;

    private static byte[] PushRet(byte[] push) => TMessageCodec.DecodeResponse(push).Ret ?? [];

    /// <summary>最后一条 outpost.UpdateOutPostInfo 推送里的某个前哨。</summary>
    private static BuildingView Pushed(IReadOnlyList<byte[]> pushes, int id) =>
        DecodeOutpost(PushRet(pushes.Last(push => Method(push) == "outpost.UpdateOutPostInfo"))).Buildings.Single(b => b.Id == id);

    /// <summary>解码 TOutPostInfo{BuildingInfos=1, SpeedUpTime=2}。</summary>
    private static (List<BuildingView> Buildings, int SpeedUpTime) DecodeOutpost(byte[] payload)
    {
        var buildings = new List<BuildingView>();
        int speedUpTime = 0;
        foreach ((int field, int wire, ulong value, byte[] bytes) in Fields(payload))
        {
            if (field == 2 && wire == 0) speedUpTime = (int)value;
            if (field != 1 || wire != 2) continue;
            int id = 0, level = 0, state = 0, useCoin = 0;
            var heroes = new List<uint>();
            var items = new List<OutpostItem>();
            foreach ((int f, int w, ulong v, byte[] b) in Fields(bytes))
            {
                switch (f)
                {
                    case 1 when w == 0: id = (int)v; break;
                    case 2 when w == 0: level = (int)v; break;
                    case 3 when w == 0: heroes.Add((uint)v); break;
                    case 4 when w == 0: state = (int)v; break;
                    case 5 when w == 0: useCoin = (int)v; break;
                    case 6 when w == 2: items.Add(DecodeReward(b)); break;
                }
            }
            buildings.Add(new BuildingView(id, level, heroes, state, useCoin, items));
        }
        return (buildings, speedUpTime);
    }

    /// <summary>解码 TOPReceiveRet{ItemInfo=1}。</summary>
    private static List<OutpostItem> DecodeRewards(byte[] payload) =>
        Fields(payload).Where(f => f.Field == 1 && f.Wire == 2).Select(f => DecodeReward(f.Bytes)).ToList();

    /// <summary>解码 TCommonReward{Type=1, ConfigId=2, Num=3}。</summary>
    private static OutpostItem DecodeReward(byte[] payload)
    {
        int type = 0, configId = 0, num = 0;
        foreach ((int field, int wire, ulong value, _) in Fields(payload))
        {
            if (wire != 0) continue;
            if (field == 1) type = (int)value;
            else if (field == 2) configId = (int)value;
            else if (field == 3) num = (int)value;
        }
        return new OutpostItem(type, configId, num);
    }
}
