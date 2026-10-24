using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 一件装饰品（config_interaction_item_bag 的一行，GoodsType 25）。
/// Type：0 = 其他（摆件 / 套装家具），1 = 海报；Group：所属套装（config_interaction_item_bag_group），0 = 不属于套装；
/// InteractionItem：摆出来的 config_interaction_item（海报的模型取自本行，可以为 0）；
/// ShowType：0 = 在背包「装飾」页显示；Source：「config」或声明它的 Mod id。
/// </summary>
internal sealed record DecorBag(int Id, int Type, int Group, int InteractionItem, int ShowType, string Source = DecorCatalog.ConfigSource);

/// <summary>家具套装（config_interaction_item_bag_group 的一行）：套装 id 与按配置顺序列出的装饰品 id。</summary>
internal sealed record DecorGroup(int Id, IReadOnlyList<int> Bags, string Source = DecorCatalog.ConfigSource);

/// <summary>
/// 指挥室装饰目录：客户端配置里的装饰品、套装、可摆出的物件类型、海报挂点与水晶球小人，外加 Mod 用 decorations.json 声明的
/// 装饰品与套装。服务端只推送这里有的 id：客户端遇到配置里没有的装饰品或套装会在背包页、红点或主页 onStart 时报错。
/// </summary>
internal sealed class DecorCatalog
{
    internal const string ConfigSource = "config";

    /// <summary>装饰品 id 段：客户端按 id / 10000 = 25 到 config_interaction_item_bag 取配置（baglogic.lua GetItemByConfig）。</summary>
    internal const int TableIndex = 25;

    internal const int BagTypeOther = 0;
    internal const int BagTypePoster = 1;

    /// <summary>decorateMutexType.furnitureTheme：唯一的互斥装饰组。</summary>
    internal const int MutexFurnitureTheme = 1;

    /// <summary>InteractionItemType.SnowGlobe（水晶球）。</summary>
    internal const int ItemTypeSnowGlobe = 3;

    /// <summary>config_parameter 377 poster_interaction_item：主页三个海报框。</summary>
    internal const int PosterPointsParameter = 377;

    public static DecorCatalog Empty { get; } = new([], [], new Dictionary<int, int>(), [], []);

    public DecorCatalog(
        IEnumerable<DecorBag> bags,
        IEnumerable<DecorGroup> groups,
        IReadOnlyDictionary<int, int> interactionItemTypes,
        IReadOnlyList<int> posterPoints,
        IEnumerable<int> figures)
    {
        BagList = bags.OrderBy(bag => bag.Id).ToList();
        Bags = BagList.ToDictionary(bag => bag.Id);
        GroupList = groups.OrderBy(group => group.Id).ToList();
        Groups = GroupList.ToDictionary(group => group.Id);
        InteractionItemTypes = interactionItemTypes;
        PosterPoints = posterPoints;
        Figures = figures.ToHashSet();
    }

    /// <summary>全部装饰品，按 id 排序。</summary>
    public IReadOnlyList<DecorBag> BagList { get; }

    public IReadOnlyDictionary<int, DecorBag> Bags { get; }

    /// <summary>全部套装，按 id 排序。</summary>
    public IReadOnlyList<DecorGroup> GroupList { get; }

    public IReadOnlyDictionary<int, DecorGroup> Groups { get; }

    /// <summary>config_interaction_item：id → interaction_item_type。</summary>
    public IReadOnlyDictionary<int, int> InteractionItemTypes { get; }

    /// <summary>海报挂点（config_parameter 377 的 arrValue，按配置顺序）。</summary>
    public IReadOnlyList<int> PosterPoints { get; }

    /// <summary>config_interaction_figurte 的 id（水晶球里能摆的小人）。</summary>
    public IReadOnlySet<int> Figures { get; }

    /// <summary>配置读到了装饰品（没读到时不改存档、不迁移背包）。</summary>
    public bool IsLoaded => BagList.Count > 0;

    /// <summary>id 在装饰品的 id 段（25xxxx），不论配置里有没有这一行。</summary>
    public static bool IsDecorItemId(int templateId) => templateId > 0 && templateId / 10_000 == TableIndex;

    /// <summary>按客户端规则可以单独切换显示的装饰品：不属于套装、type 0（iteminfopage.lua _ShowItemVisible）。</summary>
    public static bool IsToggleable(DecorBag bag) => bag.Group == 0 && bag.Type == BagTypeOther;

