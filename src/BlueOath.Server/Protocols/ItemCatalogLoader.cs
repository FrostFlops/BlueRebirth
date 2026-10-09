using System.Text.Json;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 背包道具目录（GM 存档编辑用）：背包只存模板 id，客户端按 config_table_index 到各张表里找配置。
/// 这里把所有能进背包的表（物品、强化经验道具、N选1宝箱、浴室门票、支援令、祈愿道具、碎片、礼物、扩展道具、
/// 战姬经验道具、情人节礼物、主界面装饰、好感礼物等）的名称合并成一张表；同一 id 出现在多张表时取
/// <see cref="BagGoodsTypes"/> 中靠前的那张。没有名称的配置行不收录。
/// </summary>
internal static class ItemCatalogLoader
{
    /// <summary>一个道具：模板 id、名称、所在表的中文名（config_table_index.name）、GoodsType、品质、说明。</summary>
    internal sealed record Entry(int Id, string Name, string Kind, int GoodsType, int Quality, string Description);

    /// <summary>能进背包的 GoodsType（config_table_index 的 id），按查找优先级排列；装备、舰娘、货币、时装等不在背包里。</summary>
    internal static readonly int[] BagGoodsTypes = [1, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 23, 25, 28];

    private static Dictionary<int, Entry> _entries = new();
    private static bool _loaded;

    public static IReadOnlyDictionary<int, Entry> Entries => _entries;

    public static void Load(string configDir)
    {
        if (_loaded) return;
        var entries = new Dictionary<int, Entry>();
        try
        {
            Dictionary<int, ConfigTableIndex> index = ConfigDbLoader.LoadAll<ConfigTableIndex>(configDir, "config_table_index.db");
            foreach (int goodsType in BagGoodsTypes)
            {
                if (!index.TryGetValue(goodsType, out ConfigTableIndex? table) || string.IsNullOrEmpty(table.FileName))
                    continue;
                string kind = table.Name ?? table.FileName;
                ConfigDbLoader.LoadRows(configDir, table.FileName + ".db", (id, _, json) =>
                {
                    if (entries.ContainsKey(id)) return;
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;
                    string name = Text(root, "name");
                    if (name.Length == 0) return;
                    entries[id] = new Entry(id, name, kind, goodsType,
                        root.TryGetProperty("quality", out JsonElement quality) && quality.TryGetInt32(out int q) ? q : 0,
                        Text(root, "description"));
                });
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 配置缺失或损坏时目录为空，存档编辑仍可用（名称显示为「道具 id」）。
        }
        _entries = entries;
        _loaded = true;
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
