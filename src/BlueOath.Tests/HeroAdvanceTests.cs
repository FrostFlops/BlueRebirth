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
/// 突破消耗道具的规则：<see cref="CommonBreakTest"/> 覆盖突破页用 config_ship_fleet.common_break_item
/// （日服 17512 姫羅の万象核）顶替素材舰娘（客户端 ShipLogic:CheckAdvanceCosumeNumFit：舰娘数 + 道具数 == 需求数），
/// <see cref="MubTest"/> 覆盖彩色船 hero.HeroAdvanceMUB 的碎片折算与未配置道具时的拒绝，
/// <see cref="IntegrationTest"/> 起真实服务端走 TCP。客户端目录经 <see cref="TestSupport.FindClientPath"/>
/// （BLUEOATH_CLIENT_PATH 可指向未热更的 1.4.0；红奥克兰 break_item_optional 那几行只在热更配置里，没有时跳过）。
/// 由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class HeroAdvanceTests
{
    // Program.cs 的顶层函数会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    // 日服配置（热更与 1.4.0 相同）：Z39 40140212 突破需 1 艘 Z39（40140211–40140216），40140214 需 2 艘；
    // ソーマレス 30110313、サヴェージ 30110412 各需 1 艘。common_break_item 与金币消耗都从配置读取。
    private const int Z39Adv1 = 40140212, Z39Adv3 = 40140214, Z39Base = 40140211;
    private const int SaumarezAdv2 = 30110313, SavageAdv1 = 30110412;

    // 热更配置的红奥克兰（赤 / 赤·改）突破 5→6：break_item 为空、break_item_optional = [4, 5]，即任选 5 艘品质 4（SSR）。
    // 北卡罗来纳 10540111 是品质 4。
    private const int RedOakland = 10240316, RedOaklandKai = 10240326, SsrMaterial = 10540111;

    // 彩色船：イータ 12550112 → break_item_mub [120004, 30]，12550113 → [120004, 90]，可用 18051（1 个折 1）
    // 与 17512（1 个折 config_parameter 507 × 508 = 30）；ゼータ 12650111 是第一次突破，break_item_mub 为空。
    private const int MubEta = 12550112, MubFirstBreak = 12650111;
    private const int EtaFragment = 120004, OmnipotentFragment = 18051, MubCore = 17512, OtherFragment = 120005;

    private static string ConfigDir() => ConfigDbLoader.BuildConfigDir(TestSupport.FindClientPath());

    private static ServerOptions Options(string dataRoot, string profileId) => ServerOptions.Parse(
        ["--no-cheats", "--real-resource-cost=off", "--data=" + dataRoot,
         "--client-path=" + TestSupport.FindClientPath(), "--profile-id=" + profileId]);

    /// <summary>hero.HeroAdvance 参数（THeroAdvanceArgs）：repeated 字段按未打包编码，与客户端一致。</summary>
    private static byte[] AdvanceArgs(uint heroId, IEnumerable<uint>? heroes = null, IEnumerable<int>? items = null)
    {
        var package = new ProtocolPackage().Write(0x08, heroId);
        foreach (uint material in heroes ?? Enumerable.Empty<uint>()) package.Write(0x10, material);
        foreach (int item in items ?? Enumerable.Empty<int>()) package.Write(0x18, checked((ulong)item));
        return package.ToArray();
    }

    /// <summary>hero.HeroAdvanceMUB 参数：HeroId(1)、repeated TAdvanceMubItemInfo{ItemId(1), ItemNum(2)}(2)。</summary>
    private static byte[] MubArgs(uint heroId, params (int ItemId, int ItemNum)[] items)
    {
        var package = new ProtocolPackage().Write(0x08, heroId);
        foreach ((int itemId, int itemNum) in items)
            package.Write(0x12, new ProtocolPackage()
                .Write(0x08, checked((ulong)itemId)).Write(0x10, checked((ulong)itemNum)).ToArray());
        return package.ToArray();
    }

    private static int GoldCost(int templateId) =>
        ShipBreakLoader.Get(templateId)?.CurrencyCost is [_, 1, long cost]
            ? checked((int)cost)
            : throw new InvalidDataException($"config_ship_break {templateId} has no gold cost");

    private static int BreakTo(int templateId) =>
        int.TryParse(ShipBreakLoader.Get(templateId)?.BreakTo, out int next) && next > 0
            ? next
            : throw new InvalidDataException($"config_ship_break {templateId} has no break_to");

    private static int ItemCount(PlayerAccount account, int templateId) =>
        account.Bag?.Items.FirstOrDefault(item => item.TemplateId == templateId)?.Num ?? 0;

    private static int Quality(GameServices services, int templateId) =>
        ShipMainLoader.Get(templateId) is { } ship && services.ShipInfos.TryGetValue(checked((int)ship.ShipInfoId), out var info)
            ? checked((int)info.Quality)
            : 0;

    /// <summary>热更配置的红奥克兰突破行：break_item 为空、break_item_optional = [品质, 数量]；未热更的 1.4.0 没有，返回 null。</summary>
    private static (int Quality, int Count)? RedOaklandOptional()
    {
        ShipBreakLoader.Load(ConfigDir());
        foreach (int templateId in new[] { RedOakland, RedOaklandKai })
            if (ShipBreakLoader.Get(templateId) is not { BreakItem: not { Count: > 0 }, BreakItemOptional: [_, _] })
                return null;
        return ShipBreakLoader.Get(RedOakland)!.BreakItemOptional is [long quality, long count]
            ? (checked((int)quality), checked((int)count))
            : null;
    }

    public static async Task CommonBreakTest()
    {
        const string profileId = "common-break";
        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-common-break-" + Guid.NewGuid().ToString("N"));
        (int Quality, int Count)? optional = RedOaklandOptional();
        const int redOaklandItems = 7; // 3 艘 + 2 个、0 艘 + 5 个
        const int normalItems = 5;
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, 1);
            List<Hero> dock =
            [
                new Hero(10, Z39Adv1, 25, Advance: 1),
                new Hero(20, SaumarezAdv2, 28, Advance: 2),
                new Hero(30, SavageAdv1, 31, Advance: 1),
                new Hero(40, Z39Base, 1),
                new Hero(50, Z39Adv3, 45, Advance: 3),
                new Hero(60, Z39Base, 1),
                new Hero(70, Z39Base, 1),
                new Hero(80, Z39Adv3, 45, Advance: 3),
                new Hero(90, Z39Base, 1, Lock: true),
            ];
            if (optional is not null)
                dock.AddRange(
                [
                    new Hero(100, RedOakland, 80, Advance: 5),
                    new Hero(101, SsrMaterial, 1),
                    new Hero(102, SsrMaterial, 1),
                    new Hero(103, SsrMaterial, 1),
                    new Hero(110, RedOaklandKai, 80, Advance: 5),
                ]);
            int gold = 3_000_000;
            await repo.SaveAccountAsync(seed with
            {
                Character = seed.Character with { SecretaryId = 10, Gold = gold },
                Dock = new HeroDock(dock),
                // 万象核以外的道具：18051 是彩色船用的万能碎片，普通突破不认。
                Bag = new PlayerBag([new BagItem(17512, normalItems + (optional is null ? 0 : redOaklandItems)),
                    new BagItem(OmnipotentFragment, 1)]),
            });

            var services = new GameServices(repo, Options(dataRoot, profileId), NullLoggerFactory.Instance);
            var heroes = new HeroService(services);
            int core = services.GetCommonBreakItem(Z39Adv1);
            Assert(core == 17512 && services.GetCommonBreakItem(SaumarezAdv2) == core &&
                   services.GetCommonBreakItem(MubEta) == core && services.GetCommonBreakItem(0) == 0 &&
                   services.GetCommonBreakItem(99_999_999) == 0,
                $"config_ship_fleet.common_break_item was not resolved through ship_main → ship_info.sf_id (got {core})");

            async Task<HeroService.AdvanceResult> Advance(byte[] args) =>
                await heroes.BuildAdvanceRetAsync(new TRequest("hero.HeroAdvance", args), profileId, CancellationToken.None);
            async Task<PlayerAccount> Saved() => await repo.LoadAccountAsync(profileId)
                ?? throw new InvalidDataException("the common-break account disappeared");
            int cores = normalItems + (optional is null ? 0 : redOaklandItems);

            // 拒绝：道具多于需求、不是万象核、舰娘 + 道具超过需求、万象核与其他道具混在一起、锁定的素材。
            foreach ((byte[] args, string reason) in new[]
                     {
                         (AdvanceArgs(10, items: [core, core]), "two common items for a one-ship break"),
                         (AdvanceArgs(10, items: [OmnipotentFragment]), "an item that is not the common break item"),
                         (AdvanceArgs(10, [40], [core]), "a duplicate ship plus a common item for a one-ship break"),
                         (AdvanceArgs(10, items: [core, OmnipotentFragment]), "the common item mixed with another item"),
                         (AdvanceArgs(50, [90], [core]), "a locked material ship next to a common item"),
                         (AdvanceArgs(50, [60]), "one material ship for a two-ship break"),
                     })
            {
                HeroService.AdvanceResult rejected = await Advance(args);
                Assert(!rejected.Changed, $"hero advance accepted {reason}");
            }
            PlayerAccount untouched = await Saved();
            Assert(ItemCount(untouched, core) == cores && ItemCount(untouched, OmnipotentFragment) == 1 &&
                   untouched.Character.Gold == gold && untouched.Dock.Heroes.Count == dock.Count,
                "a rejected hero advance changed the bag, gold or dock");

            if (optional is { } rule)
            {
                // 红奥克兰：break_item_optional = [品质, 数量]，万象核同样计入数量（突破页的道具按钮看的是 [2] = 5）。
                Assert(rule.Count == 5 && Quality(services, SsrMaterial) == rule.Quality &&
                       services.GetCommonBreakItem(RedOakland) == core,
                    $"Red Oakland's break_item_optional is not [quality {Quality(services, SsrMaterial)}, 5]: [{rule.Quality}, {rule.Count}]");
                HeroService.AdvanceResult short4 = await Advance(AdvanceArgs(100, [101, 102, 103], [core]));
                Assert(!short4.Changed, "Red Oakland broke with 3 ships + 1 common item out of 5");
                HeroService.AdvanceResult over6 = await Advance(AdvanceArgs(110, items: Enumerable.Repeat(core, 6)));
                Assert(!over6.Changed, "Red Oakland broke with 6 common items out of 5");

                HeroService.AdvanceResult mixed = await Advance(AdvanceArgs(100, [101, 102, 103], [core, core]));
                Assert(mixed.Changed && mixed.UpdatedHero?.TemplateId == BreakTo(RedOakland) &&
                       mixed.ConsumedHeroIds.Order().SequenceEqual([101U, 102U, 103U]),
                    "Red Oakland did not break with 3 quality-matching ships + 2 common items");
                gold -= GoldCost(RedOakland);
                cores -= 2;
                HeroService.AdvanceResult itemsOnly = await Advance(AdvanceArgs(110, items: Enumerable.Repeat(core, 5)));
                Assert(itemsOnly.Changed && itemsOnly.UpdatedHero?.TemplateId == BreakTo(RedOaklandKai) &&
                       itemsOnly.ConsumedHeroIds.Count == 0,
                    "Red Oakland Kai did not break with 5 common items");
                gold -= GoldCost(RedOaklandKai);
                cores -= 5;
                PlayerAccount afterRed = await Saved();
                Assert(ItemCount(afterRed, core) == cores && afterRed.Character.Gold == gold &&
                       afterRed.Dock.Heroes.All(hero => hero.HeroId is not (101 or 102 or 103)),
                    "the Red Oakland breaks charged the wrong items, gold or ships");
            }

            // 一艘的突破：1 个万象核即可，船坞里现成的同名舰（40）不动。
            HeroService.AdvanceResult z39 = await Advance(AdvanceArgs(10, items: [core]));
            Assert(z39.Changed && z39.ConsumedHeroIds.Count == 0 &&
                   z39.UpdatedHero is { TemplateId: var z39Next, Advance: 2 } && z39Next == BreakTo(Z39Adv1),
                "Z39 did not break with one common item in place of a duplicate");
            gold -= GoldCost(Z39Adv1);
            cores--;
            PlayerAccount afterZ39 = await Saved();
            Assert(ItemCount(afterZ39, core) == cores && afterZ39.Character.Gold == gold &&
                   afterZ39.Dock.Heroes.Any(hero => hero.HeroId == 40) &&
                   afterZ39.Dock.Heroes.Single(hero => hero.HeroId == 10).TemplateId == BreakTo(Z39Adv1),
                "the common-item break did not persist the target, the item count or the gold");

            HeroService.AdvanceResult saumarez = await Advance(AdvanceArgs(20, items: [core]));
            Assert(saumarez.Changed && saumarez.UpdatedHero?.TemplateId == BreakTo(SaumarezAdv2),
                "Saumarez did not break with one common item");
            gold -= GoldCost(SaumarezAdv2);
            cores--;

            // 两艘的突破：1 艘同名舰 + 1 个万象核（舰娘与道具相加），或 2 个万象核。
            HeroService.AdvanceResult half = await Advance(AdvanceArgs(50, [60], [core]));
            Assert(half.Changed && half.ConsumedHeroIds.SequenceEqual([60U]) &&
                   half.UpdatedHero?.TemplateId == BreakTo(Z39Adv3),
                "a two-ship break did not accept one duplicate plus one common item");
            gold -= GoldCost(Z39Adv3);
            cores--;
            HeroService.AdvanceResult tooMany = await Advance(AdvanceArgs(80, [70], [core, core]));
            Assert(!tooMany.Changed, "a two-ship break accepted one duplicate plus two common items");
            HeroService.AdvanceResult twoItems = await Advance(AdvanceArgs(80, items: [core, core]));
            Assert(twoItems.Changed && twoItems.ConsumedHeroIds.Count == 0 &&
                   twoItems.UpdatedHero?.TemplateId == BreakTo(Z39Adv3),
                "a two-ship break did not accept two common items");
            gold -= GoldCost(Z39Adv3);
            cores -= 2;

            PlayerAccount drained = await Saved();
            Assert(cores == 0 && ItemCount(drained, core) == 0 && drained.Character.Gold == gold &&
                   ItemCount(drained, OmnipotentFragment) == 1 &&
                   drained.Dock.Heroes.Any(hero => hero.HeroId == 70) &&
                   drained.Dock.Heroes.All(hero => hero.HeroId != 60),
                "the common-item breaks charged the wrong items, gold or ships");

            // 背包里没有万象核了：客户端不校验持有量，服务端必须拒绝。
            HeroService.AdvanceResult notOwned = await Advance(AdvanceArgs(30, items: [core]));
            Assert(!notOwned.Changed, "hero advance accepted a common item the player does not own");
            PlayerAccount final = await Saved();
            Assert(final.Character.Gold == gold && final.Dock.Heroes.Single(hero => hero.HeroId == 30).TemplateId == SavageAdv1,
                "a rejected advance without the common item changed the account");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }

    public static async Task MubTest()
    {
        const string profileId = "mub-advance";
        string dataRoot = Path.Combine(Path.GetTempPath(), "blueoath-mub-advance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new SqliteGameRepository(dataRoot);
            PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, 1);
            int gold = 1_000_000;
            await repo.SaveAccountAsync(seed with
            {
                Character = seed.Character with { SecretaryId = 10, Gold = gold },
                Dock = new HeroDock([new Hero(10, MubEta, 25, Advance: 1), new Hero(20, MubFirstBreak, 15),
                    new Hero(30, MubFirstBreak, 15)]),
                Bag = new PlayerBag(
                [
                    new BagItem(MubCore, 2), new BagItem(EtaFragment, 20), new BagItem(OmnipotentFragment, 60),
                    new BagItem(OtherFragment, 30),
                ]),
            });
            var services = new GameServices(repo, Options(dataRoot, profileId), NullLoggerFactory.Instance);
            var heroes = new HeroService(services);
            async Task<HeroService.AdvanceResult> Advance(byte[] args) =>
                await heroes.BuildAdvanceMubRetAsync(new TRequest("hero.HeroAdvanceMUB", args), profileId, CancellationToken.None);
            async Task<PlayerAccount> Saved() => await repo.LoadAccountAsync(profileId)
                ?? throw new InvalidDataException("the Mubar account disappeared");

            int perCore = services.Parameter(507, 30) * services.Parameter(508, 1);
            Assert(perCore == 30 && ShipBreakLoader.Get(MubEta)?.BreakItemMub is [EtaFragment, 30] &&
                   ShipBreakLoader.Get(BreakTo(MubEta))?.BreakItemMub is [EtaFragment, 90] &&
                   ShipBreakLoader.Get(MubFirstBreak)?.BreakItemMub is not { Count: > 0 },
                "the Mubar break rows or the core conversion changed; update the test");

            foreach ((byte[] args, string reason) in new[]
                     {
                         (MubArgs(10, (MubCore, 2)), "two cores (60) for 30 fragments"),
                         (MubArgs(10, (MubCore, 1), (EtaFragment, 1)), "a core plus a fragment (31) for 30"),
                         (MubArgs(10, (OtherFragment, 30)), "another ship's fragments"),
                         (MubArgs(10), "no items for a break that needs 30 fragments"),
                     })
                Assert(!(await Advance(args)).Changed, $"hero.HeroAdvanceMUB accepted {reason}");

            // 1 个 17512 折 30 个碎片。
            HeroService.AdvanceResult first = await Advance(MubArgs(10, (MubCore, 1)));
            Assert(first.Changed && first.UpdatedHero?.TemplateId == BreakTo(MubEta),
                "hero.HeroAdvanceMUB rejected one core for 30 fragments");
            gold -= GoldCost(MubEta);

            // 同一道具拆成两条时按合计数比持有量：50 + 40 = 90 > 60。
            Assert(!(await Advance(MubArgs(10, (OmnipotentFragment, 50), (OmnipotentFragment, 40)))).Changed,
                "hero.HeroAdvanceMUB checked ownership per entry instead of per item");
            HeroService.AdvanceResult second = await Advance(
                MubArgs(10, (MubCore, 1), (EtaFragment, 20), (OmnipotentFragment, 40)));
            Assert(second.Changed && second.UpdatedHero?.TemplateId == BreakTo(BreakTo(MubEta)),
                "hero.HeroAdvanceMUB rejected core + fragments + omnipotent fragments adding up to 90");
            gold -= GoldCost(BreakTo(MubEta));
            PlayerAccount afterEta = await Saved();
            Assert(ItemCount(afterEta, MubCore) == 0 && ItemCount(afterEta, EtaFragment) == 0 &&
                   ItemCount(afterEta, OmnipotentFragment) == 20 && afterEta.Character.Gold == gold,
                "hero.HeroAdvanceMUB charged the wrong items or gold");

            // 第一次突破没有 break_item_mub：不要道具。
            HeroService.AdvanceResult free = await Advance(MubArgs(20));
            Assert(free.Changed && free.UpdatedHero?.TemplateId == BreakTo(MubFirstBreak),
                "hero.HeroAdvanceMUB rejected a break that needs no items");
            gold -= GoldCost(MubFirstBreak);
            // 客户端切换舰娘后残留上一艘选的道具，对这种行不校验照样发出：照常突破，道具忽略且不扣（含没有的万象核）。
            HeroService.AdvanceResult stale = await Advance(
                MubArgs(30, (OmnipotentFragment, 1), (OtherFragment, 30), (MubCore, 1)));
            Assert(stale.Changed && stale.UpdatedHero?.TemplateId == BreakTo(MubFirstBreak),
                "hero.HeroAdvanceMUB rejected a break that needs no items because of leftover items");
            gold -= GoldCost(MubFirstBreak);
            PlayerAccount final = await Saved();
            Assert(ItemCount(final, OmnipotentFragment) == 20 && ItemCount(final, OtherFragment) == 30 &&
                   final.Character.Gold == gold,
                "a Mubar break without break_item_mub changed the bag or charged the wrong gold");
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }

    public static async Task IntegrationTest()
    {
        string root = TestSupport.FindRepositoryRoot();
        string serverDll = Path.Combine(root, "src", "BlueOath.Server", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net8.0", "BlueOath.Server.dll");
        Assert(File.Exists(serverDll), "server assembly is missing; build the solution first");
        ShipBreakLoader.Load(ConfigDir());
        int cost = GoldCost(Z39Adv1), next = BreakTo(Z39Adv1);
        const int core = 17512;

        string data = Path.Combine(Path.GetTempPath(), "blueoath-common-break-it-" + Guid.NewGuid().ToString("N"));
        const string profileId = "common-break-player";
        Directory.CreateDirectory(data);
        var repo = new SqliteGameRepository(data);
        PlayerAccount seed = PlayerAccountFactory.CreateDefault(profileId, checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 60);
        await repo.SaveAccountAsync(seed with
        {
            Character = seed.Character with { Gold = 100_000 },
            Dock = seed.Dock with { Heroes = [seed.Dock.Heroes.Single() with { TemplateId = Z39Adv1, Level = 25, Advance = 1 }] },
            Bag = new PlayerBag([new BagItem(core, 1)]),
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
                     serverDll, "--port=0", "--game-login-port=0", "--region=jp", "--no-cheats", "--real-resource-cost=off",
                     "--data=" + data, "--profile-id=" + profileId, "--client-path=" + TestSupport.FindClientPath(),
                 })
            startInfo.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            Assert(process.Start(), "common-break test server did not start");
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            string readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))
                ?? throw new InvalidDataException("server did not report ready");
            using var ready = JsonDocument.Parse(readyLine);
            int port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            NetworkStream stream = client.GetStream();

            async Task<(TResponse Response, List<TResponse> Pushes)> RoundTrip(string method, byte[]? args)
            {
                await NetSocketFrameCodec.WriteAsync(stream, TMessageCodec.EncodeRequest(new TRequest(method, args, 1)),
                    NetSocketFrameCodec.TypeData, timeout.Token);
                List<TResponse> pushes = [];
                while (true)
                {
                    var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                    Assert(frame is not null, $"the server closed the connection during {method}");
                    TResponse response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                    if (response.IsResponse == 1) return (response, pushes);
                    pushes.Add(response);
                }
            }

            await RoundTrip("player.Login", GameLoginCodec.Encode(new TArgLogin(profileId, 1, "open", "hash")));
            await RoundTrip("hero.GetHeroInfo", null); // 收齐登录同步推送

            // 与日志里失败的请求同形：{HeroId, ConsumeItems=[17512]}，ConsumedHeros 为空。
            byte[] args = AdvanceArgs(1, items: [core]);
            var (response, pushes) = await RoundTrip("hero.HeroAdvance", args);
            Assert(response.Err == 0, $"a common-item break was rejected over TCP: {response.ErrMsg}");
            List<string> methods = pushes.Select(push => push.Method ?? "").ToList();
            Assert(methods.Contains("hero.UpdateHeroBagData") && methods.Contains("user.UpdateUserInfo"),
                $"the break did not refresh the hero and the gold before its response: {string.Join(", ", methods)}");
            byte[] deletion = PlayerDataCodec.Encode(new BagGridInfo(core, 0));
            Assert(pushes.Any(push => push.Method == "bag.UpdateBagData" && push.Ret is { } ret &&
                       ret.AsSpan().IndexOf(deletion) >= 0),
                "the bag push did not carry the used-up common item with an explicit Num=0");

            PlayerAccount saved = await repo.LoadAccountAsync(profileId)
                ?? throw new InvalidDataException("the common-break account disappeared");
            Hero hero = saved.Dock.Heroes.Single();
            Assert(hero.TemplateId == next && hero.Advance == 2 && ItemCount(saved, core) == 0 &&
                   saved.Character.Gold == 100_000 - cost,
                "the common-item break was not persisted");

            var (again, againPushes) = await RoundTrip("hero.HeroAdvance", AdvanceArgs(1, items: [core]));
            Assert(again.Err != 0 && againPushes.Count == 0,
                "a common-item break without the item was not rejected atomically");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
}
