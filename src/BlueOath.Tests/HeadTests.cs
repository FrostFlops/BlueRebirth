using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server;
using BlueOath.Server.Configs;
using BlueOath.Server.Protocols;
using BlueOath.Storage;
using static TestSupport;

/// <summary>
/// 舰娘头像（user.SetHead / GetHeadBuyCount / BuyHead，登录推送 user.NewHeadUnlockedList）与「头像全部解锁」作弊的测试：
/// <see cref="PureTest"/> 覆盖开关解析、编码（ProfileID 非 packed）与 <see cref="HeadLogic"/> 的纯函数（含空目录的旧行为）；
/// <see cref="ModuleTest"/> 走 GameServices 登录同步与 UserModule（作弊开 / 关）；<see cref="IntegrationTest"/> 起真实服务端走 TCP，
/// 同一数据目录先开作弊、再关作弊各启动一次。客户端路径可用环境变量 BLUEOATH_CLIENT_PATH 指向未热更的 1.4.0（307 行
/// config_profile，热更后 347 行），用到的头像 id 都从配置里按条件挑选，只有两版都有的朝日活动头像 2101018 写死。
/// 由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class HeadTests
{
    // Program.cs 的顶层函数 Assert / FindRepositoryRoot 会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private static string FindRepositoryRoot() => TestSupport.FindRepositoryRoot();

    // 两版日服客户端都有：初始秘书舰（模板 10210511）的 sf 1021051 与它的默认头像 1021051；朝日（sf 2101011）的
    // 「花咲き物語」活动表情头像 2101018（type 3），离线版没有发放途径，只有作弊下才解锁。
    private const int StarterShip = 1021051, AsahiShip = 2101011, AsahiEventHead = 2101018;
    private static readonly int T0 = checked((int)new DateTimeOffset(2026, 10, 24, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());

    private sealed record ProfileRow(int Id, int Ship, int Type, bool IsDefault, int Fashion);

    public static Task PureTest()
    {
        // 「头像全部解锁」作弊：默认开，--cheat-heads=off / --no-cheats 关闭，算作作弊。
        CheatOptions parsed = ServerOptions.Parse(["--no-cheats", "--real-resource-cost=off", "--CHEAT-HEADS"]).Cheats;
        Assert(parsed == new CheatOptions(Heads: true) && parsed.Any && CheatOptions.Default.Heads &&
               !ServerOptions.Parse(["--cheat-heads=off"]).Cheats.Heads &&
               !ServerOptions.Parse(["--cheat-heads", "--no-cheats"]).Cheats.Heads &&
               ServerOptions.Parse(["--no-cheats", "--cheat-heads=on"]).Cheats.Heads,
            "--cheat-heads did not parse, default on, or turn off with --no-cheats");

        // 编码：每艘舰娘一个 TNewHeadNode，ProfileID 每个 id 一个 wire-0 标签（客户端 protobuf.lua 解不了 packed）。
        List<(int Ship, List<int> Ids)> nodes = DecodeUnlockedList(
            ProtocolEncoder.EncodeHeadUnlockedList([(AsahiShip, [AsahiShip, AsahiEventHead]), (StarterShip, [StarterShip])]));
        Assert(nodes.Count == 2 && nodes[0].Ship == AsahiShip && nodes[0].Ids.SequenceEqual([AsahiShip, AsahiEventHead]) &&
               nodes[1].Ship == StarterShip && nodes[1].Ids.SequenceEqual([StarterShip]),
            "TNewHeadUnlockedList did not encode one node per ship with unpacked ProfileIDs");
        Assert(ProtocolEncoder.EncodeHeadUnlockedList([]).Length == 0, "an empty unlock list was not empty");
        // TNewHeadBuyCountRet：两个字段都写出，即使为 0。
        Assert(ProtocolEncoder.EncodeHeadBuyCount(0, 0).SequenceEqual(new byte[] { 0x08, 0x00, 0x10, 0x00 }) &&
               Fields(ProtocolEncoder.EncodeHeadBuyCount(AsahiShip, 0)) is [(1, 0, (ulong)AsahiShip, _), (2, 0, 0, _)],
            "TNewHeadBuyCountRet did not write both ShipFleetId and a zero Count");

        // HeadLogic 用一张小目录：舰娘 10（拥有，模板 1001）有默认、两套换装（只拥有 104）、表情与付费头像；舰娘 20 没有。
        var catalog = new HeadCatalog(
        [
            new HeadProfile(100, 10, HeadCatalog.TypePortrait, true, 100),
            new HeadProfile(104, 10, HeadCatalog.TypeFashion, false, 104),
            new HeadProfile(105, 10, HeadCatalog.TypeFashion, false, 105),
            new HeadProfile(107, 10, HeadCatalog.TypeExpression, false, 0),
            new HeadProfile(108, 10, HeadCatalog.TypeBuy, false, 0),
            new HeadProfile(109, 10, HeadCatalog.TypePortrait, false, 0),
            new HeadProfile(203, 20, HeadCatalog.TypeExpression, false, 0),
            new HeadProfile(200, 20, HeadCatalog.TypePortrait, true, 200),
        ], templateId => templateId / 100);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault("head-pure", T0);
        PlayerAccount account = seed with
        {
            Character = seed.Character with { SecretaryId = 1, Head = 203 },
            Dock = new HeroDock([new Hero(1, 1001, 1), new Hero(2, 1002, 1)]),
            Fashion = new PlayerFashion([new FashionEntry(10, [100, 104])]),
        };
        Assert(catalog.Rows.Select(row => row.Id).SequenceEqual([100, 104, 105, 107, 108, 109, 200, 203]) &&
               catalog.DefaultHeadOf(10) == 100 && catalog.DefaultHeadOf(30) is null && catalog.ShipFleetOf(1002) == 10,
            "HeadCatalog did not order rows by ship and id or find the default heads");
        // 作弊关：拥有的舰娘的默认头像与已拥有的换装头像，加上当前头像（作弊下选的别的舰娘的表情）；没有表情、付费、非默认立绘。
        Assert(Groups(HeadLogic.Unlocked(account, catalog, cheat: false)) == "10:100,104|20:203",
            $"cheat-off unlocks were {Groups(HeadLogic.Unlocked(account, catalog, cheat: false))}");
        Assert(Groups(HeadLogic.Unlocked(account, catalog, cheat: true)) == "10:100,104,105,107,108,109|20:200,203",
            $"cheat-on unlocks were {Groups(HeadLogic.Unlocked(account, catalog, cheat: true))}");
        Assert(new[] { 100, 104, 203 }.All(id => HeadLogic.CanSet(account, catalog, false, id)) &&
               !new[] { 0, 105, 107, 108, 109, 200, 999, -1 }.Any(id => HeadLogic.CanSet(account, catalog, false, id)) &&
               new[] { 105, 107, 108, 200 }.All(id => HeadLogic.CanSet(account, catalog, true, id)) &&
               !new[] { 0, 999 }.Any(id => HeadLogic.CanSet(account, catalog, true, id)),
            "HeadLogic.CanSet accepted a locked or unknown head, or refused an unlocked one");
        // 下发的头像：表里有就原样；表里没有时用秘书舰的默认头像，秘书舰不在船坞（或没有默认头像）时用 1021051。
        Assert(HeadLogic.DisplayHead(account, catalog) == 203 &&
               HeadLogic.DisplayHead(account with { Character = account.Character with { Head = 999 } }, catalog) == 100 &&
               HeadLogic.DisplayHead(account with { Character = account.Character with { Head = 999, SecretaryId = 7 } }, catalog) ==
               HeadLogic.FallbackHead && HeadLogic.FallbackHead == 1021051,
            "HeadLogic.DisplayHead did not fall back to the secretary's default head, then 1021051");

        // 空目录（国服客户端没有 config_profile）：原来的行为。每艘舰娘（按船坞顺序去重）一个节点，ProfileID = (模板 − 1) / 10；
        // 作弊不影响；任意非 0 id 都能设置；头像原样下发。
        PlayerAccount legacy = account with
        {
            Character = account.Character with { Head = 999 },
            Dock = new HeroDock([new Hero(1, 21010111, 1), new Hero(2, 10210511, 1), new Hero(3, 21010112, 1)]),
        };
        Assert(HeadCatalog.Empty.IsEmpty &&
               Groups(HeadLogic.Unlocked(legacy, HeadCatalog.Empty, cheat: false)) == "2101011:2101011|1021051:1021051" &&
               Groups(HeadLogic.Unlocked(legacy, HeadCatalog.Empty, cheat: true)) == "2101011:2101011|1021051:1021051" &&
               HeadLogic.CanSet(legacy, HeadCatalog.Empty, false, 12345) && !HeadLogic.CanSet(legacy, HeadCatalog.Empty, true, 0) &&
               HeadLogic.DisplayHead(legacy, HeadCatalog.Empty) == 999,
            "an empty head catalog did not keep the old (TemplateId - 1) / 10 push, any-id SetHead and raw Head");
        return Task.CompletedTask;
    }

    public static async Task ModuleTest()
    {
        string configDir = ConfigDbLoader.BuildConfigDir(FindClientPath());
        Dictionary<int, ProfileRow> configured = ConfiguredHeads(configDir);
        HashSet<int> fashionIds = ConfigDbLoader.LoadAll<ConfigFashion>(configDir, "config_fashion.db").Keys.ToHashSet();
        Dictionary<int, int> shipOf = ShipFleetsOfTemplates(configDir);
        Assert(configured.Count >= 300 && configured.GetValueOrDefault(AsahiEventHead) is { Ship: AsahiShip, Type: 3 } &&
               configured.GetValueOrDefault(StarterShip) is { Ship: StarterShip, Type: 0, IsDefault: true } &&
               configured.GetValueOrDefault(AsahiShip) is { Ship: AsahiShip, Type: 0, IsDefault: true } &&
               shipOf.GetValueOrDefault(PlayerAccountFactory.DefaultHeroTemplateId) == StarterShip,
            $"config_profile ({configured.Count} rows) lacks the starter or Asahi heads used by these tests");
        // 按条件挑选（两版客户端行数不同）：初始舰娘的一个表情头像（作弊关时锁着）与一个换装头像（换装全部拥有，作弊关也解锁）、
        // 一个付费头像、一个朝日的模板。
        int starterExpression = configured.Values.Where(row => row.Ship == StarterShip && row.Type == 3).Min(row => row.Id);
        int starterSkin = configured.Values
            .Where(row => row.Ship == StarterShip && row.Type == 2 && fashionIds.Contains(row.Fashion)).Min(row => row.Id);
        ProfileRow buyable = configured.Values.Where(row => row.Type == 1).MinBy(row => row.Id)
            ?? throw new InvalidDataException("config_profile has no purchasable head");
        int asahiTemplate = shipOf.Where(pair => pair.Value == AsahiShip && pair.Key > 10_000_000).Min(pair => pair.Key);
        int unknownHead = configured.Keys.Max() + 1;

        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-heads-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            foreach (string profile in new[] { "heads-off", "heads-on", "heads-buy" })
                await repo.SaveAccountAsync(PlayerAccountFactory.CreateDefault(profile, T0));
            PlayerAccount staleSeed = PlayerAccountFactory.CreateDefault("heads-stale", T0);
            await repo.SaveAccountAsync(staleSeed with
            {
                Character = staleSeed.Character with { SecretaryId = 2, Head = unknownHead },
                Dock = staleSeed.Dock with { Heroes = [.. staleSeed.Dock.Heroes, new Hero(2, asahiTemplate, 1)] },
            });
            using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
                Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            GameServices Services(string profile, params string[] flags) => new(repo, ServerOptions.Parse(
                [.. new[] { "--no-cheats", "--real-resource-cost=off", "--data=" + dataRoot, "--client-path=" + FindClientPath(), "--profile-id=" + profile }, .. flags]), loggerFactory);
            async Task<PlayerAccount> Load(string profile) =>
                await repo.LoadAccountAsync(profile) ?? throw new InvalidDataException("account missing");
            async Task<List<(int Ship, List<int> Ids)>> SyncHeads(GameServices services, string profile) =>
                DecodeUnlockedList(PushRet((await services.BuildSyncPushesAsync(profile, checked((uint)T0), CancellationToken.None))
                    .Single(push => Method(push) == HeadLogic.UnlockedListMethod)));

            // 服务端的目录与独立读到的配置一致；模板换算按 config_ship_main / config_ship_info（个别模板不是「去掉末位」）。
            GameServices off = Services("heads-off");
            Assert(!off.Cheats.Heads, "--no-cheats left the heads cheat on");
            HeadCatalog catalog = ProfileHeadLoader.Catalog;
            Assert(catalog.Rows.Select(row => row.Id).Order().SequenceEqual(configured.Keys.Order()) &&
                   catalog.Rows.All(row => configured[row.Id] == new ProfileRow(row.Id, row.BelongShipId, row.Type, row.IsDefault, row.Fashion)),
                "ProfileHeadLoader did not load config_profile row for row");
            List<int> mismatched = shipOf.Where(pair => (pair.Key - 1) / 10 != pair.Value).Select(pair => pair.Key).Take(50).ToList();
            Assert(mismatched.Count > 0 && mismatched.All(templateId => catalog.ShipFleetOf(templateId) == shipOf[templateId]) &&
                   catalog.ShipFleetOf(PlayerAccountFactory.DefaultHeroTemplateId) == StarterShip,
                "HeadCatalog.ShipFleetOf did not map templates through config_ship_main and config_ship_info");

            // ── 作弊关（新档案：只有初始舰娘，换装在加载时全部拥有）：默认头像 + 已拥有的换装头像 + 当前头像，没有表情头像。
            PlayerAccount offAccount = await off.GetOrCreateAccountAsync("heads-off", CancellationToken.None);
            HashSet<int> ownedShips = offAccount.Dock.Heroes.Select(hero => shipOf[hero.TemplateId]).ToHashSet();
            HashSet<int> expectedOff = configured.Values
                .Where(row => row.Id == offAccount.Character.Head ||
                              (ownedShips.Contains(row.Ship) &&
                               ((row.Type == 0 && row.IsDefault) || (row.Type == 2 && fashionIds.Contains(row.Fashion)))))
                .Select(row => row.Id).ToHashSet();
            List<(int Ship, List<int> Ids)> offNodes = await SyncHeads(off, "heads-off");
            Assert(offNodes.SelectMany(node => node.Ids).ToHashSet().SetEquals(expectedOff) &&
                   offNodes.All(node => node.Ids.All(id => configured[id].Ship == node.Ship)) &&
                   expectedOff.Contains(StarterShip) && expectedOff.Contains(starterSkin) &&
                   !expectedOff.Any(id => configured[id].Type is 1 or 3),
                $"the cheat-off login push was [{string.Join(",", offNodes.SelectMany(node => node.Ids))}], expected [{string.Join(",", expectedOff.Order())}]");

            var offModule = new UserModule(new UserService(off), off);
            // 锁着的头像（初始舰娘的表情）、表里没有的 id、没有参数、没有 ProfileID：都回 3600006、不改档，应答前仍推一次原来的头像。
            foreach (TRequest locked in new[]
                     {
                         SetHead(StarterShip, starterExpression), SetHead(AsahiShip, AsahiEventHead), SetHead(StarterShip, unknownHead),
                         new TRequest("user.SetHead"), new TRequest("user.SetHead", new ProtocolPackage().Write(0x08, (ulong)StarterShip).ToArray()),
                     })
            {
                ModuleResult refused = await offModule.HandleAsync(At(off, "heads-off"), locked);
                Assert(refused.Err == HeadLogic.ErrLocked && refused.Err == 3600006 && refused.ErrMsg.Length > 0 &&
                       EchoedHead(refused) == StarterShip && refused.PostPushes.Count == 0 &&
                       (await Load("heads-off")).Character.Head == StarterShip,
                    $"a locked head was not refused with 3600006 (err={refused.Err}, echoed={EchoedHead(refused)})");
            }
            // 已拥有换装的换装头像可以设置；应答前推送的用户信息里已是新头像（客户端收到应答就重画资料页的头像）。
            ModuleResult skin = await offModule.HandleAsync(At(off, "heads-off"), SetHead(StarterShip, starterSkin));
            Assert(skin.Err == 0 && EchoedHead(skin) == starterSkin && (await Load("heads-off")).Character.Head == starterSkin,
                $"an owned skin head {starterSkin} was not accepted (err={skin.Err})");

            // user.GetHeadBuyCount：回显请求的舰娘，次数 0；没有参数时 0/0。
            ModuleResult count = await offModule.HandleAsync(At(off, "heads-off"),
                new TRequest("user.GetHeadBuyCount", new ProtocolPackage().Write(0x08, (ulong)AsahiShip).ToArray()));
            ModuleResult emptyCount = await offModule.HandleAsync(At(off, "heads-off"), new TRequest("user.GetHeadBuyCount"));
            Assert(count.Err == 0 && Fields(count.Ret) is [(1, 0, (ulong)AsahiShip, _), (2, 0, 0, _)] &&
                   emptyCount.Err == 0 && emptyCount.Ret.SequenceEqual(new byte[] { 0x08, 0x00, 0x10, 0x00 }),
                "user.GetHeadBuyCount did not echo the requested ship with Count 0");

            // ── 作弊开：登录推送 config_profile 全表，每个节点的舰娘就是这些头像的 belongshipid。
            GameServices on = Services("heads-on", "--cheat-heads");
            List<(int Ship, List<int> Ids)> onNodes = await SyncHeads(on, "heads-on");
            Assert(on.Cheats.Heads && onNodes.SelectMany(node => node.Ids).Order().SequenceEqual(configured.Keys.Order()) &&
                   onNodes.Select(node => node.Ship).Distinct().Count() == onNodes.Count &&
                   onNodes.All(node => node.Ids.All(id => configured[id].Ship == node.Ship)),
                "the heads cheat did not push every config_profile id grouped by belongshipid");
            var onModule = new UserModule(new UserService(on), on);
            ModuleResult asahi = await onModule.HandleAsync(At(on, "heads-on"), SetHead(AsahiShip, AsahiEventHead));
            Assert(asahi.Err == 0 && EchoedHead(asahi) == AsahiEventHead && (await Load("heads-on")).Character.Head == AsahiEventHead,
                $"the heads cheat did not accept event head {AsahiEventHead} (err={asahi.Err})");
            ModuleResult unknownOn = await onModule.HandleAsync(At(on, "heads-on"), SetHead(StarterShip, unknownHead));
            Assert(unknownOn.Err == HeadLogic.ErrLocked && EchoedHead(unknownOn) == AsahiEventHead &&
                   (await Load("heads-on")).Character.Head == AsahiEventHead,
                "the heads cheat accepted an id that is not in config_profile");

            // 关掉作弊后当前头像仍算已解锁（推送里有、可以再次设置），其他表情头像又锁上。
            GameServices offAgain = Services("heads-on");
            List<(int Ship, List<int> Ids)> againNodes = await SyncHeads(offAgain, "heads-on");
            var againModule = new UserModule(new UserService(offAgain), offAgain);
            Assert(againNodes.Any(node => node.Ship == AsahiShip && node.Ids.SequenceEqual([AsahiEventHead])) &&
                   (await againModule.HandleAsync(At(offAgain, "heads-on"), SetHead(AsahiShip, AsahiEventHead))).Err == 0 &&
                   (await againModule.HandleAsync(At(offAgain, "heads-on"), SetHead(StarterShip, starterExpression))).Err == HeadLogic.ErrLocked &&
                   (await Load("heads-on")).Character.Head == AsahiEventHead,
                "turning the cheat off did not keep the current head selectable (and only it)");

            // user.BuyHead：两种情况都回 110025，不扣钻石、不改档（按原规则扣费也开着）。
            foreach (GameServices buyer in new[] { Services("heads-buy", "--real-resource-cost"), Services("heads-buy", "--real-resource-cost", "--cheat-heads") })
            {
                PlayerAccount before = await Load("heads-buy");
                ModuleResult bought = await new UserModule(new UserService(buyer), buyer)
                    .HandleAsync(At(buyer, "heads-buy"), new TRequest("user.BuyHead", new ProtocolPackage()
                        .Write(0x08, (ulong)buyable.Ship).Write(0x10, (ulong)buyable.Id).ToArray()));
                PlayerAccount after = await Load("heads-buy");
                Assert(bought.Err == HeadLogic.ErrBuyUnavailable && bought.Err == 110025 && bought.Ret.Length == 0 &&
                       bought.PrePushes.Count == 0 && bought.PostPushes.Count == 0 &&
                       after.Character == before.Character && after.Character.Diamond == PlayerAccountFactory.DefaultDiamond,
                    $"user.BuyHead was not refused with 110025 or changed the save (cheat={buyer.Cheats.Heads}, err={bought.Err})");
            }

            // 存档里的头像不在 config_profile 里：用户信息改发秘书舰（朝日）的默认头像，存档不改；推送里也没有它。
            GameServices stale = Services("heads-stale");
            int shown = HeadOf(PushRet(await stale.BuildUpdateUserInfoPushAsync("heads-stale", checked((uint)T0), CancellationToken.None)));
            int reread = HeadOf(GameServices.EncodeGetUserInfo(await stale.GetOrCreateAccountAsync("heads-stale", CancellationToken.None)));
            List<(int Ship, List<int> Ids)> staleNodes = await SyncHeads(stale, "heads-stale");
            Assert(shown == AsahiShip && reread == AsahiShip && (await Load("heads-stale")).Character.Head == unknownHead &&
                   !staleNodes.Any(node => node.Ids.Contains(unknownHead)) &&
                   staleNodes.Any(node => node.Ship == AsahiShip && node.Ids.Contains(AsahiShip)),
                $"a head missing from config_profile was shown as {shown}/{reread} instead of the secretary's default {AsahiShip}");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }

    /// <summary>
    /// 起真实服务端：先开「头像全部解锁」（--no-cheats 之后单独打开），登录推送全部头像、设置活动表情头像后经
    /// user.UpdateUserInfo 回显、user.GetUserInfo 读回、profiles.db 保存；再在同一数据目录关掉作弊重启，推送只剩默认、
    /// 换装与当前头像，设置另一个锁着的表情头像回 3600006 且头像不变。
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
        string clientPath = FindClientPath();
        string configDir = ConfigDbLoader.BuildConfigDir(clientPath);
        Dictionary<int, ProfileRow> configured = ConfiguredHeads(configDir);
        HashSet<int> fashionIds = ConfigDbLoader.LoadAll<ConfigFashion>(configDir, "config_fashion.db").Keys.ToHashSet();
        Assert(configured.GetValueOrDefault(AsahiEventHead) is { Ship: AsahiShip, Type: 3 },
            $"config_profile lacks Asahi's event head {AsahiEventHead}");
        int starterExpression = configured.Values.Where(row => row.Ship == StarterShip && row.Type == 3).Min(row => row.Id);
        // 作弊关时的期望：初始舰娘的默认头像与换装头像（换装全部拥有），加上作弊下选的朝日活动头像。
        HashSet<int> expectedOff = configured.Values
            .Where(row => row.Id == AsahiEventHead ||
                          (row.Ship == StarterShip && ((row.Type == 0 && row.IsDefault) || (row.Type == 2 && fashionIds.Contains(row.Fashion)))))
            .Select(row => row.Id).ToHashSet();

        string data = Path.Combine(Path.GetTempPath(), "blueoath-heads-tcp-" + Guid.NewGuid().ToString("N"));
        const string profileId = "heads-player";
        await new SqliteGameRepository(data).SaveAccountAsync(
            PlayerAccountFactory.CreateDefault(profileId, checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 60));
        try
        {
            // ── 第一次启动：作弊开。
            await using (var server = await TestServer.StartAsync(serverDll, data, profileId, clientPath, "--cheat-heads"))
            {
                Assert(server.CheatEcho("heads"), "the ready JSON did not echo the enabled heads cheat");
                List<TResponse> sync = await server.LoginAsync();
                List<(int Ship, List<int> Ids)> nodes = DecodeUnlockedList(
                    sync.Single(push => push.Method == HeadLogic.UnlockedListMethod).Ret ?? []);
                Assert(nodes.SelectMany(node => node.Ids).Order().SequenceEqual(configured.Keys.Order()),
                    "the TCP login sync did not unlock every config_profile id under the heads cheat");

                var pushes = new List<TResponse>();
                TResponse set = await server.RoundTrip("user.SetHead", SetHeadArgs(AsahiShip, AsahiEventHead), pushes);
                Assert(set.Err == 0 && pushes.Any(push => push.Method == "user.UpdateUserInfo") &&
                       HeadOf(pushes.Last(push => push.Method == "user.UpdateUserInfo").Ret ?? []) == AsahiEventHead,
                    $"user.SetHead {AsahiEventHead} over TCP failed or was not echoed before the response (err={set.Err})");
                TResponse reread = await server.RoundTrip("user.GetUserInfo", null);
                Assert(HeadOf(reread.Ret ?? []) == AsahiEventHead,
                    $"user.GetUserInfo did not return the saved head ({HeadOf(reread.Ret ?? [])})");
            }
            PlayerAccount? saved = await new SqliteGameRepository(data).LoadAccountAsync(profileId);
            Assert(saved?.Character.Head == AsahiEventHead, $"profiles.db did not keep head {AsahiEventHead} (saved={saved?.Character.Head})");

            // ── 第二次启动：同一数据目录，作弊关。
            await using (var server = await TestServer.StartAsync(serverDll, data, profileId, clientPath))
            {
                Assert(!server.CheatEcho("heads"), "the ready JSON did not echo the disabled heads cheat");
                List<TResponse> sync = await server.LoginAsync();
                HashSet<int> unlocked = DecodeUnlockedList(sync.Single(push => push.Method == HeadLogic.UnlockedListMethod).Ret ?? [])
                    .SelectMany(node => node.Ids).ToHashSet();
                Assert(unlocked.SetEquals(expectedOff),
                    $"the cheat-off TCP login pushed [{string.Join(",", unlocked.Order())}], expected [{string.Join(",", expectedOff.Order())}]");

                var pushes = new List<TResponse>();
                TResponse refused = await server.RoundTrip("user.SetHead", SetHeadArgs(StarterShip, starterExpression), pushes);
                Assert(refused.Err == 3600006 && pushes.Any(push => push.Method == "user.UpdateUserInfo") &&
                       HeadOf(pushes.Last(push => push.Method == "user.UpdateUserInfo").Ret ?? []) == AsahiEventHead,
                    $"a locked head over TCP was not refused with 3600006 (err={refused.Err})");
            }
            saved = await new SqliteGameRepository(data).LoadAccountAsync(profileId);
            Assert(saved?.Character.Head == AsahiEventHead, $"a refused SetHead changed the saved head to {saved?.Character.Head}");
        }
        finally
        {
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

    /// <summary>一个测试用服务端进程（作弊全关、资源免费，另加 <c>flags</c>）与它的 TCP 连接。</summary>
    private sealed class TestServer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly JsonDocument _ready;
        private readonly TcpClient _client = new();
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        private readonly string _profileId;
        private NetworkStream? _stream;

        private TestServer(Process process, JsonDocument ready, string profileId)
        {
            _process = process;
            _ready = ready;
            _profileId = profileId;
        }

        public static async Task<TestServer> StartAsync(string serverDll, string data, string profileId, string clientPath,
            params string[] flags)
        {
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
                     }.Concat(flags))
                startInfo.ArgumentList.Add(arg);
            var process = new Process { StartInfo = startInfo };
            try
            {
                Assert(process.Start(), "heads test server did not start");
                // stderr 不读会在缓冲区写满后卡住服务端。
                _ = process.StandardError.ReadToEndAsync();
                string? readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                var server = new TestServer(process, JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready")), profileId);
                await server._client.ConnectAsync("127.0.0.1", server._ready.RootElement.GetProperty("gameLoginPort").GetInt32(), server._timeout.Token);
                server._stream = server._client.GetStream();
                return server;
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
        }

        public bool CheatEcho(string key) =>
            _ready.RootElement.TryGetProperty("cheats", out JsonElement cheats) &&
            cheats.TryGetProperty(key, out JsonElement value) && value.GetBoolean();

        public async Task<TResponse> RoundTrip(string method, byte[]? args, ICollection<TResponse>? prePushes = null)
        {
            byte[] request = TMessageCodec.EncodeRequest(new TRequest(method, args, 1));
            await NetSocketFrameCodec.WriteAsync(_stream!, request, NetSocketFrameCodec.TypeData, _timeout.Token);
            while (true)
            {
                TResponse response = await NextAsync();
                if (response.IsResponse == 1) return response;
                prePushes?.Add(response);
            }
        }

        /// <summary>登录并读完 user.GetUserInfo 之后的登录同步推送（最后一条是 talentTree.TalentTreeAllList）。</summary>
        public async Task<List<TResponse>> LoginAsync()
        {
            await RoundTrip("player.Login", GameLoginCodec.Encode(new TArgLogin(_profileId, 1, "open", "hash")));
            TResponse userInfo = await RoundTrip("user.GetUserInfo", null);
            Assert(userInfo.Err == 0, "user.GetUserInfo failed");
            var sync = new List<TResponse>();
            while (sync.Count < 100 && (sync.Count == 0 || sync[^1].Method != "talentTree.TalentTreeAllList"))
                sync.Add(await NextAsync());
            return sync;
        }

        private async Task<TResponse> NextAsync()
        {
            var frame = await NetSocketFrameCodec.ReadFromServerAsync(_stream!, _timeout.Token);
            Assert(frame is not null, "the server closed the connection");
            return TMessageCodec.DecodeResponse(frame!.Value.Payload);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (InvalidOperationException)
            {
            }
            _process.Dispose();
            _ready.Dispose();
            _timeout.Dispose();
        }
    }

    /// <summary>直接读客户端 config_profile（与服务端加载器互相独立）。</summary>
    private static Dictionary<int, ProfileRow> ConfiguredHeads(string configDir) =>
        ConfigDbLoader.LoadAll<ConfigProfile>(configDir, "config_profile.db").Values
            .Where(row => row.Id > 0)
            .ToDictionary(row => checked((int)row.Id),
                row => new ProfileRow(checked((int)row.Id), checked((int)row.Belongshipid), checked((int)row.Type), row.Default == 1,
                    checked((int)row.Fashion)));

    /// <summary>舰娘模板 → sf_id：config_ship_main.ship_info_id 再查 config_ship_info.sf_id（与服务端的换算互相独立）。</summary>
    private static Dictionary<int, int> ShipFleetsOfTemplates(string configDir)
    {
        Dictionary<int, ConfigShipInfo> infos = ConfigDbLoader.LoadAll<ConfigShipInfo>(configDir, "config_ship_info.db");
        return ConfigDbLoader.LoadAll<ConfigShipMain>(configDir, "config_ship_main.db")
            .Where(pair => infos.TryGetValue(checked((int)pair.Value.ShipInfoId), out ConfigShipInfo? info) && info.SfId > 0)
            .ToDictionary(pair => pair.Key, pair => checked((int)infos[checked((int)pair.Value.ShipInfoId)].SfId));
    }

    /// <summary>解 TNewHeadUnlockedList：只允许 UnlockedList(1) 的节点，节点里一个 ShipFleetId(1) 与非 packed 的 ProfileID(2)。</summary>
    private static List<(int Ship, List<int> Ids)> DecodeUnlockedList(byte[] payload)
    {
        var nodes = new List<(int, List<int>)>();
        foreach ((int field, int wire, _, byte[] node) in Fields(payload))
        {
            Assert(field == 1 && wire == 2, $"unexpected field {field} (wire {wire}) in TNewHeadUnlockedList");
            int ship = -1;
            var ids = new List<int>();
            foreach ((int f, int w, ulong value, _) in Fields(node))
            {
                Assert(w == 0 && f is 1 or 2, $"unexpected field {f} (wire {w}) in TNewHeadNode (ProfileID must not be packed)");
                if (f == 1)
                {
                    Assert(ship == -1, "a TNewHeadNode carried two ShipFleetIds");
                    ship = checked((int)value);
                }
                else ids.Add(checked((int)value));
            }
            Assert(ship >= 0 && ids.Count > 0, "a TNewHeadNode had no ShipFleetId or no ProfileID");
            nodes.Add((ship, ids));
        }
        return nodes;
    }

    private static string Groups(IReadOnlyList<(int ShipFleetId, IReadOnlyList<int> ProfileIds)> groups) =>
        string.Join("|", groups.Select(group => $"{group.ShipFleetId}:{string.Join(",", group.ProfileIds)}"));

    private static byte[] SetHeadArgs(int ship, int profileId) =>
        new ProtocolPackage().Write(0x08, (ulong)ship).Write(0x10, unchecked((ulong)profileId)).ToArray();

    private static TRequest SetHead(int ship, int profileId) => new("user.SetHead", SetHeadArgs(ship, profileId));

    /// <summary>应答前推送的 user.UpdateUserInfo 里的头像。</summary>
    private static int EchoedHead(ModuleResult result) => HeadOf(result.PrePushes
        .Select(push => TMessageCodec.DecodeResponse(push))
        .Single(push => push.Method == "user.UpdateUserInfo").Ret ?? []);

    /// <summary>TGetUserInfoRet.Head(4)。</summary>
    private static int HeadOf(byte[] userInfoRet) => checked((int)ProtocolDecoder.DecodeVarintField(userInfoRet, 4));

    private static GameContext At(GameServices services, string profileId) =>
        new() { ProfileId = profileId, Now = T0, Ct = CancellationToken.None, Services = services };

    private static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;

    private static byte[] PushRet(byte[] push) => TMessageCodec.DecodeResponse(push).Ret ?? [];
}
