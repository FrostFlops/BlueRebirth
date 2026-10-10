using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;

namespace BlueOath.LocalServer;

/// <summary>
/// 独立应用主界面：启动/停止本地服务端、显示运行状态与实时日志、一键拉起游戏，
/// 以及「安装套件」—— 把客户端 APK + 热更资源包合成的单个 .brk 文件装到本机。
/// UI 全部用代码构建，避免额外的布局资源。
/// </summary>
[Activity(
    Label = "BlueRebirthApp",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : Activity
{
    private const int NotificationPermissionRequest = 19091;
    private const int KitPickRequest = 19092;

    private const string GamePackage = "com.zephyrus.clsy.gp";
    private const string GameActivity = "com.Babel.GD.MainActivity";

    /// <summary>用 adb 直接指定套件路径（放 App 自己的外部目录，免存储权限）。</summary>
    public const string KitPathExtra = "kitPath";

    private TextView _status = null!;
    private TextView _kitStatus = null!;
    private TextView _log = null!;
    private ScrollView _scroll = null!;
    private Button _startButton = null!;
    private Button _stopButton = null!;
    private Button _kitButton = null!;

    private bool _kitRunning;
    private int _kitLastPercent = -1;
    private KitInstallResultReceiver? _installResultReceiver;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        BuildUi();

        ServerLog.Appended += OnLogAppended;
        EmbeddedServer.Changed += OnServerChanged;
        RegisterInstallResultReceiver();

        RenderFullLog();
        RenderState();
        RefreshKitStatus();

        RequestNotificationPermissionIfNeeded();
        HandleKitPathExtra(Intent);
    }

    protected override void OnDestroy()
    {
        ServerLog.Appended -= OnLogAppended;
        EmbeddedServer.Changed -= OnServerChanged;
        UnregisterInstallResultReceiver();
        base.OnDestroy();
    }

    /// <summary>
    /// 注册安装结果接收器。用 OnCreate/OnDestroy 而不是 Start/Stop：
    /// 用户去系统确认界面时本 Activity 会 onStop，用 Stop 注销就会漏掉结果广播。
    /// </summary>
    private void RegisterInstallResultReceiver()
    {
        if (_installResultReceiver is not null)
            return;

        try
        {
            _installResultReceiver = new KitInstallResultReceiver();
            var filter = new IntentFilter(KitInstallResultReceiver.Action);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                RegisterReceiver(_installResultReceiver, filter, ReceiverFlags.NotExported);
            else
                RegisterReceiver(_installResultReceiver, filter);
        }
        catch (Exception ex)
        {
            _installResultReceiver = null;
            ServerLog.Error("注册安装结果接收器失败", ex);
        }
    }

    private void UnregisterInstallResultReceiver()
    {
        if (_installResultReceiver is null)
            return;

        try
        {
            UnregisterReceiver(_installResultReceiver);
        }
        catch (Exception)
        {
            // 忽略
        }

        _installResultReceiver = null;
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null)
            HandleKitPathExtra(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        RefreshKitStatus();
    }

    // ---------- UI ----------

    private void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(20), Dp(24), Dp(20), Dp(16));
        root.SetBackgroundColor(Color.ParseColor("#F5F6F8"));

        var title = new TextView(this)
        {
            Text = "BlueRebirthApp",
            TextSize = 21f,
        };
        title.SetTextColor(Color.ParseColor("#1A1A1A"));
        root.AddView(title, WrapWrap());

        _status = new TextView(this) { TextSize = 15f };
        _status.SetPadding(0, Dp(10), 0, 0);
        root.AddView(_status, WrapWrap());

        _kitStatus = new TextView(this) { TextSize = 12f };
        _kitStatus.SetTextColor(Color.ParseColor("#6B7280"));
        _kitStatus.SetPadding(0, Dp(4), 0, 0);
        root.AddView(_kitStatus, WrapWrap());

        var hint = new TextView(this)
        {
            Text = "用法：先在此启动服务，再打开游戏（同一台设备）。配置直接读取热更资源包（bundle/config）。",
            TextSize = 12f,
        };
        hint.SetTextColor(Color.ParseColor("#6B7280"));
        hint.SetPadding(0, Dp(6), 0, Dp(12));
        root.AddView(hint, WrapWrap());

        // 服务端按钮行
        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        root.AddView(buttons, WrapWrap());

        _startButton = new Button(this) { Text = "启动服务" };
        _startButton.Click += (_, _) => StartServerAndService();
        buttons.AddView(_startButton, WeightedButtonParams());

        _stopButton = new Button(this) { Text = "停止服务" };
        _stopButton.Click += (_, _) => StopServerAndService();
        buttons.AddView(_stopButton, WeightedButtonParams());

        var gameButton = new Button(this) { Text = "打开游戏" };
        gameButton.Click += (_, _) => LaunchGame();
        buttons.AddView(gameButton, WeightedButtonParams());

        var copyButton = new Button(this) { Text = "复制日志" };
        copyButton.Click += (_, _) => CopyLog();
        buttons.AddView(copyButton, WeightedButtonParams());

        // 安装套件按钮行
        var kitRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        kitRow.SetPadding(0, Dp(8), 0, 0);
        root.AddView(kitRow, WrapWrap());

        _kitButton = new Button(this) { Text = "安装套件…" };
        _kitButton.Click += (_, _) => PickKit();
        kitRow.AddView(_kitButton, WeightedButtonParams());

        var logLabel = new TextView(this)
        {
            Text = "日志",
            TextSize = 13f,
        };
        logLabel.SetTextColor(Color.ParseColor("#374151"));
        logLabel.SetPadding(0, Dp(14), 0, Dp(4));
        root.AddView(logLabel, WrapWrap());

        _log = new TextView(this)
        {
            TextSize = 10.5f,
        };
        _log.Typeface = Typeface.Monospace;
        _log.SetTextColor(Color.ParseColor("#111827"));
        _log.SetTextIsSelectable(true);

        _scroll = new ScrollView(this);
        _scroll.SetBackgroundColor(Color.ParseColor("#FFFFFF"));
        _scroll.AddView(_log, WrapWrap());

        var scrollParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        root.AddView(_scroll, scrollParams);

        SetContentView(root);
    }

    private static LinearLayout.LayoutParams WrapWrap() =>
        new(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);

    private LinearLayout.LayoutParams WeightedButtonParams()
    {
        var p = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        p.RightMargin = Dp(6);
        return p;
    }

    private int Dp(double dp) => (int)Math.Round(dp * (Resources?.DisplayMetrics?.Density ?? 1f));

    // ---------- 行为 ----------

    private void StartServerAndService()
    {
        EnsureForegroundService();
        EmbeddedServer.EnsureStarted(this);
    }

    private void StopServerAndService()
    {
        try
        {
            StopService(new Intent(this, typeof(ServerService)));
        }
        catch (Exception ex)
        {
            ServerLog.Error("停止前台服务失败", ex);
        }

        EmbeddedServer.Stop();
    }

    private void EnsureForegroundService()
    {
        try
        {
            var intent = new Intent(this, typeof(ServerService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                StartForegroundService(intent);
            else
                StartService(intent);
        }
        catch (Exception ex)
        {
            ServerLog.Error("启动前台服务失败（服务端仍会运行）", ex);
        }
    }

    private void LaunchGame()
    {
        try
        {
            var intent = new Intent(Intent.ActionMain);
            intent.SetClassName(GamePackage, GameActivity);
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, "未找到游戏客户端，请先安装。", ToastLength.Long)?.Show();
            ServerLog.Error("打开游戏失败（可能未安装）", ex);
        }
    }

    private void CopyLog()
    {
        try
        {
            var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
            clipboard?.PrimaryClip = ClipData.NewPlainText("BO-SRV", ServerLog.Snapshot());
            Toast.MakeText(this, "日志已复制到剪贴板", ToastLength.Short)?.Show();
        }
        catch (Exception ex)
        {
            ServerLog.Error("复制日志失败", ex);
        }
    }

    // ---------- 安装套件 ----------

    /// <summary>让用户用系统文件选择器挑出 .brk 套件（SAF，不需要存储权限）。</summary>
    private void PickKit()
    {
        if (_kitRunning)
        {
            Toast.MakeText(this, "正在安装中，请稍候…", ToastLength.Short)?.Show();
            return;
        }

        if (!EnsureInstallPermission())
            return;

        try
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            StartActivityForResult(intent, KitPickRequest);
        }
        catch (Exception ex)
        {
            ServerLog.Error("打开文件选择器失败", ex);
            Toast.MakeText(this, "无法打开文件选择器，可改用 adb 指定套件路径。",
                ToastLength.Long)?.Show();
        }
    }

    /// <summary>adb 直达：--es kitPath /sdcard/Android/data/com.blueoath.server/files/xxx.brk</summary>
    private void HandleKitPathExtra(Intent? intent)
    {
        var path = intent?.GetStringExtra(KitPathExtra);
        if (string.IsNullOrWhiteSpace(path) || _kitRunning)
            return;

        try
        {
            if (!File.Exists(path))
            {
                ServerLog.Warn("kitPath 指定的套件不存在：" + path);
                return;
            }

            if (!EnsureInstallPermission())
                return;

            ServerLog.Info("从 kitPath 安装套件：" + path);
            var local = path;
            _ = InstallKitAsync(() => Task.FromResult<Stream>(File.OpenRead(local)),
                System.IO.Path.GetFileName(local));
        }
        catch (Exception ex)
        {
            ServerLog.Error("按路径安装套件失败", ex);
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != KitPickRequest)
            return;
        if (resultCode != Result.Ok || data?.Data is null)
            return;

        var uri = data.Data;
        var resolver = ContentResolver;
        if (resolver is null)
        {
            ServerLog.Error("无法访问文件内容（ContentResolver 不可用）",
                new InvalidOperationException("ContentResolver is null"));
            return;
        }

        _ = InstallKitAsync(
            () => Task.FromResult(resolver.OpenInputStream(uri)
                                  ?? throw new InvalidOperationException("无法打开所选文件")),
            uri.LastPathSegment ?? "kit.brk");
    }

    private async Task InstallKitAsync(Func<Task<Stream>> openKit, string displayName)
    {
        _kitRunning = true;
        _kitLastPercent = -1;
        UpdateKitButtonState();
        SetKitStatus("准备安装：" + displayName);

        // 期间保持前台优先级，避免长任务被系统回收。
        EnsureForegroundService();

        try
        {
            await using var kit = await openKit();
            var summary = await KitInstaller.RunAsync(this, kit, displayName, OnKitProgress,
                CancellationToken.None);
            ServerLog.Info("套件安装：" + summary);
            SetKitStatus("✔ " + summary);

            if (EmbeddedServer.State is ServerState.Ready or ServerState.Starting)
            {
                ServerLog.Info("重启服务端以应用新的版本 / 配置…");
                EmbeddedServer.Stop();
                await Task.Delay(1500);
                EmbeddedServer.EnsureStarted(this);
            }
        }
        catch (Exception ex)
        {
            ServerLog.Error("安装套件失败", ex);
            SetKitStatus("✘ 安装失败：" + ex.Message);
            Toast.MakeText(this, "安装套件失败：" + ex.Message, ToastLength.Long)?.Show();
        }
        finally
        {
            _kitRunning = false;
            UpdateKitButtonState();
            RefreshKitStatus();
        }
    }

    private void OnKitProgress(KitInstaller.Progress progress)
    {
        string text;
        if (progress.Total > 0)
        {
            var percent = (int)(progress.Done * 100 / Math.Max(progress.Total, 1));
            if (percent == _kitLastPercent)
                return;
            _kitLastPercent = percent;
            text = $"{progress.Stage}  {percent}%  " +
                   $"({progress.Done / 1048576.0:F0}/{progress.Total / 1048576.0:F0} MB)" +
                   (string.IsNullOrEmpty(progress.Detail) ? string.Empty : "  " + progress.Detail);
        }
        else
        {
            text = progress.Stage;
        }

        SetKitStatus(text);
    }

    // ---------- 渲染 ----------

    private void OnLogAppended(string line) =>
        RunOnUiThread(() =>
        {
            _log.Append(line + "\n");
            _scroll.Post(() => _scroll.FullScroll(FocusSearchDirection.Down));
        });

    private void OnServerChanged() => RunOnUiThread(RenderState);

    private void RenderFullLog()
    {
        var text = ServerLog.Snapshot();
        _log.Text = text;
        _scroll.Post(() => _scroll.FullScroll(FocusSearchDirection.Down));
    }

    private void RenderState()
    {
        var (label, color) = EmbeddedServer.State switch
        {
            ServerState.Ready => ("● " + EmbeddedServer.Message, "#0B8457"),
            ServerState.Starting => ("● " + EmbeddedServer.Message, "#B7791F"),
            ServerState.Error => ("● " + EmbeddedServer.Message, "#C0392B"),
            _ => ("● " + EmbeddedServer.Message, "#4B5563"),
        };

        _status.Text = label;
        _status.SetTextColor(Color.ParseColor(color));

        _startButton.Enabled = EmbeddedServer.State is not ServerState.Ready
            and not ServerState.Starting;
        _stopButton.Enabled = EmbeddedServer.State is ServerState.Ready or ServerState.Starting;
    }

    private void UpdateKitButtonState()
    {
        _kitButton.Text = _kitRunning ? "安装中…" : "安装套件…";
        _kitButton.Enabled = !_kitRunning;
    }

    private void SetKitStatus(string text) => RunOnUiThread(() => _kitStatus.Text = text);

    /// <summary>把「套件版本 / 资源包 / 已装客户端版本」显示出来，便于确认安装结果。</summary>
    private void RefreshKitStatus()
    {
        if (_kitRunning)
            return;

        var client = KitInstaller.DescribeInstalledClient(this);
        var bundle = EmbeddedServer.BundleRoot is null ? "未就绪" : "已就绪";
        SetKitStatus($"套件版本 {EmbeddedServer.ClientVersion} · 资源包 {bundle} · 已装客户端 " +
                     (client ?? "未安装"));
    }

    // ---------- 权限 ----------

    private bool EnsureInstallPermission()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return true;

        var pm = PackageManager;
        if (pm is null || pm.CanRequestPackageInstalls())
            return true;

        Toast.MakeText(this, "请先允许本应用「安装未知应用」", ToastLength.Long)?.Show();
        try
        {
            var intent = new Intent(Settings.ActionManageUnknownAppSources);
            intent.SetData(Android.Net.Uri.Parse("package:" + PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
        }
        catch (Exception ex)
        {
            ServerLog.Error("打开「安装未知应用」设置失败", ex);
        }

        return false;
    }

    private void RequestNotificationPermissionIfNeeded()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
            return;

        if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications },
                NotificationPermissionRequest);
    }
}
