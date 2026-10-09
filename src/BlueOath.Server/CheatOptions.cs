namespace BlueOath.Server;

/// <summary>
/// 启动器「作弊选项」与「资源与商店」两类开关，默认全关。由不带值的命令行开关开启（--cheat-* / --real-*），
/// 经 GameServices 写进 SettlementRules / VowRules 或由各服务直接读取 <see cref="GameServices.Cheats"/>。
/// <para>
/// 作弊关闭时按原游戏规则（真实时间、配置里的数量）；关闭开关后从当时的存档状态起继续按规则结算。
/// 开启前建议备份 --data 目录下的 profiles.db（启动器与 run-game.bat 默认 runtime\jp\profiles.db）。
/// </para>
/// </summary>
/// <param name="Production">生产（--cheat-production）：道具工厂下单即完成，资源楼始终满仓。</param>
/// <param name="Strength">体力（--cheat-strength）：工人体力（货币 21）不消耗并保持上限（加速、合成、建造都不扣）。</param>
/// <param name="Vow">许愿墙（--cheat-vow）：祈愿后无冷却。</param>
/// <param name="Mood">心情（--cheat-mood）：基建工作、体力加速与前哨驻守不消耗心情（自然、宿舍、浴场回复照常）。</param>
/// <param name="RealResourceCost">
/// 按原规则消耗资源（--real-resource-cost）：探索（建造）扣推薦状等消耗、商店购买按价格扣货币与道具、
/// 出击扣燃料（共闘单人扣 RP）。默认关，即沿用离线版的免费规则。
/// </param>
/// <param name="RealShopStock">
/// 按原规则的商店库存（--real-shop-stock）：随机商店抽取陈列、购买扣库存、定时与手动刷新。默认关，即列出全部商品且不限量。
/// </param>
/// <param name="Medals">
/// 探索勋章（--cheat-medals）：探索每抽固定附赠 100 个精鋭戦姫勲章（离线版原来的规则）。
/// 默认关：按日服规则，每抽到一艘舰娘按 config_ship_main.extract_reward 发放（SSR 25、SR 5，其余没有）。
/// </param>
/// <param name="Drops">
/// 掉落加成（--cheat-drops）：config_drop_item 掉落池里可堆叠的资源与道具每项额外 +600～+2000
/// （离线版原来的加成，作用于战斗、扫荡、每日副本、宝箱与充值奖励）。默认关：按配置的数量。
/// </param>
/// <param name="Sweep">
/// 扫荡跳过时间（--cheat-sweep）：扫荡作战开始即完成；前哨不按时间产出、不扣驻守心情，加速不限次数、不消耗、
/// 立即产出（离线版原来的规则）。默认关：扫荡按真实时间，前哨按时间产出、每天共 2 次加速。
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
    bool RealShopStock = false)
{
    public static CheatOptions None { get; } = new();

    /// <summary>是否开启了任一作弊（不含两项按原规则的选项）。</summary>
    public bool Any => Production || Strength || Vow || Mood || Medals || Drops || Sweep;

    /// <summary>全部选项都是默认值。</summary>
    public bool IsDefault => this == None;
}
