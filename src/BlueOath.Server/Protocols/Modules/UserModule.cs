using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>玩家模块：user.*（登录/信息/档案更新）。</summary>
internal sealed class UserModule(UserService user, GameServices services) : IGameModule
{
    public IReadOnlyList<string> Prefixes => ["user"];

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        ModuleResult result;
        switch (request.Method)
        {
            case "user.UserLogin":
                // 应答前推送完整用户信息 + 引导数据 + 关卡数据，确保 LoginOk 事件触发前
                // Data.userData / GUIDE_DONE_STAGES / copyData 已就绪，
                // 避免 _RecordCanOpenModuleInfo 误判模块未开启导致 ModuleOpenPage 弹窗。
                var loginAccount = await ctx.GetAccountAsync();
                var now = (uint)ctx.Now;
                result = new ModuleResult
                {
                    Ret = TMessageCodec.EncodeRetUserLogin("ok", "", 0),
                    PrePushes =
                    [
                        await services.BuildUpdateUserInfoPushAsync(ctx.ProfileId, now, ctx.Ct),
                        services.BuildGuideInfoPush(now, loginAccount),
                        TMessageCodec.EncodeResponse(new TResponse(
                            Method: "copy.GetCopy",
                            Ret: ProtocolEncoder.EncodePlotCopyInfo(int.MaxValue, loginAccount.CopyProgress),
                            Time: now)),
                        TMessageCodec.EncodeResponse(new TResponse(
                            Method: "copy.GetCopy",
                            Ret: ProtocolEncoder.EncodeSeaCopyInfo(loginAccount.SeaProgress),
                            Time: now)),
                        TMessageCodec.EncodeResponse(new TResponse(
                            Method: "copy.GetCopy",
                            Ret: ProtocolEncoder.EncodeMubarCopyInfo(),
                            Time: now)),
                        TMessageCodec.EncodeResponse(new TResponse(
                            Method: "copy.GetCopy",
                            Ret: ProtocolEncoder.EncodeDailyCopyInfo(loginAccount.DailyCopy),
                            Time: now)),
                        TMessageCodec.EncodeResponse(new TResponse(
                            Method: "copy.GetCopy",
                            Ret: ProtocolEncoder.EncodeGoodsCopyInfo(),
                            Time: now)),
                        TMessageCodec.EncodeResponse(new TResponse(
                            Method: "outpost.UpdateOutPostInfo",
                            Ret: ProtocolEncoder.EncodeOutPostInfo(loginAccount.Outpost),
                            Time: now)),
                        DailyCopyService.BuildUpdatePush(loginAccount.DailyCopy, now),
                    ],
                };
                break;
            case "user.GetUserInfo":
            {
                // 同步推送内部会先结算离线期间的时间（可能改变温泉币等），应答必须用结算后的账号编码。
                IReadOnlyList<byte[]> syncPushes = await services.BuildSyncPushesAsync(ctx.ProfileId, (uint)ctx.Now, ctx.Ct);
                result = new ModuleResult
                {
                    Ret = GameServices.EncodeGetUserInfo(await ctx.GetAccountAsync()),
                    PostPushes = syncPushes,
                };
                break;
            }
            case "user.Refresh":
            {
                // 客户端定期刷新：结算心情与浴券，有变化时按统一顺序推送。
                SettlementResult settled = await services.SettleAsync(ctx.ProfileId, ctx.Now, ctx.Ct);
                uint refreshNow = (uint)ctx.Now;
                result = new ModuleResult
                {
                    PrePushes = GameServices.BuildMoodSyncPushes(
                        settled.Account, settled.ChangedHeroIds, settled.BuildingChanged, refreshNow),
                    PostPushes = GameServices.BuildSettlementPostPushes(settled, refreshNow),
                };
                break;
            }
            case "user.SetUserSecretary":
                result = await SetSecretaryAsync(ctx, request);
                break;
            case "user.ChangeName":
            case "user.SetMessage":
            case "user.SetPlayerHeadFrame":
            case "user.SetHead":
                var field = request.Method switch
                {
                    "user.ChangeName" => "Name",
                    "user.SetMessage" => "Message",
                    "user.SetPlayerHeadFrame" => "HeadFrame",
                    _ => "Head",
                };
                result = new ModuleResult
                {
                    Ret = await user.BuildUserProfileUpdateAsync(request, ctx.ProfileId, ctx.Ct, field),
                    PostPushes = [await services.BuildUpdateUserInfoPushAsync(ctx.ProfileId, (uint)ctx.Now, ctx.Ct)],
                };
                break;
            case "user.GetHeadBuyCount":
                result = ModuleResult.Ok(new byte[] { 0x08, 0x00, 0x10, 0x00 }); // ShipFleetId=0, Count=0
                break;
            case "user.BuyHead":
            case "user.NewHeadUnlockedList":
            default:
                result = ModuleResult.Empty;
                break;
        }
        return result;
    }

    /// <summary>
    /// 更换秘书舰：先结算，再写入新的 SecretaryId，然后结算旧秘书舰的好感并为新秘书舰建立好感锚点，
    /// 两位舰娘的变化在应答前推送。
    /// </summary>
    private async Task<ModuleResult> SetSecretaryAsync(GameContext ctx, TRequest request)
    {
        uint now = (uint)ctx.Now;
        IReadOnlyList<byte[]> pushes;
        IReadOnlyList<byte[]> settlementPost;
        byte[] ret;
        using (await services.LockAccountAsync(ctx.ProfileId, ctx.Ct))
        {
            SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
            settlementPost = GameServices.BuildSettlementPostPushes(settled, now);
            uint oldSecretary = settled.Account.Character.SecretaryId;
            ret = await user.BuildUserProfileUpdateAsync(request, ctx.ProfileId, ctx.Ct, "Secretary");
            PlayerAccount account = await ctx.GetAccountAsync();
            PlayerAccount changed = TimeSettlement.ChangeSecretary(
                account, oldSecretary, account.Character.SecretaryId, now, services.SettlementRules,
                out IReadOnlySet<uint> changedHeroIds);
            if (!ReferenceEquals(changed, account)) await services.SaveAccountAsync(changed, ctx.Ct);
            pushes = GameServices.BuildMoodSyncPushes(
                changed, settled.ChangedHeroIds.Concat(changedHeroIds), settled.BuildingChanged, now);
        }
        return new ModuleResult
        {
            Ret = ret,
            PrePushes = pushes,
            PostPushes = [await services.BuildUpdateUserInfoPushAsync(ctx.ProfileId, now, ctx.Ct), .. settlementPost],
        };
    }
}
