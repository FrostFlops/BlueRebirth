using System.Text.Json;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 技能升级配置：config_pskill_dict_group 里升级要用的三列。
/// <see cref="Materials"/> 第 n 行（从 1 起）是 n 级升 n+1 级的主材料 [GoodsType, id, num]；
/// <see cref="MubMaterials"/> 同下标是附加材料列表（日服只有 17 个技能配置了，空表示没有）。
/// </summary>
internal sealed record PSkillUpgradeConfig(
    int MaxLevel,
    IReadOnlyList<IReadOnlyList<long>> Materials,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<long>>> MubMaterials);

/// <summary>按技能 id 读取 config_pskill_dict_group 的 max_level / upgrade_materials / upgrade_materials_mub。</summary>
internal static class PSkillUpgradeLoader
{
    private static readonly Dictionary<int, PSkillUpgradeConfig> _skills = new();
    private static bool _loaded;

    internal static void Load(string configDir)
    {
        if (_loaded) return;
        _skills.Clear();
        ConfigDbLoader.LoadRows(configDir, "config_pskill_dict_group.db", (id, _, json) =>
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                int maxLevel = root.TryGetProperty("max_level", out JsonElement max) && max.TryGetInt32(out int value)
                    ? value
                    : 0;
                _skills[id] = new PSkillUpgradeConfig(
                    maxLevel,
                    ReadRows(root, "upgrade_materials"),
                    root.TryGetProperty("upgrade_materials_mub", out JsonElement mub) && mub.ValueKind == JsonValueKind.Array
                        ? mub.EnumerateArray().Select(ReadRows).ToList()
                        : []);
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        });
        _loaded = true;
    }

    internal static PSkillUpgradeConfig? Get(int pskillId) => _skills.GetValueOrDefault(pskillId);

    private static IReadOnlyList<IReadOnlyList<long>> ReadRows(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement rows) ? ReadRows(rows) : [];

    private static IReadOnlyList<IReadOnlyList<long>> ReadRows(JsonElement rows) =>
        rows.ValueKind != JsonValueKind.Array
            ? []
            : rows.EnumerateArray()
                .Select(row => (IReadOnlyList<long>)(row.ValueKind == JsonValueKind.Array
                    ? row.EnumerateArray().Select(cell => cell.TryGetInt64(out long v) ? v : 0).ToList()
                    : []))
                .ToList();
}

/// <summary>
/// 修理折扣：config_affection_favor.affection_cost（万分制），客户端按好感档位取行。日服同一婚姻状态下所有档位的值相同
/// （未誓约 10000，誓约 9000），这里按 affection_marry 取该状态的第一行。
/// </summary>
internal static class RepairDiscountLoader
{
    private static readonly Dictionary<bool, long> _costs = new();
    private static bool _loaded;

    internal static void Load(string configDir)
    {
        if (_loaded) return;
        _costs.Clear();
        ConfigDbLoader.LoadRows(configDir, "config_affection_favor.db", (_, _, json) =>
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("affection_marry", out JsonElement marry) && marry.TryGetInt32(out int married) &&
                    root.TryGetProperty("affection_cost", out JsonElement cost) && cost.TryGetInt64(out long value))
                    _costs.TryAdd(married == 1, value);
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        });
        _loaded = true;
    }

    /// <summary>该婚姻状态的 affection_cost；缺配置时为 0（不打折）。</summary>
    internal static long AffectionCost(bool married) => _costs.GetValueOrDefault(married);
}

/// <summary>
/// 养成操作的消耗（技能升级、实验室天赋、前哨升级、修理；与「真实消耗资源」选项无关，总是扣），
/// 按日服 1.4.0 客户端发请求前的预检计算。纯函数，
/// 由调用方交给 <see cref="CostLogic.TryPay"/>。返回 null 表示客户端本就不会发这个请求（配置缺失、已满级）。
/// </summary>
internal static class UpgradeCosts
{
    /// <summary>
    /// hero.StudySkill：日服 ShipSkillLogic:SortSkillMaterial(level, skillId)。level 是客户端看到的当前等级
    /// （PSKillLevelMap[skillId] or 1）；材料取 upgrade_materials[min(level, #materials)]，
    /// 再加上 upgrade_materials_mub 同下标的全部附加材料。已满级（level ≥ max_level）或没有材料的技能客户端不让升级。
    /// </summary>
    internal static IReadOnlyList<CostItem>? SkillLevelUp(PSkillUpgradeConfig? config, int level)
    {
        if (config is null || config.Materials.Count == 0 || level < 1 || level >= config.MaxLevel) return null;
        int index = Math.Min(level, config.Materials.Count) - 1;
        var costs = new List<CostItem>();
        AddTriple(costs, config.Materials[index]);
        if (index < config.MubMaterials.Count)
            foreach (IReadOnlyList<long> extra in config.MubMaterials[index])
                AddTriple(costs, extra);
        return costs;
    }

    /// <summary>
    /// outpost.UpgradeBuilding：日服 MubarOutpostLogic:CheckLevelUpCondition 用当前等级那一行（level 0 是解锁）的
    /// item_cost，每项 [GoodsType, id, num]（金币是 [5, 1, n]）。满级行的 item_cost 为空。
    /// </summary>
    internal static IReadOnlyList<CostItem> OutpostUpgrade(ConfigOutpostLevel? current)
    {
        var costs = new List<CostItem>();
        foreach (List<long> row in current?.ItemCost ?? [])
            AddTriple(costs, row);
        return costs;
    }

    /// <summary>
    /// talentTree.UnLockTalent / UpgradeTalent：日服 TalentPage:CheckLvUpCost 检查的是请求里那个天赋
    /// （解锁发选中的天赋，升级发当前天赋的 nexttalent）的 config_talent.levelup，每项 [GoodsType, id, num]，
    /// 金币是 [5, 1, n]，其余是道具。
    /// </summary>
    internal static IReadOnlyList<CostItem> Talent(ConfigTalent? talent)
    {
        var costs = new List<CostItem>();
        foreach (List<long> row in talent?.Levelup ?? [])
            AddTriple(costs, row);
        return costs;
    }

    /// <summary>
    /// repair.RepairHero 一艘舰娘的金币：日服 RepaireLogic:CalculateNeedAllGold，
    /// ceil(fixed_money × affection_cost / 10000 × (1 − 当前血量比例))；affection_cost 为 0（缺配置）时不打折。
    /// 运算顺序与 Lua 相同（先整数乘，再浮点除），避免 ceil 在边界上差一。
    /// </summary>
    internal static long RepairGold(long fixedMoney, long affectionCost, double curHpPer)
    {
        double need = affectionCost > 0 ? fixedMoney * affectionCost / 10000.0 : fixedMoney;
        return (long)Math.Ceiling(need * (1 - curHpPer));
    }

    /// <summary>配置里的 [GoodsType, id, num] 三元组；不完整或数量非正的行跳过。</summary>
    internal static void AddTriple(List<CostItem> costs, IReadOnlyList<long> row)
    {
        if (row.Count >= 3 && row[0] > 0 && row[0] <= int.MaxValue && row[1] > 0 && row[1] <= int.MaxValue && row[2] > 0)
            costs.Add(new CostItem((int)row[0], (int)row[1], row[2]));
    }
}
