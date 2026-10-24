using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 从启用的 Mod 读取 decorations.json，把 Mod 往客户端 config_interaction_item_bag / config_interaction_item_bag_group
/// 注入的行同样告诉服务端（服务端读不到客户端 Lua 注入的配置）。格式：
/// <code>
/// {
///   "groups": [ { "id": 5, "bags": [250041, 250042] } ],
///   "bags": [
///     { "id": 250041, "group": 5, "interactionItem": 36, "type": 0, "showType": 0 },
///     { "id": 250042, "group": 5, "interactionItem": 37 }
///   ]
/// }
/// </code>
/// <list type="bullet">
/// <item>bags[].id：装饰品 id，必须在 250000–259999（客户端按 id / 10000 = 25 找表）；group：所属套装，0 = 不属于套装；
/// interactionItem：摆出来的 config_interaction_item，必须是客户端配置里已有的行（海报 type 1 可以为 0）；
/// type：0 摆件 / 1 海报（海报的 group 必须为 0）；showType：0 在背包「装飾」页显示、1 不显示。缺省的整数字段按 0。</item>
/// <item>groups[].id：套装 id；bags：套装里的装饰品，必须都在同一文件的 bags 里声明、且它们的 group 等于这个套装。
/// 文件里 group 非 0 的装饰品也必须出现在对应套装的 bags 里。</item>
/// </list>
/// 结构错误（id 越界、重复、套装与装饰品对不上）时整份文件被忽略；与客户端配置或先加载的 Mod 撞号、引用了客户端配置里
/// 没有的 interactionItem 的装饰品被跳过，套装里只要有一件被跳过（或套装 id 撞号）整套连同其装饰品一起跳过。
/// Mod 发现规则见 <see cref="ModDeclarationScanner"/>（要求 bootstrap.lua 的 entries 列出该 Mod）。
/// </summary>
internal static class DecorModLoader
{
    internal const string FileName = "decorations.json";

    internal const int MinBagId = DecorCatalog.TableIndex * 10_000;
    internal const int MaxBagId = MinBagId + 9_999;

    private sealed record DecorationsFile(IReadOnlyList<GroupEntry?>? Groups, IReadOnlyList<BagEntry?>? Bags);

    private sealed record GroupEntry(int Id, IReadOnlyList<int>? Bags);

    private sealed record BagEntry(int Id, int Group = 0, int InteractionItem = 0, int Type = 0, int ShowType = 0);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>在 <paramref name="config"/>（只含客户端配置）上并入 <paramref name="modsRoot"/> 下所有可用的声明。</summary>
    internal static DecorCatalog Load(string modsRoot, string clientId, DecorCatalog config, ILogger logger)
    {
        IReadOnlyList<ModDeclarationFile> files =
            ModDeclarationScanner.Find(modsRoot, FileName, clientId, logger, requireClientEntry: true);
        DecorCatalog catalog = config;
        foreach (ModDeclarationFile file in files)
        {
            try
            {
                DecorationsFile parsed = JsonSerializer.Deserialize<DecorationsFile>(File.ReadAllText(file.Path), JsonOptions)
                    ?? throw new InvalidDataException($"{FileName} is empty");
                catalog = Merge(catalog, parsed, file.ModId, logger);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                logger.LogWarning("decor mod file ignored {Path}: {Reason}", file.Path, ex.Message);
            }
        }
        return catalog;
    }

