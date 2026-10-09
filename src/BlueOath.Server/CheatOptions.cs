namespace BlueOath.Server;

/// <summary>
/// 启动器「作弊选项」与「资源与商店」两类开关。服务端、启动器与 run-game.bat 的默认值都是 <see cref="Default"/>：
/// 作弊全部开启（离线版原来的规则），两项按原规则的选项关闭。命令行用 --cheat-x / --real-x 开启、
/// --cheat-x=off 关闭，--no-cheats 关闭全部作弊（见 <see cref="ServerOptions.Parse"/>）。
/// 经 GameServices 写进 SettlementRules / VowRules 或由各服务直接读取 <see cref="GameServices.Cheats"/>。
/// <para>
/// 作弊关闭时按原游戏规则（真实时间、配置里的数量）；关闭开关后从当时的存档状态起继续按规则结算。
/// 切换前建议备份 --data 目录下的 profiles.db（启动器与 run-game.bat 默认 runtime\jp\profiles.db）。
/// 记录各参数的默认值是 false，<see cref="None"/> 即「全部按原游戏规则」，供测试与代码里显式构造。
/// </para>
/// </summary>
/// <param name="Production">生产（--cheat-production）：道具工厂下单即完成，资源楼始终满仓。</param>
/// <param name="Strength">体力（--cheat-strength）：工人体力（货币 21）不消耗并保持上限（加速、合成、建造都不扣）。</param>
/// <param name="Vow">许愿墙（--cheat-vow）：祈愿后无冷却。</param>
/// <param name="Mood">心情（--cheat-mood）：基建工作、体力加速与前哨驻守不消耗心情（自然、宿舍、浴场回复照常）。</param>
/// <param name="RealResourceCost">
/// 按原规则消耗资源（--real-resource-cost）：探索（建造）扣推薦状等消耗、商店购买按价格扣货币与道具、
/// 出击扣燃料（共闘单人扣 RP）等。默认关，即沿用离线版的免费规则。
/// </param>
/// <param name="RealShopStock">
/// 按原规则的商店库存（--real-shop-stock）：随机商店抽取陈列、购买扣库存、定时与手动刷新。默认关，即列出全部商品且不限量。
/// </param>
/// <param name="Medals">
/// 探索勋章（--cheat-medals）：探索每抽固定附赠 100 个精鋭戦姫勲章（离线版原来的规则）。
/// 关闭：按日服规则，每抽到一艘舰娘按 config_ship_main.extract_reward 发放（SSR 25、SR 5，其余没有）。
/// </param>
/// <param name="Drops">
/// 掉落加成（--cheat-drops）：config_drop_item 掉落池里可堆叠的资源与道具每项额外 +600～+2000
/// （离线版原来的加成，作用于战斗、扫荡、每日副本、宝箱与充值奖励）。关闭：按配置的数量。
/// </param>
/// <param name="Sweep">
/// 扫荡跳过时间（--cheat-sweep）：扫荡作战开始即完成；前哨不按时间产出、不扣驻守心情，加速不限次数、不消耗、
/// 立即产出（离线版原来的规则）。关闭：扫荡按真实时间，前哨按时间产出、每天共 2 次加速。
/// </param>
/// <param name="Battle">
/// 战斗数值（--cheat-battle）：出击时沿用离线版原来下发的舰娘属性（离线版原来的规则）：等级成长按
/// base + levelup × (等级 − 1) 算，每级约放大 100 倍（24 级 Z39 耐久 26136，详情页 1447），舰载机每格 100 架，
/// 不计强化、装备强化与好感加成，每场满耐久出击。
/// 关闭：按客户端详情页的公式（Protocols/Services/BattleStats.cs）下发属性与装备，出击带当前耐久。
/// </param>
internal sealed record CheatOptions(
    bool Production = false,
    bool Strength = false,
    bool Vow = false,
    bool Mood = false,
    bool Medals = false,
    bool Drops = false,
    bool Sweep = false,
    bool RealResourceCost = false,
    bool RealShopStock = false,
    bool Battle = false)
{
    /// <summary>全部按原游戏规则：作弊全关，按原规则的选项也关。</summary>
    public static CheatOptions None { get; } = new();

    /// <summary>服务端、启动器与脚本的默认值：作弊全开，按原规则的选项关闭。</summary>
    public static CheatOptions Default { get; } = AllCheats(true);

    /// <summary>是否开启了任一作弊（不含两项按原规则的选项）。</summary>
    public bool Any => Production || Strength || Vow || Mood || Medals || Drops || Sweep || Battle;

    /// <summary>与 <see cref="Default"/> 相同。</summary>
    public bool IsDefault => this == Default;

    /// <summary>把全部作弊设为 <paramref name="enabled"/>，两项按原规则的选项保持不变。</summary>
    public CheatOptions WithAllCheats(bool enabled) => this with
    {
        Production = enabled,
        Strength = enabled,
        Vow = enabled,
        Mood = enabled,
        Medals = enabled,
        Drops = enabled,
        Sweep = enabled,
        Battle = enabled,
    };

    private static CheatOptions AllCheats(bool enabled) => new CheatOptions().WithAllCheats(enabled);
}
