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

    // 作弊选项（跳过时间）：启动前由 ApplyCheats 从设置文件刷新，再由 CheatArguments 转成服务端开关。
    public bool CheatProduction { get; set; }
    public bool CheatStrength { get; set; }
    public bool CheatVow { get; set; }
    public bool CheatMood { get; set; }
    public bool CheatMaterials { get; set; }
    public bool CheatMedals { get; set; }

    // 原规则选项（资源与商店）：默认关闭即离线版的免费规则，与作弊开关一起由 ApplyCheats 刷新、CheatArguments 转换。
    public bool RealResourceCost { get; set; }
    public bool RealShopStock { get; set; }

    public bool HasCheats => CheatProduction || CheatStrength || CheatVow || CheatMood || CheatMaterials || CheatMedals;

    public bool HasRules => RealResourceCost || RealShopStock;

    /// <summary>从设置复制作弊与原规则开关（启动时以设置文件为准）。</summary>
    public void ApplyCheats(SettingsConfig settings)
    {
        CheatProduction = settings.CheatProduction;
        CheatStrength = settings.CheatStrength;
        CheatVow = settings.CheatVow;
        CheatMood = settings.CheatMood;
        CheatMaterials = settings.CheatMaterials;
        CheatMedals = settings.CheatMedals;
        RealResourceCost = settings.RealResourceCost;
        RealShopStock = settings.RealShopStock;
    }

    /// <summary>按固定顺序生成服务端作弊开关，再接原规则开关（都是裸开关，无值）；全关时返回空列表。</summary>
    public IReadOnlyList<string> CheatArguments()
    {
        var args = new List<string>(8);
        if (CheatProduction) args.Add("--cheat-production");
        if (CheatStrength) args.Add("--cheat-strength");
        if (CheatVow) args.Add("--cheat-vow");
        if (CheatMood) args.Add("--cheat-mood");
        if (CheatMaterials) args.Add("--cheat-materials");
        if (CheatMedals) args.Add("--cheat-medals");
        if (RealResourceCost) args.Add("--real-resource-cost");
        if (RealShopStock) args.Add("--real-shop-stock");
        return args;
    }

    /// <summary>已开启作弊的中文名，如「生产、心情」；全关时返回空串。</summary>
    public string CheatDisplayNames() =>
        DescribeCheats(CheatProduction, CheatStrength, CheatVow, CheatMood, CheatMaterials, CheatMedals);

    /// <summary>已开启原规则选项的中文名，如「真实消耗资源」；全关时返回空串。</summary>
    public string RuleDisplayNames() => DescribeRules(RealResourceCost, RealShopStock);

    /// <summary>启动页提示：「已开启作弊：…」与「按原规则：…」，两类都有时用「；」连接；全关时返回空串。</summary>
    public string SummaryText()
    {
        var parts = new List<string>(2);
        var cheats = CheatDisplayNames();
        if (cheats.Length > 0) parts.Add($"已开启作弊：{cheats}");
        var rules = RuleDisplayNames();
        if (rules.Length > 0) parts.Add($"按原规则：{rules}");
        return string.Join("；", parts);
    }

    /// <summary>各项作弊的中文名（固定顺序，用「、」连接），也用于描述服务端 ready JSON 回显的 cheats。</summary>
    public static string DescribeCheats(bool production, bool strength, bool vow, bool mood, bool materials = false,
        bool medals = false)
    {
        var names = new List<string>(6);
        if (production) names.Add("生产");
        if (strength) names.Add("体力");
        if (vow) names.Add("许愿墙");
        if (mood) names.Add("心情");
        if (materials) names.Add("无限道具");
        if (medals) names.Add("探索勋章");
        return string.Join("、", names);
    }

    /// <summary>两项原规则选项的中文名（固定顺序，用「、」连接），也用于描述服务端 ready JSON 回显的 cheats。</summary>
    public static string DescribeRules(bool realResourceCost, bool realShopStock)
    {
        var names = new List<string>(2);
        if (realResourceCost) names.Add("真实消耗资源");
        if (realShopStock) names.Add("商店真实库存");
        return string.Join("、", names);
    }

    /// <summary>作弊与原规则全部选项的中文名（作弊在前，用「、」连接），用于核对服务端回显的日志。</summary>
    public static string DescribeOptions(bool production, bool strength, bool vow, bool mood,
        bool realResourceCost, bool realShopStock, bool materials = false, bool medals = false)
    {
        var cheats = DescribeCheats(production, strength, vow, mood, materials, medals);
        var rules = DescribeRules(realResourceCost, realShopStock);
        if (cheats.Length == 0) return rules;
        if (rules.Length == 0) return cheats;
        return cheats + "、" + rules;
    }
}
