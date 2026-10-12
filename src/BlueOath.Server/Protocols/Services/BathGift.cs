using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>一次浴场送礼抽到的强化效果：config_value_effect id、强度与是否「暴击」（金色强化）。</summary>
internal readonly record struct BathGiftRoll(int BuffId, int Power, bool IsCrit);

/// <summary>
/// 浴场送礼（bathroom.BathService）的纯函数：价格与强化效果抽取。
/// <para>
/// 客户端不含抽取逻辑，规则按配置与帮助文本 300010 推定：礼物类型在舰娘 config_ship_main.favorite_gift 中时，
/// 以 match_valueup_rate（万分比，日服 5000）概率抽到金色强化，否则以 notmatch_valueup_rate（2000）概率；
/// 金色强化从舰娘的 match_value_group 中均匀抽取（4 小时），否则从 not_match_value_group 中抽取（3 小时），
/// 舰娘的分组为空时退回礼物自身的分组。抽到金色强化时应答 IsCrit=1（客户端显示暴击提示）。
/// 新的强化效果替换旧的，不叠加。
/// </para>
/// </summary>
internal static class BathGift
{
    /// <summary>valueup_rate 的分母（万分比）。</summary>
    internal const int RateBase = 10_000;

    internal static bool IsFavorite(ConfigGift gift, ConfigShipMain? ship) =>
        ship?.FavoriteGift?.Contains(gift.GiftType) == true;

    /// <summary>价格 = [5（货币）, 货币 id, 数量]；日服 5 种礼物都是 50 温泉币（货币 13）。</summary>
    internal static bool TryGetPrice(ConfigGift gift, out int currency, out int amount)
    {
        currency = 0;
        amount = 0;
        if (gift.Price is not { Count: 3 } price || price[0] != 5 || price[2] <= 0) return false;
        currency = checked((int)price[1]);
        amount = checked((int)price[2]);
        return true;
    }

    /// <summary>
    /// 抽取强化效果。next(n) 返回 [0, n) 内均匀分布的整数（测试可注入）；effectExists 过滤配置里不存在的效果。
    /// 没有可用效果时返回 null。
    /// </summary>
    internal static BathGiftRoll? Roll(ConfigGift gift, ConfigShipMain? ship, Func<int, int> next, Func<int, bool> effectExists)
    {
        long rate = IsFavorite(gift, ship) ? gift.MatchValueupRate : gift.NotmatchValueupRate;
        bool crit = next(RateBase) < rate;
        List<long>? giftPool = crit ? gift.MatchValueGroup : gift.NotMatchValueGroup;
        List<long>? shipPool = crit ? ship?.MatchValueGroup : ship?.NotMatchValueGroup;
        bool fromShip = shipPool is { Count: > 0 };
        List<long> pool = ((fromShip ? shipPool : giftPool) ?? [])
            .Where(id => id > 0 && id <= int.MaxValue && effectExists((int)id))
            .ToList();
        if (pool.Count == 0) return null;
        int index = Math.Clamp(next(pool.Count), 0, pool.Count - 1);
        List<long>? powers = crit ? gift.MatchValuePower : gift.NotMatchValuePower;
        // 礼物自身分组与强度表一一对应；舰娘分组没有强度表，按 1（日服所有强度都是 1，帮助文本「强化 10%」）。
        int power = !fromShip && powers is { } list && index < list.Count && list[index] > 0
            ? checked((int)list[index])
            : 1;
        return new BathGiftRoll(checked((int)pool[index]), power, crit);
    }
}
