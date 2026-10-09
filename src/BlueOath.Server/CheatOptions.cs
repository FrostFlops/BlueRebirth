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
internal sealed record CheatOptions(bool Production = false, bool Strength = false, bool Vow = false, bool Mood = false)
{
    public static CheatOptions None { get; } = new();

    public bool Any => Production || Strength || Vow || Mood;
}
