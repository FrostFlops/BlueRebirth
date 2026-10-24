using System.Globalization;
using System.Text.Json;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 一期可下发的杂志（config_magazine_info 一行，已通过 <see cref="MagazineCatalog.Build"/> 的完整性检查）。
/// </summary>
/// <param name="Id">config_magazine_info.id（列表页显示为 "No." .. id）。</param>
/// <param name="Special">special = 1 的特别号（sp1 / sp2）：没有印章箱，也没有栞解锁按钮。</param>
/// <param name="Order">排序键：客户端按 order 降序、id 升序，第一项是「最新号」（活动期内才是 Active）。</param>
/// <param name="Period">活动周期（config_period.id）。</param>
/// <param name="TaskPeriodArea">任务期的周期分段（task_period_area），Active 状态按它判断。</param>
/// <param name="TaskIds">这一期引用的全部任务：task_left_id、task_right_id 与每一页的 task_id（去重，保持出现顺序）。</param>
internal sealed record MagazineIssue(
    int Id, bool Special, int Order, int Period, IReadOnlyList<int> TaskPeriodArea, IReadOnlyList<int> TaskIds);

/// <summary>构建杂志目录用到的客户端配置（全部从 --client-path 的 config_*.db 读取，行数不写死）。</summary>
/// <param name="Infos">config_magazine_info。</param>
/// <param name="Pages">config_magazine_page。</param>
/// <param name="Tasks">config_task_magazine。</param>
/// <param name="RewardIds">config_rewards 里至少有一项奖励的 id（只读杂志任务引用到的行）。</param>
/// <param name="TagIds">config_magazine_tag 的 id。</param>
/// <param name="BuffIds">config_value_effect 的 id。</param>
/// <param name="Periods">杂志引用到的 config_period 行。</param>
/// <param name="UnlockCost">config_parameter 365（magazine_release_lock_item）的 arrValue：[GoodsType, id, 数量]；缺失时为空。</param>
internal sealed record MagazineTables(
    IReadOnlyDictionary<int, ConfigMagazineInfo> Infos,
    IReadOnlyDictionary<int, ConfigMagazinePage> Pages,
    IReadOnlyDictionary<int, ConfigTaskMagazine> Tasks,
    IReadOnlySet<int> RewardIds,
    IReadOnlySet<int> TagIds,
    IReadOnlySet<int> BuffIds,
    IReadOnlyDictionary<int, ConfigPeriod> Periods,
    IReadOnlyList<long> UnlockCost)
{
    /// <summary>config_parameter 中栞解锁消耗的 id（日服 [1, 18006, 1]）。</summary>
    internal const int UnlockCostParameterId = 365;

    /// <summary>从客户端配置目录读取；缺表时对应集合为空（随后由完整性检查剔除期刊）。</summary>
    internal static MagazineTables Load(string configDir)
    {
        Dictionary<int, ConfigMagazineInfo> infos = ConfigDbLoader.LoadAll<ConfigMagazineInfo>(configDir, "config_magazine_info.db");
        Dictionary<int, ConfigMagazinePage> pages = ConfigDbLoader.LoadAll<ConfigMagazinePage>(configDir, "config_magazine_page.db");
        Dictionary<int, ConfigTaskMagazine> tasks = ConfigDbLoader.LoadAll<ConfigTaskMagazine>(configDir, "config_task_magazine.db");

        // config_rewards 有七千多行，只解出杂志任务引用到的那些。
        HashSet<int> wantedRewards = tasks.Values.Select(task => checked((int)task.Rewards)).Where(id => id > 0).ToHashSet();
        var rewardIds = new HashSet<int>();
        ConfigDbLoader.LoadRows(configDir, "config_rewards.db", (id, _, json) =>
        {
            if (!wantedRewards.Contains(id)) return;
            try
            {
                if (JsonSerializer.Deserialize<ConfigRewards>(json, JsonOptions) is { Rewards.Count: > 0 })
                    rewardIds.Add(id);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
            }
        });

        HashSet<int> wantedPeriods = infos.Values.Select(info => checked((int)info.Period)).ToHashSet();
        var periods = new Dictionary<int, ConfigPeriod>();
        ConfigDbLoader.LoadRows(configDir, "config_period.db", (id, _, json) =>
        {
            if (!wantedPeriods.Contains(id)) return;
            try
            {
                if (JsonSerializer.Deserialize<ConfigPeriod>(json, JsonOptions) is { } period) periods[id] = period;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
            }
        });

        IReadOnlyList<long> unlockCost = [];
        ConfigDbLoader.LoadRows(configDir, "config_parameter.db", (id, _, json) =>
        {
            if (id != UnlockCostParameterId) return;
            try
            {
                if (JsonSerializer.Deserialize<ConfigParameter>(json, JsonOptions) is { ArrValue: { } values })
                    unlockCost = ToLongs(values);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
            }
        });

        return new MagazineTables(infos, pages, tasks, rewardIds, Ids(configDir, "config_magazine_tag.db"),
            Ids(configDir, "config_value_effect.db"), periods, unlockCost);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static HashSet<int> Ids(string configDir, string dbFile)
    {
        var ids = new HashSet<int>();
        ConfigDbLoader.LoadRows(configDir, dbFile, (id, _, _) => ids.Add(id));
        return ids;
    }

    private static IReadOnlyList<long> ToLongs(List<object> values)
    {
        var result = new List<long>(values.Count);
        foreach (object value in values)
        {
            if (value is JsonElement { ValueKind: JsonValueKind.Number } element && element.TryGetInt64(out long number))
                result.Add(number);
            else if (value is IConvertible convertible)
                result.Add(convertible.ToInt64(CultureInfo.InvariantCulture));
            else
                return [];
        }
        return result;
    }
}

/// <summary>
/// 杂志（真珠湾 Magazine / SK BOOK）的只读目录：可下发的期刊与它们引用的任务。
/// <para>
/// 客户端打开杂志页时不发请求，期刊列表只来自 magazine.UpdateMagazineInfo 推送；列出的期刊引用的每个任务都必须在
/// task.TaskInfo 的字段 14（MagazineTask）里，否则 tasklogic.lua:464 的 GetCurCount(nil) 报错、列表页与单期页渲染中断。
/// 因此只列出 is_open = 1 且页面、任务、任务奖励、标签与 buff 都齐全的期刊（缺任何一项就整期不列），
/// 字段 14 下发的正是这些期刊的任务（<see cref="TaskDefinitions"/>）。
/// </para>
/// <para>
/// 日服（热更与未热更两版的四张表逐字节相同）：10 期（含特别号 4、6）、306 个任务；
/// 所有期刊的活动期都在 2020-06～2021-03，早已结束，往期一律是 Lock / UnLock 状态，不会是 Active。
/// </para>
/// </summary>
internal sealed class MagazineCatalog
{
    /// <summary>日服客户端设备时区（config_period type 1 按客户端本地时间 os.time 计算）。</summary>
    private static readonly TimeSpan ClientUtcOffset = TimeSpan.FromHours(9);

    /// <summary>config_parameter 365 缺失时的栞解锁消耗：道具（GoodsType 1）18006「栞」× 1。</summary>
    internal static readonly CostItem DefaultUnlockCost = new(GameServices.GoodsTypeItem, 18006, 1);

    private static readonly object LoadLock = new();
    private static string? s_loadedDir;

    private readonly Dictionary<int, MagazineIssue> _issues;
    private readonly IReadOnlyDictionary<int, ConfigPeriod> _periods;

    private MagazineCatalog(
        IReadOnlyList<MagazineIssue> issues, IReadOnlyList<TaskDefinition> taskDefinitions, CostItem unlockCost,
        IReadOnlyDictionary<int, ConfigPeriod> periods, IReadOnlyList<string> rejected)
    {
        Issues = issues;
        _issues = issues.ToDictionary(issue => issue.Id);
        TaskDefinitions = taskDefinitions;
        UnlockCost = unlockCost;
        _periods = periods;
        Rejected = rejected;
        Latest = issues.OrderByDescending(issue => issue.Order).ThenBy(issue => issue.Id).FirstOrDefault();
    }

    /// <summary>没有任何期刊的目录（配置缺失或尚未加载）：只推空列表，主页不显示杂志入口。</summary>
    internal static MagazineCatalog Empty { get; } = new([], [], DefaultUnlockCost, new Dictionary<int, ConfigPeriod>(), []);

    /// <summary>
    /// 最近一次 <see cref="Load"/> 的结果。task.TaskInfo 的字段 14 与 magazine.* 协议都读它，
    /// 保证下发的期刊与任务来自同一份配置。
    /// </summary>
    internal static MagazineCatalog Current { get; private set; } = Empty;

    /// <summary>可下发的期刊，按 id 升序。</summary>
    internal IReadOnlyList<MagazineIssue> Issues { get; }

    /// <summary>可下发的期刊 id（magazine.UpdateMagazineInfo 的 MagazineIdList），按 id 升序。</summary>
    internal IReadOnlyList<int> ListedIds => Issues.Select(issue => issue.Id).ToList();

    /// <summary>
    /// 字段 14 下发的任务：每个列出期刊引用的全部任务各一条（TaskType 11，EventType = goal[0]），
    /// 按 EventType、id 排序。
    /// </summary>
    internal IReadOnlyList<TaskDefinition> TaskDefinitions { get; }

    /// <summary>用栞解锁一期的消耗（config_parameter 365）。</summary>
    internal CostItem UnlockCost { get; }

    /// <summary>被剔除的期刊及原因（供启动日志）。</summary>
    internal IReadOnlyList<string> Rejected { get; }

    /// <summary>客户端的「最新号」configs[1]：order 最大、同 order 时 id 最小。没有期刊时为 null。</summary>
    internal MagazineIssue? Latest { get; }

    /// <summary>
    /// 从客户端配置目录载入并设为 <see cref="Current"/>。同一配置目录只读一次（测试会反复构造 GameServices）。
    /// </summary>
    internal static MagazineCatalog Load(string configDir)
    {
        lock (LoadLock)
        {
            if (s_loadedDir is not null && string.Equals(s_loadedDir, configDir, StringComparison.OrdinalIgnoreCase))
                return Current;
            MagazineCatalog catalog = Build(MagazineTables.Load(configDir));
            Current = catalog;
            s_loadedDir = configDir;
            return catalog;
        }
    }

    /// <summary>
    /// 按完整性检查筛选期刊（纯函数）。一期要列出，必须同时满足：
    /// <list type="bullet">
    /// <item>is_open = 1；</item>
    /// <item>page_id、pagedown_id、contents_left、contents_right 引用的页都在 config_magazine_page 里
    /// （HomeBookFlip:SetTextures 与目录按钮直接取 .image / .contents_desc）；</item>
    /// <item>task_left_id、task_right_id 与每页 task_id 引用的任务都在 config_task_magazine 里且 goal 非空；</item>
    /// <item>每页任务与 task_right_id[1] 的 rewards 在 config_rewards 里至少有一项（单期页取 reward[1].Num，
    /// 列表页取 rewardInfo[1].Type）；task_left_id 的 rewards 是 0，客户端不读；</item>
    /// <item>show_tag_id 都在 config_magazine_tag、buff_id 都在 config_value_effect 里（列表页 ShowTag）；</item>
    /// <item>普通号：item 是 [GoodsType, 印章 id]、condition 非空、condition_max 是数字（列表页与印章箱直接做算术/拼接）。</item>
    /// </list>
    /// </summary>
    internal static MagazineCatalog Build(MagazineTables tables)
    {
        var issues = new List<MagazineIssue>();
        var rejected = new List<string>();
        foreach ((int id, ConfigMagazineInfo info) in tables.Infos.OrderBy(pair => pair.Key))
        {
            if (info.IsOpen != 1) continue;
            string? problem = Check(info, tables, out List<int> taskIds);
            if (problem is not null)
            {
                rejected.Add($"magazine {id}: {problem}");
                continue;
            }
            issues.Add(new MagazineIssue(
                id,
                info.Special == 1,
                checked((int)info.Order),
                checked((int)info.Period),
                (info.TaskPeriodArea ?? []).Select(area => checked((int)area)).ToList(),
                taskIds));
        }

        List<TaskDefinition> definitions = issues
            .SelectMany(issue => issue.TaskIds)
            .Distinct()
            .Select(taskId => ToDefinition(tables.Tasks[taskId]))
            .OrderBy(definition => definition.EventType)
            .ThenBy(definition => definition.Id)
            .ToList();
        CostItem unlockCost = tables.UnlockCost is [var type, var itemId, var num] && type > 0 && itemId > 0 && num > 0
            ? new CostItem(checked((int)type), checked((int)itemId), num)
            : DefaultUnlockCost;
        return new MagazineCatalog(issues, definitions, unlockCost, tables.Periods, rejected);
    }

    /// <summary>按 id 取可下发的期刊；未列出（包括配置里不存在）时为 null。</summary>
    internal MagazineIssue? Get(int magazineId) => _issues.GetValueOrDefault(magazineId);

    /// <summary>
    /// 客户端 MagazineLogic:GetMagazineState 的 Active：这一期是「最新号」且现在处于它的任务期（task_period_area）。
    /// Active 时客户端不显示栞按钮与锁定遮罩；日服所有期刊的任务期都已结束。
    /// </summary>
    internal bool IsActive(int magazineId, long now) =>
        Latest is { } latest && latest.Id == magazineId &&
        _periods.TryGetValue(latest.Period, out ConfigPeriod? period) &&
        IsInPeriodArea(period, latest.TaskPeriodArea, now);

    /// <summary>
    /// PeriodManager:IsInPeriodArea 的 type 1（LOCAL，从 p1-p2-p3 p4:p5:p6 开始）实现：
    /// 早于开始、超过 duration 或到了 end_time 时为 false；periodArea 为空时为 true；
    /// 否则看现在落在 duration_list 的第几段（getDurationArea：逐段相减、首次为负的那一段，超出时算最后一段）。
    /// 其它周期类型不实现，按不在期内处理（杂志只用 type 1）。
    /// </summary>
    internal static bool IsInPeriodArea(ConfigPeriod period, IReadOnlyList<int> periodArea, long now)
    {
        if (period.Type != 1) return false;
        long start;
        try
        {
            start = new DateTimeOffset(checked((int)period.P1), checked((int)period.P2), checked((int)period.P3),
                checked((int)period.P4), checked((int)period.P5), checked((int)period.P6), ClientUtcOffset).ToUnixTimeSeconds();
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        if (period.StartTime > 0 && start < period.StartTime) start = period.StartTime;
        long elapsed = now - start;
        if (elapsed < 0) return false;
        if (period.Duration > 0 && elapsed > period.Duration) return false;
        if (period.EndTime > 0 && now >= period.EndTime) return false;
        if (periodArea.Count == 0) return true;
        IReadOnlyList<long> durations = period.DurationList ?? [];
        int area = durations.Count;
        long remaining = elapsed;
        for (int i = 0; i < durations.Count; i++)
        {
            remaining -= durations[i];
            if (remaining < 0)
            {
                area = i + 1;
                break;
            }
        }
        return periodArea.Contains(area);
    }

    private static string? Check(ConfigMagazineInfo info, MagazineTables tables, out List<int> taskIds)
    {
        taskIds = [];
        IEnumerable<long> pageRefs = (info.PageId ?? []).Concat(info.PagedownId ?? [])
            .Concat(info.ContentsLeft ?? []).Concat(info.ContentsRight ?? []);
        foreach (long page in pageRefs)
            if (!tables.Pages.ContainsKey(checked((int)page)))
                return $"page {page} is missing from config_magazine_page";
        if (info.PageId is not { Count: > 0 }) return "it has no pages";

        var ordered = new List<int>();
        var seen = new HashSet<int>();
        void Add(IEnumerable<long>? ids)
        {
            foreach (long raw in ids ?? [])
                if (seen.Add(checked((int)raw)))
                    ordered.Add(checked((int)raw));
        }
        Add(info.TaskLeftId);
        Add(info.TaskRightId);
        var rewarded = new List<int>();
        foreach (long page in info.PageId)
        {
            List<long> pageTasks = tables.Pages[checked((int)page)].TaskId ?? [];
            Add(pageTasks);
            rewarded.AddRange(pageTasks.Select(task => checked((int)task)));
        }
        if (info.TaskRightId is not { Count: > 0 }) return "task_right_id is empty";
        rewarded.Add(checked((int)info.TaskRightId[0]));

        foreach (int taskId in ordered)
            if (!tables.Tasks.TryGetValue(taskId, out ConfigTaskMagazine? task) || task.Goal is not { Count: > 0 })
                return $"task {taskId} is missing from config_task_magazine or has no goal";
        foreach (int taskId in rewarded)
            if (!tables.RewardIds.Contains(checked((int)tables.Tasks[taskId].Rewards)))
                return $"task {taskId} rewards {tables.Tasks[taskId].Rewards} are missing from config_rewards";
        foreach (long tag in info.ShowTagId ?? [])
            if (!tables.TagIds.Contains(checked((int)tag)))
                return $"tag {tag} is missing from config_magazine_tag";
        foreach (long buff in info.BuffId ?? [])
            if (!tables.BuffIds.Contains(checked((int)buff)))
                return $"buff {buff} is missing from config_value_effect";
        if (info.Special != 1)
        {
            if (info.Item is not { Count: >= 2 }) return "item is not [GoodsType, stamp id]";
            if (info.Condition is not { Count: > 0 }) return "condition is empty";
            if (!IsNumber(info.ConditionMax)) return "condition_max is not a number";
        }
        taskIds = ordered;
        return null;
    }

    /// <summary>condition_max 在特别号里是 []，所以生成的配置类把它定义成 object。</summary>
    private static bool IsNumber(object? value) => value switch
    {
        JsonElement element => element.ValueKind == JsonValueKind.Number,
        IConvertible => true,
        _ => false,
    };

    // goal 非空：只对通过 Check（要求 goal 至少一项）的期刊引用的任务调用。
    private static TaskDefinition ToDefinition(ConfigTaskMagazine task) => new(
        TaskConfigCatalog.TypeMagazine,
        checked((int)task.Id),
        checked((int)task.Goal![0]),
        checked((int)Math.Max(1, task.Goal[^1])),
        checked((int)task.CountType),
        checked((int)task.Rewards));
}
