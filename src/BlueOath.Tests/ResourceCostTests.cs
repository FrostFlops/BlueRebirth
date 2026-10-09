using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server;
using BlueOath.Server.Protocols;
using BlueOath.Storage;
using static TestSupport;

/// <summary>
/// 原来不扣、现在总是扣的养成消耗：技能升级教材、经验道具、实验室天赋、修理金币、前哨升级（与「真实消耗资源」选项无关）。
/// <see cref="PureTest"/> 按日服 1.4.0 配置核对 <see cref="UpgradeCosts"/> 的算价；
/// <see cref="ModuleTest"/> 走协议模块，选项关（默认）与开各跑一遍相同的场景：扣费、余额不足整单拒绝并重推货币与背包。
/// 由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class ResourceCostTests
{
    // Program.cs 的顶层函数 Assert / FindRepositoryRoot 会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private static string FindRepositoryRoot() => TestSupport.FindRepositoryRoot();

    // 日服 1.4.0 配置里的测试数据：
    // 技能 10501  max_level 10，upgrade_materials 1 级 [1,10185,4]、4 级 [1,10186,20]、9 级 [1,10187,50]，无附加材料；
    // 技能 12111  max_level 20，19 级主材料 [1,10184,50]，附加材料 [[1,18050,100]]；
    // 技能 41210  max_level 10，没有 upgrade_materials（客户端不让升级）；
    // 天赋 520202 levelup [[1,12801,10],[5,1,25000]]（链根 520201）；
    // 舰娘 10210511（默认秘书舰）fixed_money 70；誓约后 affection_cost 9000；
    // 前哨 1 级那一行 item_cost [[5,1,12000],[1,14002,2],[1,14003,1],[1,10029,1000],[1,10030,1000]]。
    private const int SkillBook = 10501, MubSkill = 12111, NoMaterialSkill = 41210;
    private const int BookC = 10185, BookA = 10184, MubBook = 18050;
    private const int Talent = 520202, TalentRoot = 520201, TalentItem = 12801;
    private const int Gold = 1, Currency = 5, Item = 1;

    public static Task PureTest()
    {
        string configDir = ConfigDbLoader.BuildConfigDir(Path.Combine(FindRepositoryRoot(), "blueoath", "blueoath"));
        PSkillUpgradeLoader.Load(configDir);
        RepairDiscountLoader.Load(configDir);
        TalentConfigLoader.Load(configDir);
        OutpostLevelLoader.Load(configDir);

        // 技能：按客户端当前等级取 upgrade_materials[min(level, #materials)]，满级与无材料返回 null。
        PSkillUpgradeConfig book = PSkillUpgradeLoader.Get(SkillBook) ?? throw new InvalidDataException("skill 10501 missing");
        Assert(book.MaxLevel == 10 && book.Materials.Count == 9, "config_pskill_dict_group 10501 was not loaded");
        Assert(Same(UpgradeCosts.SkillLevelUp(book, 1), [new(Item, BookC, 4)]) &&
               Same(UpgradeCosts.SkillLevelUp(book, 4), [new(Item, 10186, 20)]) &&
               Same(UpgradeCosts.SkillLevelUp(book, 9), [new(Item, 10187, 50)]),
            "skill books are not taken from upgrade_materials[level]");
        Assert(UpgradeCosts.SkillLevelUp(book, 10) is null && UpgradeCosts.SkillLevelUp(book, 11) is null &&
               UpgradeCosts.SkillLevelUp(book, 0) is null && UpgradeCosts.SkillLevelUp(null, 1) is null,
            "a maxed, level-0 or unknown skill was priced");
        Assert(UpgradeCosts.SkillLevelUp(PSkillUpgradeLoader.Get(NoMaterialSkill), 1) is null,
            "a skill without upgrade_materials was priced");
        PSkillUpgradeConfig mub = PSkillUpgradeLoader.Get(MubSkill) ?? throw new InvalidDataException("skill 12111 missing");
        Assert(Same(UpgradeCosts.SkillLevelUp(mub, 1), [new(Item, 10182, 10), new(Item, MubBook, 10)]) &&
               Same(UpgradeCosts.SkillLevelUp(mub, 19), [new(Item, BookA, 50), new(Item, MubBook, 100)]),
            "upgrade_materials_mub at the same index was not added");
        // 等级超过材料行数时取最后一行（math.min），且只在 level < max_level 时成立。
        var synthetic = new PSkillUpgradeConfig(5, [[1, 1, 1], [1, 2, 2]], []);
        Assert(Same(UpgradeCosts.SkillLevelUp(synthetic, 4), [new(Item, 2, 2)]), "levels past the material rows did not reuse the last row");

        // 天赋：请求天赋那一行的 levelup。
        Assert(Same(UpgradeCosts.Talent(TalentConfigLoader.Get(Talent)), [new(Item, TalentItem, 10), new(Currency, Gold, 25_000)]) &&
               UpgradeCosts.Talent(null).Count == 0,
            "the talent cost is not config_talent.levelup");

        // 前哨：当前等级那一行的 item_cost；满级（6）为空。
        Assert(Same(UpgradeCosts.OutpostUpgrade(OutpostLevelLoader.Get(1, 1)),
                   [new(Currency, Gold, 12_000), new(Item, 14002, 2), new(Item, 14003, 1), new(Item, 10029, 1000), new(Item, 10030, 1000)]) &&
               Same(UpgradeCosts.OutpostUpgrade(OutpostLevelLoader.Get(1, 0)),
                   [new(Currency, Gold, 10_000), new(Item, 14001, 2), new(Item, 14002, 1), new(Item, 10029, 1000), new(Item, 10030, 1000)]) &&
               UpgradeCosts.OutpostUpgrade(OutpostLevelLoader.Get(1, 6)).Count == 0,
            "the outpost upgrade cost is not the current level's item_cost");

        // 修理：ceil(fixed × affection_cost / 10000 × (1 − hp))；誓约 9000，未誓约 10000，缺配置不打折。
        Assert(RepairDiscountLoader.AffectionCost(married: true) == 9000 && RepairDiscountLoader.AffectionCost(married: false) == 10000,
            "config_affection_favor.affection_cost was not loaded per marriage state");
        Assert(UpgradeCosts.RepairGold(70, 10000, 0.5) == 35 && UpgradeCosts.RepairGold(70, 9000, 0.5) == 32 &&
               UpgradeCosts.RepairGold(1000, 9000, 0) == 900 && UpgradeCosts.RepairGold(1000, 0, 0.25) == 750 &&
               UpgradeCosts.RepairGold(1000, 9000, 1) == 0,
            "the repair price does not follow RepaireLogic:CalculateNeedAllGold");
        return Task.CompletedTask;
    }

    public static async Task ModuleTest()
    {
        string root = FindRepositoryRoot();
        string clientPath = Path.Combine(root, "blueoath", "blueoath");
        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-cost-" + Guid.NewGuid().ToString("N"));
        long t0 = new DateTimeOffset(2026, 10, 7, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            foreach (string profile in new[] { "cost-off", "cost-on" })
                await repo.SaveAccountAsync(PlayerAccountFactory.CreateDefault(profile, checked((int)t0)));
            using Microsoft.Extensions.Logging.ILoggerFactory loggerFactory =
                Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            GameServices Services(string profile, params string[] flags) => new(repo, ServerOptions.Parse(
                [.. new[] { "--no-cheats", "--data=" + dataRoot, "--client-path=" + clientPath, "--profile-id=" + profile }, .. flags]), loggerFactory);
            GameServices off = Services("cost-off");
            GameServices on = Services("cost-on", "--real-resource-cost");
            Assert(!off.Cheats.RealResourceCost && on.Cheats.RealResourceCost, "--real-resource-cost did not reach CheatOptions");

            // 这些消耗与「真实消耗资源」无关：选项关（默认）与开时场景与断言完全相同。
            foreach ((GameServices services, string profile) in new[] { (off, "cost-off"), (on, "cost-on") })
            {
                await SkillTest(services, profile, t0);
                await AddExpTest(services, profile, t0);
                await TalentTest(services, profile, t0);
                await RepairTest(services, profile, t0);
                await OutpostTest(services, profile, t0);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ───────────────────────── hero.StudySkill ─────────────────────────

    private static async Task SkillTest(GameServices services, string profile, long t0)
    {
        IReadOnlyList<PSkillEntry> skills =
        [
            new((uint)SkillBook, level: 1), new((uint)MubSkill, level: 19), new((uint)NoMaterialSkill, level: 1), new(10502, level: 10),
        ];
        Hero WithSkills(Hero hero) => hero with { PSkills = skills.Select(s => new PSkillEntry(s.PSkillId, s.PSkillExp, s.Level, s.Replace)).ToList() };
        HeroModule module = new(new HeroService(services), services);

        // 教材不足：被拒（不升级、不扣、重推货币与背包）；够了扣 upgrade_materials[1] = 10185 × 4。
        await Edit(services, profile, a => a with { Dock = a.Dock with { Heroes = [WithSkills(a.Dock.Heroes[0])] }, Bag = Bag((BookC, 3)) });
        ModuleResult shortBooks = await module.HandleAsync(At(services, profile, t0), StudySkill(1, SkillBook));
        PlayerAccount afterShort = await Load(services, profile);
        Assert(shortBooks.Err != 0 && SkillLevel(afterShort, SkillBook) == 1 && Count(afterShort, BookC) == 3 && IsResync(shortBooks),
            $"[{profile}] a skill upgrade without enough books was not rejected with a resync");
        await Edit(services, profile, a => a with { Bag = Bag((BookC, 5)) });
        ModuleResult paid = await module.HandleAsync(At(services, profile, t0), StudySkill(1, SkillBook));
        PlayerAccount afterPaid = await Load(services, profile);
        Assert(paid.Err == 0 && paid.Ret.Length > 0 && SkillLevel(afterPaid, SkillBook) == 2 && Count(afterPaid, BookC) == 1 &&
               paid.PrePushes.Select(Method).SequenceEqual(["hero.UpdateHeroBagData", "bag.UpdateBagData"]),
            $"[{profile}] a skill upgrade did not consume 4 rank-C books");

        // 附加材料：19 → 20 要 10184 × 50 + 18050 × 100，全部够才扣。
        await Edit(services, profile, a => a with { Bag = Bag((BookA, 50), (MubBook, 99)) });
        ModuleResult shortMub = await module.HandleAsync(At(services, profile, t0), StudySkill(1, MubSkill));
        PlayerAccount afterShortMub = await Load(services, profile);
        Assert(shortMub.Err != 0 && SkillLevel(afterShortMub, MubSkill) == 19 && Count(afterShortMub, BookA) == 50 &&
               Count(afterShortMub, MubBook) == 99,
            $"[{profile}] a partial payment was taken when the extra material was short");
        await Edit(services, profile, a => a with { Bag = Bag((BookA, 50), (MubBook, 100)) });
        ModuleResult paidMub = await module.HandleAsync(At(services, profile, t0), StudySkill(1, MubSkill));
        PlayerAccount afterMub = await Load(services, profile);
        Assert(paidMub.Err == 0 && SkillLevel(afterMub, MubSkill) == 20 && Count(afterMub, BookA) == 0 && Count(afterMub, MubBook) == 0,
            $"[{profile}] the extra material (upgrade_materials_mub) was not consumed");

        // 满级、无材料：客户端不发，服务端也拒绝且不改等级。
        ModuleResult maxed = await module.HandleAsync(At(services, profile, t0), StudySkill(1, 10502));
        ModuleResult noMaterial = await module.HandleAsync(At(services, profile, t0), StudySkill(1, NoMaterialSkill));
        PlayerAccount afterRefused = await Load(services, profile);
        Assert(maxed.Err != 0 && noMaterial.Err != 0 && SkillLevel(afterRefused, 10502) == 10 && SkillLevel(afterRefused, NoMaterialSkill) == 1,
            $"[{profile}] a maxed skill or one without materials was upgraded");

        // 存档里没有的技能：客户端按 1 级显示并扣 1 级的教材，升级后是 2 级（原来停在 1 级）。
        await Edit(services, profile, a => a with { Bag = Bag((BookC, 4)) });
        ModuleResult missing = await module.HandleAsync(At(services, profile, t0), StudySkill(1, 10503));
        PlayerAccount afterMissing = await Load(services, profile);
        Assert(missing.Err == 0 && SkillLevel(afterMissing, 10503) == 2 && Count(afterMissing, BookC) == 0,
            $"[{profile}] a skill missing from the save was not upgraded from the client's level 1");
    }

    // ───────────────────────── hero.AddExp ─────────────────────────

    private static async Task AddExpTest(GameServices services, string profile, long t0)
    {
        (int expItem, int perExp) = services.ExpPerItem.OrderBy(pair => pair.Value).First(pair => pair.Value > 0);
        HeroModule module = new(new HeroService(services), services);

        // 道具不足（包括一个都没有，原来照加经验）：整单拒绝，不加经验。
        await Edit(services, profile, a => a with { Bag = Bag((expItem, 1)) });
        (int Level, int Exp) before = LevelExp(await Load(services, profile));
        ModuleResult refused = await module.HandleAsync(At(services, profile, t0), AddExp(1, expItem, 2));
        PlayerAccount afterRefused = await Load(services, profile);
        Assert(refused.Err != 0 && LevelExp(afterRefused) == before && Count(afterRefused, expItem) == 1 && IsResync(refused),
            $"[{profile}] hero.AddExp without enough items was not rejected with a resync");
        await Edit(services, profile, a => a with { Bag = Bag() });
        ModuleResult none = await module.HandleAsync(At(services, profile, t0), AddExp(1, expItem, 1));
        Assert(none.Err != 0 && LevelExp(await Load(services, profile)) == before,
            $"[{profile}] hero.AddExp granted exp for items the player does not own");

        // 够了按数量扣，经验只按扣掉的道具加。
        await Edit(services, profile, a => a with { Bag = Bag((expItem, 3)) });
        ModuleResult paid = await module.HandleAsync(At(services, profile, t0), AddExp(1, expItem, 2));
        PlayerAccount afterPaid = await Load(services, profile);
        Assert(paid.Err == 0 && Count(afterPaid, expItem) == 1 && TotalExp(services, afterPaid) == TotalExp(services, before) + 2 * perExp &&
               paid.PrePushes.Select(Method).SequenceEqual(["hero.UpdateHeroBagData", "bag.UpdateBagData"]),
            $"[{profile}] hero.AddExp did not consume the items it granted exp for");
    }

    // ───────────────────────── talentTree.* ─────────────────────────

    private static async Task TalentTest(GameServices services, string profile, long t0)
    {
        var module = new TalentModule(services);

        // levelup 全部够才扣（缺一本记录也不扣金币）；成功时货币与背包推送在 TalentChange 之前。
        await Edit(services, profile, a => a with { Character = a.Character with { Gold = 30_000 }, Bag = Bag((TalentItem, 9)), Talent = null });
        ModuleResult refused = await module.HandleAsync(At(services, profile, t0), TalentRequest("talentTree.UnLockTalent", Talent));
        PlayerAccount afterRefused = await Load(services, profile);
        Assert(refused.Err != 0 && afterRefused.Talent is null && afterRefused.Character.Gold == 30_000 && Count(afterRefused, TalentItem) == 9 &&
               IsResync(refused),
            $"[{profile}] a talent without enough materials was not rejected with a resync");
        await Edit(services, profile, a => a with { Bag = Bag((TalentItem, 10)) });
        ModuleResult paid = await module.HandleAsync(At(services, profile, t0), TalentRequest("talentTree.UnLockTalent", Talent));
        PlayerAccount afterPaid = await Load(services, profile);
        Assert(paid.Err == 0 && afterPaid.Talent?.ActiveTalents.GetValueOrDefault(TalentRoot) == Talent &&
               afterPaid.Character.Gold == 5_000 && Count(afterPaid, TalentItem) == 0 &&
               paid.PrePushes.Select(Method).SequenceEqual(["user.UpdateUserInfo", "bag.UpdateBagData", "talentTree.TalentChange"]),
            $"[{profile}] a talent did not consume its levelup cost");

        // 金币差 1 同样整单拒绝（520203 的 levelup 与 520202 相同）。
        await Edit(services, profile, a => a with { Character = a.Character with { Gold = 24_999 }, Bag = Bag((TalentItem, 10)) });
        ModuleResult poor = await module.HandleAsync(At(services, profile, t0), TalentRequest("talentTree.UpgradeTalent", Talent + 1));
        PlayerAccount afterPoor = await Load(services, profile);
        Assert(poor.Err != 0 && afterPoor.Talent?.ActiveTalents.GetValueOrDefault(TalentRoot) == Talent &&
               afterPoor.Character.Gold == 24_999 && Count(afterPoor, TalentItem) == 10,
            $"[{profile}] a talent upgrade without enough gold was not rejected");
    }

    // ───────────────────────── repair.RepairHero ─────────────────────────

    private static async Task RepairTest(GameServices services, string profile, long t0)
    {
        const long half = PlayerAccountFactory.HpCoefficient / 2;
        PlayerAccount Damaged(PlayerAccount a, int gold)
        {
            Hero first = a.Dock.Heroes[0] with { CurHp = half, MarryTime = 0 };
            Hero married = first with { HeroId = 2, CurHp = half, MarryTime = checked((int)t0), MarryType = 1, EquipSlots = null };
            return a with { Character = a.Character with { Gold = gold }, Dock = a.Dock with { Heroes = [first, married] } };
        }
        long fixedMoney = ShipMainLoader.Get(PlayerAccountFactory.DefaultHeroTemplateId)?.FixedMoney ?? 0;
        Assert(fixedMoney == 70, "the default hero's fixed_money is not 70 in the JP config");
        var module = new RepairModule(new RepairService(services), services);

        // 35 + 誓约舰 ceil(63 × 0.5) = 32，共 67；差 1 金币整单拒绝（原来按原价 70 扣、可扣成负数）。
        await Edit(services, profile, a => Damaged(a, 66));
        ModuleResult refused = await module.HandleAsync(At(services, profile, t0), Repair(1, 2));
        PlayerAccount afterRefused = await Load(services, profile);
        Assert(refused.Err != 0 && afterRefused.Character.Gold == 66 && afterRefused.Dock.Heroes.All(h => h.CurHp == half) && IsResync(refused),
            $"[{profile}] a repair without enough gold was not rejected with a resync");
        await Edit(services, profile, a => Damaged(a, 67));
        ModuleResult paid = await module.HandleAsync(At(services, profile, t0), Repair(1, 2));
        PlayerAccount afterPaid = await Load(services, profile);
        Assert(paid.Err == 0 && afterPaid.Character.Gold == 0 && afterPaid.Dock.Heroes.All(h => h.CurHp == PlayerAccountFactory.HpCoefficient),
            $"[{profile}] a repair did not charge the client's total (married ships at 90%)");
    }

    // ───────────────────────── outpost.UpgradeBuilding ─────────────────────────

    private static async Task OutpostTest(GameServices services, string profile, long t0)
    {
        (int Id, int Num)[] cost = [(14002, 2), (14003, 1), (10029, 1000), (10030, 1000)];
        var module = new OutpostModule(new OutpostService(services), services);

        // 1 → 2 级扣 12000 金币与四种材料；缺 1 个铝整单拒绝。
        await Edit(services, profile, a => a with
        {
            Character = a.Character with { Gold = 12_000 },
            Bag = Bag([.. cost.Select(c => c.Id == 10030 ? (c.Id, c.Num - 1) : c)]),
            Outpost = PlayerAccountFactory.DefaultOutpost(),
        });
        ModuleResult refused = await module.HandleAsync(At(services, profile, t0), OutpostUpgrade(1));
        PlayerAccount afterRefused = await Load(services, profile);
        Assert(refused.Err != 0 && OutpostLevel(afterRefused, 1) == 1 && afterRefused.Character.Gold == 12_000 &&
               Count(afterRefused, 14002) == 2 && Count(afterRefused, 10030) == 999 &&
               refused.PrePushes.Select(Method).Contains("user.UpdateUserInfo") && refused.PrePushes.Select(Method).Contains("bag.UpdateBagData"),
            $"[{profile}] an outpost upgrade without enough materials was not rejected with a resync");
        await Edit(services, profile, a => a with { Bag = Bag(cost) });
        ModuleResult paid = await module.HandleAsync(At(services, profile, t0), OutpostUpgrade(1));
        PlayerAccount afterPaid = await Load(services, profile);
        Assert(paid.Err == 0 && OutpostLevel(afterPaid, 1) == 2 && afterPaid.Character.Gold == 0 && cost.All(c => Count(afterPaid, c.Id) == 0) &&
               paid.PrePushes.Select(Method).Contains("user.UpdateUserInfo") && paid.PrePushes.Select(Method).Contains("bag.UpdateBagData"),
            $"[{profile}] an outpost upgrade did not consume the current level's item_cost");
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private static bool Same(IReadOnlyList<CostItem>? actual, IReadOnlyList<CostItem> expected) =>
        actual is not null && actual.SequenceEqual(expected);

    private static GameContext At(GameServices services, string profileId, long now) =>
        new() { ProfileId = profileId, Now = checked((int)now), Ct = CancellationToken.None, Services = services };

    /// <summary>经 GameServices 读取（含加载迁移）后改写并保存，账号缓存与数据库保持一致。</summary>
    private static async Task Edit(GameServices services, string profile, Func<PlayerAccount, PlayerAccount> edit) =>
        await services.SaveAccountAsync(edit(await services.GetOrCreateAccountAsync(profile, CancellationToken.None)));

    private static Task<PlayerAccount> Load(GameServices services, string profile) =>
        services.GetOrCreateAccountAsync(profile, CancellationToken.None);

    private static PlayerBag Bag(params (int Id, int Num)[] items) =>
        new([.. items.Select(item => new BagItem(item.Id, item.Num))]);

    private static int Count(PlayerAccount account, int templateId) =>
        account.Bag?.Items.Where(item => item.TemplateId == templateId).Sum(item => item.Num) ?? 0;

    private static int SkillLevel(PlayerAccount account, int skillId) =>
        account.Dock.Heroes[0].PSkills?.FirstOrDefault(skill => skill.PSkillId == skillId)?.Level ?? 0;

    private static (int Level, int Exp) LevelExp(PlayerAccount account) => (account.Dock.Heroes[0].Level, account.Dock.Heroes[0].Exp);

    /// <summary>舰娘 1 的累计经验（已升的等级按 ExpNeeded 折回经验），用来比较加了多少经验。</summary>
    private static long TotalExp(GameServices services, (int Level, int Exp) state)
    {
        long total = state.Exp;
        for (int level = 1; level < state.Level; level++) total += services.ExpNeeded.GetValueOrDefault(level, 500);
        return total;
    }

    private static long TotalExp(GameServices services, PlayerAccount account) => TotalExp(services, LevelExp(account));

    private static int OutpostLevel(PlayerAccount account, int id) =>
        account.Outpost?.Buildings.Single(building => building.Id == id).Level ?? 0;

    /// <summary>被拒时只有两条纠正推送：玩家信息（货币）与背包。</summary>
    private static bool IsResync(ModuleResult result) =>
        result.PrePushes.Select(Method).SequenceEqual(["user.UpdateUserInfo", "bag.UpdateBagData"]);

    private static string Method(byte[] push) => TMessageCodec.DecodeResponse(push).Method;

    private static TRequest StudySkill(uint heroId, int skillId) =>
        new("hero.StudySkill", new ProtocolPackage().Write(0x08, heroId).Write(0x10, (ulong)skillId).ToArray());

    private static TRequest AddExp(uint heroId, int itemId, int num) =>
        new("hero.AddExp", new ProtocolPackage().Write(0x08, heroId)
            .Write(0x12, new ProtocolPackage().Write(0x08, 1UL).Write(0x10, (ulong)itemId).Write(0x18, (ulong)num).ToArray()).ToArray());

    private static TRequest TalentRequest(string method, int talentId) =>
        new(method, new ProtocolPackage().Write(0x08, (ulong)talentId).ToArray());

    private static TRequest Repair(params uint[] heroIds)
    {
        var package = new ProtocolPackage();
        foreach (uint heroId in heroIds) package.Write(0x08, heroId);
        return new TRequest("repair.RepairHero", package.ToArray());
    }

    private static TRequest OutpostUpgrade(int buildingId) =>
        new("outpost.UpgradeBuilding", new ProtocolPackage().Write(0x08, (ulong)buildingId).ToArray());
}
