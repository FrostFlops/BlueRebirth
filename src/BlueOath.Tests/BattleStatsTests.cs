using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server;
using BlueOath.Server.Configs;
using BlueOath.Server.Protocols;
using BlueOath.Storage;
using static TestSupport;

/// <summary>
/// 「战斗数值」作弊（--cheat-battle）的测试：<see cref="PureTest"/> 覆盖 BattleStats 的公式（等级成长、好感档、装备强化、
/// 显示脚本折算、舰载机数）、JP 1.4.0 配置下存档里的 Z39 与临时舰船，以及 copy.StartBase 编码在作弊开启时逐字节不变；
/// <see cref="ModuleTest"/> 走 CopyModule，确认启动参数决定下发哪一套属性。由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class BattleStatsTests
{
    // Program.cs 的顶层函数 Assert / FindRepositoryRoot 会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private static string FindRepositoryRoot() => TestSupport.FindRepositoryRoot();

    // 存档 runtime\jp\profiles.db 里第一舰队的旗舰：Z39（40140211）24 级、已誓约、强化 火力5/装甲3/雷装34/对雷3/对空13、
    // 1 号栏装 53.3cm三連装魚雷（30421，强化 0）。
    private const int Z39 = 40140211, TorpedoMount = 30421, Z39Level = 24;
    private const int StoryCopy = 5011;
    // 临时舰船：Z1（ship_main 40110112）25 级，hp 等字段为 -1（按 config_ship_main 在该等级的值），装备强化 10；
    // 航母 11500211（ship_main 20640114）带三件舰载机，plane_number 25。
    private const int AssistZ1 = 10201024, AssistCarrier = 11500211;
    private const long HpCoefficient = PlayerAccountFactory.HpCoefficient;

    private sealed record EquipView(int TemplateId, int Index, long PlaneNum, Dictionary<int, long> Attrs);

    private sealed record ShipView(uint HeroId, Dictionary<int, long> Attrs, long CurHp, List<EquipView> Equips);

    public static Task PureTest()
    {
        FormulaChecks();
        ConfigChecks();
        return Task.CompletedTask;
    }

    // ───────────── 公式（手工配置） ─────────────

    private static BattleStatsCatalog SmallCatalog() => new(
        ShowAttrs:
        [
            new(1, "hp", "basicattr_display", [1, 138]),
            new(8, "attack", "basicattr_display", [8, 139]),
            new(9, "defense", "basicattr_display", [9, 142]),
            new(10, "torpedo_attack", "basicattr_display", [10, 140]),
            new(21, "main_gun_range", "range_display", [21]),
            new(24, "main_gun_cd", "variable_2_display", [24, 81]),
            new(25, "torpedo_num", "variable_1_display", [25, 82]),
            new(63, "main_gun_cd", "airattack_display_2", [24, 81]),
            new(88, "PlaneAir_carry", "specialattr_display", [88]),
            new(3101, "attack_grade", "specialattr_display", [3101]),
        ],
        AttributeLevels: new Dictionary<int, int> { [1] = 1, [24] = 24, [100] = 320 },
        PropValueTypes: new Dictionary<int, int>
        {
            [138] = 0, [139] = 0, [140] = 0, [142] = 0, [3201] = 0, [81] = 1, [82] = 1, [88] = 1, [89] = 1, [90] = 1,
        },
        ValueEffects: new Dictionary<int, IReadOnlyList<(int Attr, double Value)>>
        {
            [49] = [(3201, 0.001)], [52] = [(138, 0.01)], [53] = [(139, 0.01)], [54] = [(142, 0.01)], [55] = [(140, 0.01)],
            [60] = [(82, 1)], [61] = [(81, -1)], [64] = [(90, 1)],
        },
        AffectionTiers:
        [
            new(1, false, 0, 309_999, []),
            new(2, false, 310_000, 609_999, []),
            new(3, false, 610_000, 809_999, [(49, 20), (52, 2), (53, 2)]),
            new(5, false, 1_000_000, 1_000_000, [(52, 6), (53, 6)]),
            new(6, true, 0, 1_999_999, [(49, 80), (52, 8), (53, 8), (54, 8), (55, 8)]),
            new(7, true, 2_000_000, 2_000_000, [(52, 10)]),
        ],
        UnmarriedBounds: (0, 1_000_000),
        MarriedBounds: (1_000_000, 2_000_000),
        PlaneNumbers: new Dictionary<int, IReadOnlyList<long>>());

    private static readonly IReadOnlyDictionary<string, double> Z39Fields = new Dictionary<string, double>
    {
        ["hp"] = 1089, ["hp_levelup"] = 1089, ["attack"] = 112, ["attack_levelup"] = 112,
        ["defense"] = 165, ["defense_levelup"] = 165, ["torpedo_attack"] = 881, ["torpedo_attack_levelup"] = 881,
        ["main_gun_range"] = 1, ["main_gun_cd"] = 20000, ["torpedo_num"] = 2,
        ["ship_torpedo_attack"] = 0, ["crit"] = 0, ["anti_crit"] = 0, ["hit"] = 100, ["dodge"] = 60,
    };

    private static readonly IReadOnlyDictionary<string, double> NoOverrides = new Dictionary<string, double>();

    private static BattleEquipInput TorpedoInput(int slot, int enhanceLv) =>
        new(slot, TorpedoMount, enhanceLv, [[10, 30], [3200, 20]], [[10, 7], [3200, 5]], [9]);

    private static void FormulaChecks()
    {
        BattleStatsCatalog catalog = SmallCatalog();

        // 等级成长：客户端 base + ⌊(attribute_level − 1) × levelup / 100⌋；离线版原来 base + levelup × (等级 − 1)。
        Assert(BattleStats.GrowthFactor(catalog, Z39Level) == 23 && BattleStats.GrowthFactor(catalog, 100) == 319 &&
               BattleStats.GrowthFactor(catalog, 55) == 0, "growth factor is not attribute_level − 1 (0 without a level row)");
        Assert(BattleStats.Grown(1089, 1089, 23) == 1339 && BattleStats.Grown(881, 881, 23) == 1083 &&
               BattleStats.Grown(1089, 1089, 319) == 4562 && BattleStats.Grown(165, 165, 0) == 165,
            "level growth does not match HeroBasicAttr");
        Assert(ShipMainLoader.Leveled(1089, 1089, Z39Level) == 26136,
            "the legacy (cheat) growth formula changed; Battle ON must stay byte-for-byte");

        // 好感档：按是否誓约夹到上下限再找档。
        Assert(BattleStats.Tier(catalog, 0, false)?.Id == 1 && BattleStats.Tier(catalog, 500_000, false)?.Id == 2 &&
               BattleStats.Tier(catalog, 700_000, false)?.Id == 3 && BattleStats.Tier(catalog, 1_500_000, false)?.Id == 5 &&
               BattleStats.Tier(catalog, 900_000, false) is null && BattleStats.Tier(catalog, 500_000, true)?.Id == 6 &&
               BattleStats.Tier(catalog, 1_000_000, true)?.Id == 6 && BattleStats.Tier(catalog, 3_000_000, true)?.Id == 7,
            "affection tier lookup does not clamp to the (un)married bounds");

        // 装备：equip_prop + enhance_prop × 强化等级；只在 enhance_prop 的属性单独成项；0 去掉。
        IReadOnlyList<(int Attr, long Value)> enhanced = BattleStats.EquipAttrs(
            new BattleEquipInput(0, 30091, 3, [[8, 7], [3200, 20]], [[8, 1], [3200, 5], [12, 2]], [3]));
        Assert(enhanced.SequenceEqual([(8, 10L), (3200, 35L), (12, 6L)]), "equipment enhancement is not equip_prop + enhance × level");
        Assert(BattleStats.EquipAttrs(new BattleEquipInput(0, 30091, 0, [[8, 7]], [[12, 2]], [3])).SequenceEqual([(8, 7L)]),
            "an enhance-only attribute at enhance level 0 was not dropped");

        // value_effect：万分比属性 ⌊值 × 10000⌋ × ⌊强度⌋。
        var buffs = new Dictionary<int, double>();
        BattleStats.AddBuffs(catalog, buffs, [(52, 8.9), (61, 5), (999, 3)]);
        Assert(buffs[138] == 800 && buffs[81] == -5 && buffs.Count == 2, "DisposeAttrBuff scaling is wrong");
        Assert(BattleStatsLoader.ParseValues("138,0.01|82,1| bad |17,10").SequenceEqual([(138, 0.01), (82, 1d), (17, 10d)]),
            "config_value_effect.values parsing is wrong");

        // Z39：24 级、誓约（+8%）、强化、突破（主炮 −5 秒、鱼雷 +1、轰炸机每组 +10），1 号栏鱼雷、3 号栏轰炸机。
        var ship = new BattleShipInput(Z39Fields, NoOverrides, Z39Level, LevelGrowth: true, ScoutNum: 1,
            FlatAdds: [(8, 5), (9, 3), (10, 34), (137, 500), (3200, 100)],
            HasAffection: true, Affection: 1_000_000, Married: true,
            BreakValueEffects: [(61, 5), (60, 1), (64, 10)],
            PlaneNumbers: [5, 5, 25, 5, 5, 5],
            Equips: [TorpedoInput(0, 0), new BattleEquipInput(2, 30333, 0, [[43, 50]], [], [19])]);
        BattleShipStats stats = BattleStats.Compute(catalog, ship);
        var attrs = stats.Attrs.ToDictionary(a => a.Attr, a => a.Value);
        Assert(attrs[1] == 1447 && attrs[8] == 154 && attrs[9] == 222,
            $"hp/attack/defense are not ⌈(base + level + intensify) × 1.08⌉: {attrs[1]}/{attrs[8]}/{attrs[9]}");
        Assert(attrs[10] == 1239 - 30, $"torpedo did not fold the equipment into the percentage and leave it to the equip: {attrs[10]}");
        Assert(attrs[5] == 1 && attrs[15] == 0 && attrs[17] == 0 && attrs[18] == 0 && attrs[19] == 100 && attrs[20] == 60,
            "non-display battle attributes (scout, crit, hit, dodge) were lost");
        // 只下发离线版原有的属性 + 对空/制空：射程、装填、鱼雷数、百分比修正、改造的伤害修正、战力评分都不下发。
        Assert(stats.Attrs.Select(a => a.Attr).SequenceEqual([1, 5, 8, 9, 10, 15, 17, 18, 19, 20]),
            $"unexpected TBattleShip.Attr ids: {string.Join(",", stats.Attrs.Select(a => a.Attr))}");
        Assert(stats.Equips.Count == 2 &&
               stats.Equips[0].TemplateId == TorpedoMount && stats.Equips[0].PlaneNum == 5 &&
               stats.Equips[0].Attrs.SequenceEqual([(10, 30L), (3200, 20L)]) &&
               stats.Equips[1].PlaneNum == 35 && stats.Equips[1].Attrs.SequenceEqual([(43, 50L)]),
            "equipment plane counts are not plane_number[slot] (+ break squadron bonus for planes)");

        // 未誓约、低好感：没有百分比加成，数值就是基础 + 强化（装备另算）。
        BattleShipStats plain = BattleStats.Compute(catalog, ship with { Married = false, Affection = 500_000, BreakValueEffects = [] });
        var plainAttrs = plain.Attrs.ToDictionary(a => a.Attr, a => a.Value);
        Assert(plainAttrs[1] == 1339 && plainAttrs[8] == 142 && plainAttrs[10] == 1117 && plain.Equips[1].PlaneNum == 25,
            "an unmarried, low-affection ship did not get plain base + intensify values");

        // 临时舰船：不是 -1 的字段直接取用（不成长），其余按 config_ship_main 成长；没有好感。
        BattleShipStats assist = BattleStats.Compute(catalog, ship with
        {
            Overrides = new Dictionary<string, double> { ["hp"] = 3000, ["hit"] = 130 },
            FlatAdds = [], HasAffection = false, BreakValueEffects = [], Equips = [],
        });
        var assistAttrs = assist.Attrs.ToDictionary(a => a.Attr, a => a.Value);
        Assert(assistAttrs[1] == 3000 && assistAttrs[19] == 130 && assistAttrs[8] == 137,
            "assist-ship overrides are not taken verbatim with the rest grown from config_ship_main");
    }

    // ───────────── JP 1.4.0 配置 ─────────────

    private static string LoadConfig()
    {
        string configDir = ConfigDbLoader.BuildConfigDir(Path.Combine(FindRepositoryRoot(), "blueoath", "blueoath"));
        ShipMainLoader.Load(configDir);
        ShipBreakLoader.Load(configDir);
        RemouldConfigLoader.Load(configDir);
        EquipLoader.Load(configDir);
        AssistShipLoader.Load(configDir);
        ChapterCopyLoader.Load(configDir);
        CopyBattleLoader.Load(configDir);
        BattleStatsLoader.Load(configDir);
        return configDir;
    }

    private static Hero SavedZ39(uint heroId, long curHp) => new(heroId, Z39, Z39Level,
        Affection: 1_000_000, MarryTime: 1_791_518_348, CurHp: curHp, EquipSlots: [73, 0, 0, 0, 0, 0],
        Intensify: [new(8, 5), new(9, 3), new(10, 34), new(11, 3), new(12, 13)],
        PSkills: [new PSkillEntry(4000, level: 1), new PSkillEntry(11941, level: 4)]);

    private static PlayerEquip SavedEquip(uint heroId) => new([new EquipItem(73, TorpedoMount, HeroId: heroId)]);

    private static void ConfigChecks()
    {
        LoadConfig();
        BattleStatsCatalog catalog = BattleStatsLoader.Catalog
            ?? throw new InvalidOperationException("battle-stat config did not load from the JP client");
        Assert(catalog.AttributeLevels[24] == 24 && catalog.AttributeLevels[100] == 320,
            "config_ship_levelup attribute_level was not loaded");
        Assert(catalog.ShowAttrs.Any(a => a is { Id: 1, Field: "hp", Display: "basicattr_display" } && a.Params.SequenceEqual([1, 138])) &&
               catalog.ShowAttrs.Any(a => a is { Id: 24, Display: "variable_2_display" }) &&
               catalog.ShowAttrs.All(a => a.Id is not (17 or 19)),
            "config_attribute girl_if_show rows were not loaded");
        Assert(catalog.AffectionTiers.Count == 7 && BattleStats.Tier(catalog, 1_000_000, true)?.Id == 6 &&
               BattleStats.Tier(catalog, 500_000, false)?.Id == 2, "config_affection_favor tiers were not loaded");

        // 存档里的 Z39：详情页 耐久 1447 / 火力 154 / 装甲 222 / 雷装 1239（含鱼雷 +30）/ 对雷 222 / 对空 329；
        // 离线版原来下发 26136 / 2688 / 3960 / 21144（不含强化、好感与装备）。
        Hero z39 = SavedZ39(55, 7_000_000_000);
        BattleShipInput input = BattleStatsInputs.ForHero(catalog, z39, SavedEquip(55).Items.ToDictionary(e => e.EquipId))
            ?? throw new InvalidOperationException("Z39 config_ship_main row missing");
        var real = BattleStats.Compute(catalog, input).Attrs.ToDictionary(a => a.Attr, a => a.Value);
        Assert(real[1] == 1447 && real[8] == 154 && real[9] == 222 && real[10] == 1209 && real[11] == 222 && real[12] == 329,
            $"Z39 does not match the detail page: hp {real[1]} atk {real[8]} def {real[9]} torp {real[10]} tdef {real[11]} aa {real[12]}");

        // copy.StartBase：作弊开启（realStats = null，默认）逐字节等于不传参数；属性仍是离线版原来的值。
        var heroes = new List<Hero> { z39 };
        PlayerCharacter character = PlayerAccountFactory.CreateDefault("battle-stats", 1).Character;
        byte[] legacyDefault = ProtocolEncoder.EncodeStartBaseRet(StoryCopy, heroes, character, [55], playerEquip: SavedEquip(55));
        byte[] legacyExplicit = ProtocolEncoder.EncodeStartBaseRet(StoryCopy, heroes, character, [55], playerEquip: SavedEquip(55),
            realStats: null);
        Assert(StripSeed(legacyDefault).SequenceEqual(StripSeed(legacyExplicit)), "the cheat (legacy) encoding depends on realStats = null");
        ShipView legacy = Ships(legacyDefault).Single();
        Assert(legacy.Attrs[1] == 26136 && legacy.Attrs[8] == 2688 && legacy.Attrs[9] == 3960 && legacy.Attrs[10] == 21144 &&
               legacy.CurHp == HpCoefficient && legacy.Equips.Single().PlaneNum == 100 &&
               legacy.Equips.Single().Attrs.Count == 2 && legacy.Equips.Single().Attrs[10] == 30,
            "the legacy (cheat) ship attributes changed");
        Assert(legacy.Attrs.Keys.SequenceEqual([1, 5, 8, 9, 10, 11, 14, 15, 17, 18, 19, 20]), "the legacy attribute list changed");

        // 作弊关闭：详情页数值、当前耐久、装备按配置的舰载机数。
        byte[] realPayload = ProtocolEncoder.EncodeStartBaseRet(StoryCopy, heroes, character, [55], playerEquip: SavedEquip(55),
            realStats: catalog);
        ShipView realShip = Ships(realPayload).Single();
        Assert(realShip.HeroId == 55 && realShip.Attrs[1] == 1447 && realShip.Attrs[8] == 154 && realShip.Attrs[9] == 222 &&
               realShip.Attrs[10] == 1209 && realShip.Attrs[19] == 100 && realShip.Attrs[20] == 60 && realShip.Attrs[5] == 1,
            "copy.StartBase did not carry the detail-page attributes");
        Assert(realShip.Attrs.Keys.Order().SequenceEqual([1, 5, 8, 9, 10, 11, 12, 14, 15, 16, 17, 18, 19, 20]) &&
               realShip.Attrs[11] == 222 && realShip.Attrs[12] == 329 && realShip.Attrs[14] == 0 && realShip.Attrs[16] == 0,
            $"unexpected real attribute set: {string.Join(",", realShip.Attrs.Keys)}");
        Assert(realShip.CurHp == 7_000_000_000, "copy.StartBase did not carry the hero's current HP");
        EquipView torpedo = realShip.Equips.Single();
        Assert(torpedo.TemplateId == TorpedoMount && torpedo.Index == 0 && torpedo.PlaneNum == 5 &&
               torpedo.Attrs.Count == 2 && torpedo.Attrs[10] == 30 && torpedo.Attrs[3200] == 20,
            "the equipment was not encoded with config plane_number and its attributes");
        Assert(StripSeed(realPayload).Length > 0 && !realPayload.SequenceEqual(legacyDefault), "real stats did not change the payload");
        Assert(Ships(ProtocolEncoder.EncodeStartBaseRet(StoryCopy, [SavedZ39(55, -5)], character, [55], playerEquip: SavedEquip(55),
                   realStats: catalog)).Single().CurHp == 0 &&
               Ships(ProtocolEncoder.EncodeStartBaseRet(StoryCopy, [SavedZ39(55, HpCoefficient * 2)], character, [55],
                   playerEquip: SavedEquip(55), realStats: catalog)).Single().CurHp == HpCoefficient,
            "current HP was not clamped to [0, HpCoefficient]");

        // 临时舰船 Z1：离线版原来把 -1 原样下发（耐久 -1）；关闭作弊时按 config_ship_main 25 级（耐久 1227），装备强化 10、满耐久。
        ShipView legacyZ1 = Ships(ProtocolEncoder.EncodeStartBaseRet(StoryCopy, [], character, [AssistZ1])).Single();
        Assert(legacyZ1.Attrs[1] == -1 && legacyZ1.Equips.All(e => e.PlaneNum == 100), "the legacy assist-ship encoding changed");
        ShipView realZ1 = Ships(ProtocolEncoder.EncodeStartBaseRet(StoryCopy, [], character, [AssistZ1], realStats: catalog)).Single();
        Assert(realZ1.Attrs[1] == 1227 && realZ1.Attrs[8] == 123 && realZ1.Attrs[9] == 180 && realZ1.Attrs[10] == 964 &&
               realZ1.Attrs[19] == 130 && realZ1.CurHp == HpCoefficient,
            $"assist ship Z1 did not resolve -1 fields from config_ship_main at level 25: hp {realZ1.Attrs[1]}");
        Assert(realZ1.Equips.Count == 6 && realZ1.Equips[0].TemplateId == 30422 && realZ1.Equips[0].Attrs[10] == 90 + 15 * 10 &&
               realZ1.Equips.All(e => e.PlaneNum == 5), "assist equipment did not use equip_level and plane_number");
        ShipView carrier = Ships(ProtocolEncoder.EncodeStartBaseRet(StoryCopy, [], character, [AssistCarrier], realStats: catalog)).Single();
        Assert(carrier.Equips.Take(3).All(e => e.PlaneNum == 25), "carrier squadrons were not plane_number (25) per slot");
    }

    // ───────────── CopyModule（启动参数） ─────────────

    public static async Task ModuleTest()
    {
        LoadConfig();
        string root = FindRepositoryRoot();
        const int T0 = 1_800_000_000;
        static byte[] Start(uint heroId) => new ProtocolPackage().Write(0x10, StoryCopy).Write(0x48, 1UL)
            .Write(0x6A, new ProtocolPackage().Write(0x08, heroId).ToArray()).ToArray();

        async Task<ShipView> Sortie(string label, params string[] flags)
        {
            string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-battle-stats-" + Guid.NewGuid().ToString("N"));
            string profileId = "battle-stats-" + label;
            try
            {
                var repo = new SqliteGameRepository(dataRoot);
                PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, T0);
                uint heroId = seed.Dock.Heroes[0].HeroId;
                seed = seed with
                {
                    Dock = seed.Dock with { Heroes = [SavedZ39(heroId, 6_000_000_000), .. seed.Dock.Heroes.Skip(1)] },
                    Equip = SavedEquip(heroId),
                };
                await repo.SaveAccountAsync(seed);
                ServerOptions options = ServerOptions.Parse(
                [
                    .. flags, "--data=" + dataRoot, "--client-path=" + Path.Combine(root, "blueoath", "blueoath"), "--profile-id=" + profileId,
                ]);
                using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
                    Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
                var services = new GameServices(repo, options, loggerFactory);
                var copy = new CopyModule(new BattleService(services, new DailyCopyService(services)));
                ModuleResult result = await copy.HandleAsync(
                    new GameContext { ProfileId = profileId, Now = T0 + 10, Ct = CancellationToken.None, Services = services },
                    new TRequest("copy.StartBase", Start(heroId)));
                Assert(result.Err == 0 && result.Ret.Length > 0, $"copy.StartBase failed ({label}): {result.ErrMsg}");
                return Ships(result.Ret).Single();
            }
            finally
            {
                if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
            }
        }

        Assert(ServerOptions.Parse([]).Cheats.Battle && !ServerOptions.Parse(["--no-cheats"]).Cheats.Battle &&
               !ServerOptions.Parse(["--cheat-battle=off"]).Cheats.Battle && !CheatOptions.None.Battle,
            "--cheat-battle defaults are wrong (on by default, off with --no-cheats / --cheat-battle=off)");

        ShipView cheat = await Sortie("default");
        Assert(cheat.Attrs[1] == 26136 && cheat.CurHp == HpCoefficient && cheat.Equips.Single().PlaneNum == 100,
            "with the battle cheat on (default) copy.StartBase did not keep the legacy attributes");
        ShipView real = await Sortie("off", "--cheat-battle=off");
        Assert(real.Attrs[1] == 1447 && real.Attrs[8] == 154 && real.Attrs[10] == 1209 && real.CurHp == 6_000_000_000 &&
               real.Equips.Single().PlaneNum == 5,
            "with --cheat-battle=off copy.StartBase did not send the detail-page attributes and current HP");
        ShipView noCheats = await Sortie("none", "--no-cheats");
        Assert(noCheats.Attrs[1] == 1447, "--no-cheats did not switch battles to the detail-page attributes");
    }

    // ───────────── 解码 ─────────────

    /// <summary>去掉 RandomSeed（字段 2，当前时间），两次编码才能逐字节比较。</summary>
    private static byte[] StripSeed(byte[] payload)
    {
        var output = new ProtocolPackage();
        foreach ((int field, int wire, ulong value, byte[] bytes) in Fields(payload))
        {
            if (field == 2) continue;
            int key = (field << 3) | wire;
            if (wire == 0) output.Write(key, value);
            else output.Write(key, bytes);
        }
        return output.ToArray();
    }

    private static Dictionary<int, long> AttrMap(IEnumerable<byte[]> entries)
    {
        var map = new Dictionary<int, long>();
        foreach (byte[] entry in entries)
        {
            var fields = Fields(entry);
            int id = (int)fields.Where(f => f.Field == 1).Select(f => f.Value).FirstOrDefault();
            long value = unchecked((long)fields.Where(f => f.Field == 2).Select(f => f.Value).FirstOrDefault());
            map[id] = value;
        }
        return map;
    }

    /// <summary>TStartBaseRet → BattlePlayer(1) → BattlePlayerList(1) → FleetInfo(7) → Ships(4)。</summary>
    private static List<ShipView> Ships(byte[] startBaseRet)
    {
        byte[] playerList = Fields(startBaseRet).First(f => f.Field == 1).Bytes;
        byte[] player = Fields(playerList).First(f => f.Field == 1).Bytes;
        byte[] fleet = Fields(player).First(f => f.Field == 7).Bytes;
        var ships = new List<ShipView>();
        foreach (var shipField in Fields(fleet).Where(f => f.Field == 4))
        {
            var fields = Fields(shipField.Bytes);
            var equips = new List<EquipView>();
            foreach (var equipField in fields.Where(f => f.Field == 7))
            {
                var e = Fields(equipField.Bytes);
                ulong Get(int field) => e.Where(f => f.Field == field).Select(f => f.Value).FirstOrDefault();
                equips.Add(new EquipView((int)Get(1), (int)Get(2), (long)Get(3),
                    AttrMap(e.Where(f => f.Field == 4).Select(f => f.Bytes))));
            }
            ships.Add(new ShipView(
                (uint)fields.First(f => f.Field == 1).Value,
                AttrMap(fields.Where(f => f.Field == 5).Select(f => f.Bytes)),
                (long)fields.Where(f => f.Field == 6).Select(f => f.Value).FirstOrDefault(),
                equips));
        }
        return ships;
    }
}
