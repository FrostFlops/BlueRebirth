using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BlueOath.LocalServer;

/// <summary>服务端生命周期状态，供 UI 着色与文案使用。</summary>
public enum ServerState
{
    Idle,
    Starting,
    Ready,
    Error,
}

/// <summary>
/// 本地服务端的进程内宿主（独立应用形态）。
///
/// 参数与 <c>run_server.sh</c> 保持一致（JP 服 / client-version 1.4.140 / 固定端口），
/// 只是把路径换成 App 私有目录，并在进程内直接做 TLS（客户端走 https 127.0.0.1:19090）。
/// 游戏客户端通过 loopback 连接这些端口，因此不需要 adb reverse，也不需要 PC 侧代理。
/// </summary>
public static class EmbeddedServer
{
    /// <summary>主端口。客户端 game_config.json 写死 https://127.0.0.1:19090。</summary>
    public const int MainPort = 19090;

    /// <summary>游戏登录 TCP 端口（引导响应会把它下发给客户端）。</summary>
    public const int GameLoginPort = 19191;

    /// <summary>通告给客户端的 KCP 端口（服务端并未真正实现 UDP 端）。</summary>
    public const int KcpGameLoginPort = 19192;

    /// <summary>
    /// 明文 HTTP 端口，专门给客户端的热更下载用（原生 libcurl+OpenSSL 不接受自签 TLS）。
    /// 与 19090 的 TLS 引导端口并存。
    /// </summary>
    public const int BundlePort = 19193;

    /// <summary>
    /// 对客户端宣告的版本号（写进 getversion 的 src_version / tar_version）。
    ///
    /// ★★ 必须等于**客户端当前 sdcard 上 assetmap 的版本**。
    ///   客户端同时读「APK 内置 assetmap」与「sdcard 资源 assetmap」，以 sdcard 的为准：
    ///   · sdcard 放了 1.4.140 资源（游戏数据目录 files/bundles/assetmap = 668147B/1.4.140）
    ///     → 这里必须 1.4.140（与历史验证通过的 PC 方案一致）；
    ///   · 若把 sdcard 资源清掉（如 pm clear），客户端回落 APK 内置 assetmap = 1.4.90
    ///     → 这里必须 1.4.90，否则版本不符 → 走 PATCH_TO_LATEST 去热更 → 永远停在热更页。
    ///   （历史记录：此前正是「客户端 sdcard assetmap=1.4.140 vs 服务端 1.4.90」导致弹更新提示，
    ///     改成 1.4.140 后提示消失并顺利进到登录阶段。）
    /// </summary>
    public const string ClientVersion = "1.4.140";

    /// <summary>APK 中存放配置库的 asset 子路径。</summary>
    private const string ConfigAssetPath = "bosrv/config";

    /// <summary>是否为客户端提供 TLS。客户端走 https，因此默认开启。</summary>
    private const bool EnableTls = true;

    private static readonly object Gate = new();
    private static IHost? _host;
    private static BundleHttpServer? _bundleServer;

    /// <summary>当前状态。</summary>
    public static ServerState State { get; private set; } = ServerState.Idle;

    /// <summary>状态附带的人类可读文案。</summary>
    public static string Message { get; private set; } = "未启动";

    /// <summary>状态变化时触发；处理方需自行切回 UI 线程。</summary>
    public static event Action? Changed;

    /// <summary>服务端落地目录（files/blueoath/blueoath），便于 UI 展示。</summary>
    public static string? ClientPath { get; private set; }

    /// <summary>热更 bundle 实际使用的根目录；为 null 表示未放置（客户端热更检查会失败）。</summary>
    public static string? BundleRoot { get; private set; }

    /// <summary>建议放置热更 bundle 的目录（App 专属外部目录，免存储权限，可用 adb push / 文件管理器拷入）。</summary>
    public static string BundleDropDir(Android.Content.Context context)
    {
        var external = context.GetExternalFilesDir(null)?.AbsolutePath;
        if (!string.IsNullOrEmpty(external))
            return Path.Combine(external, "bundle");
        return Path.Combine(GetFilesRoot(context), "bundle");
    }

    /// <summary>幂等启动。可从 Application.OnCreate / Service / Activity 任意位置调用。</summary>
    public static void EnsureStarted(Android.Content.Context context)
    {
        lock (Gate)
        {
            if (_host is not null || State is ServerState.Starting or ServerState.Ready)
                return;
            State = ServerState.Starting;
            Message = "正在启动…";
        }

        Set(ServerState.Starting, "正在启动…");
        var appContext = context.ApplicationContext ?? context;
        _ = Task.Run(() => StartAsync(appContext));
    }

