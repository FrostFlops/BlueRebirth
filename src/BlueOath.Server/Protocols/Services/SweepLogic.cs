using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 扫荡作战（mopUp.*）的纯函数：完成轮数、结束时刻、每轮燃料、可同时扫荡的舰队数与 TMopUpRet 编码。
/// <para>
/// 扫荡没有随时间推进、需要落盘的状态，因此不并入 <see cref="TimeSettlement"/>：完成轮数在领取（mopUp.StopSweep）时
/// 由开始时刻与当前时间算出，与客户端的外推（LevelDetailsPage:SetSweepCopyCountName）一致。
/// </para>
/// </summary>
internal static class SweepLogic
{
    /// <summary>一次扫荡的轮数下限（客户端 AutoBattleSelectPage 的 1–10）。</summary>
    internal const int MinCounts = 1;

    /// <summary>一次扫荡的轮数上限。</summary>
    internal const int MaxCounts = 10;

    /// <summary>离线默认规则下可同时扫荡的舰队数。</summary>
    internal const int DefaultFleetsNum = 4;

    /// <summary>追加艦隊チケット（config_item_info 17106）：「真实消耗资源」下每张多一支扫荡舰队。</summary>
    internal const int ExtraFleetTicketId = 17106;

    /// <summary>追加艦隊チケット最多计入的张数（离线没有月卡，上限即 1 + 2 = 3 支）。</summary>
    internal const int MaxExtraFleets = 2;

    /// <summary>扫荡按 6 艘出击计燃料（客户端要求扫荡舰队满 6 艘）。</summary>
    internal const int SweepShips = 6;

    /// <summary>mopUp.GetMopUpData：服务端推送（客户端 CopyService:BackSweepCopyInfo），客户端主动请求时同样作答。</summary>
    internal const string DataMethod = "mopUp.GetMopUpData";

    /// <summary>账号当前的全部扫荡（旧档为空）。</summary>
    internal static IReadOnlyList<SweepEntry> Entries(PlayerAccount account) => account.Sweep?.Entries ?? [];

    /// <summary>
    /// 已完成的轮数 = clamp(⌊(max(now, StartTime) − StartTime) / RunSeconds⌋, 0, SweepCounts)。
    /// now 早于开始时刻（时钟回拨）时为 0；RunSeconds 不合法时只在到达 EndTime 后算全部完成。
    /// </summary>
    internal static int CompletedRuns(SweepEntry entry, long now)
    {
        if (entry.SweepCounts <= 0) return 0;
        if (entry.RunSeconds <= 0) return now >= entry.EndTime ? entry.SweepCounts : 0;
        long elapsed = Math.Max(now, entry.StartTime) - entry.StartTime;
        return (int)Math.Clamp(elapsed / entry.RunSeconds, 0, entry.SweepCounts);
    }

    /// <summary>结束时刻 = 开始时刻 + 轮数 × 每轮秒数（客户端用 (EndTime − now) mod RunSeconds 显示本轮剩余时间）。</summary>
    internal static long EndTime(long start, int counts, int runSeconds) => start + (long)counts * runSeconds;

    /// <summary>
    /// 可同时扫荡的舰队数（TMopUpRet.sweepFleetsNum）：默认 4；「真实消耗资源」下为 1 + 背包里追加艦隊チケット的张数（最多 2）。
    /// </summary>
    internal static int FleetsNum(PlayerAccount account, bool realResourceCost)
    {
        if (!realResourceCost) return DefaultFleetsNum;
        long tickets = account.Bag?.Items
            .Where(item => item.TemplateId == ExtraFleetTicketId)
            .Sum(item => (long)Math.Max(0, item.Num)) ?? 0;
        return 1 + (int)Math.Min(MaxExtraFleets, tickets);
    }

    /// <summary>
    /// 追击关卡：章节 running_level_list 中与本关在 level_list 里同位置的关卡（客户端 CopyLogic:GetCopyChaseInfo），没有时为 0。
    /// </summary>
    internal static int ChaseCopyId(int chapterId, int copyId)
    {
        int chase = SortieCostLoader.ChargedCopyId(chapterId, copyId, runningFight: true);
        return chase != copyId ? chase : 0;
    }

