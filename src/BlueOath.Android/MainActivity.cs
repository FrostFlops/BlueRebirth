using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;

namespace BlueOath.LocalServer;

/// <summary>
/// 独立应用主界面：启动/停止本地服务端、显示运行状态与实时日志、一键拉起游戏，
/// 「安装套件」（把客户端 APK + 热更资源包合成的单个 .brk 装到本机），以及「检查更新」
/// （直接读 GitHub Release）。
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

    /// <summary>反馈入口（腾讯频道「苍蓝誓约复原」）。</summary>
    private const string FeedbackUrl = "https://pd.qq.com/s/8gwks8zdo";

    private const string PrefsName = "blueoath";
    private const string PrefsNoticeShown = "resale_notice_shown";

    /// <summary>用 adb 直接指定套件路径（放 App 自己的外部目录，免存储权限）。</summary>
    public const string KitPathExtra = "kitPath";

    /// <summary>测试用：覆盖更新检查的 Release API 地址。</summary>
    public const string UpdateApiExtra = "updateApi";

    private TextView _status = null!;
    private TextView _kitStatus = null!;
    private TextView _updateStatus = null!;
    private TextView _log = null!;
    private ScrollView _scroll = null!;
    private Button _startButton = null!;
    private Button _stopButton = null!;
    private Button _kitButton = null!;
    private Button _updateButton = null!;

    private bool _kitRunning;
    private bool _updateRunning;
    private int _kitLastPercent = -1;
    private KitInstallResultReceiver? _installResultReceiver;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        EmbeddedServer.InitAppVersion(this);
        BuildUi();

        ServerLog.Appended += OnLogAppended;
        EmbeddedServer.Changed += OnServerChanged;
        RegisterInstallResultReceiver();

        RenderFullLog();
        RenderState();
        RefreshKitStatus();

        RequestNotificationPermissionIfNeeded();
        ShowResaleNoticeIfFirstRun();
        HandleKitPathExtra(Intent);
        AutoCheckUpdate();
    }

    protected override void OnDestroy()
    {
        ServerLog.Appended -= OnLogAppended;
        EmbeddedServer.Changed -= OnServerChanged;
        UnregisterInstallResultReceiver();
        base.OnDestroy();
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
        root.SetPadding(Dp(18), Dp(20), Dp(18), Dp(14));
        root.SetBackgroundColor(Color.ParseColor("#F5F6F8"));

        var title = new TextView(this)
        {
            Text = $"BlueRebirthApp  v{EmbeddedServer.DisplayVersion}",
            TextSize = 20f,
        };
        title.SetTextColor(Color.ParseColor("#1A1A1A"));
        root.AddView(title, WrapWrap());

        // ★ 防倒卖提示：常驻横幅，必须醒目。
        root.AddView(BuildResaleBanner(), BannerParams());

        _status = new TextView(this) { TextSize = 15f };
        _status.SetPadding(0, Dp(8), 0, 0);
        root.AddView(_status, WrapWrap());

        _kitStatus = new TextView(this) { TextSize = 12f };
        _kitStatus.SetTextColor(Color.ParseColor("#6B7280"));
        _kitStatus.SetPadding(0, Dp(4), 0, 0);
        root.AddView(_kitStatus, WrapWrap());

        _updateStatus = new TextView(this) { TextSize = 12f };
        _updateStatus.SetTextColor(Color.ParseColor("#6B7280"));
        _updateStatus.SetPadding(0, Dp(2), 0, 0);
        root.AddView(_updateStatus, WrapWrap());

        var hint = new TextView(this)
        {
            Text = "用法：先在此启动服务，再打开游戏（同一台设备）。配置直接读取热更资源包（bundle/config）。",
            TextSize = 12f,
        };
        hint.SetTextColor(Color.ParseColor("#6B7280"));
        hint.SetPadding(0, Dp(6), 0, Dp(10));
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

        // 安装套件 / 检查更新
        var kitRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        kitRow.SetPadding(0, Dp(8), 0, 0);
        root.AddView(kitRow, WrapWrap());

        _kitButton = new Button(this) { Text = "安装套件…" };
        _kitButton.Click += (_, _) => PickKit();
        kitRow.AddView(_kitButton, WeightedButtonParams());

        _updateButton = new Button(this) { Text = "检查更新" };
        _updateButton.Click += (_, _) => _ = CheckUpdateAsync(auto: false);
        kitRow.AddView(_updateButton, WeightedButtonParams());

        var feedbackButton = new Button(this) { Text = "反馈" };
        feedbackButton.Click += (_, _) => OpenFeedback();
        kitRow.AddView(feedbackButton, WeightedButtonParams());

        var logLabel = new TextView(this)
        {
            Text = "日志",
            TextSize = 13f,
        };
        logLabel.SetTextColor(Color.ParseColor("#374151"));
        logLabel.SetPadding(0, Dp(12), 0, Dp(4));
        root.AddView(logLabel, WrapWrap());

        _log = new TextView(this) { TextSize = 10.5f };
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

    private TextView BuildResaleBanner()
    {
        var banner = new TextView(this)
        {
            Text = "本软件永久免费，如果您付费获得了本软件，那么您已经被骗了。",
            TextSize = 14.5f,
        };
        banner.SetTextColor(Color.ParseColor("#8B1A1A"));
        banner.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

        var background = new GradientDrawable();
        background.SetColor(Color.ParseColor("#FDECEC"));
        background.SetStroke(Math.Max(2, Dp(1)), Color.ParseColor("#C0392B"));
        background.SetCornerRadius(Dp(8));
        banner.Background = background;
        return banner;
    }

    private LinearLayout.LayoutParams BannerParams()
    {
        var p = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        p.TopMargin = Dp(10);
        return p;
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

    // ---------- 防倒卖提示 ----------

    private void ShowResaleNoticeIfFirstRun()
    {
        try
        {
            var prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs?.GetBoolean(PrefsNoticeShown, false) == true)
                return;
            prefs?.Edit()?.PutBoolean(PrefsNoticeShown, true)?.Apply();

            RunOnUiThread(() =>
            {
                new AlertDialog.Builder(this)
                    .SetTitle("请注意")
                    .SetMessage("本软件永久免费，如果您付费获得了本软件，那么您已经被骗了。\n\n" +
                                "本项目是免费的开源复原项目，任何形式的收费售卖都与作者无关。")
                    .SetPositiveButton("我明白了", (_, _) => { })
                    .SetCancelable(false)
                    .Show();
            });
        }
        catch (Exception ex)
        {
            ServerLog.Error("展示防倒卖提示失败", ex);
        }
    }

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

    /// <summary>打开反馈入口（系统浏览器 / 对应 App 处理 pd.qq.com 链接）。</summary>
    private void OpenFeedback()
    {
        try
        {
            var intent = new Intent(Intent.ActionView);
            intent.SetData(Android.Net.Uri.Parse(FeedbackUrl));
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
        }
        catch (Exception ex)
        {
            // 没有可用浏览器时兜底：把链接复制到剪贴板，至少让用户能手动打开。
            try
            {
                var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
                clipboard?.PrimaryClip = ClipData.NewPlainText("反馈链接", FeedbackUrl);
                Toast.MakeText(this, "未能打开浏览器，反馈链接已复制到剪贴板", ToastLength.Long)?.Show();
            }
            catch (Exception)
            {
                Toast.MakeText(this, "未能打开反馈链接：" + FeedbackUrl, ToastLength.Long)?.Show();
            }

            ServerLog.Error("打开反馈链接失败", ex);
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
        UpdateButtonStates();
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
            UpdateButtonStates();
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

    // ---------- 启动器自更新（GitHub Release） ----------

    private void AutoCheckUpdate()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000);
                await CheckUpdateAsync(auto: true);
            }
            catch (Exception ex)
            {
                // fire-and-forget 的异常默认会被吞掉，这里兜底打日志。
                ServerLog.Error("自动检查更新异常", ex);
            }
        });
    }

    private async Task CheckUpdateAsync(bool auto)
    {
        if (_updateRunning || _kitRunning)
            return;

        _updateRunning = true;

        try
        {
            UpdateButtonStates();
            SetUpdateStatus("正在检查更新…");
            var api = Intent?.GetStringExtra(UpdateApiExtra);
            var info = await UpdateChecker.FetchLatestAsync(api, CancellationToken.None);
            ServerLog.Info($"最新 Release：{info.Tag}（本机 v{EmbeddedServer.DisplayVersion}）" +
                           (info.HasApk ? $" · 安卓包 {info.ApkName}" : " · 该 Release 未附带安卓包"));

            if (!UpdateChecker.IsNewer(info.Tag, EmbeddedServer.DisplayVersion))
            {
                SetUpdateStatus($"已是最新版本 v{EmbeddedServer.DisplayVersion}");
                return;
            }

            if (!info.HasApk)
            {
                SetUpdateStatus($"发现新版本 {info.Tag}，但该 Release 未附带安卓包");
                ServerLog.Warn($"Release {info.Tag} 里没有 BlueRebirthApp-*.apk，无法自动更新");
                return;
            }

            var message = $"发现新版本 {info.Tag}（当前 v{EmbeddedServer.DisplayVersion}）\n\n" +
                          $"{info.ApkName}\n\n是否立即下载并安装？";
            if (!await ConfirmAsync("启动器更新", message))
            {
                SetUpdateStatus($"发现新版本 {info.Tag}（已跳过）");
                return;
            }

            if (!EnsureInstallPermission())
                return;

            var dir = System.IO.Path.Combine(CacheDir?.AbsolutePath ?? FilesDir!.AbsolutePath, "update");
            Directory.CreateDirectory(dir);
            var target = System.IO.Path.Combine(dir, info.ApkName!);

            SetUpdateStatus($"正在下载 {info.ApkName} …");
            var size = await UpdateChecker.DownloadAsync(info.ApkUrl!, target, (done, total) =>
            {
                var text = total > 0
                    ? $"正在下载更新  {done * 100 / total}%  ({done / 1048576.0:F0}/{total / 1048576.0:F0} MB)"
                    : $"正在下载更新  {done / 1048576.0:F0} MB";
                SetUpdateStatus(text);
            }, CancellationToken.None);

            ServerLog.Info($"更新包已下载：{target}（{size / 1048576.0:F0} MB）");
            SetUpdateStatus("已下载完成，正在提交安装…");
            await KitInstaller.InstallApkFileAsync(this, target, info.ApkName!, OnKitProgress,
                CancellationToken.None);
            SetUpdateStatus("已提交更新安装，请在系统弹窗点「安装」");
        }
        catch (Exception ex)
        {
            ServerLog.Error("检查更新失败", ex);
            SetUpdateStatus((auto ? "自动检查更新失败：" : "检查更新失败：") + ex.Message);
        }
        finally
        {
            _updateRunning = false;
            UpdateButtonStates();
        }
    }

    private Task<bool> ConfirmAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        RunOnUiThread(() =>
        {
            try
            {
                new AlertDialog.Builder(this)
                    .SetTitle(title)
                    .SetMessage(message)
                    .SetPositiveButton("下载并安装", (_, _) => tcs.TrySetResult(true))
                    .SetNegativeButton("以后再说", (_, _) => tcs.TrySetResult(false))
                    .SetCancelable(false)
                    .Show();
            }
            catch (Exception ex)
            {
                ServerLog.Error("弹出确认框失败", ex);
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
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

    /// <summary>
    /// 刷新按钮状态。★ 必须切回 UI 线程：自动检查更新是从后台线程发起的，
    /// 直接改控件会抛 CalledFromWrongThread，而被 fire-and-forget 的 Task 吞掉
    /// （表现为「自动检查更新毫无动静、连日志都没有」）。
    /// </summary>
    private void UpdateButtonStates() => RunOnUiThread(() =>
    {
        _kitButton.Text = _kitRunning ? "安装中…" : "安装套件…";
        _kitButton.Enabled = !_kitRunning && !_updateRunning;
        _updateButton.Text = _updateRunning ? "检查中…" : "检查更新";
        _updateButton.Enabled = !_kitRunning && !_updateRunning;
    });

    private void SetKitStatus(string text) => RunOnUiThread(() => _kitStatus.Text = text);

    private void SetUpdateStatus(string text) => RunOnUiThread(() => _updateStatus.Text = text);

    /// <summary>把「配置/资源包/已装客户端」显示出来，便于确认安装结果。</summary>
    private void RefreshKitStatus()
    {
        if (_kitRunning)
            return;

        var client = KitInstaller.DescribeInstalledClient(this);
        var bundle = EmbeddedServer.BundleRoot is null ? "未就绪" : "已就绪";

        var config = "未就绪";
        if (EmbeddedServer.ConfigDir is not null)
        {
            var installedDir = EmbeddedServer.ConfigInstallDir(this);
            config = SamePath(EmbeddedServer.ConfigDir, installedDir) ? "已导入" : "来自资源包";
        }

        SetKitStatus($"套件版本 {EmbeddedServer.ClientVersion} · 配置表 {config} · 资源包 {bundle} · " +
                     $"已装客户端 " + (client ?? "未安装"));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a),
            System.IO.Path.TrimEndingDirectorySeparator(b), StringComparison.Ordinal);

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

    private void RequestNotificationPermissionIfNeeded()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
            return;

        if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications },
                NotificationPermissionRequest);
    }
}
