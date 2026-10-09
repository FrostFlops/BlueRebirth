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

    // 作弊、原规则与实验功能选项：启动前由 ApplyCheats 从设置文件刷新，再由 CheatArguments 转成服务端开关。
    // 默认值与服务端一致：除「战斗数值」外的作弊开启，「真实消耗资源」开启，「商店真实库存」与实验功能关闭。
    public bool CheatProduction { get; set; } = true;
    public bool CheatStrength { get; set; } = true;
    public bool CheatVow { get; set; } = true;
    public bool CheatMood { get; set; } = true;
    public bool CheatMedals { get; set; } = true;
    public bool CheatDrops { get; set; } = true;
    public bool CheatSweep { get; set; } = true;
    public bool CheatBattle { get; set; }
    public bool RealResourceCost { get; set; } = true;
    public bool RealShopStock { get; set; }
    public bool FullBattleStats { get; set; }

    /// <summary>选项类别：作弊、按原规则（方向与作弊相反）、实验功能。</summary>
    public enum OptionKind { Cheat, Rule, Experimental }

    /// <summary>一个选项：服务端开关、ready JSON cheats 回显的键、中文名、类别。</summary>
    public sealed record ServerOption(string Switch, string EchoKey, string DisplayName, OptionKind Kind,
        Func<LaunchConfig, bool> IsEnabled, Action<LaunchConfig, SettingsConfig> CopyFrom);

    /// <summary>全部选项（固定顺序：作弊、原规则、实验功能），开关参数、启动页提示与回显核对都按这张表生成。</summary>
    public static IReadOnlyList<ServerOption> Options { get; } =
    [
        new("--cheat-production", "production", "生产", OptionKind.Cheat, c => c.CheatProduction, (c, s) => c.CheatProduction = s.CheatProduction),
        new("--cheat-strength", "strength", "体力", OptionKind.Cheat, c => c.CheatStrength, (c, s) => c.CheatStrength = s.CheatStrength),
        new("--cheat-vow", "vow", "许愿墙", OptionKind.Cheat, c => c.CheatVow, (c, s) => c.CheatVow = s.CheatVow),
        new("--cheat-mood", "mood", "心情", OptionKind.Cheat, c => c.CheatMood, (c, s) => c.CheatMood = s.CheatMood),
        new("--cheat-medals", "medals", "探索勋章", OptionKind.Cheat, c => c.CheatMedals, (c, s) => c.CheatMedals = s.CheatMedals),
        new("--cheat-drops", "drops", "掉落加成", OptionKind.Cheat, c => c.CheatDrops, (c, s) => c.CheatDrops = s.CheatDrops),
        new("--cheat-sweep", "sweep", "扫荡跳过时间", OptionKind.Cheat, c => c.CheatSweep, (c, s) => c.CheatSweep = s.CheatSweep),
        new("--cheat-battle", "battle", "战斗数值", OptionKind.Cheat, c => c.CheatBattle, (c, s) => c.CheatBattle = s.CheatBattle),
        new("--real-resource-cost", "realResourceCost", "真实消耗资源", OptionKind.Rule,
            c => c.RealResourceCost, (c, s) => c.RealResourceCost = s.RealResourceCost),
        new("--real-shop-stock", "realShopStock", "商店真实库存", OptionKind.Rule,
            c => c.RealShopStock, (c, s) => c.RealShopStock = s.RealShopStock),
        new("--exp-full-battle-stats", "fullBattleStats", "完整战斗属性", OptionKind.Experimental,
            c => c.FullBattleStats, (c, s) => c.FullBattleStats = s.FullBattleStats),
    ];

    /// <summary>从设置复制作弊与原规则开关（启动时以设置文件为准）。</summary>
    public void ApplyCheats(SettingsConfig settings)
    {
        foreach (ServerOption option in Options)
            option.CopyFrom(this, settings);
    }

    /// <summary>
    /// 传给服务端的全部选项（按 <see cref="Options"/> 的顺序，每项都显式给出 <c>=on</c> / <c>=off</c>），
    /// 因此服务端自己的默认值不影响启动器的设置。
    /// </summary>
    public IReadOnlyList<string> CheatArguments() =>
        Options.Select(option => $"{option.Switch}={(option.IsEnabled(this) ? "on" : "off")}").ToList();

    /// <summary>已开启的选项对应的服务端开关（不带值，按 <see cref="Options"/> 的顺序），用于核对服务端回显。</summary>
    public IReadOnlyList<string> EnabledSwitches() =>
        Options.Where(option => option.IsEnabled(this)).Select(option => option.Switch).ToList();

    /// <summary>启动页提示「已开启作弊：…」；没有开启作弊时返回空串（「资源与商店」两项不在启动页提示）。</summary>
    public string SummaryText()
    {
        string cheats = Names(Options.Where(option => option.Kind == OptionKind.Cheat && option.IsEnabled(this)));
        return cheats.Length > 0 ? $"已开启作弊：{cheats}" : "";
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
