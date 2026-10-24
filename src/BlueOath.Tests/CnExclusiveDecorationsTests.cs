using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueOath.Server.Protocols;

/// <summary>
/// 仓库随附的 cn-exclusive.mod 的春节家具套装（spring_furniture）。客户端 data.lua 往 config_interaction_item_bag /
/// config_interaction_item_bag_group 注入的行与服务端读的 decorations.json 出自 tools/export-cn-exclusive.py 的同一次生成：
/// 两边的装饰品、套装与字段必须一致，并符合服务端装饰声明的规则（id 段、不与客户端配置撞号、只引用客户端已有的
/// config_interaction_item、套装与装饰品互相对应）；注入行带齐客户端表的全部键。只核对文件与客户端配置，不依赖服务端的
/// 装饰目录加载器。由 Program.cs 注册。
/// </summary>
internal static class CnExclusiveDecorationsTests
{
    private const string BagTable = "config_interaction_item_bag";
    private const string SetTable = "config_interaction_item_bag_group";
    private const string ItemTable = "config_interaction_item";
    private const string Feature = "spring_furniture";

    // 国服春节家具套装：套装 5、装饰品 250041–250049，摆出来的是日服已有的 config_interaction_item 36–44
    // （国服把同样的物件编为 181236–181244）。日服的套装是 1 和 3。
    private const int SpringSet = 5;
    private static readonly int[] SpringPieces = [.. Enumerable.Range(250041, 9)];
    private static readonly int[] SpringItems = [.. Enumerable.Range(36, 9)];

    // 客户端按 id / 10000 = 25 找装饰品的表；InteractionItemType.Furniture = 2（主页摆出、点击无反应）。
    private const int MinBagId = 250_000, MaxBagId = 259_999, FurnitureItemType = 2;
    private static readonly string[] ModelFields = ["item_name", "item_name_dawn", "item_name_night"];

    private static void Assert(bool condition, string message) => TestSupport.Assert(condition, message);

    private sealed record Insert(string Feature, int Id, Dictionary<string, string> Fields);

