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

    public bool HasCheats => CheatProduction || CheatStrength || CheatVow || CheatMood;

    /// <summary>从设置复制作弊开关（启动时以设置文件为准）。</summary>
    public void ApplyCheats(SettingsConfig settings)
    {
        CheatProduction = settings.CheatProduction;
        CheatStrength = settings.CheatStrength;
        CheatVow = settings.CheatVow;
        CheatMood = settings.CheatMood;
    }

    /// <summary>按固定顺序生成服务端作弊开关（裸开关，无值）；全关时返回空列表。</summary>
    public IReadOnlyList<string> CheatArguments()
    {
        var args = new List<string>(4);
        if (CheatProduction) args.Add("--cheat-production");
        if (CheatStrength) args.Add("--cheat-strength");
        if (CheatVow) args.Add("--cheat-vow");
        if (CheatMood) args.Add("--cheat-mood");
        return args;
    }

    /// <summary>已开启作弊的中文名，如「生产、心情」；全关时返回空串。</summary>
    public string CheatDisplayNames() => DescribeCheats(CheatProduction, CheatStrength, CheatVow, CheatMood);

    /// <summary>四类作弊的中文名（固定顺序，用「、」连接），也用于描述服务端 ready JSON 回显的 cheats。</summary>
    public static string DescribeCheats(bool production, bool strength, bool vow, bool mood)
    {
        var names = new List<string>(4);
        if (production) names.Add("生产");
        if (strength) names.Add("体力");
        if (vow) names.Add("许愿墙");
        if (mood) names.Add("心情");
        return string.Join("、", names);
    }
}
