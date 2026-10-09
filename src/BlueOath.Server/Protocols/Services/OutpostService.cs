using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>
/// アンブラ前哨领域服务：outpost.* 的业务与持久化。调用方（OutpostModule）已持有账号锁并把账号结算到 now，
/// 这里执行 <see cref="OutpostProduction"/> 的业务并在成功时落盘。
/// </summary>
internal sealed class OutpostService(GameServices services)
{
    internal static PlayerAccount EnsureOutpost(PlayerAccount account)
        => account.Outpost is null
            ? account with { Outpost = PlayerAccountFactory.DefaultOutpost() }
            : account;

    /// <summary>outpost.UpdateOutPostInfo 推送（客户端 _UpdateOutPostInfo 整表写入 Data.mubarOutpostData 并刷新前哨界面）。</summary>
    internal static byte[] BuildInfoPush(PlayerOutpost? state, uint now) =>
        TMessageCodec.EncodeResponse(new TResponse(
            Method: "outpost.UpdateOutPostInfo",
            Ret: ProtocolEncoder.EncodeOutPostInfo(state),
            Time: now));

    internal Task<OutpostProduction.Outcome> SetHeroAsync(
        PlayerAccount account, int buildingId, IReadOnlyList<uint> heroIds, long now, CancellationToken ct)
        => SaveAsync(account, OutpostProduction.SetHero(account, buildingId, heroIds, now, services.SettlementRules), ct);

    internal Task<OutpostProduction.Outcome> UpgradeBuildingAsync(PlayerAccount account, int buildingId, CancellationToken ct)
        => SaveAsync(account, OutpostProduction.Upgrade(account, buildingId, chargeCost: true), ct);

    internal Task<OutpostProduction.Outcome> SetUseCoinAsync(
        PlayerAccount account, int buildingId, int useCoin, long now, CancellationToken ct)
        => SaveAsync(account, OutpostProduction.SetUseCoin(account, buildingId, useCoin, now, services.SettlementRules), ct);

    internal Task<OutpostProduction.Outcome> SpeedUpProductionAsync(
        PlayerAccount account, int buildingId, long now, CancellationToken ct)
        => SaveAsync(account,
            OutpostProduction.SpeedUp(account, buildingId, now, services.SettlementRules, services.Cheats.RealResourceCost), ct);

    /// <summary>领取单个前哨（buildingId）或全部前哨（null）的产出。</summary>
    internal Task<OutpostProduction.Outcome> ReceiveAsync(PlayerAccount account, int? buildingId, CancellationToken ct)
        => SaveAsync(account, OutpostProduction.Receive(account, buildingId), ct);

    private async Task<OutpostProduction.Outcome> SaveAsync(
        PlayerAccount before, OutpostProduction.Outcome outcome, CancellationToken ct)
    {
        if (outcome.Success && !ReferenceEquals(outcome.Account, before))
            await services.SaveAccountAsync(outcome.Account, ct);
        return outcome;
    }
}
