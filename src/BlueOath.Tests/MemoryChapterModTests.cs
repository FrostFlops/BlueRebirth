using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueOath.Mods;
using BlueOath.Protocol;
using BlueOath.Server.Protocols;
using Microsoft.Extensions.Logging;
using static TestSupport;

/// <summary>
/// Mod 声明的图鉴回忆章节（memory-chapters.json）的测试：<see cref="LoaderTest"/> 覆盖 mod.json 规则与各项校验，
/// <see cref="MergeTest"/> 覆盖并入 ChapterCopyLoader 的幂等性与编码，<see cref="IntegrationTest"/> 起真实服务端走登录与回忆战斗。
/// 这三项的夹具都在临时目录里生成，不依赖仓库 Mods 下的真实文件；<see cref="ShippedModTest"/> 检查仓库里随附的
/// cn-exclusive.mod。由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class MemoryChapterModTests
{
    // Program.cs 的顶层函数 Assert / FindRepositoryRoot 会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private static string FindRepositoryRoot() => TestSupport.FindRepositoryRoot();

    // 国服 2020 春节剧情「春节风波」：日服配置里有节点 50301–50307（50306 是战斗，config_copy 503060），
    // 但没有章节引用它们；12010 在两个客户端的 config_chapter 里都不存在。12000 是日服自己的春节剧情章节。
    private const int ModChapter = 12010, JpChapter = 12000, BattleCopy = 50306, BattleRid = 503060, BattleFleet = 100500001;
    private static readonly int[] SpringCopies = [50301, 50302, 50303, 50304, 50305, 50306, 50307];

    // 日服已有的回忆战斗节点（圣诞剧情章节 10041 的 50205），作为分类的对照。
    private const int JpMemoryBattleCopy = 50205;

    // config_copy_display 里有、但日服 config_chapter 没有任何章节列出的关卡（与 50301–50307 一样无主）。
    private const int FreeCopy = 42101;

    private static string ConfigDir() => ConfigDbLoader.BuildConfigDir(Path.Combine(FindRepositoryRoot(), "blueoath", "blueoath"));

    public static Task LoaderTest()
    {
        string configDir = ConfigDir();
        string root = Path.Combine(Path.GetTempPath(), "blueoath-memory-chapters-" + Guid.NewGuid().ToString("N"));
        string modsRoot = Path.Combine(root, "Mods");
        try
        {
            // 什么都不加载时也写一行 Information，源码布局缺 <客户端父目录>\Mods 时能从服务端日志看出来。
            var logger = new ListLogger();
            Assert(MemoryChapterModLoader.Load(modsRoot, "jp-1.4.0", configDir, logger).Count == 0 &&
                   logger.Messages.Count == 1 && logger.Has(LogLevel.Information, $"no Mods directory at {modsRoot}"),
                "a missing Mods root must load nothing and say so once");
            WriteMod(modsRoot, "no-chapters.mod", Manifest("no-chapters.mod"), null);
            logger.Messages.Clear();
            Assert(MemoryChapterModLoader.Load(modsRoot, "jp-1.4.0", configDir, logger).Count == 0 &&
                   logger.Messages.Count == 1 && logger.Has(LogLevel.Information, $"no {MemoryChapterModLoader.FileName} under {modsRoot}"),
                "a Mods root without memory-chapters.json must load nothing and say so once");

            // 按目录名的序处理：a 先于 b，所以 12010 归 a。
            WriteMod(modsRoot, "a-cn-exclusive.mod", Manifest("cn-exclusive.mod"), Chapters(
                (ModChapter, SpringCopies),
                (JpChapter, [50301]),
                (12011, [50301, 99_999_999])));
            // 一个关卡只能属于一章：40001 在 config_chapter 10001 里，50303 已归 a 的 12010。42101 不属于任何章节。
            WriteMod(modsRoot, "b-other.mod", Manifest("other.mod"), Chapters(
                (ModChapter, [50302]),
                (12012, [FreeCopy]),
                (12013, [40001]),
                (12014, [50303])));
            WriteMod(modsRoot, "c-disabled.mod", Manifest("disabled.mod", enabled: false), Chapters((12020, [50304])));
            WriteMod(modsRoot, "d-cn-only.mod", Manifest("cn-only.mod", targets: "\"cn-1.5.20\""), Chapters((12021, [50305])));
            // 结构错误忽略整个文件，同文件里合法的章节也不加载。
            WriteMod(modsRoot, "e-empty.mod", Manifest("empty.mod"), Chapters((12030, []), (12031, [50301])));
            WriteMod(modsRoot, "f-repeat.mod", Manifest("repeat.mod"), Chapters((12032, [50305, 50305])));
            WriteMod(modsRoot, "g-zero.mod", Manifest("zero.mod"), Chapters((0, [50301]), (12033, [50301])));
            WriteMod(modsRoot, "h-negative.mod", Manifest("negative.mod"), Chapters((12034, [50301, -5])));
            WriteMod(modsRoot, "i-no-manifest", null, Chapters((12035, [50301])));
            WriteMod(modsRoot, "j-duplicate.mod", Manifest("duplicate.mod"), Chapters((12036, [50301]), (12036, [50302])));
            WriteMod(modsRoot, "k-malformed.mod", Manifest("malformed.mod"), "{\"memoryChapters\": [{\"chapterId\": 12037, ");
            WriteMod(modsRoot, "l-null-entry.mod", Manifest("null-entry.mod"), "{\"memoryChapters\": [null]}");

            logger.Messages.Clear();
            IReadOnlyList<MemoryChapterModDefinition> jp = MemoryChapterModLoader.Load(modsRoot, "jp-1.4.0", configDir, logger);
            Assert(jp.Count == 2 &&
                   jp[0] is { ChapterId: ModChapter, ModId: "cn-exclusive.mod" } && jp[0].Copies.SequenceEqual(SpringCopies) &&
                   jp[1] is { ChapterId: 12012, ModId: "other.mod" } && jp[1].Copies.SequenceEqual([FreeCopy]),
                "the JP client did not get exactly 12010 (first mod) and 12012: " +
                string.Join("; ", jp.Select(x => $"{x.ChapterId}@{x.ModId}")));
            Assert(logger.Has(LogLevel.Warning, $"chapter {JpChapter} already exists in config_chapter"),
                "a chapter id from the client config_chapter was not reported as a collision");
            Assert(logger.Has(LogLevel.Warning, "chapter 12011 lists copies missing from config_copy_display (99999999)"),
                "a chapter with a copy missing from config_copy_display was not reported");
            Assert(logger.Has(LogLevel.Warning, $"other.mod: chapter {ModChapter} is already declared by another mod"),
                "a chapter id declared by two mods was not reported");
            Assert(logger.Has(LogLevel.Warning, "other.mod: chapter 12013 lists copies already used by another chapter (40001)"),
                "a copy that belongs to a config_chapter chapter was not rejected");
            Assert(logger.Has(LogLevel.Warning, "other.mod: chapter 12014 lists copies already used by another chapter (50303)"),
                "a copy already registered by an earlier mod chapter was not rejected");
            Assert(logger.Has(LogLevel.Information, "other.mod: loaded 1 of 4 memory chapters"),
                "the second mod's summary was not logged");
            string[] invalid =
                ["e-empty.mod", "f-repeat.mod", "g-zero.mod", "h-negative.mod", "i-no-manifest", "j-duplicate.mod",
                 "k-malformed.mod", "l-null-entry.mod"];
            foreach (string directory in invalid)
                Assert(logger.Has(LogLevel.Warning, Path.Combine(modsRoot, directory, MemoryChapterModLoader.FileName)),
                    $"the invalid file in {directory} was not reported as ignored");
            Assert(!logger.Messages.Any(x => x.Message.Contains("disabled.mod") || x.Message.Contains("cn-only.mod")),
                "disabled and other-client mods must be skipped silently");
            Assert(logger.Has(LogLevel.Information, "cn-exclusive.mod: loaded 1 of 3 memory chapters"),
                "the per-mod summary was not logged");

            // targetClients 双向过滤：国服客户端只拿到声明了 cn-1.5.20 的 Mod。
            IReadOnlyList<MemoryChapterModDefinition> cn = MemoryChapterModLoader.Load(modsRoot, "cn-1.5.20", configDir, new ListLogger());
            Assert(cn.Count == 1 && cn[0] is { ChapterId: 12021, ModId: "cn-only.mod" },
                "targetClients did not select the CN-only mod for the CN client");

            // 读不到客户端配置时不能确认章节与关卡，全部跳过。
            var noConfig = new ListLogger();
            Assert(MemoryChapterModLoader.Load(modsRoot, "jp-1.4.0", Path.Combine(root, "no-config"), noConfig).Count == 0 &&
                   noConfig.Has(LogLevel.Warning, "config_chapter / config_copy_display not found"),
                "memory chapters were accepted without the client config");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }

    /// <summary>仓库 Mods 里随附的 cn-exclusive.mod：发现规则、bootstrap 入口，以及 memory-chapters.json 与 data.lua 一致。</summary>
    public static Task ShippedModTest()
    {
        string modsRoot = Path.Combine(FindRepositoryRoot(), "Mods");
        var manager = new ModManager(modsRoot, "jp-1.4.0");
        manager.LoadAll();
        Assert(manager.LoadedIds.Contains("cn-exclusive.mod"), "cn-exclusive.mod was not discoverable by the JP mod loader");
        var cnManager = new ModManager(modsRoot, "cn-1.5.20");
        cnManager.LoadAll();
        Assert(!cnManager.LoadedIds.Contains("cn-exclusive.mod"), "JP-only cn-exclusive.mod was loaded for the CN client");

        string bootstrap = File.ReadAllText(Path.Combine(modsRoot, "bootstrap.lua"));
        Assert(bootstrap.Contains("\"cn-exclusive.mod/main.lua\"", StringComparison.Ordinal),
            "cn-exclusive.mod is missing from the bootstrap entry list");

        var logger = new ListLogger();
        List<MemoryChapterModDefinition> shipped = MemoryChapterModLoader.Load(modsRoot, "jp-1.4.0", ConfigDir(), logger)
            .Where(x => x.ModId == "cn-exclusive.mod")
            .ToList();
        Assert(shipped.Count == 1 && shipped[0].ChapterId == ModChapter && shipped[0].Copies.SequenceEqual(SpringCopies),
            "the shipped memory-chapters.json did not register exactly chapter 12010 with 50301-50307: " +
            string.Join(" | ", logger.Messages.Select(x => x.Message)));

        // 客户端的 data.lua 与服务端的 memory-chapters.json 出自同一次生成，两边的章节与关卡必须一致。
        string data = File.ReadAllText(Path.Combine(modsRoot, "cn-exclusive.mod", "data.lua"));
        Match block = Regex.Match(data, @"\n  memory_chapters = \{(.*?)\n  \},", RegexOptions.Singleline);
        Assert(block.Success, "data.lua has no memory_chapters block");
        List<(int ChapterId, int[] Copies)> declared = Regex.Matches(block.Groups[1].Value,
                @"chapter_id = (\d+),\s*copies = \{([^}]*)\}")
            .Select(match => (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(copy => int.Parse(copy, CultureInfo.InvariantCulture)).ToArray()))
            .ToList();
        Assert(declared.Count == shipped.Count &&
               declared.Zip(shipped).All(x => x.First.ChapterId == x.Second.ChapterId && x.First.Copies.SequenceEqual(x.Second.Copies)),
            "data.lua memory_chapters and memory-chapters.json disagree: " +
            string.Join("; ", declared.Select(x => $"{x.ChapterId}:[{string.Join(",", x.Copies)}]")));
        return Task.CompletedTask;
    }

    public static Task MergeTest()
    {
        ChapterCopyLoader.Load(ConfigDir());
        Assert(ChapterCopyLoader.GetCopyType(JpMemoryBattleCopy) == 1,
            "the JP memory battle used as the reference is not a PlotCopy");

        // 同一进程里每个服务实例都会登记一次（仓库 Mods 里有真实文件时，之前的测试也可能已登记过 12010）。
        var chapter = new MemoryChapterModDefinition(ModChapter, SpringCopies, "cn-exclusive.mod");
        ChapterCopyLoader.RegisterModChapters([chapter]);
        ChapterCopyLoader.RegisterModChapters([chapter]);
        // 已登记的章节不被替换；配置里的章节不被 Mod 改动。
        ChapterCopyLoader.RegisterModChapters([new MemoryChapterModDefinition(ModChapter, [50301], "other.mod")]);
        ChapterCopyLoader.RegisterModChapters([new MemoryChapterModDefinition(10001, [50301], "other.mod")]);

        IReadOnlyList<ChapterMemory> memories = ChapterCopyLoader.AllChapterMemories;
        Assert(memories.Count(x => x.ChapterId == ModChapter) == 1 && memories.Contains(new ChapterMemory(ModChapter, 7)),
            "registering the mod chapter repeatedly did not leave exactly one ChapterMemory(12010, 7)");
        Assert(memories.Select(x => x.ChapterId).Zip(memories.Skip(1).Select(x => x.ChapterId)).All(x => x.First < x.Second),
            "chapter memories are not unique and sorted by chapter id");
        Assert(ChapterCopyLoader.GetCopyIds(ModChapter).SequenceEqual(SpringCopies) &&
               ChapterCopyLoader.GetFirstCopyId(ModChapter) == 50301 &&
               ChapterCopyLoader.GetAllChapterIds().Contains(ModChapter),
            "the mod chapter copies were not registered (or were replaced by a later registration)");
        Assert(SpringCopies.All(copyId => ChapterCopyLoader.GetCopyType(copyId) == 1) &&
               ChapterCopyLoader.GetCopyType(BattleCopy) == ChapterCopyLoader.GetCopyType(JpMemoryBattleCopy),
            "the mod chapter copies are not classified like the JP memory battles (PlotCopy)");
        Assert(ChapterCopyLoader.GetCopyIds(10001).Contains(40001) && !ChapterCopyLoader.GetCopyIds(10001).Contains(50301) &&
               memories.Count(x => x.ChapterId == 10001) == 1 && memories.Contains(new ChapterMemory(10001, 15)),
            "a mod registration changed a chapter from the client config");

        // copy.GetCopy（PlotCopy）把 7 个节点都列为已通关；illustrate.Memory 带 (12010, 7)。
        List<(int Field, int Wire, ulong Value, byte[] Bytes)> plot = Fields(ProtocolEncoder.EncodePlotCopyInfo(int.MaxValue));
        Assert(plot.Any(x => x.Field == 3 && x.Value == 1), "plot copy info is not CopyType 1");
        Dictionary<int, List<(int Field, int Wire, ulong Value, byte[] Bytes)>> baseInfos = PlotBaseInfos(plot);
        Assert(SpringCopies.All(copyId => baseInfos.TryGetValue(copyId, out var info) &&
                                          info.Any(x => x.Field == 3 && x.Value == 7) &&
                                          info.Any(x => x.Field == 6 && x.Value == 1)),
            "copy.GetCopy does not list 50301-50307 as cleared PlotCopy records");
        Assert(DecodeMemories(PlayerDataCodec.Encode(new StoryMemoryList(memories))).Contains((ModChapter, 7)),
            "the illustrate.Memory payload does not carry chapter 12010 with index 7");
        return Task.CompletedTask;
    }

    public static async Task IntegrationTest()
    {
        string repoRoot = FindRepositoryRoot();
        string serverDll = ServerDll(repoRoot);
        Assert(File.Exists(serverDll), "server assembly is missing; build the solution first");
        // Mods 根目录是 --client-path 的父目录下的 Mods；配置用 --config-dir 指向真实客户端，客户端目录本身留空。
        string root = Path.Combine(Path.GetTempPath(), "blueoath-memory-chapter-it-" + Guid.NewGuid().ToString("N"));
        string clientPath = Path.Combine(root, "client");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(clientPath);
        WriteMod(Path.Combine(root, "Mods"), "cn-exclusive.mod", Manifest("cn-exclusive.mod"), Chapters((ModChapter, SpringCopies)));

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
        startInfo.ArgumentList.Add("--no-cheats");
        startInfo.ArgumentList.Add("--real-resource-cost=off");
        startInfo.ArgumentList.Add("--data=" + data);
        startInfo.ArgumentList.Add("--client-path=" + clientPath);
        startInfo.ArgumentList.Add("--config-dir=" + ConfigDir());
        using var process = new Process { StartInfo = startInfo };
        var stderr = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { lock (stderr) stderr.AppendLine(e.Data); };
        try
        {
            Assert(process.Start(), "memory chapter test server did not start");
            process.BeginErrorReadLine();
            string? readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            using JsonDocument ready = JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready"));
            int port = ready.RootElement.GetProperty("gameLoginPort").GetInt32();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            NetworkStream stream = client.GetStream();
            var pushes = new List<TResponse>();

            async Task<TResponse> RoundTrip(string method, byte[]? args)
            {
                byte[] request = TMessageCodec.EncodeRequest(new TRequest(method, args, 1));
                await NetSocketFrameCodec.WriteAsync(stream, request, NetSocketFrameCodec.TypeData, timeout.Token);
                while (true)
                {
                    var frame = await NetSocketFrameCodec.ReadFromServerAsync(stream, timeout.Token);
                    Assert(frame is not null, $"empty response for {method}");
                    TResponse response = TMessageCodec.DecodeResponse(frame!.Value.Payload);
                    if (response.IsResponse == 1) return response;
                    pushes.Add(response);
                }
            }

            await RoundTrip("player.Login", GameLoginCodec.Encode(new TArgLogin("memory-player", 1, "open", "hash")));
            await RoundTrip("player.GetUserList", null);
            await RoundTrip("player.CreateUser",
                new byte[] { 0x0A, 0x03, (byte)'m', (byte)'e', (byte)'m', 0x10, 0x01 });
            TResponse userLogin = await RoundTrip("user.UserLogin", new byte[] { 0x08, 0x01 });
            Assert(TMessageCodec.DecodeRetUserLogin(userLogin.Ret!) == "ok", "user login failed");
            Assert(pushes.Any(p => p.Method == "copy.GetCopy" && p.Ret is { Length: > 0 } && ListsPlotCopy(p.Ret, BattleCopy)),
                "the login copy.GetCopy (PlotCopy) does not list the mod chapter's battle node 50306");

            // user.GetUserInfo 的同步推送在应答之后，下一次请求时一并读到。
            await RoundTrip("user.GetUserInfo", null);
            byte[] startArgs = new ProtocolPackage()
                .Write(0x08, (ulong)ModChapter)   // ChapterId(1)
                .Write(0x10, (ulong)BattleCopy)   // CopyId(2)
                .Write(0x48, 3UL)                 // BattleMode(9)=Memory
                .ToArray();
            TResponse start = await RoundTrip("copy.StartBase", startArgs);
            TResponse memory = pushes.LastOrDefault(p => p.Method == "illustrate.Memory")
                ?? throw new InvalidDataException("the login sync did not push illustrate.Memory");
            Assert(memory.Ret is { Length: > 0 } && DecodeMemories(memory.Ret).Contains((ModChapter, 7)),
                "illustrate.Memory does not report chapter 12010 with all 7 nodes");
            Assert(start.Ret is { Length: > 0 } &&
                   ProtocolDecoder.DecodeVarintField(start.Ret, 7) == 1 &&
                   ProtocolDecoder.DecodeVarintField(start.Ret, 3) == BattleRid &&
                   ProtocolDecoder.DecodeVarintField(start.Ret, 6) == BattleCopy,
                "copy.StartBase for 50306 is not a PlotCopy battle on config_copy 503060");

            // FleetInfo 带最后一支敌舰队（config_fleet 100500001 is_last_fleet = 1），否则 PassBase 不结算通关。
            byte[] passArgs = new ProtocolPackage()
                .Write(0x08, (ulong)BattleCopy)                                          // BaseId(1)
                .Write(0x40, 1UL)                                                        // Grade(8)=SSS
                .Write(0x8A, new ProtocolPackage().Write(0x08, (ulong)BattleFleet).ToArray()) // FleetInfo(17){EnemyId(1)}
                .ToArray();
            pushes.Clear();
            TResponse pass = await RoundTrip("copy.PassBase", passArgs);
            Assert(pass.Err == 0 && pass.Ret is { Length: > 0 }, "copy.PassBase for 50306 failed");
            // FirstPass(10) 只在新账号第一次记下这一关的通关记录（RecordPass 追加 CopyRecord）时为 1。
            Assert(ProtocolDecoder.DecodeVarintField(pass.Ret!, 10) == 1, "copy.PassBase for 50306 did not record a first pass");
            // PassBase 的进度推送在应答之后，再发一个无副作用的请求把它读出来。
            await RoundTrip("copy.QuitBase", null);
            Assert(pushes.Any(p => p.Method == "copy.GetCopy" && p.Ret is { Length: > 0 } && ListsPlotCopy(p.Ret, BattleCopy)),
                "copy.PassBase for 50306 did not push PlotCopy (CopyType 1) data listing the node");
            lock (stderr)
                Assert(stderr.ToString().Contains("cn-exclusive.mod: loaded 1 of 1 memory chapters"),
                    "the server did not log the loaded memory chapter on stderr");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string ServerDll(string root) => Path.Combine(root, "src", "BlueOath.Server", "bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net8.0", "BlueOath.Server.dll");

    private static void WriteMod(string modsRoot, string directory, string? manifest, string? chapters)
    {
        string path = Path.Combine(modsRoot, directory);
        Directory.CreateDirectory(path);
        if (manifest is not null) File.WriteAllText(Path.Combine(path, "mod.json"), manifest);
        if (chapters is not null) File.WriteAllText(Path.Combine(path, MemoryChapterModLoader.FileName), chapters);
    }

    private static string Manifest(string id, bool enabled = true, string targets = "\"jp-1.4.0\"") =>
        $$"""{"id":"{{id}}","version":"1.0.0","entry":"main.lua","targetClients":[{{targets}}],"loadOrder":80,"enabled":{{(enabled ? "true" : "false")}}}""";

    private static string Chapters(params (int ChapterId, int[] Copies)[] chapters) =>
        JsonSerializer.Serialize(new { memoryChapters = chapters.Select(x => new { chapterId = x.ChapterId, copies = x.Copies }) });

    /// <summary>TUserCopyInfo.BaseInfo(1) 按 BaseId(1) 索引。</summary>
    private static Dictionary<int, List<(int Field, int Wire, ulong Value, byte[] Bytes)>> PlotBaseInfos(
        List<(int Field, int Wire, ulong Value, byte[] Bytes)> fields)
    {
        var result = new Dictionary<int, List<(int Field, int Wire, ulong Value, byte[] Bytes)>>();
        foreach (var field in fields.Where(x => x.Field == 1 && x.Wire == 2))
        {
            var info = Fields(field.Bytes);
            result[checked((int)info.First(x => x.Field == 1).Value)] = info;
        }
        return result;
    }

    private static bool ListsPlotCopy(byte[] userCopyInfo, int copyId)
    {
        var fields = Fields(userCopyInfo);
        return fields.Any(x => x.Field == 3 && x.Value == 1) && PlotBaseInfos(fields).ContainsKey(copyId);
    }

    /// <summary>TStoryMemoryList.MemoryList(1) → (ChapterId(1), Index(2))。</summary>
    private static List<(int ChapterId, int Index)> DecodeMemories(byte[] payload) =>
        Fields(payload).Where(x => x.Field == 1 && x.Wire == 2).Select(x =>
        {
            var memory = Fields(x.Bytes);
            return ((int)(memory.FirstOrDefault(f => f.Field == 1).Value), (int)(memory.FirstOrDefault(f => f.Field == 2).Value));
        }).ToList();

    /// <summary>收集日志，供断言跳过原因。</summary>
    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public bool Has(LogLevel level, string text) =>
            Messages.Any(x => x.Level == level && x.Message.Contains(text, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add((logLevel, formatter(state, exception)));
    }
}
