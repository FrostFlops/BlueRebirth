using BlueOath.Core;
using BlueOath.Protocol;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>
/// アンブラ前哨模块：outpost.*。每个请求先在账号锁内按经过时间结算（前哨产出与驻守舰娘心情在
/// TimeSettlement.Settle 第 5 步）并落盘，再执行业务。前哨快照 outpost.UpdateOutPostInfo（客户端
/// _UpdateOutPostInfo 整表写入 Data.mubarOutpostData）与货币、背包推送都放在应答之前，客户端在应答回调里
/// （升级后的刷新、领取后的奖励页）读到的已是新数据；结算改了心情的舰娘按 [建筑, 舰娘, 建筑] 先推，
/// 浴场、祈愿、商店等其它结算推送放在应答之后。业务失败时同样推送结算结果。
/// </summary>
internal sealed class OutpostModule(OutpostService outpost, GameServices services) : IGameModule
{
    public IReadOnlyList<string> Prefixes => ["outpost"];

    private readonly ILogger _diagnostics = services.FileLogger;

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        using var accountLock = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
        PlayerAccount account = settled.Account;
        long now = Math.Max(ctx.Now, account.LastSettleTime);
        uint pushTime = checked((uint)ctx.Now);
        byte[] args = request.Args ?? [];
        switch (request.Method)
        {
            case "outpost.GetOutPostInfo":
                // 前哨界面打开后每 60 秒请求一次（MubarOutpostPage:StartOutpostTimer）；结算后总是推送前哨快照。
                return Result(settled, new OutpostProduction.Outcome(account, []), pushTime, reply: false);
            case "outpost.SetHero":
            {
                (int buildingId, IReadOnlyList<uint> heroIds) = DecodeSetHero(args);
                return Result(settled, await outpost.SetHeroAsync(account, buildingId, heroIds, now, ctx.Ct), pushTime, reply: false);
            }
            case "outpost.UpgradeBuilding":
            {
                int buildingId = DecodeBuildingId(args);
                return Result(settled, await outpost.UpgradeBuildingAsync(account, buildingId, ctx.Ct), pushTime, reply: false);
            }
            case "outpost.DegradeBuilding":
                // 离线模式降级直接回当前状态，避免客户端报错。
                return Result(settled, new OutpostProduction.Outcome(account, []), pushTime, reply: false);
            case "outpost.SetUseCoin":
            {
                (int buildingId, int useCoin) = DecodeSetUseCoin(args);
                return Result(settled, await outpost.SetUseCoinAsync(account, buildingId, useCoin, now, ctx.Ct), pushTime, reply: false);
            }
            case "outpost.SpeedUpProduction":
            {
                int buildingId = DecodeBuildingId(args);
                OutpostProduction.Outcome outcome = await outpost.SpeedUpProductionAsync(account, buildingId, now, ctx.Ct);
                Log(request.Method, buildingId, outcome);
                return Result(settled, outcome, pushTime, reply: true);
            }
            case "outpost.ReceiveItem":
            {
                int buildingId = DecodeBuildingId(args);
                OutpostProduction.Outcome outcome = await outpost.ReceiveAsync(account, buildingId, ctx.Ct);
                Log(request.Method, buildingId, outcome);
                return Result(settled, outcome, pushTime, reply: true);
            }
            case "outpost.ReceiveAll":
            {
                OutpostProduction.Outcome outcome = await outpost.ReceiveAsync(account, null, ctx.Ct);
                Log(request.Method, 0, outcome);
                return Result(settled, outcome, pushTime, reply: true);
            }
            default:
                // 编队预设（SaveTactic）等尚未实现；只同步本次结算产生的变化。
                return new ModuleResult
                {
                    PrePushes = GameServices.BuildMoodSyncPushes(account, settled.ChangedHeroIds, settled.BuildingChanged, pushTime),
                    PostPushes = GameServices.BuildSettlementPostPushes(settled, pushTime, current: account),
                };
        }
    }

    /// <summary>
    /// 统一出口。reply 为 true 时应答为 TOPReceiveRet（加速与领取，客户端据此弹 GetRewardsPage），否则为空应答。
    /// </summary>
    private ModuleResult Result(SettlementResult settled, OutpostProduction.Outcome outcome, uint now, bool reply)
    {
        PlayerAccount account = outcome.Account;
        var pre = new List<byte[]>(GameServices.BuildMoodSyncPushes(
            account, settled.ChangedHeroIds, settled.BuildingChanged || outcome.BuildingChanged, now));
        // 加速/领取发放的货币，以及结算里自动入浴扣的温泉币。
        if (outcome.CurrencyChanged || settled.CurrencyChanged) pre.Add(GameServices.BuildUpdateUserInfoPush(account, now));
        if (outcome.BagChanged) pre.Add(services.BuildBagPush(account, now));
        pre.Add(OutpostService.BuildInfoPush(account.Outpost, now));
        return new ModuleResult
        {
            Err = outcome.Err,
            ErrMsg = outcome.ErrMsg,
            Ret = reply && outcome.Success ? ProtocolEncoder.EncodeOutPostReceiveRet(outcome.Rewards) : [],
            PrePushes = pre,
            PostPushes = GameServices.BuildSettlementPostPushes(settled, now, includeOutpost: false, current: account),
        };
    }

    /// <summary>加速与领取的诊断日志：参数、结果、奖励与当日已用加速次数。</summary>
    private void Log(string method, int buildingId, OutpostProduction.Outcome outcome)
    {
        string rewards = string.Join(",", outcome.Rewards.Select(item => $"{item.Type}:{item.ConfigId}x{item.Num}"));
        _diagnostics.LogInformation(
            "{Method} building={Building} err={Err} {ErrMsg} rewards=[{Rewards}] speedUpTime={SpeedUpTime}",
            method, buildingId, outcome.Err, outcome.ErrMsg, rewards, outcome.Account.Outpost?.SpeedUpTime ?? 0);
    }

    private static int DecodeBuildingId(byte[] args)
    {
        int id = 0;
        ProtocolDecoder.ProtoReader reader = new(args);
        while (reader.TryReadField(out int field, out int wire))
            if (field == 1 && wire == 0) id = checked((int)reader.ReadVarint());
            else reader.Skip(wire);
        return id;
    }

    private static (int BuildingId, IReadOnlyList<uint> HeroIds) DecodeSetHero(byte[] args)
    {
        int buildingId = 0;
        var heroIds = new List<uint>();
        ProtocolDecoder.ProtoReader reader = new(args);
        while (reader.TryReadField(out int field, out int wire))
            switch (field)
            {
                case 1 when wire == 0:
                    buildingId = checked((int)reader.ReadVarint());
                    break;
                case 2 when wire == 0:
                    heroIds.Add(checked((uint)reader.ReadVarint()));
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        return (buildingId, heroIds);
    }

    private static (int BuildingId, int UseCoin) DecodeSetUseCoin(byte[] args)
    {
        int buildingId = 0, useCoin = 0;
        ProtocolDecoder.ProtoReader reader = new(args);
        while (reader.TryReadField(out int field, out int wire))
            switch (field)
            {
                case 1 when wire == 0:
                    buildingId = checked((int)reader.ReadVarint());
                    break;
                case 2 when wire == 0:
                    useCoin = checked((int)reader.ReadVarint());
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        return (buildingId, useCoin);
    }
}
