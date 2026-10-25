using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server.Protocols;
using BlueOath.Storage;
using static TestSupport;

/// <summary>
/// 图鉴 MarryCount（IllustrateInfo field 6）的 TCP 测试：誓约在应答前推送誓约舰的图鉴条目，MarryCount 依次为 1、2
/// （同型的两艘），重新登录的图鉴同步也带 2。客户端 IllustrateData:GetAllMarryNum 数 0 &lt; MarryCount 的条目，誓约 CG 的
/// SKIP 按钮在它大于 1 时才显示。由 Program.cs 注册；不依赖 Program.cs 的顶层辅助函数。
/// </summary>
internal static class MarryCountTests
{
    // Program.cs 的顶层函数 Assert / FindRepositoryRoot 会遮住 using static 导入，这里转发到 TestSupport。
    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private const int Ship = 40320111, Ring = 10180;

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
        int illustrateId = GameServices.ToIllustrateId(Ship);

        string data = Path.Combine(Path.GetTempPath(), "blueoath-marry-count-" + Guid.NewGuid().ToString("N"));
        const string profileId = "marry-count-player";
        PlayerAccount account = PlayerAccountFactory.CreateDefault(profileId, checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 60);
        Hero starter = account.Dock.Heroes[0];
        account = account with
        {
            // two ships of one family, so the second oath has to report MarryCount 2
            Dock = account.Dock with
            {
                Heroes = [starter, starter with { HeroId = 2, TemplateId = Ship }, starter with { HeroId = 3, TemplateId = Ship }],
            },
            Bag = new PlayerBag([new BagItem(Ring, 2)], 100),
        };
        await new SqliteGameRepository(data).SaveAccountAsync(account);
        try
        {
            await using (var server = await Server.StartAsync(serverDll, data, profileId, FindClientPath()))
            {
                List<TResponse> sync = await server.LoginAsync();
                Assert(MarryCountsOf(sync).All(count => count == 0), "an unmarried account reported a MarryCount");
                for (uint heroId = 2; heroId <= 3; heroId++)
                {
                    var pushes = new List<TResponse>();
                    TResponse marry = await server.RoundTrip("hero.Marry", [0x08, (byte)heroId, 0x10, 0x01], pushes);
                    Assert(marry.Err == 0, $"hero.Marry {heroId} failed: {marry.ErrMsg}");
                    int expected = (int)heroId - 1;
                    Assert(MarryCountsOf(pushes, illustrateId).SequenceEqual([expected]),
                        $"hero.Marry {heroId} did not push handbook {illustrateId} with MarryCount {expected} before its response " +
                        $"(got [{string.Join(",", MarryCountsOf(pushes, illustrateId))}])");
                }
            }
            await using (var server = await Server.StartAsync(serverDll, data, profileId, FindClientPath()))
            {
                List<int> counts = MarryCountsOf(await server.LoginAsync(), illustrateId);
                Assert(counts.Count > 0 && counts.All(count => count == 2),
                    $"the login handbook sync reported MarryCount [{string.Join(",", counts)}] for {illustrateId}, expected 2");
            }
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

    /// <summary>illustrate.IllustrateInfo 推送里（IllustrateList = field 1）各条目的 MarryCount（field 6），可按 IllustrateId（field 1）筛选。</summary>
    private static List<int> MarryCountsOf(IEnumerable<TResponse> pushes, int? illustrateId = null) =>
        pushes.Where(push => push.Method == "illustrate.IllustrateInfo")
            .SelectMany(push => Fields(push.Ret ?? []))
            .Where(field => field.Field == 1 && field.Wire == 2)
            .Select(field => Fields(field.Bytes))
            .Where(entry => illustrateId is null || entry.Any(x => x.Field == 1 && x.Value == (ulong)illustrateId))
            .Select(entry => (int)entry.Where(x => x.Field == 6).Select(x => x.Value).DefaultIfEmpty(0UL).First())
            .ToList();

    /// <summary>一个测试用服务端进程（作弊全关、资源免费）与它的 TCP 连接。</summary>
    private sealed class Server : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly JsonDocument _ready;
        private readonly TcpClient _client = new();
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        private readonly string _profileId;
        private NetworkStream? _stream;

        private Server(Process process, JsonDocument ready, string profileId)
        {
            _process = process;
            _ready = ready;
            _profileId = profileId;
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
                         serverDll, "--port=0", "--game-login-port=0", "--gm-port=0", "--region=jp", "--no-cheats", "--real-resource-cost=off",
                         "--data=" + data, "--profile-id=" + profileId, "--client-path=" + clientPath,
                     })
                startInfo.ArgumentList.Add(arg);
            var process = new Process { StartInfo = startInfo };
            try
            {
                Assert(process.Start(), "marry-count test server did not start");
                // stderr 不读会在缓冲区写满后卡住服务端。
                _ = process.StandardError.ReadToEndAsync();
                string? readyLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                var server = new Server(process, JsonDocument.Parse(readyLine ?? throw new InvalidDataException("server did not report ready")), profileId);
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
}