    private static DecorCatalog Merge(DecorCatalog catalog, DecorationsFile file, string modId, ILogger logger)
    {
        if (file.Groups?.Any(group => group is null) == true || file.Bags?.Any(bag => bag is null) == true)
            throw new InvalidDataException("a group or bag entry is null");
        List<GroupEntry> groups = file.Groups?.Select(group => group!).ToList() ?? [];
        List<BagEntry> bags = file.Bags?.Select(bag => bag!).ToList() ?? [];
        Validate(groups, bags);

        // 撞号与引用检查：被跳过的装饰品让它所在的整套被跳过。
        var rejectedBags = new HashSet<int>();
        foreach (BagEntry bag in bags)
        {
            string? reason =
                catalog.Bags.ContainsKey(bag.Id)
                    ? catalog.Bags[bag.Id].Source == DecorCatalog.ConfigSource
                        ? "already exists in config_interaction_item_bag"
                        : $"is already declared by mod {catalog.Bags[bag.Id].Source}"
                    : bag.InteractionItem != 0 && !catalog.InteractionItemTypes.ContainsKey(bag.InteractionItem)
                        ? $"references interaction item {bag.InteractionItem}, which the client config_interaction_item lacks"
                        : null;
            if (reason is null) continue;
            rejectedBags.Add(bag.Id);
            logger.LogWarning("decor mod {ModId}: decoration {BagId} {Reason}; skipped", modId, bag.Id, reason);
        }
        var acceptedGroups = new List<DecorGroup>();
        foreach (GroupEntry group in groups)
        {
            string? reason =
                catalog.Groups.TryGetValue(group.Id, out DecorGroup? existing)
                    ? existing.Source == DecorCatalog.ConfigSource
                        ? "already exists in config_interaction_item_bag_group"
                        : $"is already declared by mod {existing.Source}"
                    : group.Bags!.FirstOrDefault(rejectedBags.Contains) is var bad and not 0
                        ? $"contains the skipped decoration {bad}"
                        : null;
            if (reason is null)
            {
                acceptedGroups.Add(new DecorGroup(group.Id, group.Bags!.ToList(), modId));
                continue;
            }
            rejectedBags.UnionWith(group.Bags!);
            logger.LogWarning("decor mod {ModId}: set {GroupId} {Reason}; the set and its decorations are skipped",
                modId, group.Id, reason);
        }
        List<DecorBag> acceptedBags = bags
            .Where(bag => !rejectedBags.Contains(bag.Id))
            .Select(bag => new DecorBag(bag.Id, bag.Type, bag.Group, bag.InteractionItem, bag.ShowType, modId))
            .ToList();
        logger.LogInformation("decor mod {ModId}: loaded {Bags} of {DeclaredBags} decorations and {Groups} of {DeclaredGroups} sets",
            modId, acceptedBags.Count, bags.Count, acceptedGroups.Count, groups.Count);
        return catalog.With(acceptedBags, acceptedGroups);
    }

    /// <summary>文件内部的结构检查；不通过时整份文件被忽略。</summary>
    private static void Validate(IReadOnlyList<GroupEntry> groups, IReadOnlyList<BagEntry> bags)
    {
        if (bags.Any(bag => bag.Id is < MinBagId or > MaxBagId))
            throw new InvalidDataException($"decoration ids must be in {MinBagId}-{MaxBagId}");
        if (bags.Select(bag => bag.Id).Distinct().Count() != bags.Count)
            throw new InvalidDataException("duplicate decoration ids");
        if (bags.Any(bag => bag.Type is not (DecorCatalog.BagTypeOther or DecorCatalog.BagTypePoster) ||
                            bag.ShowType is not (0 or 1) || bag.Group < 0 || bag.InteractionItem < 0))
            throw new InvalidDataException("decoration type and showType must be 0 or 1, group and interactionItem must not be negative");
        if (bags.Any(bag => bag.Type == DecorCatalog.BagTypeOther && bag.InteractionItem == 0))
            throw new InvalidDataException("a type-0 decoration needs an interactionItem");
        if (bags.Any(bag => bag.Type == DecorCatalog.BagTypePoster && bag.Group != 0))
            throw new InvalidDataException("posters (type 1) cannot belong to a set");
        if (groups.Any(group => group.Id <= 0))
            throw new InvalidDataException("set ids must be positive");
        if (groups.Select(group => group.Id).Distinct().Count() != groups.Count)
            throw new InvalidDataException("duplicate set ids");
        Dictionary<int, BagEntry> byId = bags.ToDictionary(bag => bag.Id);
        foreach (GroupEntry group in groups)
        {
            if (group.Bags is not { Count: > 0 } members)
                throw new InvalidDataException($"set {group.Id} lists no decorations");
            if (members.Distinct().Count() != members.Count)
                throw new InvalidDataException($"set {group.Id} lists a decoration twice");
            if (members.Any(id => !byId.TryGetValue(id, out BagEntry? bag) || bag.Group != group.Id))
                throw new InvalidDataException($"set {group.Id} lists a decoration that is not declared here with group {group.Id}");
        }
        var listed = groups.SelectMany(group => group.Bags!).ToHashSet();
        if (bags.Any(bag => bag.Group != 0 && !listed.Contains(bag.Id)))
            throw new InvalidDataException("a decoration names a set that does not list it");
    }
}