    private static async Task StartAsync(Android.Content.Context context)
    {
        try
        {
            var filesRoot = GetFilesRoot(context);
            Directory.CreateDirectory(filesRoot);

            var dataRoot = Path.Combine(filesRoot, "bo-data");
            Directory.CreateDirectory(dataRoot);

            // ConfigDbLoader: <clientPath>/blueoath_Data/StreamingAssets/config
            var clientPath = Path.Combine(filesRoot, "blueoath", "blueoath");
            ClientPath = clientPath;
            var configTarget = Path.Combine(clientPath, "blueoath_Data", "StreamingAssets", "config");

            Set(ServerState.Starting, "正在解压配置库（首次约 105MB）…");
            await AssetDeployer.DeployAsync(context, ConfigAssetPath, configTarget,
                (done, total) => Set(ServerState.Starting, $"正在解压配置库 {done}/{total} …"));

            Set(ServerState.Starting, "正在初始化服务端…");

            // 客户端热更会向 CDN 基址请求 /windows_android/<rel>，映射到 BundleRoot 下的真实文件。
            // 未放置时客户端会在 assetmap 下载处失败（NetworkFailTimes 递增 → 提示读取更新列表失败）。
            var bundleRoot = ResolveBundleRoot(context);
            BundleRoot = bundleRoot;
            if (bundleRoot is null)
            {
                ServerLog.Warn("未找到热更 bundle（把合并后的 bundle 目录拷到 " +
                               BundleDropDir(context) + "）；客户端热更检查会失败");
            }
            else
            {
                ServerLog.Info("bundle root: " + bundleRoot);
                // 另开明文端口供原生下载器用（自签 TLS 会被 libcurl 拒绝）。
                try
                {
                    _bundleServer?.Stop();
                    _bundleServer = new BundleHttpServer(BundlePort, bundleRoot);
                    _bundleServer.Start();
                }
                catch (Exception ex)
                {
                    _bundleServer = null;
                    ServerLog.Error("热更明文下载服务启动失败", ex);
                }
            }

            var bundleStaticUrl = bundleRoot is not null && _bundleServer is not null
                ? $"http://127.0.0.1:{BundlePort}/"
                : null;

            var options = new BlueOath.Server.ServerOptions(
                MainPort,
                BlueOath.Protocol.ProtocolProfile.Japan with { ClientVersion = ClientVersion },
                dataRoot,
                clientPath,
                EnableTls,
                Path.Combine(dataRoot, "_tls"),
                null,
                false,
                GameLoginPort,
                KcpGameLoginPort,
                null,
                BlueOath.Core.PlayerAccountFactory.DefaultProfileId,
                "android-local",
                bundleRoot,
                bundleStaticUrl);

            var host = BlueOath.Server.Hosting.ServerHostBuilder.Build(options);
            await host.StartAsync();

            var endpoints = host.Services.GetRequiredService<BlueOath.Server.Hosting.ServerEndpoints>();

            lock (Gate)
            {
                _host = host;
            }

            Set(ServerState.Ready, string.Format(
                "运行中  ·  https://127.0.0.1:{0}  ·  登录 {1}/{2}",
                endpoints.Port, endpoints.GameLoginPort, endpoints.KcpGameLoginPort));
        }
        catch (Exception ex)
        {
            lock (Gate)
            {
                _host = null;
            }

            ServerLog.Error("服务端启动失败", ex);
            Set(ServerState.Error, "启动失败：" + ex.Message);
        }
    }

    /// <summary>停止服务端并释放监听端口。</summary>
    public static async Task StopAsync()
    {
        IHost? host;
        lock (Gate)
        {
            host = _host;
            _host = null;
        }

        if (host is null)
        {
            Set(ServerState.Idle, "未启动");
            return;
        }

        Set(ServerState.Starting, "正在停止…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.StopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            ServerLog.Error("停止服务端时出错", ex);
        }
        finally
        {
            try
            {
                host.Dispose();
            }
            catch (Exception)
            {
                // ignore
            }

            try
            {
                _bundleServer?.Stop();
                _bundleServer = null;
            }
            catch (Exception)
            {
                // ignore
            }
        }

        Set(ServerState.Idle, "未启动");
    }

    /// <summary>同步停止（供 UI 按钮 / 退出回调使用）。</summary>
    public static void Stop() => _ = Task.Run(StopAsync);

    private static void Set(ServerState state, string message)
    {
        lock (Gate)
        {
            State = state;
            Message = message;
        }

        ServerLog.Info("state=" + state + " :: " + message);

        // 在锁外触发，避免订阅方回调造成死锁。
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(ServerLog.Tag, "状态回调异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 解析热更 bundle 根目录：优先 App 专属外部目录（便于拷入），其次内部目录。
    /// 只有存在 assetmap 才认为有效，避免把空目录当根导致 404。
    /// </summary>
    private static string? ResolveBundleRoot(Android.Content.Context context)
    {
        var candidates = new List<string> { BundleDropDir(context) };
        var internalDir = Path.Combine(GetFilesRoot(context), "bundle");
        if (!candidates.Contains(internalDir))
            candidates.Add(internalDir);

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "assetmap")))
                return dir;
        }

        return null;
    }

    private static string GetFilesRoot(Android.Content.Context context)
    {
        var files = context.FilesDir;
        if (files is not null)
            return files.AbsolutePath;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "blueoath");
    }
}
