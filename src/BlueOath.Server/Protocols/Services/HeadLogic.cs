using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>
/// config_profile 的一行：头像 id、所属舰娘 BelongShipId（= config_ship_info.sf_id）、类型
/// （0 立绘头像，default=1 的那一行是舰娘的默认头像；1 付费头像；2 换装头像，Fashion 是对应的 config_fashion id；3 表情与活动头像）。
/// </summary>
internal sealed record HeadProfile(int Id, int BelongShipId, int Type, bool IsDefault, int Fashion);

/// <summary>头像目录：config_profile 按 id 索引，外加「舰娘模板 → sf_id」的换算（见 <see cref="ProfileHeadLoader"/>）。</summary>
internal sealed class HeadCatalog
{
    public const int TypePortrait = 0, TypeBuy = 1, TypeFashion = 2, TypeExpression = 3;

    /// <summary>空目录（表缺失或读取失败）：模板按旧规则 (TemplateId − 1) / 10 换算。</summary>
    public static HeadCatalog Empty { get; } = new([], LegacyShipFleetOf);

    private readonly Dictionary<int, HeadProfile> _byId = [];
    private readonly Dictionary<int, int> _defaultOf = [];
    private readonly Func<int, int> _shipFleetOf;

    /// <param name="rows">config_profile 的行；重复的 id 只保留第一行。</param>
    /// <param name="shipFleetOf">舰娘模板（THeroGrid.TemplateId）→ sf_id。</param>
    public HeadCatalog(IEnumerable<HeadProfile> rows, Func<int, int> shipFleetOf)
    {
        foreach (HeadProfile row in rows)
        {
            if (!_byId.TryAdd(row.Id, row)) continue;
            if (row.Type == TypePortrait && row.IsDefault) _defaultOf.TryAdd(row.BelongShipId, row.Id);
        }
        Rows = _byId.Values.OrderBy(row => row.BelongShipId).ThenBy(row => row.Id).ToList();
        _shipFleetOf = shipFleetOf;
    }

    /// <summary>全部头像，按所属舰娘、再按 id 升序。</summary>
    public IReadOnlyList<HeadProfile> Rows { get; }

    public bool IsEmpty => Rows.Count == 0;

    public bool Contains(int id) => _byId.ContainsKey(id);

    public HeadProfile? Get(int id) => _byId.GetValueOrDefault(id);

    /// <summary>该舰娘的默认头像（type 0、default=1，id 与 sf_id 相同）；没有时为 null。</summary>
    public int? DefaultHeadOf(int shipFleetId) => _defaultOf.TryGetValue(shipFleetId, out int id) ? id : null;

    /// <summary>舰娘模板所属的 sf_id（客户端头像页与 TNewHeadNode.ShipFleetId 用的就是它）。</summary>
    public int ShipFleetOf(int templateId) => _shipFleetOf(templateId);

    /// <summary>旧规则：模板 id 去掉末位的突破等级。改造后的舰娘得到的是改造图鉴的 id，不是 sf_id。</summary>
    internal static int LegacyShipFleetOf(int templateId) => (templateId - 1) / 10;
}

/// <summary>
/// 舰娘头像（user.SetHead / GetHeadBuyCount / BuyHead 与登录推送 user.NewHeadUnlockedList）的纯函数。
/// <para>
/// 客户端只从 user.NewHeadUnlockedList / user.NewHeadUnlock 两条推送得知哪些头像已解锁（HeadData.ShipHeadUnlockData），
/// 未解锁的头像带锁、「确认」只提示 3600006，不会发 user.SetHead。离线版只在登录同步里推 NewHeadUnlockedList：
/// 「头像全部解锁」作弊（--cheat-heads）开启时下发 config_profile 的全部头像（包括还没获得的舰娘的头像，客户端自己按
/// 图鉴状态挡住，中途获得的舰娘也就不用补推 NewHeadUnlock）；关闭时只下发船坞里舰娘的默认头像、已拥有换装的换装头像
/// 与当前头像。活动发放的表情头像（[29, id] 奖励）与付费头像离线不实现。
/// </para>
/// <para>
/// 作弊关闭时客户端的解锁状态是登录时的快照（HeadData 只置 true、不清除），而 user.SetHead 按请求时的船坞判断：
/// 中途获得的舰娘要重新登录后头像才解锁；中途退役了最后一艘的舰娘，头像在重新登录前仍显示已解锁，选用时回 3600006。
/// </para>
/// <para>
/// 目录为空（国服客户端没有 config_profile）时保持原来的行为：每艘舰娘推一个节点、ProfileID = (模板 − 1) / 10
/// （图鉴 id，未改造时等于 sf_id），
/// user.SetHead 接受任意非 0 id，用户信息里的头像原样下发。
/// </para>
/// </summary>
internal static class HeadLogic
{
    internal const string UnlockedListMethod = "user.NewHeadUnlockedList";

