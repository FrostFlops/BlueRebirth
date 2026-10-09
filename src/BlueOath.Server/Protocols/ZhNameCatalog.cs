using System.Text.Json;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 道具与货币的简体中文名（GM 存档编辑页在日服名称后的括号里显示）。内嵌资源 item-names-zh.json 由
/// tools/export-zh-names.py 生成：国服客户端配置里同 id 有名称的取官方译名，日服独有的取
/// tools/zh-names-extra.json 里按日文名的翻译；与日文名相同的不收录。资源缺失或损坏时为空表。
/// </summary>
internal static class ZhNameCatalog
{
    private const string ResourceName = "BlueOath.Server.item-names-zh.json";

    private static readonly Lazy<(Dictionary<int, string> Items, Dictionary<int, string> Currencies)> Data = new(Load);

    /// <summary>背包道具模板 id → 中文名。</summary>
    public static IReadOnlyDictionary<int, string> Items => Data.Value.Items;

    /// <summary>货币 id → 中文名。</summary>
    public static IReadOnlyDictionary<int, string> Currencies => Data.Value.Currencies;

    private static (Dictionary<int, string>, Dictionary<int, string>) Load()
    {
        try
        {
            using Stream? stream = typeof(ZhNameCatalog).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return (new(), new());
            using JsonDocument doc = JsonDocument.Parse(stream);
            return (Section(doc.RootElement, "items"), Section(doc.RootElement, "currencies"));
        }
        catch (JsonException)
        {
            return (new(), new());
        }
    }

    private static Dictionary<int, string> Section(JsonElement root, string name)
    {
        var result = new Dictionary<int, string>();
        if (!root.TryGetProperty(name, out JsonElement section) || section.ValueKind != JsonValueKind.Object)
            return result;
        foreach (JsonProperty entry in section.EnumerateObject())
        {
            if (int.TryParse(entry.Name, out int id) && entry.Value.ValueKind == JsonValueKind.String &&
                entry.Value.GetString() is { Length: > 0 } text)
                result[id] = text;
        }
        return result;
    }
}