    /// <summary>加上 Mod 声明的装饰品与套装（调用方保证 id 不与已有的重复）。</summary>
    public DecorCatalog With(IEnumerable<DecorBag> bags, IEnumerable<DecorGroup> groups) =>
        new(BagList.Concat(bags), GroupList.Concat(groups), InteractionItemTypes, PosterPoints, Figures);
}

/// <summary>
/// 从 --client-path 的客户端配置载入 <see cref="DecorCatalog"/>（行数不写死：日服热更版 68 件装饰品，1.4.0 是 66 件），
/// 再并入启用的 Mod 在 decorations.json 里声明的装饰品与套装（<see cref="DecorModLoader"/>）。
/// </summary>
internal static class DecorCatalogLoader
{
    private static bool _loaded;

    /// <summary>服务端使用的目录（首次 <see cref="Load"/> 之前为空目录）。</summary>
    public static DecorCatalog Catalog { get; private set; } = DecorCatalog.Empty;

    public static void Load(string configDir, string modsRoot, string clientId, ILogger logger)
    {
        if (_loaded) return;
        DecorCatalog config = LoadConfig(configDir, logger);
        Catalog = config.IsLoaded ? DecorModLoader.Load(modsRoot, clientId, config, logger) : config;
        logger.LogInformation(
            "decor catalog: {Bags} decorations ({ModBags} from mods), {Groups} sets, {Points} poster points",
            Catalog.BagList.Count, Catalog.BagList.Count(bag => bag.Source != DecorCatalog.ConfigSource),
            Catalog.GroupList.Count, Catalog.PosterPoints.Count);
        _loaded = true;
    }

    /// <summary>只读客户端配置（不含 Mod）。配置缺失或损坏时返回空目录并记警告。</summary>
    internal static DecorCatalog LoadConfig(string configDir, ILogger? logger = null)
    {
        try
        {
            var bags = new List<DecorBag>();
            ConfigDbLoader.LoadRows(configDir, "config_interaction_item_bag.db", (id, _, json) =>
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement row = doc.RootElement;
                bags.Add(new DecorBag(id, Int(row, "type"), Int(row, "interactionitem_bag_group"),
                    Int(row, "interactionitem"), Int(row, "show_type")));
            });
            var groups = new List<DecorGroup>();
            ConfigDbLoader.LoadRows(configDir, "config_interaction_item_bag_group.db", (id, _, json) =>
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                groups.Add(new DecorGroup(id, Ints(doc.RootElement, "interactionitem_bag")));
            });
            var itemTypes = new Dictionary<int, int>();
            ConfigDbLoader.LoadRows(configDir, "config_interaction_item.db", (id, _, json) =>
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                itemTypes[id] = Int(doc.RootElement, "interaction_item_type");
            });
            IReadOnlyList<int> points = [];
            ConfigDbLoader.LoadRows(configDir, "config_parameter.db", (id, _, json) =>
            {
                if (id != DecorCatalog.PosterPointsParameter) return;
                using JsonDocument doc = JsonDocument.Parse(json);
                points = Ints(doc.RootElement, "arrValue");
            });
            var figures = new List<int>();
            ConfigDbLoader.LoadRows(configDir, "config_interaction_figurte.db", (id, _, _) => figures.Add(id));

            // 套装只保留配置里存在的装饰品（客户端按 id 取行，缺行会报错）；海报挂点必须是配置里的物件。
            var bagIds = bags.Select(bag => bag.Id).ToHashSet();
            groups = groups.Select(group => group with { Bags = group.Bags.Where(bagIds.Contains).ToList() }).ToList();
            points = points.Where(itemTypes.ContainsKey).Distinct().ToList();
            if (bags.Count == 0)
                logger?.LogWarning("decor catalog: config_interaction_item_bag not found in {ConfigDir}; decorations are disabled", configDir);
            return new DecorCatalog(bags, groups, itemTypes, points, figures);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or
                                       Microsoft.Data.Sqlite.SqliteException)
        {
            logger?.LogWarning(ex, "decor catalog: client config in {ConfigDir} could not be read; decorations are disabled", configDir);
            return DecorCatalog.Empty;
        }
    }

    private static int Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number) ? number : 0;

    private static IReadOnlyList<int> Ints(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array) return [];
        var result = new List<int>();
        foreach (JsonElement item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number) && number > 0)
                result.Add(number);
        return result;
    }
}