    public static Task ShippedTest()
    {
        string modDir = Path.Combine(TestSupport.FindRepositoryRoot(), "Mods", "cn-exclusive.mod");
        string data = File.ReadAllText(Path.Combine(modDir, "data.lua"));
        List<Insert> bags = Inserts(data, BagTable).OrderBy(x => x.Id).ToList();
        List<Insert> sets = Inserts(data, SetTable);
        Assert(bags.Select(x => x.Id).SequenceEqual(SpringPieces) && sets.Select(x => x.Id).SequenceEqual([SpringSet]) &&
               bags.Concat(sets).All(x => x.Feature == Feature),
            $"data.lua does not insert exactly the spring pieces 250041-250049 and set 5 under {Feature}: " +
            string.Join(",", bags.Concat(sets).Select(x => $"{x.Id}@{x.Feature}")));
        string main = File.ReadAllText(Path.Combine(modDir, "main.lua"));
        Assert(Regex.IsMatch(main, @"^\s*spring_furniture = true,", RegexOptions.Multiline),
            "main.lua SETTINGS has no spring_furniture switch that defaults to true");

        // 与客户端配置核对（BLUEOATH_CLIENT_PATH 可指向未热更的 1.4.0 客户端，两者结论相同）。
        string configDir = ConfigDbLoader.BuildConfigDir(TestSupport.FindClientPath());
        Dictionary<int, JsonElement> clientBags = Rows(configDir, BagTable);
        Dictionary<int, JsonElement> clientSets = Rows(configDir, SetTable);
        Dictionary<int, JsonElement> clientItems = Rows(configDir, ItemTable);
        HashSet<int> rewards = [.. Rows(configDir, "config_rewards").Keys];
        Assert(clientBags.Count > 0 && clientSets.Count > 0 && clientItems.Count > 0 && rewards.Count > 0,
            $"the client config in {configDir} could not be read");
        HashSet<string> bagKeys = KeyUnion(clientBags.Values), setKeys = KeyUnion(clientSets.Values);

        foreach ((Insert bag, int itemId) in bags.Zip(SpringItems))
        {
            Dictionary<string, string> row = bag.Fields;
            // GetMultiDataByKey 遇到缺键的行会对整张表返回 nil，所以注入行要带齐客户端表的全部键。
            Assert(row.Keys.ToHashSet().SetEquals(bagKeys),
                $"data.lua {BagTable} {bag.Id} does not carry exactly the client keys: " +
                string.Join(",", row.Keys.Except(bagKeys).Concat(bagKeys.Except(row.Keys))));
            Assert(!clientBags.ContainsKey(bag.Id), $"{BagTable} {bag.Id} collides with the client config");
            Assert(Int(row, "id") == bag.Id && Int(row, "interactionitem_bag_group") == SpringSet && Int(row, "type") == 0 &&
                   Int(row, "show_type") == 0 && rewards.Contains(Int(row, "reward")),
                $"data.lua {BagTable} {bag.Id}: id, set, type, show_type or reward is wrong");
            Assert(Int(row, "interactionitem") == itemId && clientItems.TryGetValue(itemId, out JsonElement item) &&
                   item.GetProperty("interaction_item_type").GetInt32() == FurnitureItemType &&
                   ModelFields.All(field => item.GetProperty(field).GetString() is { Length: > 0 } model && model == Str(row, field)),
                $"data.lua {BagTable} {bag.Id} does not point at the client furniture item {itemId} with the same models");
            // 国服的 *_124 图标日服没有：icon / icon_small 用这件家具的 picture，也就是客户端配置给同一物件的图。
            string picture = clientItems[itemId].GetProperty("interaction_item_pic").GetString() ?? "";
            Assert(picture.Length > 0 && Str(row, "picture") == picture && Str(row, "icon") == picture && Str(row, "icon_small") == picture,
                $"data.lua {BagTable} {bag.Id}: icon, icon_small and picture are not the client item picture {picture}");
        }
        Insert set = sets.Single();
        Assert(set.Fields.Keys.ToHashSet().SetEquals(setKeys) && !clientSets.ContainsKey(SpringSet) && Int(set.Fields, "id") == SpringSet,
            $"data.lua {SetTable} {SpringSet} collides with the client config or does not carry exactly its keys");
        int[] members = IntList(set.Fields, "interactionitem_bag");
        Assert(members.Order().SequenceEqual(SpringPieces) && members.Distinct().Count() == members.Length,
            $"data.lua set {SpringSet} does not list each of the pieces once: {string.Join(",", members)}");
        // 国服封面 uipic_ui_lunarnewyear_cover 日服也没有，用套装里一件家具的图代替。
        Assert(bags.Any(bag => Str(bag.Fields, "picture") == Str(set.Fields, "icon")),
            $"the set cover {Str(set.Fields, "icon")} is not the picture of one of its pieces");

        // decorations.json：服务端读的声明，与 data.lua 的行逐项一致。
        using JsonDocument declaration = JsonDocument.Parse(File.ReadAllText(Path.Combine(modDir, "decorations.json")));
        JsonElement root = declaration.RootElement;
        List<JsonElement> declaredBags = [.. root.GetProperty("bags").EnumerateArray()];
        List<JsonElement> declaredSets = [.. root.GetProperty("groups").EnumerateArray()];
        Assert(root.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(["bags", "groups"]),
            "decorations.json has properties other than groups and bags");
        Assert(declaredBags.Select(x => x.GetProperty("id").GetInt32()).SequenceEqual(SpringPieces),
            "decorations.json does not declare exactly the pieces data.lua inserts, in id order");
        foreach ((JsonElement declared, Insert bag) in declaredBags.Zip(bags))
        {
            int id = declared.GetProperty("id").GetInt32();
            Assert(id is >= MinBagId and <= MaxBagId &&
                   declared.GetProperty("group").GetInt32() == Int(bag.Fields, "interactionitem_bag_group") &&
                   declared.GetProperty("interactionItem").GetInt32() == Int(bag.Fields, "interactionitem") &&
                   declared.GetProperty("type").GetInt32() == Int(bag.Fields, "type") &&
                   declared.GetProperty("showType").GetInt32() == Int(bag.Fields, "show_type") &&
                   clientItems.ContainsKey(declared.GetProperty("interactionItem").GetInt32()) &&
                   declared.EnumerateObject().Count() == 5,
                $"decorations.json bag {id} does not match the data.lua row {bag.Id}");
        }
        Assert(declaredSets.Count == 1 && declaredSets[0].GetProperty("id").GetInt32() == SpringSet &&
               declaredSets[0].GetProperty("bags").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(members) &&
               declaredSets[0].EnumerateObject().Count() == 2,
            "decorations.json does not declare set 5 with the pieces of the data.lua row, in the same order");
        return Task.CompletedTask;
    }

