using BlueOath.Core;
using BlueOath.Protocol;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 指挥室装饰模块：interactionitem.*。
/// <list type="bullet">
/// <item>每个请求先在账号锁内按经过时间结算并落盘，结算结果随应答推送（心情 / 建筑在应答前，浴场 / 祈愿 / 商店在应答后），业务失败也推。</item>
/// <item>客户端的应答处理器只看 err（失败时提示 330001），装饰状态只从 interactionitem.RefreshInteractionItems 推送读取，
/// 所以切换套装、显示/收起摆件、挂海报、摆水晶球成功后都在应答之前推送变化的部分（<see cref="DecorLogic"/>）。</item>
/// <item>GetItemReward 一律返回错误：可点击的装饰摆件（interaction_item_type 1）没有配置奖励，成功应答会让客户端在
/// interactionitemlogic.lua 的 GetClickEventReward 里取 config_rewards(0) 报错；南瓜、儿童节礼物与漂流瓶都是已关闭的活动。</item>
/// <item>BuyChristmasFurniture / GetSpringPaperFlowerReward：圣诞与春节家具活动已关闭，返回错误。</item>
/// </list>
/// </summary>
internal sealed class InteractionItemModule(GameServices services) : IGameModule
{
    public IReadOnlyList<string> Prefixes => ["interactionitem"];

    /// <summary>测试注入的装饰目录；为 null 时用服务端载入的 <see cref="DecorCatalogLoader.Catalog"/>。</summary>
    internal DecorCatalog? CatalogOverride { get; init; }

    private DecorCatalog Catalog => CatalogOverride ?? DecorCatalogLoader.Catalog;

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        using var accountLock = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
        PlayerAccount account = settled.Account;
        byte[] args = request.Args ?? [];
        DecorLogic.SetResult result;
        try
        {
            result = request.Method switch
            {
                "interactionitem.SetMutexBagGroupState" => SetTheme(account, PlayerDataCodec.DecodeDecorateMutexBagGroupArg(args)),
                "interactionitem.SetBagItemVisible" => SetVisible(account, PlayerDataCodec.DecodeInteractionItemArg(args)),
                "interactionitem.SetPosterState" => SetPoster(account, PlayerDataCodec.DecodePosterStateArg(args)),
                "interactionitem.SetCrystalBallToy" => SetBallToy(account, PlayerDataCodec.DecodeInteractionBallToyArg(args)),
                "interactionitem.GetItemReward" => GetItemReward(account, PlayerDataCodec.DecodeInteractionItemArg(args)),
                "interactionitem.BuyChristmasFurniture" or "interactionitem.GetSpringPaperFlowerReward" =>
                    new DecorLogic.SetResult(account, "the furniture events are closed"),
                // RefreshInteractionItems 是服务端推送；客户端若主动请求，就应答当前完整状态。
                DecorLogic.RefreshMethod => new DecorLogic.SetResult(account, null, DecorLogic.Snapshot(account, Catalog)),
                _ => new DecorLogic.SetResult(account, null),
            };
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or OverflowException)
        {
            result = new DecorLogic.SetResult(account, $"malformed arguments: {ex.Message}");
        }

        if (result.Changed) await services.SaveAccountAsync(result.Account, ctx.Ct);
        uint pushTime = checked((uint)ctx.Now);
        var pre = new List<byte[]>();
        byte[] ret = [];
        if (result.Error is null && result.Push is { } push)
        {
            if (request.Method == DecorLogic.RefreshMethod) ret = PlayerDataCodec.Encode(push);
            else pre.Add(DecorLogic.BuildPush(push, pushTime));
        }
        if (result.Error is not null)
            services.FileLogger.LogInformation("{Method} rejected: {Error}", request.Method, result.Error);
        else if (result.Changed)
            services.FileLogger.LogInformation("{Method} saved: {Push}", request.Method, Describe(result.Push));
        pre.AddRange(GameServices.BuildMoodSyncPushes(
            result.Account, settled.ChangedHeroIds, settled.BuildingChanged, pushTime));
        return new ModuleResult
        {
            Ret = ret,
            Err = result.Error is null ? 0 : 1,
            ErrMsg = result.Error ?? "",
            PrePushes = pre,
            PostPushes = GameServices.BuildSettlementPostPushes(settled, pushTime, current: result.Account),
        };
    }

    private DecorLogic.SetResult SetTheme(PlayerAccount account, DecorateMutexBagGroupArg arg) =>
        DecorLogic.SetTheme(account, Catalog, arg.GroupType, arg.SelectId);

    private DecorLogic.SetResult SetVisible(PlayerAccount account, InteractionItemArg arg) =>
        DecorLogic.SetVisible(account, Catalog, arg.InteractionItem, arg.VisibleState);

    private DecorLogic.SetResult SetPoster(PlayerAccount account, PosterStateArg arg) =>
        DecorLogic.SetPoster(account, Catalog, arg.Point, arg.PosterId);

    private DecorLogic.SetResult SetBallToy(PlayerAccount account, InteractionBallToyArg arg) =>
        DecorLogic.SetBallToy(account, Catalog, arg.BallId, arg.ToyId);

    private DecorLogic.SetResult GetItemReward(PlayerAccount account, InteractionItemArg arg) =>
        new(account, Catalog.InteractionItemTypes.TryGetValue(arg.InteractionItem, out int type)
            ? $"interaction item {arg.InteractionItem} (type {type}) has no offline reward"
            : $"interaction item {arg.InteractionItem} is not in the client config");

    private static string Describe(InteractionItemRet? push) => push is null
        ? ""
        : string.Join(" ",
            (push.Decorate ?? []).Select(group => $"set[{group.TypeId}]={group.CurSelect}")
            .Concat((push.BagItems ?? []).Select(item => $"item {item.Id} state={item.State}"))
            .Concat((push.Posters ?? []).Select(poster => $"poster[{poster.Point}]={poster.PosterId}"))
            .Concat((push.BallToys ?? []).Select(toy => $"ball[{toy.BallId}]={toy.ToyId}")));
}
