namespace BlueOath.Launcher.Wpf.Models;

public class LaunchConfig
{
    public string ProfileId { get; set; } = "local-player";
    public string ProfileName { get; set; } = "默认账号";
    public string Region { get; set; } = "jp";
    public int ServerPort { get; set; } = 0;
    public int GameLoginPort { get; set; } = 7201;
    public int GmPort { get; set; } = 9780;
    public int ProxyPort { get; set; } = 0;
    public bool SkipBuild { get; set; } = true;
    public bool KeepLog { get; set; } = false;

    // 作弊与原规则选项：启动前由 ApplyCheats 从设置文件刷新，再由 CheatArguments 转成服务端裸开关。
    public bool CheatProduction { get; set; }
    public bool CheatStrength { get; set; }
    public bool CheatVow { get; set; }
    public bool CheatMood { get; set; }
    public bool CheatMedals { get; set; }
    public bool CheatDrops { get; set; }
    public bool CheatSweep { get; set; }
    public bool RealResourceCost { get; set; }
    public bool RealShopStock { get; set; }

    /// <summary>一个选项：服务端开关、ready JSON cheats 回显的键、中文名、是否「按原规则」类（方向与作弊相反）。</summary>
    public sealed record ServerOption(string Switch, string EchoKey, string DisplayName, bool IsRule,
        Func<LaunchConfig, bool> IsEnabled, Action<LaunchConfig, SettingsConfig> CopyFrom);

    /// <summary>全部选项（固定顺序：作弊在前、原规则在后），开关参数、启动页提示与回显核对都按这张表生成。</summary>
    public static IReadOnlyList<ServerOption> Options { get; } =
    [
        new("--cheat-production", "production", "生产", false, c => c.CheatProduction, (c, s) => c.CheatProduction = s.CheatProduction),
        new("--cheat-strength", "strength", "体力", false, c => c.CheatStrength, (c, s) => c.CheatStrength = s.CheatStrength),
        new("--cheat-vow", "vow", "许愿墙", false, c => c.CheatVow, (c, s) => c.CheatVow = s.CheatVow),
        new("--cheat-mood", "mood", "心情", false, c => c.CheatMood, (c, s) => c.CheatMood = s.CheatMood),
        new("--cheat-medals", "medals", "探索勋章", false, c => c.CheatMedals, (c, s) => c.CheatMedals = s.CheatMedals),
        new("--cheat-drops", "drops", "掉落加成", false, c => c.CheatDrops, (c, s) => c.CheatDrops = s.CheatDrops),
        new("--cheat-sweep", "sweep", "扫荡跳过时间", false, c => c.CheatSweep, (c, s) => c.CheatSweep = s.CheatSweep),
        new("--real-resource-cost", "realResourceCost", "真实消耗资源", true,
            c => c.RealResourceCost, (c, s) => c.RealResourceCost = s.RealResourceCost),
        new("--real-shop-stock", "realShopStock", "商店真实库存", true,
            c => c.RealShopStock, (c, s) => c.RealShopStock = s.RealShopStock),
    ];

    public bool HasCheats => Options.Any(option => !option.IsRule && option.IsEnabled(this));

    public bool HasRules => Options.Any(option => option.IsRule && option.IsEnabled(this));

    /// <summary>从设置复制作弊与原规则开关（启动时以设置文件为准）。</summary>
    public void ApplyCheats(SettingsConfig settings)
    {
        foreach (ServerOption option in Options)
            option.CopyFrom(this, settings);
    }

    /// <summary>已开启的服务端开关（裸开关，无值，按 <see cref="Options"/> 的顺序）；全关时返回空列表。</summary>
    public IReadOnlyList<string> CheatArguments() =>
        Options.Where(option => option.IsEnabled(this)).Select(option => option.Switch).ToList();

    /// <summary>启动页提示：「已开启作弊：…」与「按原规则：…」，两类都有时用「；」连接；全关时返回空串。</summary>
    public string SummaryText()
    {
        var parts = new List<string>(2);
        string cheats = Names(Options.Where(option => !option.IsRule && option.IsEnabled(this)));
        if (cheats.Length > 0) parts.Add($"已开启作弊：{cheats}");
        string rules = Names(Options.Where(option => option.IsRule && option.IsEnabled(this)));
        if (rules.Length > 0) parts.Add($"按原规则：{rules}");
        return string.Join("；", parts);
    }

    /// <summary>一组服务端开关对应的中文名（按 <see cref="Options"/> 的顺序，用「、」连接），用于核对服务端回显。</summary>
    public static string DescribeSwitches(IEnumerable<string> switches)
    {
        var set = switches.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Names(Options.Where(option => set.Contains(option.Switch)));
    }

    private static string Names(IEnumerable<ServerOption> options) =>
        string.Join("、", options.Select(option => option.DisplayName));
}
