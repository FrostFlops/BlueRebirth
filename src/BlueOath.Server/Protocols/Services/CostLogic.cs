using BlueOath.Core;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>一项消耗：GoodsType（5 = 货币，其余按背包道具扣）、配置 id、数量。</summary>
internal readonly record struct CostItem(int Type, int Id, long Num);

/// <summary>扣资源的结果。<see cref="Ok"/> 为 false 时 <see cref="Account"/> 是原账号，<see cref="Shortfall"/> 说明缺什么。</summary>
internal sealed record PaymentResult(bool Ok, PlayerAccount Account, bool CurrencyChanged, bool BagChanged, string Shortfall = "");

/// <summary>
/// 「真实消耗资源」选项（--real-resource-cost）使用的扣费纯函数：同类项合并后全部够才扣，任一项不足时一项都不扣。
/// 货币（GoodsType 5）走 TryGetCurrency / AddCurrency，其它类型从背包扣（数量扣到 0 的条目由背包推送当作删除下发）。
/// </summary>
internal static class CostLogic
{
    internal static PaymentResult TryPay(PlayerAccount account, IEnumerable<CostItem> costs)
    {
        List<CostItem> merged = costs
            .Where(cost => cost.Num > 0)
            .GroupBy(cost => (cost.Type, cost.Id))
            .Select(group => new CostItem(group.Key.Type, group.Key.Id, group.Sum(cost => cost.Num)))
            .ToList();
        foreach (CostItem cost in merged)
        {
            long have = cost.Type == GameServices.GoodsTypeCurrency
                ? GameServices.TryGetCurrency(account, cost.Id, out int value) ? value : 0
                : account.Bag?.Items.Where(item => item.TemplateId == cost.Id).Sum(item => (long)item.Num) ?? 0;
            if (have < cost.Num)
                return new PaymentResult(false, account, false, false, $"{cost.Type}:{cost.Id} {have}/{cost.Num}");
        }
        PlayerAccount after = account;
        bool currency = false, bag = false;
        foreach (CostItem cost in merged)
        {
            if (cost.Type == GameServices.GoodsTypeCurrency)
            {
                after = GameServices.AddCurrency(after, cost.Id, checked((int)-cost.Num));
                currency = true;
            }
            else
            {
                after = RemoveFromBag(after, cost.Id, cost.Num);
                bag = true;
            }
        }
        return new PaymentResult(true, after, currency, bag);
    }

    /// <summary>从背包扣道具；同一模板有多条时按顺序逐条扣（余额检查用的是各条之和，只扣第一条可能扣成负数）。</summary>
    private static PlayerAccount RemoveFromBag(PlayerAccount account, int templateId, long num)
    {
        if (account.Bag is not { } bagData) return account;
        List<BagItem> items = bagData.Items.ToList();
        for (int i = 0; i < items.Count && num > 0; i++)
        {
            if (items[i].TemplateId != templateId || items[i].Num <= 0) continue;
            int take = (int)Math.Min(num, items[i].Num);
            items[i] = items[i] with { Num = items[i].Num - take };
            num -= take;
        }
        return account with { Bag = bagData with { Items = items } };
    }
}

/// <summary>商店商品价格（与日服客户端 ShopLogic 一致）。</summary>
internal static class ShopPricing
{
    /// <summary>
    /// 日服 ShopLogic:GetPriceByNum：价格表按「已购次数」分档，第 n 次（从 0 起）购买用第 min(n, 末档) 档，
    /// 买 buyNum 件就把对应的 buyNum 档加起来。空价格表视为免费。
    /// </summary>
    internal static long PriceByNum(IReadOnlyList<long>? tiers, int purchased, int buyNum)
    {
        if (tiers is not { Count: > 0 } || buyNum <= 0) return 0;
        long total = 0;
        int last = tiers.Count - 1;
        for (int k = 0; k < buyNum; k++)
            total += tiers[Math.Min(Math.Max(0, purchased) + k, last)];
        return total;
    }

    /// <summary>
    /// 一次购买要扣的资源：currency 每行是 [GoodsType, id]，price 同下标是该行的价格分档。
    /// cur_relation == 2 时只按客户端选中的那一种货币付（请求的 PriceIndex，0 起），否则每行都要付。
    /// 没有货币或价格全为 0 的商品免费。
    /// </summary>
    internal static IReadOnlyList<CostItem> Costs(ConfigShopGoods good, int priceIndex, int purchased, int buyNum)
    {
        if (good.Currency is not { Count: > 0 } currency || good.Price is not { Count: > 0 } price) return [];
        IEnumerable<int> rows = good.CurRelation == 2
            ? [Math.Clamp(priceIndex, 0, currency.Count - 1)]
            : Enumerable.Range(0, currency.Count);
        var costs = new List<CostItem>();
        foreach (int row in rows)
        {
            if (currency[row] is not { Count: >= 2 } kind || row >= price.Count) continue;
            long amount = PriceByNum(price[row], purchased, buyNum);
            if (amount > 0) costs.Add(new CostItem(checked((int)kind[0]), checked((int)kind[1]), amount));
        }
        return costs;
    }
}
