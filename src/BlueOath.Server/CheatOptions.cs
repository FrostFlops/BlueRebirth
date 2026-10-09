namespace BlueOath.Server;

/// <summary>
/// 启动器「作弊选项（跳过时间）」：每一类跳过对应功能按真实时间的消耗，默认全关。
/// 由不带值的命令行开关 --cheat-production / --cheat-strength / --cheat-vow / --cheat-mood 开启，
/// 经 GameServices 写进 SettlementRules / VowRules，各纯函数据此分支。
/// <para>
/// 关闭开关后从当时的存档状态起按真实时间继续结算。开启前建议备份 --data 目录下的 profiles.db
/// （启动器与 run-game.bat 默认 runtime\jp\profiles.db）。
/// </para>
/// </summary>
/// <param name="Production">生产：道具工厂下单即完成，资源楼始终满仓。</param>
/// <param name="Strength">体力：工人体力（货币 21）不消耗并保持上限。</param>
/// <param name="Vow">许愿墙：祈愿后无冷却。</param>
/// <param name="Mood">心情：基建工作与体力加速不消耗心情（自然、宿舍、浴场回复照常）。</param>
/// <param name="RealResourceCost">
/// 按原规则消耗资源（--real-resource-cost）：探索（建造）扣推薦状等消耗、商店购买按价格扣货币与道具、
/// 出击扣燃料（共闘单人扣 RP）。默认关，即沿用离线版的免费规则。
/// </param>
/// <param name="RealShopStock">
/// 按原规则的商店库存（--real-shop-stock）：随机商店抽取陈列、购买扣库存、定时与手动刷新。默认关，即列出全部商品且不限量。
/// </param>
/// <param name="Materials">
/// 无限道具（--cheat-materials）：每次加载档案把戒指商店的价格道具 17553 与基地建材补满，基地建造/升级不扣建材。
/// 默认关：只给从未有过这些道具的档案发一次，之后不再补，建造/升级按 config_buildinglevelup 扣建材。
/// </param>
/// <param name="Medals">
/// 探索勋章（--cheat-medals）：探索每抽固定附赠 100 个精鋭戦姫勲章（离线版原来的规则）。
/// 默认关：按日服规则，每抽到一艘舰娘按 config_ship_main.extract_reward 发放（SSR 25、SR 5，其余没有）。
/// </param>
internal sealed record CheatOptions(
    bool Production = false,
    bool Strength = false,
    bool Vow = false,
    bool Mood = false,
    bool RealResourceCost = false,
    bool RealShopStock = false,
    bool Materials = false,
    bool Medals = false)
{
    public static CheatOptions None { get; } = new();

    /// <summary>是否开启了任一作弊（四项跳过时间、无限道具与探索勋章；不含两项按原规则的选项）。</summary>
    public bool Any => Production || Strength || Vow || Mood || Materials || Medals;

    /// <summary>全部选项都是默认值。</summary>
    public bool IsDefault => this == None;
}
