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
/// 「日志」页面：从主界面点按钮跳进来。
///
/// 主界面刻意不显示任何技术信息，全部集中到这一页：
///   · 顶部「诊断信息」：版本、资源 / 配置表 / 客户端状态、关键路径 —— 反馈问题时报这些就够；
///   · 下面就是完整的运行日志（实时追加、自动滚到底部）；
///   · 「复制日志」一键把诊断信息 + 日志拷到剪贴板，便于发给开发者。
/// </summary>
[Activity(
    Label = "运行日志",
    ParentActivity = typeof(MainActivity),
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public sealed class LogActivity : Activity
{
    private TextView _diagnostics = null!;
    private TextView _log = null!;
    private ScrollView _scroll = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        EmbeddedServer.InitAppVersion(this);
        BuildUi();

        ServerLog.Appended += OnLogAppended;
        Render();
    }

    protected override void OnDestroy()
    {
        ServerLog.Appended -= OnLogAppended;
        base.OnDestroy();
    }

    private void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(16), Dp(14), Dp(16), Dp(12));
        root.SetBackgroundColor(Color.ParseColor("#F5F6F8"));

        _diagnostics = new TextView(this) { TextSize = 12f };
        _diagnostics.SetTextColor(Color.ParseColor("#374151"));
        _diagnostics.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        _diagnostics.SetTextIsSelectable(true);
        _diagnostics.Background = Rounded("#FFFFFF", "#D1D5DB", 10);
        root.AddView(_diagnostics, WrapWrap());

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var rowParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        { TopMargin = Dp(10) };
        root.AddView(row, rowParams);

        var copy = new Button(this) { Text = "复制日志", TextSize = 14f };
        copy.SetTextColor(Color.ParseColor("#1F2937"));
        copy.Background = Rounded("#FFFFFF", "#94A3B8", 10);
        copy.Click += (_, _) => CopyLog();
        row.AddView(copy, Weighted());

        var scrollButton = new Button(this) { Text = "滚到底部", TextSize = 14f };
        scrollButton.SetTextColor(Color.ParseColor("#1F2937"));
        scrollButton.Background = Rounded("#FFFFFF", "#94A3B8", 10);
        scrollButton.Click += (_, _) => ScrollToEnd();
        row.AddView(scrollButton, Weighted());

        _log = new TextView(this) { TextSize = 10.5f };
        _log.Typeface = Typeface.Monospace;
        _log.SetTextColor(Color.ParseColor("#111827"));
        _log.SetTextIsSelectable(true);

        _scroll = new ScrollView(this);
        _scroll.SetBackgroundColor(Color.ParseColor("#FFFFFF"));
        _scroll.AddView(_log, WrapWrap());

        var scrollParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f)
        { TopMargin = Dp(10) };
        root.AddView(_scroll, scrollParams);

        SetContentView(root);
    }

    private static LinearLayout.LayoutParams WrapWrap() =>
        new(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);

    private LinearLayout.LayoutParams Weighted() =>
        new(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) };

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

    // ---------- 内容 ----------

    private void Render()
    {
        _diagnostics.Text = BuildDiagnostics();
        _log.Text = ServerLog.Snapshot();
        ScrollToEnd();
    }

    /// <summary>反馈问题时最有用的那几行（资源 / 配置 / 客户端 / 路径）。</summary>
    private string BuildDiagnostics()
    {
        var info = EmbeddedServer.InspectResources(this);
        var lines = new List<string>
        {
            $"启动器 v{EmbeddedServer.DisplayVersion}",
            $"服务状态：{EmbeddedServer.State} · {EmbeddedServer.Message}",
            $"资源包：{(info.BundleReady ? "已就绪" : "缺失")}" +
            (info.BundleRoot is null ? string.Empty : " · " + info.BundleRoot),
            $"配置表：{(info.ConfigReady ? $"{info.ConfigCount} 个（{info.ConfigSource}）" : "缺失")}",
            $"客户端安装包：{info.ClientPackageVersion ?? "未安装"}",
            $"客户端内容版本：{info.ClientResourceVersion ?? "未铺设"}",
            $"资源版本（服务端宣告）：{EmbeddedServer.ClientVersion}",
        };

        if (!info.Ready)
            lines.Add("⚠ 阻塞：" + (info.Blocker ?? "资源不齐"));

        return string.Join('\n', lines);
    }

    private void OnLogAppended(string line) => RunOnUiThread(() =>
    {
        _log.Append(line + "\n");
        _scroll.Post(ScrollToEnd);
    });

    private void ScrollToEnd() => _scroll.FullScroll(FocusSearchDirection.Down);

    private void CopyLog()
    {
        try
        {
            var text = BuildDiagnostics() + "\n\n---- 日志 ----\n" + ServerLog.Snapshot();
            var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
            clipboard?.PrimaryClip = ClipData.NewPlainText("BO-SRV", text);
            Toast.MakeText(this, "已复制，可直接粘贴反馈", ToastLength.Short)?.Show();
        }
        catch (Exception ex)
        {
            ServerLog.Error("复制日志失败", ex);
            Toast.MakeText(this, "复制失败", ToastLength.Short)?.Show();
        }
    }
}
