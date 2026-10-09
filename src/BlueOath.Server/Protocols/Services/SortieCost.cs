using System.Text.Json;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 出击消耗（「真实消耗资源」选项）：复刻日服 LevelDetailsPage:_GetSupplyNum / _PreconditionCheck。
/// <list type="bullet">
/// <item>只在 BattleMode == 1（普通出击）收费；演习、回忆、扫荡不收。</item>
/// <item>共闘单人（IsPvePtMode）扣共闘RP（货币 30）× config_parameter[527]。</item>
/// <item>否则扣燃料（货币 5）= config_copy_display.total_supple_num[出击舰数 - 1]；max_fleet &gt; 0 的关卡按 6 艘计；
/// 新海域章节（config_chapter.new_ocean_tag == 1，服务端总是报告已首通）改用 after_clear_supple_num（为空时免费）。</item>
/// <item>出击舰数只算主舰队：客户端 CopyService.JoinBattleFleetList 先按 split_team 每组发一个 HeroList，
/// 有附属舰队时再追加一个，而 _GetSupplyNum 只数 fleet.heroInfo。</item>
/// <item>追击（IsRunningFight）按追击关卡的燃料表：客户端 GetCopyChaseInfo 取章节 running_level_list 中与本关同位置的关卡，
/// 请求里发的仍是本关 id。</item>
/// </list>
/// </summary>
internal static class SortieCost
{
    internal const int SupplyCurrency = 5;
    internal const int PvePtCurrency = 30;
    internal const int NormalBattleMode = 1;

    /// <summary>一个关卡的燃料表；SplitTeams 是 split_team 的组数（主舰队拆成几个 HeroList，至少 1）。</summary>
    internal sealed record Display(
        IReadOnlyList<long> Total, IReadOnlyList<long> AfterClear, long MaxFleet, bool NewOcean, int SplitTeams = 1);

    /// <summary>算出这次出击要扣的货币与数量；不收费时 Amount 为 0。shipCountsByList 是请求里各 HeroList 的舰数（按发送顺序）。</summary>
    internal static (int Currency, long Amount) Cost(
        Display? display, int battleMode, bool pvePtMode, IReadOnlyList<int>? shipCountsByList, int pvePtCost)
    {
        if (battleMode != NormalBattleMode) return (0, 0);
        if (pvePtMode) return (PvePtCurrency, Math.Max(0, pvePtCost));
        if (display is null) return (0, 0);
        int mainShips = (shipCountsByList ?? []).Take(Math.Max(1, display.SplitTeams)).Sum();
        int ships = display.MaxFleet > 0 ? 6 : Math.Clamp(mainShips, 0, 6);
        if (ships == 0) return (0, 0);
        IReadOnlyList<long> table = display.NewOcean ? display.AfterClear : display.Total;
        if (table.Count == 0) return (0, 0);
        return (SupplyCurrency, Math.Max(0, table[Math.Min(ships, table.Count) - 1]));
    }
}

/// <summary>config_copy_display 的燃料字段与 config_chapter.new_ocean_tag（按关卡 id 索引）。</summary>
internal static class SortieCostLoader
{
    private static readonly Dictionary<int, SortieCost.Display> _displays = new();
    private static readonly Dictionary<int, (IReadOnlyList<int> Levels, IReadOnlyList<int> Running)> _chapters = new();
    private static bool _loaded;

    public static void Load(string configDir)
    {
        if (_loaded) return;
        try
        {
            var newOceanCopies = new HashSet<int>();
            ConfigDbLoader.LoadRows(configDir, "config_chapter.db", (chapterId, _, json) =>
            {
                using var doc = JsonDocument.Parse(json);
                IReadOnlyList<int> running = Ints(doc.RootElement, "running_level_list");
                if (running.Count > 0) _chapters[chapterId] = (Ints(doc.RootElement, "level_list"), running);
                if (!doc.RootElement.TryGetProperty("new_ocean_tag", out var tag) || tag.ValueKind != JsonValueKind.Number ||
                    tag.GetInt32() != 1)
                    return;
                foreach (string key in new[] { "level_list", "running_level_list" })
                    if (doc.RootElement.TryGetProperty(key, out var levels) && levels.ValueKind == JsonValueKind.Array)
                        foreach (var level in levels.EnumerateArray())
                            if (level.ValueKind == JsonValueKind.Number) newOceanCopies.Add(level.GetInt32());
            });
            ConfigDbLoader.LoadRows(configDir, "config_copy_display.db", (id, _, json) =>
            {
                using var doc = JsonDocument.Parse(json);
                _displays[id] = new SortieCost.Display(
                    Longs(doc.RootElement, "total_supple_num"),
                    Longs(doc.RootElement, "after_clear_supple_num"),
                    doc.RootElement.TryGetProperty("max_fleet", out var maxFleet) && maxFleet.ValueKind == JsonValueKind.Number
                        ? maxFleet.GetInt64()
                        : 0,
                    newOceanCopies.Contains(id),
                    doc.RootElement.TryGetProperty("split_team", out var split) && split.ValueKind == JsonValueKind.Array
                        ? Math.Max(1, split.GetArrayLength())
                        : 1);
            });
        }
        catch { }
        _loaded = true;
    }

    public static SortieCost.Display? Get(int copyId) => _displays.GetValueOrDefault(copyId);

    /// <summary>
    /// 计费用的关卡 id：追击时取章节 running_level_list 中与本关在 level_list 里同位置的关卡（客户端 GetCopyChaseInfo），
    /// 章节不明时在所有章节里找；找不到或不是追击时就是本关。
    /// </summary>
    public static int ChargedCopyId(int chapterId, int copyId, bool runningFight)
    {
        if (!runningFight) return copyId;
        if (_chapters.TryGetValue(chapterId, out var chapter) && RunningOf(chapter, copyId) is { } running) return running;
        foreach (var other in _chapters.Values)
            if (RunningOf(other, copyId) is { } found)
                return found;
        return copyId;
    }

    private static int? RunningOf((IReadOnlyList<int> Levels, IReadOnlyList<int> Running) chapter, int copyId)
    {
        int index = IndexOf(chapter.Levels, copyId);
        return index >= 0 && index < chapter.Running.Count ? chapter.Running[index] : null;
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value)
                return i;
        return -1;
    }

    private static IReadOnlyList<int> Ints(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => item.GetInt32()).ToList()
            : [];

    private static IReadOnlyList<long> Longs(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => item.GetInt64()).ToList()
            : [];
}
