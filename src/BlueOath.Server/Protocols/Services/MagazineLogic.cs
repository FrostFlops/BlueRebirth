using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>栞解锁的结果。<see cref="Success"/> 为 false 时 <see cref="Account"/> 是原账号，<see cref="Error"/> 说明原因。</summary>
internal sealed record MagazineUnlockOutcome(PlayerAccount Account, string Error, bool BagChanged = false, bool CurrencyChanged = false)
{
    internal bool Success => Error.Length == 0;
}

/// <summary>
/// 杂志（SK BOOK）的只读状态（纯函数）。离线只实现阅读：期刊列表、往期的锁定 / 解锁与栞解锁；
/// 印章进度、印章箱、趋势舰娘与人气投票都不实现（活动期在 2020–2021 年，离线到不了，领奖类请求一律拒绝）。
/// <para>
/// 客户端 MagazineLogic:GetMagazineState：最新号在任务期内为 Active，否则有解锁时刻（&gt; 0）为 UnLock，其余为 Lock。
/// Lock 只影响三处：列表页的锁定角标（tag_lock / tag_release），单期页的栞按钮（btn_bookmark）与遮罩 im_mask
/// （都只在普通号上显示）。页面图片、目录、放大与每页任务列表不论状态都会渲染；im_mask 盖住哪块要看 prefab，未实测
/// （帮助文本 4000022 第 6 条说栞「再解放」的是戦姫専属任務）。
/// </para>
/// <para>
/// 「杂志全部解锁」作弊（--cheat-magazine）开启时每一期都按 UnLock 下发（特别号也是），不读写存档；关闭时只下发存档里
/// 用栞解锁过、且仍在目录里的期刊。下发的解锁只会是 <see cref="MagazineCatalog"/> 列出的期刊，
/// 它们的任务都在 task.TaskInfo 的字段 14 里，客户端不会看到「已解锁却没有任务数据」的期刊。
/// </para>
/// </summary>
internal static class MagazineLogic
{
    /// <summary>
    /// 下发的已解锁期刊：作弊时为目录里的全部期刊（存档里有解锁时刻就用它，否则用 now），
    /// 否则为存档里解锁过、且仍在目录里的期刊。按 id 升序；Time 都大于 0。
    /// </summary>
    internal static IReadOnlyList<MagazineUnlock> VisibleUnlocks(PlayerAccount account, MagazineCatalog catalog, bool cheat, long now)
    {
        Dictionary<int, long> stored = StoredUnlocks(account);
        long fallback = Math.Max(1, now);
        return catalog.Issues
            .Where(issue => cheat || stored.ContainsKey(issue.Id))
            .Select(issue => new MagazineUnlock(issue.Id, stored.TryGetValue(issue.Id, out long time) ? time : fallback))
            .ToList();
    }

    /// <summary>这一期是否已用栞解锁（只看存档，不含作弊）。</summary>
    internal static bool IsUnlocked(PlayerAccount account, int magazineId) => StoredUnlocks(account).ContainsKey(magazineId);

    /// <summary>magazine.GetMagazine 的内容：没有印章箱与趋势舰娘，只有解锁。</summary>
    internal static MagazineSnapshot Snapshot(PlayerAccount account, MagazineCatalog catalog, bool cheat, long now) =>
        new([], [], VisibleUnlocks(account, catalog, cheat, now));

    /// <summary>magazine.UpdateMagazineInfo 推送：目录里的期刊 id，Time = now，Version = 1。列表为空时客户端不显示杂志入口。</summary>
    internal static byte[] BuildInfoPush(MagazineCatalog catalog, uint now) =>
        TMessageCodec.EncodeResponse(new TResponse(
            Method: MagazineProtocolCodec.InfoMethod,
            Ret: MagazineProtocolCodec.EncodeInfo(new MagazineInfoMessage(catalog.ListedIds, now, 1)),
            Time: now));

    /// <summary>magazine.GetMagazine 推送（客户端整表替换，并刷新已打开的杂志页与红点）。</summary>
    internal static byte[] BuildSnapshotPush(PlayerAccount account, MagazineCatalog catalog, bool cheat, uint now) =>
        TMessageCodec.EncodeResponse(new TResponse(
            Method: MagazineProtocolCodec.SnapshotMethod,
            Ret: MagazineProtocolCodec.EncodeSnapshot(Snapshot(account, catalog, cheat, now)),
            Time: now));

    /// <summary>
    /// magazine.UnLock：用 config_parameter 365 的消耗（日服 1 张栞 18006）解锁一期往期并存档。
    /// 拒绝：未列出的期刊、特别号（客户端不显示栞按钮）、作弊已全部解锁、已解锁过、最新号仍在任务期（Active）、栞不足。
    /// 解锁不推进任何任务（离线不跟踪杂志任务进度），只去掉锁定遮罩与栞按钮。
    /// </summary>
    internal static MagazineUnlockOutcome TryUnlock(
        PlayerAccount account, MagazineCatalog catalog, int magazineId, long now, bool cheat)
    {
        MagazineIssue? issue = catalog.Get(magazineId);
        string? error =
            issue is null ? $"magazine {magazineId} is not listed" :
            issue.Special ? $"magazine {magazineId} is a special issue without a bookmark unlock" :
            cheat ? "--cheat-magazine already unlocks every issue" :
            IsUnlocked(account, magazineId) ? $"magazine {magazineId} is already unlocked" :
            catalog.IsActive(magazineId, now) ? $"magazine {magazineId} is still in its task period" :
            null;
        if (error is not null) return new MagazineUnlockOutcome(account, error);

        PaymentResult payment = CostLogic.TryPay(account, [catalog.UnlockCost]);
        if (!payment.Ok) return new MagazineUnlockOutcome(account, $"not enough bookmarks ({payment.Shortfall})");

        PlayerMagazine state = payment.Account.Magazine ?? new PlayerMagazine([]);
        PlayerAccount unlocked = payment.Account with
        {
            Magazine = state with { Unlocks = [.. state.Unlocks, new MagazineUnlock(magazineId, Math.Max(1, now))] },
        };
        return new MagazineUnlockOutcome(unlocked, "", payment.BagChanged, payment.CurrencyChanged);
    }

    /// <summary>存档里的解锁（同一期重复时取最早的一条，时刻不大于 0 的忽略）。</summary>
    private static Dictionary<int, long> StoredUnlocks(PlayerAccount account)
    {
        var stored = new Dictionary<int, long>();
        foreach (MagazineUnlock unlock in account.Magazine?.Unlocks ?? [])
            if (unlock.Time > 0 && (!stored.TryGetValue(unlock.MagazineId, out long existing) || unlock.Time < existing))
                stored[unlock.MagazineId] = unlock.Time;
        return stored;
    }
}
