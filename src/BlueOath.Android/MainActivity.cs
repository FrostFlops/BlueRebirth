using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace BlueOath.LocalServer;

/// <summary>
/// 独立应用主界面：启动/停止本地服务端、显示运行状态与实时日志，并可一键拉起游戏客户端。
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
    private const string GamePackage = "com.zephyrus.clsy.gp";
    private const string GameActivity = "com.Babel.GD.MainActivity";

    private TextView _status = null!;
    private TextView _log = null!;
    private ScrollView _scroll = null!;
    private Button _startButton = null!;
    private Button _stopButton = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        BuildUi();

        ServerLog.Appended += OnLogAppended;
        EmbeddedServer.Changed += OnServerChanged;

        RenderFullLog();
        RenderState();

        RequestNotificationPermissionIfNeeded();
    }

    protected override void OnDestroy()
    {
        ServerLog.Appended -= OnLogAppended;
        EmbeddedServer.Changed -= OnServerChanged;
        base.OnDestroy();
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

        var hint = new TextView(this)
        {
            Text = "用法：先在此启动服务，再打开游戏（同一台设备）。配置直接读取热更资源包（bundle/config）。",
            TextSize = 12f,
        };
        hint.SetTextColor(Color.ParseColor("#6B7280"));
        hint.SetPadding(0, Dp(6), 0, Dp(12));
        root.AddView(hint, WrapWrap());

        // 按钮行
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
            ServerLog.Error("启动前台服务失败", ex);
        }

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

    private void RequestNotificationPermissionIfNeeded()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
            return;

        if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications },
                NotificationPermissionRequest);
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
}
