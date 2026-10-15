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
/// 独立应用主界面。
///
/// ★ 界面设计原则（面向没有编程经验的使用者）：
///   · **文字最少**：主界面只有「一个状态卡 + 两个大按钮 + 一行小按钮」，
///     不出现端口号、路径、版本号、配置表数量这类技术信息；
///   · **错误/阻塞重点标注**：资源不齐、启动失败等用整块红卡 + 大字说明，
///     旁边直接给「下一步该点哪里」；
///   · **日志独立成页**：主界面不显示日志，点「日志」按钮跳到 <see cref="LogActivity"/>，
///     技术细节（版本、路径、计数）也只在那一页出现。
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
    private const int StoragePermissionRequest = 19093;

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

    // 状态卡
    private LinearLayout _statusCard = null!;
    private TextView _statusTitle = null!;
    private TextView _statusDetail = null!;

    // 新版提示条（只在真有新版本时出现）
    private LinearLayout _updateCard = null!;
    private TextView _updateText = null!;
    private Button _updateAction = null!;

    // 主按钮
    private Button _playButton = null!;
    private Button _kitButton = null!;

    // 小按钮行
    private Button _stopButton = null!;
    private Button _logButton = null!;
    private Button _checkButton = null!;
    private Button _feedbackButton = null!;

    /// <summary>最近一次资源体检结果（门控按钮用）。</summary>
    private EmbeddedServer.ResourceInfo? _resources;

    private bool _kitRunning;
    private bool _updateRunning;
    private bool _playBusy;
    private int _kitLastPercent = -1;
    private UpdateChecker.ReleaseInfo? _pendingUpdate;
    private KitInstallResultReceiver? _installResultReceiver;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        EmbeddedServer.InitAppVersion(this);
        BuildUi();

        EmbeddedServer.Changed += OnServerChanged;
        RegisterInstallResultReceiver();

        RefreshStatus();

        RequestNotificationPermissionIfNeeded();
        RequestStoragePermissionIfNeeded(userInitiated: false);
        ShowResaleNoticeIfFirstRun();
        HandleKitPathExtra(Intent);
        AutoCheckUpdate();
    }

    protected override void OnDestroy()
    {
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
        RefreshStatus();
    }

    // ---------- UI ----------

    private void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(20), Dp(18), Dp(20), Dp(16));
        root.SetBackgroundColor(Color.ParseColor("#F5F6F8"));

        // ★ 防倒卖提示：常驻横幅（紧凑版，仍必须醒目）。
        root.AddView(BuildResaleBanner());

        // 状态卡：整个界面唯一的大字信息区
        _statusCard = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _statusCard.SetPadding(Dp(18), Dp(16), Dp(18), Dp(16));

        _statusTitle = new TextView(this) { TextSize = 22f };
        _statusTitle.SetTextColor(Color.ParseColor("#1A1A1A"));
        _statusCard.AddView(_statusTitle, WrapWrap());

        _statusDetail = new TextView(this) { TextSize = 13.5f };
        _statusDetail.SetPadding(0, Dp(6), 0, 0);
        _statusCard.AddView(_statusDetail, WrapWrap());

        var cardParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        { TopMargin = Dp(14) };
        root.AddView(_statusCard, cardParams);

        // 新版提示条（默认隐藏）
        _updateCard = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _updateCard.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
        _updateCard.Visibility = ViewStates.Gone;

        _updateText = new TextView(this) { TextSize = 14f };
        _updateText.SetTextColor(Color.ParseColor("#8A5A00"));
        _updateCard.AddView(_updateText, WrapWrap());

        _updateAction = new Button(this) { Text = "立即倒卖" };
        _updateAction.Click += (_, _) => _ = DoUpdateAsync();
        _updateCard.AddView(_updateAction, ButtonParams(Dp(8)));

        var updateParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        { TopMargin = Dp(12) };
        root.AddView(_updateCard, updateParams);

        // 主按钮 1：开始游戏
        _playButton = new Button(this) { Text = "开始屠杀倒卖狗的家人", TextSize = 20f };
        _playButton.Click += (_, _) => _ = StartGameAsync();
        var playParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(64)) { TopMargin = Dp(18) };
        root.AddView(_playButton, playParams);

        // 主按钮 2：安装资源包
        _kitButton = new Button(this) { Text = "安装倒卖狗死妈包", TextSize = 16f };
        _kitButton.SetTextColor(Color.ParseColor("#1F2937"));
        _kitButton.Background = Rounded("#FFFFFF", "#94A3B8", 12);
        _kitButton.Click += (_, _) => PickKit();
        var kitParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(50)) { TopMargin = Dp(10) };
        root.AddView(_kitButton, kitParams);

        // 次要动作：小按钮一行，弱化显示
        var minorRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var minorParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        { TopMargin = Dp(16) };
        root.AddView(minorRow, minorParams);

        _stopButton = AddMinorButton(minorRow, "停止操倒卖狗的妈", (_, _) => StopServerAndService());
        _logButton = AddMinorButton(minorRow, "看看倒卖狗的妈是怎么死的", (_, _) => OpenLog());
        _checkButton = AddMinorButton(minorRow, "检查新的倒卖狗死妈小技巧", (_, _) => _ = CheckUpdateAsync(auto: false));
        _feedbackButton = AddMinorButton(minorRow, "反馈", (_, _) => OpenFeedback());

        SetContentView(root);
    }

    private TextView BuildResaleBanner()
    {
        var banner = new TextView(this)
        {
            Text = "本软件永久免费，如果你付费购得本软件，请你退款",
            TextSize = 12f,
            Gravity = GravityFlags.Center,
        };
        banner.SetTextColor(Color.ParseColor("#8B1A1A"));
        banner.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));
        banner.Background = Rounded("#FDECEC", "#C0392B", 8);
        return banner;
    }

    private Button AddMinorButton(LinearLayout row, string text, EventHandler onClick)
    {
        var button = new Button(this) { Text = text, TextSize = 13.5f };
        button.SetTextColor(Color.ParseColor("#374151"));
        button.Background = Rounded("#FFFFFF", "#D1D5DB", 8);
        button.Click += onClick;

        var p = new LinearLayout.LayoutParams(0, Dp(40), 1f) { RightMargin = Dp(6) };
        row.AddView(button, p);
        return button;
    }

    private LinearLayout.LayoutParams ButtonParams(int topMargin) =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        { TopMargin = topMargin };

    private static LinearLayout.LayoutParams WrapWrap() =>
        new(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);

    private GradientDrawable Rounded(string fill, string? stroke, double radius)
    {
        var background = new GradientDrawable();
        background.SetColor(Color.ParseColor(fill));
        if (stroke is not null)
            background.SetStroke(Math.Max(2, Dp(1)), Color.ParseColor(stroke));
        background.SetCornerRadius(Dp(radius));
        return background;
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
                                "本项目是免费的开源复原项目，任何形式的收费售卖都与作者无关。\n\n" +
                                "倒卖狗你妈死了")
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

    /// <summary>
    /// 主按钮：一句话把该做的事做完 —— 服务没起就先起，起来后打开游戏。
    /// 使用者不需要理解「服务端 / 端口 / 资源包」这些概念。
    /// </summary>
    private async Task StartGameAsync()
    {
        if (_playBusy)
            return;

        var resources = EmbeddedServer.InspectResources(this);
        _resources = resources;
        if (!resources.Ready)
        {
            RenderState();
            Toast.MakeText(this, "请先点「安装资源包」", ToastLength.Long)?.Show();
            return;
        }

        _playBusy = true;
        RenderState();
        try
        {
            if (EmbeddedServer.State is not ServerState.Ready)
            {
                EnsureForegroundService();
                EmbeddedServer.EnsureStarted(this);

                if (!await WaitUntilReadyAsync(TimeSpan.FromSeconds(25)))
                {
                    Toast.MakeText(this, "启动失败，请点「日志」查看原因", ToastLength.Long)?.Show();
                    return;
                }
            }

            LaunchGame();
        }
        catch (Exception ex)
        {
            ServerLog.Error("开始游戏失败", ex);
            Toast.MakeText(this, "启动失败，请点「日志」查看原因", ToastLength.Long)?.Show();
        }
        finally
        {
            _playBusy = false;
            RenderState();
        }
    }

    private static async Task<bool> WaitUntilReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (EmbeddedServer.State == ServerState.Ready)
                return true;
            if (EmbeddedServer.State == ServerState.Error)
                return false;
            await Task.Delay(400);
        }

        return EmbeddedServer.State == ServerState.Ready;
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
        // 资源不齐时不要拉起游戏：客户端自己那份 assetmap 还是 1.4.90，会卡在热更/下载界面。
        var resources = _resources ??= EmbeddedServer.InspectResources(this);
        if (!resources.Ready)
        {
            RenderState();
            Toast.MakeText(this, "请先点「安装资源包」", ToastLength.Long)?.Show();
            return;
        }

        try
        {
            var intent = new Intent(Intent.ActionMain);
            intent.SetClassName(GamePackage, GameActivity);
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, "还没装游戏本体，请先安装资源包", ToastLength.Long)?.Show();
            ServerLog.Error("打开游戏失败（可能未安装）", ex);
        }
    }

    private void OpenLog() => StartActivity(new Intent(this, typeof(LogActivity)));

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

    /// <summary>让用户用系统文件选择器挑出 .brk 资源包（SAF，不需要存储权限）。</summary>
    private void PickKit()
    {
        if (_kitRunning)
        {
            Toast.MakeText(this, "正在安装中，请稍候…", ToastLength.Short)?.Show();
            return;
        }

        if (!EnsureInstallPermission())
            return;

        // 铺设「客户端资源」要写游戏自己的外部目录，需要存储权限。
        RequestStoragePermissionIfNeeded(userInitiated: true);

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
        UpdateKitButton();
        RenderState();

        // 期间保持前台优先级，避免长任务被系统回收。
        EnsureForegroundService();

        try
        {
            await using var kit = await openKit();
            var summary = await KitInstaller.RunAsync(this, kit, displayName, OnKitProgress,
                CancellationToken.None);
            ServerLog.Info("安装完成：" + summary);

            // 装完立刻重算资源状态；齐了就顺手把服务拉起来。
            var resources = EmbeddedServer.InspectResources(this);
            RefreshStatus();
            if (resources.Ready)
            {
                ServerLog.Info("资源已就绪，重启服务端以应用新的版本 / 配置…");
                EmbeddedServer.Stop();
                await Task.Delay(1500);
                EnsureForegroundService();
                EmbeddedServer.EnsureStarted(this);
            }
            else
            {
                ServerLog.Warn("安装已结束，但资源仍不齐：" + (resources.Blocker ?? ""));
            }

            Toast.MakeText(this, "安装完成", ToastLength.Long)?.Show();
        }
        catch (Exception ex)
        {
            ServerLog.Error("安装失败", ex);
            Toast.MakeText(this, "安装失败：" + ex.Message + "\n可点「日志」查看详情", ToastLength.Long)?.Show();
        }
        finally
        {
            _kitRunning = false;
            UpdateKitButton();
            RefreshStatus();
        }
    }

    private void OnKitProgress(KitInstaller.Progress progress) => RunOnUiThread(() =>
    {
        if (progress.Total > 0)
        {
            var percent = (int)(progress.Done * 100 / Math.Max(progress.Total, 1));
            if (percent == _kitLastPercent)
                return;
            _kitLastPercent = percent;
            _kitButton.Text = Math.Clamp(percent, 0, 100) + "%";
        }
        else
        {
            _kitButton.Text = progress.Stage;
        }
    });

    private void UpdateKitButton()
    {
        _kitButton.Text = _kitRunning ? "安装中…" : "安装资源包";
        _kitButton.Enabled = !_kitRunning;
        _kitButton.Alpha = _kitRunning ? 0.5f : 1f;
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
        // ★ 自动检查是从后台线程发起的：碰控件必须切回 UI 线程，
        //   否则抛 AndroidRuntimeException("Animators may only be run on Looper threads")
        //   并被 fire-and-forget 吞掉 —— 表现为「自动检查更新完全不工作」。
        RunOnUiThread(() => _checkButton.Enabled = false);

        try
        {
            var api = Intent?.GetStringExtra(UpdateApiExtra);
            var info = await UpdateChecker.FetchLatestAsync(api, CancellationToken.None);
            ServerLog.Info($"最新 Release：{info.Tag}（本机 v{EmbeddedServer.DisplayVersion}）" +
                           (info.HasApk ? $" · 安卓包 {info.ApkName}" : " · 该 Release 未附带安卓包"));

            if (!UpdateChecker.IsNewer(info.Tag, EmbeddedServer.DisplayVersion))
            {
                _pendingUpdate = null;
                RunOnUiThread(() => _updateCard.Visibility = ViewStates.Gone);
                if (!auto)
                    Toast.MakeText(this, "已是最新版本", ToastLength.Short)?.Show();
                return;
            }

            if (!info.HasApk)
            {
                ServerLog.Warn($"Release {info.Tag} 里没有 BlueRebirthApp-*.apk，无法自动更新");
                if (!auto)
                    Toast.MakeText(this, "发现新版本，但官方没有提供安卓包", ToastLength.Long)?.Show();
                return;
            }

            _pendingUpdate = info;
            RunOnUiThread(() =>
            {
                _updateText.Text = $"有新版本 {info.Tag} 可用";
                _updateCard.Visibility = ViewStates.Visible;
            });
        }
        catch (Exception ex)
        {
            ServerLog.Error("检查更新失败", ex);
            if (!auto)
                Toast.MakeText(this, "检查更新失败（请检查网络）", ToastLength.Long)?.Show();
        }
        finally
        {
            _updateRunning = false;
            RunOnUiThread(() => _checkButton.Enabled = true);
        }
    }

    /// <summary>点了「立即更新」：下载并交给系统安装。</summary>
    private async Task DoUpdateAsync()
    {
        var info = _pendingUpdate;
        if (info?.ApkUrl is null || info.ApkName is null)
        {
            _ = CheckUpdateAsync(auto: false);
            return;
        }

        if (_updateRunning || _kitRunning)
            return;

        var confirmed = await ConfirmAsync("启动器更新",
            $"下载并安装 {info.Tag}？安装时系统会弹一次确认。");
        if (!confirmed)
            return;

        if (!EnsureInstallPermission())
            return;

        _updateRunning = true;
        _updateAction.Enabled = false;
        try
        {
            var dir = System.IO.Path.Combine(CacheDir?.AbsolutePath ?? FilesDir!.AbsolutePath, "update");
            Directory.CreateDirectory(dir);
            var target = System.IO.Path.Combine(dir, info.ApkName);

            await UpdateChecker.DownloadAsync(info.ApkUrl, target, (done, total) =>
                RunOnUiThread(() => _updateText.Text = total > 0
                    ? $"正在下载更新  {done * 100 / total}%"
                    : "正在下载更新…"), CancellationToken.None);

            ServerLog.Info($"更新包已下载：{target}");
            RunOnUiThread(() => _updateText.Text = "正在安装，请在系统弹窗点「安装」");
            await KitInstaller.InstallApkFileAsync(this, target, info.ApkName, OnKitProgress,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            ServerLog.Error("更新失败", ex);
            Toast.MakeText(this, "更新失败：" + ex.Message, ToastLength.Long)?.Show();
            RunOnUiThread(() => _updateText.Text = "更新失败，可稍后再试");
        }
        finally
        {
            _updateRunning = false;
            RunOnUiThread(() => _updateAction.Enabled = true);
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

    private void OnServerChanged() => RunOnUiThread(RenderState);

    /// <summary>重算资源体检结果并刷新状态卡与按钮（启动 / 回到前台 / 装完套件时调用）。</summary>
    private void RefreshStatus() => RunOnUiThread(() =>
    {
        _resources = EmbeddedServer.InspectResources(this);
        RenderState();
    });

    /// <summary>
    /// 状态卡：整个界面唯一需要使用者读的内容。
    /// 只讲两件事 ——「现在能不能玩」和「不能的话点哪里」。
    /// </summary>
    private void RenderState()
    {
        var resources = _resources ??= EmbeddedServer.InspectResources(this);
        var running = EmbeddedServer.State is ServerState.Ready or ServerState.Starting;

        string title, detail, fill, stroke;

        if (!resources.Ready)
        {
            // ★ 阻塞：资源没装好，什么都做不了 —— 用最醒目的红卡 + 直接告诉点哪里。
            title = "还不能开始游戏";
            detail = resources.ConfigReady
                ? "资源包不完整。点下面「安装资源包」，重新选一次 BlueRebirth-kit 文件。"
                : "第一次使用：点下面「安装资源包」，选择 BlueRebirth-kit-…brk 文件。";
            fill = "#FDECEC";
            stroke = "#C0392B";
        }
        else
        {
            switch (EmbeddedServer.State)
            {
                case ServerState.Ready:
                    title = "可以开始游戏了";
                    detail = "点下面「开始游戏」。";
                    fill = "#E8F5EE";
                    stroke = "#0B8457";
                    break;
                case ServerState.Starting:
                    title = _playBusy ? "正在进入游戏…" : "正在启动…";
                    detail = "请稍等几秒。";
                    fill = "#FFF6E5";
                    stroke = "#B7791F";
                    break;
                case ServerState.Error:
                    title = "启动失败";
                    detail = "点下面「开始游戏」重试，或点「日志」查看原因。";
                    fill = "#FDECEC";
                    stroke = "#C0392B";
                    break;
                default:
                    title = "准备就绪";
                    detail = "点下面「开始游戏」。";
                    fill = "#EEF2F7";
                    stroke = "#94A3B8";
                    break;
            }
        }

        _statusTitle.Text = title;
        _statusTitle.SetTextColor(Color.ParseColor(stroke));
        _statusDetail.Text = detail;
        _statusDetail.SetTextColor(Color.ParseColor("#4B5563"));
        _statusCard.Background = Rounded(fill, stroke, 14);

        // 按钮可用性：资源不齐时主按钮置灰（并降低不透明度，视觉上明确"不可用"）。
        var canPlay = resources.Ready && !_playBusy && !_kitRunning;
        _playButton.Text = _playBusy ? "正在进入游戏…" : "开始游戏";
        _playButton.Enabled = canPlay;
        _playButton.Alpha = canPlay ? 1f : 0.5f;
        _playButton.Background = Rounded("#0B8457", null, 12);

        _stopButton.Enabled = running;

        if (_kitRunning)
            UpdateKitButton();
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

    /// <summary>
    /// 申请「读写外部存储」。安装资源包时要把热更资源铺进**游戏客户端自己的**资源目录
    /// （/sdcard/Android/data/com.zephyrus.clsy.gp/files/bundles），API 28 上写别的 App
    /// 的外部目录必须持有该权限；API 30+ 系统不再真正授予，那时铺设会失败并只告警
    /// （客户端仍可从服务端的明文端口热更）。
    /// </summary>
    private void RequestStoragePermissionIfNeeded(bool userInitiated)
    {
        // API 23 以下安装即授予；API 33+ 该权限已被系统忽略，不必打扰用户。
        if (Build.VERSION.SdkInt < BuildVersionCodes.M || Build.VERSION.SdkInt > BuildVersionCodes.S)
            return;

        if (CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted)
            return;

        if (userInitiated)
        {
            Toast.MakeText(this, "请允许存储权限：否则资源无法铺进游戏目录",
                ToastLength.Long)?.Show();
        }

        try
        {
            RequestPermissions(new[]
            {
                Android.Manifest.Permission.WriteExternalStorage,
                Android.Manifest.Permission.ReadExternalStorage,
            }, StoragePermissionRequest);
        }
        catch (Exception ex)
        {
            ServerLog.Error("申请存储权限失败", ex);
        }
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode != StoragePermissionRequest)
            return;

        var granted = CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage)
                      == Permission.Granted;
        ServerLog.Info(granted
            ? "已获得存储权限：安装资源包时会把资源铺进游戏目录"
            : "未获得存储权限：安装资源包时无法铺设客户端资源（客户端仍需走热更）");
        RefreshStatus();
    }
}