    /// <summary>新档案的头像（PlayerCharacter.Head 的默认值：初始秘书舰 1021051 的默认头像）。</summary>
    internal const int FallbackHead = 1021051;

    /// <summary>
    /// user.SetHead 拒绝未解锁头像的错误码：config_language 3600006「このアイコンが解放されていません」。
    /// 客户端 checkErr 把正的 Err 当作语言 id 弹出提示，与它自己在「确认」前发现头像未解锁时的提示相同。
    /// </summary>
    internal const int ErrLocked = 3600006;

    /// <summary>
    /// user.BuyHead 的错误码：config_language 110025「機能はまだ開放されていません」（功能尚未开放），
    /// 热更前后两版日服客户端都有且文字相同。离线版不实现头像购买：头像页的 3600xxx 文字都是购买确认或
    /// 未获得舰娘之类的提示，没有一条是「无法购买」，借用通用的「功能未开放」最贴切。
    /// </summary>
    internal const int ErrBuyUnavailable = 110025;

    /// <summary>
    /// 已解锁的头像，按所属舰娘分组（舰娘与 id 都升序）。目录为空时按旧规则：船坞里每艘舰娘（按出现顺序）一个节点，
    /// 节点里只有 ProfileID = (模板 − 1) / 10（图鉴 id，未改造时等于 sf_id）。
    /// </summary>
    internal static IReadOnlyList<(int ShipFleetId, IReadOnlyList<int> ProfileIds)> Unlocked(
        PlayerAccount account, HeadCatalog catalog, bool cheat)
    {
        if (catalog.IsEmpty)
            return account.Dock.Heroes
                .Select(hero => HeadCatalog.LegacyShipFleetOf(hero.TemplateId))
                .Distinct()
                .Select(illustrateId => (illustrateId, (IReadOnlyList<int>)[illustrateId]))
                .ToList();

        IEnumerable<HeadProfile> rows = catalog.Rows;
        if (!cheat)
        {
            HashSet<int> ownedShips = account.Dock.Heroes.Select(hero => catalog.ShipFleetOf(hero.TemplateId)).ToHashSet();
            HashSet<int> ownedFashion = (account.Fashion?.Entries ?? []).SelectMany(entry => entry.FashionTids).ToHashSet();
            int current = account.Character.Head;
            rows = rows.Where(row =>
                row.Id == current ||
                (ownedShips.Contains(row.BelongShipId) &&
                 ((row.Type == HeadCatalog.TypePortrait && row.IsDefault) ||
                  (row.Type == HeadCatalog.TypeFashion && row.Fashion > 0 && ownedFashion.Contains(row.Fashion)))));
        }
        return rows
            .GroupBy(row => row.BelongShipId)
            .Select(group => (group.Key, (IReadOnlyList<int>)group.Select(row => row.Id).ToList()))
            .ToList();
    }

    /// <summary>
    /// user.SetHead 能否换成 <paramref name="profileId"/>：必须是目录里已解锁的头像（作弊开启时目录里的任意头像）。
    /// 目录为空时沿用旧规则，任意非 0 id 都接受。
    /// </summary>
    internal static bool CanSet(PlayerAccount account, HeadCatalog catalog, bool cheat, int profileId)
    {
        if (catalog.IsEmpty) return profileId != 0;
        if (!catalog.Contains(profileId)) return false;
        return cheat || Unlocked(account, catalog, cheat: false).Any(group => group.ProfileIds.Contains(profileId));
    }

    /// <summary>
    /// 下发给客户端的头像（TGetUserInfoRet.Head）。客户端的资料页与头像框页按这个 id 查 config_profile 且不判空，
    /// 存档里的头像不在当前客户端的表里时（例如在热更客户端选的 2023-02 表情头像，换到未热更的客户端）改发秘书舰的
    /// 默认头像，再没有就发 <see cref="FallbackHead"/>。只改下发的值，不改存档。
    /// </summary>
    internal static int DisplayHead(PlayerAccount account, HeadCatalog catalog)
    {
        int head = account.Character.Head;
        if (catalog.IsEmpty || catalog.Contains(head)) return head;
        Hero? secretary = account.Dock.Heroes.FirstOrDefault(hero => hero.HeroId == account.Character.SecretaryId);
        return secretary is not null && catalog.DefaultHeadOf(catalog.ShipFleetOf(secretary.TemplateId)) is int id
            ? id
            : FallbackHead;
    }

    /// <summary>登录同步里的 user.NewHeadUnlockedList 推送。</summary>
    internal static byte[] BuildUnlockedListPush(PlayerAccount account, HeadCatalog catalog, bool cheat, uint now) =>
        TMessageCodec.EncodeResponse(new TResponse(Method: UnlockedListMethod,
            Ret: ProtocolEncoder.EncodeHeadUnlockedList(Unlocked(account, catalog, cheat)),
            Time: now));
}
