using System.Text.Json;
using System.Text.Json.Nodes;
using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server;
using BlueOath.Server.Protocols;
using BlueOath.Storage;
using static TestSupport;

/// <summary>
/// 扫荡作战（mopUp.*）的测试：<see cref="PureTest"/> 覆盖完成轮数、结束时刻、每轮燃料、舰队上限与存档；
/// <see cref="ModuleTest"/> 走 MopUpModule 协议（默认规则与「真实消耗资源」两种）。
/// 由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class SweepTests
{
    // Program.cs 的顶层函数 Assert / FindRepositoryRoot 会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private static string FindRepositoryRoot() => TestSupport.FindRepositoryRoot();

    // 测试用关卡（JP 1.4.0 配置）：
    // 5113  海域 8011 的普通关卡，autobattle 360 秒，total_supple_num[6] = 400，没有追击；
    // 5114  同章节，用来占第四支舰队；5112 同章节，用来测「舰队已满」；
    // 25061 海域 5006，追击关卡 35061（total_supple_num[6] = 90），本关 450；两者 period_drop 614 必掉道具 17006；
    // 5101  海域 8010，max_fleet = 3（仍按 6 艘价 400）；
    // 1600100 新海域章节 1001（new_ocean_tag = 1），after_clear_supple_num = 100（total 为 300），autobattle_open = 0；
    // 1670100 海域关卡，autobattle_open = 0；
    // 20101 每日副本（章节 20001），autobattle 180 秒；
    // 20518 每日副本 20001 的条约关卡（只在 treaty_copy 里）。
    private const int NormalCopy = 5113, OtherCopy = 5114, ThirdCopy = 5112, ChaseBase = 25061, ChaseCopy = 35061;
    private const int MaxFleetCopy = 5101, NewOceanCopy = 1600100, NoAutoCopy = 1670100, DailyCopy = 20101, TreatyCopy = 20518;
    private const int SeaRun = 360, DailyRun = 180, Fuel = 5, PeriodDropItem = 17006;

    private sealed record RewardView(int Type, int ConfigId, int Num, int Id);

    private sealed record PassRetView(int CopyId, List<RewardView> Rewards, List<RewardView> Extra);

    private sealed record MopUpView(bool HasFleetsNum, int FleetsNum, List<SweepEntry> Data, List<PassRetView> PassRets);

    public static Task PureTest()
    {
        string configDir = ConfigDbLoader.BuildConfigDir(Path.Combine(FindRepositoryRoot(), "blueoath", "blueoath"));
        SortieCostLoader.Load(configDir);

        // 完成轮数：⌊(now − start) / run⌋ 夹在 [0, n]；结束时刻 = start + n × run；时钟回拨到开始之前为 0。
        const long start = 1_800_000_000;
        var entry = new SweepEntry(1, NormalCopy, 8011, start, SweepLogic.EndTime(start, 3, SeaRun), 3, SeaRun);
        Assert(entry.EndTime == start + 3 * SeaRun && SweepLogic.EndTime(start, 10, DailyRun) == start + 1_800,
            "endTime is not start + n × run_seconds");
        Assert(SweepLogic.CompletedRuns(entry, start) == 0 &&
               SweepLogic.CompletedRuns(entry, start + SeaRun - 1) == 0 &&
               SweepLogic.CompletedRuns(entry, start + SeaRun) == 1 &&
               SweepLogic.CompletedRuns(entry, start + 2 * SeaRun - 1) == 1 &&
               SweepLogic.CompletedRuns(entry, start + 2 * SeaRun) == 2 &&
               SweepLogic.CompletedRuns(entry, entry.EndTime - 1) == 2 &&
               SweepLogic.CompletedRuns(entry, entry.EndTime) == 3 &&
               SweepLogic.CompletedRuns(entry, entry.EndTime + 100 * SeaRun) == 3,
            "CompletedRuns is wrong at the run boundaries");
        Assert(SweepLogic.CompletedRuns(entry, start - 1) == 0 && SweepLogic.CompletedRuns(entry, 0) == 0 &&
               SweepLogic.CompletedRuns(entry, long.MinValue / 2) == 0,
            "a clock rolled back before the start produced completed runs");
        var broken = entry with { RunSeconds = 0 };
        Assert(SweepLogic.CompletedRuns(broken, entry.EndTime - 1) == 0 && SweepLogic.CompletedRuns(broken, entry.EndTime) == 3 &&
               SweepLogic.CompletedRuns(entry with { SweepCounts = 0 }, entry.EndTime) == 0,
            "CompletedRuns did not guard a zero run time or zero runs");

        // 扫荡配置：autobattle_open / autobattle_time 与关卡所属章节。
        Assert(SortieCostLoader.Autobattle(NormalCopy) == (true, SeaRun) && SortieCostLoader.Autobattle(DailyCopy) == (true, DailyRun) &&
               SortieCostLoader.Autobattle(NoAutoCopy) is { Open: false } && SortieCostLoader.Autobattle(999_999_999) is null,
            "autobattle_open / autobattle_time were not loaded from config_copy_display");
        Assert(SortieCostLoader.ChapterOf(NormalCopy) == 8011 && SortieCostLoader.ChapterOf(ChaseBase) == 5006 &&
               SortieCostLoader.ChapterOf(ChaseCopy) == 5006 && SortieCostLoader.ChapterOf(DailyCopy) == 20001 &&
               SortieCostLoader.ChapterOf(TreatyCopy) == 20001 && SortieCostLoader.ChapterOf(999_999_999) == 0,
            "the copy → chapter map does not follow level_list / running_level_list / treaty_copy");

        // 每轮燃料：6 艘出击价；追击关卡再加 total_supple_num[6]；新海域用 after_clear；max_fleet 关卡按 6 艘价。
        Assert(SweepLogic.ChaseCopyId(8011, NormalCopy) == 0 && SweepLogic.ChaseCopyId(5006, ChaseBase) == ChaseCopy &&
               SweepLogic.ChaseCopyId(5006, ChaseCopy) == 0,
            "the chase copy was not resolved through running_level_list");
        Assert(SweepLogic.SupplyPerRun(8011, NormalCopy) == 400, $"normal copy per-run fuel {SweepLogic.SupplyPerRun(8011, NormalCopy)} != 400");
        Assert(SweepLogic.SupplyPerRun(5006, ChaseBase) == 450 + 90,
            $"chase-capable copy per-run fuel {SweepLogic.SupplyPerRun(5006, ChaseBase)} != 450 + 90");
        Assert(SortieCostLoader.Get(NewOceanCopy) is { NewOcean: true } && SweepLogic.SupplyPerRun(1001, NewOceanCopy) == 100,
            $"new-ocean per-run fuel {SweepLogic.SupplyPerRun(1001, NewOceanCopy)} != after_clear 100");
        Assert(SortieCostLoader.Get(MaxFleetCopy) is { MaxFleet: 3 } && SweepLogic.SupplyPerRun(8010, MaxFleetCopy) == 400,
            $"max_fleet per-run fuel {SweepLogic.SupplyPerRun(8010, MaxFleetCopy)} != the 6-ship price 400");

        // 舰队上限：默认 4；「真实消耗资源」下 1 + min(2, 追加艦隊チケット 17106 的张数)。
        PlayerAccount account = PlayerAccountFactory.CreateDefault("sweep-pure", checked((int)start));
        PlayerAccount WithTickets(params int[] stacks) =>
            account with { Bag = new PlayerBag([.. stacks.Select(num => new BagItem(SweepLogic.ExtraFleetTicketId, num)), new BagItem(10303, 9)]) };
        Assert(SweepLogic.FleetsNum(account, false) == 4 && SweepLogic.FleetsNum(WithTickets(7), false) == 4,
            "the default sweep fleet count is not 4");
        Assert(SweepLogic.FleetsNum(account with { Bag = null }, true) == 1 && SweepLogic.FleetsNum(WithTickets(), true) == 1 &&
               SweepLogic.FleetsNum(WithTickets(1), true) == 2 && SweepLogic.FleetsNum(WithTickets(2), true) == 3 &&
               SweepLogic.FleetsNum(WithTickets(1, 1), true) == 3 && SweepLogic.FleetsNum(WithTickets(9), true) == 3 &&
               SweepLogic.FleetsNum(WithTickets(0), true) == 1,
            "the real-cost sweep fleet count is not 1 + min(2, tickets)");

        // 编码：字段 1 总是写出（即使为 0），MopUpData 全部字段写出。
        Assert(SweepLogic.EncodeRet(0, []).SequenceEqual(new byte[] { 0x08, 0x00 }), "sweepFleetsNum 0 was not encoded");
        MopUpView encoded = DecodeMopUp(SweepLogic.EncodeRet(4, [entry with { ChapterId = 0 }]));
        Assert(encoded is { HasFleetsNum: true, FleetsNum: 4, Data.Count: 1, PassRets.Count: 0 } &&
               encoded.Data[0] == entry with { ChapterId = 0, RunSeconds = 0 },
            "TMopUpRet / MopUpData did not round-trip");

        // 存档：带 Sweep 的账号 JSON 往返；没有 sweep 字段的旧档读成 null。
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var charged = new SweepEntry(2, ChaseBase, 5006, start + 5, SweepLogic.EndTime(start + 5, 2, SeaRun), 2, SeaRun, 540, true);
        PlayerAccount withSweep = SweepLogic.Put(SweepLogic.Put(account, entry), charged);
        PlayerAccount back = JsonSerializer.Deserialize<PlayerAccount>(JsonSerializer.Serialize(withSweep, json), json)!;
        Assert(back.Sweep is { Version: 1 } sweep && sweep.Entries.SequenceEqual([entry, charged]),
            "PlayerAccount.Sweep did not survive a JSON round-trip");
        JsonObject legacy = JsonNode.Parse(JsonSerializer.Serialize(account, json))!.AsObject();
        Assert(legacy.Remove("sweep") && JsonSerializer.Deserialize<PlayerAccount>(legacy.ToJsonString(), json)!.Sweep is null,
            "a legacy save without a sweep property did not load");
        Assert(JsonSerializer.Deserialize<PlayerAccount>(JsonSerializer.Serialize(account, json), json)!.Sweep is null,
            "an account without sweeps did not round-trip to null");
        Assert(SweepLogic.RemoveFleet(SweepLogic.RemoveFleet(withSweep, 1), 2).Sweep is null &&
               SweepLogic.RemoveFleet(withSweep, 1).Sweep!.Entries.SequenceEqual([charged]) &&
               SweepLogic.Put(withSweep, entry with { CopyId = OtherCopy }).Sweep!.Entries.Count == 2,
            "Put / RemoveFleet did not key sweeps by fleet");
        return Task.CompletedTask;
    }

    public static async Task ModuleTest()
    {
        string root = FindRepositoryRoot();
        string clientPath = Path.Combine(root, "blueoath", "blueoath");
        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-sweep-" + Guid.NewGuid().ToString("N"));
        long t0 = new DateTimeOffset(2026, 10, 7, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            foreach ((string profile, int supply, int tickets) in new[] { ("sweep-off", 5_000, 0), ("sweep-cost", 1_000, 1) })
            {
                PlayerAccount seed = PlayerAccountFactory.CreateDefault(profile, checked((int)t0));
                await repo.SaveAccountAsync(seed with
                {
                    Character = seed.Character with { Supply = supply },
                    Bag = new PlayerBag(tickets > 0 ? [new BagItem(SweepLogic.ExtraFleetTicketId, tickets)] : []),
                });
            }
            using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
                Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            GameServices Services(string profile, params string[] flags) => new(repo, ServerOptions.Parse(
                [.. new[] { "--no-cheats", "--real-resource-cost=off", "--data=" + dataRoot, "--client-path=" + clientPath, "--profile-id=" + profile }, .. flags]), loggerFactory);
            async Task<PlayerAccount> Load(string profile) =>
                await repo.LoadAccountAsync(profile) ?? throw new InvalidDataException("account missing");
            static MopUpModule Module(GameServices services) =>
                new(new BattleService(services, new DailyCopyService(services)), services);

            // ── 默认规则（不扣燃料，4 支舰队）。
            GameServices off = Services("sweep-off");
            MopUpModule module = Module(off);
            const string P = "sweep-off";

            // 回归：mopUp.CheckSweep 必须带 sweepFleetsNum（原先落到 OfflineStubModule 回空应答，客户端什么都不做）。
            ModuleResult check = await module.HandleAsync(At(off, P, t0), new TRequest("mopUp.CheckSweep"));
            MopUpView checkView = DecodeMopUp(check.Ret);
            Assert(check.Err == 0 && check.Ret.Length >= 2 && check.Ret[0] == 0x08 &&
                   checkView is { HasFleetsNum: true, FleetsNum: 4, Data.Count: 0 },
                "mopUp.CheckSweep did not answer sweepFleetsNum = 4");
            // 路由用真实时钟结算，换一个账号，免得把测试账号的结算时刻推到现在。
            ModuleResult routed = await new MessageRouter(off, [module]).DispatchAsync(
                new TRequest("mopUp.CheckSweep"), "sweep-route", CancellationToken.None);
            Assert(DecodeMopUp(routed.Ret) is { HasFleetsNum: true, FleetsNum: 4 }, "the router did not send mopUp.* to MopUpModule");
            Assert(DecodeMopUp((await module.HandleAsync(At(off, P, t0), new TRequest(SweepLogic.DataMethod))).Ret) is
                   { HasFleetsNum: true, FleetsNum: 4, Data.Count: 0 },
                "a mopUp.GetMopUpData request was not answered with the sweep state");
            int fuelBefore = (await Load(P)).Character.Supply;

            // 开始：应答与随后的 GetMopUpData 推送都是完整列表，结束时刻 = 开始 + n × 360。
            ModuleResult started = await module.HandleAsync(At(off, P, t0), Start(1, NormalCopy, 3));
            MopUpView startView = DecodeMopUp(started.Ret);
            var expected1 = new SweepEntry(1, NormalCopy, 8011, t0, t0 + 3 * SeaRun, 3, 0);
            Assert(started.Err == 0 && startView is { FleetsNum: 4, Data.Count: 1, PassRets.Count: 0 } && startView.Data[0] == expected1,
                "mopUp.StartSweep did not answer the new sweep");
            Assert(Method(started.PostPushes[0]) == SweepLogic.DataMethod &&
                   PushRet(started.PostPushes[0]).SequenceEqual(started.Ret) &&
                   started.PrePushes.All(push => Method(push) != "user.UpdateUserInfo"),
                "mopUp.StartSweep did not post-push GetMopUpData (or pushed user info without charging)");
            PlayerAccount afterStart = await Load(P);
            Assert(afterStart.Sweep!.Entries.Single() == new SweepEntry(1, NormalCopy, 8011, t0, t0 + 3 * SeaRun, 3, SeaRun, 400, false) &&
                   afterStart.Character.Supply == fuelBefore,
                "the sweep was not saved, or fuel changed with the option off");

            // 拒绝：每种都 Err = 1，并推送当前列表纠正客户端，存档不变。
            async Task Rejected(TRequest request, long at, string what)
            {
                ModuleResult rejected = await module.HandleAsync(At(off, P, at), request);
                PlayerAccount saved = await Load(P);
                Assert(rejected.Err == 1 && rejected.Ret.Length == 0 &&
                       rejected.PostPushes.Count >= 1 && Method(rejected.PostPushes[0]) == SweepLogic.DataMethod &&
                       DecodeMopUp(PushRet(rejected.PostPushes[0])).Data.Select(e => e.FleetId)
                           .SequenceEqual(SweepLogic.Entries(saved).Select(e => e.FleetId)),
                    $"{what} was not rejected with a GetMopUpData resync");
            }
            int countBefore = SweepLogic.Entries(await Load(P)).Count;
            await Rejected(Start(2, 999_999_999, 1), t0 + 1, "a missing copy");
            await Rejected(Start(2, NoAutoCopy, 1), t0 + 1, "a copy with autobattle_open = 0");
            await Rejected(Start(2, OtherCopy, 0), t0 + 1, "0 runs");
            await Rejected(Start(2, OtherCopy, 11), t0 + 1, "11 runs");
            await Rejected(Start(0, OtherCopy, 1), t0 + 1, "fleet 0");
            await Rejected(Start(1, OtherCopy, 1), t0 + 1, "a fleet that is already sweeping");
            await Rejected(Start(2, NormalCopy, 1), t0 + 1, "a copy that is already being swept");
            Assert(SweepLogic.Entries(await Load(P)).Count == countBefore, "a rejected start changed the save");

            // 占满 4 支舰队：追击关卡、每日副本、普通关卡；第 5 支被拒。
            Assert((await module.HandleAsync(At(off, P, t0 + 2), Start(2, ChaseBase, 2))).Err == 0, "starting the chase copy failed");
            Assert((await module.HandleAsync(At(off, P, t0 + 3), Start(3, DailyCopy, 3))).Err == 0, "starting the daily copy failed");
            Assert((await module.HandleAsync(At(off, P, t0 + 4), Start(4, OtherCopy, 1))).Err == 0, "starting the fourth fleet failed");
            await Rejected(Start(5, ThirdCopy, 1), t0 + 5, "a fifth fleet");
            Assert((await Load(P)).Character.Supply == fuelBefore, "starting sweeps changed fuel with the option off");

            // 登录同步推送带进行中的扫荡（否则重登后无法领取）。
            IReadOnlyList<byte[]> sync = await off.BuildSyncPushesAsync(P, checked((uint)(t0 + 6)), CancellationToken.None);
            MopUpView syncView = DecodeMopUp(PushRet(sync.Single(push => Method(push) == SweepLogic.DataMethod)));
            Assert(syncView is { FleetsNum: 4, Data.Count: 4, PassRets.Count: 0 } &&
                   syncView.Data.Select(e => e.FleetId).Order().SequenceEqual([1, 2, 3, 4]) &&
                   syncView.Data.Single(e => e.FleetId == 3) is { CopyId: DailyCopy, ChapterId: 20001 } daily3 &&
                   daily3.EndTime == t0 + 3 + 3 * DailyRun,
                "the login sync pushes did not carry the active sweeps");

            // 领取普通关卡：3 轮全部完成 → 3 个 TPassBaseRet（CopyId = 5113），应答为空，先推玩家信息与背包，存档里移除。
            PlayerAccount beforeStop1 = await Load(P);
            ModuleResult stop1 = await module.HandleAsync(At(off, P, t0 + 3 * SeaRun), Stop(1, NormalCopy));
            List<string> pre1 = stop1.PrePushes.Select(Method).ToList();
            Assert(stop1.Err == 0 && stop1.Ret.Length == 0 && pre1.Count >= 2 &&
                   pre1[0] == "user.UpdateUserInfo" && pre1[1] == "bag.UpdateBagData" &&
                   Method(stop1.PostPushes[0]) == SweepLogic.DataMethod,
                $"mopUp.StopSweep pushes are out of order: [{string.Join(", ", pre1)}] / [{string.Join(", ", stop1.PostPushes.Select(Method))}]");
            MopUpView stopView1 = DecodeMopUp(PushRet(stop1.PostPushes[0]));
            Assert(stopView1 is { FleetsNum: 4, Data.Count: 3, PassRets.Count: 3 } &&
                   stopView1.PassRets.All(pass => pass.CopyId == NormalCopy && pass.Extra.Count == 0) &&
                   stopView1.Data.All(e => e.FleetId != 1),
                "collecting 3 finished runs did not push 3 passRets with CopyId and the remaining sweeps");
            PlayerAccount afterStop1 = await Load(P);
            AssertRewardsApplied(beforeStop1, afterStop1, stopView1.PassRets, 0, "the normal copy");
            Assert(SweepLogic.Entries(afterStop1).All(e => e.FleetId != 1) &&
                   afterStop1.SeaProgress?.Records.Single(r => r.CopyId == NormalCopy).PassCount == 3,
                "the collected sweep was not removed or the passes were not recorded");

            // 追击关卡：每轮 Reward 与 ExtraReward（追击关卡掉落）都有 period_drop 614 的道具 17006。
            PlayerAccount beforeChase = await Load(P);
            ModuleResult chase = await module.HandleAsync(At(off, P, t0 + 2 + 2 * SeaRun), Stop(2, ChaseBase));
            MopUpView chaseView = DecodeMopUp(PushRet(chase.PostPushes[0]));
            Assert(chase.Err == 0 && chaseView.PassRets.Count == 2 &&
                   chaseView.PassRets.All(pass => pass.CopyId == ChaseBase &&
                                                  pass.Rewards.Any(r => r.ConfigId == PeriodDropItem) &&
                                                  pass.Extra.Any(r => r.ConfigId == PeriodDropItem)),
                "the chase copy drop was not reported in ExtraReward (field 9)");
            AssertRewardsApplied(beforeChase, await Load(P), chaseView.PassRets, 0, "the chase-capable copy");

            // 每日副本：3 轮全部结算（与手动通关一样不限当日次数），记 3 次挑战并推送每日副本数据。
            ModuleResult dailyStop = await module.HandleAsync(At(off, P, t0 + 3 + 3 * DailyRun), Stop(3, OtherCopy));
            MopUpView dailyView = DecodeMopUp(PushRet(dailyStop.PostPushes[0]));
            PlayerAccount afterDaily = await Load(P);
            Assert(dailyStop.Err == 0 && dailyView.PassRets.Count == 3 && dailyView.PassRets.All(pass => pass.CopyId == DailyCopy) &&
                   dailyStop.PrePushes.Any(push => Method(push) == "dailycopy.UpdateDailyCopyData") &&
                   afterDaily.DailyCopy!.Chapters!.Single(c => c.ChapterId == 20001).ChallengeTimes == 3 &&
                   SweepLogic.Entries(afterDaily).All(e => e.FleetId != 3),
                $"the daily sweep did not credit every completed run ({dailyView.PassRets.Count} runs)");

            // 找不到该舰队的扫荡：不报错，应答与推送都是当前列表。
            ModuleResult none = await module.HandleAsync(At(off, P, t0 + 4_000), Stop(9, NormalCopy));
            Assert(none.Err == 0 && DecodeMopUp(none.Ret) is { FleetsNum: 4, Data.Count: 1 } &&
                   Method(none.PostPushes[0]) == SweepLogic.DataMethod && DecodeMopUp(PushRet(none.PostPushes[0])).PassRets.Count == 0,
                "stopping a fleet without a sweep did not answer the current state");

            // 默认规则：除了掉落里的燃料，燃料不变。
            PlayerAccount beforeLast = await Load(P);
            ModuleResult last = await module.HandleAsync(At(off, P, t0 + 4 + SeaRun), Stop(4, OtherCopy));
            PlayerAccount afterLast = await Load(P);
            Assert(last.Err == 0 && afterLast.Sweep is null &&
                   afterLast.Character.Supply == beforeLast.Character.Supply + RewardSum(DecodeMopUp(PushRet(last.PostPushes[0])).PassRets, 5, Fuel),
                "with the option off, fuel changed beyond the dropped fuel, or the last sweep was not cleared");
            Assert(DecodeMopUp(PushRet(last.PostPushes[0])) is { Data.Count: 0, PassRets.Count: 1 },
                "the final GetMopUpData push did not carry an empty list and the last passRet");

            // ── 「真实消耗资源」：1 + 1 张追加艦隊チケット = 2 支舰队；开始时预扣 n × 每轮燃料，不足拒绝；提前取消退还。
            GameServices cost = Services("sweep-cost", "--real-resource-cost");
            MopUpModule costModule = Module(cost);
            const string C = "sweep-cost";
            Assert(DecodeMopUp((await costModule.HandleAsync(At(cost, C, t0), new TRequest("mopUp.CheckSweep"))).Ret).FleetsNum == 2,
                "the real-cost fleet count did not count the extra fleet ticket");
            ModuleResult paid = await costModule.HandleAsync(At(cost, C, t0), Start(1, NormalCopy, 2));
            PlayerAccount afterPaid = await Load(C);
            Assert(paid.Err == 0 && Method(paid.PrePushes[0]) == "user.UpdateUserInfo" && afterPaid.Character.Supply == 1_000 - 800 &&
                   afterPaid.Sweep!.Entries.Single() is { SupplyPerRun: 400, Charged: true },
                "the real-cost start did not charge 2 × 400 fuel up front");
            ModuleResult poor = await costModule.HandleAsync(At(cost, C, t0 + 1), Start(2, OtherCopy, 1));
            PlayerAccount afterPoor = await Load(C);
            Assert(poor.Err == 1 && Method(poor.PrePushes[0]) == "user.UpdateUserInfo" &&
                   Method(poor.PostPushes[0]) == SweepLogic.DataMethod &&
                   afterPoor.Character.Supply == 200 && SweepLogic.Entries(afterPoor).Count == 1,
                "a start without enough fuel was not rejected cleanly");

            // 提前取消（完成 1 / 2 轮）：结算 1 轮，退还 1 × 400。
            ModuleResult early = await costModule.HandleAsync(At(cost, C, t0 + SeaRun + 10), Stop(1, OtherCopy));
            MopUpView earlyView = DecodeMopUp(PushRet(early.PostPushes[0]));
            PlayerAccount afterEarly = await Load(C);
            Assert(early.Err == 0 && earlyView is { Data.Count: 0, PassRets.Count: 1 } &&
                   afterEarly.Character.Supply == 200 + 400 + RewardSum(earlyView.PassRets, 5, Fuel) && afterEarly.Sweep is null,
                $"an early cancel did not refund the unfinished run (fuel {afterEarly.Character.Supply})");

            // 立即取消（0 轮）：没有 passRets，全额退还。
            int fuelReady = afterEarly.Character.Supply;
            Assert((await costModule.HandleAsync(At(cost, C, t0 + 1_000), Start(2, OtherCopy, 1))).Err == 0 &&
                   (await Load(C)).Character.Supply == fuelReady - 400,
                "the second real-cost start did not charge");
            ModuleResult cancel = await costModule.HandleAsync(At(cost, C, t0 + 1_000), Stop(2, OtherCopy));
            MopUpView cancelView = DecodeMopUp(PushRet(cancel.PostPushes[0]));
            PlayerAccount afterCancel = await Load(C);
            Assert(cancel.Err == 0 && cancelView is { Data.Count: 0, PassRets.Count: 0 } &&
                   afterCancel.Character.Supply == fuelReady && afterCancel.Sweep is null,
                "an immediate cancel did not refund every run or pushed passRets");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private static GameContext At(GameServices services, string profileId, long now) =>
        new() { ProfileId = profileId, Now = checked((int)now), Ct = CancellationToken.None, Services = services };

    private static TRequest Start(int fleetId, int copyId, int counts) => new("mopUp.StartSweep",
        new ProtocolPackage().Write(0x08, (ulong)fleetId).Write(0x10, (ulong)copyId).Write(0x18, unchecked((ulong)counts)).ToArray());

    private static TRequest Stop(int fleetId, int copyId) => new("mopUp.StopSweep",
        new ProtocolPackage().Write(0x08, (ulong)fleetId).Write(0x10, (ulong)copyId).Write(0x18, 1UL).ToArray());

    private static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;

    private static byte[] PushRet(byte[] push) => TMessageCodec.DecodeResponse(push).Ret ?? [];

    /// <summary>passRets 里某种奖励（Reward 与 ExtraReward）的总数。</summary>
    private static long RewardSum(IEnumerable<PassRetView> passRets, int type, int configId) =>
        passRets.SelectMany(pass => pass.Rewards.Concat(pass.Extra))
            .Where(reward => reward.Type == type && reward.ConfigId == configId)
            .Sum(reward => (long)reward.Num);

    /// <summary>
    /// 推送里的奖励与存档变化一致：货币与背包道具按种类求和，装备 / 舰娘按实例数（舰娘自带的默认装备另算）。
    /// <paramref name="refund"/> 是同时退还的燃料。
    /// </summary>
    private static void AssertRewardsApplied(
        PlayerAccount before, PlayerAccount after, IReadOnlyList<PassRetView> passRets, int refund, string what)
    {
        List<RewardView> rewards = passRets.SelectMany(pass => pass.Rewards.Concat(pass.Extra)).ToList();
        foreach (var group in rewards.Where(r => r.Type == 5).GroupBy(r => r.ConfigId))
        {
            GameServices.TryGetCurrency(before, group.Key, out int had);
            GameServices.TryGetCurrency(after, group.Key, out int has);
            long expected = group.Sum(r => (long)r.Num) + (group.Key == Fuel ? refund : 0);
            Assert(has - had == expected, $"{what}: currency {group.Key} changed by {has - had}, rewards say {expected}");
        }
        foreach (var group in rewards.Where(r => r.Type is not (5 or 2 or 3)).GroupBy(r => r.ConfigId))
        {
            long had = before.Bag?.Items.Where(i => i.TemplateId == group.Key).Sum(i => (long)i.Num) ?? 0;
            long has = after.Bag?.Items.Where(i => i.TemplateId == group.Key).Sum(i => (long)i.Num) ?? 0;
            Assert(has - had == group.Sum(r => (long)r.Num), $"{what}: bag item {group.Key} changed by {has - had}");
        }
        int ships = rewards.Count(r => r.Type == 3);
        Assert(after.Dock.Heroes.Count - before.Dock.Heroes.Count == ships, $"{what}: dropped ships were not added");
        Assert((after.Equip?.Items.Count ?? 0) - (before.Equip?.Items.Count ?? 0) >= rewards.Count(r => r.Type == 2),
            $"{what}: dropped equipment was not added");
    }

    /// <summary>解码 TMopUpRet{sweepFleetsNum(1), data(2), passRets(3)}；MopUpData 转成 RunSeconds = 0 的 SweepEntry。</summary>
    private static MopUpView DecodeMopUp(byte[] payload)
    {
        bool hasFleetsNum = false;
        int fleetsNum = 0;
        var data = new List<SweepEntry>();
        var passRets = new List<PassRetView>();
        foreach ((int field, int wire, ulong value, byte[] bytes) in Fields(payload))
        {
            if (field == 1 && wire == 0)
            {
                hasFleetsNum = true;
                fleetsNum = (int)value;
            }
            else if (field == 2 && wire == 2)
            {
                int fleetId = 0, copyId = 0, counts = 0, chapterId = 0;
                long startTime = 0, endTime = 0;
                foreach ((int f, int w, ulong v, _) in Fields(bytes))
                {
                    if (w != 0) continue;
                    switch (f)
                    {
                        case 1: fleetId = (int)v; break;
                        case 2: copyId = (int)v; break;
                        case 3: startTime = (long)v; break;
                        case 4: endTime = (long)v; break;
                        case 5: counts = (int)v; break;
                        case 6: chapterId = (int)v; break;
                    }
                }
                data.Add(new SweepEntry(fleetId, copyId, chapterId, startTime, endTime, counts, 0));
            }
            else if (field == 3 && wire == 2)
            {
                int copyId = 0;
                var rewards = new List<RewardView>();
                var extra = new List<RewardView>();
                foreach ((int f, int w, ulong v, byte[] b) in Fields(bytes))
                {
                    if (f == 12 && w == 0) copyId = (int)v;
                    else if (f == 1 && w == 2) rewards.Add(DecodeReward(b));
                    else if (f == 9 && w == 2) extra.Add(DecodeReward(b));
                }
                passRets.Add(new PassRetView(copyId, rewards, extra));
            }
        }
        return new MopUpView(hasFleetsNum, fleetsNum, data, passRets);
    }

    /// <summary>解码 TCommonReward{Type(1), ConfigId(2), Num(3), Id(4)}。</summary>
    private static RewardView DecodeReward(byte[] payload)
    {
        int type = 0, configId = 0, num = 0, id = 0;
        foreach ((int field, int wire, ulong value, _) in Fields(payload))
        {
            if (wire != 0) continue;
            switch (field)
            {
                case 1: type = (int)value; break;
                case 2: configId = (int)value; break;
                case 3: num = (int)value; break;
                case 4: id = (int)value; break;
            }
        }
        return new RewardView(type, configId, num, id);
    }
}
