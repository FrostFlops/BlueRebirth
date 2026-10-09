using BlueOath.Core;
using BlueOath.Mods;
using BlueOath.Protocol;
using BlueOath.Server.Protocols;
using BlueOath.Server.Configs;
using BlueOath.Server;
using BlueOath.Server.Hosting;
using BlueOath.Storage;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var tests = new (string Name, Func<Task> Run)[]
{
    ("frame codec handles fragmented input", FrameCodecTest),
    ("real login protobuf payload round-trips", LoginProtobufTest),
    ("selected launcher profile flows through bootstrap login responses", AccountProfileBootstrapTest),
    ("launcher profile names initialize and migrate character names", AccountProfileNameMigrationTest),
    ("legacy hero-advance duplicate ids migrate to the upgraded survivor", DuplicateHeroMigrationTest),
    ("client login wire envelope round-trips", ClientLoginWireTest),
    ("temporary game login frame round-trips", GameLoginFrameTest),
    ("equipment enhancement response contains required payload", EquipEnhanceRetCodecTest),
    ("equipment renovation request decodes consumed equipment ids", EquipRiseStarArgsCodecTest),
    ("zero-count bag entries encode an explicit deletion marker", BagDeletionMarkerCodecTest),
    ("normal treasure request and equipment reward use client protobuf layout", TreasureCodecTest),
    ("hero advance preserves neighbors and unbinds consumed equipment", HeroAdvanceStateTest),
    ("build ship response omits empty special rewards", BuildShipRewardCodecTest),
    ("120-draw ship reward creates a hero instance and heals legacy bag pollution", BuildShipHundredRewardIntegrationTest),
    ("traditional construction config, protocol and queue match the client", ConstructionConfigAndCodecTest),
    ("building config, lifecycle and assignment codecs match the client", BuildingCodecTest),
    ("story unlock config includes event, side and personal stories", StoryUnlockConfigTest),
    ("illustrate entries unlock every configured behaviour", IllustrateBehaviourUnlockTest),
    ("ship acquisition keeps the client illustrate snapshot in sync", ShipAcquisitionIllustrateSyncTest),
    ("Mubar battle start preserves CopyType 33", MubarBattleStartCodecTest),
    ("sea-copy entries include non-nil safe-area state", SeaCopySafeAreaCodecTest),
    ("daily-copy gameplay unlocks and resets destroyer challenge progress", DailyCopyGameplayTest),
    ("tasks remain incomplete until persisted progress reaches the goal", TaskCompletionRequiresProgressTest),
    ("illustrate payload encodes unlocked personal stories", HeroMemoryCodecTest),
    ("remould config and hero protobuf fields match the client", RemouldConfigAndCodecTest),
    ("sqlite repository persists and isolates profiles", StorageTest),
    ("sqlite repository persists player account (character + dock)", AccountStorageTest),
    ("game service resolves deterministic battle", GameTest),
    ("mod manager filters target and orders mods", ModTest),
    ("equipment mod adds a client/server template and GM shop good", EquipmentModTest),
    ("fashion shop previews tolerate an unlocked skin without its hero", FashionPreviewModTest),
    ("native draw results only prompt to lock a genuinely new ship", BuildShipNewStateNativePatchTest),
    ("item selection boxes grant the chosen option", SelectTreasureTest),
    ("tls material loads in OpenSSL proxy runtime", TlsCaptureIntegrationTest),
    ("ship intensify grants attribute levels and consumes materials", ShipIntensifyTest),
    ("guide user settings persist and ship on the login push", GuideUserSettingTest),
    ("time settlement matches the client mood formulas", TimeSettlementFormulaTest),
    ("time settlement anchors, bath slots and mood migration encode correctly", TimeSettlementCodecTest),
    ("building and bathroom modules settle elapsed time", TimeSettlementModuleTest),
    ("vow wall encodes and decodes its snapshot", VowCodecTest),
    ("vow cooldown, stones and daily reset follow the client", VowFormulaTest),
    ("vow wall protocols persist and push the cooldown", VowModuleTest),
    ("vow wall edits push settlement data after the response", VowWallEditPushOrderTest),
    ("server frames include the header length so the client reads Time", NetSocketServerFramingTest),
    ("real resource cost prices gacha, shop and sortie like the client", RealCostPureTest),
    ("real resource cost charges through the modules", RealCostModuleTest),
    ("real shop stock: calendar, refresh, stock, random lineup, codec and save", ShopStockTests.PureTest),
    ("real shop stock through the shop module", ShopStockTests.ModuleTest),
    ("sweep: run timing, costs, rewards and save", SweepTests.PureTest),
    ("sweep through the mopUp module", SweepTests.ModuleTest),
    ("save editor sets currencies and bag items", SaveEditorPureTest),
    ("unlimited materials cheat refills on load; otherwise building charges materials", MaterialsCheatTest),
    ("save editor pushes to the live session after the current request", SaveEditorResyncTest),
    ("building production follows the client formulas", BuildingProductionFormulaTest),
    ("building production snapshots encode the client fields", BuildingProductionCodecTest),
    ("building production protocols receive, order and push", BuildingProductionModuleTest),
    ("worker strength recovers and is consumed like the client", WorkerStrengthTest),
    ("launcher time cheats omit each time-based cost", TimeCheatsTest),
    ("server cheat switches reach the rules and the building module", CheatWiringTest),
    ("bath gift rolls a buff from the gift and ship pools", BathGiftRollTest),
    ("bath gift charges coins and pushes the buff before the response", BathGiftModuleTest),
    ("production speedup confirm clamps to the finishing count and sends directly", ProductionSpeedupModTest),
    ("wish cooldown result page is skipped only without a cooldown", WishCooldownTipModTest),
    ("construction and GM grants settle worker strength first", ConstructionStrengthTest),
    ("cheats keep degrade and window edge cases consistent", CheatEdgeCasesTest)
};
if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase)) tests = [.. tests,
    ("tcp server completes local gameplay flow", TcpIntegrationTest),
    ("protobuf login server creates a local profile", GameLoginIntegrationTest),
    ("tactic SetHerosTactic persists formation", TacticIntegrationTest),
    ("fashion synchronization and shops use configured catalog", FashionUnlockIntegrationTest),
    ("equipped UR equipment supports normal and bound enhancement", EquipEnhanceIntegrationTest),
    ("traditional construction consumes resources and persists its queue", ConstructionIntegrationTest),
    ("building construction and hero assignment persist and refresh the client", BuildingAssignmentIntegrationTest),
    ("hero remould consumes costs and persists its node", HeroRemouldIntegrationTest),
    ("hero gift, lock/unlock and retirement synchronize client state", HeroMutationIntegrationTest),
    ("time settlement persists and syncs over TCP", TimeSettlementIntegrationTest)];
if (args.Contains("--tcp-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("tcp server pins legacy login ids to the selected launcher profile", TcpIntegrationTest)];
if (args.Contains("--equip-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("equipped UR equipment supports normal and bound enhancement", EquipEnhanceIntegrationTest)];
if (args.Contains("--equipment-mod", StringComparer.OrdinalIgnoreCase))
    tests = [("equipment mod adds a client/server template and GM shop good", EquipmentModTest)];
if (args.Contains("--equipment-mod-config", StringComparer.OrdinalIgnoreCase))
    tests = [("equipment mod overlays the real client equipment database", EquipmentModConfigIntegrationTest)];
if (args.Contains("--retire-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("hero lock/unlock and retirement synchronize client state", HeroMutationIntegrationTest)];
if (args.Contains("--hero-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("hero gift, lock/unlock and retirement synchronize client state", HeroMutationIntegrationTest)];
if (args.Contains("--affection-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("hero gift, lock/unlock and retirement synchronize client state", HeroMutationIntegrationTest)];
if (args.Contains("--rename-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("hero rename supports Unicode, reset and client synchronization", HeroMutationIntegrationTest)];
if (args.Contains("--marry-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("oath ring purchase and consecutive marriages are atomic", HeroMutationIntegrationTest)];
if (args.Contains("--shop-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("oath ring purchase refreshes inventory before its response", HeroMutationIntegrationTest)];
if (args.Contains("--fashion-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("fashion synchronization and shops use configured catalog", FashionUnlockIntegrationTest)];
if (args.Contains("--treasure-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("equipment treasure consumes its box and persists a new equipment instance", TreasureIntegrationTest)];
if (args.Contains("--hero-advance", StringComparer.OrdinalIgnoreCase))
    tests = [("hero advance preserves neighbors and unbinds consumed equipment", HeroAdvanceStateTest)];
if (args.Contains("--buildship-codec", StringComparer.OrdinalIgnoreCase))
    tests = [("build ship response omits empty special rewards", BuildShipRewardCodecTest)];
if (args.Contains("--buildship-reward", StringComparer.OrdinalIgnoreCase))
    tests = [("120-draw ship reward creates a hero instance and heals legacy bag pollution", BuildShipHundredRewardIntegrationTest)];
if (args.Contains("--tactic-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("tactic SetHerosTactic persists formation", TacticIntegrationTest)];
if (args.Contains("--story-unlock", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("story unlock config includes event, side and personal stories", StoryUnlockConfigTest),
        ("illustrate payload encodes unlocked personal stories", HeroMemoryCodecTest)
    ];
if (args.Contains("--illustrate-behaviour", StringComparer.OrdinalIgnoreCase))
    tests = [("illustrate entries unlock every configured behaviour", IllustrateBehaviourUnlockTest)];
if (args.Contains("--remould-integration", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("remould config and hero protobuf fields match the client", RemouldConfigAndCodecTest),
        ("hero remould consumes costs and persists its node", HeroRemouldIntegrationTest)
    ];
if (args.Contains("--construction-integration", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("traditional construction config, protocol and queue match the client", ConstructionConfigAndCodecTest),
        ("traditional construction consumes resources and persists its queue", ConstructionIntegrationTest)
    ];
if (args.Contains("--building-integration", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("building config, lifecycle and assignment codecs match the client", BuildingCodecTest),
        ("building construction and hero assignment persist and refresh the client", BuildingAssignmentIntegrationTest)
    ];
if (args.Contains("--time-settlement", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("time settlement matches the client mood formulas", TimeSettlementFormulaTest),
        ("time settlement anchors, bath slots and mood migration encode correctly", TimeSettlementCodecTest),
        ("building and bathroom modules settle elapsed time", TimeSettlementModuleTest),
        ("time settlement persists and syncs over TCP", TimeSettlementIntegrationTest)
    ];
if (args.Contains("--vow", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("vow wall encodes and decodes its snapshot", VowCodecTest),
        ("vow cooldown, stones and daily reset follow the client", VowFormulaTest),
        ("vow wall protocols persist and push the cooldown", VowModuleTest),
        ("vow wall edits push settlement data after the response", VowWallEditPushOrderTest)
    ];
if (args.Contains("--sweep", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("sweep: run timing, costs, rewards and save", SweepTests.PureTest),
        ("sweep through the mopUp module", SweepTests.ModuleTest)
    ];
if (args.Contains("--save-editor", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("save editor sets currencies and bag items", SaveEditorPureTest),
        ("save editor pushes to the live session after the current request", SaveEditorResyncTest)
    ];
if (args.Contains("--shop-stock", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("real shop stock: calendar, refresh, stock, random lineup, codec and save", ShopStockTests.PureTest),
        ("real shop stock through the shop module", ShopStockTests.ModuleTest)
    ];
if (args.Contains("--real-cost", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("real resource cost prices gacha, shop and sortie like the client", RealCostPureTest),
        ("real resource cost charges through the modules", RealCostModuleTest)
    ];
if (args.Contains("--netsocket", StringComparer.OrdinalIgnoreCase))
    tests = [("server frames include the header length so the client reads Time", NetSocketServerFramingTest)];
if (args.Contains("--production", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("building production follows the client formulas", BuildingProductionFormulaTest),
        ("building production snapshots encode the client fields", BuildingProductionCodecTest),
        ("building production protocols receive, order and push", BuildingProductionModuleTest),
        ("worker strength recovers and is consumed like the client", WorkerStrengthTest),
        ("construction and GM grants settle worker strength first", ConstructionStrengthTest)
    ];
if (args.Contains("--cheats", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("launcher time cheats omit each time-based cost", TimeCheatsTest),
        ("server cheat switches reach the rules and the building module", CheatWiringTest),
        ("cheats keep degrade and window edge cases consistent", CheatEdgeCasesTest),
        ("unlimited materials cheat refills on load; otherwise building charges materials", MaterialsCheatTest)
    ];
if (args.Contains("--bath-gift", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("bath gift rolls a buff from the gift and ship pools", BathGiftRollTest),
        ("bath gift charges coins and pushes the buff before the response", BathGiftModuleTest)
    ];
if (args.Contains("--production-speedup-mod", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--client-mods", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("production speedup confirm clamps to the finishing count and sends directly", ProductionSpeedupModTest),
        ("wish cooldown result page is skipped only without a cooldown", WishCooldownTipModTest)
    ];
if (args.Contains("--fashion-preview-mod", StringComparer.OrdinalIgnoreCase))
    tests = [("fashion shop previews tolerate an unlocked skin without its hero", FashionPreviewModTest)];
if (args.Contains("--buildship-new-state-native", StringComparer.OrdinalIgnoreCase))
    tests = [("native draw results only prompt to lock a genuinely new ship", BuildShipNewStateNativePatchTest)];
if (args.Contains("--login-integration", StringComparer.OrdinalIgnoreCase))
    tests = [("protobuf login server creates a local profile", GameLoginIntegrationTest)];
if (args.Contains("--mubar-battle-codec", StringComparer.OrdinalIgnoreCase))
    tests = [("Mubar battle start preserves CopyType 33", MubarBattleStartCodecTest)];
if (args.Contains("--sea-safe-codec", StringComparer.OrdinalIgnoreCase))
    tests = [("sea-copy entries include non-nil safe-area state", SeaCopySafeAreaCodecTest)];
if (args.Contains("--dailycopy", StringComparer.OrdinalIgnoreCase))
    tests = [("daily-copy gameplay unlocks and resets destroyer challenge progress", DailyCopyGameplayTest)];
if (args.Contains("--task-completion", StringComparer.OrdinalIgnoreCase))
    tests = [("tasks remain incomplete until persisted progress reaches the goal", TaskCompletionRequiresProgressTest)];
if (args.Contains("--account-profile", StringComparer.OrdinalIgnoreCase))
    tests = [
        ("selected launcher profile flows through bootstrap login responses", AccountProfileBootstrapTest),
        ("launcher profile names initialize and migrate character names", AccountProfileNameMigrationTest)
    ];
var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
}
return failed;

// 客户端 IllustrateData:IsFirstGetHero() 判定的是 oldCurrId —— 应用某次 illustrate.IllustrateInfo
// 之前的 currId 快照。ShowGirlPage 的 bNew（首次获得动画、上锁询问、NEW 角标）全部取自它。
// 因此两件事必须成立，否则重复获得的舰船会被当成首次获得：
//   1. 登录全量图鉴推送之后要补一条 illustrate.OldIllustrateInfo，把 oldCurrId 从空表同步成
//      currId；登录时 currId 尚为空，SetIllustrateData 快照出来的 oldCurrId 必然是空的。
//   2. 商店发放舰娘时要推 illustrate.IllustrateInfo，否则该 ship_info_id 永远进不了 currId。
static async Task ShipAcquisitionIllustrateSyncTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-illustrate-sync-" + Guid.NewGuid().ToString("N"));
    const string profileId = "illustrate-sync";
    const int shipTemplateId = 40110311;
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        await repo.SaveAccountAsync(PlayerAccountFactory.CreateDefault(profileId, 1) with
        {
            Dock = new HeroDock([new Hero(10, 10210511, 5)]),
        });

        ServerOptions options = ServerOptions.Parse(
            ["--data=" + dataRoot,
             "--client-path=" + Path.Combine(root, "blueoath", "blueoath"),
             "--profile-id=" + profileId]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);

        List<string> loginMethods = (await services.BuildSyncPushesAsync(profileId, 1, CancellationToken.None))
            .Select(push => TMessageCodec.DecodeResponse(push).Method ?? "").ToList();
        int infoIndex = loginMethods.IndexOf("illustrate.IllustrateInfo");
        int oldIndex = loginMethods.IndexOf("illustrate.OldIllustrateInfo");
        Assert(infoIndex >= 0 && oldIndex >= 0,
            "login snapshot did not seed the client illustrate baseline");
        Assert(oldIndex > infoIndex,
            "illustrate.OldIllustrateInfo must follow the full illustrate snapshot it takes a copy of");

        List<string> plainMethods =
            (await services.BuildBuyPushesAsync(profileId, 1, CancellationToken.None))
            .Select(push => TMessageCodec.DecodeResponse(push).Method ?? "").ToList();
        Assert(!plainMethods.Contains("illustrate.IllustrateInfo"),
            "a purchase without ships should not push illustrate data");

        IReadOnlyList<byte[]> shipPushes = await services.BuildBuyPushesAsync(
            profileId, 1, CancellationToken.None, newShipTemplateIds: [shipTemplateId]);
        List<TResponse> shipResponses = shipPushes.Select(push => TMessageCodec.DecodeResponse(push)).ToList();
        Assert(shipResponses.Any(push => push.Method == "hero.UpdateHeroBagData"),
            "buying a ship did not refresh the dock");
        TResponse illustratePush = shipResponses.SingleOrDefault(p => p.Method == "illustrate.IllustrateInfo")
            ?? throw new InvalidDataException("buying a ship did not push illustrate data");
        // IllustrateId = ship_info_id = (templateId - 1) / 10，客户端据此把该船并入 currId。
        byte[] expected = new ProtocolPackage().Write(0x08, (ulong)((shipTemplateId - 1) / 10)).ToArray();
        Assert(illustratePush.Ret is { Length: > 0 } && ContainsSequence(illustratePush.Ret, expected),
            "illustrate push did not carry the purchased ship's illustrate id");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

static async Task HeroAdvanceStateTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-hero-advance-" + Guid.NewGuid().ToString("N"));
    const string profileId = "hero-advance-state";
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        PlayerAccount account = PlayerAccountFactory.CreateDefault(profileId, 1) with
        {
            Character = PlayerAccountFactory.CreateDefault(profileId, 1).Character with
            {
                SecretaryId = 70,
                Gold = 1_600_000,
            },
            Dock = new HeroDock(
            [
                new Hero(10, 10210511, 5, EquipSlots: [101, 0, 0, 0, 0, 0]),
                new Hero(20, 10210512, 15, EquipSlots: [102, 0, 0, 0, 0, 0]),
                new Hero(30, 10330111, 5, EquipSlots: [103, 0, 0, 0, 0, 0]),
                new Hero(40, 10210513, 25),
                new Hero(50, 10210514, 40),
                new Hero(60, 10210515, 60),
                new Hero(70, 10210511, 5),
                new Hero(80, 10210511, 5, Lock: true),
                new Hero(90, 12550112, 15),
                new Hero(100, 10240316, 80),
                new Hero(110, 10540111, 1),
                new Hero(120, 10210511, 5),
                new Hero(130, 10240326, 80),
                new Hero(140, 12850111, 1),
            ]),
            Bag = new PlayerBag([new BagItem(120004, 15), new BagItem(17512, 15)]),
            Equip = new PlayerEquip(
            [
                new EquipItem(101, 30151, HeroId: 10),
                new EquipItem(102, 30141, HeroId: 20),
                new EquipItem(103, 30421, HeroId: 30),
            ], 2000),
            Building = new PlayerBuilding(
                [new PlayerBuildingEntry(1, 2, 2, [40])],
                [new PlayerBuildingLand(1, 1)]),
            Fleet = new PlayerFleet([new FleetEntry(1, HeroInfo: [50])]),
            Bath = new PlayerBath([new BathHero(60)]),
        };
        await repo.SaveAccountAsync(account);

        ServerOptions options = ServerOptions.Parse(
            ["--data=" + dataRoot,
             "--client-path=" + Path.Combine(root, "blueoath", "blueoath"),
             "--profile-id=" + profileId]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        var heroService = new HeroService(services);

        byte[] emptyArgs = new ProtocolPackage().Write(0x08, 20UL).ToArray();
        HeroService.AdvanceResult emptyRejected = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", emptyArgs), profileId, CancellationToken.None);
        Assert(!emptyRejected.Changed, "hero advance ignored its configured material requirement");

        byte[] nonexistentArgs = new ProtocolPackage()
            .Write(0x08, 20UL)
            .Write(0x10, 999UL)
            .ToArray();
        HeroService.AdvanceResult nonexistentRejected = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", nonexistentArgs), profileId, CancellationToken.None);
        Assert(!nonexistentRejected.Changed, "hero advance accepted a nonexistent material hero");

        byte[] assignedArgs = new ProtocolPackage()
            .Write(0x08, 20UL)
            .Write(0x10, 40UL)
            .ToArray();
        HeroService.AdvanceResult assignedRejected = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", assignedArgs), profileId, CancellationToken.None);
        Assert(!assignedRejected.Changed, "hero advance consumed a hero assigned to a building");

        foreach (var (materialId, reason) in new[]
                 {
                     (50UL, "fleet"),
                     (60UL, "bath"),
                     (70UL, "secretary"),
                     (80UL, "lock"),
                 })
        {
            byte[] protectedArgs = new ProtocolPackage()
                .Write(0x08, 20UL)
                .Write(0x10, materialId)
                .ToArray();
            HeroService.AdvanceResult protectedRejected = await heroService.BuildAdvanceRetAsync(
                new TRequest("hero.HeroAdvance", protectedArgs), profileId, CancellationToken.None);
            Assert(!protectedRejected.Changed, $"hero advance consumed a material protected by {reason}");
        }

        PlayerAccount loaded = await services.GetOrCreateAccountAsync(profileId, CancellationToken.None);
        PlayerAccount poor = loaded with { Character = loaded.Character with { Gold = 19_999 } };
        await services.SaveAccountAsync(poor, CancellationToken.None);
        byte[] args = new ProtocolPackage()
            .Write(0x08, 20UL)
            .Write(0x10, 10UL)
            .ToArray();
        HeroService.AdvanceResult poorRejected = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", args), profileId, CancellationToken.None);
        Assert(!poorRejected.Changed, "hero advance allowed a negative currency balance");
        await services.SaveAccountAsync(
            poor with { Character = poor.Character with { Gold = 1_600_000 } }, CancellationToken.None);

        HeroService.AdvanceResult result = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", args), profileId, CancellationToken.None);
        Assert(result.Changed && result.ConsumedHeroIds.SequenceEqual([10U]),
            "valid hero advance did not consume its material hero");

        PlayerAccount saved = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("advanced account disappeared");
        Assert(saved.Dock.Heroes.Select(h => h.HeroId).SequenceEqual(
                   [20U, 30U, 40U, 50U, 60U, 70U, 80U, 90U, 100U, 110U, 120U, 130U, 140U]) &&
               saved.Dock.Heroes.Select(h => h.HeroId).Distinct().Count() == saved.Dock.Heroes.Count,
            "hero advance duplicated its target or overwrote the neighboring hero");
        Hero advanced = saved.Dock.Heroes.Single(h => h.HeroId == 20);
        Assert(advanced.TemplateId == 10210513 && advanced.Advance == 1,
            "hero advance did not persist the upgraded target");
        Assert(saved.Character.Gold == 1_580_000,
            "hero advance did not deduct the configured currency cost");
        Assert(saved.Equip!.Items.Single(e => e.EquipId == 101).HeroId == 0 &&
               saved.Equip.Items.Single(e => e.EquipId == 102).HeroId == 20 &&
               saved.Equip.Items.Single(e => e.EquipId == 103).HeroId == 30,
            "hero advance left orphan equipment or changed a surviving hero's equipment");

        var insufficientItemArgs = new ProtocolPackage().Write(0x08, 90UL);
        for (int i = 0; i < 15; i++) insufficientItemArgs.Write(0x18, 120004UL);
        for (int i = 0; i < 14; i++) insufficientItemArgs.Write(0x18, 17512UL);
        HeroService.AdvanceResult itemRejected = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", insufficientItemArgs.ToArray()), profileId, CancellationToken.None);
        Assert(!itemRejected.Changed, "hero advance ignored its configured item quantity");

        var itemArgs = new ProtocolPackage().Write(0x08, 90UL);
        for (int i = 0; i < 15; i++) itemArgs.Write(0x18, 120004UL);
        for (int i = 0; i < 15; i++) itemArgs.Write(0x18, 17512UL);
        HeroService.AdvanceResult itemResult = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", itemArgs.ToArray()), profileId, CancellationToken.None);
        Assert(itemResult.Changed && itemResult.UpdatedHero?.TemplateId == 12550113,
            "configured item-based hero advance was rejected");
        PlayerAccount itemSaved = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("item-advanced account disappeared");
        Assert(itemSaved.Bag!.Items.Single(i => i.TemplateId == 120004).Num == 0 &&
               itemSaved.Bag.Items.Single(i => i.TemplateId == 17512).Num == 0 &&
               itemSaved.Character.Gold == 1_500_000,
            "item-based hero advance deducted the wrong inventory or currency amount");

        byte[] invalidQualityArgs = new ProtocolPackage()
            .Write(0x08, 100UL)
            .Write(0x10, 120UL)
            .ToArray();
        HeroService.AdvanceResult invalidQualityRejected = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", invalidQualityArgs), profileId, CancellationToken.None);
        Assert(!invalidQualityRejected.Changed,
            "optional hero advance accepted a material below configured quality 4");

        byte[] optionalArgs = new ProtocolPackage()
            .Write(0x08, 100UL)
            .Write(0x10, 110UL)
            .ToArray();
        HeroService.AdvanceResult optionalResult = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", optionalArgs), profileId, CancellationToken.None);
        Assert(optionalResult.Changed && optionalResult.ConsumedHeroIds.SequenceEqual([110U]) &&
               optionalResult.UpdatedHero?.TemplateId == 10240317,
            "configured quality-4 optional hero advance was rejected");
        PlayerAccount optionalSaved = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("optional-advanced account disappeared");
        Assert(optionalSaved.Dock.Heroes.All(hero => hero.HeroId != 110) &&
               optionalSaved.Character.Gold == 1_000_000,
            "optional hero advance consumed the wrong hero or currency amount");

        byte[] ssrOptionalArgs = new ProtocolPackage()
            .Write(0x08, 130UL)
            .Write(0x10, 140UL)
            .ToArray();
        HeroService.AdvanceResult ssrOptionalResult = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", ssrOptionalArgs), profileId, CancellationToken.None);
        Assert(ssrOptionalResult.Changed && ssrOptionalResult.ConsumedHeroIds.SequenceEqual([140U]) &&
               ssrOptionalResult.UpdatedHero?.TemplateId == 10240327,
            "configured quality-5 optional hero advance was rejected");
        PlayerAccount ssrOptionalSaved = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("SSR optional-advanced account disappeared");
        Assert(ssrOptionalSaved.Dock.Heroes.All(hero => hero.HeroId != 140) &&
               ssrOptionalSaved.Character.Gold == 500_000,
            "SSR optional hero advance consumed the wrong hero or currency amount");

        byte[] selfConsumeArgs = new ProtocolPackage()
            .Write(0x08, 20UL)
            .Write(0x10, 20UL)
            .ToArray();
        HeroService.AdvanceResult rejected = await heroService.BuildAdvanceRetAsync(
            new TRequest("hero.HeroAdvance", selfConsumeArgs), profileId, CancellationToken.None);
        Assert(!rejected.Changed,
            "hero advance allowed the target hero to consume itself");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

static async Task FrameCodecTest()
{
    await using var output = new MemoryStream();
    await FrameCodec.WriteAsync(output, new { hello = "world" });
    await using var input = new FragmentedStream(output.ToArray(), 1);
    using var doc = await FrameCodec.ReadAsync(input);
    Assert(doc is not null && doc.RootElement.GetProperty("hello").GetString() == "world", "frame payload mismatch");
}

static Task LoginProtobufTest()
{
    var request = new TArgLogin("local-player", 1712345678, "2026-08-13", "offline-hash",
        new TSampleInfo("uuid", "desktop", "win", "loopback", "windows", "jp.local"));
    var bytes = GameLoginCodec.Encode(request);
    var decoded = GameLoginCodec.DecodeLogin(bytes);
    Assert(decoded == request, "login protobuf request mismatch");
    var response = new TRetLogin("0", "1");
    Assert(GameLoginCodec.DecodeLoginResponse(GameLoginCodec.Encode(response)) == response,
        "login protobuf response mismatch");
    Assert(GameOperationCodes.Login == 2, "login operation code changed");
    return Task.CompletedTask;
}

static async Task GameLoginFrameTest()
{
    var frame = new GameLoginFrame(GameOperationCodes.Login,
        GameLoginCodec.Encode(new TArgLogin("frame-player", 1, "open", "hash")));
    await using var output = new MemoryStream();
    await GameLoginFrameCodec.WriteAsync(output, frame);
    await using var input = new FragmentedStream(output.ToArray(), 1);
    var decoded = await GameLoginFrameCodec.ReadAsync(input);
    Assert(decoded?.Operation == GameOperationCodes.Login &&
        GameLoginCodec.DecodeLogin(decoded.Payload).Pid == "frame-player", "game login frame mismatch");
}

static Task RemouldConfigAndCodecTest()
{
    RemouldConfigLoader.Load(FindClientConfigDir());
    Assert(RemouldConfigLoader.AllEffects.Count >= 800,
        "ship remould effect config was not loaded");
    ConfigShipRemouldTemplate stage = RemouldConfigLoader.GetTemplate(525)
        ?? throw new InvalidDataException("Oakland remould stage 525 was not loaded");
    IReadOnlyList<long> stageEffects = stage.RemouldItemGroup is { Count: > 0 } configuredEffects
        ? configuredEffects
        : throw new InvalidDataException("Oakland remould stage has no effects");
    Assert(stageEffects.All(id => RemouldConfigLoader.GetEffect(checked((int)id)) is not null),
        "a stage references a missing remould effect");
    Assert(RemouldConfigLoader.AllEffects.Values
        .SelectMany(effect => effect.Cost ?? [])
        .All(cost => cost.Count >= 3 && cost[0] is not (2 or 3)),
        "remould config contains an unsupported instance-asset cost");

    var args = new ProtocolPackage();
    args.Write(0x08, 7UL);
    args.Write(0x10, 388UL);
    HeroRemouldArg decoded = ProtocolDecoder.DecodeHeroRemouldArg(args.ToArray());
    Assert(decoded == new HeroRemouldArg(7, 388), "TRemouldArg field layout did not decode");

    byte[] hero = PlayerDataCodec.Encode(new HeroGrid(
        HeroId: 7,
        TemplateId: PlayerAccountFactory.DefaultHeroTemplateId,
        Lvl: 80,
        AdvLv: 3,
        ArrRemouldEffect: [388, 400],
        RemouldLV: 2));
    Assert(ContainsSequence(hero, new byte[] { 0xB8, 0x01, 0x84, 0x03 }) &&
        ContainsSequence(hero, new byte[] { 0xB8, 0x01, 0x90, 0x03 }),
        "THeroGrid did not encode repeated ArrRemouldEffect field 23");
    Assert(ContainsSequence(hero, new byte[] { 0xC0, 0x01, 0x02 }) &&
        ContainsSequence(hero, new byte[] { 0xC8, 0x01, 0x03 }),
        "THeroGrid did not encode RemouldLV/AdvLv fields 24/25");
    return Task.CompletedTask;
}

static Task ConstructionConfigAndCodecTest()
{
    string configDir = FindClientConfigDir();
    ConstructionConfigLoader.Load(configDir);
    Assert(ConstructionConfigLoader.Formulas.Count == 4,
        "traditional construction formulas were not loaded");
    Assert(ConstructionConfigLoader.Qualities.Count == 2020,
        "traditional construction quality curve was not loaded");
    Assert(ConstructionConfigLoader.Ships.Count == 30,
        "traditional construction ship packages were not loaded");
    Assert(ConstructionService.FormatFormulaMessage(999, 999, 999) ==
           "固定建造配方\n金999 钢999 铝999",
        "traditional construction discussion text does not fit all three resources");

    ShipMainLoader.Load(configDir);
    ShipHandbookLoader.Load(configDir);
    var (_, _, _, shipInfos) = BuildShipExtractLoader.Load(configDir);
    BuildFormulaCatalog.Load(shipInfos);
    var remouldHandbooks = ShipHandbookLoader.All
        .Where(entry => entry.Value.ShowState == 1 && entry.Value.ShowTag == 1)
        .ToList();
    Assert(remouldHandbooks.Count > 0,
        "client config did not contain an open remould handbook entry for construction alias coverage");
    foreach (var (remouldShipInfoId, _) in remouldHandbooks)
    {
        Assert(shipInfos.TryGetValue(remouldShipInfoId, out ConfigShipInfo? remouldInfo) &&
               remouldInfo.SfId > 0,
            $"remould handbook {remouldShipInfoId} did not identify its base ship");
        int baseTemplateId = BuildFormulaCatalog.TryGetTemplateByHtid(checked((int)remouldInfo!.SfId));
        Assert(baseTemplateId > 0 &&
               BuildFormulaCatalog.TryGetTemplateByHtid(remouldShipInfoId) == baseTemplateId,
            $"remould handbook {remouldShipInfoId} did not resolve to its base construction formula");
        Assert(BuildFormulaCatalog.GetFormula(remouldShipInfoId * 10 + 1) is null,
            $"remould handbook {remouldShipInfoId} became an independent construction target");
    }

    var steel = new ProtocolPackage().Write(0x08, 10029UL).Write(0x10, 30UL);
    var aluminium = new ProtocolPackage().Write(0x08, 10030UL).Write(0x10, 40UL);
    var project = new ProtocolPackage()
        .Write(0x0A, steel.ToArray())
        .Write(0x0A, aluminium.ToArray())
        .Write(0x10, 50UL);
    var request = new ProtocolPackage().Write(0x0A, project.ToArray());
    ConstructionProjectsArg decoded = ProtocolDecoder.DecodeConstructionProjectsArg(request.ToArray());
    Assert(decoded.Projects.Count == 1 && decoded.Projects[0].Gold == 50 &&
        decoded.Projects[0].Items.SequenceEqual([
            new ConstructionItemArg(10029, 30), new ConstructionItemArg(10030, 40)]),
        "traditional construction project protobuf mismatch");
    Assert(ProtocolDecoder.DecodeConstructionIndexArg(new byte[] { 0x08, 0x01, 0x08, 0x02 })
        .Indexes.SequenceEqual([1, 2]), "unpacked construction indexes did not decode");
    Assert(ProtocolDecoder.DecodeConstructionIndexArg(new byte[] { 0x0A, 0x02, 0x01, 0x02 })
        .Indexes.SequenceEqual([1, 2]), "packed construction indexes did not decode");

    ConstructionProject persistedProject = new(
        [new ConstructionItem(10029, 30), new ConstructionItem(10030, 40)], 50);
    PlayerAccount account = PlayerAccountFactory.CreateDefault("queue-test", 1) with
    {
        Construction = new PlayerConstruction([
            new ConstructionJob(1, 40110211, 100, 100, false, persistedProject),
            new ConstructionJob(2, 40110211, 200, 200, false, persistedProject),
            new ConstructionJob(3, 40110211, 50, 0, false, persistedProject)], persistedProject, 4),
    };
    PlayerAccount refreshed = ConstructionService.RefreshQueue(account, 500);
    Assert(refreshed.Construction?.Jobs.All(job => job.Completed) == true,
        "offline construction queue did not cascade through waiting work");
    byte[] info = ConstructionService.EncodeInfo(refreshed.Construction);
    Assert(info.Count(value => value == 0x0A) >= 3,
        "completed traditional construction jobs were not encoded");
    return Task.CompletedTask;
}

static Task BuildingCodecTest()
{
    BuildingConfigLoader.Load(FindClientConfigDir());
    Assert(BuildingConfigLoader.Infos.Count == 35 && BuildingConfigLoader.Lands.Count == 10,
        "building or land configs were not loaded");
    Assert(BuildingConfigLoader.GetInfo(11) is { Type: 2, Level: 1 } &&
        BuildingConfigLoader.GetLand(2)?.BuildinggroupId?.Contains(2) == true,
        "electric building/land config mismatch");
    Assert(BuildingConfigLoader.GetLevelUp(11)?.Leveluptime == 0,
        "JP level-1 building should complete immediately");
    Assert(BuildingConfigLoader.MaterialTemplateIds.SequenceEqual(
            new[] { 14001, 14002, 14003, 14004, 14011 }),
        "building material ids were not collected from level-up configs");

    PlayerBuilding state = PlayerAccountFactory.DefaultBuilding(1234);
    Assert(state.Buildings.Count == 2 && state.Buildings.Any(x => x.Tid == 2) &&
        state.Buildings.Any(x => x.Tid == 41), "default office/dormitory state mismatch");
    Assert(state.Lands.Any(x => x.Index == 1 && x.BuildingId == 1) &&
        state.Lands.Any(x => x.Index == 6 && x.BuildingId == 2), "default building land mapping mismatch");

    byte[] snapshot = PlayerDataCodec.Encode(BuildingService.ToProtocol(state, 1234));
    // 字段 5 Productivity 无人驻守时为 10000（28 90 4E），不再是 0（客户端会显示 −100%）。
    Assert(ContainsSequence(snapshot, new byte[] { 0x08, 0x01, 0x10, 0x02, 0x18, 0x02, 0x28, 0x90, 0x4E }),
        "building snapshot omitted the level-2 office");
    Assert(ContainsSequence(snapshot, new byte[] { 0x08, 0x02, 0x10, 0x29, 0x18, 0x01, 0x28, 0x90, 0x4E }),
        "building snapshot omitted the level-1 dormitory");

    var setHero = new ProtocolPackage().Write(0x08, 2UL).Write(0x10, 1UL);
    SetBuildingHeroArg decoded = PlayerDataCodec.DecodeSetBuildingHeroArg(setHero.ToArray());
    Assert(decoded.BuildingId == 2 && decoded.HeroIds.SequenceEqual(new uint[] { 1 }),
        "building.SetHero request did not decode");

    var setList = new ProtocolPackage()
        .Write(0x08, 1UL)
        .Write(0x08, 2UL)
        .Write(0x10, unchecked((ulong)(long)-1))
        .Write(0x10, 1UL)
        .Write(0x10, unchecked((ulong)(long)-1));
    SetBuildingListHeroArg listDecoded = PlayerDataCodec.DecodeSetBuildingListHeroArg(setList.ToArray());
    Assert(listDecoded.BuildingIds.SequenceEqual(new[] { 1, 2 }) &&
        listDecoded.HeroIds.SequenceEqual(new[] { -1, 1, -1 }),
        "building.SetBuildingListHero request did not preserve -1 separators");

    var add = new ProtocolPackage().Write(0x08, 11UL).Write(0x10, 2UL);
    AddBuildingArg addDecoded = PlayerDataCodec.DecodeAddBuildingArg(add.ToArray());
    Assert(addDecoded == new AddBuildingArg(11, 2), "building.AddBuilding request did not decode");
    var buildingId = new ProtocolPackage().Write(0x08, 3UL);
    Assert(PlayerDataCodec.DecodeBuildingIdArg(buildingId.ToArray()) == 3,
        "building lifecycle building id did not decode");
    Assert(PlayerDataCodec.EncodeAddBuildingRet(3).SequenceEqual(new byte[] { 0x08, 0x03 }),
        "building.AddBuilding response did not contain the new building id");
    return Task.CompletedTask;
}

static Task StoryUnlockConfigTest()
{
    string configDir = FindClientConfigDir();
    ChapterCopyLoader.Load(configDir);
    CharacterStoryLoader.Load(configDir);

    Assert(ChapterCopyLoader.GetCopyIds(1).Count > 0, "main story chapter was not loaded");
    Assert(ChapterCopyLoader.GetCopyIds(14005).Contains(953001), "event story chapter was not loaded");
    Assert(ChapterCopyLoader.GetCopyIds(14006).Contains(955001), "side story chapter was not loaded");
    Assert(ChapterCopyLoader.GetCopyIds(10001).Contains(40001), "archived activity story was not loaded");
    Assert(ChapterCopyLoader.GetCopyType(953001) == 1, "event story was not exposed as PlotCopy");
    Assert(ChapterCopyLoader.AllChapterMemories.Contains(new ChapterMemory(10001, 15)),
        "archived activity story was not marked fully unlocked");
    Assert(CharacterStoryLoader.AllMemories.Count > 0, "personal stories were not loaded");
    Assert(CharacterStoryLoader.AllMemories.Contains(new HeroMemory(2064011, 1001)),
        "known personal story was not exposed through HeroMemoryList");
    return Task.CompletedTask;
}

static Task IllustrateBehaviourUnlockTest()
{
    HandbookBehaviourLoader.Load(FindClientConfigDir());
    IReadOnlyList<int> expected = HandbookBehaviourLoader.AllBehaviourIds;
    Assert(expected.Count >= 60, "handbook behaviour index was not fully loaded");
    Assert(expected.SequenceEqual(expected.OrderBy(id => id)) && expected.Distinct().Count() == expected.Count,
        "handbook behaviour ids must be unique and deterministic");

    IllustrateInfo info = GameServices.BuildUnlockedIllustrateInfo(102105, 123456);
    Assert(info.IllustrateId == 102105 && info.GetTime == 123456,
        "unlocked illustrate entry lost its identity or timestamp");
    Assert(info.BehaviourList is not null && info.BehaviourList.SequenceEqual(expected),
        "unlocked illustrate entry did not include every configured behaviour");
    return Task.CompletedTask;
}

static Task HeroMemoryCodecTest()
{
    var memory = new HeroMemory(2064011, 1001);
    var nested = PlayerDataCodec.Encode(memory);
    var payload = PlayerDataCodec.Encode(new IllustrateInfoRet(
        IllustrateEquipList: [new IllustrateEquipInfo()],
        HeroMemoryList: [memory]));

    Assert(payload.Length >= nested.Length + 4, "personal story payload is incomplete");
    Assert(payload[0] == 0x42, "HeroMemoryList must use illustrate field 8");
    Assert(payload[1] == nested.Length, "HeroMemory message length mismatch");
    Assert(payload.AsSpan(2, nested.Length).SequenceEqual(nested), "HeroMemory message body mismatch");
    Assert(payload[2 + nested.Length] == 0x4A, "IllustrateEquipList must remain field 9");

    var chapter = new ChapterMemory(10001, 15);
    var chapterNested = PlayerDataCodec.Encode(chapter);
    var chapterPayload = PlayerDataCodec.Encode(new StoryMemoryList([chapter]));
    Assert(chapterPayload[0] == 0x0A, "MemoryList must use field 1");
    Assert(chapterPayload[1] == chapterNested.Length, "MemoryInfo message length mismatch");
    Assert(chapterPayload.AsSpan(2).SequenceEqual(chapterNested), "MemoryInfo message body mismatch");
    return Task.CompletedTask;
}

static Task EquipEnhanceRetCodecTest()
{
    var payload = TMessageCodec.EncodeEquipEnhanceRet(42, 3, 200);
    Assert(payload.AsSpan().SequenceEqual(new byte[] { 0x08, 0x2A, 0x10, 0x03, 0x18, 0xC8, 0x01 }),
        "equipment enhancement response protobuf mismatch");
    return Task.CompletedTask;
}

static Task EquipRiseStarArgsCodecTest()
{
    var args = TMessageCodec.DecodeEquipRiseStarArgs(new byte[] { 0x08, 0x08, 0x10, 0x0E, 0x10, 0x0F });
    Assert(args.EquipId == 8 && args.ConsumeIds!.SequenceEqual(new uint[] { 14, 15 }),
        "equipment renovation request protobuf mismatch");
    return Task.CompletedTask;
}

static Task BagDeletionMarkerCodecTest()
{
    var payload = PlayerDataCodec.Encode(new BagGridInfo(10180, 0));
    Assert(payload.AsSpan().SequenceEqual(new byte[] { 0x08, 0xC4, 0x4F, 0x10, 0x00 }),
        "zero-count bag entry omitted the explicit Num=0 deletion marker");
    return Task.CompletedTask;
}

static Task TreasureCodecTest()
{
    var arg = PlayerDataCodec.DecodeBagNormalTreasureInfoArg(
        new byte[] { 0x08, 0xBC, 0x50, 0x10, 0x01 });
    Assert(arg == new BagNormalTreasureInfoArg(10300, 1),
        "normal treasure request protobuf mismatch");

    var payload = PlayerDataCodec.Encode(new BagTreasureInfoRet(
        [new CommonReward(Type: 2, ConfigId: 30164, Num: 1, Id: 7)], TreasureId: 10300));
    Assert(payload.Length > 5 && payload[0] == 0x0A && payload[^3..].SequenceEqual(new byte[] { 0x10, 0xBC, 0x50 }),
        "normal treasure response protobuf mismatch");
    return Task.CompletedTask;
}

static Task BuildShipRewardCodecTest()
{
    var payload = ProtocolEncoder.EncodeBuildShipRet(
        [new CommonReward(Type: 1, ConfigId: 2, Num: 1, Id: 3)]);
    Assert(payload.AsSpan().SequenceEqual(
        new byte[] { 0x0A, 0x08, 0x08, 0x01, 0x10, 0x02, 0x18, 0x01, 0x20, 0x03, 0x1A, 0x00 }),
        "build ship response must omit empty SpReward while retaining aligned TransReward");
    return Task.CompletedTask;
}

static async Task BuildShipHundredRewardIntegrationTest()
{
    string repositoryRoot = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-buildship-reward-" + Guid.NewGuid().ToString("N"));
    const string profileId = "hundred-reward-player";
    const string legacyProfileId = "legacy-polluted-bag-player";
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        ServerOptions options = ServerOptions.Parse([
            "--data=" + dataRoot,
            "--client-path=" + Path.Combine(repositoryRoot, "blueoath", "blueoath"),
            "--profile-id=" + profileId
        ]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        var buildShip = new BuildShipService(services);

        int poolId = 0, limitCount = 0, shipTemplateId = 0, rewardCount = 0;
        foreach (var (id, config) in services.ExtractShips)
        {
            foreach (var entry in config.HundredReward ?? [])
            {
                if (entry.Count >= 4 && entry[0] == 120 && entry[1] == GameServices.GoodsTypeShip &&
                    ShipHandbookLoader.GetShipName(checked((int)entry[2])) == "朝日")
                {
                    poolId = id;
                    limitCount = checked((int)entry[0]);
                    shipTemplateId = checked((int)entry[2]);
                    rewardCount = checked((int)entry[3]);
                    break;
                }
            }
            if (poolId != 0) break;
        }
        Assert(poolId != 0 && shipTemplateId != 0,
            "real client config has no 120-draw Asahi ship reward to exercise");

        PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, CancellationToken.None);
        var originalHeroIds = account.Dock.Heroes.Select(hero => hero.HeroId).ToHashSet();
        var buildState = account.BuildState ?? new PlayerBuildState(
            new Dictionary<int, int>(),
            new Dictionary<int, IReadOnlyList<int>>(),
            new Dictionary<int, IReadOnlyList<int>>());
        var drawCount = buildState.DrawCount.ToDictionary(pair => pair.Key, pair => pair.Value);
        drawCount[poolId] = limitCount;
        account = account with { BuildState = buildState with { DrawCount = drawCount } };
        await services.SaveAccountAsync(account, CancellationToken.None);

        var requestArgs = new List<byte>();
        AppendTestVarint(requestArgs, 1 << 3);
        AppendTestVarint(requestArgs, checked((ulong)poolId));
        AppendTestVarint(requestArgs, 2 << 3);
        AppendTestVarint(requestArgs, checked((ulong)limitCount));
        byte[] response = await buildShip.BuildBuildShipRewardRetAsync(
            new TRequest("buildship.BuildShipReward", requestArgs.ToArray()), profileId, CancellationToken.None);

        PlayerAccount claimed = await services.GetOrCreateAccountAsync(profileId, CancellationToken.None);
        Hero granted = claimed.Dock.Heroes.Single(hero => !originalHeroIds.Contains(hero.HeroId));
        Assert(granted.TemplateId == shipTemplateId,
            "120-draw reward created the wrong ship template");
        Assert(claimed.Bag?.Items.Any(item => item.TemplateId == shipTemplateId) != true,
            "ship reward polluted the item bag");
        byte[] expectedReward = PlayerDataCodec.Encode(new CommonReward(
            GameServices.GoodsTypeShip, shipTemplateId, rewardCount, checked((int)granted.HeroId)));
        Assert(ContainsSequence(response, expectedReward),
            "ship reward response omitted the created hero instance id");
        Assert(claimed.BuildState?.UsedRewardInfo.GetValueOrDefault(poolId)?.Contains(limitCount) == true,
            "120-draw reward claim state was not persisted");

        byte[] repeated = await buildShip.BuildBuildShipRewardRetAsync(
            new TRequest("buildship.BuildShipReward", requestArgs.ToArray()), profileId, CancellationToken.None);
        Assert(repeated.Length == 0 &&
               (await services.GetOrCreateAccountAsync(profileId, CancellationToken.None)).Dock.Heroes.Count == claimed.Dock.Heroes.Count,
            "repeated reward claim was not idempotent");

        PlayerAccount legacy = PlayerAccountFactory.CreateDefault(
            legacyProfileId, checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), legacyProfileId);
        int legacyHeroCount = legacy.Dock.Heroes.Count;
        legacy = GameServices.AddBagItem(legacy, shipTemplateId, 1);
        await repo.SaveAccountAsync(legacy, CancellationToken.None);
        var migrationServices = new GameServices(repo, options, loggerFactory);
        PlayerAccount healed = await migrationServices.GetOrCreateAccountAsync(legacyProfileId, CancellationToken.None);
        Assert(healed.Bag?.Items.Any(item => item.TemplateId == shipTemplateId) != true,
            "GetOrCreateAccountAsync did not remove a ship template from the legacy item bag");
        Assert(healed.Dock.Heroes.Count == legacyHeroCount + 1 &&
               healed.Dock.Heroes.Count(hero => hero.TemplateId == shipTemplateId) == 1,
            "legacy bag cleanup did not restore the claimed ship as a real hero instance");
        PlayerAccount persisted = await repo.LoadAccountAsync(legacyProfileId, CancellationToken.None)
            ?? throw new InvalidDataException("legacy profile disappeared after bag migration");
        Assert(persisted.Bag?.Items.Any(item => item.TemplateId == shipTemplateId) != true,
            "legacy bag cleanup was not persisted");
        Assert(persisted.Dock.Heroes.Count(hero => hero.TemplateId == shipTemplateId) == 1,
            "restored legacy ship instance was not persisted");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

static Task ClientLoginWireTest()
{
    var requestPayload = GameLoginCodec.Encode(new TArgLogin("wire-player", 1, "open", "hash"));
    var requestPacket = ClientGameWireCodec.EncodeClientRequest(GameOperationCodes.Login, requestPayload, 42, 3);
    var request = ClientGameWireCodec.DecodeClientRequest(requestPacket);
    Assert(request.Channel == 0 && request.Operation == 2 && request.SessionId == 42 && request.State == 3 &&
        GameLoginCodec.DecodeLogin(request.Payload).Pid == "wire-player", "client request wire mismatch");
    var responsePacket = ClientGameWireCodec.EncodeServerResponse(GameOperationCodes.Login,
        GameLoginCodec.Encode(new TRetLogin("0", "wire-player")));
    var response = ClientGameWireCodec.DecodeServerResponse(responsePacket);
    Assert(response.Operation == 2 && GameLoginCodec.DecodeLoginResponse(response.Payload).FeignRoleId == "wire-player",
        "server response wire mismatch");
    return Task.CompletedTask;
}

static async Task StorageTest()
{
    var root = Path.Combine(Path.GetTempPath(), "blueoath-tests-" + Guid.NewGuid().ToString("N"));
    var repo = new SqliteGameRepository(root);
    await repo.CreateAsync("one", "One"); await repo.CreateAsync("two", "Two");
    var one = await repo.LoadAsync("one"); var two = await repo.LoadAsync("two");
    Assert(one?.Name == "One" && two?.Name == "Two", "profiles were not isolated");
    await repo.ResetAsync("one"); Assert(await repo.LoadAsync("one") is null, "reset did not remove profile");
    Directory.Delete(root, true);
}

static async Task GameTest()
{
    var root = Path.Combine(Path.GetTempPath(), "blueoath-tests-" + Guid.NewGuid().ToString("N"));
    var repo = new SqliteGameRepository(root); await repo.CreateAsync("player", "Player");
    var game = new GameService(repo, ProtocolProfile.Japan);
    await game.SetFormationAsync("player", [1001, 1002]); await game.EnterStageAsync("player", 1);
    var result = await game.ResolveBattleAsync("player", 1, true);
    Assert(result.Outcome.Victory && result.State.Coins == 100 && result.State.Fuel == 90, "battle result mismatch");
    Directory.Delete(root, true);
}

static async Task AccountStorageTest()
{
    var root = Path.Combine(Path.GetTempPath(), "blueoath-tests-" + Guid.NewGuid().ToString("N"));
    var repo = new SqliteGameRepository(root);

    // 创建档案时应同时播种默认账号（角色 + 船坞）。
    await repo.CreateAsync("hero", "Hero");
    var account = await repo.LoadAccountAsync("hero");
    Assert(account is not null, "account was not created with profile");
    Assert(account!.Character.Uid == 1 && account.Character.Name == "Hero", "character defaults mismatch");
    Assert(account.Character.SecretaryId == 1, "secretary id mismatch");
    Assert(account.Dock.Heroes.Count == 1, "dock should contain one hero");
    Assert(account.Dock.Heroes[0].HeroId == account.Character.SecretaryId, "secretary hero not in dock");
    Hero initialHero = account.Dock.Heroes[0];
    Assert(initialHero.Lock, "initial secretary hero was not locked");
    Assert(initialHero.EquipSlots?.SequenceEqual(new uint[] { 1, 0, 2, 0, 0, 0 }) == true,
        "initial secretary hero equipment slots mismatch");
    Assert(account.Equip?.Items is { Count: 2 } initialEquips &&
           initialEquips.Select(x => x.TemplateId).SequenceEqual(new[]
           {
               PlayerAccountFactory.DefaultHeroMainGunTemplateId,
               PlayerAccountFactory.DefaultHeroSecondaryGunTemplateId,
           }) &&
           initialEquips.All(x => x.HeroId == initialHero.HeroId),
        "initial secretary equipment instances were not created and bound");

    // 修改并保存账号，验证往返持久化。
    var updated = account with
    {
        Character = account.Character with { Level = 10 },
        Dock = new HeroDock(
            [new Hero(1, PlayerAccountFactory.DefaultHeroTemplateId, 5), new Hero(2, 10210512, 3)],
            BagSize: 200),
        Building = account.Building! with
        {
            Buildings = account.Building!.Buildings
                .Select(building => building.Id == 2
                    ? building with { HeroIds = new uint[] { 1 } }
                    : building)
                .ToArray(),
        },
    };
    await repo.SaveAccountAsync(updated);
    var reloaded = await repo.LoadAccountAsync("hero");
    Assert(reloaded is not null, "account was not reloaded");
    Assert(reloaded!.Character.Level == 10, "character update not persisted");
    Assert(reloaded.Dock.Heroes.Count == 2 && reloaded.Dock.BagSize == 200, "dock update not persisted");
    Assert(reloaded.Building?.Buildings.Single(x => x.Id == 2).HeroIds.SequenceEqual(new uint[] { 1 }) == true,
        "building assignment not persisted");
    // Reset 应同时清除档案与账号。
    await repo.ResetAsync("hero");
    Assert(await repo.LoadAsync("hero") is null, "reset did not remove profile");
    Assert(await repo.LoadAccountAsync("hero") is null, "reset did not remove account");

    Directory.Delete(root, true);
}

static Task ModTest()
{
    var root = Path.Combine(Path.GetTempPath(), "blueoath-mod-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    var dir = Path.Combine(root, "sample"); Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "mod.json"), "{\"id\":\"sample\",\"version\":\"1\",\"entry\":\"main.lua\",\"targetClients\":[\"jp-1.4.0\"],\"dependencies\":[],\"loadOrder\":1,\"enabled\":true}");
    File.WriteAllText(Path.Combine(dir, "main.lua"), "function on_login() end");
    var manager = new ModManager(root, "jp-1.4.0"); manager.LoadAll(); Assert(manager.LoadedIds.SequenceEqual(["sample"]), "targeted mod was not loaded");
    Directory.Delete(root, true); return Task.CompletedTask;
}

static Task AccountProfileBootstrapTest()
{
    const string profileId = "player-test_01";
    const string profileName = "测试账号";
    ServerOptions options = ServerOptions.Parse([
        "--profile-id=" + profileId,
        "--profile-name=" + profileName
    ]);
    Assert(options.ProfileId == profileId && options.ProfileName == profileName,
        "server profile identity options mismatch");

    var endpoints = new ServerEndpoints { GameLoginPort = 8123 };
    var responder = new BootstrapHttpResponder(endpoints, new AnnouncementConfig(), options);

    using JsonDocument plData = JsonDocument.Parse(
        responder.BuildResponse("GET /phone/getPlData/getPlData HTTP/1.1").Body);
    // 事件 1007 的信封是 errornu/errordesc/data，平台字段（含 pid）都在 data 之下，
    // 见 BootstrapHttpResponder 的 getPlData 分支；摊到根对象会让 SDK 走 111111
    // 未知错误分支，因此响应体是对的，断言需跟随该结构。
    Assert(plData.RootElement.GetProperty("data").GetProperty("pid").GetString() == profileId,
        "getPlData did not expose selected profile");

    using JsonDocument login = JsonDocument.Parse(
        responder.BuildResponse("GET /login?test=1 HTTP/1.1").Body);
    Assert(login.RootElement.GetProperty("Pid").GetString() == profileId &&
        login.RootElement.GetProperty("openid").GetString() == profileId,
        "SDK login did not expose selected profile");

    using JsonDocument hash = JsonDocument.Parse(
        responder.BuildResponse("GET /gethash HTTP/1.1").Body);
    Assert(hash.RootElement.GetProperty("pid").GetString() == profileId,
        "gethash did not expose selected profile");
    return Task.CompletedTask;
}

static Task DuplicateHeroMigrationTest()
{
    PlayerAccount account = PlayerAccountFactory.CreateDefault("duplicate-hero", 1);
    Hero original = account.Dock.Heroes[0] with
    {
        HeroId = 11,
        TemplateId = 20640214,
        Advance = 3,
        Level = 49,
    };
    Hero upgraded = original with { TemplateId = 20640215, Advance = 4 };
    Hero neighbor = original with { HeroId = 14, TemplateId = 20620111, Advance = 0 };
    account = account with
    {
        Dock = account.Dock with { Heroes = [original, upgraded, neighbor] },
    };

    PlayerAccount repaired = GameServices.RepairDuplicateHeroIds(account);
    Assert(repaired.Dock.Heroes.Select(hero => hero.HeroId).SequenceEqual([11U, 14U]) &&
           repaired.Dock.Heroes[0].TemplateId == 20640215 &&
           repaired.Dock.Heroes[0].Advance == 4,
        "duplicate hero migration did not preserve the upgraded record");
    Assert(ReferenceEquals(GameServices.RepairDuplicateHeroIds(repaired), repaired),
        "duplicate hero migration was not idempotent");
    return Task.CompletedTask;
}

static Task AccountProfileNameMigrationTest()
{
    const string profileId = "player-legacy01";
    PlayerAccount legacy = PlayerAccountFactory.CreateDefault(profileId, 1) with
    {
        ProfileDisplayName = null
    };

    PlayerAccount migrated = GameServices.SynchronizeProfileDisplayName(legacy, "Asa");
    Assert(migrated.Character.Name == "Asa" && migrated.ProfileDisplayName == "Asa",
        "legacy profile id name was not migrated to the launcher account name");

    PlayerAccount renamedInLauncher = GameServices.SynchronizeProfileDisplayName(migrated, "Bob");
    Assert(renamedInLauncher.Character.Name == "Bob" && renamedInLauncher.ProfileDisplayName == "Bob",
        "launcher-managed character name did not follow account rename");

    PlayerAccount custom = renamedInLauncher with
    {
        Character = renamedInLauncher.Character with { Name = "游戏内昵称" }
    };
    PlayerAccount preserved = GameServices.SynchronizeProfileDisplayName(custom, "Carol");
    Assert(preserved.Character.Name == "游戏内昵称" && preserved.ProfileDisplayName == "Carol",
        "custom in-game character name was overwritten");

    byte[] userList = GameServices.EncodeGetUsers(preserved);
    ProtocolDecoder.ProtoReader listReader = new(userList);
    Assert(listReader.TryReadField(out int field, out int wire) && field == 1 && wire == 2,
        "saved character was omitted from player.GetUserList");
    byte[] userInfo = listReader.ReadBytes().ToArray();
    Assert(ProtocolDecoder.DecodeStringField(userInfo, 2) == "游戏内昵称" &&
           ProtocolDecoder.DecodeVarintField(userInfo, 3) == 80,
        "player.GetUserList did not preserve the saved character name and level");
    return Task.CompletedTask;
}

static Task EquipmentModTest()
{
    string modsRoot = Path.Combine(FindRepositoryRoot(), "Mods");
    EquipmentModCatalog catalog = EquipmentModLoader.Load(modsRoot, "jp-1.4.0");
    EquipmentModDefinition definition = catalog.Equipment.Single(x => x.Id == 900001);
    Assert(definition.SourceTemplateId == 30023,
        "custom equipment does not clone the expected built-in template");
    Assert(catalog.Goods.Single(x => x.GoodId == 990001) is
        { ShopId: 5, Type: GameServices.GoodsTypeEquip, ItemId: 900001, Num: 1 },
        "custom equipment GM shop good is invalid");

    var source = new ConfigEquip
    {
        EId = 30023,
        Name = "source",
        Quality = 3,
        EnhanceLevelMax = 30,
        StarMax = 5,
        EquipProp = [[8, 67], [3200, 225]],
        EnhanceProp = [[8, 4], [3200, 15]],
    };
    ConfigEquip custom = EquipmentModLoader.BuildConfig(source, definition);
    Assert(custom.EId == 900001 && custom.Name == "未来試作砲" && custom.NoResolve == 1,
        "custom equipment overrides were not applied");
    Assert(custom.EquipProp is [[8, 90], [3200, 300]] &&
           custom.EnhanceProp is [[8, 6], [3200, 20]],
        "custom equipment attributes were not applied");
    Assert(source.EId == 30023 && source.Name == "source",
        "building custom equipment mutated its source template");

    var merged = EquipmentModLoader.MergeGoods(
        new GmGoodsConfig([], new Dictionary<int, int>()), catalog);
    Assert(merged.Goods.Any(x => x.GoodId == 990001),
        "custom equipment was not merged into the GM shop catalog");
    Assert(EquipmentModLoader.Load(modsRoot, "cn-1.5.20").Equipment.Count == 0,
        "JP custom equipment loaded for the CN client");
    return Task.CompletedTask;
}

static Task EquipmentModConfigIntegrationTest()
{
    string root = FindRepositoryRoot();
    string clientPath = Environment.GetEnvironmentVariable("BLUEOATH_TEST_CLIENT_PATH")
        ?? Path.Combine(root, "blueoath", "blueoath");
    string configDir = ConfigDbLoader.BuildConfigDir(clientPath);
    Assert(File.Exists(Path.Combine(configDir, "config_equip.db")),
        "real config_equip.db is missing");
    EquipmentModCatalog catalog = EquipmentModLoader.Load(Path.Combine(root, "Mods"), "jp-1.4.0");
    EquipLoader.Load(configDir, catalog.Equipment);
    ConfigEquip? custom = EquipLoader.Get(900001);
    Assert(custom is { EId: 900001, Name: "未来試作砲", NoResolve: 1 },
        "custom equipment was not added to the real server catalog");
    Assert(custom!.EquipProp is [[8, 90], [3200, 300]],
        "real server catalog contains incorrect custom equipment attributes");
    Assert(EquipLoader.Get(30023) is { EId: 30023 },
        "source equipment disappeared after applying the mod overlay");
    return Task.CompletedTask;
}

static Task FashionPreviewModTest()
{
    string root = FindRepositoryRoot();
    string modsRoot = Path.Combine(root, "Mods");
    var manager = new ModManager(modsRoot, "jp-1.4.0");
    manager.LoadAll();
    Assert(manager.LoadedIds.Contains("fashion-preview-fix.mod"),
        "fashion preview fix was not discoverable by the JP mod loader");

    string entry = File.ReadAllText(Path.Combine(modsRoot, "fashion-preview-fix.mod", "main.lua"));
    Assert(entry.Contains("GetOwnFashionByHeroId", StringComparison.Ordinal) &&
           entry.Contains("hero_id == nil", StringComparison.Ordinal) &&
           entry.Contains("self:GetOwnFashion(sf_id)", StringComparison.Ordinal),
        "fashion preview hook does not guard nil heroes through ship-level ownership");
    Assert(!entry.Contains("key ~= \"heroId\"", StringComparison.Ordinal),
        "fashion preview hook still strips heroId and can break remould ownership checks");
    string bootstrap = File.ReadAllText(Path.Combine(modsRoot, "bootstrap.lua"));
    Assert(bootstrap.Contains("fashion-preview-fix.mod/main.lua", StringComparison.Ordinal) &&
           bootstrap.Contains("global_watchers", StringComparison.Ordinal) &&
           bootstrap.Contains("watch_global = function", StringComparison.Ordinal),
        "fashion preview fix is missing its shared runtime global watcher");
    return Task.CompletedTask;
}

static Task BuildShipNewStateNativePatchTest()
{
    string root = FindRepositoryRoot();
    string source = File.ReadAllText(Path.Combine(root, "native", "Payload", "lua_mod_loader.cpp"));
    Assert(source.Contains("BuildShipCheckShowMeetPatched", StringComparison.Ordinal) &&
           source.Contains("pendingBuildShipIsNew", StringComparison.Ordinal) &&
           source.Contains("ShowGirlUpdatePagePatched", StringComparison.Ordinal) &&
           source.Contains("showGirlSetField(state, paramIndex, \"bNew\")", StringComparison.Ordinal),
        "native xLua bridge does not carry CheckShowMeet into ShowGirlPage");
    Assert(source.Contains("hasBuildNum && !hasGetWay", StringComparison.Ordinal) &&
           source.Contains("TryPatchBuildShipNewState(state)", StringComparison.Ordinal),
        "native build-ship patch is not scoped and invoked for live Lua states");

    string bootstrap = File.ReadAllText(Path.Combine(root, "Mods", "bootstrap.lua"));
    Assert(!bootstrap.Contains("buildship-new-state-fix", StringComparison.Ordinal),
        "build-ship new-state fix still depends on a Lua mod entry");
    return Task.CompletedTask;
}

static async Task TcpIntegrationTest()
{
    var root = FindRepositoryRoot();
    var serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0", "BlueOath.Server.dll");
    Assert(File.Exists(serverDll), "server assembly is missing; build the solution first");
    var data = Path.Combine(Path.GetTempPath(), "blueoath-tcp-" + Guid.NewGuid().ToString("N"));
    var startInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    startInfo.ArgumentList.Add(serverDll); startInfo.ArgumentList.Add("--port=0"); startInfo.ArgumentList.Add("--region=jp"); startInfo.ArgumentList.Add("--data=" + data);
    startInfo.ArgumentList.Add("--client-path=" + Path.Combine(root, "blueoath", "blueoath"));
    startInfo.ArgumentList.Add("--profile-id=local-player");
    startInfo.ArgumentList.Add("--profile-name=默认账号");
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "server process did not start");
        var readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
        var port = ready.RootElement.GetProperty("port").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new LocalGameClient(); await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        JsonElement login = await client.SendAsync<JsonElement>(MessageTypes.Login,
            new { profileId = "local_player", name = "local_player" }, timeout.Token);
        Assert(login.GetProperty("profileId").GetString() == "local-player",
            "legacy login did not return the launcher-selected profile");
        var state = await client.SendAsync<PlayerState>(MessageTypes.State, new { profileId = "local_player" }, timeout.Token); Assert(state.Fuel == 100, "initial state mismatch");
        await client.SendAsync<PlayerState>(MessageTypes.SetFormation, new { profileId = "local_player", shipIds = new[] { 1001, 1002 } }, timeout.Token);
        await client.SendAsync<Stage>(MessageTypes.EnterStage, new { profileId = "local_player", stageId = 1 }, timeout.Token);
        var outcome = await client.SendAsync<JsonElement>(MessageTypes.BattleResult, new { profileId = "local_player", stageId = 1, win = true }, timeout.Token); Assert(outcome.GetProperty("outcome").GetProperty("victory").GetBoolean(), "battle response mismatch");

        var repo = new SqliteGameRepository(data);
        Assert((await repo.ListProfilesAsync()).SequenceEqual(["local-player"]),
            "legacy login created a second profile from the client-supplied id");
        Assert(await repo.LoadAccountAsync("local-player") is not null &&
               await repo.LoadAccountAsync("local_player") is null,
            "legacy login created a second account from the client-supplied id");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static async Task GameLoginIntegrationTest()
{
    var root = FindRepositoryRoot();
    var serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0", "BlueOath.Server.dll");
    var data = Path.Combine(Path.GetTempPath(), "blueoath-login-" + Guid.NewGuid().ToString("N"));
    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--port=0");
    startInfo.ArgumentList.Add("--game-login-port=0");
    startInfo.ArgumentList.Add("--region=jp");
    startInfo.ArgumentList.Add("--data=" + data);
    var clientPath = Environment.GetEnvironmentVariable("BLUEOATH_CLIENT_PATH")
        ?? Path.Combine(root, "blueoath", "blueoath");
    startInfo.ArgumentList.Add("--client-path=" + clientPath);
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "game login server did not start");
        var readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
        var port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        var stream = client.GetStream();
        var observedPushes = new List<TResponse>();

        async Task<TResponse> RoundTrip(string method, byte[]? args)
        {
            var request = TMessageCodec.EncodeRequest(new TRequest(method, args, 1));
            await NetSocketFrameCodec.WriteAsync(stream, request, NetSocketFrameCodec.TypeData, timeout.Token);
            while (true)
            {
                var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                Assert(frame is not null, $"empty response for {method}");
                var response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                // 服务器可能在应答前主动推送（IsResponse == 0），跳过直到拿到真正响应。
                if (response.IsResponse == 1)
                    return response;
                observedPushes.Add(response);
            }
        }

        var login = await RoundTrip("player.Login",
            GameLoginCodec.Encode(new TArgLogin("protobuf-player", 1, "open", "hash")));
        Assert(login.Method == "player.Login", "login response method mismatch");
        Assert(GameLoginCodec.DecodeLoginResponse(login.Ret!).Ret == "ok", "login response ret mismatch");

        var list = await RoundTrip("player.GetUserList", null);
        Assert(list.Method == "player.GetUserList", "get user list response method mismatch");

        var create = await RoundTrip("player.CreateUser",
            new byte[] { 0x0A, 0x05, (byte)'t', (byte)'e', (byte)'s', (byte)'t', (byte)'1', 0x10, 0x01 });
        Assert(create.Method == "player.CreateUser", "create user response method mismatch");

        var userLogin = await RoundTrip("user.UserLogin", new byte[] { 0x08, 0x01 });
        Assert(userLogin.Method == "user.UserLogin", "user login response method mismatch");
        Assert(TMessageCodec.DecodeRetUserLogin(userLogin.Ret!) == "ok", "user login response ret mismatch");
        Assert(observedPushes.Any(p => p.Method == "copy.GetCopy" && p.Ret is { Length: > 20 } &&
            p.Ret[^2] == 0x18 && p.Ret[^1] == 33),
            "user login did not synchronize MubarCopy (CopyType=33), leaving アンブラ進軍 empty");

        var userInfo = await RoundTrip("user.GetUserInfo", null);
        Assert(userInfo.Method == "user.GetUserInfo", "get user info response method mismatch");
        Assert(userInfo.Ret is { Length: > 0 }, "get user info response was empty");

        byte[] mubarStartArgs = new ProtocolPackage()
            .Write(0x10, 932113UL) // CopyId(2)
            .Write(0x48, 1UL)      // BattleMode(9)=Normal
            .ToArray();
        var mubarStart = await RoundTrip("copy.StartBase", mubarStartArgs);
        Assert(mubarStart.Method == "copy.StartBase", "Mubar StartBase response method mismatch");
        Assert(mubarStart.Ret is { Length: > 0 } &&
               ProtocolDecoder.DecodeVarintField(mubarStart.Ret, 7) == 33,
            "Mubar StartBase response did not preserve CopyType 33");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static async Task TreasureIntegrationTest()
{
    var root = FindRepositoryRoot();
    var serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0", "BlueOath.Server.dll");
    var data = Path.Combine(root, "test-treasure-tmp");
    Directory.CreateDirectory(data);
    const string profileId = "treasure-player";
    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--port=0");
    startInfo.ArgumentList.Add("--game-login-port=0");
    startInfo.ArgumentList.Add("--region=jp");
    startInfo.ArgumentList.Add("--data=" + data);
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "treasure test server did not start");
        var readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
        var port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        var stream = client.GetStream();

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
        await RoundTrip("player.GetUserList", null);
        await RoundTrip("player.CreateUser",
            new byte[] { 0x0A, 0x04, (byte)'b', (byte)'o', (byte)'x', (byte)'1', 0x10, 0x01 });
        await RoundTrip("user.UserLogin", new byte[] { 0x08, 0x01 });

        // GM 商品 10001（shop 18）发放一个 10300「激稀有武器箱」。
        TResponse bought = await RoundTrip("shop.BuyGoods",
            new byte[] { 0x08, 0x12, 0x10, 0x91, 0x4E, 0x18, 0x01 });
        Assert(bought.Err == 0, "equipment treasure could not be granted");
        var repo = new SqliteGameRepository(data);
        PlayerAccount before = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("treasure profile was not persisted");
        int equipCountBefore = before.Equip?.Items.Count ?? 0;
        Assert(before.Bag?.Items.Any(x => x.TemplateId == 10300 && x.Num == 1) == true,
            "granted equipment treasure was missing from the bag");

        var openPushes = new List<TResponse>();
        TResponse opened = await RoundTrip("bag.GetNormalTreasureInfo",
            new byte[] { 0x08, 0xBC, 0x50, 0x10, 0x01 }, openPushes);
        Assert(opened.Err == 0 && opened.Ret is { Length: > 0 }, "equipment treasure response was empty");
        Assert(opened.Ret![0] == 0x0A && opened.Ret[^3..].SequenceEqual(new byte[] { 0x10, 0xBC, 0x50 }),
            "equipment treasure response did not contain reward and treasure id");
        TResponse bagPush = openPushes.Single(x => x.Method == "bag.UpdateBagData");
        Assert(bagPush.Ret is { Length: > 0 } &&
            ContainsSequence(bagPush.Ret, new byte[] { 0x08, 0xBC, 0x50, 0x10, 0x00 }),
            "consumed equipment treasure did not send a Num=0 bag deletion marker");

        PlayerAccount after = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("opened treasure profile was not persisted");
        Assert(after.Bag?.Items.All(x => x.TemplateId != 10300) != false,
            "opened equipment treasure was not consumed");
        Assert((after.Equip?.Items.Count ?? 0) == equipCountBefore + 1,
            "opening the treasure did not create exactly one equipment instance");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static async Task TacticIntegrationTest()
{
    var root = FindRepositoryRoot();
    var serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0", "BlueOath.Server.dll");
    var data = Path.Combine(Path.GetTempPath(), "blueoath-tactic-" + Guid.NewGuid().ToString("N"));
    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--port=0");
    startInfo.ArgumentList.Add("--game-login-port=0");
    startInfo.ArgumentList.Add("--region=jp");
    startInfo.ArgumentList.Add("--data=" + data);
    startInfo.ArgumentList.Add("--client-path=" + Path.Combine(root, "blueoath", "blueoath"));
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "tactic test server did not start");
        var readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
        var port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        var stream = client.GetStream();

        async Task<TResponse> RoundTrip(string method, byte[]? args)
        {
            var request = TMessageCodec.EncodeRequest(new TRequest(method, args, 1));
            await NetSocketFrameCodec.WriteAsync(stream, request, NetSocketFrameCodec.TypeData, timeout.Token);
            while (true)
            {
                var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                Assert(frame is not null, $"empty response for {method}");
                var response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                if (response.IsResponse == 1)
                    return response;
            }
        }

        await RoundTrip("player.Login", GameLoginCodec.Encode(new TArgLogin("tactic-player", 1, "open", "hash")));
        await RoundTrip("player.GetUserList", null);
        await RoundTrip("player.CreateUser",
            new byte[] { 0x0A, 0x04, (byte)'t', (byte)'a', (byte)'c', (byte)'1', 0x10, 0x01 });
        await RoundTrip("user.UserLogin", new byte[] { 0x08, 0x01 });

        // An explicitly encoded empty tacticName lets the Lua client distinguish the default
        // name from nil and replace it with the localized First..Fifth Fleet text.
        var defaults = await RoundTrip("tactic.GetHerosTactic", null);
        for (byte fleetId = 1; fleetId <= 5; fleetId++)
            Assert(defaults.Ret is { Length: > 0 } &&
                ContainsSequence(defaults.Ret, new byte[] { 0x0A, 0x00, 0x18, fleetId }),
                $"default fleet {fleetId} did not contain an explicit empty localized-name marker");

        // tactic.SetHerosTactic: tactics[0] { heroInfo=[1], modeId=1, strategyId=0, formationId=2, type=1 }
        var entry = new ProtocolPackage();
        entry.Write(0x10, 1UL); // heroInfo (2)
        entry.Write(0x18, 1UL); // modeId (3)
        entry.Write(0x20, 0UL); // strategyId (4)
        entry.Write(0x28, 2UL); // formationId (5)
        entry.Write(0x30, 1UL); // type (6)
        var pkg = new ProtocolPackage();
        pkg.Write(0x0A, entry.ToArray()); // tactics (1)

        var set = await RoundTrip("tactic.SetHerosTactic", pkg.ToArray());
        Assert(set.Method == "tactic.SetHerosTactic", "set tactic response method mismatch");
        Assert(set.Ret is { Length: > 0 }, "set tactic response was empty");

        var get = await RoundTrip("tactic.GetHerosTactic", null);
        Assert(get.Method == "tactic.GetHerosTactic", "get tactic response method mismatch");
        byte[] marker = [0x10, 0x01, 0x18, 0x01]; // heroInfo=1 + modeId=1
        Assert(get.Ret is { Length: > 0 } && ContainsSequence(get.Ret, marker),
            "tactic.GetHerosTactic did not include saved hero info");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static async Task BuildingAssignmentIntegrationTest()
{
    var root = FindRepositoryRoot();
    var serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0", "BlueOath.Server.dll");
    Assert(File.Exists(serverDll), "server assembly is missing; build the solution first");
    // 配置加载器从数据目录向上定位客户端配置，因此集成数据放在仓库根目录内。
    var data = Path.Combine(root, "test-building-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(data);
    const string profileId = "building-player";
    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--port=0");
    startInfo.ArgumentList.Add("--game-login-port=0");
    startInfo.ArgumentList.Add("--region=jp");
    startInfo.ArgumentList.Add("--data=" + data);
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "building test server did not start");
        var readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
        int port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        NetworkStream stream = client.GetStream();

        async Task<TResponse> RoundTrip(
            string method,
            byte[]? args,
            ICollection<TResponse>? prePushes = null)
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
        await RoundTrip("player.GetUserList", null);
        await RoundTrip("player.CreateUser",
            new byte[] { 0x0A, 0x04, (byte)'b', (byte)'a', (byte)'s', (byte)'e', 0x10, 0x01 });

        var assignedPushes = new List<TResponse>();
        var setHero = new ProtocolPackage().Write(0x08, 2UL).Write(0x10, 1UL);
        TResponse assigned = await RoundTrip("building.SetHero", setHero.ToArray(), assignedPushes);
        Assert(assigned.Err == 0, "building.SetHero returned an error");
        TResponse? push = assignedPushes.LastOrDefault(item => item.Method == "building.UpdateBuildingInfo");
        Assert(push is not null, "building.SetHero did not send a refresh push before its response");
        Assert(push!.IsResponse == 0 && push.Method == "building.UpdateBuildingInfo",
            "building.SetHero sent the wrong refresh push");
        Assert(push.Ret is { Length: > 0 } &&
            ContainsSequence(push.Ret, new byte[] { 0x08, 0x02, 0x10, 0x29, 0x18, 0x01, 0x20, 0x01 }),
            "dormitory refresh did not include the assigned hero");

        var initialRepo = new SqliteGameRepository(data);
        PlayerAccount beforeConstruction = await initialRepo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("building profile was not persisted before construction");
        int initialGold = beforeConstruction.Character.Gold;
        string initialBag = JsonSerializer.Serialize(beforeConstruction.Bag);
        Assert(BuildingConfigLoader.MaterialTemplateIds.All(templateId =>
                beforeConstruction.Bag?.Items.SingleOrDefault(item => item.TemplateId == templateId)?.Num >= 99_999),
            "new profile did not receive the building materials required by the client");

        var addPushes = new List<TResponse>();
        var add = new ProtocolPackage().Write(0x08, 11UL).Write(0x10, 2UL);
        TResponse added = await RoundTrip("building.AddBuilding", add.ToArray(), addPushes);
        Assert(added.Err == 0 && added.Ret?.SequenceEqual(new byte[] { 0x08, 0x03 }) == true,
            "building.AddBuilding did not return building id 3");
        TResponse? addPush = addPushes.LastOrDefault(item => item.Method == "building.UpdateBuildingInfo");
        Assert(addPush?.Ret is { Length: > 0 } &&
            ContainsSequence(addPush.Ret, new byte[] { 0x08, 0x03, 0x10, 0x0B, 0x18, 0x01 }),
            "building.AddBuilding did not pre-push the new electric building");

        var occupied = new ProtocolPackage().Write(0x08, 11UL).Write(0x10, 2UL);
        TResponse duplicate = await RoundTrip("building.AddBuilding", occupied.ToArray());
        Assert(duplicate.Err != 0, "building.AddBuilding accepted an occupied land");

        static byte[] BuildingId(int id) => new ProtocolPackage().Write(0x08, unchecked((ulong)id)).ToArray();

        var officeUpgradePushes = new List<TResponse>();
        TResponse officeUpgraded = await RoundTrip(
            "building.UpgradeBuilding", BuildingId(1), officeUpgradePushes);
        Assert(officeUpgraded.Err == 0 && officeUpgradePushes.Any(item =>
                item.Method == "building.UpdateBuildingInfo" && item.Ret is { Length: > 0 } &&
                ContainsSequence(item.Ret, new byte[] { 0x08, 0x01, 0x10, 0x03, 0x18, 0x03 })),
            "office upgrade to level 3 was not synchronized before the response");

        TResponse electricUpgraded = await RoundTrip("building.UpgradeBuilding", BuildingId(3));
        Assert(electricUpgraded.Err == 0, "electric building upgrade failed");
        TResponse electricDegraded = await RoundTrip("building.DegradeBuilding", BuildingId(3));
        Assert(electricDegraded.Err == 0, "electric building degradation failed");

        TResponse officeDegraded = await RoundTrip("building.DegradeBuilding", BuildingId(1));
        Assert(officeDegraded.Err == 0, "office degradation from level 3 to level 2 failed");
        TResponse invalidOfficeDegrade = await RoundTrip("building.DegradeBuilding", BuildingId(1));
        Assert(invalidOfficeDegrade.Err == 3409,
            "office degradation ignored an occupied land's office-level requirement");

        TResponse finished = await RoundTrip("building.FinishBuilding", BuildingId(3));
        Assert(finished.Err == 0, "building.FinishBuilding was not idempotent for an instant build");

        var repo = new SqliteGameRepository(data);
        PlayerAccount persisted = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("building profile was not persisted");
        Assert(persisted.Building?.Buildings.Single(x => x.Id == 2).HeroIds.SequenceEqual(new uint[] { 1 }) == true,
            "building assignment was not written to the profile database");
        Assert(persisted.Building?.Buildings.Single(x => x.Id == 1) is { Tid: 2, Level: 2 } &&
            persisted.Building.Buildings.Single(x => x.Id == 3) is { Tid: 11, Level: 1 } &&
            persisted.Building.Lands.Any(x => x.Index == 2 && x.BuildingId == 3),
            "building lifecycle result was not written to the profile database");
        Assert(persisted.Character.Gold == initialGold &&
            JsonSerializer.Serialize(persisted.Bag) == initialBag,
            "base construction unexpectedly consumed gold or items");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static async Task FashionUnlockIntegrationTest()
{
    var root = FindRepositoryRoot();
    string clientPath = Path.Combine(root, "blueoath", "blueoath");
    string configDir = ConfigDbLoader.BuildConfigDir(clientPath);
    FashionConfigLoader.Load(configDir);
    FashionShopCatalog catalog = FashionShopGoodsLoader.Load(configDir);
    Assert(catalog.Goods.Count(g => g.ShopId == 23) == 32 &&
           catalog.Goods.Count(g => g.ShopId == 29) == 51,
        "fashion catalog was not rebuilt from the 32 featured and 51 broken shelf entries");
    Assert(catalog.Goods.Single(g => g.ItemId == 4011014).ShopId == 29,
        "legacy Z1 broken fashion was not moved from its stale shop_id 23 to shelf shop 29");
    Assert(catalog.Goods.All(g => g.ItemId != 1062024),
        "archived Ranger broken fashion was reintroduced despite being absent from both shelves");

    var serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0", "BlueOath.Server.dll");
    Assert(File.Exists(serverDll), "server assembly is missing; build the solution first");
    // 游戏客户端配置目录由 --client-path 直接指定（不再依赖数据目录位置向上逐级查找）。
    var data = Path.Combine(root, "test-fashion-tmp");
    Directory.CreateDirectory(data);
    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--port=0");
    startInfo.ArgumentList.Add("--game-login-port=0");
    startInfo.ArgumentList.Add("--region=jp");
    startInfo.ArgumentList.Add("--data=" + data);
    startInfo.ArgumentList.Add("--client-path=" + clientPath);
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "fashion test server did not start");
        var readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
        var port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        var stream = client.GetStream();

        async Task<TResponse> RoundTrip(string method, byte[]? args)
        {
            var request = TMessageCodec.EncodeRequest(new TRequest(method, args, 1));
            await NetSocketFrameCodec.WriteAsync(stream, request, NetSocketFrameCodec.TypeData, timeout.Token);
            while (true)
            {
                var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                Assert(frame is not null, $"empty response for {method}");
                var response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                if (response.IsResponse == 1)
                    return response;
            }
        }

        await RoundTrip("player.Login", GameLoginCodec.Encode(new TArgLogin("fashion-player", 1, "open", "hash")));
        await RoundTrip("player.GetUserList", null);
        await RoundTrip("player.CreateUser",
            new byte[] { 0x0A, 0x04, (byte)'f', (byte)'a', (byte)'s', (byte)'h', 0x10, 0x01 });
        await RoundTrip("user.UserLogin", new byte[] { 0x08, 0x01 });
        var userInfo = await RoundTrip("user.GetUserInfo", null);
        Assert(userInfo.Method == "user.GetUserInfo", "get user info response method mismatch");

        // user.GetUserInfo 应答后，服务器会通过 PostPushes 初始化技能训练数据并推送
        // fashion.updateData。若缺少 study.GetStudyInfo，退役页会对 nil ArrProgress
        // 执行 ipairs，从而显示空白候选列表。
        byte[]? fashionRet = null;
        byte[]? studyRet = null;
        for (var attempts = 0; attempts < 24 && (fashionRet is null || studyRet is null); attempts++)
        {
            var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
            Assert(frame is not null, "missing expected login synchronization push");
            var push = TMessageCodec.DecodeResponse(frame!.Value.Payload);
            if (push.Method == "fashion.updateData")
                fashionRet = push.Ret;
            else if (push.Method == "study.GetStudyInfo")
                studyRet = push.Ret;
        }
        Assert(studyRet is { Length: > 0 } && ContainsSequence(studyRet, new byte[] { 0x08, 0x02 }),
            "login synchronization did not initialize an empty study progress list");
        Assert(fashionRet is { Length: > 100 },
            $"fashion.updateData push did not contain unlocked fashions (ret length: {fashionRet?.Length ?? 0})");

        var shopsResponse = await RoundTrip("shop.GetShopsInfo", null);
        Dictionary<int, List<int>> shopGoods = DecodeShopGoods(shopsResponse.Ret ?? []);
        Assert(shopGoods.GetValueOrDefault(23) is { Count: 32 },
            $"featured fashion shop 23 did not contain its 32 shelf fashions " +
            $"(actual: {shopGoods.GetValueOrDefault(23)?.Count ?? 0})");
        Assert(shopGoods.GetValueOrDefault(29) is { Count: 51 },
            $"broken fashion shop 29 did not contain its 51 shelf fashions " +
            $"(actual: {shopGoods.GetValueOrDefault(29)?.Count ?? 0})");
        Assert(!shopGoods[23].Intersect(shopGoods[29]).Any(),
            "featured and broken fashion shops contained overlapping shelf goods");
        Assert(shopGoods.GetValueOrDefault(1)?.Contains(500) != true,
            "legacy unassigned fashion good 500 remained visible in shop 1");

        int fashionGoodId = shopGoods[23][0];
        var buyResponse = await RoundTrip("shop.BuyGoods", EncodeBuyGoodsRequest(23, fashionGoodId));
        Assert(buyResponse.Err == 0,
            $"fashion good {fashionGoodId} was listed in shop 23 but purchase validation rejected it");

        int brokenFashionGoodId = shopGoods[29][0];
        var brokenBuyResponse = await RoundTrip(
            "shop.BuyGoods", EncodeBuyGoodsRequest(29, brokenFashionGoodId));
        Assert(brokenBuyResponse.Err == 0,
            $"broken fashion good {brokenFashionGoodId} was listed in shop 29 but purchase validation rejected it");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static Dictionary<int, List<int>> DecodeShopGoods(byte[] payload)
{
    var shops = new Dictionary<int, List<int>>();
    int offset = 0;
    while (offset < payload.Length)
    {
        ulong key = ReadTestVarint(payload, ref offset);
        int field = checked((int)(key >> 3));
        int wire = (int)(key & 7);
        if (field != 1 || wire != 2)
        {
            SkipTestField(payload, ref offset, wire);
            continue;
        }

        byte[] shopPayload = ReadTestBytes(payload, ref offset);
        int shopOffset = 0;
        int shopId = 0;
        var goods = new List<int>();
        while (shopOffset < shopPayload.Length)
        {
            ulong shopKey = ReadTestVarint(shopPayload, ref shopOffset);
            int shopField = checked((int)(shopKey >> 3));
            int shopWire = (int)(shopKey & 7);
            if (shopField == 1 && shopWire == 0)
            {
                shopId = checked((int)ReadTestVarint(shopPayload, ref shopOffset));
            }
            else if (shopField == 3 && shopWire == 2)
            {
                byte[] goodsPayload = ReadTestBytes(shopPayload, ref shopOffset);
                int goodsOffset = 0;
                while (goodsOffset < goodsPayload.Length)
                {
                    ulong goodsKey = ReadTestVarint(goodsPayload, ref goodsOffset);
                    int goodsField = checked((int)(goodsKey >> 3));
                    int goodsWire = (int)(goodsKey & 7);
                    if (goodsField == 1 && goodsWire == 0)
                        goods.Add(checked((int)ReadTestVarint(goodsPayload, ref goodsOffset)));
                    else
                        SkipTestField(goodsPayload, ref goodsOffset, goodsWire);
                }
            }
            else
            {
                SkipTestField(shopPayload, ref shopOffset, shopWire);
            }
        }
        if (shopId != 0) shops[shopId] = goods;
    }
    return shops;
}

static byte[] EncodeBuyGoodsRequest(int shopId, int goodId)
{
    var bytes = new List<byte>();
    AppendTestVarint(bytes, 1 << 3);
    AppendTestVarint(bytes, checked((ulong)shopId));
    AppendTestVarint(bytes, 2 << 3);
    AppendTestVarint(bytes, checked((ulong)goodId));
    AppendTestVarint(bytes, 3 << 3);
    AppendTestVarint(bytes, 1);
    return bytes.ToArray();
}

static void AppendTestVarint(List<byte> bytes, ulong value)
{
    while (value >= 0x80)
    {
        bytes.Add((byte)(value | 0x80));
        value >>= 7;
    }
    bytes.Add((byte)value);
}

static ulong ReadTestVarint(byte[] payload, ref int offset)
{
    ulong value = 0;
    for (int shift = 0; shift < 64; shift += 7)
    {
        if (offset >= payload.Length) throw new EndOfStreamException("truncated test protobuf varint");
        byte current = payload[offset++];
        value |= (ulong)(current & 0x7f) << shift;
        if ((current & 0x80) == 0) return value;
    }
    throw new InvalidDataException("test protobuf varint is too long");
}

static byte[] ReadTestBytes(byte[] payload, ref int offset)
{
    int length = checked((int)ReadTestVarint(payload, ref offset));
    if (length < 0 || offset + length > payload.Length)
        throw new EndOfStreamException("truncated test protobuf bytes");
    byte[] value = payload.AsSpan(offset, length).ToArray();
    offset += length;
    return value;
}

static void SkipTestField(byte[] payload, ref int offset, int wire)
{
    switch (wire)
    {
        case 0:
            ReadTestVarint(payload, ref offset);
            return;
        case 1:
            offset = checked(offset + 8);
            break;
        case 2:
            offset = checked(offset + checked((int)ReadTestVarint(payload, ref offset)));
            break;
        case 5:
            offset = checked(offset + 4);
            break;
        default:
            throw new InvalidDataException($"unsupported test protobuf wire type {wire}");
    }
    if (offset > payload.Length) throw new EndOfStreamException("truncated test protobuf field");
}

static bool ContainsSequence(byte[] haystack, byte[] needle)
{
    for (int i = 0; i + needle.Length <= haystack.Length; i++)
    {
        bool match = true;
        for (int j = 0; j < needle.Length; j++)
            if (haystack[i + j] != needle[j]) { match = false; break; }
        if (match) return true;
    }
    return false;
}

static async Task EquipEnhanceIntegrationTest()
{
    var root = FindRepositoryRoot();
    var serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0", "BlueOath.Server.dll");
    Assert(File.Exists(serverDll), "server assembly is missing; build the solution first");
    var data = Path.Combine(root, "test-equip-enhance-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(data);
    const string profileId = "equip-enhance-player";
    const uint normalEquipId = 77;
    const uint heroPageEquipId = 78;
    const uint boundEquipId = 79;
    const uint goldEquipId = 80;
    const uint legacyGoldEquipId = 81;
    const uint riseStarEquipId = 82;
    const uint riseStarConsumedEquipId = 83;
    const uint universalRiseStarEquipId = 84;
    const uint universalCoreEquipId = 85;
    const uint wrongQualityCoreEquipId = 86;
    const int urEquipTemplateId = 100106;
    const int goldEquipTemplateId = 100101;
    const int goldUniversalCoreTemplateId = 30581;
    const int purpleUniversalCoreTemplateId = 30582;

    var repo = new SqliteGameRepository(data);
    await repo.CreateAsync(profileId, profileId);
    PlayerAccount seeded = await repo.LoadAccountAsync(profileId)
        ?? throw new InvalidDataException("failed to seed equipment enhancement account");
    Hero hero = seeded.Dock.Heroes[0];
    uint[] slots = [normalEquipId, heroPageEquipId, boundEquipId, 0, 0, 0];
    seeded = seeded with
    {
        Character = seeded.Character with { Gold = 100_000, UrEquipCoin = 100 },
        Dock = seeded.Dock with
        {
            Heroes = [hero with { EquipSlots = slots }],
        },
        Equip = new PlayerEquip(
        [
            new EquipItem(normalEquipId, urEquipTemplateId, EnhanceLv: 1, HeroId: hero.HeroId),
            new EquipItem(heroPageEquipId, urEquipTemplateId, EnhanceLv: 1, HeroId: hero.HeroId),
            new EquipItem(boundEquipId, urEquipTemplateId, EnhanceLv: 35, HeroId: hero.HeroId),
            new EquipItem(goldEquipId, goldEquipTemplateId),
            // Older builds stored only the remainder instead of cumulative EnhanceExp.
            new EquipItem(legacyGoldEquipId, goldEquipTemplateId, EnhanceLv: 3, EnhanceExp: 0),
            new EquipItem(riseStarEquipId, goldEquipTemplateId, EnhanceLv: 15, Star: 2),
            new EquipItem(riseStarConsumedEquipId, goldEquipTemplateId),
            new EquipItem(universalRiseStarEquipId, goldEquipTemplateId, EnhanceLv: 15, Star: 2),
            new EquipItem(universalCoreEquipId, goldUniversalCoreTemplateId),
            new EquipItem(wrongQualityCoreEquipId, purpleUniversalCoreTemplateId),
        ], 2000),
        Bag = new PlayerBag(
        [
            new BagItem(60000, 30),
            new BagItem(10029, 200),
            new BagItem(10030, 200),
            new BagItem(60003, 15),
            new BagItem(10000, 53),
        ], 100),
    };
    await repo.SaveAccountAsync(seeded);

    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--port=0");
    startInfo.ArgumentList.Add("--game-login-port=0");
    startInfo.ArgumentList.Add("--region=jp");
    startInfo.ArgumentList.Add("--data=" + data);
    startInfo.ArgumentList.Add("--profile-id=" + profileId);
    startInfo.ArgumentList.Add("--client-path=" + Path.Combine(root, "blueoath", "blueoath"));
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "equipment enhancement test server did not start");
        var readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using var ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
        var port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        var stream = client.GetStream();

        async Task<(TResponse Response, List<TResponse> Pushes)> RoundTrip(string method, byte[]? requestArgs)
        {
            byte[] request = TMessageCodec.EncodeRequest(new TRequest(method, requestArgs, 1));
            await NetSocketFrameCodec.WriteAsync(stream, request, NetSocketFrameCodec.TypeData, timeout.Token);
            List<TResponse> pushes = [];
            while (true)
            {
                var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                Assert(frame is not null, $"empty response for {method}");
                TResponse response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                if (response.IsResponse == 1) return (response, pushes);
                pushes.Add(response);
            }
        }

        static byte[] EnhanceArgs(uint equipId, params (uint TemplateId, uint Num)[] items)
        {
            var args = new ProtocolPackage().Write(0x08, equipId);
            foreach (var item in items)
            {
                var body = new ProtocolPackage()
                    .Write(0x08, item.TemplateId)
                    .Write(0x10, item.Num);
                args.Write(0x12, body.ToArray());
            }
            return args.ToArray();
        }

        static byte[] RiseStarArgs(uint equipId, params uint[] consumeIds)
        {
            var args = new ProtocolPackage().Write(0x08, equipId);
            foreach (uint consumeId in consumeIds)
                args.Write(0x10, consumeId);
            return args.ToArray();
        }

        await RoundTrip("player.Login", GameLoginCodec.Encode(new TArgLogin(profileId, 1, "open", "hash")));
        await RoundTrip("hero.GetHeroInfo", null); // drain login synchronization pushes

        var (normalResponse, normalPushes) = await RoundTrip("equip.Enhance", EnhanceArgs(normalEquipId,
            (60000, 5), (10029, 100), (10030, 100)));
        Assert(normalResponse.Err == 0 && normalResponse.Ret is { Length: > 0 },
            $"equipped UR normal enhancement was rejected: {normalResponse.ErrMsg}");
        Assert(normalPushes.Any(p => p.Method == "bag.UpdateBagData") &&
            normalPushes.Any(p => p.Method == "equip.UpdateEquipBagData"),
            "equipped UR normal enhancement did not refresh bag and equipment data");
        PlayerAccount normallyEnhanced = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("normal enhancement account disappeared");
        Assert(normallyEnhanced.Equip!.Items.Single(e => e.EquipId == normalEquipId).EnhanceLv == 2,
            "equipped UR normal enhancement did not persist level 2");
        Assert(normallyEnhanced.Bag!.Items.Single(i => i.TemplateId == 60000).Num == 25 &&
            normallyEnhanced.Bag.Items.Single(i => i.TemplateId == 10029).Num == 100 &&
            normallyEnhanced.Bag.Items.Single(i => i.TemplateId == 10030).Num == 100,
            "equipped UR normal enhancement consumed the wrong material amount");

        var (heroPageResponse, heroPagePushes) =
            await RoundTrip("equip.EnhanceBind", EnhanceArgs(heroPageEquipId));
        Assert(heroPageResponse.Err == 0 && heroPageResponse.Ret is { Length: > 0 },
            "hero-page UR enhancement without ItemArr was rejected");
        Assert(heroPagePushes.Any(p => p.Method == "bag.UpdateBagData") &&
            heroPagePushes.Any(p => p.Method == "equip.UpdateEquipBagData"),
            "hero-page UR enhancement did not refresh bag and equipment data");
        PlayerAccount heroPageEnhanced = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("hero-page enhancement account disappeared");
        Assert(heroPageEnhanced.Equip!.Items.Single(e => e.EquipId == heroPageEquipId).EnhanceLv == 2,
            "hero-page UR enhancement did not persist level 2");
        Assert(heroPageEnhanced.Bag!.Items.Single(i => i.TemplateId == 60000).Num == 20 &&
               !heroPageEnhanced.Bag.Items.Any(i => i.TemplateId is 10029 or 10030),
            "hero-page UR enhancement did not consume its configured materials");

        for (int expectedLevel = 1; expectedLevel <= 4; expectedLevel++)
        {
            var (goldResponse, _) = await RoundTrip("equip.Enhance", EnhanceArgs(goldEquipId, (60000, 2)));
            Assert(goldResponse.Err == 0 && goldResponse.Ret is { Length: > 0 },
                $"gold equipment enhancement {expectedLevel} was rejected");
            PlayerAccount goldEnhanced = await repo.LoadAccountAsync(profileId)
                ?? throw new InvalidDataException("gold enhancement account disappeared");
            EquipItem gold = goldEnhanced.Equip!.Items.Single(e => e.EquipId == goldEquipId);
            Assert(gold.EnhanceLv == expectedLevel && gold.EnhanceExp == expectedLevel * 200,
                $"gold equipment enhancement {expectedLevel} did not persist cumulative experience");
        }

        var (legacyGoldResponse, _) =
            await RoundTrip("equip.Enhance", EnhanceArgs(legacyGoldEquipId, (60000, 2)));
        Assert(legacyGoldResponse.Err == 0 && legacyGoldResponse.Ret is { Length: > 0 },
            "legacy gold equipment enhancement was rejected after level 3");
        PlayerAccount legacyGoldEnhanced = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("legacy gold enhancement account disappeared");
        EquipItem legacyGold = legacyGoldEnhanced.Equip!.Items.Single(e => e.EquipId == legacyGoldEquipId);
        Assert(legacyGold.EnhanceLv == 4 && legacyGold.EnhanceExp == 800,
            "legacy remainder experience was not normalized to cumulative experience");

        var (boundResponse, boundPushes) = await RoundTrip("equip.EnhanceBind", EnhanceArgs(boundEquipId));
        Assert(boundResponse.Err == 0 && boundResponse.Ret is { Length: > 0 },
            "equipped UR bound enhancement was rejected");
        Assert(boundPushes.Any(p => p.Method == "user.UpdateUserInfo") &&
            boundPushes.Any(p => p.Method == "bag.UpdateBagData") &&
            boundPushes.Any(p => p.Method == "equip.UpdateEquipBagData"),
            "bound enhancement did not refresh currency, bag, and equipment data");
        PlayerAccount boundEnhanced = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("bound enhancement account disappeared");
        Assert(boundEnhanced.Equip!.Items.Single(e => e.EquipId == boundEquipId).EnhanceLv == 36,
            "equipped UR bound enhancement did not persist level 36");
        Assert(boundEnhanced.Character.UrEquipCoin == 90 && boundEnhanced.Character.Gold == 65_000,
            "equipped UR bound enhancement did not consume configured currencies");
        Assert(!boundEnhanced.Bag!.Items.Any(i => i.TemplateId == 60003) &&
               boundEnhanced.Bag.Items.Single(i => i.TemplateId == 10000).Num == 41,
            "equipped UR bound enhancement did not consume configured bag materials");

        var (riseStarResponse, riseStarPushes) = await RoundTrip(
            "equip.RiseStar", RiseStarArgs(riseStarEquipId, riseStarConsumedEquipId));
        Assert(riseStarResponse.Err == 0 && riseStarResponse.Ret is { Length: > 0 },
            "gold equipment rise-star was rejected");
        Assert(riseStarPushes.Any(p => p.Method == "user.UpdateUserInfo") &&
               riseStarPushes.Any(p => p.Method == "bag.UpdateBagData"),
            "rise-star did not refresh currency and bag data before its response");
        TResponse riseStarEquipPush = riseStarPushes.Single(p => p.Method == "equip.UpdateEquipBagData");
        byte[] consumedDeletion = PlayerDataCodec.Encode(
            new EquipInfo(riseStarConsumedEquipId, TemplateId: 0));
        Assert(riseStarEquipPush.Ret is { Length: > 0 } &&
               ContainsSequence(riseStarEquipPush.Ret, consumedDeletion),
            "rise-star equipment push omitted the consumed equipment deletion marker");

        PlayerAccount risenStar = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("rise-star account disappeared");
        Assert(risenStar.Equip!.Items.Single(e => e.EquipId == riseStarEquipId).Star == 3 &&
               risenStar.Equip.Items.All(e => e.EquipId != riseStarConsumedEquipId),
            "rise-star did not persist the target update and consumed equipment removal");
        Assert(risenStar.Character.Gold == 45_000 &&
               risenStar.Bag!.Items.Single(i => i.TemplateId == 10000).Num == 21,
            "rise-star did not consume its configured currency and item costs");

        var (wrongCoreResponse, wrongCorePushes) = await RoundTrip(
            "equip.RiseStar", RiseStarArgs(universalRiseStarEquipId, wrongQualityCoreEquipId));
        Assert(wrongCoreResponse.Err != 0 && wrongCorePushes.Count == 0,
            "rise-star accepted a universal core with the wrong quality");
        PlayerAccount wrongCoreRejected = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("wrong-quality core rejection account disappeared");
        Assert(wrongCoreRejected.Equip!.Items.Single(e => e.EquipId == universalRiseStarEquipId).Star == 2 &&
               wrongCoreRejected.Equip.Items.Any(e => e.EquipId == wrongQualityCoreEquipId) &&
               wrongCoreRejected.Character.Gold == 45_000 &&
               wrongCoreRejected.Bag!.Items.Single(i => i.TemplateId == 10000).Num == 21,
            "wrong-quality universal core rejection mutated the account");

        var (universalResponse, universalPushes) = await RoundTrip(
            "equip.RiseStar", RiseStarArgs(universalRiseStarEquipId, universalCoreEquipId));
        Assert(universalResponse.Err == 0 && universalResponse.Ret is { Length: > 0 },
            "gold equipment rise-star rejected its same-quality universal core");
        byte[] universalDeletion = PlayerDataCodec.Encode(
            new EquipInfo(universalCoreEquipId, TemplateId: 0));
        TResponse universalEquipPush = universalPushes.Single(p => p.Method == "equip.UpdateEquipBagData");
        Assert(universalEquipPush.Ret is { Length: > 0 } &&
               ContainsSequence(universalEquipPush.Ret, universalDeletion),
            "rise-star equipment push omitted the consumed universal core deletion marker");
        PlayerAccount universalRisenStar = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("universal rise-star account disappeared");
        Assert(universalRisenStar.Equip!.Items.Single(e => e.EquipId == universalRiseStarEquipId).Star == 3 &&
               universalRisenStar.Equip.Items.All(e => e.EquipId != universalCoreEquipId) &&
               universalRisenStar.Equip.Items.Any(e => e.EquipId == wrongQualityCoreEquipId),
            "rise-star did not consume only the selected same-quality universal core");
        Assert(universalRisenStar.Character.Gold == 25_000 &&
               universalRisenStar.Bag!.Items.Single(i => i.TemplateId == 10000).Num == 1,
            "universal-core rise-star did not consume its configured currency and item costs");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static async Task ConstructionIntegrationTest()
{
    string root = FindRepositoryRoot();
    string serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0",
        "BlueOath.Server.dll");
    Assert(File.Exists(serverDll), "server assembly is missing; build the server first");
    string data = Path.Combine(root, ".test-data", "blueoath-construction-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(data);
    const string profileId = "construction-test";
    var repo = new SqliteGameRepository(data);
    await repo.CreateAsync(profileId, profileId);

    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--port=0");
    startInfo.ArgumentList.Add("--game-login-port=0");
    startInfo.ArgumentList.Add("--region=jp");
    startInfo.ArgumentList.Add("--data=" + data);
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "construction test server did not start");
        string readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))
            ?? throw new InvalidDataException("server did not report ready");
        using var ready = JsonDocument.Parse(readyLine);
        int port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        NetworkStream stream = client.GetStream();

        async Task<(TResponse Response, List<TResponse> Pushes)> RoundTrip(string method, byte[]? requestArgs)
        {
            byte[] request = TMessageCodec.EncodeRequest(new TRequest(method, requestArgs, 1));
            await NetSocketFrameCodec.WriteAsync(stream, request, NetSocketFrameCodec.TypeData, timeout.Token);
            List<TResponse> pushes = [];
            while (true)
            {
                var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                Assert(frame is not null, $"empty response for {method}");
                TResponse response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                if (response.IsResponse == 1) return (response, pushes);
                pushes.Add(response);
            }
        }

        static byte[] Project(int gold, int steelCount, int aluminiumCount)
        {
            var steel = new ProtocolPackage().Write(0x08, 10029UL)
                .Write(0x10, checked((ulong)steelCount));
            var aluminium = new ProtocolPackage().Write(0x08, 10030UL)
                .Write(0x10, checked((ulong)aluminiumCount));
            return new ProtocolPackage()
                .Write(0x0A, steel.ToArray())
                .Write(0x0A, aluminium.ToArray())
                .Write(0x10, checked((ulong)gold))
                .ToArray();
        }

        await RoundTrip("player.Login", GameLoginCodec.Encode(new TArgLogin(profileId, 1, "open", "hash")));
        await RoundTrip("hero.GetHeroInfo", null); // drain login synchronization pushes
        PlayerAccount baseline = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("construction account was not initialized");
        int steelBefore = baseline.Bag?.Items.Single(item => item.TemplateId == 10029).Num ?? 0;
        int aluminiumBefore = baseline.Bag?.Items.Single(item => item.TemplateId == 10030).Num ?? 0;
        int quickBefore = baseline.Bag?.Items.Single(item => item.TemplateId == 10031).Num ?? 0;

        var invalidArgs = new ProtocolPackage().Write(0x0A, Project(29, 30, 30));
        var (invalid, invalidPushes) = await RoundTrip("build.BuildingByFormula", invalidArgs.ToArray());
        Assert(invalid.Err != 0 && invalidPushes.Count == 0,
            "an out-of-range construction project was not rejected atomically");

        var projects = new ProtocolPackage();
        for (int i = 0; i < 3; i++) projects.Write(0x0A, Project(30, 30, 30));
        var (started, startPushes) = await RoundTrip("build.BuildingByFormula", projects.ToArray());
        Assert(started.Err == 0, $"valid construction request was rejected: {started.ErrMsg}");
        Assert(startPushes.Any(push => push.Method == "build.BuildsInfo") &&
            startPushes.Any(push => push.Method == "bag.UpdateBagData") &&
            startPushes.Any(push => push.Method == "user.UpdateUserInfo"),
            "construction start did not synchronize queue, materials, and currency");

        PlayerAccount queued = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("construction queue was not persisted");
        IReadOnlyList<ConstructionJob> queuedJobs = queued.Construction?.Jobs ?? [];
        Assert(queuedJobs.Count == 3 && queuedJobs.Count(job => job.EndTime > 0) == 2 &&
            queuedJobs.Count(job => job.EndTime == 0) == 1,
            "construction did not use two active slots and one waiting slot");
        Assert(queued.Character.Gold == baseline.Character.Gold - 90 &&
            queued.Bag!.Items.Single(item => item.TemplateId == 10029).Num == steelBefore - 90 &&
            queued.Bag.Items.Single(item => item.TemplateId == 10030).Num == aluminiumBefore - 90,
            "construction resources were not deducted exactly once");
        Assert(queued.Construction?.LastProject?.Gold == 30,
            "last construction formula was not persisted for client reuse");

        byte[] firstIndex = new ProtocolPackage().Write(0x08, 1UL).ToArray();
        var (finished, finishPushes) = await RoundTrip("build.BuildQuicklyFinish", firstIndex);
        Assert(finished.Err == 0 && finishPushes.Any(push => push.Method == "build.BuildsInfo") &&
            finishPushes.Any(push => push.Method == "bag.UpdateBagData"),
            "quick construction did not synchronize its queue and item cost");
        PlayerAccount quickFinished = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("quick-finished construction was not persisted");
        Assert(quickFinished.Construction?.Jobs.Count(job => job.Completed) == 1 &&
            quickFinished.Construction.Jobs.Count(job => !job.Completed && job.EndTime > 0) == 2 &&
            quickFinished.Construction.Jobs.All(job => job.Completed || job.EndTime > 0),
            "quick construction did not promote the waiting job into the free slot");
        Assert(quickFinished.Bag!.Items.Single(item => item.TemplateId == 10031).Num == quickBefore - 1,
            "quick construction item was not consumed");

        var (received, receivePushes) = await RoundTrip("build.BuildReceive", firstIndex);
        Assert(received.Err == 0 && received.Ret is { Length: > 0 } && received.Ret[0] == 0x0A,
            "construction receive did not return a ship reward");
        Assert(receivePushes.Any(push => push.Method == "build.BuildsInfo") &&
            receivePushes.Any(push => push.Method == "hero.UpdateHeroBagData") &&
            receivePushes.Any(push => push.Method == "illustrate.IllustrateInfo"),
            "construction receive did not synchronize queue, dock, and illustration data");
        PlayerAccount receivedAccount = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("received construction was not persisted");
        Assert(receivedAccount.Construction?.Jobs.Count == 2 &&
            receivedAccount.Dock.Heroes.Count == baseline.Dock.Heroes.Count + 1,
            "receiving a construction result did not remove one job and add one ship");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static async Task HeroRemouldIntegrationTest()
{
    string root = FindRepositoryRoot();
    string serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0",
        "BlueOath.Server.dll");
    Assert(File.Exists(serverDll), "server assembly is missing; build the server first");

    RemouldConfigLoader.Load(FindClientConfigDir());
    ConfigShipRemouldTemplate stage = RemouldConfigLoader.GetTemplate(525)
        ?? throw new InvalidDataException("Oakland remould stage was not loaded");
    ConfigShipRemouldTemplate nextStage = RemouldConfigLoader.GetTemplate(526)
        ?? throw new InvalidDataException("Oakland second remould stage was not loaded");
    List<int> stageEffectIds = (stage.RemouldItemGroup ?? []).Select(id => checked((int)id)).ToList();
    List<int> nextStageEffectIds = (nextStage.RemouldItemGroup ?? [])
        .Select(id => checked((int)id)).ToList();
    List<int> allEffectIds = [.. stageEffectIds, .. nextStageEffectIds];
    int effectId = stageEffectIds
        .First(id => RemouldConfigLoader.GetEffect(checked((int)id))?.RemouldPrev is not { Count: > 0 });
    ConfigShipRemouldEffect effect = RemouldConfigLoader.GetEffect(effectId)
        ?? throw new InvalidDataException("initial Oakland remould effect was not loaded");
    Dictionary<(int Type, int Id), int> selectedCosts = (effect.Cost ?? [])
        .GroupBy(cost => (Type: checked((int)cost[0]), Id: checked((int)cost[1])))
        .ToDictionary(group => group.Key, group => checked((int)group.Sum(cost => cost[2])));
    Dictionary<(int Type, int Id), int> stageCosts = allEffectIds
        .SelectMany(id => RemouldConfigLoader.GetEffect(id)?.Cost ?? [])
        .GroupBy(cost => (Type: checked((int)cost[0]), Id: checked((int)cost[1])))
        .ToDictionary(group => group.Key, group => checked((int)group.Sum(cost => cost[2])));

    string data = Path.Combine(root, ".test-data", "blueoath-remould-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(data);
    const string profileId = "remould-test";
    var repo = new SqliteGameRepository(data);
    await repo.CreateAsync(profileId, profileId);
    PlayerAccount seeded = await repo.LoadAccountAsync(profileId)
        ?? throw new InvalidDataException("failed to seed remould account");
    Hero hero = seeded.Dock.Heroes.Single() with
    {
        Level = Math.Max(100, checked((int)effect.LimitLevel)),
        Advance = Math.Max(10, checked((int)effect.LimitStar)),
    };
    seeded = seeded with { Dock = seeded.Dock with { Heroes = [hero] } };
    foreach (var (key, amount) in stageCosts)
    {
        if (key.Type == GameServices.GoodsTypeCurrency)
        {
            Assert(GameServices.TryGetCurrency(seeded, key.Id, out int current),
                $"unsupported remould currency {key.Id}");
            if (current < amount + 10)
                seeded = GameServices.AddCurrency(seeded, key.Id, amount + 10 - current);
        }
        else
        {
            int current = seeded.Bag?.Items.FirstOrDefault(i => i.TemplateId == key.Id)?.Num ?? 0;
            if (current < amount + 10)
                seeded = GameServices.AddBagItem(seeded, key.Id, amount + 10 - current);
        }
    }
    await repo.SaveAccountAsync(seeded);

    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--port=0");
    startInfo.ArgumentList.Add("--game-login-port=0");
    startInfo.ArgumentList.Add("--region=jp");
    startInfo.ArgumentList.Add("--data=" + data);
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "remould test server did not start");
        string readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))
            ?? throw new InvalidDataException("server did not report ready");
        using var ready = JsonDocument.Parse(readyLine);
        int port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        NetworkStream stream = client.GetStream();

        async Task<(TResponse Response, List<TResponse> Pushes)> RoundTrip(string method, byte[]? requestArgs)
        {
            byte[] request = TMessageCodec.EncodeRequest(new TRequest(method, requestArgs, 1));
            await NetSocketFrameCodec.WriteAsync(stream, request, NetSocketFrameCodec.TypeData, timeout.Token);
            List<TResponse> pushes = [];
            while (true)
            {
                var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                Assert(frame is not null, $"empty response for {method}");
                TResponse response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                if (response.IsResponse == 1) return (response, pushes);
                pushes.Add(response);
            }
        }

        await RoundTrip("player.Login", GameLoginCodec.Encode(new TArgLogin(profileId, 1, "open", "hash")));
        await RoundTrip("hero.GetHeroInfo", null); // drain login synchronization pushes

        byte[] EncodeRemouldArg(int id)
        {
            var value = new ProtocolPackage();
            value.Write(0x08, 1UL);
            value.Write(0x10, checked((ulong)id));
            return value.ToArray();
        }

        byte[] remouldArg = EncodeRemouldArg(effectId);
        var (response, pushes) = await RoundTrip("hero.HeroRemould", remouldArg);
        Assert(response.Err == 0, $"valid remould request was rejected: {response.ErrMsg}");
        Assert(pushes.Any(push => push.Method == "hero.UpdateHeroBagData"),
            "remould did not refresh hero data before its response");
        Assert(pushes.Any(push => push.Method == "bag.UpdateBagData"),
            "remould did not refresh item costs before its response");
        Assert(pushes.Any(push => push.Method == "user.UpdateUserInfo"),
            "remould did not refresh currency costs before its response");

        PlayerAccount saved = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("remould account disappeared");
        Hero savedHero = saved.Dock.Heroes.Single();
        Assert(savedHero.RemouldEffects?.Contains(effectId) == true,
            "completed remould effect was not persisted");
        Assert(savedHero.RemouldLevel == 0,
            "a partially completed first stage advanced RemouldLevel");
        foreach (var (key, amount) in selectedCosts)
        {
            if (key.Type == GameServices.GoodsTypeCurrency)
            {
                Assert(GameServices.TryGetCurrency(seeded, key.Id, out int before) &&
                    GameServices.TryGetCurrency(saved, key.Id, out int after) && after == before - amount,
                    $"remould currency {key.Id} was not deducted exactly once");
            }
            else
            {
                int before = seeded.Bag?.Items.FirstOrDefault(i => i.TemplateId == key.Id)?.Num ?? 0;
                int after = saved.Bag?.Items.FirstOrDefault(i => i.TemplateId == key.Id)?.Num ?? 0;
                Assert(after == before - amount, $"remould item {key.Id} was not deducted exactly once");
            }
        }

        var (duplicate, duplicatePushes) = await RoundTrip("hero.HeroRemould", remouldArg);
        Assert(duplicate.Err != 0 && duplicatePushes.Count == 0,
            "duplicate remould request was not rejected atomically");
        PlayerAccount unchanged = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("remould account disappeared after duplicate request");
        Assert(unchanged.Dock.Heroes.Single().RemouldEffects?.Count(id => id == effectId) == 1,
            "duplicate remould request changed persisted state");

        int nextEffectId = checked((int)(nextStage.RemouldItemGroup ?? [])
            .First(id => RemouldConfigLoader.GetEffect(checked((int)id))?.RemouldPrev is not { Count: > 0 }));
        var (earlyStage, earlyPushes) = await RoundTrip("hero.HeroRemould", EncodeRemouldArg(nextEffectId));
        Assert(earlyStage.Err != 0 && earlyPushes.Count == 0,
            "a second-stage remould effect was accepted before the first stage completed");

        var completed = new HashSet<int> { effectId };
        while (completed.Count < stageEffectIds.Count)
        {
            int candidate = stageEffectIds.First(id => !completed.Contains(id) &&
                (RemouldConfigLoader.GetEffect(id)?.RemouldPrev is not { Count: > 0 } prerequisites ||
                 prerequisites.Any(prev => completed.Contains(checked((int)prev)))));
            var (nodeResponse, _) = await RoundTrip("hero.HeroRemould", EncodeRemouldArg(candidate));
            Assert(nodeResponse.Err == 0,
                $"valid first-stage remould effect {candidate} was rejected: {nodeResponse.ErrMsg}");
            completed.Add(candidate);
        }

        PlayerAccount stageCompleted = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("remould account disappeared after stage completion");
        Hero completedHero = stageCompleted.Dock.Heroes.Single();
        Assert(completedHero.RemouldLevel == 1 &&
            stageEffectIds.All(id => completedHero.RemouldEffects?.Contains(id) == true),
            "completing every first-stage node did not advance RemouldLevel to 1");
        while (completed.Count < allEffectIds.Count)
        {
            int candidate = nextStageEffectIds.First(id => !completed.Contains(id) &&
                (RemouldConfigLoader.GetEffect(id)?.RemouldPrev is not { Count: > 0 } prerequisites ||
                 prerequisites.Any(prev => completed.Contains(checked((int)prev)))));
            var (nodeResponse, _) = await RoundTrip("hero.HeroRemould", EncodeRemouldArg(candidate));
            Assert(nodeResponse.Err == 0,
                $"valid second-stage remould effect {candidate} was rejected: {nodeResponse.ErrMsg}");
            completed.Add(candidate);
        }

        PlayerAccount fullyRemoulded = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("remould account disappeared after completion");
        Hero fullyRemouldedHero = fullyRemoulded.Dock.Heroes.Single();
        Assert(fullyRemouldedHero.RemouldLevel == 3 &&
            allEffectIds.All(id => fullyRemouldedHero.RemouldEffects?.Contains(id) == true),
            "completing both node stages did not include the terminal empty stage in RemouldLevel");
        foreach (List<long> skillEffect in allEffectIds
                     .SelectMany(id => RemouldConfigLoader.GetEffect(id)?.RemouldEffectType ?? [])
                     .Where(value => value.Count >= 2 && value[0] is 4 or 5))
        {
            uint oldSkillId = checked((uint)skillEffect[1]);
            PSkillEntry? skill = fullyRemouldedHero.PSkills?.FirstOrDefault(value => value.PSkillId == oldSkillId);
            Assert(skill is not null, $"remould skill {oldSkillId} was not added to PSkill");
            if (skillEffect[0] == 5)
                Assert(skill!.Replace == checked((int)skillEffect[2]),
                    $"remould skill {oldSkillId} was not replaced");
        }
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static async Task HeroMutationIntegrationTest()
{
    var root = FindRepositoryRoot();
    var serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0", "BlueOath.Server.dll");
    Assert(File.Exists(serverDll), "server assembly is missing; build the solution first");
    var data = Path.Combine(root, "test-retire-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(data);
    const string profileId = "retire-player";

    // Seed a second ship with one equipped item. The normal client flow asks whether that item
    // should be dismantled after retirement, so retirement must leave it in the bag and unbind it.
    var repo = new SqliteGameRepository(data);
    await repo.CreateAsync(profileId, profileId);
    PlayerAccount seeded = await repo.LoadAccountAsync(profileId)
        ?? throw new InvalidDataException("failed to seed retirement account");
    Assert(seeded.Dock.Heroes[0].Affection == PlayerAccountFactory.DefaultAffection,
        "new profiles did not initialize affection at 50");
    Hero second = seeded.Dock.Heroes[0] with
    {
        HeroId = 2,
        TemplateId = 40320111,
        Fashioning = 4032011,
        Affection = 990_000,
        EquipSlots = new uint[] { 77, 0, 0, 0, 0, 0 },
    };
    Hero third = seeded.Dock.Heroes[0] with
    {
        HeroId = 3,
        Affection = 1_000_000,
    };
    Hero legacyLowAffection = seeded.Dock.Heroes[0] with
    {
        HeroId = 4,
        Affection = 10_000,
    };
    seeded = seeded with
    {
        Dock = seeded.Dock with { Heroes = [seeded.Dock.Heroes[0], second, third, legacyLowAffection] },
        Equip = new PlayerEquip([new EquipItem(77, 30421, HeroId: 2)], 2000),
        // Existing profiles may predate affection gifts and therefore have an empty bag.
        Bag = new PlayerBag([], 100),
    };
    await repo.SaveAccountAsync(seeded);

    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--port=0");
    startInfo.ArgumentList.Add("--game-login-port=0");
    startInfo.ArgumentList.Add("--region=jp");
    startInfo.ArgumentList.Add("--data=" + data);
    startInfo.ArgumentList.Add("--client-path=" + Path.Combine(root, "blueoath", "blueoath"));
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "retirement test server did not start");
        var readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using var ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
        var port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        var stream = client.GetStream();

        async Task<(TResponse Response, List<TResponse> Pushes)> RoundTrip(string method, byte[]? requestArgs)
        {
            byte[] request = TMessageCodec.EncodeRequest(new TRequest(method, requestArgs, 1));
            await NetSocketFrameCodec.WriteAsync(stream, request, NetSocketFrameCodec.TypeData, timeout.Token);
            List<TResponse> pushes = [];
            while (true)
            {
                var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                Assert(frame is not null, $"empty response for {method}");
                TResponse response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                if (response.IsResponse == 1) return (response, pushes);
                pushes.Add(response);
            }
        }

        await RoundTrip("player.Login", GameLoginCodec.Encode(new TArgLogin(profileId, 1, "open", "hash")));
        // Login synchronization is emitted immediately after the login response, so the next
        // request observes those queued pushes before its own response.
        var (initialHeroResponse, _) = await RoundTrip("hero.GetHeroInfo", null);
        Assert(initialHeroResponse.Ret is { Length: > 0 } &&
            ContainsSequence(initialHeroResponse.Ret, new byte[] { 0x80, 0x01, 0x00 }),
            "hero data did not explicitly initialize ChangeNameTime=0");
        Assert(ContainsSequence(initialHeroResponse.Ret!, new byte[] { 0x98, 0x01, 0x00 }) &&
            ContainsSequence(initialHeroResponse.Ret!, new byte[] { 0xA8, 0x01, 0x00 }),
            "unmarried hero data did not explicitly initialize MarryTime/MarryType=0");
        PlayerAccount migrated = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("migrated account disappeared");
        Assert(migrated.Dock.Heroes.Single(h => h.HeroId == 4).Affection ==
                PlayerAccountFactory.DefaultAffection,
            "legacy affection migration was not persisted during account load");

        int initialAffection = second.Affection;
        var giftArgs = new ProtocolPackage();
        giftArgs.Write(0x08, 2UL);
        giftArgs.Write(0x10, 280002UL);
        giftArgs.Write(0x18, 2UL);
        var (giftResponse, giftPushes) = await RoundTrip("hero.AddAffection", giftArgs.ToArray());
        Assert(giftResponse.Err == 0 && giftResponse.Ret is { Length: > 0 } &&
            ContainsSequence(giftResponse.Ret, new byte[] { 0x10, 0x02 }),
            "gift response did not identify HeroId=2");
        Assert(giftPushes.Any(p => p.Method == "hero.UpdateHeroBagData"),
            "gift did not refresh hero data before its response");
        Assert(giftPushes.Any(p => p.Method == "bag.UpdateBagData"),
            "gift did not refresh bag data before its response");
        PlayerAccount gifted = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("gift account disappeared");
        int giftedAffection = gifted.Dock.Heroes.Single(h => h.HeroId == 2).Affection;
        Assert(giftedAffection == PlayerAccountFactory.UnmarriedMaxAffection &&
            giftedAffection > initialAffection,
            "gift did not cap persisted unmarried affection at 100");
        Assert(gifted.Bag?.Items.Single(i => i.TemplateId == 280002).Num == 998,
            "gift count was not limited to the quantity actually needed");
        Assert(gifted.Bag?.Items.Count(i => i.Num == 999) == 7,
            "not all configured affection gift types were provisioned for the existing profile");

        var (cappedGiftResponse, cappedGiftPushes) =
            await RoundTrip("hero.AddAffection", giftArgs.ToArray());
        Assert(cappedGiftResponse.Err != 0 && cappedGiftPushes.Count == 0,
            "gift at the unmarried affection limit was accepted");
        PlayerAccount cappedGiftRejected = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("capped gift account disappeared");
        Assert(cappedGiftRejected.Dock.Heroes.Single(h => h.HeroId == 2).Affection ==
                PlayerAccountFactory.UnmarriedMaxAffection &&
            cappedGiftRejected.Bag?.Items.Single(i => i.TemplateId == 280002).Num == 998,
            "gift at the affection limit changed affection or inventory");

        // A failed request must not grant affection or consume the final gift.
        var excessiveGiftArgs = new ProtocolPackage();
        excessiveGiftArgs.Write(0x08, 2UL);
        excessiveGiftArgs.Write(0x10, 280002UL);
        excessiveGiftArgs.Write(0x18, 1000UL);
        var (excessiveGiftResponse, excessiveGiftPushes) =
            await RoundTrip("hero.AddAffection", excessiveGiftArgs.ToArray());
        Assert(excessiveGiftResponse.Err != 0, "insufficient gift inventory was accepted");
        Assert(excessiveGiftPushes.Count == 0, "failed gift request unexpectedly pushed changed data");
        PlayerAccount giftRejected = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("gift rejection account disappeared");
        Assert(giftRejected.Dock.Heroes.Single(h => h.HeroId == 2).Affection == giftedAffection &&
            giftRejected.Bag?.Items.Single(i => i.TemplateId == 280002).Num == 998,
            "failed gift request changed persisted data");

        const string customName = "马克西姆";
        var renameArgs = new ProtocolPackage();
        renameArgs.Write(0x08, 2UL);
        renameArgs.Write(0x12, customName);
        var (renameResponse, renamePushes) = await RoundTrip("hero.ChangeName", renameArgs.ToArray());
        Assert(renameResponse.Err == 0, "Unicode hero rename was rejected");
        TResponse renameHeroPush = renamePushes.FirstOrDefault(p => p.Method == "hero.UpdateHeroBagData")
            ?? throw new InvalidDataException("rename did not refresh hero data before its response");
        Assert(renameHeroPush.Ret is { Length: > 0 } &&
            ContainsSequence(renameHeroPush.Ret, Encoding.UTF8.GetBytes(customName)),
            "rename update did not contain the UTF-8 custom name");
        PlayerAccount renamed = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("rename account disappeared");
        Hero renamedHero = renamed.Dock.Heroes.Single(h => h.HeroId == 2);
        Assert(renamedHero.Name == customName && renamedHero.ChangeNameTime > 0,
            "Unicode custom name or rename time was not persisted");

        var resetNameArgs = new ProtocolPackage();
        resetNameArgs.Write(0x08, 2UL);
        resetNameArgs.Write(0x12, "");
        var (resetNameResponse, resetNamePushes) =
            await RoundTrip("hero.ChangeName", resetNameArgs.ToArray());
        Assert(resetNameResponse.Err == 0, "resetting a custom hero name was rejected");
        TResponse resetNamePush = resetNamePushes.FirstOrDefault(p => p.Method == "hero.UpdateHeroBagData")
            ?? throw new InvalidDataException("name reset did not refresh hero data before its response");
        Assert(resetNamePush.Ret is { Length: > 0 } &&
            ContainsSequence(resetNamePush.Ret, new byte[] { 0x7A, 0x00 }),
            "name reset did not explicitly clear the cached custom name");
        PlayerAccount resetNameAccount = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("name reset account disappeared");
        Assert(resetNameAccount.Dock.Heroes.Single(h => h.HeroId == 2).Name == "",
            "custom name reset was not persisted");

        var lockArgs = new ProtocolPackage();
        lockArgs.Write(0x08, 2UL);
        lockArgs.Write(0x10, 1UL);
        var (lockResponse, lockPushes) = await RoundTrip("hero.LockHero", lockArgs.ToArray());
        Assert(lockResponse.Err == 0 && lockResponse.Ret is not null &&
            ContainsSequence(lockResponse.Ret, new byte[] { 0x08, 0x02 }),
            "lock response did not identify HeroId=2");
        TResponse lockHeroPush = lockPushes.FirstOrDefault(p => p.Method == "hero.UpdateHeroBagData")
            ?? throw new InvalidDataException("lock did not refresh hero data before its response");
        Assert(lockHeroPush.Ret is { Length: > 0 } &&
            ContainsSequence(lockHeroPush.Ret, new byte[] { 0x60, 0x01 }),
            "lock update did not set Lock=true");
        PlayerAccount locked = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("lock account disappeared");
        Assert(locked.Dock.Heroes.Single(h => h.HeroId == 2).Lock, "lock state was not persisted");

        var unlockArgs = new ProtocolPackage();
        unlockArgs.Write(0x08, 2UL);
        unlockArgs.Write(0x10, 0UL);
        var (unlockResponse, unlockPushes) = await RoundTrip("hero.LockHero", unlockArgs.ToArray());
        Assert(unlockResponse.Err == 0 && unlockResponse.Ret is not null &&
            ContainsSequence(unlockResponse.Ret, new byte[] { 0x08, 0x02 }),
            "unlock response did not identify HeroId=2");
        TResponse unlockHeroPush = unlockPushes.FirstOrDefault(p => p.Method == "hero.UpdateHeroBagData")
            ?? throw new InvalidDataException("unlock did not refresh hero data before its response");
        Assert(unlockHeroPush.Ret is { Length: > 0 } &&
            ContainsSequence(unlockHeroPush.Ret, new byte[] { 0x60, 0x00 }),
            "unlock update did not explicitly set Lock=false");
        PlayerAccount unlocked = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("unlock account disappeared");
        Assert(!unlocked.Dock.Heroes.Single(h => h.HeroId == 2).Lock, "unlock state was not persisted");

        // The ring shop uses a retired event token in the JP config. Existing profiles must receive
        // that client-side currency, and the purchased ring must be pushed before the Lua callback.
        var buyRingArgs = new ProtocolPackage();
        buyRingArgs.Write(0x08, 1072UL);
        buyRingArgs.Write(0x10, 102021UL);
        buyRingArgs.Write(0x18, 1UL);
        var (buyRingResponse, buyRingPushes) =
            await RoundTrip("shop.BuyGoods", buyRingArgs.ToArray());
        Assert(buyRingResponse.Err == 0 && buyRingResponse.Ret is { Length: > 0 },
            "oath ring purchase did not return a reward");
        Assert(buyRingPushes.Any(p => p.Method == "bag.UpdateBagData"),
            "oath ring purchase did not refresh inventory before its response");
        PlayerAccount ringPurchased = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("ring purchase account disappeared");
        Assert(ringPurchased.Bag?.Items.Single(i => i.TemplateId == 10180).Num == 1,
            "purchased oath ring was not persisted");
        Assert(ringPurchased.Bag?.Items.Single(i => i.TemplateId == 17553).Num == 99_999_999,
            "retired-event currency required by the ring shop was not provisioned");

        // A hero mutation refreshes the client cache before success. HeroGrid.Name is a custom
        // nickname, not the handbook's Chinese display name, so JP/CN clients stay localized.
        var marryArgs = new ProtocolPackage();
        marryArgs.Write(0x08, 2UL);
        marryArgs.Write(0x10, 1UL);
        var (marryResponse, marryPushes) = await RoundTrip("hero.Marry", marryArgs.ToArray());
        Assert(marryResponse.Err == 0, "Blucher marriage was rejected");
        TResponse marryHeroPush = marryPushes.FirstOrDefault(p => p.Method == "hero.UpdateHeroBagData")
            ?? throw new InvalidDataException("marriage did not refresh hero data");
        Assert(marryHeroPush.Ret is { Length: > 0 } &&
            !ContainsSequence(marryHeroPush.Ret, Encoding.UTF8.GetBytes("奥克兰")),
            "hero update incorrectly sent the Chinese handbook name as a custom nickname");
        TResponse marryBagPush = marryPushes.FirstOrDefault(p => p.Method == "bag.UpdateBagData")
            ?? throw new InvalidDataException("marriage did not refresh ring inventory");
        Assert(marryBagPush.Ret is { Length: > 0 } &&
            ContainsSequence(marryBagPush.Ret, new byte[] { 0x08, 0xC4, 0x4F, 0x10, 0x00 }),
            "marriage did not send an explicit zero-count ring deletion marker");
        Assert(marryPushes.Any(p => p.Method == "user.UpdateUserInfo"),
            "marriage did not refresh ring inventory and MarriedNum before its response");
        PlayerAccount married = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("marriage account disappeared");
        Assert(married.Dock.Heroes.Single(h => h.HeroId == 2).MarryTime > 0 &&
            married.Bag?.Items.Single(i => i.TemplateId == 10180).Num == 0,
            "marriage and ring deduction were not persisted atomically");

        var noRingArgs = new ProtocolPackage();
        noRingArgs.Write(0x08, 3UL);
        noRingArgs.Write(0x10, 1UL);
        var (noRingResponse, noRingPushes) = await RoundTrip("hero.Marry", noRingArgs.ToArray());
        Assert(noRingResponse.Err != 0 && noRingPushes.Count == 0,
            "marriage without an oath ring was reported as success");
        PlayerAccount rejectedMarriage = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("rejected marriage account disappeared");
        Assert(rejectedMarriage.Dock.Heroes.Single(h => h.HeroId == 3).MarryTime == 0 &&
            rejectedMarriage.Character.MarriedNum == married.Character.MarriedNum,
            "failed marriage changed persistent data");

        var (secondRingResponse, _) = await RoundTrip("shop.BuyGoods", buyRingArgs.ToArray());
        Assert(secondRingResponse.Err == 0, "second oath ring purchase was rejected");
        var (secondMarryResponse, secondMarryPushes) =
            await RoundTrip("hero.Marry", noRingArgs.ToArray());
        Assert(secondMarryResponse.Err == 0 &&
            secondMarryPushes.Any(p => p.Method == "hero.UpdateHeroBagData"),
            "a second eligible ship could not be married after purchasing another ring");
        PlayerAccount twiceMarried = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("second marriage account disappeared");
        Assert(twiceMarried.Dock.Heroes.Single(h => h.HeroId == 3).MarryTime > 0 &&
            twiceMarried.Character.MarriedNum == married.Character.MarriedNum + 1 &&
            twiceMarried.Bag?.Items.Single(i => i.TemplateId == 10180).Num == 0,
            "consecutive marriage state was not persisted correctly");

        // The shipped Lua protobuf runtime uses the proto2 unpacked representation for HeroIds.
        var retireArgs = new ProtocolPackage();
        retireArgs.Write(0x08, 2UL);
        retireArgs.Write(0x10, 0UL);
        var (retire, pushes) = await RoundTrip("hero.RetireHero", retireArgs.ToArray());

        Assert(retire.Err == 0 && retire.Ret is { Length: > 0 }, "retirement response did not contain rewards");
        TResponse heroPush = pushes.FirstOrDefault(p => p.Method == "hero.UpdateHeroBagData")
            ?? throw new InvalidDataException("retirement did not push a hero deletion marker");
        Assert(heroPush.Ret is { Length: > 0 } &&
            ContainsSequence(heroPush.Ret, new byte[] { 0x08, 0x02, 0x10, 0x00 }),
            "hero deletion marker did not explicitly include HeroId=2 and TemplateId=0");
        Assert(pushes.Any(p => p.Method == "user.UpdateUserInfo"), "retirement did not refresh currencies");
        Assert(pushes.Any(p => p.Method == "equip.UpdateEquipBagData"), "retirement did not refresh equipment");

        PlayerAccount saved = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("retirement account disappeared");
        Assert(saved.Dock.Heroes.All(h => h.HeroId != 2), "retired hero remained in persistent dock");
        EquipItem returnedEquip = saved.Equip?.Items.SingleOrDefault(e => e.EquipId == 77)
            ?? throw new InvalidDataException("retired hero equipment was deleted");
        Assert(returnedEquip.HeroId == 0, "retired hero equipment was not unbound");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static async Task TlsCaptureIntegrationTest()
{
    var root = FindRepositoryRoot();
    var serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0", "BlueOath.Server.dll");
    Assert(File.Exists(serverDll), "server assembly is missing; build the solution first");
    var material = Path.Combine(Path.GetTempPath(), "blueoath-tls-material-" + Guid.NewGuid().ToString("N"));
    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--tls-material-only");
    startInfo.ArgumentList.Add("--tls-output=" + material);
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "TLS material process did not start");
        var readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert(process.ExitCode == 0, "TLS material process failed: " + await process.StandardError.ReadToEndAsync());
        using var ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("TLS material process did not report ready"));
        var certificate = ready.RootElement.GetProperty("leafPem").GetString();
        var key = ready.RootElement.GetProperty("leafKeyPem").GetString();
        Assert(!string.IsNullOrWhiteSpace(certificate) && File.Exists(certificate), "leaf PEM was not emitted");
        Assert(!string.IsNullOrWhiteSpace(key) && File.Exists(key), "leaf private key PEM was not emitted");

        var python = new ProcessStartInfo("python")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        python.ArgumentList.Add("-c");
        python.ArgumentList.Add("import ssl,sys;c=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER);c.load_cert_chain(sys.argv[1],sys.argv[2])");
        python.ArgumentList.Add(certificate!);
        python.ArgumentList.Add(key!);
        using var openssl = Process.Start(python) ?? throw new InvalidOperationException("Python TLS runtime did not start");
        await openssl.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert(openssl.ExitCode == 0, "OpenSSL rejected TLS material: " + await openssl.StandardError.ReadToEndAsync());
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(material)) Directory.Delete(material, true);
    }
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "src", "BlueOath.Server"))) return current.FullName;
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("Repository root not found");
}

static Task MubarBattleStartCodecTest()
{
    string configDir = FindClientConfigDir();
    ChapterCopyLoader.Load(configDir);
    CopyBattleLoader.Load(configDir);

    const int copyId = 932113; // 实际客户端日志中的アンブラ進軍关卡
    Assert(ChapterCopyLoader.GetCopyType(copyId) == 33,
        "Mubar activity copy was not classified as CopyType 33");

    PlayerAccount account = PlayerAccountFactory.CreateDefault("mubar-codec", 1);
    byte[] payload = ProtocolEncoder.EncodeStartBaseRet(
        copyId, account.Dock.Heroes.ToList(), account.Character);
    Assert(ProtocolDecoder.DecodeVarintField(payload, 7) == 33,
        "copy.StartBase downgraded MubarCopy to PlotCopy");
    Assert(CopyBattleLoader.IsSearch3d(copyId),
        "Mubar activity copy was not classified as search_3d");

    bool hasCopyResource = false;
    bool hasConfigData = false;
    bool skipsEnemyVcr = false;
    ProtocolDecoder.ProtoReader reader = new(payload);
    while (reader.TryReadField(out int field, out int wire))
    {
        if (field == 4) hasCopyResource = true;
        if (field == 25) hasConfigData = true;
        if (field == 17 && wire == 2)
        {
            ProtocolDecoder.ProtoReader skip = new(reader.ReadBytes());
            while (skip.TryReadField(out int skipField, out int skipWire))
            {
                if ((skipField == 2 || skipField == 3) && skipWire == 0)
                {
                    if (skip.ReadVarint() == 1)
                        skipsEnemyVcr = true;
                }
                else
                    skip.Skip(skipWire);
            }
            continue;
        }
        reader.Skip(wire);
    }
    Assert(!hasCopyResource,
        "search_3d Mubar battle included arrRes and can stall on missing battlefield_resource");
    Assert(hasConfigData, "search_3d Mubar battle omitted search initialization ConfigData");
    Assert(skipsEnemyVcr, "search_3d Mubar battle did not skip blocking fleet VCR sequences");
    Assert(CopyBattleLoader.GetFleetIdList(copyId).Count > 0,
        "Mubar activity copy has no enemy fleet data");
    return Task.CompletedTask;
}

static Task SeaCopySafeAreaCodecTest()
{
    ChapterCopyLoader.Load(FindClientConfigDir());
    byte[] payload = ProtocolEncoder.EncodeSeaCopyInfo();
    bool foundSafeAreaCopy = false;

    ProtocolDecoder.ProtoReader response = new(payload);
    while (response.TryReadField(out int field, out int wire))
    {
        if (field != 1 || wire != 2)
        {
            response.Skip(wire);
            continue;
        }

        int baseId = 0;
        int safeLevel = 0;
        int selectedSafeLevel = 0;
        bool hasSafePoint = false;
        uint safePointBits = uint.MaxValue;
        ProtocolDecoder.ProtoReader baseInfo = new(response.ReadBytes());
        while (baseInfo.TryReadField(out int baseField, out int baseWire))
        {
            switch (baseField)
            {
                case 1 when baseWire == 0:
                    baseId = checked((int)baseInfo.ReadVarint());
                    break;
                case 8 when baseWire == 0:
                    safeLevel = checked((int)baseInfo.ReadVarint());
                    break;
                case 9 when baseWire == 5:
                    hasSafePoint = true;
                    safePointBits = baseInfo.ReadFixed32();
                    break;
                case 12 when baseWire == 0:
                    selectedSafeLevel = checked((int)baseInfo.ReadVarint());
                    break;
                default:
                    baseInfo.Skip(baseWire);
                    break;
            }
        }

        if (baseId != 5011) continue;
        foundSafeAreaCopy = true;
        Assert(safeLevel == 1,
            "sea-copy TBaseInfo omitted SfLv, causing config_safearea strId:nil");
        Assert(hasSafePoint && safePointBits == 0,
            "sea-copy TBaseInfo omitted the initial SfPoint float");
        Assert(selectedSafeLevel == 1,
            "sea-copy TBaseInfo omitted SfLvChoose used by SafeInfoPage");
        break;
    }

    Assert(foundSafeAreaCopy, "sea-copy synchronization did not include safe-area copy 5011");
    return Task.CompletedTask;
}

static async Task DailyCopyGameplayTest()
{
    string root = FindRepositoryRoot();
    string configDir = FindClientConfigDir();
    ChapterCopyLoader.Load(configDir);
    CopyBattleLoader.Load(configDir);

    Assert(ChapterCopyLoader.GetCopyType(20101) == 9,
        "destroyer challenge 20101 was not classified as DailyCopy");
    Assert(ChapterCopyLoader.GetDailyChapterId(20101) == 20001 &&
           ChapterCopyLoader.GetDailyGroupId(20001) == 2,
        "destroyer challenge chapter/group mapping does not match client config");
    Assert(ChapterCopyLoader.GetDailyCopyIds(20001).SequenceEqual(Enumerable.Range(20101, 10)),
        "destroyer challenge did not load all ten configured difficulties");
    Assert(ChapterCopyLoader.GetDailyTreatyCopyIds(20001).SequenceEqual([20518]) &&
           ChapterCopyLoader.IsDailyTreatyCopy(20518),
        "destroyer challenge did not load its configured treaty stage");

    PlayerAccount codecAccount = PlayerAccountFactory.CreateDefault("daily-copy-codec", 1);
    byte[] startPayload = ProtocolEncoder.EncodeStartBaseRet(
        20101, codecAccount.Dock.Heroes.ToList(), codecAccount.Character);
    Assert(ProtocolDecoder.DecodeVarintField(startPayload, 7) == 9,
        "copy.StartBase did not preserve DailyCopy type 9");

    var (baseInfoCount, foundFirstDestroyerLevel, foundDestroyerTreaty) =
        ScanDailyCopyInfo(ProtocolEncoder.EncodeDailyCopyInfo());
    Assert(baseInfoCount == 44 && foundFirstDestroyerLevel && foundDestroyerTreaty,
        "DailyCopy synchronization omitted configured normal or treaty levels");

    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-daily-copy-" + Guid.NewGuid().ToString("N"));
    const string profileId = "daily-copy-player";
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        ServerOptions options = ServerOptions.Parse([
            "--data=" + dataRoot,
            "--client-path=" + Path.Combine(root, "blueoath", "blueoath"),
            "--profile-id=" + profileId,
        ]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        var dailyCopy = new DailyCopyService(services);
        const int today = 1_800_000_000;

        PlayerAccount account = await dailyCopy.GetRefreshedAccountAsync(
            profileId, today, CancellationToken.None);
        Assert(account.DailyCopy?.Chapters?.Count == 4 && account.DailyCopy.Groups?.Count == 4,
            "dailycopy.GetData did not initialize all client chapter/group rows");

        DailyCopyPassMutation mutation = dailyCopy.RecordPass(account, 20101, 3, today);
        account = mutation.Account;
        Assert(mutation.FirstPass, "first destroyer challenge clear was not marked as first pass");
        Assert(mutation.Rewards.Count >= 3 && mutation.Rewards.All(x => x.Num > 0),
            "destroyer clear omitted its configured first-pass/basic drops");
        await services.SaveAccountAsync(account, CancellationToken.None);
        DailyCopyChapterProgress destroyer = account.DailyCopy!.Chapters!
            .Single(x => x.ChapterId == 20001);
        Assert(destroyer.ChallengeTimes == 1 && destroyer.PassCopy?.SequenceEqual([20101]) == true,
            "destroyer clear did not persist challenge count and unlock progress");
        Assert(account.DailyCopy.Groups!.Single(x => x.DailyGroupId == 2).SuccessTimes == 1,
            "destroyer clear did not increment daily group success count");

        DailyCopyPassMutation treatyMutation = dailyCopy.RecordPass(account, 20518, 3, today);
        account = treatyMutation.Account;
        DailyCopyChapterProgress treatyDestroyer = account.DailyCopy!.Chapters!
            .Single(x => x.ChapterId == 20001);
        Assert(treatyMutation.FirstPass &&
               treatyMutation.Rewards.Where(x => x.ConfigId == 13001).Sum(x => x.Num) >= 5_560,
            "destroyer treaty clear omitted its configured pass/star rewards");
        Assert(treatyDestroyer.ChallengeTimes == 2 &&
               treatyDestroyer.PassCopy?.Contains(20518) == true &&
               treatyDestroyer.SelectEx && treatyDestroyer.ExStar == 3,
            "destroyer treaty clear did not persist its pass and EX-star state");
        Assert(account.DailyCopy.Groups!.Single(x => x.DailyGroupId == 2).SuccessTimes == 2,
            "destroyer treaty clear did not increment daily group success count");
        await services.SaveAccountAsync(account, CancellationToken.None);

        var (chapterRows, groupRows, extraRows) =
            CountDailyCopySnapshotRows(DailyCopyService.EncodeSnapshot(account.DailyCopy, today));
        Assert(chapterRows == 4 && groupRows == 4 && extraRows == 4,
            "dailycopy.UpdateDailyCopyData omitted a repeated array required by the client");

        PlayerAccount tomorrow = await dailyCopy.GetRefreshedAccountAsync(
            profileId, today + 86_400, CancellationToken.None);
        DailyCopyChapterProgress resetDestroyer = tomorrow.DailyCopy!.Chapters!
            .Single(x => x.ChapterId == 20001);
        Assert(resetDestroyer.ChallengeTimes == 0 &&
               resetDestroyer.PassCopy?.Contains(20101) == true &&
               resetDestroyer.PassCopy?.Contains(20518) == true &&
               resetDestroyer.ExStar == 3 &&
               tomorrow.DailyCopy.Groups!.Single(x => x.DailyGroupId == 2).SuccessTimes == 0,
            "cross-day refresh did not reset counts while preserving normal/treaty progress");
          }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

// 自选箱（config_item_selected，物品类型 8）此前完全没有服务端实现：bag.GetSelectTreasureInfo
// 落到 ShopModule 的 default 分支返回空 Ret 且 Err=0，客户端 SelectRandTreasurePage 解出空的
// treasuresInfo，表现为「选好点确认后什么都没有」。
// item_id 是 [GoodsType, ConfigId, Num] 三元组列表；position 是其中的序号，<b>从 1 开始</b>：
// 客户端 SelectRandTreasurePage 把它当 Lua 表下标直接用（item_id[self.pos]），初值即为 1。
// 按 0 基解析会整体偏移一位——选第 1 项拿到第 2 项，选末项则越界被拒。
// 本次只实现 type==3 的道具箱；type 1/2（舰船箱、装备箱）另有实现，必须保持空响应不变。
static async Task SelectTreasureTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-select-box-" + Guid.NewGuid().ToString("N"));
    const string profileId = "select-box";
    const int itemBoxId = 80134;     // 祈願特注石選択箱（精選版）：[[11,110037,18],[11,110050,18]]
    const int fashionBoxId = 80240;  // 大破着せ替え+α選択箱：首项 [18,4044015,1]
    const int shipBoxId = 80324;     // 指定戦姫チケット（限定版）：下标 0 是舰船
    const int heroBoxId = 80001;     // 舰船箱（type=1），必须保持空响应
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        await repo.SaveAccountAsync(PlayerAccountFactory.CreateDefault(profileId, 1) with
        {
            Bag = new PlayerBag(
            [
                new BagItem(itemBoxId, 5),
                new BagItem(fashionBoxId, 1),
                new BagItem(shipBoxId, 1),
                new BagItem(heroBoxId, 1),
            ], 200),
        });

        ServerOptions options = ServerOptions.Parse(
            ["--data=" + dataRoot,
             "--client-path=" + Path.Combine(root, "blueoath", "blueoath"),
             "--profile-id=" + profileId]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        var shop = new ShopService(services);

        static byte[] Args(int treasureId, int position, int num) => new ProtocolPackage()
            .Write(0x08, (ulong)treasureId)
            .Write(0x10, (ulong)position)
            .Write(0x18, (ulong)num)
            .ToArray();

        // 80134 的选项是 [[11,110037,18],[11,110050,18]]。position=1 必须给第 1 项。
        ShopService.TreasureOpenResult first = await shop.BuildOpenSelectTreasureRetAsync(
            new TRequest("bag.GetSelectTreasureInfo", Args(itemBoxId, 1, 1)), profileId, CancellationToken.None);
        Assert(first.Changed, $"item selection box was rejected at position 1: {first.Error}");
        PlayerAccount afterFirst = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("select-box account disappeared");
        Assert(afterFirst.Bag!.Items.Single(i => i.TemplateId == 110037).Num == 18,
            "position 1 did not grant the first option (off-by-one in the option index?)");
        Assert(afterFirst.Bag.Items.All(i => i.TemplateId != 110050),
            "position 1 granted the second option instead of the first");
        Assert(afterFirst.Bag.Items.Single(i => i.TemplateId == itemBoxId).Num == 4,
            "item selection box consumed the wrong number of boxes");

        // position=2 是该箱的末项，必须同样可选——按 0 基解析时这里会越界被拒。
        ShopService.TreasureOpenResult last = await shop.BuildOpenSelectTreasureRetAsync(
            new TRequest("bag.GetSelectTreasureInfo", Args(itemBoxId, 2, 1)), profileId, CancellationToken.None);
        Assert(last.Changed, $"the last option was rejected: {last.Error}");
        PlayerAccount afterLast = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("select-box account disappeared");
        Assert(afterLast.Bag!.Items.Single(i => i.TemplateId == 110050).Num == 18,
            "the last option did not grant its reward");

        // 两侧越界都必须被拒绝，且不能扣除箱子。
        foreach (int badPosition in new[] { 0, 3 })
        {
            ShopService.TreasureOpenResult outOfRange = await shop.BuildOpenSelectTreasureRetAsync(
                new TRequest("bag.GetSelectTreasureInfo", Args(itemBoxId, badPosition, 1)),
                profileId, CancellationToken.None);
            Assert(!outOfRange.Changed,
                $"item selection box accepted an out-of-range position {badPosition}");
            PlayerAccount afterBad = await repo.LoadAccountAsync(profileId)
                ?? throw new InvalidDataException("select-box account disappeared");
            Assert(afterBad.Bag!.Items.Single(i => i.TemplateId == itemBoxId).Num == 3,
                $"a rejected selection at position {badPosition} still consumed a box");
        }

        // 时装奖励必须进时装表，不能按道具入包。
        ShopService.TreasureOpenResult fashion = await shop.BuildOpenSelectTreasureRetAsync(
            new TRequest("bag.GetSelectTreasureInfo", Args(fashionBoxId, 1, 1)), profileId, CancellationToken.None);
        Assert(fashion.Changed, $"fashion option was rejected: {fashion.Error}");
        PlayerAccount afterFashion = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("select-box account disappeared");
        Assert(afterFashion.Fashion?.Entries.Any(e => e.FashionTids.Contains(4044015)) == true,
            "fashion option did not unlock the skin");
        Assert(afterFashion.Bag!.Items.All(i => i.TemplateId != 4044015),
            "fashion option polluted the item bag");

        // 舰船选项本次不实现，必须明确拒绝而不是静默发放。
        ShopService.TreasureOpenResult ship = await shop.BuildOpenSelectTreasureRetAsync(
            new TRequest("bag.GetSelectTreasureInfo", Args(shipBoxId, 1, 1)), profileId, CancellationToken.None);
        Assert(!ship.Changed && ship.Error.Length > 0,
            "ship option inside an item box should be rejected with an error");

        // 舰船箱（type=1）必须维持改动前的空响应：Changed=false 且 Error 为空。
        ShopService.TreasureOpenResult heroBox = await shop.BuildOpenSelectTreasureRetAsync(
            new TRequest("bag.GetSelectTreasureInfo", Args(heroBoxId, 1, 1)), profileId, CancellationToken.None);
        Assert(!heroBox.Changed && heroBox.Error.Length == 0,
            "hero selection box must keep its previous empty response");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

static Task TaskCompletionRequiresProgressTest()
{
    TaskDefinition definition = new(
        TaskConfigCatalog.TypeDaily, 10, EventType: 2, Goal: 3, CountType: 1, RewardId: 40008);

    byte[] emptyTask = TaskProtocolCodec.EncodeTask(definition, null);
    Assert(ProtocolDecoder.DecodeVarintField(emptyTask, 3) == 0 &&
           ProtocolDecoder.DecodeVarintField(emptyTask, 4) == 0,
        "a task without progress encoded a finish time or completed count");

    Assert(!TaskService.IsCompleted(null, definition),
        "a task without persisted progress was treated as complete");
    Assert(!TaskService.IsCompleted(
            new PlayerTaskRecord(definition.TaskType, definition.Id, 0, Count: 2), definition),
        "task progress below the configured goal was treated as complete");
    Assert(TaskService.IsCompleted(
            new PlayerTaskRecord(definition.TaskType, definition.Id, 0, Count: 3), definition),
        "task progress at the configured goal was not treated as complete");
    Assert(TaskService.IsCompleted(
            new PlayerTaskRecord(definition.TaskType, definition.Id, FinishTime: 1, Count: 0), definition),
        "an explicitly finished task was not treated as complete");

    Dictionary<(int Type, int Id), PlayerTaskRecord> noProgress = [];
    Assert(TaskProtocolCodec.GetEventProgress([definition], noProgress) == 0,
        "an event without persisted progress encoded its goal as current progress");
    Dictionary<(int Type, int Id), PlayerTaskRecord> partialProgress = new()
    {
        [(definition.TaskType, definition.Id)] =
            new PlayerTaskRecord(definition.TaskType, definition.Id, 0, Count: 2)
    };
    Assert(TaskProtocolCodec.GetEventProgress([definition], partialProgress) == 2,
        "partial task event progress was not preserved");
    return Task.CompletedTask;
}

// 舰船强化（hero.HeroIntensify）此前是空占位：返回空 Ret、Err=0、不推送，客户端
// _HeroIntensify 只看 err 就播成功动画，属性却全部取自 THeroGrid.Intensify（字段 7），
// 于是「强化后数值不变」。数值口径对齐客户端 Strengthen_Page.GenPropertyData。
static async Task ShipIntensifyTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-ship-intensify-" + Guid.NewGuid().ToString("N"));
    const string profileId = "ship-intensify";
    // 10210511：enhance_type=2，need_power_exp = 8:3750 9:5625 10:1125 11:5625 12:2812，
    //           max_power_prop = 8:24 9:16 10:80 11:16 12:32，provide = 各属性 3000。
    // 10110111：enhance_type=1（异型），provide = 各属性 1000。
    const int targetTid = 10210511;
    const int sameTypeTid = 10210511;
    const int otherTypeTid = 10110111;
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, 1);
        await repo.SaveAccountAsync(seed with
        {
            Character = seed.Character with { SecretaryId = 10 },
            Dock = new HeroDock(
            [
                new Hero(10, targetTid, 5),
                new Hero(20, sameTypeTid, 1),
                new Hero(30, otherTypeTid, 1),
                new Hero(40, sameTypeTid, 1, Lock: true),
                new Hero(50, sameTypeTid, 9),
            ]),
            Fleet = new PlayerFleet([new FleetEntry(1, HeroInfo: [10])]),
        });

        ServerOptions options = ServerOptions.Parse(
            ["--data=" + dataRoot,
             "--client-path=" + Path.Combine(root, "blueoath", "blueoath"),
             "--profile-id=" + profileId]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        var heroService = new HeroService(services);

        static TRequest Intensify(uint heroId, uint[] materials)
        {
            var package = new ProtocolPackage().Write(0x08, heroId);
            foreach (uint material in materials) package.Write(0x10, material);
            return new TRequest("hero.HeroIntensify", package.ToArray());
        }

        // 上锁、非 1 级素材必须被拒（对应客户端 ScreenShip / FilterHero 的无条件筛选）。
        foreach (uint rejected in new uint[] { 40, 50 })
            Assert(!(await heroService.BuildIntensifyRetAsync(
                        Intensify(10, [rejected]), profileId, CancellationToken.None)).Changed,
                $"intensify accepted an ineligible material (hero {rejected})");
        // 目标自身不能同时作素材。
        Assert(!(await heroService.BuildIntensifyRetAsync(
                    Intensify(10, [10]), profileId, CancellationToken.None)).Changed,
            "intensify accepted the target itself as material");

        HeroService.IntensifyResult result = await heroService.BuildIntensifyRetAsync(
            Intensify(10, [20, 30]), profileId, CancellationToken.None);
        Assert(result.Changed && result.UpdatedHero is not null, "intensify did not apply");

        // 同型 3000×1.5=4500，异型 1000×1，合计 5500：
        //   attr8  5500/3750 -> Lv1 余 1750
        //   attr10 5500/1125 -> Lv4 余 1000
        Dictionary<int, AttrIntensify> byAttr =
            result.UpdatedHero!.Intensify!.ToDictionary(entry => entry.AttrType);
        Assert(byAttr[8].IntensifyLvl == 1 && byAttr[8].CurExp == 1750,
            $"attr 8 intensify drifted: Lv{byAttr[8].IntensifyLvl} exp{byAttr[8].CurExp}");
        Assert(byAttr[10].IntensifyLvl == 4 && byAttr[10].CurExp == 1000,
            $"attr 10 intensify drifted: Lv{byAttr[10].IntensifyLvl} exp{byAttr[10].CurExp}");
        // 素材提供了 14/16 的强化值，但目标的 need_power_exp 不含这两项，不应被强化。
        Assert(!byAttr.ContainsKey(14) && !byAttr.ContainsKey(16),
            "intensify touched attributes outside the target's need_power_exp");
        Assert(result.ConsumedHeroIds.Count == 2, "intensify did not consume both materials");

        PlayerAccount after = await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        Assert(after.Dock.Heroes.All(h => h.HeroId != 20 && h.HeroId != 30),
            "consumed materials remain in the dock");

        // THeroGrid.Intensify 是字段 7（wire 2）。缺这段编码，客户端 HeroAttr:_GetIntensify
        // 拿到空表，属性加成恒为 0 —— 正是「强化后数值不变」的直接原因。
        // attr8 子消息：08 08 (AttrType=8) 10 01 (Lv=1) 18 D6 0D (CurExp=1750)，共 7 字节。
        byte[] encoded = PlayerDataCodec.Encode(GameServices.ToHeroGrid(result.UpdatedHero!));
        Assert(ContainsSequence(encoded, [0x3A, 0x07, 0x08, 0x08, 0x10, 0x01, 0x18, 0xD6, 0x0D]),
            "hero grid did not encode Intensify as field 7");

        // 满级后再强化不应白吃素材。
        HeroService.IntensifyResult noGain = await heroService.BuildIntensifyRetAsync(
            Intensify(10, [20]), profileId, CancellationToken.None);
        Assert(!noGain.Changed, "intensify consumed an already-removed material");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

// 强化页的三个开关（LOGIC_HERO_INTENSIFY_TypeMatchCancel / _RHeroSelect / _MORESELECT）
// 都通过 guide.Setting 存取，服务端此前是 "guide.Setting" => [] 直接丢弃，客户端
// GuideData:GetSettingByKey 永远读到 nil，开关恒为关闭：筛选只认 N 品质、选择槽固定 6 格，
// 「一斉追加」于是常年提示「条件を満たした戦姫はいません」。
// 客户端 GuideService:_ReceiveUserSetting 按 TGuideInfo 解应答，所以应答必须带 Setting；
// 登录的 guide.GuideInfo 也要带上，否则重登即丢。
static async Task GuideUserSettingTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-guide-setting-" + Guid.NewGuid().ToString("N"));
    const string profileId = "guide-setting";
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        await repo.SaveAccountAsync(PlayerAccountFactory.CreateDefault(profileId, 1));

        ServerOptions options = ServerOptions.Parse(
            ["--data=" + dataRoot,
             "--client-path=" + Path.Combine(root, "blueoath", "blueoath"),
             "--profile-id=" + profileId]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        var guide = new GuideModule(services);
        var ctx = new GameContext
        {
            ProfileId = profileId,
            Now = 1,
            Ct = CancellationToken.None,
            Services = services,
        };

        static byte[] SettingArgs(params (string Key, string Value)[] entries)
        {
            var package = new ProtocolPackage();
            foreach ((string key, string value) in entries)
            {
                var body = new ProtocolPackage()
                    .Write(0x0A, key)
                    .Write(0x12, value);
                package.Write(0x0A, body.ToArray());
            }
            return package.ToArray();
        }

        ModuleResult saved = await guide.HandleAsync(ctx, new TRequest("guide.Setting",
            SettingArgs(("LOGIC_HERO_INTENSIFY_RHeroSelect", "true"),
                        ("LOGIC_HERO_INTENSIFY_MORESELECT", "true"))));
        // 应答必须是带 Setting 的 TGuideInfo：字段 3 的子消息里 Key/Value 都是 string。
        Assert(saved.Ret is { Length: > 0 }, "guide.Setting returned an empty payload");
        Assert(ContainsSequence(saved.Ret!,
                   System.Text.Encoding.UTF8.GetBytes("LOGIC_HERO_INTENSIFY_RHeroSelect")) &&
               ContainsSequence(saved.Ret!,
                   System.Text.Encoding.UTF8.GetBytes("LOGIC_HERO_INTENSIFY_MORESELECT")),
            "guide.Setting response did not echo the stored settings");

        PlayerAccount stored = await repo.LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("account missing");
        Assert(stored.UserSettings?["LOGIC_HERO_INTENSIFY_RHeroSelect"] == "true" &&
               stored.UserSettings?["LOGIC_HERO_INTENSIFY_MORESELECT"] == "true",
            "guide.Setting did not persist the submitted keys");

        // 再提交一次：同键覆盖、异键并入，不能整表替换。
        await guide.HandleAsync(ctx, new TRequest("guide.Setting",
            SettingArgs(("LOGIC_HERO_INTENSIFY_RHeroSelect", "false"),
                        ("LOGIC_HERO_INTENSIFY_TypeMatchCancel", "true"))));
        stored = await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        Assert(stored.UserSettings?.Count == 3 &&
               stored.UserSettings["LOGIC_HERO_INTENSIFY_RHeroSelect"] == "false" &&
               stored.UserSettings["LOGIC_HERO_INTENSIFY_MORESELECT"] == "true" &&
               stored.UserSettings["LOGIC_HERO_INTENSIFY_TypeMatchCancel"] == "true",
            "guide.Setting replaced the map instead of merging keys");

        // 登录推送必须带上存档里的设置，否则重登即丢。
        byte[] push = services.BuildGuideInfoPush(1, stored);
        Assert(ContainsSequence(push,
                   System.Text.Encoding.UTF8.GetBytes("LOGIC_HERO_INTENSIFY_TypeMatchCancel")),
            "guide.GuideInfo login push omitted the persisted user settings");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}
// ProtocolDecoder.ProtoReader 是 ref struct，C# 12 不允许它出现在 async 方法体内
// （CS8652）。DailyCopyGameplayTest 是 async，两处解析必须提到独立的同步方法里。
static (int BaseInfoCount, bool FirstDestroyerLevel, bool DestroyerTreaty) ScanDailyCopyInfo(byte[] copyPayload)
{
    int baseInfoCount = 0;
    bool foundFirstDestroyerLevel = false, foundDestroyerTreaty = false;
    ProtocolDecoder.ProtoReader copyReader = new(copyPayload);
    while (copyReader.TryReadField(out int field, out int wire))
    {
        if (field != 1 || wire != 2)
        {
            copyReader.Skip(wire);
            continue;
        }
        baseInfoCount++;
        int baseId = 0;
        ProtocolDecoder.ProtoReader baseInfo = new(copyReader.ReadBytes());
        while (baseInfo.TryReadField(out int baseField, out int baseWire))
        {
            if (baseField == 1 && baseWire == 0) baseId = checked((int)baseInfo.ReadVarint());
            else baseInfo.Skip(baseWire);
        }
        if (baseId == 20101) foundFirstDestroyerLevel = true;
        if (baseId == 20518) foundDestroyerTreaty = true;
    }
    return (baseInfoCount, foundFirstDestroyerLevel, foundDestroyerTreaty);
}

static (int ChapterRows, int GroupRows, int ExtraRows) CountDailyCopySnapshotRows(byte[] statePayload)
{
    int chapterRows = 0, groupRows = 0, extraRows = 0;
    ProtocolDecoder.ProtoReader stateReader = new(statePayload);
    while (stateReader.TryReadField(out int stateField, out int stateWire))
    {
        if (stateWire == 2 && stateField is >= 1 and <= 3)
        {
            _ = stateReader.ReadBytes();
            if (stateField == 1) chapterRows++;
            else if (stateField == 2) groupRows++;
            else extraRows++;
        }
        else stateReader.Skip(stateWire);
    }
    return (chapterRows, groupRows, extraRows);
}

static SettlementRules TestSettlementRules(bool tavernDiscount = false, Func<int, int, int>? heroAddition = null)
{
    var infos = new Dictionary<int, ConfigBuildinginfo>
    {
        [5] = new() { Id = 5, Type = 1, Level = 5, Moodcost = 10_500 },   // 办公室
        [15] = new() { Id = 15, Type = 2, Level = 5, Moodcost = 10_500 }, // 电力室
        [35] = new() { Id = 35, Type = 4, Level = 5, Moodcost = 10_500, Reducecost = 5_000 }, // 居酒屋
        [45] = new() { Id = 45, Type = 5, Level = 5, Addmood = 34_000 },  // 宿舍
    };
    return new SettlementRules
    {
        ApplyTavernDiscount = tavernDiscount,
        BuildingInfo = tid => infos.GetValueOrDefault(tid),
        HeroAddition = heroAddition ?? ((_, _) => 0),
    };
}

static PlayerAccount TimeTestAccount(
    IReadOnlyList<Hero> heroes,
    IReadOnlyList<PlayerBuildingEntry> buildings,
    long workerUpdateTime,
    PlayerBath? bath = null,
    uint secretaryId = 999,
    int bathCoins = 0)
{
    PlayerAccount account = PlayerAccountFactory.CreateDefault("time-settlement", 1);
    return account with
    {
        Character = account.Character with { SecretaryId = secretaryId, Bath = bathCoins },
        Dock = new HeroDock(heroes),
        Building = new PlayerBuilding(buildings, [], WorkerUpdateTime: workerUpdateTime),
        Bath = bath,
    };
}

static Hero TimeHero(PlayerAccount account, uint heroId) => account.Dock.Heroes.Single(hero => hero.HeroId == heroId);

static Task TimeSettlementFormulaTest()
{
    const long T0 = 1_800_000_000; // UTC 整点：分、秒均为 0
    Assert(TimeSettlement.NaturalTicks(T0, T0 + 359, 6) == (0, T0), "natural tick fired before its first boundary");
    Assert(TimeSettlement.NaturalTicks(T0, T0 + 360, 6) == (1, T0 + 360), "first natural boundary was not counted");
    Assert(TimeSettlement.NaturalTicks(T0, T0 + 3600, 6) == (10, T0 + 3600), "hourly natural ticks mismatch");
    // UpdateTime 秒数不为 0 时边界保留秒数（客户端 nearTime = UpdateTime + (下一个 6 分钟 - 分钟) * 60）。
    Assert(TimeSettlement.NaturalTicks(1_791_442_439, 1_791_442_439 + 3600, 6) == (10, 1_791_445_739),
        "natural boundaries did not keep the UpdateTime second offset");
    Assert(TimeSettlement.ApplyNatural(0, 10, 100, 1_190_000) == 1_000, "natural recovery amount mismatch");
    Assert(TimeSettlement.ApplyNatural(0, 10, 200, 1_190_000) == 2_000, "married natural recovery mismatch");
    Assert(TimeSettlement.ApplyNatural(1_189_950, 10, 100, 1_190_000) == 1_190_000, "natural recovery exceeded its limit");
    Assert(TimeSettlement.ApplyNatural(1_300_000, 10, 100, 1_190_000) == 1_300_000,
        "natural recovery lowered a mood above its limit");

    SettlementRules rules = TestSettlementRules();

    // 工作建筑：每 600 秒扣 moodcost（10500 = 1.05），自然恢复在 119 以上不生效。
    PlayerAccount office = TimeTestAccount(
        [new Hero(1, 10210511, 1, CreateTime: (int)T0, UpdateTime: (int)T0, Mood: 1_500_000)],
        [new PlayerBuildingEntry(1, 5, 5, [1], LastUpdateTime: T0)], T0);
    SettlementResult worked = TimeSettlement.Settle(office, T0 + 3600, rules);
    Assert(TimeHero(worked.Account, 1).Mood == 1_437_000, "office mood cost did not match the client formula");
    Assert(TimeHero(worked.Account, 1).UpdateTime == T0 + 3600, "hero UpdateTime did not advance to the last natural boundary");
    Assert(worked.Account.Building!.Buildings[0].LastUpdateTime == T0 + 3600, "building anchor did not advance");
    Assert(worked.ChangedHeroIds.SetEquals([1u]) && worked.BuildingChanged, "settlement did not report its changes");
    Assert(TimeHero(TimeSettlement.Settle(office, T0 + 172_800, rules).Account, 1).Mood == 0,
        "mood cost did not clamp at zero");

    // 幂等：同一个 now 再结算不变化且返回同一个实例；时钟回拨不重复结算。
    SettlementResult again = TimeSettlement.Settle(worked.Account, T0 + 3600, rules);
    Assert(!again.Changed && ReferenceEquals(again.Account, worked.Account), "repeated settlement was not idempotent");
    SettlementResult rewound = TimeSettlement.Settle(worked.Account, T0 + 100, rules);
    Assert(!rewound.Changed, "a clock rollback produced a settlement");

    // 分段结算与一次结算结果相同。
    PlayerAccount halfway = TimeTestAccount(
        [new Hero(1, 10210511, 1, UpdateTime: (int)T0, Mood: 1_000_000)],
        [new PlayerBuildingEntry(1, 5, 5, [1], LastUpdateTime: T0)], T0);
    PlayerAccount twoStep = TimeSettlement.Settle(
        TimeSettlement.Settle(halfway, T0 + 1800, rules).Account, T0 + 3600, rules).Account;
    PlayerAccount oneStep = TimeSettlement.Settle(halfway, T0 + 3600, rules).Account;
    Assert(TimeHero(twoStep, 1).Mood == 938_000 && TimeHero(oneStep, 1).Mood == 938_000 &&
           TimeHero(twoStep, 1).UpdateTime == TimeHero(oneStep, 1).UpdateTime,
        "piecewise settlement diverged from a single settlement");

    // 宿舍：按下发的 ProduceSpeed（MoodSpeed）每 600 秒回复；可超过自然恢复上限，封顶 150。
    PlayerAccount dorm = TimeTestAccount(
        [
            new Hero(1, 10210511, 1, UpdateTime: (int)T0, Mood: 100_000),
            new Hero(2, 10210511, 1, UpdateTime: (int)T0, Mood: 1_450_000),
        ],
        [new PlayerBuildingEntry(2, 45, 5, [1, 2], LastUpdateTime: T0, MoodSpeed: 34_000)], T0);
    PlayerAccount rested = TimeSettlement.Settle(dorm, T0 + 3600, rules).Account;
    Assert(TimeHero(rested, 1).Mood == 305_000, "dormitory recovery did not match the client formula");
    Assert(TimeHero(rested, 2).Mood == 1_500_000, "dormitory recovery did not clamp at the mood bound");

    // 电力室以 WorkerUpdateTime 为锚点（客户端 CheckoutHeroMoodChange 的电力室分支）。
    PlayerAccount power = TimeTestAccount(
        [new Hero(1, 10210511, 1, UpdateTime: (int)T0, Mood: 1_000_000)],
        [new PlayerBuildingEntry(3, 15, 5, [1], LastUpdateTime: T0 + 1800)], T0);
    PlayerAccount powered = TimeSettlement.Settle(power, T0 + 3600, rules).Account;
    Assert(TimeHero(powered, 1).Mood == 938_000, "electric factory did not use WorkerUpdateTime");
    Assert(powered.Building!.WorkerUpdateTime == T0 + 3600, "WorkerUpdateTime did not advance");

    // 居酒屋折减其它建筑的消耗（reducecost 5000 = 五折）。
    PlayerAccount tavern = TimeTestAccount(
        [new Hero(1, 10210511, 1, UpdateTime: (int)T0, Mood: 1_500_000)],
        [new PlayerBuildingEntry(1, 5, 5, [1], LastUpdateTime: T0), new PlayerBuildingEntry(4, 35, 5, [], LastUpdateTime: T0)], T0);
    Assert(TimeHero(TimeSettlement.Settle(tavern, T0 + 3600, TestSettlementRules(tavernDiscount: true)).Account, 1).Mood == 1_468_500,
        "tavern reducecost did not discount the mood cost");

    // 宿舍速度：只统计心情大于 0 的舰娘的性格加成。
    SettlementRules addition = TestSettlementRules(heroAddition: (template, _) => template == 111 ? 1000 : 0);
    ConfigBuildinginfo dormInfo = addition.BuildingInfo(45)!;
    Assert(TimeSettlement.DormSpeed(dormInfo, [new Hero(1, 111, 1, Mood: 10), new Hero(2, 222, 1, Mood: 10)], addition) == 37_400,
        "dormitory speed ignored the character addition");
    Assert(TimeSettlement.DormSpeed(dormInfo, [new Hero(1, 111, 1, Mood: 0), new Hero(2, 222, 1, Mood: 10)], addition) == 34_000,
        "dormitory speed counted a zero-mood hero");

    // 秘书舰在建筑中时，推进 UpdateTime 前先结算好感（首个周期在锚点 + 6 小时）。
    PlayerAccount secretary = TimeTestAccount(
        [
            new Hero(1, 10210511, 1, UpdateTime: (int)T0, Affection: 500_000, Mood: 1_000_000),
            new Hero(2, 10210511, 1, UpdateTime: (int)T0, Affection: 500_000, Mood: 1_000_000),
        ],
        [new PlayerBuildingEntry(2, 45, 5, [1, 2], LastUpdateTime: T0, MoodSpeed: 34_000)], T0, secretaryId: 1);
    PlayerAccount baked = TimeSettlement.Settle(secretary, T0 + 21_610, rules).Account;
    Assert(TimeHero(baked, 1).Affection == 507_500 && TimeHero(baked, 1).AffectionSettledAt == T0 + 21_600,
        "secretary affection was not baked before advancing UpdateTime");
    Assert(TimeHero(baked, 2).Affection == 500_000, "a non-secretary hero gained secretary affection");
    // 频繁结算（基建页每 60 秒）不能让好感的第一个周期永远凑不满。
    PlayerAccount frequent = secretary;
    for (long t = T0 + 60; t <= T0 + 7 * 3600; t += 60)
        frequent = TimeSettlement.Settle(frequent, t, rules).Account;
    Assert(TimeHero(frequent, 1).Affection == 507_500 && TimeHero(frequent, 1).AffectionSettledAt == T0 + 21_600,
        "frequent settlements stalled the secretary affection");

    // 浴场：入浴期间每个自然恢复边界额外 +4；浴券 8 小时到期后 StartTime=0、BathTime=总时长。
    PlayerAccount bathing = TimeTestAccount(
        [new Hero(1, 10210511, 1, UpdateTime: (int)T0, Mood: 400_000)], [], T0,
        new PlayerBath([new BathHero(1, Pos: 1, StartTime: T0, EnterTime: T0)]));
    PlayerAccount soaked = TimeSettlement.Settle(bathing, T0 + 3600, rules).Account;
    Assert(TimeHero(soaked, 1).Mood == 801_000, "bath recovery did not match natural + bath ticks");
    SettlementResult expired = TimeSettlement.Settle(soaked, T0 + 28_900, rules);
    BathHero finished = expired.Account.Bath!.HeroList.Single();
    Assert(finished.StartTime == 0 && finished.BathTime == 28_800 && finished.FinishedAt == T0 + 28_800 && expired.BathChanged,
        "an expired bath ticket was not finished");
    Assert(!TimeSettlement.Settle(expired.Account, T0 + 28_900, rules).Changed, "an expired bath settled twice");
    PlayerAccount autoBath = TimeTestAccount(
        [new Hero(1, 10210511, 1, UpdateTime: (int)T0, Mood: 400_000)], [], T0,
        new PlayerBath([new BathHero(1, Pos: 1, IsAuto: 1, StartTime: T0, EnterTime: T0)]), bathCoins: 100);
    BathHero renewed = TimeSettlement.Settle(autoBath, T0 + 28_900, rules).Account.Bath!.HeroList.Single();
    Assert(renewed.StartTime == T0 + 28_800 && renewed.BathTime == 0, "an auto bath ticket did not renew");

    // 规范化：同时在浴场与建筑中的舰娘以浴场为准；船坞中不存在的舰娘被移除。
    PlayerAccount conflicted = TimeTestAccount(
        [new Hero(1, 10210511, 1, UpdateTime: (int)T0, Mood: 400_000)],
        [new PlayerBuildingEntry(1, 5, 5, [1, 77], LastUpdateTime: T0)], T0,
        new PlayerBath([new BathHero(1, Pos: 2, StartTime: T0, EnterTime: T0), new BathHero(88, Pos: 3)]));
    PlayerAccount normalized = TimeSettlement.Settle(conflicted, T0, rules).Account;
    Assert(normalized.Building!.Buildings[0].HeroIds.Count == 0 && normalized.Bath!.HeroList.Count == 1,
        "settlement did not normalise building/bath membership");

    // 换秘书舰：旧秘书舰结算好感并清锚点，新秘书舰以新的 UpdateTime 为好感锚点。
    PlayerAccount switching = TimeTestAccount(
        [
            new Hero(1, 10210511, 1, UpdateTime: (int)T0, Affection: 500_000, Mood: 1_500_000),
            new Hero(2, 10210511, 1, UpdateTime: (int)T0, Affection: 500_000, Mood: 1_000_000),
        ], [], T0, secretaryId: 2);
    PlayerAccount switched = TimeSettlement.ChangeSecretary(switching, 1, 2, T0 + 21_610, rules, out IReadOnlySet<uint> switchedIds);
    Assert(TimeHero(switched, 1).Affection == 507_500 && TimeHero(switched, 1).AffectionSettledAt == 0,
        "the previous secretary's affection was not settled");
    Assert(TimeHero(switched, 2).AffectionSettledAt == TimeHero(switched, 2).UpdateTime &&
           TimeHero(switched, 2).UpdateTime == T0 + 21_600 && switchedIds.SetEquals([1u, 2u]),
        "the new secretary did not get a fresh affection anchor");
    return Task.CompletedTask;
}

static Task TimeSettlementCodecTest()
{
    BuildingConfigLoader.Load(FindClientConfigDir());
    var state = new PlayerBuilding(
        [new PlayerBuildingEntry(2, 45, 5, [7], LastUpdateTime: 1000, MoodSpeed: 34_000)],
        [new PlayerBuildingLand(6, 2)],
        WorkerUpdateTime: 2000);
    UserBuildingInfo info = BuildingService.ToProtocol(state, 5000);
    Assert(info.BuildingInfos![0].LastUpdateTime == 1000 && info.WorkerUpdateTime == 2000 &&
           info.NormalPlotUpdateTime == 5000 && info.BuildingInfos[0].ProduceSpeed == 34_000,
        "building snapshot did not send the persisted settlement anchors");
    byte[] encoded = PlayerDataCodec.Encode(info);
    Assert(ContainsSequence(encoded, new byte[] { 0x48, 0xE8, 0x07 }) &&
           ContainsSequence(encoded, new byte[] { 0x30, 0xD0, 0x89, 0x02 }) &&
           ContainsSequence(encoded, new byte[] { 0x50, 0x88, 0x27 }),
        "LastUpdateTime / ProduceSpeed / NormalPlotUpdateTime were not encoded");

    Assert(PlayerDataCodec.EncodeBathEndRet(660, 3600, 75)
            .SequenceEqual(new byte[] { 0x08, 0x94, 0x05, 0x10, 0x90, 0x1C, 0x18, 0x4B }),
        "TBathEndRet encoding mismatch");

    // 浴场快照第 i 个元素必须是 Pos=i 的舰娘，空浴位是空子消息（客户端 GetBathHero 依赖）。
    BathroomInfo slots = GameServices.ToBathroomInfo(new PlayerBath(
        [new BathHero(98, Pos: 4, StartTime: 10), new BathHero(99, Pos: 2, StartTime: 10)]));
    Assert(slots.HeroList!.Count == 4 &&
           slots.HeroList.Select((hero, index) => hero.HeroId == 0 || hero.Pos == index + 1).All(ok => ok) &&
           slots.HeroList[1].HeroId == 99 && slots.HeroList[3].HeroId == 98,
        "bath snapshot was not indexed by position");
    byte[] bathBytes = PlayerDataCodec.Encode(slots);
    Assert(bathBytes[0] == 0x0A && bathBytes[1] == 0x00, "an empty bath slot was not encoded as an empty message");
    Assert(PlayerDataCodec.Encode(GameServices.ToBathroomInfo(new PlayerBath([])))
            .SequenceEqual(new byte[] { 0x0A, 0x00 }),
        "an empty bathroom must still encode one HeroList element");

    Assert(PlayerDataCodec.DecodeHeroIdArrayArg(new byte[] { 0x0A, 0x02, 0x01, 0x02 }).SequenceEqual(new uint[] { 1, 2 }) &&
           PlayerDataCodec.DecodeHeroIdArrayArg(new byte[] { 0x08, 0x01, 0x08, 0x02 }).SequenceEqual(new uint[] { 1, 2 }) &&
           PlayerDataCodec.DecodeHeroIdArrayArg(null).Count == 0,
        "THeroArrayArg decoding failed");

    // 初值与旧档迁移：心情是万分制，满值 150 = 1500000。
    PlayerAccount created = PlayerAccountFactory.CreateDefault("mood", 1);
    Assert(created.Dock.Heroes[0].Mood == 1_500_000 && created.MoodVersion == PlayerAccountFactory.CurrentMoodVersion,
        "new profiles did not start at mood 150");
    PlayerAccount legacy = created with
    {
        MoodVersion = 0,
        Dock = new HeroDock(
        [
            new Hero(1, 10210511, 1, UpdateTime: 42, Mood: 100),
            new Hero(2, 10210511, 1, UpdateTime: 42, Mood: 10_000),
            new Hero(3, 10210511, 1, UpdateTime: 42, Mood: 777_777),
        ]),
    };
    PlayerAccount migrated = GameServices.MigrateMoodScale(legacy);
    Assert(migrated.Dock.Heroes.Select(hero => hero.Mood).SequenceEqual(new[] { 1_500_000, 1_500_000, 777_777 }) &&
           migrated.Dock.Heroes.All(hero => hero.UpdateTime == 42) && migrated.MoodVersion == 1,
        "legacy mood values were not migrated");
    Assert(ReferenceEquals(GameServices.MigrateMoodScale(migrated), migrated), "mood migration ran twice");
    return Task.CompletedTask;
}

static async Task TimeSettlementModuleTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-time-settlement-" + Guid.NewGuid().ToString("N"));
    const string profileId = "time-settlement";
    const int T0 = 1_800_000_000;
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, T0);
        seed = seed with
        {
            Dock = new HeroDock(
            [
                seed.Dock.Heroes[0],
                new Hero(2, 10210511, 1, CreateTime: T0, UpdateTime: T0, Mood: 100_000),
                new Hero(3, 10210511, 1, CreateTime: T0, UpdateTime: T0, Mood: 1_500_000),
                new Hero(4, 10210511, 1, CreateTime: T0, UpdateTime: T0, Mood: 1_500_000),
            ]),
            Building = seed.Building! with
            {
                Buildings =
                [
                    new PlayerBuildingEntry(1, 2, 2, [3], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                    new PlayerBuildingEntry(2, 41, 1, [2], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                    new PlayerBuildingEntry(3, 11, 1, [], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                ],
            },
        };
        await repo.SaveAccountAsync(seed);

        ServerOptions options = ServerOptions.Parse(
            ["--data=" + dataRoot,
             "--client-path=" + Path.Combine(root, "blueoath", "blueoath"),
             "--profile-id=" + profileId]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        var buildingModule = new BuildingModule(new BuildingService(services), services);
        var bathModule = new BathroomModule(services);
        GameContext At(int now) => new() { ProfileId = profileId, Now = now, Ct = CancellationToken.None, Services = services };
        static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;
        SettlementRules rules = services.SettlementRules;
        Assert(rules.MoodMax == 1_500_000 && rules.BathTickAdd == 40_000 && rules.BathTicketSeconds == 28_800,
            "settlement rules were not read from the JP client configuration");
        // 直接检查加载器（兜底值故意填错），确认数值来自配置库而不是默认值。
        Assert(ParameterCatalogLoader.GetArray(142, [9, 9]).SequenceEqual(new long[] { 0, 1_500_000 }) &&
               ParameterCatalogLoader.GetArray(150, [9, 9]).SequenceEqual(new long[] { 6, 7_500 }) &&
               BathroomItemLoader.Ticket is { Time: 28_800, Price: 25, Frequency: 300, OnceExp: 55 },
            "config_parameter arrays or config_bathroom_item were not loaded");

        // 心跳（打开基建页）：办公室扣心情、宿舍回心情，推送顺序为 建筑 → 舰娘 → 建筑。
        ModuleResult heartbeat = await buildingModule.HandleAsync(At(T0 + 3600), new TRequest("building.UpdateHeroAddition"));
        Assert(heartbeat.PrePushes.Select(Method).SequenceEqual(
                ["building.UpdateBuildingInfo", "hero.UpdateHeroBagData", "building.UpdateBuildingInfo"]),
            "settlement pushes were not ordered building → hero → building");
        PlayerAccount afterBeat = await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        int officeCost = checked((int)(3600L * BuildingConfigLoader.GetInfo(2)!.Moodcost / rules.CostUnit));
        Assert(TimeHero(afterBeat, 3).Mood == 1_500_000 - officeCost, "office hero mood was not settled on the heartbeat");
        int dormSpeed = afterBeat.Building!.Buildings.Single(b => b.Id == 2).MoodSpeed ?? -1;
        Assert(dormSpeed > 0 && TimeHero(afterBeat, 2).Mood == 100_000 + 1_000 + 3600 * dormSpeed / rules.RecoverUnit,
            "dormitory hero mood was not settled on the heartbeat");
        Assert(afterBeat.Building.Buildings.Single(b => b.Id == 1).LastUpdateTime == T0 + 3600 &&
               afterBeat.Building.Buildings.Single(b => b.Id == 3).LastUpdateTime == T0,
            "only occupied buildings should advance their settlement anchor");

        // 派驻：只重置成员变化的建筑；浴场里的舰娘不能派进建筑。
        var setHero = new ProtocolPackage().Write(0x08, 2UL).Write(0x10, 2UL).Write(0x10, 4UL);
        ModuleResult assigned = await buildingModule.HandleAsync(At(T0 + 3700), new TRequest("building.SetHero", setHero.ToArray()));
        PlayerAccount afterAssign = await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        Assert(assigned.Err == 0 && afterAssign.Building!.Buildings.Single(b => b.Id == 2).HeroIds.SequenceEqual(new uint[] { 2, 4 }) &&
               afterAssign.Building.Buildings.Single(b => b.Id == 3).LastUpdateTime == T0,
            "building.SetHero touched an unrelated building");

        // 入浴：从宿舍撤下，先结算再 +30；浴场快照按浴位编码。
        var bathStart = new ProtocolPackage().Write(0x08, 2UL).Write(0x10, 1UL);
        ModuleResult started = await bathModule.HandleAsync(At(T0 + 3800), new TRequest("bathroom.BathStart", bathStart.ToArray()));
        PlayerAccount afterBath = await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        Assert(started.Err == 0 && afterBath.Bath!.HeroList.Single().HeroId == 2 &&
               !afterBath.Building!.Buildings.Single(b => b.Id == 2).HeroIds.Contains(2u),
            "bathroom.BathStart did not move the hero from the dormitory into the bath");
        Assert(TimeHero(afterBath, 2).Mood > TimeHero(afterAssign, 2).Mood + rules.BathEnterAdd - 1,
            "bathroom.BathStart did not add the entry mood");
        Assert(started.PostPushes.Select(Method).SequenceEqual(["bathroom.BathroomInfo"]) &&
               started.PrePushes.Select(Method).Contains("hero.UpdateHeroBagData"),
            "bathroom.BathStart did not sync the hero and bathroom");

        // 已在池中：换浴位不重置浴券；占用的浴位拒绝新入浴（返回错误而不是断开连接）。
        var move = new ProtocolPackage().Write(0x08, 2UL).Write(0x10, 5UL);
        await bathModule.HandleAsync(At(T0 + 3810), new TRequest("bathroom.BathStart", move.ToArray()));
        BathHero moved = (await repo.LoadAccountAsync(profileId))!.Bath!.HeroList.Single();
        Assert(moved.Pos == 5 && moved.StartTime == T0 + 3800, "moving a bathing hero reset its ticket");
        var occupied = new ProtocolPackage().Write(0x08, 3UL).Write(0x10, 5UL);
        ModuleResult rejected = await bathModule.HandleAsync(At(T0 + 3820), new TRequest("bathroom.BathStart", occupied.ToArray()));
        Assert(rejected.Err != 0, "an occupied bath position accepted a new hero");
        ModuleResult missing = await bathModule.HandleAsync(At(T0 + 3820), new TRequest("bathroom.BathStart",
            new ProtocolPackage().Write(0x08, 999UL).Write(0x10, 1UL).ToArray()));
        Assert(missing.Err != 0, "an unknown hero did not produce a bathroom error");

        // 一键入浴顶替：被顶替者作为应答返回，新舰娘继承浴位与剩余浴券。
        var startAll = new ProtocolPackage().Write(0x0A, new ProtocolPackage().Write(0x08, 4UL).Write(0x10, 5UL).ToArray());
        ModuleResult replaced = await bathModule.HandleAsync(At(T0 + 3900), new TRequest("bathroom.BathStartAll", startAll.ToArray()));
        PlayerAccount afterAll = await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        BathHero heir = afterAll.Bath!.HeroList.Single();
        Assert(heir.HeroId == 4 && heir.Pos == 5 && heir.StartTime == T0 + 3800 &&
               ContainsSequence(replaced.Ret, new byte[] { 0x18, 0x02 }),
            "bathroom.BathStartAll did not replace the occupant and report it");

        // 出浴：真实入浴时长与经验（每 300 秒 55）。
        ModuleResult ended = await bathModule.HandleAsync(At(T0 + 3800 + 100 + 3600),
            new TRequest("bathroom.BathEnd", new ProtocolPackage().Write(0x08, 4UL).ToArray()));
        Assert(ended.Ret.SequenceEqual(PlayerDataCodec.EncodeBathEndRet(3600 / 300 * 55, 3600, 4)),
            "bathroom.BathEnd did not report the bath duration and experience");
        PlayerAccount afterEnd = await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        Assert(afterEnd.Bath!.HeroList.Count == 0 && (TimeHero(afterEnd, 4).Exp > 0 || TimeHero(afterEnd, 4).Level > 1),
            "bathroom.BathEnd did not grant the bath experience");

        // user.Refresh：有时间流逝时结算并推送（办公室里的舰娘 3 在消耗心情）。
        var userModule = new UserModule(new UserService(services), services);
        ModuleResult refreshed = await userModule.HandleAsync(At(T0 + 9000), new TRequest("user.Refresh"));
        Assert(refreshed.PrePushes.Select(Method).Contains("hero.UpdateHeroBagData") &&
               refreshed.PrePushes.Select(Method).Contains("building.UpdateBuildingInfo"),
            "user.Refresh did not settle and push the elapsed time");

        // 时钟回拨：派驻写入的新锚点不能早于结算高水位，否则这段时间会被重复结算。
        long highWater = (await repo.LoadAccountAsync(profileId))!.LastSettleTime;
        var toPower = new ProtocolPackage().Write(0x08, 3UL).Write(0x10, 3UL);
        ModuleResult rolledBack = await buildingModule.HandleAsync(At(T0 + 3000), new TRequest("building.SetHero", toPower.ToArray()));
        PlayerAccount afterRollback = await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        Assert(rolledBack.Err == 0 &&
               afterRollback.Building!.Buildings.Single(b => b.Id == 3).LastUpdateTime >= highWater &&
               afterRollback.Building.Buildings.Single(b => b.Id == 1).LastUpdateTime >= highWater &&
               afterRollback.Building.WorkerUpdateTime >= highWater,
            "a clock rollback wrote building anchors below the settlement high-water mark");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

// 起真实服务端：预写一份「一小时前派驻」的存档，验证 TCP 路径上的结算与推送顺序。
static async Task TimeSettlementIntegrationTest()
{
    var root = FindRepositoryRoot();
    var serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin", "Debug", "net8.0", "BlueOath.Server.dll");
    Assert(File.Exists(serverDll), "server assembly is missing; build the solution first");
    var data = Path.Combine(root, "test-time-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(data);
    const string profileId = "time-player";
    int seededAt = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 3600;
    var seedRepo = new SqliteGameRepository(data);
    PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, seededAt);
    seed = seed with
    {
        Dock = new HeroDock(
        [
            seed.Dock.Heroes[0],
            new Hero(2, 10210511, 1, CreateTime: seededAt, UpdateTime: seededAt, Mood: 1_500_000),
        ]),
        Building = seed.Building! with
        {
            Buildings =
            [
                new PlayerBuildingEntry(1, 2, 2, [2], LastUpdateTime: seededAt, LastBuildUpdateTime: seededAt),
                new PlayerBuildingEntry(2, 41, 1, [], LastUpdateTime: seededAt, LastBuildUpdateTime: seededAt),
                new PlayerBuildingEntry(3, 21, 1, [], Status: BuildingProduction.Working,
                    LastUpdateTime: seededAt, LastBuildUpdateTime: seededAt),
            ],
        },
    };
    await seedRepo.SaveAccountAsync(seed);

    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add(serverDll);
    startInfo.ArgumentList.Add("--port=0");
    startInfo.ArgumentList.Add("--game-login-port=0");
    startInfo.ArgumentList.Add("--region=jp");
    startInfo.ArgumentList.Add("--data=" + data);
    startInfo.ArgumentList.Add("--profile-id=" + profileId);
    startInfo.ArgumentList.Add("--client-path=" + Path.Combine(root, "blueoath", "blueoath"));
    using var process = new Process { StartInfo = startInfo };
    try
    {
        Assert(process.Start(), "time settlement test server did not start");
        var readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using var ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
        Assert(ready.RootElement.TryGetProperty("cheats", out JsonElement cheats) &&
               !cheats.GetProperty("production").GetBoolean() && !cheats.GetProperty("strength").GetBoolean() &&
               !cheats.GetProperty("vow").GetBoolean() && !cheats.GetProperty("mood").GetBoolean() &&
               !cheats.GetProperty("realResourceCost").GetBoolean() && !cheats.GetProperty("realShopStock").GetBoolean() &&
               !cheats.GetProperty("materials").GetBoolean(),
            "the ready JSON did not echo the (disabled) cheat switches");
        int port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
        var pushes = new List<TResponse>();
        TResponse heartbeat = await RoundTrip("building.UpdateHeroAddition", null, pushes);
        Assert(heartbeat.Err == 0, "building.UpdateHeroAddition returned an error");
        Assert(pushes.Select(p => p.Method).SequenceEqual(
                ["building.UpdateBuildingInfo", "hero.UpdateHeroBagData", "building.UpdateBuildingInfo"]),
            "heartbeat pushes were not ordered building → hero → building");

        PlayerAccount stored = await new SqliteGameRepository(data).LoadAccountAsync(profileId)
            ?? throw new InvalidDataException("time profile was not persisted");
        int mood = TimeHero(stored, 2).Mood;
        // 一小时（加上测试启动的几秒）× 每秒 17.5 的工作消耗。
        Assert(mood is <= 1_437_000 and >= 1_437_000 - 30 * 18,
            $"one hour of office work did not cost the expected mood (got {mood})");
        long anchor = stored.Building!.Buildings.Single(b => b.Id == 1).LastUpdateTime;
        Assert(anchor > seededAt && stored.Building.Buildings.Single(b => b.Id == 2).LastUpdateTime == seededAt,
            "only the occupied building should advance its anchor");
        Assert(ContainsSequence(pushes[0].Ret ?? [], EncodeTestVarintField(9, (ulong)anchor)),
            "the building push did not carry the persisted settlement anchor");

        TResponse refresh = await RoundTrip("user.Refresh", null);
        Assert(refresh.Err == 0, "user.Refresh returned an error");

        // 一小时前开工的石油精製工場：领取约 180 燃油，先推玩家信息再推建筑快照。
        var receivePushes = new List<TResponse>();
        TResponse received = await RoundTrip("building.ReceiveBuilding",
            new ProtocolPackage().Write(0x08, 3UL).ToArray(), receivePushes);
        int supply = (await new SqliteGameRepository(data).LoadAccountAsync(profileId))!.Character.Supply;
        Assert(received.Err == 0 && ContainsSequence(received.Ret ?? [], new byte[] { 0x0A, 0x09, 0x08, 0x05, 0x10, 0x05, 0x18 }) &&
               receivePushes.Select(p => p.Method).Take(2).SequenceEqual(["user.UpdateUserInfo", "building.UpdateBuildingInfo"]) &&
               supply is >= PlayerAccountFactory.DefaultSupply + 180 and <= PlayerAccountFactory.DefaultSupply + 182,
            $"building.ReceiveBuilding over TCP did not grant an hour of oil (supply {supply})");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}

static byte[] EncodeTestVarintField(int field, ulong value)
{
    var bytes = new List<byte>();
    AppendTestVarint(bytes, (ulong)(field << 3));
    AppendTestVarint(bytes, value);
    return bytes.ToArray();
}

static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

static Task VowCodecTest()
{
    byte[] snapshot = PlayerDataCodec.Encode(new IllustrateInfoRet(Vow: new VowSnapshot(
        300, 10210611, 2, [1021061, 1021051], [new VowItemUseInfo(110002, 2)])));
    Assert(Hex(snapshot) == "10AC0218B39AEF0420022885A93E28FBA83E3A0608B2DB061002",
        "IllustrateInfoRet vow fields were not encoded as expected: " + Hex(snapshot));
    Assert(!snapshot.Contains((byte)0x2A), "VowHeroList must not use packed encoding");
    Assert(Hex(PlayerDataCodec.Encode(new IllustrateInfoRet(Vow: new VowSnapshot()))) == "100018002000",
        "zero vow scalars must still be written");
    Assert(PlayerDataCodec.Encode(new IllustrateInfoRet(HeroMemoryList: [new HeroMemory(1, 2)]))[0] == 0x42,
        "IllustrateInfoRet without a vow snapshot changed its encoding");
    Assert(Hex(ProtocolEncoder.EncodeVowDecTimeRet(12, 1_800_496_800)) == "080C10A0CDC5DA06" &&
           Hex(ProtocolEncoder.EncodeVowDecTimeRet(0, 0)) == "08001000",
        "TVowDecTimeRet encoding mismatch");
    Assert(Hex(ProtocolEncoder.EncodeVowHeroRet(1, 18051, 30, 0)) == "080110838D01181E2000",
        "TVowHeroRet fragment encoding mismatch");
    Assert(ProtocolDecoder.DecodeChooseHeroList(Convert.FromHexString("0885A93E08FBA83E")).SequenceEqual([1021061, 1021051]) &&
           ProtocolDecoder.DecodeChooseHeroList(Convert.FromHexString("0A0685A93EFBA83E")).SequenceEqual([1021061, 1021051]),
        "ChooseHeroList decoding failed");
    var (useInfo, type) = ProtocolDecoder.DecodeVowDecTimeArgs(Convert.FromHexString("0A0608B2DB06100D1001"));
    Assert(useInfo.SequenceEqual([(110002, 13)]) && type == 1, "TVowDecTimeArgs decoding failed");
    return Task.CompletedTask;
}

static async Task<(GameServices Services, SqliteGameRepository Repo, string DataRoot)> VowTestServices(
    string profileId, Func<PlayerAccount, PlayerAccount> seed)
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-vow-" + Guid.NewGuid().ToString("N"));
    var repo = new SqliteGameRepository(dataRoot);
    await repo.SaveAccountAsync(seed(PlayerAccountFactory.CreateDefault(profileId, 1_800_000_000)));
    ServerOptions options = ServerOptions.Parse(
        ["--data=" + dataRoot, "--client-path=" + Path.Combine(root, "blueoath", "blueoath"), "--profile-id=" + profileId]);
    var services = new GameServices(repo, options, Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { }));
    return (services, repo, dataRoot);
}

static HeroDock VowTestDock() => new(
[
    new Hero(1, 10210511, 1),   // SR 1021051
    new Hero(2, 10210611, 1),   // SSR 1021061
    new Hero(3, 10210616, 1),   // 同上，满突破形态
    new Hero(4, 12750116, 1),   // ムーバー UR 1275011，突破 6（配置最大 7）
    new Hero(5, 10120211, 1),   // R，不可祈愿
    new Hero(6, 10210711, 1),   // is_wish 1 但 show_state 0
    new Hero(7, 10240311, 1),   // SSR，is_wish 0
]);

static async Task VowFormulaTest()
{
    const long T0 = 1_800_000_000;
    var (services, _, dataRoot) = await VowTestServices("vow-formula", account => account);
    try
    {
        VowRules rules = services.VowRules;
        Assert(rules.CapSr == 194_400 && rules.CapSsr == 777_600 && rules.CapUr == 1_036_800 && rules.SuperItemId == 10_008,
            "vow caps were not read from config_parameter");
        Assert(VowConfigLoader.GetVow(3) is { BanNotFullBreakAddTime: 14_400, BanFullBreakAddTime: 0, ResultThatQualityAddTime: 129_600 } &&
               VowConfigLoader.GetVow(4) is { BanNotFullBreakAddTime: 7_200, ResultThatQualityAddTime: 518_400 } &&
               VowConfigLoader.GetVow(5) is { BanNotFullBreakAddTime: 7_200, ResultThatQualityAddTime: 777_600 },
            "config_vow was not loaded by quality");
        Assert(VowConfigLoader.GetItem(110002) is { Type: 1, Time: 3_600, DailyLimit: 12 } &&
               VowConfigLoader.GetItem(110003) is { Type: 1, Time: 14_400 } &&
               VowConfigLoader.GetItem(10008) is null,
            "config_vow_item was not loaded");
        Assert(ShipMainLoader.MaxBreakLevel(1021061) == 6 && ShipMainLoader.MaxBreakLevel(1275011) == 7,
            "max break levels were not indexed by ship_info_id");

        HeroDock dock = VowTestDock();
        Assert(VowLogic.Candidates(dock, T0, rules).SequenceEqual([1021051, 1021061, 1275011]),
            "wish candidates mismatch: " + string.Join(",", VowLogic.Candidates(dock, T0, rules)));
        Assert(VowLogic.MaxAdvance(1021061, dock, rules) == 6 && VowLogic.MaxAdvance(1275011, dock, rules) == 6,
            "max advance by sf_id mismatch");
        Assert(VowLogic.BanAddTime(dock, [], T0, rules) == 21_600, "ban time with an empty wall mismatch");
        Assert(VowLogic.FinalChargeTime(dock, [1021061], 1021061, T0, rules) == 540_000 &&
               VowLogic.FinalChargeTime(dock, [1021051], 1021051, T0, rules) == 136_800 &&
               VowLogic.FinalChargeTime(dock, [1275011], 1275011, T0, rules) == 792_000,
            "vow cooldown formula mismatch");
        HeroDock fullBreak = dock with { Heroes = [.. dock.Heroes, new Hero(8, 12750127, 1)] };
        Assert(VowLogic.MaxAdvance(1275011, fullBreak, rules) == 7 &&
               VowLogic.FinalChargeTime(fullBreak, [1021061], 1021061, T0, rules) == 532_800,
            "a fully broken mubar ship should not add ban time");
        HeroDock srOnly = new([new Hero(1, 10130111, 1), new Hero(2, 10140111, 1), new Hero(3, 10210311, 1),
            new Hero(4, 10210511, 1), new Hero(5, 10310211, 1), new Hero(6, 10330111, 1)]);
        Assert(VowLogic.FinalChargeTime(srOnly, [1013011], 1013011, T0, rules) == 194_400, "SR cooldown cap was not applied");

        // 抽取：墙列表清洗、在可祈愿者中随机、冷却/无候选/船坞满/碎片。
        PlayerAccount account = PlayerAccountFactory.CreateDefault("vow", 1) with { Dock = dock };
        int seenCount = 0;
        VowPick picked = VowLogic.PickWish(account, [1012021, 1021061, 1021051, 1021061, 99999], T0, rules,
            n => { seenCount = n; return 1; });
        Assert(picked.Failure == VowFailure.None && picked.Wall.SequenceEqual([1012021, 1021061, 1021051]) &&
               seenCount == 2 && picked.ShipInfoId == 1021051 && picked.TemplateId == 10210511,
            "PickWish did not choose randomly among wishable wall entries");
        Assert(VowLogic.PickWish(account, [1012021], T0, rules, _ => 0).Failure == VowFailure.NoCandidate,
            "a wall without wishable ships was accepted");
        PlayerAccount cooling = account with { Vow = new PlayerVow(CoolTime: T0 + 60) };
        Assert(VowLogic.PickWish(cooling, [1021061], T0, rules, _ => 0).Failure == VowFailure.Cooling &&
               VowLogic.PickWish(cooling with { Vow = new PlayerVow(CoolTime: T0 + 2) }, [1021061], T0, rules, _ => 0).Failure == VowFailure.None,
            "cooldown check or its tolerance is wrong");
        Assert(VowLogic.PickWish(cooling, [1021061], T0, rules with { OmitCooldown = true }, _ => 0).Failure == VowFailure.None,
            "the vow cheat did not allow a wish during the cooldown");
        PlayerAccount full = account with { Dock = dock with { BagSize = 7 } };
        VowPick fragment = VowLogic.PickWish(full, [1275011], T0, rules, _ => 0);
        Assert(VowLogic.PickWish(full, [1021061], T0, rules, _ => 0).Failure == VowFailure.DockFull &&
               fragment is { Failure: VowFailure.None, IsFragment: true, FragmentItem: 18051, FragmentNum: 30, TemplateId: 12750111 },
            "dock-full or mubar fragment handling is wrong");

        // 用石减冷却（now = T0+100，冷却舰娘 1021061）。
        PlayerAccount stones = account with
        {
            Vow = new PlayerVow(CoolTime: T0 + 540_000, CoolHero: 10210611, UseResetDay: VowLogic.Day(T0)),
            Bag = new PlayerBag([new BagItem(110002, 20), new BagItem(110003, 3), new BagItem(10008, 1),
                new BagItem(110013, 1), new BagItem(110030, 2)]),
        };
        static int Bag(PlayerAccount a, int tid) => a.Bag!.Items.Single(i => i.TemplateId == tid).Num;
        VowDecResult a = VowLogic.DecTime(stones, [(110002, 13)], T0 + 100, rules);
        Assert(a.Changed && a.Account.Vow!.CoolTime == T0 + 496_800 && Bag(a.Account, 110002) == 8 &&
               a.Account.Vow.UseInfo!.SequenceEqual([new VowItemUse(110002, 12)]) && a.Account.Vow.Count == 12,
            "daily-limited stone usage mismatch");
        VowDecResult b = VowLogic.DecTime(a.Account, [(110002, 1)], T0 + 100, rules);
        Assert(!b.Changed && ReferenceEquals(b.Account, a.Account), "a stone beyond its daily limit was consumed");
        VowDecResult c = VowLogic.DecTime(b.Account, [(110003, 2)], T0 + 100, rules);
        Assert(c.Account.Vow!.CoolTime == T0 + 468_000 && Bag(c.Account, 110003) == 1 && c.Account.Vow.Count == 14,
            "unlimited stone usage mismatch");
        VowDecResult d = VowLogic.DecTime(c.Account, [(110030, 1)], T0 + 100, rules);
        Assert(!d.Changed && Bag(d.Account, 110030) == 2, "a ship-bound stone for another ship was consumed");
        VowDecResult e = VowLogic.DecTime(d.Account, [(110013, 1)], T0 + 100, rules);
        Assert(e.Account.Vow!.CoolTime == T0 + 453_600 && e.Account.Vow.Count == 15, "ship-bound stone usage mismatch");
        VowDecResult f = VowLogic.DecTime(e.Account, [(10008, 1)], T0 + 200, rules);
        Assert(f.Account.Vow!.CoolTime == T0 + 200 && Bag(f.Account, 10008) == 0 && f.Account.Vow.Count == 16 &&
               f.Account.Vow.UseInfo!.Any(u => u.ItemTid == 10008 && u.ItemNum == 1),
            "the super stone did not clear the cooldown");
        Assert(!VowLogic.DecTime(f.Account, [(110003, 1)], T0 + 200, rules).Changed, "stones were consumed without a cooldown");
        PlayerAccount waste = c.Account with { Vow = c.Account.Vow! with { CoolTime = T0 + 5_100 }, Bag = new PlayerBag([new BagItem(110003, 3)]) };
        VowDecResult h = VowLogic.DecTime(waste, [(110003, 3)], T0 + 100, rules);
        Assert(h.Account.Vow!.CoolTime == T0 + 100 && Bag(h.Account, 110003) == 0,
            "a confirmed wasteful request must consume every requested stone");

        // 每日重置（UTC+8 0 点 = T0+28800）：用量置 0 而不是删除，冷却与墙不变；时钟回拨不重复重置。
        PlayerAccount daily = PlayerAccountFactory.CreateDefault("vow-daily", (int)T0) with
        {
            Vow = new PlayerVow(CoolTime: T0 + 496_800, HeroList: [1021061], Count: 12,
                UseInfo: [new VowItemUse(110002, 12)], UseResetDay: VowLogic.Day(T0)),
        };
        SettlementRules settleRules = services.SettlementRules;
        SettlementResult before = TimeSettlement.Settle(daily, T0 + 28_799, settleRules);
        Assert(!before.Changed && ReferenceEquals(before.Account, daily), "daily reset fired before midnight UTC+8");
        SettlementResult reset = TimeSettlement.Settle(daily, T0 + 28_800, settleRules);
        Assert(reset.VowChanged && reset.Account.Vow!.Count == 0 &&
               reset.Account.Vow.UseInfo!.SequenceEqual([new VowItemUse(110002, 0)]) &&
               reset.Account.Vow.CoolTime == T0 + 496_800 && reset.Account.Vow.HeroList!.SequenceEqual([1021061]) &&
               reset.Account.Vow.UseResetDay == VowLogic.Day(T0) + 1,
            "the daily reset did not zero the stone usage");
        Assert(!TimeSettlement.Settle(reset.Account, T0 + 28_900, settleRules).Changed, "the daily reset ran twice");
        Assert(!TimeSettlement.Settle(reset.Account with { LastSettleTime = T0 + 28_800 }, T0, settleRules).Changed,
            "a clock rollback re-ran the daily reset");
        // 当日计数已记在更晚的日期上（时钟回拨后首次结算，高水位为 0）：日期只进不退，不能清零。
        PlayerAccount ahead = daily with
        {
            Vow = daily.Vow! with { UseResetDay = VowLogic.Day(T0) + 1 },
        };
        Assert(!TimeSettlement.Settle(ahead, T0, settleRules).Changed,
            "a reset day ahead of the clock was moved backwards");
        Assert(VowLogic.Snapshot(daily.Vow, T0 + 28_800).UseInfo!.All(u => u.ItemNum == 0) && daily.Vow!.Count == 12,
            "the push snapshot did not present the next day's view");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

static async Task VowModuleTest()
{
    const int T0 = 1_800_000_000;
    const string profileId = "vow-module";
    var (services, repo, dataRoot) = await VowTestServices(profileId, account => account with
    {
        Dock = VowTestDock() with { Heroes = [account.Dock.Heroes[0] with { HeroId = 100 }, .. VowTestDock().Heroes] },
        Character = account.Character with { SecretaryId = 100 },
        Bag = new PlayerBag([new BagItem(110002, 20), new BagItem(10008, 1)]),
    });
    try
    {
        var module = new BuildShipModule(new BuildShipService(services), services, BuildPoolsConfigLoader.Load());
        GameContext At(int now) => new() { ProfileId = profileId, Now = now, Ct = CancellationToken.None, Services = services };
        static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;
        static byte[] RetOf(byte[] push) => TMessageCodec.DecodeResponse(push).Ret ?? [];
        byte[] wallBytes = Convert.FromHexString("2885A93E28FBA83E");

        ModuleResult wall = await module.HandleAsync(At(T0), new TRequest("illustrate.ModiVowHeroList",
            Convert.FromHexString("0885A93E08FBA83E")));
        Assert(wall.Err == 0 && (await repo.LoadAccountAsync(profileId))!.Vow!.HeroList!.SequenceEqual([1021061, 1021051]),
            "illustrate.ModiVowHeroList did not persist the wall");
        byte[] loginIllustrate = (await services.BuildSyncPushesAsync(profileId, T0 + 10, CancellationToken.None))
            .First(push => Method(push) == "illustrate.IllustrateInfo");
        Assert(ContainsSequence(RetOf(loginIllustrate), wallBytes), "the login illustrate push did not restore the wall");

        int dockBefore = (await repo.LoadAccountAsync(profileId))!.Dock.Heroes.Count;
        ModuleResult wished = await module.HandleAsync(At(T0 + 20), new TRequest("illustrate.VowHero",
            Convert.FromHexString("0885A93E")));
        PlayerAccount afterWish = (await repo.LoadAccountAsync(profileId))!;
        Hero newHero = afterWish.Dock.Heroes[^1];
        Assert(wished.Err == 0 && afterWish.Dock.Heroes.Count == dockBefore + 1 && newHero.TemplateId == 10210611 &&
               wished.Ret.SequenceEqual(ProtocolEncoder.EncodeVowHeroRet(3, 10210611, 1, checked((int)newHero.HeroId))),
            "illustrate.VowHero did not add the wished ship");
        Assert(wished.PrePushes.Select(Method).SkipWhile(m => m != "hero.UpdateHeroBagData")
                .SequenceEqual(["hero.UpdateHeroBagData", "equip.UpdateEquipBagData", "illustrate.IllustrateInfo"]),
            "VowHero pushes were not ordered hero → equip → illustrate before the response");
        long expectedCool = T0 + 20 + VowLogic.FinalChargeTime(afterWish.Dock, [1021061], 1021061, T0 + 20, services.VowRules);
        Assert(afterWish.Vow!.CoolTime == expectedCool && afterWish.Vow.CoolHero == 10210611 &&
               ContainsSequence(RetOf(wished.PrePushes[^1]), Convert.FromHexString("18B39AEF04")),
            "the vow cooldown was not persisted and pushed before the response");

        ModuleResult cooling = await module.HandleAsync(At(T0 + 30), new TRequest("illustrate.VowHero",
            Convert.FromHexString("08FBA83E")));
        Assert(cooling.Err != 0 && (await repo.LoadAccountAsync(profileId))!.Dock.Heroes.Count == dockBefore + 1 &&
               cooling.PrePushes.Select(Method).Contains("illustrate.IllustrateInfo"),
            "a wish during the cooldown was not rejected with a corrective snapshot");

        ModuleResult dec = await module.HandleAsync(At(T0 + 40), new TRequest("illustrate.VowDecTime",
            Convert.FromHexString("0A0608B2DB06100D1001")));
        PlayerAccount afterDec = (await repo.LoadAccountAsync(profileId))!;
        Assert(dec.Ret.SequenceEqual(ProtocolEncoder.EncodeVowDecTimeRet(12, expectedCool - 12 * 3600)) &&
               afterDec.Bag!.Items.Single(i => i.TemplateId == 110002).Num == 8 &&
               dec.PrePushes.Select(Method).SkipWhile(m => m != "bag.UpdateBagData")
                   .SequenceEqual(["bag.UpdateBagData", "illustrate.IllustrateInfo"]),
            "illustrate.VowDecTime did not consume stones and push the new cooldown before the response");

        // 跨日后的 user.Refresh 落盘重置并补发祈愿快照。
        var userModule = new UserModule(new UserService(services), services);
        ModuleResult refreshed = await userModule.HandleAsync(At(T0 + 28_800), new TRequest("user.Refresh"));
        PlayerAccount afterReset = (await repo.LoadAccountAsync(profileId))!;
        Assert(refreshed.PostPushes.Select(Method).Contains("illustrate.IllustrateInfo") &&
               afterReset.Vow!.Count == 0 && afterReset.Vow.UseInfo!.All(u => u.ItemNum == 0),
            "the daily reset was not persisted and pushed");

        // 购买等其它图鉴推送不能清空墙。
        byte[] buyIllustrate = (await services.BuildBuyPushesAsync(profileId, T0 + 28_900, CancellationToken.None,
                newShipTemplateIds: [40110311]))
            .First(push => Method(push) == "illustrate.IllustrateInfo");
        Assert(ContainsSequence(RetOf(buyIllustrate), Convert.FromHexString("2885A93E")),
            "a purchase illustrate push cleared the wish wall");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

// ───────────────────────── 基建生产 ─────────────────────────

// 日服 1.4.0 实测的建筑与配方数值（只注入测试用到的条目）。
static SettlementRules ProductionTestRules(
    Func<int, int, int>? heroAddition = null, Func<int, int, int>? recipeAddition = null)
{
    var infos = new Dictionary<int, ConfigBuildinginfo>
    {
        [5] = new() { Id = 5, Type = 1, Level = 5, Moodcost = 10_500 },
        [21] = new() { Id = 21, Type = 3, Level = 1, Productid = [5, 5], Productivity = 300_000, Productmax = 4_200, Moodcost = 10_500 },
        [22] = new() { Id = 22, Type = 3, Level = 2, Productid = [5, 5], Productivity = 310_000, Productmax = 5_400, Moodcost = 10_500 },
        [25] = new() { Id = 25, Type = 3, Level = 5, Productid = [5, 5], Productivity = 340_000, Productmax = 9_000, Moodcost = 10_500 },
        [31] = new() { Id = 31, Type = 4, Level = 1, Productid = [5, 1], Productivity = 2_000_000, Productmax = 50_000, Moodcost = 10_500, Reducecost = 1_000 },
        [45] = new() { Id = 45, Type = 5, Level = 5, Addmood = 34_000 },
        [61] = new() { Id = 61, Type = 7, Level = 1, Productmax = 100, Moodcost = 10_500, Recipeid = [1, 2, 3, 4, 5], RecipeCompose = [3, 4, 5, 6, 7, 8, 13] },
        [11] = new() { Id = 11, Type = 2, Level = 1, Moodcost = 10_500, Addworkerhp = 8_300 },
        [15] = new() { Id = 15, Type = 2, Level = 5, Moodcost = 10_500, Addworkerhp = 10_000 },
    };
    var recipes = new Dictionary<int, ConfigRecipe>
    {
        [2] = new() { Id = 2, Type = 4, Item = [6, 60000, 1], Time = 1_800, CostEnergy = 62_500, Unlocklevel = 1 },
        [3] = new() { Id = 3, Type = 5, Item = [1, 10182, 1], Time = 1_800, CostEnergy = 62_500, Unlocklevel = 1 },
        [5] = new() { Id = 5, Type = 6, Item = [1, 14001, 1], Time = 30, CostEnergy = 0, Unlocklevel = 1, Hide = 1, Rawmaterial2 = [5, 21, 6] },
        [6] = new() { Id = 6, Type = 1, Item = [1, 10000, 1], Time = 9_000, CostEnergy = 312_500, Unlocklevel = 2 },
        [7] = new() { Id = 7, Type = 4, Item = [5, 999, 1], Time = 1_800, CostEnergy = 62_500, Unlocklevel = 1 }, // 产出未知货币
    };
    var composes = new Dictionary<int, ConfigRecipeCompose>
    {
        [3] = new() { Id = 3, Type = 6, Item = [1, 10182, 2], Rawmaterial1 = [1, 10185, 3], Unlocklevel = 1 },
        [13] = new() { Id = 13, Type = 6, Item = [1, 14001, 1], Rawmaterial1 = [5, 21, 6], Unlocklevel = 1 },
    };
    return new SettlementRules
    {
        ApplyTavernDiscount = false,
        BuildingInfo = tid => infos.GetValueOrDefault(tid),
        HeroAddition = heroAddition ?? ((_, _) => 0),
        RecipeAddition = recipeAddition ?? ((_, _) => 0),
        Recipe = id => recipes.GetValueOrDefault(id),
        Compose = id => composes.GetValueOrDefault(id),
        SupplyMax = _ => 10_000,
        // 日服 config_worker：workerhpmax 50，每级办公室 +50。
        MaxWorkerStrength = level => 50 + 50 * Math.Clamp(level, 0, 5),
        WorkerBaseRecover = 0,
    };
}

static PlayerAccount ProductionTestAccount(
    IReadOnlyList<Hero> heroes,
    IReadOnlyList<PlayerBuildingEntry> buildings,
    long workerUpdateTime,
    int supply = 9_999,
    int productionVersion = BuildingProduction.CurrentVersion,
    int workerStrength = 1_000_000)
{
    PlayerAccount account = PlayerAccountFactory.CreateDefault("production", 1);
    return account with
    {
        Character = account.Character with { SecretaryId = 999, Supply = supply, Gold = 0 },
        Dock = new HeroDock(heroes),
        Bag = new PlayerBag([]),
        Building = new PlayerBuilding(buildings, [], WorkerStrength: workerStrength,
            WorkerUpdateTime: workerUpdateTime, ProductionVersion: productionVersion),
    };
}

static PlayerBuildingEntry ProdEntry(PlayerAccount account, int id) => account.Building!.Buildings.Single(b => b.Id == id);

static int ProdBag(PlayerAccount account, int templateId) =>
    account.Bag?.Items.FirstOrDefault(item => item.TemplateId == templateId)?.Num ?? 0;

static int IndexOfSequence(byte[] haystack, byte[] needle)
{
    for (int i = 0; i + needle.Length <= haystack.Length; i++)
        if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
    return -1;
}

static Task BuildingProductionFormulaTest()
{
    const long T0 = 1_800_000_000;
    const int Idle = BuildingProduction.Idle;
    const int Working = BuildingProduction.Working;
    BuildingConfigLoader.Load(FindClientConfigDir());
    SettlementRules rules = ProductionTestRules();

    // 资源楼：石油精製工場 1 级每 600 秒 30 燃油，按精确秒数烘焙并推进锚点。
    PlayerAccount oil = ProductionTestAccount([],
        [new PlayerBuildingEntry(1, 21, 1, [], Status: Working, LastUpdateTime: T0)], T0);
    SettlementResult hour = TimeSettlement.Settle(oil, T0 + 3600, rules);
    PlayerBuildingEntry baked = ProdEntry(hour.Account, 1);
    Assert(hour.BuildingChanged && baked.ProductCount == 180 && baked.Progress == 0 && baked.Status == Working &&
           baked.LastUpdateTime == T0 + 3600,
        "an hour of oil production did not match the client formula");

    // 小数结转：每 50 秒结算一次（每次 2.5）与一次结算 500 秒结果相同。
    PlayerAccount stepped = oil;
    for (int i = 1; i <= 10; i++) stepped = TimeSettlement.Settle(stepped, T0 + 50 * i, rules).Account;
    PlayerBuildingEntry oneShot = ProdEntry(TimeSettlement.Settle(oil, T0 + 500, rules).Account, 1);
    Assert(ProdEntry(stepped, 1).ProductCount == 25 && oneShot.ProductCount == 25 &&
           Math.Abs(ProdEntry(stepped, 1).Progress) < 1e-9 && Math.Abs(oneShot.Progress) < 1e-9,
        "frequent settlements lost the fractional production");

    // 满仓：封顶 productmax 并转为 Idle；满仓后不再产出、不推进锚点。
    PlayerAccount nearlyFull = ProductionTestAccount([],
        [new PlayerBuildingEntry(1, 21, 1, [], Status: Working, LastUpdateTime: T0, ProductCount: 4_190)], T0);
    SettlementResult filled = TimeSettlement.Settle(nearlyFull, T0 + 600, rules);
    Assert(ProdEntry(filled.Account, 1) is { ProductCount: 4_200, Status: Idle } && ProdEntry(filled.Account, 1).Progress == 0,
        "a resource building did not stop at its capacity");
    SettlementResult stillFull = TimeSettlement.Settle(filled.Account, T0 + 4_200, rules);
    Assert(!stillFull.Changed && ProdEntry(stillFull.Account, 1).LastUpdateTime == T0 + 600,
        "a full resource building kept producing or moved its anchor");

    // 资金楼（真夜中居酒屋）：驻守舰娘只在加成窗口内加成，办公室舰娘同样加成资金。
    Func<int, int, int> addition = (tpl, type) =>
        tpl == 222 && type == 4 ? 600 : tpl == 111 && type == 1 ? 200 : tpl == 333 && type == 3 ? 100 : 0;
    SettlementRules bonusRules = ProductionTestRules(addition);
    PlayerAccount gold = ProductionTestAccount(
        [new Hero(1, 111, 1, UpdateTime: (int)T0, Mood: 1_500_000), new Hero(2, 222, 1, UpdateTime: (int)T0, Mood: 31_500)],
        [
            new PlayerBuildingEntry(1, 5, 5, [1], LastUpdateTime: T0, HeroWindows: [new HeroEffectWindow(1, T0, T0 + 85_715)]),
            new PlayerBuildingEntry(2, 31, 1, [2], Status: Working, LastUpdateTime: T0,
                HeroWindows: [new HeroEffectWindow(2, T0, T0 + 1_800)]),
        ], T0);
    SettlementResult goldHour = TimeSettlement.Settle(gold, T0 + 3600, bonusRules);
    PlayerBuildingEntry tavern = ProdEntry(goldHour.Account, 2);
    PlayerBuildingEntry office = ProdEntry(goldHour.Account, 1);
    Assert(tavern.ProductCount == 1_260, $"gold production with resident and office bonuses mismatch (got {tavern.ProductCount})");
    Assert(TimeHero(goldHour.Account, 2).Mood == 0 && tavern.HeroWindows is { Count: 0 } && tavern.Productivity == 10_000,
        "an exhausted resident still produced a bonus window or productivity");
    Assert(TimeHero(goldHour.Account, 1).Mood == 1_437_000 &&
           office.HeroWindows!.SequenceEqual([new HeroEffectWindow(1, T0 + 3600, T0 + 85_715)]) && office.Productivity == 10_200,
        "the office window or productivity was not recomputed after settlement");
    UserBuildingInfo goldInfo = BuildingService.ToProtocol(goldHour.Account.Building, (int)(T0 + 3600));
    Assert(goldInfo.BuildingInfos!.Single(b => b.Id == 2).ProduceSpeed == 2_040_000,
        "the gold ProduceSpeed did not include the office productivity");

    // 烘焙必须在心情结算之前用旧锚点：产出与心情都不能丢。
    PlayerAccount resident = ProductionTestAccount(
        [new Hero(3, 333, 1, UpdateTime: (int)T0, Mood: 1_500_000)],
        [new PlayerBuildingEntry(1, 21, 1, [3], Status: Working, LastUpdateTime: T0,
            HeroWindows: [new HeroEffectWindow(3, T0, T0 + 85_715)])], T0);
    SettlementResult residentHour = TimeSettlement.Settle(resident, T0 + 3600, bonusRules);
    PlayerBuildingEntry residentOil = ProdEntry(residentHour.Account, 1);
    Assert(residentOil.ProductCount == 181 && Math.Abs(residentOil.Progress - 0.8) < 1e-9 &&
           TimeHero(residentHour.Account, 3).Mood == 1_437_000 && residentOil.LastUpdateTime == T0 + 3600,
        "resident production or mood cost was lost when both settled together");
    SettlementResult residentAgain = TimeSettlement.Settle(residentHour.Account, T0 + 3600, bonusRules);
    Assert(!residentAgain.Changed && ReferenceEquals(residentAgain.Account, residentHour.Account),
        "production settlement was not idempotent");

    // 加成窗口终点 = 心情归零时刻（含自然恢复与誓约加成）。
    Hero full = new(10, 10210511, 1, UpdateTime: (int)T0, Mood: 1_500_000);
    Hero thousand = new(11, 10210511, 1, UpdateTime: (int)T0, Mood: 1_000_000);
    Assert(BuildingProduction.MoodZeroTime(full, T0, 17.5, rules) == T0 + 85_715 &&
           BuildingProduction.MoodZeroTime(full, T0, 8.75, rules) == T0 + 171_429 &&
           BuildingProduction.MoodZeroTime(thousand, T0, 17.5, rules) == T0 + 58_063 &&
           BuildingProduction.MoodZeroTime(thousand with { MarryTime = 1 }, T0, 17.5, rules) == T0 + 59_006 &&
           BuildingProduction.MoodZeroTime(thousand with { Mood = 0 }, T0, 17.5, rules) is null,
        "the bonus window end did not match the mood-zero time");
    PlayerAccount officeSettled = TimeSettlement.Settle(
        ProductionTestAccount([thousand], [new PlayerBuildingEntry(1, 5, 5, [11], LastUpdateTime: T0)], T0), T0 + 3600, rules).Account;
    Assert(TimeHero(officeSettled, 11).Mood == 938_000 &&
           ProdEntry(officeSettled, 1).HeroWindows!.SequenceEqual([new HeroEffectWindow(11, T0 + 3600, T0 + 58_063)]),
        "a recomputed window did not keep the same mood-zero time");
    PlayerAccount dormSettled = TimeSettlement.Settle(
        ProductionTestAccount([full], [new PlayerBuildingEntry(1, 45, 5, [10], LastUpdateTime: T0)], T0), T0 + 600, rules).Account;
    Assert(ProdEntry(dormSettled, 1).HeroWindows is { Count: 0 }, "a dormitory sent a bonus window");

    // 道具工厂：按配方时长逐件完成，进度以 FloatCount（×1e4）下发。
    PlayerAccount factory = ProductionTestAccount([],
        [new PlayerBuildingEntry(1, 61, 1, [], Status: Working, LastUpdateTime: T0, RecipeId: 2, ItemCount: 2)], T0);
    PlayerAccount partial = TimeSettlement.Settle(factory, T0 + 2700, rules).Account;
    PlayerBuildingEntry partialEntry = ProdEntry(partial, 1);
    Assert(partialEntry is { ProductCount: 1, ItemCount: 1, Status: Working } && Math.Abs(partialEntry.Progress - 0.5) < 1e-9,
        "item factory progress mismatch");
    BuildingInfo partialInfo = BuildingService.ToProtocol(partial.Building, (int)(T0 + 2700)).BuildingInfos!.Single();
    Assert(partialInfo.FloatCount == 5_000 &&
           ContainsSequence(PlayerDataCodec.Encode(new UserBuildingInfo(BuildingInfos: [partialInfo])), new byte[] { 0x80, 0x01, 0x88, 0x27 }),
        "item factory progress was not sent as FloatCount");
    PlayerBuildingEntry finished = ProdEntry(TimeSettlement.Settle(partial, T0 + 3600, rules).Account, 1);
    Assert(finished is { ProductCount: 2, ItemCount: 0, Status: Idle, RecipeId: 2 } && finished.Progress == 0,
        "a finished queue did not become idle with its products kept");

    SettlementRules recipeRules = ProductionTestRules(addition, (tpl, type) => tpl == 444 && type == 4 ? 1_000 : 0);
    PlayerAccount staffed = ProductionTestAccount([new Hero(4, 444, 1, UpdateTime: (int)T0, Mood: 1_500_000)],
        [new PlayerBuildingEntry(1, 61, 1, [4], Status: Working, LastUpdateTime: T0, RecipeId: 2, ItemCount: 5,
            HeroWindows: [new HeroEffectWindow(4, T0, T0 + 85_715)])], T0);
    PlayerBuildingEntry staffedEntry = ProdEntry(TimeSettlement.Settle(staffed, T0 + 1800, recipeRules).Account, 1);
    Assert(staffedEntry is { ProductCount: 1, ItemCount: 4 } && Math.Abs(staffedEntry.Progress - 0.1) < 1e-9,
        "the resident recipe bonus was not applied");
    PlayerAccount officeBoost = ProductionTestAccount([new Hero(1, 111, 1, UpdateTime: (int)T0, Mood: 1_500_000)],
        [
            new PlayerBuildingEntry(1, 5, 5, [1], LastUpdateTime: T0, HeroWindows: [new HeroEffectWindow(1, T0, T0 + 85_715)]),
            new PlayerBuildingEntry(2, 61, 1, [], Status: Working, LastUpdateTime: T0, RecipeId: 2, ItemCount: 5),
        ], T0);
    PlayerBuildingEntry boosted = ProdEntry(TimeSettlement.Settle(officeBoost, T0 + 1800, recipeRules).Account, 2);
    Assert(boosted.ProductCount == 1 && Math.Abs(boosted.Progress - 0.02) < 1e-9, "the office bonus was not applied to the factory");

    // 旧档迁移：资源楼从 Idle 转为 Working 并按存档锚点追溯，封顶 productmax；道具工厂不动。
    PlayerAccount legacy = ProductionTestAccount([],
        [
            new PlayerBuildingEntry(1, 5, 5, [], LastUpdateTime: T0),
            new PlayerBuildingEntry(2, 25, 5, [], Status: Idle, LastUpdateTime: T0),
            new PlayerBuildingEntry(3, 31, 1, [], Status: Idle, LastUpdateTime: T0),
            new PlayerBuildingEntry(4, 61, 1, [], Status: Idle, LastUpdateTime: T0),
        ], T0, productionVersion: 0);
    SettlementResult migrated = TimeSettlement.Settle(legacy, T0 + 86_400, rules);
    Assert(migrated.BuildingChanged && migrated.Account.Building!.ProductionVersion == BuildingProduction.CurrentVersion &&
           ProdEntry(migrated.Account, 2) is { ProductCount: 4_896, Status: Working } &&
           ProdEntry(migrated.Account, 3) is { ProductCount: 28_800, Status: Working } &&
           ProdEntry(migrated.Account, 4) is { ProductCount: 0, Status: Idle },
        "legacy resource buildings were not migrated to timed production");
    Assert(!TimeSettlement.Settle(migrated.Account, T0 + 86_400, rules).Changed, "production migration ran twice");
    ConfigBuildinginfo oilMax = rules.BuildingInfo(25)!;
    Assert(BuildingProduction.StatusAfterLevelChange(new PlayerBuildingEntry(1, 21, 1, [], ProductCount: 4_200), oilMax) == Working &&
           BuildingProduction.StatusAfterLevelChange(new PlayerBuildingEntry(1, 21, 1, [], ProductCount: 9_000), oilMax) == Idle &&
           BuildingProduction.StatusAfterLevelChange(new PlayerBuildingEntry(1, 61, 1, [], ItemCount: 2), rules.BuildingInfo(61)!) == Working &&
           BuildingProduction.StatusAfterLevelChange(new PlayerBuildingEntry(1, 5, 5, []), rules.BuildingInfo(5)!) == Idle,
        "the status after a level change mismatch");

    // 下单（ProduceItem）：Count 为剩余件数的绝对值。
    PlayerAccount idleFactory = ProductionTestAccount([],
        [
            new PlayerBuildingEntry(1, 21, 1, [], Status: Working, LastUpdateTime: T0),
            new PlayerBuildingEntry(2, 61, 1, [], LastUpdateTime: T0 - 100),
        ], T0);
    PlayerBuildingEntry factoryEntry = ProdEntry(idleFactory, 2);
    BuildingProduction.Outcome ordered = BuildingProduction.Order(idleFactory, 2, 2, 3, T0, rules);
    Assert(ordered.Success && ordered.Rewards.Count == 0 &&
           ProdEntry(ordered.Account, 2) is { RecipeId: 2, ItemCount: 3, Status: Working, LastUpdateTime: T0 },
        "building.ProduceItem did not start the queue");
    PlayerAccount almostFull = BuildingProduction.Replace(idleFactory, factoryEntry with { ProductCount = 98, RecipeId = 2 });
    Assert(ProdEntry(BuildingProduction.Order(almostFull, 2, 2, 5, T0, rules).Account, 2).ItemCount == 2,
        "the queue was not capped by the warehouse capacity");
    PlayerAccount running = BuildingProduction.Replace(idleFactory, factoryEntry with
    {
        Status = Working, RecipeId = 2, ItemCount = 1, Progress = 0.5, ProductCount = 1, LastUpdateTime = T0,
    });
    PlayerBuildingEntry extended = ProdEntry(BuildingProduction.Order(running, 2, 2, 4, T0, rules).Account, 2);
    Assert(extended is { ItemCount: 4, Status: Working } && extended.Progress == 0.5,
        "extending the same recipe reset the current item progress");
    PlayerBuildingEntry cancelled = ProdEntry(BuildingProduction.Order(running, 2, 2, 0, T0, rules).Account, 2);
    Assert(cancelled is { ItemCount: 0, Status: Idle, ProductCount: 1 } && cancelled.Progress == 0,
        "cancelling the queue did not keep the finished products");
    PlayerAccount withProducts = BuildingProduction.Replace(idleFactory, factoryEntry with
    {
        Status = Working, RecipeId = 2, ItemCount = 3, ProductCount = 2, LastUpdateTime = T0,
    });
    BuildingProduction.Outcome switched = BuildingProduction.Order(withProducts, 2, 3, 1, T0, rules);
    Assert(switched.Success && switched.BagChanged && switched.Rewards.SequenceEqual([new CommonReward(6, 60000, 2)]) &&
           ProdBag(switched.Account, 60000) == ProdBag(withProducts, 60000) + 2 &&
           ProdEntry(switched.Account, 2) is { ProductCount: 0, RecipeId: 3, ItemCount: 1, Status: Working } &&
           ProdEntry(switched.Account, 2).Progress == 0 &&
           ProtocolEncoder.EncodeReceiveRet(switched.Rewards).SequenceEqual(
               new byte[] { 0x0A, 0x0A, 0x08, 0x06, 0x10, 0xE0, 0xD4, 0x03, 0x18, 0x02, 0x20, 0x00 }),
        "switching recipes did not auto-receive the old products");
    foreach (var (recipeId, count, target) in new[] { (5, 1, 2), (6, 1, 2), (3, 0, 2), (2, 1, 1) })
    {
        BuildingProduction.Outcome rejected = BuildingProduction.Order(running, target, recipeId, count, T0, rules);
        Assert(rejected.Err == 1 && ReferenceEquals(rejected.Account, running),
            $"an invalid order was accepted (recipe {recipeId}, count {count}, building {target})");
    }

    // 领取：燃油在补给达到上限时不能领取，一键领取跳过燃油、照常领取资金与道具。
    PlayerAccount stocked = ProductionTestAccount([],
        [
            new PlayerBuildingEntry(1, 21, 1, [], Status: Working, LastUpdateTime: T0, ProductCount: 180),
            new PlayerBuildingEntry(2, 31, 1, [], Status: Working, LastUpdateTime: T0, ProductCount: 1_260),
            new PlayerBuildingEntry(3, 61, 1, [], Status: Idle, LastUpdateTime: T0, RecipeId: 2, ProductCount: 2),
        ], T0);
    BuildingProduction.Outcome oilReceived = BuildingProduction.Receive(stocked, BuildingProduction.ReceiveKind.Building, 1, T0, rules);
    Assert(oilReceived.Success && oilReceived.CurrencyChanged && oilReceived.Account.Character.Supply == 10_179 &&
           ProdEntry(oilReceived.Account, 1) is { ProductCount: 0, Status: Working } &&
           ProtocolEncoder.EncodeReceiveRet(oilReceived.Rewards).SequenceEqual(
               new byte[] { 0x0A, 0x09, 0x08, 0x05, 0x10, 0x05, 0x18, 0xB4, 0x01, 0x20, 0x00 }),
        "building.ReceiveBuilding did not grant the oil");
    PlayerAccount supplyFull = stocked with { Character = stocked.Character with { Supply = 10_000 } };
    BuildingProduction.Outcome blocked = BuildingProduction.Receive(supplyFull, BuildingProduction.ReceiveKind.Building, 1, T0, rules);
    Assert(blocked.Err == 1 && ReferenceEquals(blocked.Account, supplyFull), "oil was received above the supply limit");
    PlayerAccount fullOil = ProductionTestAccount([],
        [new PlayerBuildingEntry(1, 21, 1, [], Status: Idle, LastUpdateTime: T0 + 600, ProductCount: 4_200)], T0 + 600, supply: 0);
    BuildingProduction.Outcome reopened = BuildingProduction.Receive(fullOil, BuildingProduction.ReceiveKind.Building, 1, T0 + 5_000, rules);
    Assert(ProdEntry(reopened.Account, 1) is { Status: Working, LastUpdateTime: T0 + 5_000, ProductCount: 0 } &&
           reopened.Account.Character.Supply == 4_200 &&
           ProdEntry(TimeSettlement.Settle(reopened.Account, T0 + 5_600, rules).Account, 1).ProductCount == 30,
        "receiving a full building did not restart production from the receive time");
    BuildingProduction.Outcome all = BuildingProduction.Receive(supplyFull, BuildingProduction.ReceiveKind.All, 0, T0, rules);
    Assert(all.Success && all.CurrencyChanged && all.BagChanged &&
           all.Rewards.SequenceEqual([new CommonReward(5, 1, 1_260), new CommonReward(6, 60000, 2)]) &&
           ProdEntry(all.Account, 1).ProductCount == 180 && all.Account.Character.Gold == supplyFull.Character.Gold + 1_260 &&
           ProdBag(all.Account, 60000) == ProdBag(supplyFull, 60000) + 2 &&
           ProdEntry(all.Account, 3) is { RecipeId: 2, ProductCount: 0 },
        "building.ReceiveAll did not skip the blocked oil and receive the rest");
    BuildingProduction.Outcome goldOnly = BuildingProduction.Receive(stocked, BuildingProduction.ReceiveKind.Resource, 1, T0, rules);
    Assert(goldOnly.Rewards.SequenceEqual([new CommonReward(5, 1, 1_260)]) && ProdEntry(goldOnly.Account, 1).ProductCount == 180 &&
           ProtocolEncoder.EncodeReceiveRet(goldOnly.Rewards).SequenceEqual(
               new byte[] { 0x0A, 0x09, 0x08, 0x05, 0x10, 0x01, 0x18, 0xEC, 0x09, 0x20, 0x00 }),
        "building.ReceiveResource did not receive only the requested currency");
    Assert(BuildingProduction.Receive(supplyFull, BuildingProduction.ReceiveKind.Resource, 5, T0, rules).Err == 1 &&
           BuildingProduction.Receive(stocked, BuildingProduction.ReceiveKind.Resource, 7, T0, rules).Err == 1 &&
           BuildingProduction.Receive(stocked, BuildingProduction.ReceiveKind.Item, 1, T0, rules).Err == 1,
        "an invalid receive request was accepted");
    // 发不出去的产物（未知货币）不能被清零：领取跳过，换配方拒绝。
    PlayerAccount ungrantable = BuildingProduction.Replace(stocked, ProdEntry(stocked, 3) with { RecipeId = 7, ProductCount = 3 });
    BuildingProduction.Outcome skipped = BuildingProduction.Receive(ungrantable, BuildingProduction.ReceiveKind.All, 0, T0, rules);
    Assert(ProdEntry(skipped.Account, 3).ProductCount == 3 && skipped.Rewards.All(reward => reward.ConfigId != 999),
        "products that could not be granted were zeroed");
    Assert(BuildingProduction.Order(ungrantable, 3, 2, 1, T0, rules).Err == 1,
        "switching recipes discarded products that could not be granted");
    BuildingProduction.Outcome nothing = BuildingProduction.Receive(all.Account, BuildingProduction.ReceiveKind.All, 0, T0, rules);
    Assert(nothing.Success && nothing.Rewards.Count == 0 && ReferenceEquals(nothing.Account, all.Account) &&
           ProtocolEncoder.EncodeReceiveRet(nothing.Rewards).Length == 0,
        "an empty receive must succeed without rewards");

    // 工人体力加速：扣 UseCount 点体力，按客户端 ProduceNow 推进，满剩余件数即完工。
    PlayerAccount slow = ProductionTestAccount([],
        [new PlayerBuildingEntry(1, 61, 1, [], Status: Working, LastUpdateTime: T0, RecipeId: 6, ItemCount: 1)], T0);
    BuildingProduction.Outcome sped = BuildingProduction.Speedup(slow, 1, 16, T0 + 10, rules);
    PlayerBuildingEntry spedEntry = ProdEntry(sped.Account, 1);
    Assert(sped.Success && Math.Abs(spedEntry.Progress - 0.512) < 1e-9 && spedEntry.LastUpdateTime == T0 &&
           sped.Account.Building!.WorkerStrength == slow.Building!.WorkerStrength - 160_000 &&
           BuildingService.ToProtocol(sped.Account.Building, (int)T0).BuildingInfos!.Single().FloatCount == 5_120,
        "building.UseStrengthSpeedup progress mismatch");
    Assert(ProdEntry(BuildingProduction.Speedup(slow, 1, 32, T0, rules).Account, 1) is { ProductCount: 1, ItemCount: 0, Status: Idle },
        "a speedup past the queue did not finish it");
    Assert(BuildingProduction.Speedup(slow, 1, 0, T0, rules).Err == 1 &&
           BuildingProduction.Speedup(BuildingProduction.Replace(slow, ProdEntry(slow, 1) with { RecipeId = 5 }), 1, 1, T0, rules).Err == 1 &&
           BuildingProduction.Speedup(BuildingProduction.Replace(slow, ProdEntry(slow, 1) with { Status = Idle }), 1, 1, T0, rules).Err == 1,
        "an invalid speedup was accepted");
    Assert(BuildingService.ToProtocol(slow.Building! with { WorkerStrength = 1 }, (int)T0).WorkerStrength == 1,
        "the snapshot must send the stored worker strength");

    // 即时合成（ComposeItem）：工人体力原料 [5,21,6] 与其它原料都按 Count 扣除。
    PlayerAccount composer = ProductionTestAccount([], [new PlayerBuildingEntry(1, 61, 1, [], LastUpdateTime: T0)], T0) with
    {
        Bag = new PlayerBag([new BagItem(10185, 7)]),
    };
    BuildingProduction.Outcome capsules = BuildingProduction.Compose(composer, 1, 13, 2, T0, rules);
    Assert(capsules.Success && capsules.BagChanged && capsules.Rewards.SequenceEqual([new CommonReward(1, 14001, 2)]) &&
           ProdBag(capsules.Account, 14001) == 2 && capsules.Account.Building!.WorkerStrength == composer.Building!.WorkerStrength - 120_000,
        "building.ComposeItem with a worker-strength recipe mismatch");
    BuildingProduction.Outcome swapped = BuildingProduction.Compose(composer, 1, 3, 2, T0, rules);
    Assert(swapped.Success && ProdBag(swapped.Account, 10185) == 1 && ProdBag(swapped.Account, 10182) == 4,
        "building.ComposeItem did not consume its materials");
    BuildingProduction.Outcome poor = BuildingProduction.Compose(composer, 1, 3, 3, T0, rules);
    Assert(poor.Err == 1 && ReferenceEquals(poor.Account, composer), "building.ComposeItem accepted missing materials");
    return Task.CompletedTask;
}

static Task BuildingProductionCodecTest()
{
    byte[] encoded = PlayerDataCodec.Encode(new UserBuildingInfo(BuildingInfos:
    [
        new BuildingInfo(Id: 3, Tid: 61, Level: 1, Status: 3, Productivity: 10_000, ProductCount: 180, RecipeId: 2,
            ItemCount: 1, FloatCount: 5_000, HeroEffectTimes: [new HeroEffectTimeInfo(7, 1_800_000_000, 1_800_171_429)]),
    ]));
    byte[] window = [0x72, 0x0E, 0x08, 0x07, 0x10, 0x80, 0xA4, 0xA7, 0xDA, 0x06, 0x10, 0xA5, 0xDF, 0xB1, 0xDA, 0x06];
    foreach (byte[] expected in new byte[][]
             {
                 [0x28, 0x90, 0x4E], [0x38, 0xB4, 0x01], [0x50, 0x02], [0x58, 0x01], [0x80, 0x01, 0x88, 0x27], window,
             })
        Assert(ContainsSequence(encoded, expected), "TBuildingInfo production field missing: " + Hex(expected));
    int windowAt = IndexOfSequence(encoded, window);
    int recipeTimeAt = IndexOfSequence(encoded, [0x78, 0x00]);
    int floatAt = IndexOfSequence(encoded, [0x80, 0x01, 0x88, 0x27]);
    Assert(windowAt >= 0 && windowAt < recipeTimeAt && recipeTimeAt < floatAt,
        "HeroEffectTimeList must be written before fields 15 and 16");
    Assert(ProtocolDecoder.DecodeProduceItemArg(new byte[] { 0x08, 0x03, 0x10, 0x02, 0x18, 0x05 }) == (3, 2, 5),
        "TProduceItemArg decoding failed");
    return Task.CompletedTask;
}

static async Task BuildingProductionModuleTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-production-" + Guid.NewGuid().ToString("N"));
    const string profileId = "production";
    const int T0 = 1_800_000_000;
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, T0);
        seed = seed with
        {
            Building = seed.Building! with
            {
                Buildings =
                [
                    new PlayerBuildingEntry(1, 2, 2, [], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                    new PlayerBuildingEntry(2, 41, 1, [], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                    new PlayerBuildingEntry(3, 21, 1, [], Status: BuildingProduction.Working, LastUpdateTime: T0, LastBuildUpdateTime: T0),
                    new PlayerBuildingEntry(4, 61, 1, [], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                    new PlayerBuildingEntry(5, 31, 1, [], Status: BuildingProduction.Working, LastUpdateTime: T0, LastBuildUpdateTime: T0),
                    new PlayerBuildingEntry(6, 22, 2, [], Status: BuildingProduction.Working, LastUpdateTime: T0,
                        LastBuildUpdateTime: T0, ProductCount: 5_000),
                ],
                WorkerUpdateTime = T0,
                ProductionVersion = BuildingProduction.CurrentVersion,
            },
        };
        await repo.SaveAccountAsync(seed);

        ServerOptions options = ServerOptions.Parse(
            ["--data=" + dataRoot,
             "--client-path=" + Path.Combine(root, "blueoath", "blueoath"),
             "--profile-id=" + profileId]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        var module = new BuildingModule(new BuildingService(services), services);
        GameContext At(int now) => new() { ProfileId = profileId, Now = now, Ct = CancellationToken.None, Services = services };
        static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;
        static byte[] RetOf(byte[] push) => TMessageCodec.DecodeResponse(push).Ret ?? [];
        static byte[] Arg(params ulong[] values)
        {
            var package = new ProtocolPackage();
            for (int i = 0; i < values.Length; i++) package.Write((byte)((i + 1) << 3), values[i]);
            return package.ToArray();
        }
        async Task<PlayerAccount> Load() => await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        SettlementRules rules = services.SettlementRules;
        Assert(rules.SupplyMax(80) == 10_000 && rules.OilUnit == 600 && rules.GoldUnit == 600 &&
               rules.Recipe(2) is { Time: 1_800, Item: [6, 60000, 1] },
            "production rules were not read from the JP client configuration");

        // 领取燃油：先推 user.UpdateUserInfo 再推建筑快照，应答为 TReceiveRet。
        ModuleResult oil = await module.HandleAsync(At(T0 + 3600), new TRequest("building.ReceiveBuilding", Arg(3)));
        Assert(oil.Err == 0 && oil.Ret.SequenceEqual(new byte[] { 0x0A, 0x09, 0x08, 0x05, 0x10, 0x05, 0x18, 0xB4, 0x01, 0x20, 0x00 }),
            "building.ReceiveBuilding did not return the oil reward");
        Assert(oil.PrePushes.Select(Method).Take(2).SequenceEqual(["user.UpdateUserInfo", "building.UpdateBuildingInfo"]),
            "receive pushes were not ordered user → building");
        Assert((await Load()).Character.Supply == PlayerAccountFactory.DefaultSupply + 180, "the received oil was not persisted");
        ModuleResult capped = await module.HandleAsync(At(T0 + 3700), new TRequest("building.ReceiveBuilding", Arg(3)));
        Assert(capped.Err == 1 && capped.PrePushes.Select(Method).Contains("building.UpdateBuildingInfo"),
            "oil above the supply limit was received or the settlement was not synced");

        // 下单 → 心跳看到进度 → 领取道具。
        ModuleResult ordered = await module.HandleAsync(At(T0 + 3700), new TRequest("building.ProduceItem", Arg(4, 2, 2)));
        PlayerBuildingEntry queued = ProdEntry(await Load(), 4);
        Assert(ordered.Err == 0 && ordered.Ret.Length == 0 &&
               queued is { RecipeId: 2, ItemCount: 2, Status: BuildingProduction.Working, LastUpdateTime: T0 + 3700 } &&
               ContainsSequence(RetOf(ordered.PrePushes[^1]), new byte[] { 0x50, 0x02, 0x58, 0x02 }),
            "building.ProduceItem did not persist and push the queue");
        ModuleResult beat = await module.HandleAsync(At(T0 + 6400), new TRequest("building.UpdateHeroAddition"));
        PlayerBuildingEntry halfway = ProdEntry(await Load(), 4);
        Assert(halfway is { ProductCount: 1, ItemCount: 1 } && Math.Abs(halfway.Progress - 0.5) < 1e-9 &&
               ContainsSequence(RetOf(beat.PrePushes[^1]), new byte[] { 0x80, 0x01, 0x88, 0x27 }),
            "the heartbeat did not bake and push the factory progress");
        int capsulesBefore = ProdBag(await Load(), 60000);
        ModuleResult items = await module.HandleAsync(At(T0 + 7300), new TRequest("building.ReceiveItem", Arg(4)));
        PlayerAccount afterItems = await Load();
        Assert(items.Err == 0 &&
               items.Ret.SequenceEqual(new byte[] { 0x0A, 0x0A, 0x08, 0x06, 0x10, 0xE0, 0xD4, 0x03, 0x18, 0x02, 0x20, 0x00 }) &&
               items.PrePushes.Select(Method).Take(2).SequenceEqual(["bag.UpdateBagData", "building.UpdateBuildingInfo"]) &&
               ProdBag(afterItems, 60000) == capsulesBefore + 2 &&
               ProdEntry(afterItems, 4) is { RecipeId: 2, ProductCount: 0, Status: BuildingProduction.Idle },
            "building.ReceiveItem did not grant the finished items");

        ModuleResult speedup = await module.HandleAsync(At(T0 + 7300), new TRequest("building.UseStrengthSpeedup", Arg(4, 1)));
        Assert(speedup.Err == 1, "a speedup on an idle factory reported success");
        ModuleResult oilOnly = await module.HandleAsync(At(T0 + 7400), new TRequest("building.ReceiveResource", Arg(5)));
        Assert(oilOnly.Err == 1 && oilOnly.PrePushes.Select(Method).Contains("building.UpdateBuildingInfo"),
            "building.ReceiveResource accepted oil above the supply limit");

        // 一键领取：燃油被上限挡住时只领资金（T0 → T0+7400 共 2466），燃油自 T0+3600 起累计 190 留在楼里。
        int goldBefore = (await Load()).Character.Gold;
        ModuleResult all = await module.HandleAsync(At(T0 + 7400), new TRequest("building.ReceiveAll"));
        PlayerAccount afterAll = await Load();
        Assert(all.Err == 0 && all.Ret.SequenceEqual(ProtocolEncoder.EncodeReceiveRet([new CommonReward(5, 1, 2_466)])) &&
               all.PrePushes.Select(Method).First() == "user.UpdateUserInfo" &&
               afterAll.Character.Gold == goldBefore + 2_466 && ProdEntry(afterAll, 3).ProductCount == 190,
            $"building.ReceiveAll did not receive the gold and keep the blocked oil (ret {Hex(all.Ret)}, " +
            $"gold +{afterAll.Character.Gold - goldBefore}, oil {ProdEntry(afterAll, 3).ProductCount})");

        // 补给已达上限时，2 级油厂的存量（5000 + 7400 秒 × 31/600 = 5382）领不走且超过 1 级容量 4200：拒绝降级，存量原样保留。
        ModuleResult degrade = await module.HandleAsync(At(T0 + 7400), new TRequest("building.DegradeBuilding", Arg(6)));
        PlayerBuildingEntry refinery = ProdEntry(await Load(), 6);
        Assert(degrade.Err != 0 && refinery is { Tid: 22, Level: 2, ProductCount: 5_382 },
            $"degrading a refinery with blocked stock lost or overfilled it (err {degrade.Err}, tid {refinery.Tid}, stock {refinery.ProductCount})");

        // 建造/升级按 config_buildinglevelup.costwork 扣工人体力：办公室 2→3 级扣 80，3→4 级需要 150（不足时拒绝）。
        int strengthBefore = (await Load()).Building!.WorkerStrength;
        ModuleResult upgraded = await module.HandleAsync(At(T0 + 7500), new TRequest("building.UpgradeBuilding", Arg(1)));
        int strengthAfter = (await Load()).Building!.WorkerStrength;
        ModuleResult tooTired = await module.HandleAsync(At(T0 + 7600), new TRequest("building.UpgradeBuilding", Arg(1)));
        Assert(upgraded.Err == 0 && strengthAfter == strengthBefore - 800_000 &&
               tooTired.Err != 0 && ProdEntry(await Load(), 1).Level == 3,
            $"building upgrades did not charge worker strength (before {strengthBefore}, after {strengthAfter}, second err {tooTired.Err})");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

// ───────────────────────── 工人体力 / 作弊选项 / 浴场送礼 / 加速页 Mod ─────────────────────────

static Task WorkerStrengthTest()
{
    const long T0 = 1_800_000_000;
    const int Working = BuildingProduction.Working;
    const int Idle = BuildingProduction.Idle;
    SettlementRules rules = ProductionTestRules();

    // 5 级办公室：上限 50 + 5×50 = 300（存储值 3_000_000）；5 级电力室每 600 秒回复 10000（1 点）。
    PlayerAccount Electric(int strength, long w, int electricTid = 15, IReadOnlyList<Hero>? heroes = null,
        IReadOnlyList<uint>? members = null, IReadOnlyList<HeroEffectWindow>? windows = null) =>
        ProductionTestAccount(heroes ?? [],
        [
            new PlayerBuildingEntry(1, 5, 5, [], LastUpdateTime: T0),
            new PlayerBuildingEntry(2, electricTid, 5, members ?? [], LastUpdateTime: w, HeroWindows: windows),
        ], w, workerStrength: strength);

    SettlementResult hour = TimeSettlement.Settle(Electric(1_000_000, T0), T0 + 3600, rules);
    Assert(hour.BuildingChanged && hour.Account.Building!.WorkerStrength == 1_060_000 &&
           hour.Account.Building.WorkerUpdateTime == T0 + 3600,
        "an empty electric factory did not recover worker strength like GetCurStrengthReal");
    PlayerAccount stepped = Electric(1_000_000, T0);
    for (int i = 1; i <= 60; i++) stepped = TimeSettlement.Settle(stepped, T0 + 60 * i, rules).Account;
    Assert(stepped.Building!.WorkerStrength == 1_060_000, "stepped strength recovery diverged from a single settlement");
    UserBuildingInfo info = BuildingService.ToProtocol(hour.Account.Building, (int)(T0 + 3600));
    Assert(info.WorkerStrength == 1_060_000 && info.WorkerUpdateTime == T0 + 3600,
        "the snapshot did not send the stored strength and its anchor");

    // 小数结转：1 级电力室 8300 / 600 秒，每 61 秒结算一次也不丢小数。
    PlayerAccount carried = Electric(1_000_000, T0, electricTid: 11);
    for (int i = 1; i <= 60; i++) carried = TimeSettlement.Settle(carried, T0 + 61 * i, rules).Account;
    PlayerAccount oneShot = TimeSettlement.Settle(Electric(1_000_000, T0, electricTid: 11), T0 + 3660, rules).Account;
    Assert(carried.Building!.WorkerStrength == 1_050_630 && oneShot.Building!.WorkerStrength == 1_050_630,
        $"strength carry lost fractions (stepped {carried.Building!.WorkerStrength}, one-shot {oneShot.Building!.WorkerStrength})");

    // 电力室驻守舰娘在加成窗口内加速回复，并按 WorkerUpdateTime 消耗心情。
    SettlementRules electricBonus = ProductionTestRules((tpl, type) => tpl == 555 && type == 2 ? 200 : 0);
    PlayerAccount staffed = Electric(1_000_000, T0, heroes: [new Hero(7, 555, 1, UpdateTime: (int)T0, Mood: 1_000_000)],
        members: [7], windows: [new HeroEffectWindow(7, T0, T0 + 58_063)]);
    PlayerAccount staffedHour = TimeSettlement.Settle(staffed, T0 + 3600, electricBonus).Account;
    Assert(staffedHour.Building!.WorkerStrength == 1_061_200 && TimeHero(staffedHour, 7).Mood == 938_000,
        "electric factory hero bonus or mood cost mismatch");

    // 上限：回复封顶；已满且电力室无人时 W 不动、不改档。
    Assert(TimeSettlement.Settle(Electric(2_990_000, T0), T0 + 3600, rules).Account.Building!.WorkerStrength == 3_000_000,
        "strength recovery exceeded the office maximum");
    SettlementResult full = TimeSettlement.Settle(Electric(3_000_000, T0), T0 + 3600, rules);
    Assert(!full.Changed && full.Account.Building!.WorkerUpdateTime == T0, "a full strength pool still advanced its anchor");

    // 迁移 1 → 2：此前体力总是按上限下发，迁移时补满。
    PlayerAccount legacy = ProductionTestAccount([], [new PlayerBuildingEntry(1, 5, 5, [], LastUpdateTime: T0)], T0,
        productionVersion: 1, workerStrength: 1_000_000);
    SettlementResult migrated = TimeSettlement.Settle(legacy, T0, rules);
    Assert(migrated.Account.Building! is { WorkerStrength: 3_000_000, ProductionVersion: BuildingProduction.CurrentVersion } &&
           !TimeSettlement.Settle(migrated.Account, T0, rules).Changed,
        "the version 2 migration did not fill worker strength exactly once");

    // 体力加速：扣 UseCount 点，并按实际用掉的加速秒数扣本楼驻守舰娘心情（9000 秒配方，每点 288 秒）。
    PlayerAccount Factory(int strength, int itemCount, long w = T0) => ProductionTestAccount(
        [new Hero(1, 10210511, 1, UpdateTime: (int)T0, Mood: 1_000_000)],
        [
            new PlayerBuildingEntry(1, 5, 5, [], LastUpdateTime: T0),
            new PlayerBuildingEntry(2, 61, 1, [1], Status: Working, LastUpdateTime: T0, RecipeId: 6, ItemCount: itemCount,
                HeroWindows: [new HeroEffectWindow(1, T0, T0 + 58_063)]),
        ], w, workerStrength: strength);
    PlayerAccount factory = Factory(3_000_000, 2);
    BuildingProduction.Outcome sped = BuildingProduction.Speedup(factory, 2, 16, T0, rules);
    PlayerBuildingEntry spedEntry = ProdEntry(sped.Account, 2);
    Hero spedHero = TimeHero(sped.Account, 1);
    Assert(sped.Success && sped.Account.Building!.WorkerStrength == 2_840_000 && sped.Account.Building.WorkerUpdateTime == T0 &&
           Math.Abs(spedEntry.Progress - 0.512) < 1e-9 && spedEntry.LastUpdateTime == T0,
        "building.UseStrengthSpeedup did not charge strength or advance the queue");
    Assert(spedHero.Mood == 919_360 && sped.HeroesChanged.SetEquals([1u]) &&
           spedEntry.HeroWindows!.SequenceEqual(
               [new HeroEffectWindow(1, T0, BuildingProduction.MoodZeroTime(spedHero, T0, 17.5, rules)!.Value)]),
        $"the speedup did not charge mood for the sped-up time (mood {spedHero.Mood})");
    BuildingProduction.Outcome finished = BuildingProduction.Speedup(Factory(3_000_000, 1), 2, 32, T0, rules);
    Assert(ProdEntry(finished.Account, 2) is { ProductCount: 1, ItemCount: 0, Status: Idle } &&
           finished.Account.Building!.WorkerStrength == 2_680_000 && TimeHero(finished.Account, 1).Mood == 842_500,
        "the speedup mood charge was not capped at the time needed to finish the queue");
    PlayerAccount poor = Factory(150_000, 2);
    BuildingProduction.Outcome refused = BuildingProduction.Speedup(poor, 2, 16, T0, rules);
    Assert(refused.Err == 1 && ReferenceEquals(refused.Account, poor), "a speedup without enough strength was accepted");

    // 扣体力前先补结算 [W, now] 的回复（电力室的回复速度按当前建筑计算）。
    PlayerAccount stale = ProductionTestAccount([],
    [
        new PlayerBuildingEntry(1, 5, 5, [], LastUpdateTime: T0),
        new PlayerBuildingEntry(2, 15, 5, [], LastUpdateTime: T0),
        new PlayerBuildingEntry(3, 61, 1, [], Status: Working, LastUpdateTime: T0, RecipeId: 6, ItemCount: 2),
    ], T0 - 3600, workerStrength: 2_000_000);
    BuildingProduction.Outcome caughtUp = BuildingProduction.Speedup(stale, 3, 16, T0, rules);
    Assert(caughtUp.Account.Building! is { WorkerStrength: 1_900_000, WorkerUpdateTime: T0 },
        "a speedup did not bake the pending strength recovery first");

    // 合成的体力原料 [5,21,6] × Count。
    PlayerAccount composer = ProductionTestAccount([], [new PlayerBuildingEntry(1, 61, 1, [], LastUpdateTime: T0)], T0,
        workerStrength: 110_000);
    BuildingProduction.Outcome composeRefused = BuildingProduction.Compose(composer, 1, 13, 2, T0, rules);
    Assert(composeRefused.Err == 1 && ReferenceEquals(composeRefused.Account, composer),
        "building.ComposeItem accepted a recipe without enough worker strength");

    // 任务、邮件、GM 发放的货币 21 进基地体力，而不是落到资金。
    PlayerAccount granted = GameServices.AddCurrency(factory, BuildingProduction.StrengthId, 20);
    Assert(granted.Building!.WorkerStrength == 3_200_000 && granted.Character.Gold == factory.Character.Gold &&
           GameServices.TryGetCurrency(granted, BuildingProduction.StrengthId, out int shown) && shown == 320,
        "currency 21 grants did not reach the worker strength pool");
    return Task.CompletedTask;
}

static Task TimeCheatsTest()
{
    const long T0 = 1_800_000_000;
    const int Working = BuildingProduction.Working;
    const int Idle = BuildingProduction.Idle;

    // 命令行：只认不带值的开关，不区分大小写，默认全关。
    Assert(ServerOptions.Parse([]).Cheats == CheatOptions.None && !CheatOptions.None.Any, "cheats must default to off");
    Assert(ServerOptions.Parse(["--cheat-production", "--CHEAT-MOOD"]).Cheats == new CheatOptions(Production: true, Mood: true),
        "cheat switches did not parse case-insensitively");
    Assert(ServerOptions.Parse(["--cheat-production", "--cheat-strength", "--cheat-vow", "--cheat-mood"]).Cheats ==
           new CheatOptions(true, true, true, true),
        "not every cheat switch was recognised");
    Assert(ServerOptions.Parse(["--cheat-mood=1", "--cheat-vow=true"]).Cheats == CheatOptions.None,
        "a cheat switch with a value must be ignored");

    // 生产：道具队列直接完工；资源楼满仓且领完立即补满；下单直接发放产物。
    SettlementRules production = ProductionTestRules() with { OmitProductionTime = true };
    PlayerAccount queued = ProductionTestAccount([],
        [new PlayerBuildingEntry(1, 61, 1, [], Status: Working, LastUpdateTime: T0, RecipeId: 2, ItemCount: 2)], T0);
    Assert(ProdEntry(TimeSettlement.Settle(queued, T0, production).Account, 1) is { ProductCount: 2, ItemCount: 0, Status: Idle },
        "the production cheat did not finish an existing queue");
    PlayerAccount refinery = ProductionTestAccount([],
        [new PlayerBuildingEntry(1, 21, 1, [], Status: Working, LastUpdateTime: T0)], T0, supply: 0);
    PlayerAccount filled = TimeSettlement.Settle(refinery, T0 + 1, production).Account;
    BuildingProduction.Outcome drained = BuildingProduction.Receive(filled, BuildingProduction.ReceiveKind.Building, 1, T0 + 1, production);
    Assert(ProdEntry(filled, 1) is { ProductCount: 4_200, Status: Idle } && drained.Account.Character.Supply == 4_200 &&
           ProdEntry(drained.Account, 1) is { ProductCount: 4_200, Status: Idle },
        "the production cheat did not keep the resource building full");
    PlayerAccount idleFactory = ProductionTestAccount([], [new PlayerBuildingEntry(1, 61, 1, [], LastUpdateTime: T0)], T0);
    BuildingProduction.Outcome instant = BuildingProduction.Order(idleFactory, 1, 2, 3, T0, production);
    Assert(instant.Success && instant.BagChanged && instant.Rewards.SequenceEqual([new CommonReward(6, 60000, 3)]) &&
           ProdBag(instant.Account, 60000) == 3 && ProdEntry(instant.Account, 1) is { RecipeId: 2, ItemCount: 0, Status: Idle },
        "the production cheat did not grant an order immediately");

    // 体力：保持上限，加速与合成都不扣。
    SettlementRules strength = ProductionTestRules() with { OmitWorkerStrength = true };
    PlayerAccount tired = ProductionTestAccount([],
    [
        new PlayerBuildingEntry(1, 5, 5, [], LastUpdateTime: T0),
        new PlayerBuildingEntry(2, 61, 1, [], Status: Working, LastUpdateTime: T0, RecipeId: 6, ItemCount: 2),
    ], T0, workerStrength: 1_000_000);
    PlayerAccount topped = TimeSettlement.Settle(tired, T0, strength).Account;
    BuildingProduction.Outcome freeSpeedup = BuildingProduction.Speedup(topped, 2, 16, T0, strength);
    BuildingProduction.Outcome freeCompose = BuildingProduction.Compose(topped, 2, 13, 2, T0, strength);
    Assert(topped.Building!.WorkerStrength == 3_000_000 && freeSpeedup.Account.Building!.WorkerStrength == 3_000_000 &&
           Math.Abs(ProdEntry(freeSpeedup.Account, 2).Progress - 0.512) < 1e-9 &&
           freeCompose.Success && freeCompose.Account.Building!.WorkerStrength == 3_000_000,
        "the strength cheat did not keep worker strength full");

    // 心情：工作不扣心情（自然恢复照常），加成窗口一直持续，加速也不扣心情。
    SettlementRules mood = ProductionTestRules() with { OmitMoodCost = true };
    PlayerAccount office = ProductionTestAccount([new Hero(11, 10210511, 1, UpdateTime: (int)T0, Mood: 1_000_000)],
        [new PlayerBuildingEntry(1, 5, 5, [11], LastUpdateTime: T0)], T0);
    PlayerAccount rested = TimeSettlement.Settle(office, T0 + 3600, mood).Account;
    Assert(TimeHero(rested, 11).Mood == 1_001_000 &&
           ProdEntry(rested, 1).HeroWindows!.SequenceEqual([new HeroEffectWindow(11, T0 + 3600, int.MaxValue)]),
        "the mood cheat still drained mood or ended the bonus window");
    PlayerAccount staffedFactory = ProductionTestAccount([new Hero(1, 10210511, 1, UpdateTime: (int)T0, Mood: 1_000_000)],
        [new PlayerBuildingEntry(1, 61, 1, [1], Status: Working, LastUpdateTime: T0, RecipeId: 6, ItemCount: 2,
            HeroWindows: [new HeroEffectWindow(1, T0, int.MaxValue)])], T0);
    BuildingProduction.Outcome calmSpeedup = BuildingProduction.Speedup(staffedFactory, 1, 16, T0, mood);
    Assert(calmSpeedup.Success && calmSpeedup.HeroesChanged.Count == 0 && TimeHero(calmSpeedup.Account, 1).Mood == 1_000_000,
        "the mood cheat still charged mood for a speedup");

    // 许愿墙：结算清掉尚未结束的冷却，只清一次；已结束的冷却不动。
    SettlementRules vow = TestSettlementRules() with { OmitVowCooldown = true };
    PlayerAccount cooling = PlayerAccountFactory.CreateDefault("cheat-vow", (int)T0) with
    {
        Vow = new PlayerVow(CoolTime: T0 + 1_000, CoolHero: 10210611, UseResetDay: VowLogic.Day(T0)),
    };
    SettlementResult cleared = TimeSettlement.Settle(cooling, T0, vow);
    Assert(cleared.VowChanged && cleared.Account.Vow! is { CoolTime: 0, CoolHero: 10210611 } &&
           !TimeSettlement.Settle(cleared.Account, T0 + 1, vow).Changed,
        "the vow cheat did not clear the pending cooldown exactly once");
    PlayerAccount expired = cooling with { Vow = cooling.Vow! with { CoolTime = T0 - 5 } };
    Assert(!TimeSettlement.Settle(expired, T0, vow).VowChanged, "the vow cheat rewrote an expired cooldown");
    return Task.CompletedTask;
}

static Task BathGiftRollTest()
{
    var gift = new ConfigGift
    {
        Id = 130002, GiftType = 2, Price = [5, 13, 50], MatchValueupRate = 5_000, NotmatchValueupRate = 2_000,
        MatchValueGroup = [1, 2, 3, 4, 5, 6, 7, 8, 9], NotMatchValueGroup = [10, 11, 12, 13, 14, 15, 16, 17, 18],
        MatchValuePower = [1, 1, 1, 1, 1, 1, 1, 1, 1], NotMatchValuePower = [1, 1, 1, 1, 1, 1, 1, 1, 1],
    };
    static Func<int, int> Seq(params int[] values)
    {
        int next = 0;
        return _ => values[next++];
    }
    static bool Any(int _) => true;
    var favourite = new ConfigShipMain { FavoriteGift = [2] };
    var other = new ConfigShipMain { FavoriteGift = [1] };
    Assert(BathGift.Roll(gift, favourite, Seq(4_999, 0), Any) == new BathGiftRoll(1, 1, true) &&
           BathGift.Roll(gift, favourite, Seq(5_000, 8), Any) == new BathGiftRoll(18, 1, false),
        "a favourite gift did not use the 50% value-up chance");
    Assert(BathGift.Roll(gift, other, Seq(1_999, 0), Any) == new BathGiftRoll(1, 1, true) &&
           BathGift.Roll(gift, other, Seq(2_000, 0), Any) == new BathGiftRoll(10, 1, false),
        "a non-favourite gift did not use the 20% value-up chance");
    var picky = new ConfigShipMain { FavoriteGift = [2], MatchValueGroup = [2, 4], NotMatchValueGroup = [11] };
    Assert(BathGift.Roll(gift, picky, Seq(0, 1), Any)!.Value.BuffId == 4 &&
           BathGift.Roll(gift, picky, Seq(9_999, 0), Any)!.Value.BuffId == 11,
        "the ship's own buff groups did not take precedence");
    var empty = new ConfigShipMain { MatchValueGroup = [], NotMatchValueGroup = [] };
    Assert(BathGift.Roll(gift, empty, Seq(9_999, 3), Any)!.Value.BuffId == 13 &&
           BathGift.Roll(gift, null, Seq(9_999, 0), id => id != 10)!.Value.BuffId == 11,
        "the gift groups were not used as a fallback or unknown effects were not filtered");
    Assert(BathGift.TryGetPrice(gift, out int currency, out int price) && currency == 13 && price == 50 &&
           !BathGift.TryGetPrice(new ConfigGift { Price = [1, 13, 50] }, out _, out _),
        "gift price parsing mismatch");
    Assert(PlayerDataCodec.EncodeBathServiceRet(new BathHeroInfo(103, Pos: 5), 7, true)
            .SequenceEqual(new byte[] { 0x08, 0x05, 0x10, 0x67, 0x18, 0x07, 0x20, 0x01 }),
        "TBathServiceRet encoding mismatch");
    return Task.CompletedTask;
}

static async Task BathGiftModuleTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-bath-gift-" + Guid.NewGuid().ToString("N"));
    const string profileId = "bath-gift";
    const int T0 = 1_800_000_000;
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, T0);
        seed = seed with
        {
            Character = seed.Character with { Bath = 100 },
            Dock = new HeroDock(
            [
                seed.Dock.Heroes[0],
                new Hero(2, 10210511, 1, CreateTime: T0, UpdateTime: T0, Mood: 500_000),
            ]),
            Bath = new PlayerBath([new BathHero(2, Pos: 5, StartTime: T0, EnterTime: T0)]),
        };
        await repo.SaveAccountAsync(seed);
        ServerOptions options = ServerOptions.Parse(
            ["--data=" + dataRoot, "--client-path=" + Path.Combine(root, "blueoath", "blueoath"), "--profile-id=" + profileId]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        int[] draws = [0, 0, 9_999, 3];
        int drawIndex = 0;
        var module = new BathroomModule(services) { NextRandom = _ => draws[drawIndex++] };
        GameContext At(int now) => new() { ProfileId = profileId, Now = now, Ct = CancellationToken.None, Services = services };
        static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;
        static byte[] Gift(uint heroId, uint giftId) =>
            new ProtocolPackage().Write(0x08, (ulong)heroId).Write(0x10, (ulong)giftId).ToArray();
        async Task<PlayerAccount> Load() => await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");

        ConfigGift? configured = BathGiftLoader.Get(130002);
        Assert(configured is { GiftType: 2, MatchValueupRate: 5_000, NotmatchValueupRate: 2_000 } &&
               configured.Price!.SequenceEqual(new long[] { 5, 13, 50 }) &&
               BathGiftLoader.Effect(1)?.Time == 14_400 && BathGiftLoader.Effect(10)?.Time == 10_800 &&
               services.SettlementRules.BathGiftAdd == 600_000,
            "config_gift / config_value_effect were not loaded from the JP client");

        // 第一次送礼（最喜欢的礼物类型 2）：抽到金色强化 1，扣 50 币，+60 心情，浴场快照先于应答。
        ModuleResult first = await module.HandleAsync(At(T0 + 30), new TRequest("bathroom.BathService", Gift(2, 130002)));
        PlayerAccount afterFirst = await Load();
        BathHero bathed = afterFirst.Bath!.HeroList.Single();
        Assert(first.Err == 0 && first.Ret.SequenceEqual(new byte[] { 0x08, 0x05, 0x10, 0x02, 0x18, 0x01, 0x20, 0x01 }) &&
               afterFirst.Character.Bath == 50 && bathed is { BuffId: 1, BuffTime: T0 + 30, Power: 1 } &&
               TimeHero(afterFirst, 2).Mood == 1_100_000,
            "bathroom.BathService did not charge coins, roll the buff and add the gift mood");
        Assert(first.PrePushes.Select(Method).Contains("user.UpdateUserInfo") &&
               Method(first.PrePushes[^1]) == "bathroom.BathroomInfo" &&
               !first.PostPushes.Select(Method).Contains("bathroom.BathroomInfo"),
            "a successful gift must push the coins and the bath snapshot before the response");

        // 第二次：普通强化 13 替换原效果（不叠加）。
        ModuleResult second = await module.HandleAsync(At(T0 + 40), new TRequest("bathroom.BathService", Gift(2, 130002)));
        PlayerAccount afterSecond = await Load();
        Assert(second.Err == 0 && !ContainsSequence(second.Ret, new byte[] { 0x20, 0x01 }) &&
               afterSecond.Character.Bath == 0 && afterSecond.Bath!.HeroList.Single() is { BuffId: 13, BuffTime: T0 + 40 },
            "a second gift did not replace the previous buff");

        // 温泉币不足、不在浴场、未知礼物：返回错误且不改档。
        ModuleResult broke = await module.HandleAsync(At(T0 + 50), new TRequest("bathroom.BathService", Gift(2, 130002)));
        PlayerAccount afterBroke = await Load();
        Assert(broke.Err != 0 && afterBroke.Character.Bath == 0 && afterBroke.Bath!.HeroList.Single().BuffTime == T0 + 40 &&
               !broke.PrePushes.Select(Method).Contains("bathroom.BathroomInfo"),
            "a gift without enough bath coins was accepted");
        Assert((await module.HandleAsync(At(T0 + 60), new TRequest("bathroom.BathService", Gift(999, 130002)))).Err != 0 &&
               (await module.HandleAsync(At(T0 + 60), new TRequest("bathroom.BathService", Gift(2, 130099)))).Err != 0,
            "a gift for an unknown hero or gift was accepted");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

static Task ProductionSpeedupModTest()
{
    string root = FindRepositoryRoot();
    string modsRoot = Path.Combine(root, "Mods");
    var manager = new ModManager(modsRoot, "jp-1.4.0");
    manager.LoadAll();
    Assert(manager.LoadedIds.Contains("production-speedup-fix.mod"),
        "production speedup fix was not discoverable by the JP mod loader");
    var cnManager = new ModManager(modsRoot, "cn-1.5.20");
    cnManager.LoadAll();
    Assert(!cnManager.LoadedIds.Contains("production-speedup-fix.mod"),
        "JP-only production speedup fix was loaded for the CN client");

    string entry = File.ReadAllText(Path.Combine(modsRoot, "production-speedup-fix.mod", "main.lua"));
    Assert(entry.Contains("productionspeeduppage", StringComparison.Ordinal) &&
           entry.Contains("page_class.OnBtnConfirm = function", StringComparison.Ordinal) &&
           entry.Contains("Logic.buildingLogic:ProduceItem", StringComparison.Ordinal) &&
           entry.Contains("math.ceil((remain_time + SAFETY_SECONDS) / time_per_strength)", StringComparison.Ordinal) &&
           entry.Contains("before > need", StringComparison.Ordinal) &&
           entry.Contains("self:DoSpeedup()", StringComparison.Ordinal),
        "production speedup hook does not clamp to the finishing count and send directly");
    Assert(!entry.Contains("ShowMsgBox(", StringComparison.Ordinal),
        "production speedup hook still routes the confirm through a message box");
    Assert(entry.Contains("package.loaded", StringComparison.Ordinal) &&
           entry.Contains("assign_with_previous(previous_newindex", StringComparison.Ordinal),
        "production speedup hook does not chain the package.loaded __newindex");

    string bootstrap = File.ReadAllText(Path.Combine(modsRoot, "bootstrap.lua"));
    Assert(bootstrap.Contains("\"production-speedup-fix.mod/main.lua\"", StringComparison.Ordinal),
        "production speedup fix is missing from the bootstrap entry list");
    return Task.CompletedTask;
}

static async Task CheatWiringTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-cheats-" + Guid.NewGuid().ToString("N"));
    const string profileId = "cheats";
    const int T0 = 1_800_000_000;
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, T0);
        seed = seed with
        {
            Building = seed.Building! with
            {
                Buildings =
                [
                    new PlayerBuildingEntry(1, 2, 2, [], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                    new PlayerBuildingEntry(2, 61, 1, [], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                ],
                WorkerUpdateTime = T0,
                ProductionVersion = BuildingProduction.CurrentVersion,
            },
        };
        await repo.SaveAccountAsync(seed);
        ServerOptions options = ServerOptions.Parse(
        [
            "--data=" + dataRoot, "--client-path=" + Path.Combine(root, "blueoath", "blueoath"), "--profile-id=" + profileId,
            "--cheat-production", "--cheat-strength", "--cheat-vow", "--cheat-mood",
        ]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        Assert(services.Cheats == new CheatOptions(true, true, true, true) &&
               services.SettlementRules is { OmitProductionTime: true, OmitWorkerStrength: true, OmitMoodCost: true, OmitVowCooldown: true } &&
               services.VowRules.OmitCooldown,
            "cheat switches did not reach the settlement and vow rules");

        // 生产作弊下单：应答直接带产物，背包推送在建筑快照之前；体力作弊在首次结算时补到上限。
        var module = new BuildingModule(new BuildingService(services), services);
        var order = new ProtocolPackage().Write(0x08, 2UL).Write(0x10, 2UL).Write(0x18, 2UL);
        ModuleResult produced = await module.HandleAsync(
            new GameContext { ProfileId = profileId, Now = T0 + 10, Ct = CancellationToken.None, Services = services },
            new TRequest("building.ProduceItem", order.ToArray()));
        PlayerAccount saved = await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        Assert(produced.Err == 0 &&
               produced.Ret.SequenceEqual(new byte[] { 0x0A, 0x0A, 0x08, 0x06, 0x10, 0xE0, 0xD4, 0x03, 0x18, 0x02, 0x20, 0x00 }) &&
               produced.PrePushes.Select(push => TMessageCodec.DecodeResponse(push).Method).Take(2)
                   .SequenceEqual(["bag.UpdateBagData", "building.UpdateBuildingInfo"]) &&
               ProdEntry(saved, 2) is { ItemCount: 0, Status: BuildingProduction.Idle } &&
               saved.Building!.WorkerStrength == BuildingConfigLoader.GetMaxWorkerStrength(2) * BuildingProduction.StrengthScale,
            "the production / strength cheats did not take effect through the module");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

// 评审补测：建造前结算体力锚点、GM 发放体力先结算、作弊下降级与窗口改正、祈愿冷却结果页 Mod。
static async Task ConstructionStrengthTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-construction-strength-" + Guid.NewGuid().ToString("N"));
    const string profileId = "construction-strength";
    const int T0 = 1_800_000_000;
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, T0);
        // 2 级办公室上限 150（1_500_000）已满，1 级电力室无人：结算不推进 W，升级必须自己先把锚点推到 now。
        seed = seed with
        {
            Building = seed.Building! with
            {
                Buildings =
                [
                    new PlayerBuildingEntry(1, 2, 2, [], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                    new PlayerBuildingEntry(2, 11, 1, [], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                ],
                WorkerStrength = 1_500_000,
                WorkerUpdateTime = T0,
                ProductionVersion = BuildingProduction.CurrentVersion,
            },
        };
        await repo.SaveAccountAsync(seed);
        ServerOptions options = ServerOptions.Parse(
            ["--data=" + dataRoot, "--client-path=" + Path.Combine(root, "blueoath", "blueoath"), "--profile-id=" + profileId]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        var module = new BuildingModule(new BuildingService(services), services);
        GameContext At(int now) => new() { ProfileId = profileId, Now = now, Ct = CancellationToken.None, Services = services };
        async Task<PlayerBuilding> Load() =>
            (await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing")).Building!;

        ModuleResult upgraded = await module.HandleAsync(At(T0 + 86_400),
            new TRequest("building.UpgradeBuilding", new ProtocolPackage().Write(0x08, 1UL).ToArray()));
        PlayerBuilding afterUpgrade = await Load();
        Assert(upgraded.Err == 0 && afterUpgrade.WorkerStrength == 700_000 && afterUpgrade.WorkerUpdateTime == T0 + 86_400,
            $"an office upgrade did not charge costwork from an advanced anchor (strength {afterUpgrade.WorkerStrength}, W {afterUpgrade.WorkerUpdateTime})");
        await module.HandleAsync(At(T0 + 86_400), new TRequest("building.UpdateHeroAddition"));
        Assert((await Load()).WorkerStrength == 700_000, "a stale anchor credited recovery for the time the pool was full");
        await module.HandleAsync(At(T0 + 87_000), new TRequest("building.UpdateHeroAddition"));
        Assert((await Load()).WorkerStrength == 708_300, "worker strength did not recover from the upgrade time");

        // GM 发放体力：先按经过时间结算再加，避免下一次结算漏算或封顶吞掉这段回复。
        var gm = new GmCommandHandler(repo, services,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GmCommandHandler>.Instance);
        int realNow = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        PlayerAccount real = (await repo.LoadAccountAsync(profileId))!;
        await services.SaveAccountAsync(real with
        {
            LastSettleTime = realNow - 3600,
            Building = real.Building! with { WorkerStrength = 1_000_000, WorkerUpdateTime = realNow - 3600, WorkerStrengthCarry = 0 },
        });
        string answer = await gm.ExecuteAsync($"add_currency {profileId} strength 20", CancellationToken.None);
        PlayerBuilding afterGm = await Load();
        // 3 级办公室上限 200；1 小时 × 8300 / 600 秒 = 49_800，再加 20 点 = 200_000（容许执行耗时带来的几秒回复）。
        Assert(answer.StartsWith("ok", StringComparison.Ordinal) &&
               afterGm.WorkerStrength is >= 1_249_800 and <= 1_250_200 && afterGm.WorkerUpdateTime >= realNow,
            $"GM add_currency strength did not settle first (strength {afterGm.WorkerStrength}, W {afterGm.WorkerUpdateTime})");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

static Task CheatEdgeCasesTest()
{
    const long T0 = 1_800_000_000;
    const int Idle = BuildingProduction.Idle;
    SettlementRules rules = ProductionTestRules();
    SettlementRules production = rules with { OmitProductionTime = true };

    // 「生产」作弊下降级 2 级油厂：领出 5400 后按 1 级容量 4200 满仓，而不是因超容被拒。
    PlayerAccount refinery = ProductionTestAccount([],
        [new PlayerBuildingEntry(1, 22, 2, [], Status: Idle, LastUpdateTime: T0, ProductCount: 5_400)], T0, supply: 0);
    BuildingProduction.Outcome collected = BuildingProduction.CollectForDegrade(refinery, 1, rules.BuildingInfo(21)!, T0, production);
    Assert(collected.Rewards.SequenceEqual([new CommonReward(5, 5, 5_400)]) &&
           ProdEntry(collected.Account, 1) is { ProductCount: 4_200, Status: Idle },
        "the production cheat left a degraded refinery above its new capacity");

    // 「心情」作弊开关切换后：存档里按旧规则算的窗口终点先按当前规则改正。
    Hero worker = new(11, 10210511, 1, UpdateTime: (int)T0, Mood: 1_000_000);
    var heroes = new Dictionary<uint, Hero> { [11] = worker };
    ConfigBuildinginfo office = rules.BuildingInfo(5)!;
    var endless = new PlayerBuildingEntry(1, 5, 5, [11], LastUpdateTime: T0, HeroWindows: [new HeroEffectWindow(11, T0, int.MaxValue)]);
    PlayerBuildingEntry finite = BuildingProduction.NormalizeWindows(endless, office, heroes, 1, rules);
    Assert(finite.HeroWindows!.SequenceEqual([new HeroEffectWindow(11, T0, T0 + 58_063)]),
        "an endless window saved under the mood cheat was not shortened once the cheat was off");
    SettlementRules mood = rules with { OmitMoodCost = true };
    Assert(BuildingProduction.NormalizeWindows(finite, office, heroes, 1, mood).HeroWindows!
               .SequenceEqual([new HeroEffectWindow(11, T0, int.MaxValue)]) &&
           ReferenceEquals(BuildingProduction.NormalizeWindows(endless, office, heroes, 1, mood), endless) &&
           ReferenceEquals(BuildingProduction.NormalizeWindows(finite, office, heroes, 1, rules), finite),
        "window normalisation must only touch windows computed under the other rule");

    // 关掉作弊后的第一次结算：停机 2 小时的资金加成按改正后的窗口计算（与心情扣减一致）。
    SettlementRules bonus = ProductionTestRules((tpl, type) => tpl == 111 && type == 1 ? 200 : 0);
    PlayerAccount gold = ProductionTestAccount([new Hero(1, 111, 1, UpdateTime: (int)T0, Mood: 31_500)],
    [
        new PlayerBuildingEntry(1, 5, 5, [1], LastUpdateTime: T0, HeroWindows: [new HeroEffectWindow(1, T0, int.MaxValue)]),
        new PlayerBuildingEntry(2, 31, 1, [], Status: BuildingProduction.Working, LastUpdateTime: T0),
    ], T0);
    PlayerBuildingEntry tavern = ProdEntry(TimeSettlement.Settle(gold, T0 + 7_200, bonus).Account, 2);
    // 基础 2400；办公室舰娘心情 31_500 只够约 1800 秒（含自然恢复）：加成 ≈ 1800/600 × 200 × 0.02 = 12，而不是整段的 48。
    Assert(tavern.ProductCount is >= 2_410 and <= 2_414,
        $"the first settle after turning the mood cheat off still credited the endless window (gold {tavern.ProductCount})");
    return Task.CompletedTask;
}

static Task WishCooldownTipModTest()
{
    string root = FindRepositoryRoot();
    string modsRoot = Path.Combine(root, "Mods");
    var manager = new ModManager(modsRoot, "jp-1.4.0");
    manager.LoadAll();
    Assert(manager.LoadedIds.Contains("wish-cooldown-tip-fix.mod"), "wish cooldown tip fix was not discoverable by the JP mod loader");
    string entry = File.ReadAllText(Path.Combine(modsRoot, "wish-cooldown-tip-fix.mod", "main.lua"));
    Assert(entry.Contains("page_class._ShowChargeTip = function", StringComparison.Ordinal) &&
           entry.Contains("Logic.wishLogic:CheckCharge()", StringComparison.Ordinal) &&
           entry.Contains("charging == false", StringComparison.Ordinal) &&
           entry.Contains("return original_show(self, ...)", StringComparison.Ordinal) &&
           entry.Contains("assign_with_previous(previous_newindex", StringComparison.Ordinal),
        "wish cooldown tip hook does not skip the result page only when there is no cooldown");
    string bootstrap = File.ReadAllText(Path.Combine(modsRoot, "bootstrap.lua"));
    Assert(bootstrap.Contains("\"wish-cooldown-tip-fix.mod/main.lua\"", StringComparison.Ordinal),
        "wish cooldown tip fix is missing from the bootstrap entry list");
    return Task.CompletedTask;
}

// 祈愿墙编辑：结算推送必须放在应答之后，否则客户端用旧列表重建墙（拖一张卡连带好几张）。
static async Task VowWallEditPushOrderTest()
{
    const int T0 = 1_800_000_000;
    const string profileId = "vow-wall-push-order";
    var (services, repo, dataRoot) = await VowTestServices(profileId, account => account with
    {
        Dock = VowTestDock() with
        {
            Heroes = [.. VowTestDock().Heroes, new Hero(8, 10210511, 1, CreateTime: T0, UpdateTime: T0, Mood: 1_500_000)],
        },
        // 办公室里的舰娘：之后每次结算心情都会变，产生 hero.UpdateHeroBagData 推送。
        Building = account.Building! with
        {
            Buildings = [new PlayerBuildingEntry(1, 2, 2, [8], LastUpdateTime: T0, LastBuildUpdateTime: T0)],
        },
    });
    try
    {
        var module = new BuildShipModule(new BuildShipService(services), services, BuildPoolsConfigLoader.Load());
        GameContext At(int now) => new() { ProfileId = profileId, Now = now, Ct = CancellationToken.None, Services = services };
        static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;
        // 客户端模型（日服 heroservice._UpdateHeroBagData → WishData:UpdateWishHero，illustrateservice._OnModiVowHero）：
        // 舰娘推送把本地墙还原成「已确认」列表；应答把「已确认」更新为刚发出的列表。
        int[] confirmed = [], local = [];
        int now = T0;
        foreach (int[] sent in new[] { new[] { 1021061 }, new[] { 1021061, 1021051 }, new[] { 1021051 } })
        {
            local = sent;
            now += 600;
            var args = new ProtocolPackage();
            foreach (int id in sent) args.Write(0x08, (ulong)id);
            ModuleResult result = await module.HandleAsync(At(now), new TRequest("illustrate.ModiVowHeroList", args.ToArray()));
            foreach (byte[] push in result.PrePushes) if (Method(push) == "hero.UpdateHeroBagData") local = confirmed;
            confirmed = sent;
            foreach (byte[] push in result.PostPushes) if (Method(push) == "hero.UpdateHeroBagData") local = confirmed;
            Assert(result.Err == 0 && !result.PrePushes.Select(Method).Contains("hero.UpdateHeroBagData"),
                "illustrate.ModiVowHeroList pushed hero data before its response");
            Assert(local.SequenceEqual(sent), "the client's wish wall was reset to the previous list");
            Assert(result.PostPushes.Select(Method).Take(3).SequenceEqual(
                    ["building.UpdateBuildingInfo", "hero.UpdateHeroBagData", "building.UpdateBuildingInfo"]),
                "settlement pushes were not sent after the response");
        }
        PlayerAccount saved = (await repo.LoadAccountAsync(profileId))!;
        Assert(saved.Vow!.HeroList!.SequenceEqual([1021051]) && saved.Dock.Heroes.Single(hero => hero.HeroId == 8).Mood < 1_500_000,
            "the wall or the settled mood was not persisted");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

// 服务端发往客户端的帧：长度字段含 5 字节帧头（客户端按 L - 5 读正文），否则 TResponse 末尾的 Time 被截断。
static async Task NetSocketServerFramingTest()
{
    var response = new TResponse(Method: "user.Refresh", Ret: [], CallbackHandler: 7, Time: 1_800_000_000, IsResponse: 1);
    byte[] encoded = TMessageCodec.EncodeResponse(response);
    using var toClient = new MemoryStream();
    await NetSocketFrameCodec.WriteToClientAsync(toClient, encoded);
    byte[] frame = toClient.ToArray();
    Assert(frame.Length == encoded.Length + 5 &&
           System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame) == encoded.Length + 5 && frame[4] == 0,
        "a server frame must carry payload length + 5 in its header");
    toClient.Position = 0;
    var read = await NetSocketFrameCodec.ReadFromServerAsync(toClient);
    TResponse decoded = TMessageCodec.DecodeResponse(read!.Value.Payload);
    Assert(decoded.Time == 1_800_000_000 && decoded.IsResponse == 1 && decoded.CallbackHandler == 7 && decoded.Method == "user.Refresh",
        "the client could not read Time / IsResponse from a server frame");

    using var ping = new MemoryStream();
    await NetSocketFrameCodec.WriteToClientAsync(ping, ReadOnlyMemory<byte>.Empty, NetSocketFrameCodec.TypePing);
    Assert(ping.ToArray().SequenceEqual(new byte[] { 0, 0, 0, 5, 2 }), "the ping reply header mismatch");

    // 客户端发往服务端的方向保持正文长度（与客户端 NetSocket.Send 相同）。
    using var toServer = new MemoryStream();
    await NetSocketFrameCodec.WriteAsync(toServer, new byte[] { 1, 2, 3 });
    Assert(toServer.ToArray().SequenceEqual(new byte[] { 0, 0, 0, 3, 0, 1, 2, 3 }), "client-to-server framing changed");
}

// ───────────────────────── 「真实消耗资源」选项 ─────────────────────────

static Task RealCostPureTest()
{
    // 选项解析：两个按原规则的开关不算「跳过时间」作弊，但不再是默认值。
    CheatOptions real = ServerOptions.Parse(["--real-resource-cost", "--REAL-SHOP-STOCK"]).Cheats;
    Assert(real == new CheatOptions(RealResourceCost: true, RealShopStock: true) && !real.Any && !real.IsDefault &&
           ServerOptions.Parse(["--real-resource-cost=1"]).Cheats.IsDefault && CheatOptions.None.IsDefault,
        "real cost / shop stock switches did not parse as bare switches");

    // 价格分档（日服 GetPriceByNum）：第 n 次购买用第 min(n, 末档) 档。
    long[] tiers = [50, 100, 150, 200, 300, 400, 500];
    Assert(ShopPricing.PriceByNum(tiers, 0, 1) == 50 && ShopPricing.PriceByNum(tiers, 0, 2) == 150 &&
           ShopPricing.PriceByNum(tiers, 3, 2) == 500 && ShopPricing.PriceByNum(tiers, 6, 2) == 1_000 &&
           ShopPricing.PriceByNum(tiers, 9, 1) == 500 && ShopPricing.PriceByNum([], 0, 3) == 0,
        "shop price tiers do not match GetPriceByNum");
    var tiered = new ConfigShopGoods { Currency = [[1, 13000]], Price = [[50, 100, 150]], CurRelation = 0 };
    var either = new ConfigShopGoods { Currency = [[5, 25], [5, 23]], Price = [[680], [98]], CurRelation = 2 };
    var both = new ConfigShopGoods { Currency = [[5, 1], [1, 13000]], Price = [[10], [2]], CurRelation = 0 };
    Assert(ShopPricing.Costs(tiered, 0, 1, 2).SequenceEqual([new CostItem(1, 13000, 250)]) &&
           ShopPricing.Costs(either, 0, 0, 1).SequenceEqual([new CostItem(5, 25, 680)]) &&
           ShopPricing.Costs(either, 1, 0, 1).SequenceEqual([new CostItem(5, 23, 98)]) &&
           ShopPricing.Costs(either, 7, 0, 1).SequenceEqual([new CostItem(5, 23, 98)]) &&
           ShopPricing.Costs(both, 0, 0, 3).SequenceEqual([new CostItem(5, 1, 30), new CostItem(1, 13000, 6)]) &&
           ShopPricing.Costs(new ConfigShopGoods(), 0, 0, 1).Count == 0,
        "shop costs did not follow currency rows / cur_relation");

    // 扣费全部够才扣。
    PlayerAccount wallet = PlayerAccountFactory.CreateDefault("costs", 1) with { Bag = new PlayerBag([new BagItem(13000, 100)]) };
    wallet = wallet with { Character = wallet.Character with { Gold = 5 } };
    PaymentResult refused = CostLogic.TryPay(wallet, [new CostItem(1, 13000, 50), new CostItem(5, 1, 10)]);
    Assert(!refused.Ok && ReferenceEquals(refused.Account, wallet), "a payment with a shortfall was partly charged");
    PaymentResult paid = CostLogic.TryPay(wallet with { Character = wallet.Character with { Gold = 20 } },
        [new CostItem(1, 13000, 30), new CostItem(5, 1, 10), new CostItem(1, 13000, 20)]);
    Assert(paid.Ok && paid.CurrencyChanged && paid.BagChanged && paid.Account.Character.Gold == 10 &&
           paid.Account.Bag!.Items.Single(item => item.TemplateId == 13000).Num == 50,
        "a payment did not merge and deduct every cost");
    PaymentResult split = CostLogic.TryPay(wallet with { Bag = new PlayerBag([new BagItem(13000, 30), new BagItem(10007, 1), new BagItem(13000, 40)]) },
        [new CostItem(1, 13000, 50)]);
    Assert(split.Ok && split.Account.Bag!.Items.Where(item => item.TemplateId == 13000).Select(item => item.Num).SequenceEqual([0, 20]),
        "a payment over duplicate bag rows left a negative row");

    // 探索消耗：十连且有十连券时用券，否则推薦状 × 抽数；没有消耗的卡池免费。
    var pool = new ConfigExtractShip { Expend = [[1, 10007, 1]], NewTenExpend = [1, 10090, 1] };
    PlayerAccount ticketHolder = wallet with { Bag = new PlayerBag([new BagItem(10090, 1), new BagItem(10007, 30)]) };
    Assert(BuildShipService.ExtractCosts(pool, 10, ticketHolder).SequenceEqual([new CostItem(1, 10090, 1)]) &&
           BuildShipService.ExtractCosts(pool, 10, wallet).SequenceEqual([new CostItem(1, 10007, 10)]) &&
           BuildShipService.ExtractCosts(pool, 1, ticketHolder).SequenceEqual([new CostItem(1, 10007, 1)]) &&
           BuildShipService.ExtractCosts(new ConfigExtractShip(), 10, wallet).Count == 0,
        "build ship costs mismatch");

    // 出击燃料（日服 _GetSupplyNum）。
    var display = new SortieCost.Display([180, 240, 300, 350, 400, 450], [], 0, false);
    Assert(SortieCost.Cost(display, 1, false, [3], 1) == (5, 300) && SortieCost.Cost(display, 1, false, [9], 1) == (5, 450) &&
           SortieCost.Cost(display, 1, false, [0], 1) == (0, 0) && SortieCost.Cost(display, 1, false, [], 1) == (0, 0) &&
           SortieCost.Cost(display, 1, false, [4, 2], 1) == (5, 350) &&
           SortieCost.Cost(display with { SplitTeams = 2 }, 1, false, [3, 2, 1], 1) == (5, 400) &&
           SortieCost.Cost(display with { MaxFleet = 1 }, 1, false, [2], 1) == (5, 450) &&
           SortieCost.Cost(display, 3, false, [3], 1) == (0, 0) && SortieCost.Cost(display, 2, false, [3], 1) == (0, 0) &&
           SortieCost.Cost(display, 1, true, [3], 1) == (30, 1) &&
           SortieCost.Cost(display with { AfterClear = [100, 100, 100, 100, 100, 100], NewOcean = true }, 1, false, [4], 1) == (5, 100) &&
           SortieCost.Cost(display with { NewOcean = true }, 1, false, [4], 1) == (0, 0),
        "sortie fuel costs mismatch");
    SortieCostLoader.Load(FindClientConfigDir());
    Assert(SortieCostLoader.Get(5011) is { NewOcean: false, MaxFleet: 0 } d5011 && d5011.Total.SequenceEqual(new long[] { 180, 240, 300, 350, 400, 450 }) &&
           SortieCostLoader.Get(1600100) is { NewOcean: true } d16 && d16.AfterClear.SequenceEqual(new long[] { 100, 100, 100, 100, 100, 100 }) &&
           SortieCostLoader.Get(5101) is { MaxFleet: > 0 } &&
           SortieCostLoader.ChargedCopyId(0, 5061, false) == 5061 && SortieCostLoader.ChargedCopyId(0, 5061, true) == 15061 &&
           SortieCostLoader.Get(15061) is { } chase && chase.Total.SequenceEqual(new long[] { 90, 90, 90, 90, 90, 90 }),
        "config_copy_display / new_ocean_tag were not loaded");

    // copy.StartBase：所有舰队的出击舰数之和（兼容 packed）、共闘单人标记。
    var start = new ProtocolPackage().Write(0x08, 50UL).Write(0x10, 5011UL).Write(0x48, 1UL)
        .Write(0x6A, new ProtocolPackage().Write(0x08, 11UL).Write(0x08, 12UL).ToArray())
        .Write(0x6A, new ProtocolPackage().Write(0x0A, new byte[] { 13, 14, 15 }).ToArray())
        .Write(0x88, 1UL);
    StartBaseArg startArg = ProtocolDecoder.DecodeStartBaseArg(start.ToArray());
    Assert(startArg is { ChapterId: 50, CopyId: 5011, BattleMode: 1, IsPvePtMode: true } && startArg.ShipCountsByList!.SequenceEqual([2, 3]) &&
           startArg.DeployHeroIds!.SequenceEqual([13, 14, 15]),
        "TStartBaseArg fleets / IsPvePtMode were not decoded");
    return Task.CompletedTask;
}

static async Task RealCostModuleTest()
{
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-real-cost-" + Guid.NewGuid().ToString("N"));
    const string profileId = "real-cost";
    const int T0 = 1_800_000_000;
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, T0);
        seed = seed with { Bag = new PlayerBag([new BagItem(10007, 3), new BagItem(13000, 60)]) };
        await repo.SaveAccountAsync(seed);
        ServerOptions options = ServerOptions.Parse(
        [
            "--data=" + dataRoot, "--client-path=" + Path.Combine(root, "blueoath", "blueoath"), "--profile-id=" + profileId,
            "--real-resource-cost",
        ]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        GameContext At(int now) => new() { ProfileId = profileId, Now = now, Ct = CancellationToken.None, Services = services };
        static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;
        async Task<PlayerAccount> Load() => await repo.LoadAccountAsync(profileId) ?? throw new InvalidDataException("account missing");
        static int Bag(PlayerAccount account, int templateId) => account.Bag?.Items.Where(item => item.TemplateId == templateId).Sum(item => item.Num) ?? 0;

        // 探索：卡池 74 每抽 1 张推薦状；不够十连时整次不抽，并重推背包与玩家信息。
        var buildShip = new BuildShipModule(new BuildShipService(services), services, BuildPoolsConfigLoader.Load());
        int dockBefore = (await Load()).Dock.Heroes.Count;
        ModuleResult single = await buildShip.HandleAsync(At(T0 + 10),
            new TRequest("buildship.BuildShip", new ProtocolPackage().Write(0x08, 74UL).Write(0x10, 1UL).ToArray()));
        PlayerAccount afterSingle = await Load();
        Assert(single.Err == 0 && single.Ret.Length > 0 && Bag(afterSingle, 10007) == 2,
            $"a single pull did not spend one recommendation letter (err {single.Err} {single.ErrMsg}, letters {Bag(afterSingle, 10007)})");
        int dockAfterSingle = afterSingle.Dock.Heroes.Count;
        ModuleResult ten = await buildShip.HandleAsync(At(T0 + 20),
            new TRequest("buildship.BuildShip", new ProtocolPackage().Write(0x08, 74UL).Write(0x10, 10UL).ToArray()));
        PlayerAccount afterTen = await Load();
        Assert(ten.Err != 0 && ten.Ret.Length == 0 && Bag(afterTen, 10007) == 2 && afterTen.Dock.Heroes.Count == dockAfterSingle &&
               ten.PrePushes.Select(Method).SequenceEqual(["bag.UpdateBagData", "user.UpdateUserInfo"]) && dockAfterSingle >= dockBefore,
            "a ten-pull without enough letters was not refused cleanly");

        // 商店：商品 6000 价格 50 个道具 13000（分档，未开库存时每次按第一档）。
        var shopModule = new ShopModule(new ShopService(services), services);
        static byte[] Buy(int goodId) => new ProtocolPackage().Write(0x08, 6UL).Write(0x10, (ulong)goodId).Write(0x18, 1UL).ToArray();
        // 抽卡可能掉落道具 13000，先把余额设成固定值（经 GameServices 写，避免账号缓存仍是旧值）。
        PlayerAccount beforeShop = await Load();
        await services.SaveAccountAsync(beforeShop with
        {
            Bag = new PlayerBag(beforeShop.Bag!.Items.Where(item => item.TemplateId != 13000).Append(new BagItem(13000, 60)).ToList()),
        }, CancellationToken.None);
        ModuleResult bought = await shopModule.HandleAsync(At(T0 + 30), new TRequest("shop.BuyGoods", Buy(6000)));
        int boughtLeft = Bag(await Load(), 13000);
        Assert(bought.Err == 0 && boughtLeft == 10, $"a priced shop purchase did not deduct its price (err {bought.Err} {bought.ErrMsg}, left {boughtLeft})");
        ModuleResult broke = await shopModule.HandleAsync(At(T0 + 40), new TRequest("shop.BuyGoods", Buy(6000)));
        Assert(broke.Err != 0 && Bag(await Load(), 13000) == 10 &&
               broke.PrePushes.Select(Method).SequenceEqual(["user.UpdateUserInfo", "bag.UpdateBagData"]),
            "a purchase without enough resources was not refused with resync pushes");

        // 出击：关卡 5011 三艘出击扣 300 燃料；回忆模式不扣。
        var copy = new CopyModule(new BattleService(services, new DailyCopyService(services)));
        static byte[] Start(int mode) => new ProtocolPackage().Write(0x10, 5011UL).Write(0x48, (ulong)mode)
            .Write(0x6A, new ProtocolPackage().Write(0x08, 1UL).Write(0x08, 2UL).Write(0x08, 3UL).ToArray()).ToArray();
        int supplyBefore = (await Load()).Character.Supply;
        ModuleResult sortie = await copy.HandleAsync(At(T0 + 50), new TRequest("copy.StartBase", Start(1)));
        Assert(sortie.Err == 0 && sortie.Ret.Length > 0 && (await Load()).Character.Supply == supplyBefore - 300 &&
               sortie.PrePushes.Select(Method).SequenceEqual(["user.UpdateUserInfo"]),
            "copy.StartBase did not charge the sortie fuel");
        ModuleResult memory = await copy.HandleAsync(At(T0 + 60), new TRequest("copy.StartBase", Start(3)));
        Assert(memory.Err == 0 && (await Load()).Character.Supply == supplyBefore - 300 && memory.PrePushes.Count == 0,
            "a memory-mode sortie was charged");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

// ───────────────────────── 「无限道具」作弊（--cheat-materials） ─────────────────────────

static async Task MaterialsCheatTest()
{
    CheatOptions parsed = ServerOptions.Parse(["--CHEAT-MATERIALS"]).Cheats;
    Assert(parsed == new CheatOptions(Materials: true) && parsed.Any && !parsed.IsDefault &&
           ServerOptions.Parse(["--cheat-materials=1"]).Cheats.IsDefault,
        "--cheat-materials did not parse as a bare switch");
    var sample = new ConfigBuildinglevelup
    {
        Rawmaterial1 = [1, 14001, 3], Rawmaterial2 = new List<object> { 1L, 14002L, 5L }, Rawmaterial3 = [5, 1, 9],
    };
    Assert(BuildingService.MaterialCosts(sample).SequenceEqual([new CostItem(1, 14001, 3), new CostItem(1, 14002, 5)]) &&
           BuildingService.MaterialCosts(null).Count == 0,
        "building material costs were not read from rawmaterial1-3");

    string root = FindRepositoryRoot();
    const int T0 = 1_800_000_000;
    // 电力室 tid 11 → 12：config_buildinglevelup[12] 扣 14001 × 10、costwork 30。
    const int Material = 14001, MissingMaterial = 14011, OathItem = GameServices.OathShopCurrencyId;
    foreach (bool cheat in new[] { false, true })
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-materials-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            string[] args = ["--data=" + dataRoot, "--client-path=" + Path.Combine(root, "blueoath", "blueoath")];
            ServerOptions options = ServerOptions.Parse(cheat ? [.. args, "--cheat-materials"] : args);
            using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
                Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            var services = new GameServices(repo, options, loggerFactory);
            var module = new BuildingModule(new BuildingService(services), services);

            async Task Seed(string profileId, int materialCount)
            {
                PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, T0);
                var bag = new List<BagItem> { new(OathItem, 100) };
                bag.AddRange(BuildingConfigLoader.MaterialTemplateIds
                    .Where(id => id != MissingMaterial)
                    .Select(id => new BagItem(id, id == Material ? materialCount : 50)));
                await repo.SaveAccountAsync(seed with
                {
                    Bag = new PlayerBag(bag),
                    Building = seed.Building! with
                    {
                        Buildings =
                        [
                            new PlayerBuildingEntry(1, 2, 2, [], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                            new PlayerBuildingEntry(2, 11, 1, [], LastUpdateTime: T0, LastBuildUpdateTime: T0),
                        ],
                        WorkerStrength = 1_500_000,
                        WorkerUpdateTime = T0,
                        ProductionVersion = BuildingProduction.CurrentVersion,
                    },
                });
            }
            static int Count(PlayerAccount account, int id) =>
                account.Bag!.Items.Where(item => item.TemplateId == id).Sum(item => item.Num);
            GameContext At(string profileId, int now) =>
                new() { ProfileId = profileId, Now = now, Ct = CancellationToken.None, Services = services };
            static byte[] Upgrade(int buildingId) => new ProtocolPackage().Write(0x08, (ulong)buildingId).ToArray();
            string mode = cheat ? "on" : "off";

            // 加载档案：作弊开启时补满，关闭时只补从未有过的。
            await Seed("rich", 12);
            PlayerAccount loaded = await services.GetOrCreateAccountAsync("rich", CancellationToken.None);
            Assert(cheat
                    ? Count(loaded, OathItem) == GameServices.DefaultOathShopCurrencyCount &&
                      Count(loaded, Material) == GameServices.DefaultBuildingMaterialCount &&
                      Count(loaded, MissingMaterial) == GameServices.DefaultBuildingMaterialCount
                    : Count(loaded, OathItem) == 100 && Count(loaded, Material) == 12 &&
                      Count(loaded, MissingMaterial) == GameServices.DefaultBuildingMaterialCount,
                $"[{mode}] loading the account refilled the wrong items (17553 {Count(loaded, OathItem)}, {Material} {Count(loaded, Material)}, {MissingMaterial} {Count(loaded, MissingMaterial)})");

            // 升级电力室：关闭时扣 14001 × 10 并推背包；开启时不扣。
            ModuleResult upgraded = await module.HandleAsync(At("rich", T0 + 60), new TRequest("building.UpgradeBuilding", Upgrade(2)));
            PlayerAccount afterUpgrade = (await repo.LoadAccountAsync("rich"))!;
            bool bagPushed = upgraded.PrePushes.Any(push => TMessageCodec.DecodeResponse(push).Method == "bag.UpdateBagData");
            Assert(upgraded.Err == 0 && (cheat
                    ? Count(afterUpgrade, Material) == GameServices.DefaultBuildingMaterialCount && !bagPushed
                    : Count(afterUpgrade, Material) == 2 && bagPushed),
                $"[{mode}] upgrading did not charge materials correctly (err {upgraded.Err} {upgraded.ErrMsg}, {Material} = {Count(afterUpgrade, Material)}, bag push {bagPushed})");

            // 建材不足：关闭时拒绝且不改档；开启时（加载已补满）照常升级。
            await Seed("poor", 5);
            ModuleResult poor = await module.HandleAsync(At("poor", T0 + 60), new TRequest("building.UpgradeBuilding", Upgrade(2)));
            PlayerAccount afterPoor = (await repo.LoadAccountAsync("poor"))!;
            PlayerBuildingEntry electric = afterPoor.Building!.Buildings.Single(item => item.Id == 2);
            Assert(cheat
                    ? poor.Err == 0
                    : poor.Err != 0 && Count(afterPoor, Material) == 5 && electric is { Tid: 11, Level: 1 } &&
                      electric.Status != BuildingProduction.Upgrading && afterPoor.Building!.WorkerStrength == 1_500_000,
                $"[{mode}] an upgrade without enough materials was not handled (err {poor.Err}, {Material} = {Count(afterPoor, Material)}, status {electric.Status})");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }
}

// ───────────────────────── GM 存档编辑 ─────────────────────────

static Task SaveEditorPureTest()
{
    PlayerAccount account = PlayerAccountFactory.CreateDefault("editor", 1);
    account = account with
    {
        Character = account.Character with { Supply = 1000 },
        Bag = new PlayerBag([new BagItem(13000, 30), new BagItem(10007, 5), new BagItem(13000, 40)]),
    };

    SaveEditor.EditResult supply = SaveEditor.SetCurrency(account, 5, 200);
    Assert(supply is { Ok: true, CurrencyChanged: true, BagChanged: false } && supply.Account.Character.Supply == 200,
        "setting a currency did not write the new amount");
    Assert(SaveEditor.SetCurrency(account, 5, 1000) is { Ok: true, Changed: false } &&
           !SaveEditor.SetCurrency(account, BuildingProduction.StrengthId, 5).Ok &&
           !SaveEditor.SetCurrency(account, 999, 5).Ok &&
           !SaveEditor.SetCurrency(account, 5, -1).Ok &&
           !SaveEditor.SetCurrency(account, 5, (long)int.MaxValue + 1).Ok,
        "currency edits accepted an unchanged, worker-strength, unknown or out-of-range value");

    SaveEditor.EditResult lowered = SaveEditor.SetItem(account, 13000, 10, knownItem: true);
    Assert(lowered is { Ok: true, BagChanged: true } &&
           lowered.Account.Bag!.Items.Select(item => (item.TemplateId, item.Num)).SequenceEqual([(13000, 10), (10007, 5)]),
        "lowering an item did not merge its duplicate rows into the first position");
    SaveEditor.EditResult removed = SaveEditor.SetItem(account, 10007, 0, knownItem: true);
    Assert(removed is { Ok: true, BagChanged: true } && removed.Account.Bag!.Items.Single(item => item.TemplateId == 10007).Num == 0 &&
           SaveEditor.SetItem(removed.Account, 10007, 0, knownItem: true) is { Ok: true, Changed: false } &&
           SaveEditor.Build("editor", removed.Account, new Dictionary<int, string>(), new Dictionary<int, ConfigItemInfo>())
               .Items.All(item => item.Id != 10007),
        "setting an item to 0 did not keep a Num=0 row hidden from the editor");
    Assert(SaveEditor.RefillNote(GameServices.OathShopCurrencyId, refillOnLoad: true).Length > 0 &&
           SaveEditor.RefillNote(GameServices.OathShopCurrencyId, refillOnLoad: false) == "" &&
           SaveEditor.RefillNote(13000, refillOnLoad: true) == "",
        "the refill note is wrong");
    Assert(SaveEditor.SetItem(account, 10007, 5, knownItem: true) is { Ok: true, Changed: false } &&
           SaveEditor.SetItem(account, 13000, 70, knownItem: true) is { Changed: true } merged &&
           merged.Account.Bag!.Items.Count(item => item.TemplateId == 13000) == 1,
        "an unchanged single row was rewritten, or duplicate rows with the same total were not merged");
    Assert(SaveEditor.SetItem(account, 10300, 2, knownItem: true).Account.Bag!.Items[^1] == new BagItem(10300, 2),
        "a known item could not be added by id");
    PlayerAccount junk = account with { Bag = new PlayerBag([new BagItem(99_999_999, 3)]) };
    Assert(SaveEditor.SetItem(junk, 99_999_999, 1, knownItem: false).Ok &&
           !SaveEditor.SetItem(junk, 99_999_999, 5, knownItem: false).Ok &&
           !SaveEditor.SetItem(account, 424_242, 1, knownItem: false).Ok &&
           !SaveEditor.SetItem(account, 13000, -1, knownItem: true).Ok,
        "an item missing from config could be increased, or a negative count was accepted");

    SaveEditor.Snapshot snapshot = SaveEditor.Build("editor", account,
        new Dictionary<int, string> { [5] = "燃料" },
        new Dictionary<int, ConfigItemInfo> { [13000] = new() { Name = "精鋭戦姫勲章", Quality = 4 } });
    Assert(snapshot.Currencies.All(currency => currency.Id != BuildingProduction.StrengthId) &&
           snapshot.Currencies.Single(currency => currency.Id == 5) is { Name: "燃料", Value: 1000 } &&
           snapshot.Currencies.Single(currency => currency.Id == 1).Name == "货币 1" &&
           snapshot.Items.Select(item => (item.Id, item.Num)).SequenceEqual([(10007, 5), (13000, 70)]) &&
           snapshot.Items[0].Name == "道具 10007" && snapshot.Items[1] is { Name: "精鋭戦姫勲章", Quality: 4 },
        "the editor snapshot is wrong");
    return Task.CompletedTask;
}

static async Task SaveEditorResyncTest()
{
    static async Task<List<TResponse>> Frames(MemoryStream sink, long from)
    {
        var frames = new List<TResponse>();
        using var read = new MemoryStream(sink.ToArray()[(int)from..]);
        while (await NetSocketFrameCodec.ReadFromServerAsync(read) is { } frame)
            frames.Add(TMessageCodec.DecodeResponse(frame.Payload));
        return frames;
    }
    static byte[] Push(string method) => TMessageCodec.EncodeResponse(new TResponse(Method: method, Time: 1));

    // 推送通道：空闲时立即写；处理请求期间推迟到应答之后，并按写出时的状态生成。
    var hub = new BlueOath.Server.Sessions.SessionPushHub();
    var sink = new MemoryStream();
    var channel = hub.Open(sink);
    Assert(!await hub.RequestResyncAsync("p", _ => Task.FromResult<IReadOnlyList<byte[]>>([Push("x")]), CancellationToken.None),
        "a resync was accepted for a profile without a session");
    hub.Bind(channel, "p");
    Assert(await hub.RequestResyncAsync("p", _ => Task.FromResult<IReadOnlyList<byte[]>>([Push("idle.Push")]), CancellationToken.None) &&
           (await Frames(sink, 0)).Select(frame => frame.Method).SequenceEqual(["idle.Push"]),
        "an idle session did not receive the resync immediately");
    long mark = sink.Length;
    string state = "old";
    await channel.BeginRequestAsync(CancellationToken.None);
    await hub.RequestResyncAsync("p", _ => Task.FromResult<IReadOnlyList<byte[]>>([Push("resync." + state)]), CancellationToken.None);
    Assert(sink.Length == mark, "a resync was written while the session was handling a request");
    state = "new";
    await channel.WriteAsync(Push("request.Response"), NetSocketFrameCodec.TypeData, CancellationToken.None);
    await channel.EndRequestAsync(CancellationToken.None);
    Assert((await Frames(sink, mark)).Select(frame => frame.Method).SequenceEqual(["request.Response", "resync.new"]),
        "a deferred resync was not written after the response, built from the latest state");
    hub.Close(channel);
    Assert(!await hub.RequestResyncAsync("p", _ => Task.FromResult<IReadOnlyList<byte[]>>([Push("x")]), CancellationToken.None),
        "a closed session still received resyncs");

    // GM 存档编辑：在账号锁内改档并落盘，在线时补发玩家信息与带删除标记的背包推送。
    string root = FindRepositoryRoot();
    string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-save-editor-" + Guid.NewGuid().ToString("N"));
    const string profileId = "save-editor";
    try
    {
        var repo = new SqliteGameRepository(dataRoot);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, 1_800_000_000);
        await repo.SaveAccountAsync(seed with
        {
            Character = seed.Character with { Supply = 1000 },
            Bag = new PlayerBag([new BagItem(13000, 50), new BagItem(10007, 3)]),
        });
        ServerOptions options = ServerOptions.Parse(
        [
            "--data=" + dataRoot, "--client-path=" + Path.Combine(root, "blueoath", "blueoath"), "--profile-id=" + profileId,
        ]);
        using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new GameServices(repo, options, loggerFactory);
        var gm = new GmCommandHandler(repo, services,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GmCommandHandler>.Instance, hub);
        var live = new MemoryStream();
        hub.Bind(hub.Open(live), profileId);

        SaveEditor.Snapshot? snapshot = await gm.GetSaveSnapshotAsync(profileId, CancellationToken.None);
        Assert(snapshot is not null && snapshot.Items.Single(item => item.Id == 13000).Name == "精鋭戦姫勲章" &&
               snapshot.Currencies.Single(currency => currency.Id == 5).Value == 1000 &&
               await gm.GetSaveSnapshotAsync("missing", CancellationToken.None) is null,
            $"the GM snapshot did not use config names, or a missing profile returned data: {string.Join(",", snapshot?.Items.Select(item => $"{item.Id}={item.Name}") ?? [])} supply={snapshot?.Currencies.FirstOrDefault(currency => currency.Id == 5)?.Value}");

        (bool removedOk, string removedMessage) = await gm.EditSaveAsync(profileId, "item", 10007, 0, CancellationToken.None);
        List<TResponse> removedPushes = await Frames(live, 0);
        PlayerAccount afterRemove = (await repo.LoadAccountAsync(profileId))!;
        Assert(removedOk && removedMessage.Contains("已同步到游戏") &&
               removedPushes.Select(frame => frame.Method).SequenceEqual(["user.UpdateUserInfo", "bag.UpdateBagData"]) &&
               ContainsSequence(removedPushes[1].Ret!, [0x08, 0x97, 0x4E, 0x10, 0x00]) &&
               afterRemove.Bag!.Items.Single(item => item.TemplateId == 10007).Num == 0,
            $"removing an item did not save and push a deletion marker ({removedMessage})");

        long before = live.Length;
        string command = await gm.ExecuteAsync($"set_currency {profileId} supply 200", CancellationToken.None);
        Assert(command.StartsWith("ok:") && (await repo.LoadAccountAsync(profileId))!.Character.Supply == 200 &&
               (await Frames(live, before)).Select(frame => frame.Method).SequenceEqual(["user.UpdateUserInfo", "bag.UpdateBagData"]),
            $"set_currency did not save and resync: {command}");
        Assert((await gm.ExecuteAsync($"set_item {profileId} 13000 -5", CancellationToken.None)).StartsWith("error:") &&
               !(await gm.EditSaveAsync("missing", "item", 13000, 1, CancellationToken.None)).Ok &&
               !(await gm.EditSaveAsync(profileId, "hero", 1, 1, CancellationToken.None)).Ok,
            "invalid save edits were accepted");

        hub.Close(hub.Open(live));
        var offline = new BlueOath.Server.Sessions.SessionPushHub();
        var offlineGm = new GmCommandHandler(repo, services,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GmCommandHandler>.Instance, offline);
        (bool offlineOk, string offlineMessage) = await offlineGm.EditSaveAsync(profileId, "item", 13000, 7, CancellationToken.None);
        Assert(offlineOk && offlineMessage.Contains("下次登录") &&
               (await repo.LoadAccountAsync(profileId))!.Bag!.Items.Single(item => item.TemplateId == 13000).Num == 7,
            $"an offline edit was not saved or reported correctly ({offlineMessage})");
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
}

static string FindClientConfigDir()
{
    string root = FindRepositoryRoot();
    string clientPath = Environment.GetEnvironmentVariable("BLUEOATH_CLIENT_PATH")
        ?? Path.Combine(root, "blueoath", "blueoath");
    return ConfigDbLoader.BuildConfigDir(clientPath);
}

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class FragmentedStream(byte[] data, int chunk) : MemoryStream
{
    private readonly byte[] _data = data; private int _offset;
    public override int Read(Span<byte> buffer) { if (_offset >= _data.Length) return 0; var count = Math.Min(Math.Min(chunk, buffer.Length), _data.Length - _offset); _data.AsSpan(_offset, count).CopyTo(buffer); _offset += count; return count; }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));
}