    /// <summary>
    /// 每轮燃料（客户端 LevelDetailsPage:StartSweepCopy）：本关按 6 艘普通出击的价格（<see cref="SortieCost.Cost"/>：
    /// 已通关的新海域按 after_clear_supple_num，max_fleet &gt; 0 的关卡按 6 艘价），有追击关卡时再加追击关卡的 total_supple_num[6]。
    /// </summary>
    internal static long SupplyPerRun(int chapterId, int copyId)
    {
        (_, long main) = SortieCost.Cost(
            SortieCostLoader.Get(copyId), SortieCost.NormalBattleMode, pvePtMode: false, [SweepShips], pvePtCost: 0);
        int chase = ChaseCopyId(chapterId, copyId);
        long extra = chase > 0 && SortieCostLoader.Get(chase) is { } chaseDisplay && chaseDisplay.Total.Count >= SweepShips
            ? Math.Max(0, chaseDisplay.Total[SweepShips - 1])
            : 0;
        return main + extra;
    }

    /// <summary>追加一条扫荡（同一舰队的旧条目先移除）。</summary>
    internal static PlayerAccount Put(PlayerAccount account, SweepEntry entry)
    {
        List<SweepEntry> entries = Entries(account).Where(existing => existing.FleetId != entry.FleetId).ToList();
        entries.Add(entry);
        return account with { Sweep = (account.Sweep ?? new PlayerSweep([])) with { Entries = entries } };
    }

    /// <summary>移除该舰队的扫荡；没有剩余扫荡时 Sweep 置回 null。</summary>
    internal static PlayerAccount RemoveFleet(PlayerAccount account, int fleetId)
    {
        List<SweepEntry> entries = Entries(account).Where(existing => existing.FleetId != fleetId).ToList();
        return account with
        {
            Sweep = entries.Count == 0 ? null : (account.Sweep ?? new PlayerSweep([])) with { Entries = entries },
        };
    }

    /// <summary>
    /// 编码 TMopUpRet{sweepFleetsNum(1), data(2, repeated MopUpData), passRets(3, repeated TPassBaseRet)}。
    /// 字段 1 总是写出（即使为 0）：客户端 GetServerMaxSweepTeam 遇到 nil 就什么都不做。
    /// MopUpData{fleetId(1), copyId(2), startTime(3, int64), endTime(4, int64), sweepCounts(5), chaperId(6)} 的字段全部写出。
    /// </summary>
    internal static byte[] EncodeRet(int fleetsNum, IReadOnlyList<SweepEntry> entries, IReadOnlyList<byte[]>? passRets = null)
    {
        ProtocolPackage ret = new();
        ret.Write(0x08, unchecked((ulong)fleetsNum));
        foreach (SweepEntry entry in entries)
        {
            ProtocolPackage data = new();
            data.Write(0x08, unchecked((ulong)entry.FleetId));
            data.Write(0x10, unchecked((ulong)entry.CopyId));
            data.Write(0x18, unchecked((ulong)entry.StartTime));
            data.Write(0x20, unchecked((ulong)entry.EndTime));
            data.Write(0x28, unchecked((ulong)entry.SweepCounts));
            data.Write(0x30, unchecked((ulong)entry.ChapterId));
            ret.Write(0x12, data.ToArray());
        }
        if (passRets is not null)
            foreach (byte[] passRet in passRets)
                ret.Write(0x1A, passRet);
        return ret.ToArray();
    }

    /// <summary>
    /// mopUp.GetMopUpData 推送：客户端用它整表替换扫荡列表与舰队上限，带 passRets 时打开报酬页。
    /// </summary>
    internal static byte[] BuildDataPush(
        int fleetsNum, IReadOnlyList<SweepEntry> entries, uint now, IReadOnlyList<byte[]>? passRets = null) =>
        TMessageCodec.EncodeResponse(new TResponse(
            Method: DataMethod,
            Ret: EncodeRet(fleetsNum, entries, passRets),
            Time: now));
}
