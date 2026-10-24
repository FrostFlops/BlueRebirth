using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server;
using BlueOath.Server.Configs;
using BlueOath.Server.Protocols;
using BlueOath.Storage;
using static TestSupport;

/// <summary>
/// 杂志（SK BOOK，magazine.*）的测试：<see cref="PureTest"/> 覆盖目录筛选、活动期判断、编解码（显式写出的 0）、
/// task.TaskInfo 字段 14 与栞解锁的纯函数；<see cref="ModuleTest"/> 走 MagazineModule 与登录同步推送（作弊开 / 关）；
/// <see cref="IntegrationTest"/> 起真实服务端走 TCP。客户端路径可用环境变量 BLUEOATH_CLIENT_PATH 指向未热更的 1.4.0。
/// 由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class MagazineTests
{
    // Program.cs 的顶层函数 Assert / FindRepositoryRoot 会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private static string FindRepositoryRoot() => TestSupport.FindRepositoryRoot();

    // 日服配置（热更与未热更两版的四张表相同）：10 期，4、6 是特别号，最新号是 10（order 最大）；
    // 第 1 期的周期 20001 从 2020-06-02 00:00（客户端时区 JST）开始，duration_list = [2159999, 432000, 312768001]，
    // task_period_area = [1, 2]；第 10 期的周期 20010 从 2021-03-02 开始。栞是 18006，第 1 期的印章是 18004。
    private const int Bookmark = 18006, FirstStamp = 18004, ExpectedTasks = 306;
    private static readonly int[] ExpectedIssues = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    private static readonly long FirstStart = new DateTimeOffset(2020, 6, 2, 0, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeSeconds();
    private static readonly long LatestStart = new DateTimeOffset(2021, 3, 2, 0, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeSeconds();
    private static readonly long T0 = new DateTimeOffset(2026, 10, 23, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static string ClientPath() =>
        Environment.GetEnvironmentVariable("BLUEOATH_CLIENT_PATH") is { Length: > 0 } clientPath
            ? clientPath
            : Path.Combine(FindRepositoryRoot(), "blueoath", "blueoath");

    public static Task PureTest()
    {
        // 「杂志全部解锁」作弊：默认开，--cheat-magazine=off / --no-cheats 关闭，算作作弊。
        CheatOptions parsed = ServerOptions.Parse(["--no-cheats", "--real-resource-cost=off", "--CHEAT-MAGAZINE"]).Cheats;
        Assert(parsed == new CheatOptions(Magazine: true) && parsed.Any && CheatOptions.Default.Magazine &&
               !ServerOptions.Parse(["--cheat-magazine=off"]).Cheats.Magazine &&
               !ServerOptions.Parse(["--cheat-magazine", "--no-cheats"]).Cheats.Magazine &&
               ServerOptions.Parse(["--no-cheats", "--cheat-magazine=on"]).Cheats.Magazine,
            "--cheat-magazine did not parse, default on, or turn off with --no-cheats");

        string configDir = ConfigDbLoader.BuildConfigDir(ClientPath());
        MagazineTables tables = MagazineTables.Load(configDir);
        MagazineCatalog catalog = MagazineCatalog.Build(tables);

        // 目录：全部 is_open 期刊都齐全；任务 = 每期 task_left/right 与各页 task_id 的并集，各一条，EventType = goal[0]。
        Assert(catalog.ListedIds.SequenceEqual(ExpectedIssues) && catalog.Rejected.Count == 0,
            $"the JP catalog listed [{string.Join(",", catalog.ListedIds)}], rejected [{string.Join("; ", catalog.Rejected)}]");
        Assert(catalog.Issues.Where(issue => issue.Special).Select(issue => issue.Id).SequenceEqual([4, 6]) &&
               catalog.Latest is { Id: 10 },
            "special issues are not 4 and 6, or the latest issue is not 10");
        HashSet<int> referenced = [];
        foreach (ConfigMagazineInfo info in tables.Infos.Values.Where(info => info.IsOpen == 1))
        {
            referenced.UnionWith((info.TaskLeftId ?? []).Concat(info.TaskRightId ?? []).Select(id => (int)id));
            foreach (long page in info.PageId ?? [])
                referenced.UnionWith((tables.Pages[(int)page].TaskId ?? []).Select(id => (int)id));
        }
        List<int> definitionIds = catalog.TaskDefinitions.Select(definition => definition.Id).ToList();
        Assert(definitionIds.Count == ExpectedTasks && definitionIds.Distinct().Count() == ExpectedTasks &&
               referenced.SetEquals(definitionIds) &&
               catalog.Issues.All(issue => issue.TaskIds.All(referenced.Contains)),
            $"the catalog tasks ({definitionIds.Count}) are not the {ExpectedTasks} tasks the listed issues reference");
        Assert(catalog.TaskDefinitions.All(definition =>
                   definition.TaskType == TaskConfigCatalog.TypeMagazine &&
                   definition.EventType == tables.Tasks[definition.Id].Goal![0]),
            "a magazine task definition is not TaskType 11 with EventType = goal[0]");
        Assert(catalog.UnlockCost == new CostItem(1, Bookmark, 1) &&
               MagazineCatalog.Build(tables with { UnlockCost = [] }).UnlockCost == MagazineCatalog.DefaultUnlockCost,
            "the bookmark cost is not config_parameter 365 = [1, 18006, 1] (with that as the fallback)");

        // 缺页、缺任务、缺任务奖励、缺标签的期刊整期不列，它的任务也不进字段 14；is_open = 0 的期刊不列也不报告。
        MagazineCatalog withoutPage = MagazineCatalog.Build(tables with { Pages = Without(tables.Pages, 1) });
        Assert(!withoutPage.ListedIds.Contains(1) && withoutPage.ListedIds.Count == ExpectedIssues.Length - 1 &&
               withoutPage.Rejected.Single().StartsWith("magazine 1:", StringComparison.Ordinal) &&
               withoutPage.TaskDefinitions.All(definition => !catalog.Issues[0].TaskIds.Contains(definition.Id)),
            "an issue with a missing page was still listed, or its tasks were still sent");
        int firstPageTask = (int)tables.Pages[1].TaskId![0];
        Assert(!MagazineCatalog.Build(tables with { Tasks = Without(tables.Tasks, firstPageTask) }).ListedIds.Contains(1),
            "an issue with a missing task was still listed");
        int firstPageReward = (int)tables.Tasks[firstPageTask].Rewards;
        Assert(!MagazineCatalog.Build(tables with { RewardIds = tables.RewardIds.Where(id => id != firstPageReward).ToHashSet() })
                .ListedIds.Contains(1),
            "an issue whose page task has no config_rewards row was still listed");
        Assert(!MagazineCatalog.Build(tables with { TagIds = tables.TagIds.Where(id => id != 22).ToHashSet() }).ListedIds.Contains(1),
            "an issue with a missing config_magazine_tag row was still listed");
        var closed = tables.Infos.ToDictionary(pair => pair.Key, pair => pair.Value);
        closed[2] = Clone(closed[2]);
        closed[2].IsOpen = 0;
        MagazineCatalog withoutSecond = MagazineCatalog.Build(tables with { Infos = closed });
        Assert(!withoutSecond.ListedIds.Contains(2) && withoutSecond.Rejected.Count == 0 &&
               withoutSecond.ListedIds.Count == ExpectedIssues.Length - 1,
            "an issue with is_open = 0 was listed or reported");

        // 活动期：PeriodManager type 1。第 1 期任务期是第 1、2 段（约 30 天）；2026 年所有期刊都不是 Active。
        ConfigPeriod first = tables.Periods[20001];
        IReadOnlyList<int> taskArea = [1, 2];
        Assert(!MagazineCatalog.IsInPeriodArea(first, taskArea, FirstStart - 1) &&
               MagazineCatalog.IsInPeriodArea(first, taskArea, FirstStart) &&
               MagazineCatalog.IsInPeriodArea(first, taskArea, FirstStart + 2_159_999 + 1) &&
               !MagazineCatalog.IsInPeriodArea(first, taskArea, FirstStart + 2_159_999 + 432_000) &&
               !MagazineCatalog.IsInPeriodArea(first, taskArea, T0) &&
               MagazineCatalog.IsInPeriodArea(first, [3], T0) && MagazineCatalog.IsInPeriodArea(first, [], T0) &&
               !MagazineCatalog.IsInPeriodArea(first, [], FirstStart + 315_360_000 + 1),
            "IsInPeriodArea does not follow PeriodManager's duration_list areas");
        Assert(catalog.IsActive(10, LatestStart + 86_400) && !catalog.IsActive(9, LatestStart + 86_400) &&
               ExpectedIssues.All(id => !catalog.IsActive(id, T0)),
            "only the latest issue inside its task period may be Active, and none is Active in 2026");

        // 编解码：每个标量即使为 0 也写出，repeated int32 不打包。
        byte[] info0 = MagazineProtocolCodec.EncodeInfo(new MagazineInfoMessage([1, 2], 0, 0));
        Assert(Convert.ToHexString(info0) == "0801080210001800", $"TRetMagazineInfo encoded as {Convert.ToHexString(info0)}");
        MagazineInfoMessage infoBack = MagazineProtocolCodec.DecodeInfo(
            MagazineProtocolCodec.EncodeInfo(new MagazineInfoMessage([10, 3], 1_800_000_000, 1)));
        Assert(infoBack.MagazineIds.SequenceEqual([10, 3]) && infoBack is { Time: 1_800_000_000, Version: 1 },
            "TRetMagazineInfo did not round-trip");
        var zeros = new MagazineSnapshot(
            [new MagazineSubEntry(0, [new MagazineSubReward(0, 0)])], [new MagazineHeroSlot(0, 0, 0)], [new MagazineUnlock(0, 0)]);
        byte[] zeroBytes = MagazineProtocolCodec.EncodeSnapshot(zeros);
        // MagazineSub{0A 08: 08 00, 12 04 {08 00 10 00}}、MagazineHero{12 06: 08 00 10 00 18 00}、MagazineUnLock{1A 04: 08 00 10 00}。
        Assert(Convert.ToHexString(zeroBytes) == "0A08080012040800100012060800100018001A0408001000",
            $"TMagazine with zero scalars encoded as {Convert.ToHexString(zeroBytes)}");
        var full = new MagazineSnapshot(
            [new MagazineSubEntry(1, [new MagazineSubReward(1, 1_800_000_000), new MagazineSubReward(3, 0)]), new MagazineSubEntry(7, [])],
            [new MagazineHeroSlot(2, 1234, 10)],
            [new MagazineUnlock(1, 1_800_000_001), new MagazineUnlock(5, 1)]);
        MagazineSnapshot back = MagazineProtocolCodec.DecodeSnapshot(MagazineProtocolCodec.EncodeSnapshot(full));
        Assert(back.Subs.Count == 2 && back.Subs[0].MagazineId == 1 && back.Subs[0].Rewards.SequenceEqual(full.Subs[0].Rewards) &&
               back.Subs[1] is { MagazineId: 7, Rewards.Count: 0 } &&
               back.Heroes.SequenceEqual(full.Heroes) && back.Unlocks.SequenceEqual(full.Unlocks),
            "TMagazine did not round-trip");
        Assert(MagazineProtocolCodec.EncodeSnapshot(new MagazineSnapshot([], [], [])).Length == 0 &&
               MagazineProtocolCodec.DecodeSnapshot([]) is { Subs.Count: 0, Heroes.Count: 0, Unlocks.Count: 0 },
            "an empty TMagazine is not empty");
        Assert(MagazineProtocolCodec.DecodeMagazineId(new ProtocolPackage().Write(0x08, 7UL).ToArray()) == 7 &&
               MagazineProtocolCodec.DecodeArg(new ProtocolPackage().Write(0x08, 3UL).Write(0x10, 2UL).ToArray()) == (3, 2) &&
               MagazineProtocolCodec.DecodeAddHero(new ProtocolPackage().Write(0x08, 1UL).Write(0x10, 99UL).Write(0x18, 10UL).ToArray()) == (1, 99, 10) &&
               MagazineProtocolCodec.DecodeMagazineId([]) == 0 && MagazineProtocolCodec.DecodeArg([0x08]) == (0, 0),
            "the magazine request arguments did not decode (or a truncated one threw)");

        // task.TaskInfo 字段 14：目录里的每个任务恰好一条，6 个标量都写出且为 0；其它任务字段不受影响。
        TaskConfigCatalog.Load(configDir);
        MagazineCatalog current = MagazineCatalog.Load(configDir);
        PlayerAccount account = PlayerAccountFactory.CreateDefault("magazine-pure", checked((int)T0));
        byte[] taskInfo = TaskProtocolCodec.EncodeTaskInfo(account, checked((int)T0));
        Dictionary<int, (int EventType, MagazineTaskView Task)> sent = MagazineTaskField(taskInfo);
        Assert(sent.Count == current.TaskDefinitions.Count &&
               current.TaskDefinitions.All(definition => sent.TryGetValue(definition.Id, out var entry) && entry.EventType == definition.EventType) &&
               current.Issues.All(issue => issue.TaskIds.All(sent.ContainsKey)),
            $"task.TaskInfo field 14 carried {sent.Count} tasks instead of every listed issue's {current.TaskDefinitions.Count}");
        Assert(sent.Values.All(entry => entry.Task is { AllScalars: true, RewardTime: 0, FinishTime: 0, Count: 0 }),
            "a field-14 task omitted a zero scalar (TaskId / RewardTime / FinishTime / Count / StartTime / FinishNum)");
        Assert(Fields(taskInfo).Any(field => field.Field == 1) && Fields(taskInfo).Any(field => field.Field == 12),
            "adding field 14 dropped the main tasks or the teaching stage info");

        // 栞解锁（纯函数）：没有栞、未列出、特别号、作弊、重复、Active 都拒绝，原账号不变；成功时扣 1 张并记时刻。
        PlayerAccount poor = account with { Bag = new PlayerBag([new BagItem(FirstStamp, 60)]) };
        PlayerAccount rich = account with { Bag = new PlayerBag([new BagItem(Bookmark, 1), new BagItem(Bookmark, 1)]) };
        MagazineUnlockOutcome noBookmark = MagazineLogic.TryUnlock(poor, catalog, 1, T0, cheat: false);
        Assert(!noBookmark.Success && ReferenceEquals(noBookmark.Account, poor) && noBookmark.Error.Contains("bookmark"),
            "an unlock without a bookmark was not rejected");
        foreach ((int id, bool cheat, long now, string why) in new[]
                 {
                     (99, false, T0, "an unlisted issue"), (4, false, T0, "a special issue"),
                     (1, true, T0, "the cheat"), (10, false, LatestStart + 86_400, "the active latest issue"),
                 })
        {
            MagazineUnlockOutcome refused = MagazineLogic.TryUnlock(rich, catalog, id, now, cheat);
            Assert(!refused.Success && ReferenceEquals(refused.Account, rich) && refused.Error.Length > 0,
                $"unlocking {why} was not rejected");
        }
        MagazineUnlockOutcome unlocked = MagazineLogic.TryUnlock(rich, catalog, 1, T0, cheat: false);
        Assert(unlocked is { Success: true, BagChanged: true, CurrencyChanged: false } &&
               unlocked.Account.Bag!.Items.Sum(item => item.TemplateId == Bookmark ? item.Num : 0) == 1 &&
               unlocked.Account.Magazine!.Unlocks.SequenceEqual([new MagazineUnlock(1, T0)]),
            "a successful unlock did not take one bookmark and record the time");
        Assert(!MagazineLogic.TryUnlock(unlocked.Account, catalog, 1, T0 + 1, cheat: false).Success,
            "unlocking the same issue twice was accepted");

        // 下发的解锁：作弊时全部期刊（存档里的时刻优先），否则只有存档里且仍在目录里的期刊。
        PlayerAccount stale = unlocked.Account with
        {
            Magazine = new PlayerMagazine([new MagazineUnlock(1, T0), new MagazineUnlock(99, T0), new MagazineUnlock(3, 0)]),
        };
        Assert(MagazineLogic.VisibleUnlocks(stale, catalog, cheat: false, T0 + 5).SequenceEqual([new MagazineUnlock(1, T0)]),
            "a stale or zero-time unlock was sent");
        IReadOnlyList<MagazineUnlock> all = MagazineLogic.VisibleUnlocks(stale, catalog, cheat: true, T0 + 5);
        Assert(all.Select(unlock => unlock.MagazineId).SequenceEqual(ExpectedIssues) &&
               all.All(unlock => unlock.Time == (unlock.MagazineId == 1 ? T0 : T0 + 5)),
            "the cheat did not unlock every listed issue (keeping the stored time)");
        Assert(MagazineLogic.VisibleUnlocks(account, catalog, cheat: false, T0).Count == 0 &&
               MagazineLogic.Snapshot(stale, catalog, cheat: false, T0) is { Subs.Count: 0, Heroes.Count: 0, Unlocks.Count: 1 },
            "a fresh account showed unlocks, or the snapshot carried stamp boxes / trend heroes");

        // 存档：PlayerAccount.Magazine JSON 往返；没有 magazine 字段的旧档读成 null。
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PlayerAccount reloaded = JsonSerializer.Deserialize<PlayerAccount>(JsonSerializer.Serialize(unlocked.Account, json), json)!;
        Assert(reloaded.Magazine is { Version: 1 } saved && saved.Unlocks.SequenceEqual(unlocked.Account.Magazine!.Unlocks),
            "PlayerAccount.Magazine did not survive a JSON round-trip");
        JsonObject legacy = JsonNode.Parse(JsonSerializer.Serialize(account, json))!.AsObject();
        Assert(legacy.Remove("magazine") && JsonSerializer.Deserialize<PlayerAccount>(legacy.ToJsonString(), json)!.Magazine is null,
            "a legacy save without a magazine property did not load");
        return Task.CompletedTask;
    }

    public static async Task ModuleTest()
    {
        string clientPath = ClientPath();
        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-magazine-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            foreach ((string profile, int bookmarks) in new[] { ("mag-off", 2), ("mag-poor", 0), ("mag-on", 1) })
            {
                PlayerAccount seed = PlayerAccountFactory.CreateDefault(profile, checked((int)T0));
                var items = new List<BagItem> { new(FirstStamp, 60) };
                if (bookmarks > 0) items.Add(new BagItem(Bookmark, bookmarks));
                await repo.SaveAccountAsync(seed with { Bag = new PlayerBag(items) });
            }
            using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
                Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            GameServices Services(string profile, params string[] flags) => new(repo, ServerOptions.Parse(
                [.. new[] { "--no-cheats", "--real-resource-cost=off", "--data=" + dataRoot, "--client-path=" + clientPath, "--profile-id=" + profile }, .. flags]), loggerFactory);
            async Task<PlayerAccount> Load(string profile) =>
                await repo.LoadAccountAsync(profile) ?? throw new InvalidDataException("account missing");
            static int Count(PlayerAccount account, int templateId) =>
                account.Bag?.Items.Where(item => item.TemplateId == templateId).Sum(item => item.Num) ?? 0;

            // ── 作弊关：往期上锁，用栞解锁。
            GameServices off = Services("mag-off");
            Assert(!off.Cheats.Magazine, "--no-cheats left the magazine cheat on");
            var module = new MagazineModule(off);
            MagazineCatalog catalog = off.Magazines;
            Assert(catalog.ListedIds.SequenceEqual(ExpectedIssues), "GameServices did not load the magazine catalog");

            // 登录同步推送：task.TaskInfo（带字段 14）→ magazine.UpdateMagazineInfo（全部期刊）→ magazine.GetMagazine（没有解锁）。
            IReadOnlyList<byte[]> sync = await off.BuildSyncPushesAsync("mag-off", checked((uint)T0), CancellationToken.None);
            List<string> methods = sync.Select(Method).ToList();
            int taskIndex = methods.IndexOf("task.TaskInfo");
            int infoIndex = methods.IndexOf(MagazineProtocolCodec.InfoMethod);
            int snapshotIndex = methods.IndexOf(MagazineProtocolCodec.SnapshotMethod);
            Assert(taskIndex >= 0 && infoIndex > taskIndex && snapshotIndex > infoIndex &&
                   methods.Count(method => method.StartsWith("magazine.", StringComparison.Ordinal)) == 2,
                $"the login sync did not push task.TaskInfo → UpdateMagazineInfo → GetMagazine: [{string.Join(", ", methods)}]");
            Assert(MagazineProtocolCodec.DecodeInfo(PushRet(sync[infoIndex])) is { Version: 1 } info &&
                   info.MagazineIds.SequenceEqual(ExpectedIssues) && info.Time == T0,
                "magazine.UpdateMagazineInfo did not list every issue with Time = now and Version = 1");
            Assert(Snapshot(sync[snapshotIndex]) is { Subs.Count: 0, Heroes.Count: 0, Unlocks.Count: 0 },
                "a fresh account was pushed unlocks, stamp boxes or trend heroes");
            Assert(MagazineTaskField(PushRet(sync[taskIndex])).Keys.ToHashSet().SetEquals(catalog.TaskDefinitions.Select(d => d.Id)),
                "the login task.TaskInfo did not carry every listed issue's tasks in field 14");

            // 没有栞：Err，ErrMsg 非空，先推背包再推 GetMagazine，存档不变。
            ModuleResult poor = await module.HandleAsync(At(off, "mag-poor", T0), UnLock(1));
            Assert(poor.Err == 1 && poor.ErrMsg.Contains("bookmark") && poor.Ret.Length == 0 &&
                   poor.PrePushes.Select(Method).Take(2).SequenceEqual(["bag.UpdateBagData", MagazineProtocolCodec.SnapshotMethod]) &&
                   Snapshot(poor.PrePushes[1]).Unlocks.Count == 0 && (await Load("mag-poor")).Magazine is null,
                $"an unlock without a bookmark was not rejected with a resync ({poor.Err} {poor.ErrMsg})");

            // 有栞：扣 1 张，记解锁时刻；应答前推背包与带解锁的 GetMagazine。
            ModuleResult first = await module.HandleAsync(At(off, "mag-off", T0 + 10), UnLock(1));
            PlayerAccount afterFirst = await Load("mag-off");
            Assert(first.Err == 0 && first.Ret.Length == 0 &&
                   first.PrePushes.Select(Method).Take(2).SequenceEqual(["bag.UpdateBagData", MagazineProtocolCodec.SnapshotMethod]) &&
                   Snapshot(first.PrePushes[1]).Unlocks.SequenceEqual([new MagazineUnlock(1, T0 + 10)]),
                $"magazine.UnLock did not push the bag and the unlock before its response ({first.Err} {first.ErrMsg})");
            Assert(Count(afterFirst, Bookmark) == 1 && afterFirst.Magazine!.Unlocks.SequenceEqual([new MagazineUnlock(1, T0 + 10)]),
                "the unlock was not saved or did not take exactly one bookmark");

            // 重复解锁、特别号、未列出的期刊都拒绝，不扣栞。
            foreach ((int id, string why) in new[] { (1, "the same issue"), (4, "special issue 4"), (99, "unlisted issue 99") })
            {
                ModuleResult refused = await module.HandleAsync(At(off, "mag-off", T0 + 20), UnLock(id));
                Assert(refused.Err == 1 && refused.ErrMsg.Length > 0 && Count(await Load("mag-off"), Bookmark) == 1 &&
                       refused.PrePushes.Select(Method).Contains(MagazineProtocolCodec.SnapshotMethod),
                    $"unlocking {why} was not rejected with a resync");
            }

            // 印章箱、趋势舰娘与投票：Err，什么都不发放（60 个印章够第 1～4 箱的门槛也不行）。
            // 先在同一时刻结算一次，下面三个请求不再有经过的时间，存档的任何变化都只能来自请求本身。
            Assert((await module.HandleAsync(At(off, "mag-off", T0 + 30), new TRequest("magazine.Magazine"))).Err == 0,
                "magazine.Magazine failed");
            PlayerAccount beforeFetch = await Load("mag-off");
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            foreach (TRequest request in new[]
                     {
                         new TRequest("magazine.FetchMagazineReward", new ProtocolPackage().Write(0x08, 1UL).Write(0x10, 1UL).ToArray()),
                         new TRequest("magazine.AddHero", new ProtocolPackage().Write(0x08, 1UL).Write(0x10, 1UL).Write(0x18, 1UL).ToArray()),
                         new TRequest("magazine.Vote", new ProtocolPackage().Write(0x08, 1UL).ToArray()),
                     })
            {
                ModuleResult refused = await module.HandleAsync(At(off, "mag-off", T0 + 30), request);
                PlayerAccount after = await Load("mag-off");
                Assert(refused.Err == 1 && refused.ErrMsg.Length > 0 && refused.Ret.Length == 0 &&
                       refused.PrePushes.Select(Method).Take(2).SequenceEqual(["bag.UpdateBagData", MagazineProtocolCodec.SnapshotMethod]) &&
                       Snapshot(refused.PrePushes[1]) is { Subs.Count: 0, Heroes.Count: 0 } &&
                       after.Bag!.Items.SequenceEqual(beforeFetch.Bag!.Items) &&
                       JsonSerializer.Serialize(after.Character, json) == JsonSerializer.Serialize(beforeFetch.Character, json) &&
                       (after.Equip?.Items.Count ?? 0) == (beforeFetch.Equip?.Items.Count ?? 0) &&
                       after.Dock.Heroes.Count == beforeFetch.Dock.Heroes.Count &&
                       after.Magazine!.Unlocks.SequenceEqual(beforeFetch.Magazine!.Unlocks),
                    $"{request.Method} was not rejected without granting anything");
            }

            // magazine.Magazine（活动期计时器用）：成功并推 GetMagazine；路由按前缀送到 MagazineModule。
            ModuleResult refresh = await module.HandleAsync(At(off, "mag-off", T0 + 40), new TRequest("magazine.Magazine"));
            Assert(refresh.Err == 0 && Snapshot(refresh.PrePushes.Single(push => Method(push) == MagazineProtocolCodec.SnapshotMethod))
                       .Unlocks.SequenceEqual([new MagazineUnlock(1, T0 + 10)]),
                "magazine.Magazine did not answer with the current unlocks");
            ModuleResult routed = await new MessageRouter(off, [module]).DispatchAsync(
                new TRequest("magazine.Magazine"), "mag-route", CancellationToken.None);
            Assert(routed.Err == 0 && routed.PrePushes.Any(push => Method(push) == MagazineProtocolCodec.SnapshotMethod),
                "the router did not send magazine.* to MagazineModule");

            // 用掉最后一张栞后再解锁被拒；重新登录时下发两期解锁。
            Assert((await module.HandleAsync(At(off, "mag-off", T0 + 50), UnLock(2))).Err == 0 &&
                   (await module.HandleAsync(At(off, "mag-off", T0 + 60), UnLock(3))).Err == 1 &&
                   Count(await Load("mag-off"), Bookmark) == 0,
                "the second bookmark did not unlock issue 2, or a third unlock was accepted without bookmarks");
            IReadOnlyList<byte[]> relogin = await off.BuildSyncPushesAsync("mag-off", checked((uint)(T0 + 70)), CancellationToken.None);
            Assert(Snapshot(relogin.Single(push => Method(push) == MagazineProtocolCodec.SnapshotMethod)).Unlocks
                       .SequenceEqual([new MagazineUnlock(1, T0 + 10), new MagazineUnlock(2, T0 + 50)]),
                "the login sync did not push the saved unlocks");

            // ── 作弊开：全部期刊（含特别号）按已解锁下发，栞按钮不出现；即使发来 UnLock 也不扣栞、不写存档。
            GameServices on = Services("mag-on", "--cheat-magazine");
            var onModule = new MagazineModule(on);
            IReadOnlyList<MagazineUnlock> onUnlocks = Snapshot((await on.BuildSyncPushesAsync("mag-on", checked((uint)T0), CancellationToken.None))
                .Single(push => Method(push) == MagazineProtocolCodec.SnapshotMethod)).Unlocks;
            Assert(on.Cheats.Magazine && onUnlocks.Select(unlock => unlock.MagazineId).SequenceEqual(ExpectedIssues) &&
                   onUnlocks.All(unlock => unlock.Time > 0),
                "the magazine cheat did not unlock every issue");
            ModuleResult onUnlock = await onModule.HandleAsync(At(on, "mag-on", T0 + 10), UnLock(1));
            PlayerAccount afterOn = await Load("mag-on");
            Assert(onUnlock.Err == 1 && Count(afterOn, Bookmark) == 1 && afterOn.Magazine is null &&
                   Snapshot(onUnlock.PrePushes.Single(push => Method(push) == MagazineProtocolCodec.SnapshotMethod)).Unlocks.Count == ExpectedIssues.Length,
                "magazine.UnLock under the cheat took a bookmark or wrote the save");

            // 切换作弊：开启时存档里的解锁时刻保留；关闭后只剩存档里的解锁。
            IReadOnlyList<MagazineUnlock> switchedOn = Snapshot((await Services("mag-off", "--cheat-magazine")
                    .BuildSyncPushesAsync("mag-off", checked((uint)(T0 + 80)), CancellationToken.None))
                .Single(push => Method(push) == MagazineProtocolCodec.SnapshotMethod)).Unlocks;
            Assert(switchedOn.Count == ExpectedIssues.Length && switchedOn[0] == new MagazineUnlock(1, T0 + 10) &&
                   switchedOn[1] == new MagazineUnlock(2, T0 + 50) && switchedOn.Skip(2).All(unlock => unlock.Time == T0 + 80),
                "turning the cheat on lost the saved unlock times");
            IReadOnlyList<MagazineUnlock> switchedOff = Snapshot((await Services("mag-on")
                    .BuildSyncPushesAsync("mag-on", checked((uint)(T0 + 90)), CancellationToken.None))
                .Single(push => Method(push) == MagazineProtocolCodec.SnapshotMethod)).Unlocks;
            Assert(switchedOff.Count == 0, "turning the cheat off kept showing issues that were never unlocked with a bookmark");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }

    /// <summary>
    /// 起真实服务端（作弊关）：登录同步推送里 task.TaskInfo（字段 14 覆盖全部杂志任务）在 UpdateMagazineInfo、GetMagazine 之前；
    /// TCP 上用 1 张栞解锁第 1 期，第二次解锁与领印章箱都被拒绝，存档只记第 1 期。
    /// </summary>
    public static async Task IntegrationTest()
    {
        string root = FindRepositoryRoot();
        string serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net8.0", "BlueOath.Server.dll");
        Assert(File.Exists(serverDll), "server assembly is missing; build the solution first");
        string clientPath = ClientPath();
        MagazineCatalog catalog = MagazineCatalog.Build(MagazineTables.Load(ConfigDbLoader.BuildConfigDir(clientPath)));
        string data = Path.Combine(Path.GetTempPath(), "blueoath-magazine-tcp-" + Guid.NewGuid().ToString("N"));
        const string profileId = "magazine-player";
        int seededAt = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 60;
        await new SqliteGameRepository(data).SaveAccountAsync(PlayerAccountFactory.CreateDefault(profileId, seededAt) with
        {
            Bag = new PlayerBag([new BagItem(Bookmark, 1), new BagItem(FirstStamp, 60)]),
        });

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[]
                 {
                     serverDll, "--port=0", "--game-login-port=0", "--gm-port=0", "--region=jp", "--no-cheats", "--real-resource-cost=off",
                     "--data=" + data, "--profile-id=" + profileId, "--client-path=" + clientPath,
                 })
            startInfo.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            Assert(process.Start(), "magazine test server did not start");
            _ = process.StandardError.ReadToEndAsync();
            string? readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            using JsonDocument ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
            Assert(ready.RootElement.TryGetProperty("cheats", out JsonElement cheats) &&
                   cheats.TryGetProperty("magazine", out JsonElement magazineEcho) && !magazineEcho.GetBoolean(),
                "the ready JSON did not echo the (disabled) magazine cheat");
            int port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            NetworkStream stream = client.GetStream();

            async Task<TResponse> RoundTrip(string method, byte[]? args, ICollection<TResponse>? prePushes = null)
            {
                byte[] request = TMessageCodec.EncodeRequest(new TRequest(method, args, 1));
                await NetSocketFrameCodec.WriteAsync(stream, request, NetSocketFrameCodec.TypeData, timeout.Token);
                while (true)
                {
                    var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                    Assert(frame is not null, $"empty response for {method}");
                    TResponse response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                    if (response.IsResponse == 1) return response;
                    prePushes?.Add(response);
                }
            }

            await RoundTrip("player.Login", GameLoginCodec.Encode(new TArgLogin(profileId, 1, "open", "hash")));
            TResponse userInfo = await RoundTrip("user.GetUserInfo", null);
            Assert(userInfo.Err == 0, "user.GetUserInfo failed");

            // 登录同步推送跟在 user.GetUserInfo 的应答之后，最后一条是 talentTree.TalentTreeAllList。
            var sync = new List<TResponse>();
            while (sync.Count < 100 && (sync.Count == 0 || sync[^1].Method != "talentTree.TalentTreeAllList"))
            {
                var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                Assert(frame is not null, "the login synchronization pushes stopped early");
                sync.Add(TMessageCodec.DecodeResponse(frame!.Value.Payload));
            }
            List<string> methods = sync.Select(push => push.Method).ToList();
            int taskIndex = methods.IndexOf("task.TaskInfo");
            int infoIndex = methods.IndexOf(MagazineProtocolCodec.InfoMethod);
            int snapshotIndex = methods.IndexOf(MagazineProtocolCodec.SnapshotMethod);
            Assert(taskIndex >= 0 && infoIndex > taskIndex && snapshotIndex > infoIndex,
                $"the login sync over TCP was not task.TaskInfo → UpdateMagazineInfo → GetMagazine: [{string.Join(", ", methods)}]");
            Assert(MagazineProtocolCodec.DecodeInfo(sync[infoIndex].Ret ?? []).MagazineIds.SequenceEqual(catalog.ListedIds) &&
                   MagazineProtocolCodec.DecodeSnapshot(sync[snapshotIndex].Ret ?? []).Unlocks.Count == 0,
                "the TCP login pushed the wrong issue list or unlocks");
            Assert(MagazineTaskField(sync[taskIndex].Ret ?? []).Keys.ToHashSet()
                    .SetEquals(catalog.Issues.SelectMany(issue => issue.TaskIds)),
                "the TCP task.TaskInfo field 14 did not cover every listed issue's tasks");

            // 用 1 张栞解锁第 1 期：应答前先推背包，再推带解锁的 GetMagazine。
            var unlockPushes = new List<TResponse>();
            TResponse unlock = await RoundTrip("magazine.UnLock", new ProtocolPackage().Write(0x08, 1UL).ToArray(), unlockPushes);
            List<string> unlockMethods = unlockPushes.Select(push => push.Method).ToList();
            Assert(unlock.Err == 0 && unlockMethods.IndexOf("bag.UpdateBagData") is >= 0 and var bagIndex &&
                   unlockMethods.IndexOf(MagazineProtocolCodec.SnapshotMethod) is var unlockSnapshot && unlockSnapshot > bagIndex &&
                   MagazineProtocolCodec.DecodeSnapshot(unlockPushes[unlockSnapshot].Ret ?? []).Unlocks.Select(u => u.MagazineId).SequenceEqual([1]),
                $"magazine.UnLock over TCP failed or pushed [{string.Join(", ", unlockMethods)}] ({unlock.Err} {unlock.ErrMsg})");

            // 没有栞了：第 2 期被拒；印章箱被拒；都带非空 ErrMsg 并重推 GetMagazine。
            var refusedPushes = new List<TResponse>();
            TResponse second = await RoundTrip("magazine.UnLock", new ProtocolPackage().Write(0x08, 2UL).ToArray(), refusedPushes);
            TResponse fetch = await RoundTrip("magazine.FetchMagazineReward",
                new ProtocolPackage().Write(0x08, 1UL).Write(0x10, 1UL).ToArray(), refusedPushes);
            Assert(second.Err == 1 && second.ErrMsg.Length > 0 && fetch.Err == 1 && fetch.ErrMsg.Length > 0 &&
                   refusedPushes.Count(push => push.Method == MagazineProtocolCodec.SnapshotMethod) == 2 &&
                   refusedPushes.Where(push => push.Method == MagazineProtocolCodec.SnapshotMethod)
                       .All(push => MagazineProtocolCodec.DecodeSnapshot(push.Ret ?? []).Unlocks.Select(u => u.MagazineId).SequenceEqual([1])),
                "a second unlock without bookmarks or a stamp box fetch was not rejected with a resync");

            PlayerAccount stored = await new SqliteGameRepository(data).LoadAccountAsync(profileId)
                ?? throw new InvalidDataException("magazine profile was not persisted");
            Assert(stored.Magazine is { } magazine && magazine.Unlocks.Select(u => u.MagazineId).SequenceEqual([1]) &&
                   stored.Bag!.Items.Where(item => item.TemplateId == Bookmark).Sum(item => item.Num) == 0 &&
                   stored.Bag.Items.Where(item => item.TemplateId == FirstStamp).Sum(item => item.Num) == 60,
                "the save did not record exactly the bookmark unlock (or the stamp fetch changed the bag)");
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (InvalidOperationException)
            {
            }
            for (int attempt = 0; attempt < 10 && Directory.Exists(data); attempt++)
            {
                try
                {
                    Directory.Delete(data, true);
                }
                catch (IOException)
                {
                    await Task.Delay(200);
                }
            }
        }
    }

    // ───────────────────────── 辅助 ─────────────────────────

    /// <summary>字段 14 里一个任务（TTask）的标量：AllScalars 表示 TaskId、RewardTime、FinishTime、Count、StartTime、FinishNum 都写出了。</summary>
    private sealed record MagazineTaskView(int TaskId, long RewardTime, long FinishTime, long Count, bool AllScalars);

    /// <summary>解析 TTaskInfoRet 的字段 14（MagazineTask：repeated TEventInfo{EventType 1, Count 2, Task 3}），按任务 id 索引。</summary>
    private static Dictionary<int, (int EventType, MagazineTaskView Task)> MagazineTaskField(byte[] taskInfo)
    {
        var result = new Dictionary<int, (int, MagazineTaskView)>();
        foreach ((int field, int wire, _, byte[] eventBytes) in Fields(taskInfo))
        {
            if (field != 14 || wire != 2) continue;
            int eventType = -1;
            bool zeroCount = false;
            var tasks = new List<MagazineTaskView>();
            foreach ((int f, int w, ulong v, byte[] b) in Fields(eventBytes))
            {
                if (f == 1 && w == 0) eventType = (int)v;
                else if (f == 2 && w == 0) zeroCount = v == 0;
                else if (f == 3 && w == 2)
                {
                    var scalars = new Dictionary<int, ulong>();
                    foreach ((int tf, int tw, ulong tv, _) in Fields(b))
                        if (tw == 0) scalars[tf] = tv;
                    tasks.Add(new MagazineTaskView(
                        (int)scalars.GetValueOrDefault(1), (long)scalars.GetValueOrDefault(2), (long)scalars.GetValueOrDefault(3),
                        (long)scalars.GetValueOrDefault(4), new[] { 1, 2, 3, 4, 7, 8 }.All(scalars.ContainsKey)));
                }
            }
            Assert(eventType >= 0 && zeroCount, "a field-14 event omitted EventType or its zero Count");
            foreach (MagazineTaskView task in tasks)
                Assert(result.TryAdd(task.TaskId, (eventType, task)), $"task {task.TaskId} appeared twice in field 14");
        }
        return result;
    }

    private static Dictionary<int, T> Without<T>(IReadOnlyDictionary<int, T> source, int key) =>
        source.Where(pair => pair.Key != key).ToDictionary(pair => pair.Key, pair => pair.Value);

    private static ConfigMagazineInfo Clone(ConfigMagazineInfo info)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return JsonSerializer.Deserialize<ConfigMagazineInfo>(JsonSerializer.Serialize(info, json), json)!;
    }

    private static GameContext At(GameServices services, string profileId, long now) =>
        new() { ProfileId = profileId, Now = checked((int)now), Ct = CancellationToken.None, Services = services };

    private static TRequest UnLock(int magazineId) =>
        new("magazine.UnLock", new ProtocolPackage().Write(0x08, (ulong)magazineId).ToArray());

    private static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;

    private static byte[] PushRet(byte[] push) => TMessageCodec.DecodeResponse(push).Ret ?? [];

    private static MagazineSnapshot Snapshot(byte[] push) => MagazineProtocolCodec.DecodeSnapshot(PushRet(push));
}
