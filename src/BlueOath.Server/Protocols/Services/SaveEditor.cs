using BlueOath.Core;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 存档编辑（GM 控制台 /save 页面与 set_currency / set_item 命令）：查看并直接设置货币与背包道具的数量，
/// 主要用来丢掉作弊刷出的多余资源。纯函数，加锁、落盘与推送由 <see cref="GmCommandHandler"/> 负责。
/// <para>
/// 只改 UserInfo 里的货币与背包，不碰按时间结算的数据：工人体力（货币 21）存在基地数据里、随时间回复，不在可编辑之列。
/// 设成 0 的道具保留一条 Num=0 的记录（背包推送据此让客户端删除），与消耗光的好感礼物一样，
/// 防止 GameServices.EnsureAffectionGifts 在下次加载档案时把「从未有过」的礼物补回来。
/// </para>
/// </summary>
internal static class SaveEditor
{
    /// <summary>一种货币；NameZh 是简体中文名（没有时为空串），页面显示在名称后的括号里。</summary>
    internal sealed record CurrencyRow(int Id, string Name, int Value, string NameZh = "");

    /// <summary>
    /// 背包里的一种道具；Kind 是它所在配置表的中文名（config_table_index.name，如「物品」「N选1宝箱」），
    /// NameZh 是简体中文名（没有时为空串）。
    /// </summary>
    internal sealed record ItemRow(
        int Id, string Name, int Num, string Kind, int Quality, string Description, string NameZh = "");

    internal sealed record Snapshot(
        string ProfileId, string PlayerName, IReadOnlyList<CurrencyRow> Currencies, IReadOnlyList<ItemRow> Items);

    /// <summary>编辑结果。Ok 为 false 时 Account 是原账号；CurrencyChanged / BagChanged 决定要补发哪些推送。</summary>
    internal sealed record EditResult(
        bool Ok, PlayerAccount Account, string Message, bool CurrencyChanged = false, bool BagChanged = false)
    {
        public bool Changed => CurrencyChanged || BagChanged;
    }

    /// <summary>可编辑的货币 id：UserInfo 里的持久货币（<see cref="GameServices.TryGetCurrency"/> 能读到的），不含工人体力。</summary>
    internal static IReadOnlyList<int> EditableCurrencyIds(PlayerAccount account) =>
        Enumerable.Range(1, 64)
            .Where(id => id != BuildingProduction.StrengthId && GameServices.TryGetCurrency(account, id, out _))
            .ToList();

    internal static Snapshot Build(
        string profileId, PlayerAccount account,
        IReadOnlyDictionary<int, string> currencyNames, IReadOnlyDictionary<int, ItemCatalogLoader.Entry> catalog,
        IReadOnlyDictionary<int, string>? currencyNamesZh = null)
    {
        List<CurrencyRow> currencies = EditableCurrencyIds(account)
            .Select(id =>
            {
                GameServices.TryGetCurrency(account, id, out int value);
                return new CurrencyRow(id, currencyNames.GetValueOrDefault(id) ?? $"货币 {id}", value,
                    currencyNamesZh?.GetValueOrDefault(id) ?? "");
            })
            .ToList();
        List<ItemRow> items = (account.Bag?.Items ?? [])
            .GroupBy(item => item.TemplateId)
            .Select(group =>
            {
                ItemCatalogLoader.Entry? info = catalog.GetValueOrDefault(group.Key);
                return new ItemRow(
                    group.Key,
                    info?.Name ?? $"道具 {group.Key}",
                    group.Sum(item => item.Num),
                    info?.Kind ?? "",
                    info?.Quality ?? 0,
                    info?.Description ?? "",
                    info?.NameZh ?? "");
            })
            .Where(row => row.Num > 0)
            .OrderBy(row => row.Id)
            .ToList();
        return new Snapshot(profileId, account.Character.Name, currencies, items);
    }

    /// <summary>把货币设成 <paramref name="value"/>（0 到 int.MaxValue）。</summary>
    internal static EditResult SetCurrency(PlayerAccount account, int currencyId, long value)
    {
        if (currencyId == BuildingProduction.StrengthId)
            return new EditResult(false, account, "工人体力随时间回复，不能在这里修改");
        if (!GameServices.TryGetCurrency(account, currencyId, out int current))
            return new EditResult(false, account, $"货币 {currencyId} 不存在或不可编辑");
        if (value < 0 || value > int.MaxValue)
            return new EditResult(false, account, "数量必须在 0 到 2147483647 之间");
        if (value == current)
            return new EditResult(true, account, "数量没有变化");
        PlayerAccount after = GameServices.AddCurrency(account, currencyId, checked((int)(value - current)));
        return new EditResult(true, after, $"货币 {currencyId}：{current} → {value}", CurrencyChanged: true);
    }

    /// <summary>
    /// 把背包道具设成 <paramref name="value"/> 个（0 即删除，保留一条 Num=0 的记录）。同一模板的多条合并成一条（位置取第一条），
    /// 不触发入库副作用（如扩容道具加容量）。<paramref name="knownItem"/>（在 <see cref="ItemCatalogLoader"/> 里）为 false 时
    /// 只允许减少或删除已有的道具。
    /// </summary>
    internal static EditResult SetItem(PlayerAccount account, int templateId, long value, bool knownItem)
    {
        if (templateId <= 0)
            return new EditResult(false, account, "道具 id 无效");
        if (value < 0 || value > int.MaxValue)
            return new EditResult(false, account, "数量必须在 0 到 2147483647 之间");
        PlayerBag bag = account.Bag ?? new PlayerBag([], 100);
        int current = bag.Items.Where(item => item.TemplateId == templateId).Sum(item => item.Num);
        if (value > current && !knownItem)
            return new EditResult(false, account, $"道具 {templateId} 不在道具配置里，只能减少或删除");
        if (value == current && bag.Items.Count(item => item.TemplateId == templateId) <= 1)
            return new EditResult(true, account, "数量没有变化");

        var items = new List<BagItem>(bag.Items.Count + 1);
        bool placed = false;
        foreach (BagItem item in bag.Items)
        {
            if (item.TemplateId != templateId)
            {
                items.Add(item);
            }
            else if (!placed)
            {
                placed = true;
                items.Add(item with { Num = (int)value });
            }
        }
        if (!placed && value > 0) items.Add(new BagItem(templateId, (int)value));
        PlayerAccount after = account with { Bag = bag with { Items = items } };
        return new EditResult(true, after, $"道具 {templateId}：{current} → {value}", BagChanged: true);
    }
}