    /// <summary>data.lua 的 inserts 里指定表的条目：feature、id 与 row 的顶层字段（值是 Lua 源码文本）。</summary>
    private static List<Insert> Inserts(string data, string table)
    {
        var result = new List<Insert>();
        foreach (Match match in Regex.Matches(data,
                     @"\{\s*feature = ""(?<feature>\w+)"",\s*table = ""(?<table>\w+)"",\s*id = (?<id>\d+),\s*row = \{"))
        {
            if (match.Groups["table"].Value != table) continue;
            int start = match.Index + match.Length - 1;
            result.Add(new Insert(match.Groups["feature"].Value, int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture),
                TopLevelFields(data[start..(TableEnd(data, start) + 1)])));
        }
        return result;
    }

    /// <summary>从 start 处的 '{' 起，返回与它配对的 '}' 的位置（跳过字符串）。</summary>
    private static int TableEnd(string text, int start)
    {
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"':
                    for (i++; i < text.Length && text[i] != '"'; i++)
                        if (text[i] == '\\') i++;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    if (--depth == 0) return i;
                    break;
            }
        }
        throw new InvalidDataException("unbalanced table in data.lua");
    }

    /// <summary>Lua 表构造式的顶层 key = value 项（data.lua 只用这种写法），值为源码文本。</summary>
    private static Dictionary<string, string> TopLevelFields(string table)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        int depth = 0, entryStart = 1;
        for (int i = 0; i < table.Length; i++)
        {
            char c = table[i];
            if (c == '"')
            {
                for (i++; i < table.Length && table[i] != '"'; i++)
                    if (table[i] == '\\') i++;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' || (c == ',' && depth == 1))
            {
                if (depth == 1)
                {
                    Match entry = Regex.Match(table[entryStart..i], @"^\s*([A-Za-z_]\w*) = (.*\S)\s*$", RegexOptions.Singleline);
                    if (entry.Success) fields.Add(entry.Groups[1].Value, entry.Groups[2].Value);
                    entryStart = i + 1;
                }
                if (c == '}') depth--;
            }
        }
        return fields;
    }

    private static int Int(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number)
            ? number
            : int.MinValue;

    private static string? Str(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : null;

    private static int[] IntList(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && value.StartsWith('{') && value.EndsWith('}')
            ? [.. value[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(item => int.Parse(item, CultureInfo.InvariantCulture))]
            : [];

    private static Dictionary<int, JsonElement> Rows(string configDir, string table)
    {
        var rows = new Dictionary<int, JsonElement>();
        ConfigDbLoader.LoadRows(configDir, table + ".db", (id, _, json) =>
        {
            using JsonDocument document = JsonDocument.Parse(json);
            rows[id] = document.RootElement.Clone();
        });
        return rows;
    }

    private static HashSet<string> KeyUnion(IEnumerable<JsonElement> rows) =>
        [.. rows.SelectMany(row => row.EnumerateObject().Select(property => property.Name))];
}
