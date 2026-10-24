using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server;
using BlueOath.Server.Protocols;
using BlueOath.Storage;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// 指挥室装饰（interactionitem.*）的测试：<see cref="PureTest"/> 覆盖目录、编码、持有、背包迁移、三种设置与 decorations.json；
/// <see cref="ModuleTest"/> 走 GameServices 与 InteractionItemModule；<see cref="IntegrationTest"/> 起服务端走 TCP。
/// 客户端目录经 <see cref="TestSupport.FindClientPath"/>（BLUEOATH_CLIENT_PATH 可指向未热更的 1.4.0 客户端，此时只跑 -- --decor）。
/// 由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class DecorTests
{
    // Program.cs 的顶层函数会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private static string FindRepositoryRoot() => TestSupport.FindRepositoryRoot();

    private static string FindClientPath() => TestSupport.FindClientPath();

    // 日服配置（热更版与 1.4.0 相同的部分）：
    // 套装 1 祝100日記念セット = 250011–250019，套装 3 聖誕装飾品セット = 255801–255809；
    // 250001 鯉のぼり、253009 クリスマスツリー（物件 59009，interaction_item_type 1）是可单独显示的摆件；
    // 251001–251011、252001–252005 是海报；海报框是 config_parameter 377 的 8200100–8200102；
    // 45 是水晶球（type 3），1012011 是 config_interaction_figurte 的小人；36–40 是套装家具物件（type 2）。
    private const int SetAnniversary = 1, SetChristmas = 3, SetItem = 250011, Koinobori = 250001, Tree = 253009;
    private const int TreeItem = 59009, Poster1 = 251001, Poster2 = 251002, Poster16 = 252005, Ball = 45, Figure = 1012011;
    private const int Point1 = 8200100, Point2 = 8200101, Point3 = 8200102, Unknown = 259_998;
    private static readonly int[] Points = [Point1, Point2, Point3];

    public static Task PureTest()
    {
        string configDir = ConfigDbLoader.BuildConfigDir(FindClientPath());
        DecorCatalog catalog = DecorCatalogLoader.LoadConfig(configDir, NullLogger.Instance);
        CatalogChecks(configDir, catalog);
        CodecChecks();
        PlayerAccount owned = OwnershipChecks(catalog);
        SetterChecks(catalog, owned);
        SaveEditorChecks(owned);
        ModFileChecks(catalog);
        return Task.CompletedTask;
    }

    private static void CatalogChecks(string configDir, DecorCatalog catalog)
    {
        int rows = 0;
        ConfigDbLoader.LoadRows(configDir, "config_interaction_item_bag.db", (_, _, _) => rows++);
        Assert(catalog.IsLoaded && catalog.BagList.Count == rows,
            $"the decor catalog did not load every config_interaction_item_bag row ({catalog.BagList.Count} of {rows})");
        Assert(catalog.GroupList.Select(group => group.Id).SequenceEqual([SetAnniversary, SetChristmas]) &&
               catalog.Groups[SetAnniversary].Bags.SequenceEqual(Enumerable.Range(250011, 9)) &&
               catalog.Groups[SetChristmas].Bags.SequenceEqual(Enumerable.Range(255801, 9)) &&
               catalog.GroupList.All(group => group.Bags.All(id => catalog.Bags[id].Group == group.Id)),
            $"the furniture sets are not {{1, 3}} with their nine pieces: {string.Join(",", catalog.GroupList.Select(group => group.Id))}");
        Assert(catalog.PosterPoints.SequenceEqual(Points),
            $"the poster frames were not read from config_parameter 377: {string.Join(",", catalog.PosterPoints)}");
        List<int> posters = catalog.BagList.Where(bag => bag.Type == DecorCatalog.BagTypePoster).Select(bag => bag.Id).ToList();
        Assert(posters.SequenceEqual(Enumerable.Range(251001, 11).Concat(Enumerable.Range(252001, 5))),
            $"the posters are not 251001–251011 and 252001–252005: {string.Join(",", posters)}");
        Assert(catalog.InteractionItemTypes[TreeItem] == 1 && catalog.Bags[Tree].InteractionItem == TreeItem &&
               DecorCatalog.IsToggleable(catalog.Bags[Tree]) && DecorCatalog.IsToggleable(catalog.Bags[Koinobori]) &&
               !DecorCatalog.IsToggleable(catalog.Bags[SetItem]) && !DecorCatalog.IsToggleable(catalog.Bags[Poster1]),
            "only group-0 type-0 decorations may be shown one by one");
        Assert(catalog.InteractionItemTypes[Ball] == DecorCatalog.ItemTypeSnowGlobe && catalog.Figures.Contains(Figure),
            "the crystal ball or the figure table was not loaded");
        Assert(DecorCatalog.IsDecorItemId(250_000) && DecorCatalog.IsDecorItemId(259_999) &&
               !DecorCatalog.IsDecorItemId(249_999) && !DecorCatalog.IsDecorItemId(260_000) &&
               !DecorCatalog.IsDecorItemId(25) && !DecorCatalog.IsDecorItemId(-250_001),
            "the 25xxxx decoration id range is wrong");
        Assert(!DecorCatalogLoader.LoadConfig(Path.Combine(Path.GetTempPath(), "blueoath-no-config-" + Guid.NewGuid().ToString("N"))).IsLoaded,
            "a missing config directory produced decorations");
    }

    private static void CodecChecks()
    {
        // 所有子消息的标量都写出，包括 0（num 缺失会让客户端整条推送报错，state 缺失等于没有持有）。
        byte[] encoded = PlayerDataCodec.Encode(new InteractionItemRet(
            BallToys: [new BallToyStateInfo(Ball, 0)],
            BagItems: [new InteractionBagItemInfo(Koinobori, 1, 0)],
            Decorate: [new DecorateMutexBagGroupInfo(1, 0)],
            Posters: [new PosterStateInfo(Point1, 0)]));
        Assert(Convert.ToHexString(encoded) == "2204082D1000" + "2A080891A10F10011800" + "320408011000" + "420708A4BFF4031000",
            "TInteractionItemRet did not write every scalar in field order 4/5/6/8: " + Convert.ToHexString(encoded));
        InteractionItemRet decoded = PlayerDataCodec.DecodeInteractionItemRet(encoded);
        Assert(decoded.BallToys!.SequenceEqual([new BallToyStateInfo(Ball, 0)]) &&
               decoded.BagItems!.SequenceEqual([new InteractionBagItemInfo(Koinobori, 1, 0)]) &&
               decoded.Decorate!.SequenceEqual([new DecorateMutexBagGroupInfo(1, 0)]) &&
               decoded.Posters!.SequenceEqual([new PosterStateInfo(Point1, 0)]),
            "TInteractionItemRet did not round-trip");
        Assert(PlayerDataCodec.Encode(new InteractionItemRet()).Length == 0, "an empty TInteractionItemRet is not empty");
        // 客户端关闭套装时 SelectId = 0 可能不在线上；缺失的字段按 0。
        Assert(PlayerDataCodec.DecodeDecorateMutexBagGroupArg([0x08, 0x01]) == new DecorateMutexBagGroupArg(1, 0) &&
               PlayerDataCodec.DecodeInteractionItemArg(PlayerDataCodec.EncodeIntPair(Tree, 1)) == new InteractionItemArg(Tree, 1) &&
               PlayerDataCodec.DecodePosterStateArg(PlayerDataCodec.EncodeIntPair(Point2, Poster2)) == new PosterStateArg(Point2, Poster2) &&
               PlayerDataCodec.DecodeInteractionBallToyArg(PlayerDataCodec.EncodeIntPair(Ball, Figure)) == new InteractionBallToyArg(Ball, Figure) &&
               PlayerDataCodec.DecodeInteractionItemArg([]) == new InteractionItemArg(),
            "the interactionitem request arguments did not decode");
    }

    private static PlayerAccount OwnershipChecks(DecorCatalog catalog)
    {
        PlayerAccount fresh = PlayerAccountFactory.CreateDefault("decor", 1);
        PlayerAccount owned = DecorLogic.EnsureAll(fresh, catalog);
        PlayerDecor decor = owned.Decor!;
        Assert(decor.Items.Select(item => item.Id).SequenceEqual(catalog.BagList.Select(bag => bag.Id)) &&
               decor.Items.All(item => item.Visible == 0) &&
               decor.Themes.SequenceEqual([new DecorTheme(DecorCatalog.MutexFurnitureTheme, 0)]) &&
               decor.Posters.SequenceEqual(Points.Select(point => new DecorPoster(point, 0))) && decor.BallToys is null,
            "a new account does not own every decoration (hidden), no set and empty poster frames");
        Assert(ReferenceEquals(DecorLogic.EnsureAll(owned, catalog), owned), "EnsureAll is not idempotent");
        Assert(ReferenceEquals(DecorLogic.EnsureAll(fresh, DecorCatalog.Empty), fresh), "an empty catalog changed the account");
        PlayerAccount christmas = owned with { Decor = decor with { Themes = [new DecorTheme(1, SetChristmas)] } };
        Assert(ReferenceEquals(DecorLogic.EnsureAll(christmas, catalog), christmas), "a valid set was not kept");

        // 失效的数据：不在配置里的套装改回 0（主页 onStart 会在未知套装上报错）、只保留摆件的显示状态、
        // 海报框按配置重排、失效的海报改回 0、失效的水晶球去掉、配置里没有的装饰品不再出现。
        PlayerAccount stale = owned with
        {
            Decor = new PlayerDecor(
                [new DecorBagItem(Tree, 1), new DecorBagItem(SetItem, 1), new DecorBagItem(Poster1, 1), new DecorBagItem(Unknown, 1)],
                [new DecorTheme(1, 999)],
                [new DecorPoster(Point1, Poster1), new DecorPoster(Point2, Koinobori), new DecorPoster(Point3, Unknown), new DecorPoster(123, Poster2)],
                [new DecorBallToy(Ball, Figure), new DecorBallToy(59005, Figure), new DecorBallToy(47, 999)]),
        };
        PlayerDecor repaired = DecorLogic.EnsureAll(stale, catalog).Decor!;
        Assert(repaired.Items.Count == catalog.BagList.Count && repaired.Items.All(item => catalog.Bags.ContainsKey(item.Id)) &&
               repaired.Items.Where(item => item.Visible == 1).Select(item => item.Id).SequenceEqual([Tree]),
            "EnsureAll kept an unknown decoration or a visibility flag on a set piece or poster");
        Assert(repaired.Themes.SequenceEqual([new DecorTheme(1, 0)]), "a set id the client config lacks was not reset to 0");
        Assert(repaired.Posters.SequenceEqual([new DecorPoster(Point1, Poster1), new DecorPoster(Point2, 0), new DecorPoster(Point3, 0)]),
            "stale poster frames were not repaired");
        Assert(repaired.BallToys!.SequenceEqual([new DecorBallToy(Ball, Figure)]), "stale crystal balls were not dropped");

        // 推送只放目录里有的 id，即使存档没有修复过。
        InteractionItemRet raw = DecorLogic.Snapshot(stale, catalog);
        Assert(raw.BagItems!.Select(item => item.Id).SequenceEqual([Tree, SetItem, Poster1]) &&
               raw.BagItems!.All(item => item.Num == 1) && raw.BagItems!.Single(item => item.State == 1).Id == Tree &&
               raw.Decorate!.SequenceEqual([new DecorateMutexBagGroupInfo(1, 0)]) &&
               raw.Posters!.SequenceEqual([new PosterStateInfo(Point1, Poster1), new PosterStateInfo(Point2, 0), new PosterStateInfo(Point3, 0)]) &&
               raw.BallToys!.SequenceEqual([new BallToyStateInfo(Ball, Figure)]),
            "the snapshot pushed an id the client config lacks");
        InteractionItemRet login = DecorLogic.Snapshot(owned, catalog);
        Assert(login.BagItems!.Count == catalog.BagList.Count && login.BagItems.All(item => item is { Num: 1, State: 0 }) &&
               login.Posters!.Count == 3 && login.BallToys!.Count == 0,
            "the login snapshot does not list every owned decoration with num 1 and state 0");

        // 背包迁移：25xxxx 移出普通背包；其它道具不动；目录没读到时不动。
        PlayerAccount polluted = fresh with
        {
            Bag = new PlayerBag([new BagItem(10007, 5), new BagItem(250032, 1), new BagItem(Tree, 2), new BagItem(Unknown, 1)]),
        };
        PlayerAccount cleaned = DecorLogic.CleanupBag(polluted, catalog);
        Assert(cleaned.Bag!.Items.SequenceEqual([new BagItem(10007, 5)]) && cleaned.Decor!.Items.Any(item => item.Id == 250032),
            "decorations were not moved out of the bag");
        Assert(ReferenceEquals(DecorLogic.CleanupBag(cleaned, catalog), cleaned) &&
               ReferenceEquals(DecorLogic.CleanupBag(polluted, DecorCatalog.Empty), polluted),
            "the bag cleanup changed a clean bag, or ran without a catalog");

        // 获得：目录里有的记为持有（默认收起），已持有或未知的不变。
        PlayerAccount partial = fresh with { Decor = new PlayerDecor([new DecorBagItem(Tree, 1)], [new DecorTheme(1, 0)], []) };
        PlayerAccount granted = DecorLogic.Grant(partial, 250032, catalog);
        Assert(granted.Decor!.Items.SequenceEqual([new DecorBagItem(Tree, 1), new DecorBagItem(250032, 0)]) &&
               ReferenceEquals(DecorLogic.Grant(granted, 250032, catalog), granted) &&
               ReferenceEquals(DecorLogic.Grant(granted, Unknown, catalog), granted),
            "granting a decoration did not record it once");
        return owned;
    }

    private static void SetterChecks(DecorCatalog catalog, PlayerAccount owned)
    {
        static void Rejected(DecorLogic.SetResult result, PlayerAccount before, string what) =>
            Assert(result.Error is not null && ReferenceEquals(result.Account, before) && result.Push is null && !result.Changed,
                $"{what} was not rejected without changes");

        // 套装：groupType 必须是 1；SelectId 为 0 或至少持有一件的配置套装。
        Rejected(DecorLogic.SetTheme(owned, catalog, 2, SetAnniversary), owned, "group type 2");
        Rejected(DecorLogic.SetTheme(owned, catalog, 1, 999), owned, "an unknown set");
        PlayerAccount noChristmas = owned with
        {
            Decor = owned.Decor! with { Items = owned.Decor.Items.Where(item => catalog.Bags[item.Id].Group != SetChristmas).ToList() },
        };
        Rejected(DecorLogic.SetTheme(noChristmas, catalog, 1, SetChristmas), noChristmas, "a set without any owned piece");
        DecorLogic.SetResult opened = DecorLogic.SetTheme(owned, catalog, 1, SetChristmas);
        Assert(opened is { Error: null, Changed: true } &&
               opened.Account.Decor!.Themes.SequenceEqual([new DecorTheme(1, SetChristmas)]) &&
               opened.Push!.Decorate!.SequenceEqual([new DecorateMutexBagGroupInfo(1, SetChristmas)]) &&
               opened.Push.BagItems is null && opened.Push.Posters is null,
            "opening a set did not save it and push only the decorate field");
        DecorLogic.SetResult again = DecorLogic.SetTheme(opened.Account, catalog, 1, SetChristmas);
        Assert(again is { Error: null, Changed: false } && ReferenceEquals(again.Account, opened.Account) && again.Push is not null,
            "re-opening the current set was not a pushed no-op");
        DecorLogic.SetResult closed = DecorLogic.SetTheme(opened.Account, catalog, 1, 0);
        Assert(closed is { Error: null, Changed: true } && closed.Account.Decor!.Themes.Single().SelectId == 0,
            "closing the set did not save 0");
        PlayerAccount onePiece = owned with
        {
            Decor = owned.Decor! with { Items = owned.Decor.Items.Where(item => catalog.Bags[item.Id].Group != SetAnniversary || item.Id == SetItem).ToList() },
        };
        Assert(DecorLogic.SetTheme(onePiece, catalog, 1, SetAnniversary).Error is null, "a partly owned set could not be opened");

        // 显示：只能切换持有的摆件，值只能是 0 或 1。
        Rejected(DecorLogic.SetVisible(owned, catalog, Tree, 2), owned, "visible state 2");
        Rejected(DecorLogic.SetVisible(owned, catalog, Unknown, 1), owned, "an unknown decoration");
        Rejected(DecorLogic.SetVisible(owned, catalog, SetItem, 1), owned, "a set piece");
        Rejected(DecorLogic.SetVisible(owned, catalog, Poster1, 1), owned, "a poster");
        PlayerAccount noTree = owned with { Decor = owned.Decor! with { Items = owned.Decor.Items.Where(item => item.Id != Tree).ToList() } };
        Rejected(DecorLogic.SetVisible(noTree, catalog, Tree, 1), noTree, "an unowned decoration");
        DecorLogic.SetResult shown = DecorLogic.SetVisible(owned, catalog, Tree, 1);
        Assert(shown is { Error: null, Changed: true } &&
               shown.Account.Decor!.Items.Single(item => item.Id == Tree).Visible == 1 &&
               shown.Push!.BagItems!.SequenceEqual([new InteractionBagItemInfo(Tree, 1, 1)]) && shown.Push.Decorate is null,
            "showing a decoration did not save it and push only that item");
        Assert(DecorLogic.SetVisible(shown.Account, catalog, Tree, 1) is { Error: null, Changed: false } &&
               DecorLogic.SetVisible(shown.Account, catalog, Tree, 0) is { Error: null, Changed: true } hidden &&
               hidden.Push!.BagItems!.Single() == new InteractionBagItemInfo(Tree, 1, 0),
            "repeating or hiding a decoration misbehaved");

        // 海报：挂点必须是海报框；posterId 为 0 或持有的海报；同一张可挂在多个框上。
        Rejected(DecorLogic.SetPoster(owned, catalog, 123, Poster1), owned, "a point that is not a poster frame");
        Rejected(DecorLogic.SetPoster(owned, catalog, Point1, Koinobori), owned, "a decoration that is not a poster");
        Rejected(DecorLogic.SetPoster(owned, catalog, Point1, Unknown), owned, "an unknown poster");
        PlayerAccount noPoster = owned with { Decor = owned.Decor! with { Items = owned.Decor.Items.Where(item => item.Id != Poster2).ToList() } };
        Rejected(DecorLogic.SetPoster(noPoster, catalog, Point1, Poster2), noPoster, "an unowned poster");
        DecorLogic.SetResult hung = DecorLogic.SetPoster(owned, catalog, Point2, Poster2);
        Assert(hung is { Error: null, Changed: true } &&
               hung.Account.Decor!.Posters.SequenceEqual([new DecorPoster(Point1, 0), new DecorPoster(Point2, Poster2), new DecorPoster(Point3, 0)]) &&
               hung.Push!.Posters!.SequenceEqual([new PosterStateInfo(Point2, Poster2)]),
            "hanging a poster did not save it in frame order and push only that frame");
        DecorLogic.SetResult twice = DecorLogic.SetPoster(hung.Account, catalog, Point3, Poster2);
        DecorLogic.SetResult removed = DecorLogic.SetPoster(twice.Account, catalog, Point2, 0);
        Assert(twice is { Error: null, Changed: true } && removed is { Error: null, Changed: true } &&
               removed.Account.Decor!.Posters.SequenceEqual([new DecorPoster(Point1, 0), new DecorPoster(Point2, 0), new DecorPoster(Point3, Poster2)]),
            "the same poster could not hang twice, or taking one down failed");

        // 水晶球：球必须是 type 3，小人为 0 或 config_interaction_figurte 里的。
        Rejected(DecorLogic.SetBallToy(owned, catalog, 59005, Figure), owned, "a ball that is not a crystal ball");
        Rejected(DecorLogic.SetBallToy(owned, catalog, Ball, 999), owned, "an unknown figure");
        DecorLogic.SetResult toy = DecorLogic.SetBallToy(owned, catalog, Ball, Figure);
        Assert(toy is { Error: null, Changed: true } && toy.Account.Decor!.BallToys!.SequenceEqual([new DecorBallToy(Ball, Figure)]) &&
               toy.Push!.BallToys!.SequenceEqual([new BallToyStateInfo(Ball, Figure)]),
            "a crystal ball figure was not saved and pushed");
    }

    private static void SaveEditorChecks(PlayerAccount owned)
    {
        Assert(SaveEditor.SetItem(owned, 250032, 1, knownItem: true) is { Ok: false } refused && ReferenceEquals(refused.Account, owned),
            "the save editor put a decoration into the bag");
        PlayerAccount legacy = owned with { Bag = new PlayerBag([new BagItem(250032, 3)]) };
        Assert(SaveEditor.SetItem(legacy, 250032, 0, knownItem: true) is { Ok: true, BagChanged: true },
            "the save editor could not delete a legacy decoration stack");
    }

    /// <summary>decorations.json：发现规则（enabled / targetClients / bootstrap.lua entries / 路径顺序）、结构检查与撞号。</summary>
    private static void ModFileChecks(DecorCatalog config)
    {
        string root = Path.Combine(Path.GetTempPath(), "blueoath-decor-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "bootstrap.lua"), """
                local root = "ignored"
                --[[ "e.mod/main.lua" in a block comment does not count ]]
                local entries = {
                  "a.mod/main.lua",
                  "b.mod/main.lua",
                  -- "f.mod/main.lua",
                  "c.mod/main.lua", "d.mod/main.lua",
                  'g.mod/main.lua',
                  "h.mod/main.lua",
                  "j.mod/main.lua",
                  "k.mod/main.lua",
                  "l.mod/main.lua",
                  "nested/i.mod/main.lua"
                }
                """);
            void Mod(string dir, string? manifest, string decorations)
            {
                string path = Path.Combine(root, dir);
                Directory.CreateDirectory(path);
                if (manifest is not null) File.WriteAllText(Path.Combine(path, "mod.json"), manifest);
                File.WriteAllText(Path.Combine(path, DecorModLoader.FileName), decorations);
            }
            const string Jp = """{"id":"%ID%","targetClients":["jp-1.4.0"],"enabled":true}""";
            Mod("a.mod", Jp.Replace("%ID%", "a.mod"), """
                {
                  "groups": [ { "id": 5, "bags": [250041, 250042, 250043] } ],
                  "bags": [
                    { "id": 250041, "group": 5, "interactionItem": 36 },
                    { "id": 250042, "group": 5, "interactionItem": 37, "type": 0, "showType": 0 },
                    { "id": 250043, "group": 5, "interactionItem": 38 },
                    { "id": 252101, "type": 1 },
                    { "id": 254001, "interactionItem": 59005, "showType": 1 }
                  ]
                }
                """);
            Mod("b.mod", """{"id":"b.mod"}""", """
                {
                  "groups": [ { "id": 5, "bags": [250051] } ],
                  "bags": [
                    { "id": 250051, "group": 5, "interactionItem": 39 },
                    { "id": 250011, "interactionItem": 59005 },
                    { "id": 254002, "interactionItem": 999999 },
                    { "id": 254003, "interactionItem": 59006 },
                    { "id": 254001, "interactionItem": 59007 }
                  ]
                }
                """);
            Mod("c.mod", """{"id":"c.mod","enabled":false}""", """{"bags":[{"id":254010,"interactionItem":59005}]}""");
            Mod("d.mod", """{"id":"d.mod","targetClients":["cn-1.5.20"]}""", """{"bags":[{"id":254011,"interactionItem":59005}]}""");
            Mod("e.mod", """{"id":"e.mod"}""", """{"bags":[{"id":254012,"interactionItem":59005}]}""");
            Mod("f.mod", """{"id":"f.mod"}""", """{"bags":[{"id":254013,"interactionItem":59005}]}""");
            Mod("g.mod", """{"id":"g.mod"}""", """{"bags":[{"id":254014,"interactionItem":59005},{"id":123,"interactionItem":59005}]}""");
            Mod("h.mod", """{"id":"h.mod"}""", """
                {"groups":[{"id":6,"bags":[250061,250062]}],
                 "bags":[{"id":250061,"group":6,"interactionItem":40},{"id":250062,"group":6,"interactionItem":999999}]}
                """);
            Mod("j.mod", null, """{"bags":[{"id":254015,"interactionItem":59005}]}""");
            Mod("k.mod", """{"id":"k.mod"}""", """{"bags":[{"id":254016,""");
            Mod("l.mod", """{"id":"l.mod"}""", """{"groups":[{"id":7,"bags":[254030]}],"bags":[{"id":254030,"interactionItem":59005}]}""");
            Mod(Path.Combine("nested", "i.mod"), """{"id":"i.mod"}""", """{"bags":[{"id":254020,"interactionItem":59008}]}""");

            DecorCatalog merged = DecorModLoader.Load(root, "jp-1.4.0", config, NullLogger.Instance);
            List<int> added = merged.BagList.Where(bag => bag.Source != DecorCatalog.ConfigSource).Select(bag => bag.Id).ToList();
            Assert(added.SequenceEqual([250041, 250042, 250043, 252101, 254001, 254003, 254020]),
                $"decorations.json rules accepted the wrong rows: {string.Join(",", added)}");
            Assert(merged.Bags[254001] == new DecorBag(254001, 0, 0, 59005, 1, "a.mod") &&
                   merged.Bags[252101] == new DecorBag(252101, DecorCatalog.BagTypePoster, 0, 0, 0, "a.mod") &&
                   merged.Bags[254003].Source == "b.mod" && merged.Bags[254020].Source == "i.mod" &&
                   merged.Bags[SetItem].Source == DecorCatalog.ConfigSource,
                "mod decorations lost their fields or overwrote a config row");
            Assert(merged.GroupList.Select(group => group.Id).SequenceEqual([SetAnniversary, SetChristmas, 5]) &&
                   merged.Groups[5].Bags.SequenceEqual([250041, 250042, 250043]) && merged.Groups[5].Source == "a.mod" &&
                   merged.Groups[SetAnniversary].Source == DecorCatalog.ConfigSource,
                "mod sets were not merged, or a colliding or partly rejected set was kept");
            Assert(config.BagList.All(bag => bag.Source == DecorCatalog.ConfigSource) && config.GroupList.Count == 2,
                "merging mods changed the config-only catalog");

            // 没有 bootstrap.lua（客户端不会加载这些 Mod）时一行都不收。
            string bare = Path.Combine(root, "nested");
            Assert(DecorModLoader.Load(bare, "jp-1.4.0", config, NullLogger.Instance).BagList.Count == config.BagList.Count,
                "decorations were accepted although bootstrap.lua is missing");
            Assert(ReferenceEquals(DecorModLoader.Load(Path.Combine(root, "missing"), "jp-1.4.0", config, NullLogger.Instance), config),
                "a missing Mods directory changed the catalog");
            IReadOnlyList<string>? entries = ModDeclarationScanner.ReadBootstrapEntries(Path.Combine(FindRepositoryRoot(), "Mods"));
            Assert(entries is { Count: > 0 } && entries.Contains("custom-equipment.mod/main.lua") &&
                   ModDeclarationScanner.IsListed(entries, root, Path.Combine(root, "custom-equipment.mod")) &&
                   !ModDeclarationScanner.IsListed(entries, root, Path.Combine(root, "custom")),
                "the repository's Mods/bootstrap.lua entries could not be read");

            // 声明的套装可以打开；关掉 Mod（目录里不再有套装 5）后存档里的套装改回 0，推送也不再出现它的装饰品。
            PlayerAccount withMod = DecorLogic.EnsureAll(PlayerAccountFactory.CreateDefault("decor-mod", 1), merged);
            DecorLogic.SetResult spring = DecorLogic.SetTheme(withMod, merged, 1, 5);
            Assert(spring is { Error: null, Changed: true } &&
                   DecorLogic.Snapshot(spring.Account, merged).BagItems!.Any(item => item.Id == 250041),
                "a mod-declared set could not be opened or was not pushed");
            PlayerAccount modOff = DecorLogic.EnsureAll(spring.Account, config);
            Assert(modOff.Decor!.Themes.Single().SelectId == 0 && modOff.Decor.Items.All(item => item.Id < 250040 || item.Id > 250049) &&
                   DecorLogic.Snapshot(spring.Account, config) is { } offPush &&
                   offPush.Decorate!.Single().CurSelect == 0 && offPush.BagItems!.All(item => config.Bags.ContainsKey(item.Id)),
                "disabling the mod left its set or decorations in the save or the push");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    public static async Task ModuleTest()
    {
        string clientPath = FindClientPath();
        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-decor-" + Guid.NewGuid().ToString("N"));
        const string P = "decor-module";
        int now = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            PlayerAccount seed = PlayerAccountFactory.CreateDefault(P, now - 60);
            // 旧档：装饰品误入普通背包，且存着一个配置里没有的套装。
            await repo.SaveAccountAsync(seed with
            {
                Bag = new PlayerBag([new BagItem(10007, 5), new BagItem(250032, 1)]),
                Decor = new PlayerDecor([new DecorBagItem(Tree, 1)], [new DecorTheme(1, 999)], []),
            });
            using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
                Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            ServerOptions options = ServerOptions.Parse(
                ["--no-cheats", "--real-resource-cost=off", "--data=" + dataRoot, "--client-path=" + clientPath, "--profile-id=" + P]);
            var services = new GameServices(repo, options, loggerFactory);
            DecorCatalog catalog = DecorCatalogLoader.Catalog;
            Assert(catalog.IsLoaded && catalog.Groups.ContainsKey(SetChristmas), "GameServices did not load the decor catalog");

            PlayerAccount loaded = await services.GetOrCreateAccountAsync(P, CancellationToken.None);
            PlayerAccount stored = await repo.LoadAccountAsync(P) ?? throw new InvalidDataException("account missing");
            static bool MigratedBag(PlayerBag? bag) =>
                bag is not null && bag.Items.All(item => !DecorCatalog.IsDecorItemId(item.TemplateId)) &&
                bag.Items.Single(item => item.TemplateId == 10007).Num == 5;
            Assert(MigratedBag(loaded.Bag) && MigratedBag(stored.Bag) &&
                   stored.Decor!.Themes.Single().SelectId == 0 && stored.Decor.Items.Count == catalog.BagList.Count &&
                   stored.Decor.Items.Single(item => item.Id == Tree).Visible == 1,
                "loading did not move decorations out of the bag, reset the stale set and persist it");

            // AddBagItem：装饰品不进背包（已持有时账号不变），其它道具照常。
            Assert(ReferenceEquals(GameServices.AddBagItem(loaded, 250032, 1), loaded) &&
                   ReferenceEquals(GameServices.AddBagItem(loaded, Unknown, 1), loaded) &&
                   GameServices.AddBagItem(loaded, 10007, 1).Bag!.Items.Single(item => item.TemplateId == 10007).Num == 6,
                "AddBagItem did not route decorations away from the bag");

            // 登录同步：完整状态，排在时装推送之后（其他功能可能在两者之间插入自己的推送）。
            List<TResponse> login = (await services.BuildSyncPushesAsync(P, checked((uint)now), CancellationToken.None))
                .Select(push => TMessageCodec.DecodeResponse(push)).ToList();
            int decorIndex = login.FindIndex(push => push.Method == DecorLogic.RefreshMethod);
            int fashionIndex = login.FindIndex(push => push.Method == "fashion.updateData");
            Assert(fashionIndex >= 0 && decorIndex > fashionIndex, "the login sync lacks RefreshInteractionItems");
            InteractionItemRet snapshot = PlayerDataCodec.DecodeInteractionItemRet(login[decorIndex].Ret!);
            Assert(snapshot.BagItems!.Select(item => item.Id).SequenceEqual(catalog.BagList.Select(bag => bag.Id)) &&
                   snapshot.BagItems!.All(item => item.Num == 1) &&
                   snapshot.BagItems!.Where(item => item.State == 1).Select(item => item.Id).SequenceEqual([Tree]) &&
                   snapshot.Decorate!.SequenceEqual([new DecorateMutexBagGroupInfo(1, 0)]) &&
                   snapshot.Posters!.SequenceEqual(Points.Select(point => new PosterStateInfo(point, 0))),
                "the login push does not carry every owned decoration, set 0 and empty frames");

            var module = new InteractionItemModule(services);
            GameContext Ctx() => new() { ProfileId = P, Now = now, Ct = CancellationToken.None, Services = services };
            async Task<(ModuleResult Result, InteractionItemRet? Push)> Call(string method, byte[] args)
            {
                ModuleResult result = await module.HandleAsync(Ctx(), new TRequest("interactionitem." + method, args));
                byte[]? push = result.PrePushes.Select(frame => TMessageCodec.DecodeResponse(frame))
                    .FirstOrDefault(frame => frame.Method == DecorLogic.RefreshMethod)?.Ret;
                return (result, push is null ? null : PlayerDataCodec.DecodeInteractionItemRet(push));
            }
            async Task<PlayerDecor> Saved() => (await repo.LoadAccountAsync(P))!.Decor!;

            (ModuleResult setOk, InteractionItemRet? setPush) = await Call("SetMutexBagGroupState", PlayerDataCodec.EncodeIntPair(1, SetChristmas));
            Assert(setOk.Err == 0 && setPush is not null && setPush.Decorate!.SequenceEqual([new DecorateMutexBagGroupInfo(1, SetChristmas)]) &&
                   setPush.BagItems!.Count == 0 && (await Saved()).Themes.Single().SelectId == SetChristmas,
                "SetMutexBagGroupState did not save the set and push it before the response");
            (ModuleResult setBad, InteractionItemRet? setBadPush) = await Call("SetMutexBagGroupState", PlayerDataCodec.EncodeIntPair(1, 999));
            Assert(setBad.Err == 1 && setBadPush is null && (await Saved()).Themes.Single().SelectId == SetChristmas,
                "an unknown set was accepted");
            (ModuleResult close, _) = await Call("SetMutexBagGroupState", [0x08, 0x01]);
            Assert(close.Err == 0 && (await Saved()).Themes.Single().SelectId == 0, "closing the set (SelectId omitted) failed");
            await Call("SetMutexBagGroupState", PlayerDataCodec.EncodeIntPair(1, SetChristmas));

            (ModuleResult showOk, InteractionItemRet? showPush) = await Call("SetBagItemVisible", PlayerDataCodec.EncodeIntPair(Koinobori, 1));
            Assert(showOk.Err == 0 && showPush!.BagItems!.SequenceEqual([new InteractionBagItemInfo(Koinobori, 1, 1)]) &&
                   (await Saved()).Items.Single(item => item.Id == Koinobori).Visible == 1,
                "SetBagItemVisible did not save and push the item");
            Assert((await Call("SetBagItemVisible", PlayerDataCodec.EncodeIntPair(SetItem, 1))).Result.Err == 1,
                "a set piece could be shown on its own");

            (ModuleResult posterOk, InteractionItemRet? posterPush) = await Call("SetPosterState", PlayerDataCodec.EncodeIntPair(Point1, Poster1));
            Assert(posterOk.Err == 0 && posterPush!.Posters!.SequenceEqual([new PosterStateInfo(Point1, Poster1)]) &&
                   (await Saved()).Posters.First().PosterId == Poster1,
                "SetPosterState did not save and push the frame");
            Assert((await Call("SetPosterState", PlayerDataCodec.EncodeIntPair(Point1, Koinobori))).Result.Err == 1,
                "a non-poster could be hung");
            Assert((await Call("SetCrystalBallToy", PlayerDataCodec.EncodeIntPair(Ball, Figure))) is ({ Err: 0 }, { } toyPush) &&
                   toyPush.BallToys!.SequenceEqual([new BallToyStateInfo(Ball, Figure)]),
                "SetCrystalBallToy did not push the ball");

            // 点击摆件领奖、已关闭的家具活动与坏参数一律返回错误，不推装饰。
            foreach ((string method, byte[] args) in new[]
                     {
                         ("GetItemReward", PlayerDataCodec.EncodeIntPair(TreeItem, 0)),
                         ("GetItemReward", PlayerDataCodec.EncodeIntPair(999_999, 0)),
                         ("BuyChristmasFurniture", PlayerDataCodec.EncodeIntPair(1, 0)),
                         ("GetSpringPaperFlowerReward", PlayerDataCodec.EncodeIntPair(36, 0)),
                         ("SetPosterState", new byte[] { 0xFF }),
                     })
            {
                (ModuleResult rejected, InteractionItemRet? push) = await Call(method, args);
                Assert(rejected.Err == 1 && rejected.Ret.Length == 0 && push is null, $"{method} was not rejected");
            }
            (ModuleResult refresh, _) = await Call("RefreshInteractionItems", []);
            Assert(refresh.Err == 0 && PlayerDataCodec.DecodeInteractionItemRet(refresh.Ret).Decorate!.Single().CurSelect == SetChristmas,
                "a RefreshInteractionItems request was not answered with the state");
            ModuleResult routed = await new MessageRouter(services, [module]).DispatchAsync(
                new TRequest("interactionitem.SetPosterState", PlayerDataCodec.EncodeIntPair(Point3, Poster16)), P, CancellationToken.None);
            Assert(routed.Err == 0 && (await Saved()).Posters.Last() == new DecorPoster(Point3, Poster16),
                "the router did not send interactionitem.* to InteractionItemModule");

            // 重启服务端（新的 GameServices，没有内存缓存）后从存档恢复。
            var restarted = new GameServices(repo, options, loggerFactory);
            TResponse relogin = (await restarted.BuildSyncPushesAsync(P, checked((uint)now), CancellationToken.None))
                .Select(push => TMessageCodec.DecodeResponse(push)).Single(push => push.Method == DecorLogic.RefreshMethod);
            InteractionItemRet after = PlayerDataCodec.DecodeInteractionItemRet(relogin.Ret!);
            Assert(after.Decorate!.Single().CurSelect == SetChristmas &&
                   after.BagItems!.Where(item => item.State == 1).Select(item => item.Id).SequenceEqual([Koinobori, Tree]) &&
                   after.Posters!.SequenceEqual([new PosterStateInfo(Point1, Poster1), new PosterStateInfo(Point2, 0), new PosterStateInfo(Point3, Poster16)]) &&
                   after.BallToys!.SequenceEqual([new BallToyStateInfo(Ball, Figure)]),
                "the decorations did not survive a restart");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }

    /// <summary>TCP：登录推送、切换套装的推送先于应答、显示与海报往返、重启服务端后重新登录仍在。</summary>
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
        DecorCatalog config = DecorCatalogLoader.LoadConfig(ConfigDbLoader.BuildConfigDir(clientPath));
        string data = Path.Combine(Path.GetTempPath(), "blueoath-decor-it-" + Guid.NewGuid().ToString("N"));
        const string profileId = "decor-player";
        Directory.CreateDirectory(data);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 60);
        await new SqliteGameRepository(data).SaveAccountAsync(seed with { Bag = new PlayerBag([new BagItem(250032, 1)]) });
        try
        {
            List<InteractionItemRet> firstLogin;
            using (Server server = await Server.StartAsync(serverDll, data, profileId, clientPath))
            {
                firstLogin = await server.LoginAsync(profileId);
                InteractionItemRet login = firstLogin.Single();
                List<int> pushed = login.BagItems!.Select(item => item.Id).ToList();
                Assert(config.BagList.All(bag => pushed.Contains(bag.Id)) && pushed.All(DecorCatalog.IsDecorItemId) &&
                       pushed.Count == pushed.Distinct().Count() && login.BagItems!.All(item => item is { Num: 1, State: 0 }) &&
                       login.Decorate!.SequenceEqual([new DecorateMutexBagGroupInfo(1, 0)]) &&
                       login.Posters!.SequenceEqual(Points.Select(point => new PosterStateInfo(point, 0))),
                    $"the login push does not list every decoration with num 1 / state 0 (pushed {pushed.Count}, config {config.BagList.Count})");

                (TResponse set, List<InteractionItemRet> setPushes) =
                    await server.CallAsync("interactionitem.SetMutexBagGroupState", PlayerDataCodec.EncodeIntPair(1, SetChristmas));
                Assert(set.Err == 0 && setPushes.SingleOrDefault()?.Decorate?.SequenceEqual([new DecorateMutexBagGroupInfo(1, SetChristmas)]) == true,
                    "SetMutexBagGroupState(1, 3) did not push the set before its response");
                (TResponse shown, List<InteractionItemRet> shownPushes) =
                    await server.CallAsync("interactionitem.SetBagItemVisible", PlayerDataCodec.EncodeIntPair(Tree, 1));
                Assert(shown.Err == 0 && shownPushes.Single().BagItems!.SequenceEqual([new InteractionBagItemInfo(Tree, 1, 1)]),
                    "SetBagItemVisible did not push the item before its response");
                (TResponse hung, List<InteractionItemRet> hungPushes) =
                    await server.CallAsync("interactionitem.SetPosterState", PlayerDataCodec.EncodeIntPair(Point2, Poster2));
                Assert(hung.Err == 0 && hungPushes.Single().Posters!.SequenceEqual([new PosterStateInfo(Point2, Poster2)]),
                    "SetPosterState did not push the frame before its response");
                (TResponse bad, List<InteractionItemRet> badPushes) =
                    await server.CallAsync("interactionitem.SetPosterState", PlayerDataCodec.EncodeIntPair(Point2, Koinobori));
                Assert(bad.Err != 0 && badPushes.Count == 0, "hanging a non-poster was accepted over TCP");
            }

            PlayerAccount stored = await new SqliteGameRepository(data).LoadAccountAsync(profileId)
                ?? throw new InvalidDataException("profile was not persisted");
            Assert(stored.Bag!.Items.All(item => !DecorCatalog.IsDecorItemId(item.TemplateId)) &&
                   stored.Decor!.Themes.Single().SelectId == SetChristmas,
                "the server did not persist the migrated bag and the set");

            using (Server server = await Server.StartAsync(serverDll, data, profileId, clientPath))
            {
                InteractionItemRet relogin = (await server.LoginAsync(profileId)).Single();
                Assert(relogin.Decorate!.SequenceEqual([new DecorateMutexBagGroupInfo(1, SetChristmas)]) &&
                       relogin.BagItems!.Where(item => item.State == 1).Select(item => item.Id).SequenceEqual([Tree]) &&
                       relogin.Posters!.SequenceEqual([new PosterStateInfo(Point1, 0), new PosterStateInfo(Point2, Poster2), new PosterStateInfo(Point3, 0)]),
                    "the set, visibility or poster did not survive a server restart and relogin");
            }
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    /// <summary>测试用的服务端进程与一条 TCP 连接。</summary>
    private sealed class Server : IDisposable
    {
        private readonly Process _process;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

        private Server(Process process, TcpClient client)
        {
            _process = process;
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<Server> StartAsync(string serverDll, string data, string profileId, string clientPath)
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
                         serverDll, "--port=0", "--game-login-port=0", "--region=jp", "--no-cheats", "--real-resource-cost=off",
                         "--data=" + data, "--profile-id=" + profileId, "--client-path=" + clientPath,
                     })
                startInfo.ArgumentList.Add(arg);
            var process = new Process { StartInfo = startInfo };
            Assert(process.Start(), "decor test server did not start");
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            try
            {
                string? readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                using JsonDocument ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
                int port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
                var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                return new Server(process, client);
            }
            catch
            {
                if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
                process.Dispose();
                throw;
            }
        }

        /// <summary>发一个请求，返回应答与应答之前收到的装饰推送。</summary>
        public async Task<(TResponse Response, List<InteractionItemRet> Pushes)> CallAsync(string method, byte[]? args)
        {
            await NetSocketFrameCodec.WriteAsync(_stream, TMessageCodec.EncodeRequest(new TRequest(method, args, 1)),
                NetSocketFrameCodec.TypeData, _timeout.Token);
            var pushes = new List<InteractionItemRet>();
            while (true)
            {
                var frame = await NetSocketFrameCodec.ReadFromServerAsync(_stream, _timeout.Token);
                Assert(frame is not null, $"the server closed the connection during {method}");
                TResponse response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                if (response.IsResponse == 1) return (response, pushes);
                if (response.Method == DecorLogic.RefreshMethod)
                    pushes.Add(PlayerDataCodec.DecodeInteractionItemRet(response.Ret ?? []));
            }
        }

        /// <summary>
        /// 登录并取回登录同步里的装饰推送：同步推送跟在 user.GetUserInfo 的应答之后，再发一个 GetSvrTime 把它们收齐。
        /// </summary>
        public async Task<List<InteractionItemRet>> LoginAsync(string profileId)
        {
            await CallAsync("player.Login", GameLoginCodec.Encode(new TArgLogin(profileId, 1, "open", "hash")));
            (TResponse info, List<InteractionItemRet> early) = await CallAsync("user.GetUserInfo", null);
            Assert(info.Err == 0, "user.GetUserInfo failed");
            (_, List<InteractionItemRet> sync) = await CallAsync("GetSvrTime", null);
            return [.. early, .. sync];
        }

        public void Dispose()
        {
            _client.Dispose();
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit(5000);
            }
            _process.Dispose();
            _timeout.Dispose();
        }
    }
}
