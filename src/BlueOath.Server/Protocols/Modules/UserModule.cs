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
                // 同步推送内部会先结算离线期间的时间（心情、基建产出、工人体力、浴券与祈愿墙的时间结算），应答必须用结算后的账号编码。
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
                    PostPushes = GameServices.BuildSettlementPostPushes(settled, refreshNow, current: settled.Account),
                };
                break;
            }
            case "user.SetUserSecretary":
                result = await SetSecretaryAsync(ctx, request);
                break;
            case "user.SetPlayerHeadFrame":
            {
                // 未拥有的头像框回 Err（客户端只记日志）；无论成败都推一次用户信息，客户端靠它显示当前头像框。
                bool frameSet = await user.SetPlayerHeadFrameAsync(request, ctx.ProfileId, ctx.Ct);
                result = new ModuleResult
                {
                    Err = frameSet ? 0 : 1,
                    ErrMsg = frameSet ? "" : "head frame not owned",
                    PostPushes = [await services.BuildUpdateUserInfoPushAsync(ctx.ProfileId, (uint)ctx.Now, ctx.Ct)],
                };
                break;
            }
            case "user.SetHead":
            {
                // 未解锁的头像回 Err=3600006（客户端 checkErr 按语言表弹出「このアイコンが解放されていません」）且不改档。
                // 无论成败都推一次用户信息，而且放在应答之前：成功时客户端的应答处理器发 UpdataUserInfoHead，
                // 资料页随即从 Data.userData 重画头像，推送在应答之后的话这时画的还是旧头像。
                bool headSet = await user.SetHeadAsync(request, ctx.ProfileId, ctx.Ct);
                result = new ModuleResult
                {
                    Err = headSet ? 0 : HeadLogic.ErrLocked,
                    ErrMsg = headSet ? "" : "head not unlocked",
                    PrePushes = [await services.BuildUpdateUserInfoPushAsync(ctx.ProfileId, (uint)ctx.Now, ctx.Ct)],
                };
                break;
            }
            case "user.ChangeName":
            case "user.SetMessage":
                var field = request.Method == "user.ChangeName" ? "Name" : "Message";
                result = new ModuleResult
                {
                    Ret = await user.BuildUserProfileUpdateAsync(request, ctx.ProfileId, ctx.Ct, field),
                    PostPushes = [await services.BuildUpdateUserInfoPushAsync(ctx.ProfileId, (uint)ctx.Now, ctx.Ct)],
                };
                break;
            case "user.GetHeadBuyCount":
            {
                // TNewHeadBuyCountArg{ShipFleetId(1)}：打开某艘舰娘的头像列表时发送。客户端按应答里的 ShipFleetId 记这艘舰娘
                // 买过几次付费头像（价格 = cost × (1 + costrate × 次数)），所以回显请求的舰娘；离线版不实现购买，次数总是 0。
                int shipFleetId = request.Args is null ? 0 : unchecked((int)ProtocolDecoder.DecodeVarintField(request.Args, 1));
                result = ModuleResult.Ok(ProtocolEncoder.EncodeHeadBuyCount(shipFleetId, 0));
                break;
            }
            case "user.BuyHead":
                // 付费头像离线不实现（作弊开启时它们本来就已解锁，购买按钮不会出现）。以前回空的成功应答，
                // 客户端记一条「购买成功」却什么也没解锁；现在回错误码，客户端弹「機能はまだ開放されていません」，不扣任何货币。
                result = new ModuleResult { Err = HeadLogic.ErrBuyUnavailable, ErrMsg = "buying ship heads is not available offline" };
                break;
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
            // 业务只改秘书舰，不动货币：结算后的账号可以直接用来编码快照。
            settlementPost = GameServices.BuildSettlementPostPushes(settled, now, current: settled.Account);
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
