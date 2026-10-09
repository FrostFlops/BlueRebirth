using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>修理模块：repair.RepairHero（花费金币回满血）。</summary>
internal sealed class RepairModule(RepairService repair, GameServices services) : IGameModule
{
    public IReadOnlyList<string> Prefixes => ["repair"];

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        ModuleResult result;
        switch (request.Method)
        {
            case "repair.RepairHero":
                RepairService.RepairResult repaired = await repair.BuildRepairRetAsync(request, ctx.ProfileId, ctx.Ct);
                // 修理后推送船坞（HP 刷新）+ 用户信息（金币扣除），让客户端立即生效。
                var account = await ctx.GetAccountAsync();
                uint now = (uint)ctx.Now;
                if (repaired.Rejected)
                {
                    // 金币不足：不修理，重推货币与背包。
                    result = new ModuleResult
                    {
                        Ret = repaired.Ret,
                        Err = 1,
                        ErrMsg = repaired.Error,
                        PrePushes = [GameServices.BuildUpdateUserInfoPush(account, now), services.BuildBagPush(account, now)],
                    };
                    break;
                }
                var heroes = account.Dock.Heroes.Select(GameServices.ToHeroGrid).ToList();
                result = new ModuleResult
                {
                    Ret = repaired.Ret,
                    PrePushes =
                    [
                        TMessageCodec.EncodeResponse(new TResponse(
                            Method: "hero.UpdateHeroBagData",
                            Ret: PlayerDataCodec.Encode(new HeroBag(heroes, account.Dock.BagSize)),
                            Time: now)),
                        await services.BuildUpdateUserInfoPushAsync(ctx.ProfileId, now, ctx.Ct),
                    ],
                };
                break;
            default:
                result = ModuleResult.Empty;
                break;
        }
        return result;
    }
}