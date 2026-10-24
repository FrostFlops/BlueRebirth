using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 指挥室装饰（interactionitem.*）的纯函数：持有、背包迁移、三种设置的校验与存档、推送编码。
/// <para>
/// 装饰品的来源几乎都是早已结束的活动、通行证与联动，所以与时装一样全部持有（<see cref="EnsureAll"/>，没有开关）。
/// 客户端只从 interactionitem.RefreshInteractionItems 推送读取装饰状态，应答处理器一律不改本地数据，
/// 所以每次设置都要推送变化的部分；推送里只放目录里有的 id（客户端遇到配置里没有的装饰品或套装会报错）。
/// </para>
/// </summary>
internal static class DecorLogic
{
    internal const string RefreshMethod = "interactionitem.RefreshInteractionItems";

    /// <summary>一次设置的结果：Error 为 null 表示成功；Push 是要推送的变化（只含改动的字段）。</summary>
    internal sealed record SetResult(PlayerAccount Account, string? Error, InteractionItemRet? Push = null, bool Changed = false);

    /// <summary>
    /// 按目录补齐装饰存档：持有目录里的全部装饰品（存档里没有的默认收起），只保留可单独切换的装饰品的显示状态；
    /// 套装不在目录里（如关掉了声明它的 Mod）时改回 0，海报框按配置的挂点重排，失效的海报改回 0，失效的水晶球去掉。
    /// 目录没读到时原样返回；没有变化时返回同一个账号。
    /// </summary>
    internal static PlayerAccount EnsureAll(PlayerAccount account, DecorCatalog catalog)
    {
        if (!catalog.IsLoaded) return account;
        PlayerDecor? saved = account.Decor;
        var visible = new Dictionary<int, int>();
        foreach (DecorBagItem item in saved?.Items ?? [])
            visible.TryAdd(item.Id, item.Visible);
        List<DecorBagItem> items = catalog.BagList
            .Select(bag => new DecorBagItem(bag.Id,
                DecorCatalog.IsToggleable(bag) && visible.GetValueOrDefault(bag.Id) == 1 ? 1 : 0))
            .ToList();

        int theme = saved?.Themes.FirstOrDefault(entry => entry.TypeId == DecorCatalog.MutexFurnitureTheme)?.SelectId ?? 0;
        if (theme != 0 && !catalog.Groups.ContainsKey(theme)) theme = 0;
        List<DecorTheme> themes = [new DecorTheme(DecorCatalog.MutexFurnitureTheme, theme)];

        var posterOf = new Dictionary<int, int>();
        foreach (DecorPoster poster in saved?.Posters ?? [])
            posterOf.TryAdd(poster.Point, poster.PosterId);
        List<DecorPoster> posters = catalog.PosterPoints
            .Select(point => new DecorPoster(point, IsPoster(catalog, posterOf.GetValueOrDefault(point))
                ? posterOf.GetValueOrDefault(point)
                : 0))
            .ToList();

        List<DecorBallToy>? toys = saved?.BallToys?
            .Where(toy => IsBall(catalog, toy.BallId) && (toy.ToyId == 0 || catalog.Figures.Contains(toy.ToyId)))
            .GroupBy(toy => toy.BallId)
            .Select(group => group.First())
            .ToList();
        if (toys is { Count: 0 }) toys = null;

        var result = new PlayerDecor(items, themes, posters, toys);
        return saved is not null && Same(saved, result) ? account : account with { Decor = result };
    }

    /// <summary>
    /// 迁移：把误入普通背包的装饰品（25xxxx）移出背包并记为持有。之前它们作为无用道具出现在素材页，从不出现在「装飾」页。
    /// 目录没读到时不动（不丢数据）。没有变化时返回同一个账号。
    /// </summary>
    internal static PlayerAccount CleanupBag(PlayerAccount account, DecorCatalog catalog)
    {
        if (!catalog.IsLoaded || account.Bag is not { } bag || !bag.Items.Any(item => DecorCatalog.IsDecorItemId(item.TemplateId)))
            return account;
        PlayerAccount result = account with
        {
            Bag = bag with { Items = bag.Items.Where(item => !DecorCatalog.IsDecorItemId(item.TemplateId)).ToList() },
        };
        foreach (BagItem item in bag.Items.Where(item => DecorCatalog.IsDecorItemId(item.TemplateId) && item.Num > 0))
            result = Grant(result, item.TemplateId, catalog);
        return result;
    }

    /// <summary>
    /// 获得一件装饰品（GameServices.AddBagItem 遇到 25xxxx 时调用）：目录里有的记为持有（默认收起），已持有或目录里没有的不变。
    /// 装饰品从不进普通背包。
    /// </summary>
    internal static PlayerAccount Grant(PlayerAccount account, int bagId, DecorCatalog catalog)
    {
        if (!catalog.Bags.ContainsKey(bagId)) return account;
        PlayerAccount ensured = account.Decor is null ? EnsureAll(account, catalog) : account;
        PlayerDecor decor = ensured.Decor ?? new PlayerDecor([], [], []);
        if (decor.Items.Any(item => item.Id == bagId)) return ensured;
        return ensured with
        {
            Decor = decor with { Items = [.. decor.Items, new DecorBagItem(bagId)] },
        };
    }

    /// <summary>
    /// interactionitem.SetMutexBagGroupState：groupType 必须是 1（家具套装）；SelectId 为 0（收起）或目录里至少持有一件的套装。
    /// 推送 decorate(6)。
    /// </summary>
    internal static SetResult SetTheme(PlayerAccount account, DecorCatalog catalog, int groupType, int selectId)
    {
        if (groupType != DecorCatalog.MutexFurnitureTheme)
            return new SetResult(account, $"unknown decoration group type {groupType}");
        PlayerDecor decor = Decor(account);
        if (selectId != 0)
        {
            if (!catalog.Groups.TryGetValue(selectId, out DecorGroup? group))
                return new SetResult(account, $"decoration set {selectId} is not in the client config");
            var owned = decor.Items.Select(item => item.Id).ToHashSet();
            if (!group.Bags.Any(owned.Contains))
                return new SetResult(account, $"no decoration of set {selectId} is owned");
        }
        int current = decor.Themes.FirstOrDefault(theme => theme.TypeId == groupType)?.SelectId ?? 0;
        var push = new InteractionItemRet(Decorate: [new DecorateMutexBagGroupInfo(groupType, selectId)]);
        if (current == selectId) return new SetResult(account, null, push);
        List<DecorTheme> themes = decor.Themes.Where(theme => theme.TypeId != groupType).ToList();
        themes.Add(new DecorTheme(groupType, selectId));
        return new SetResult(account with { Decor = decor with { Themes = themes } }, null, push, Changed: true);
    }

    /// <summary>
    /// interactionitem.SetBagItemVisible：只能切换持有的、不属于套装的 type 0 装饰品（客户端只给这些显示按钮），值只能是 0 或 1。
    /// 推送 interactionBagItem(5) 里的这一件。
    /// </summary>
    internal static SetResult SetVisible(PlayerAccount account, DecorCatalog catalog, int bagId, int visible)
    {
        if (visible is not (0 or 1))
            return new SetResult(account, $"invalid visible state {visible}");
        if (!catalog.Bags.TryGetValue(bagId, out DecorBag? bag))
            return new SetResult(account, $"decoration {bagId} is not in the client config");
        if (!DecorCatalog.IsToggleable(bag))
            return new SetResult(account, $"decoration {bagId} belongs to a set or is a poster and cannot be shown on its own");
        PlayerDecor decor = Decor(account);
        int index = decor.Items.ToList().FindIndex(item => item.Id == bagId);
        if (index < 0)
            return new SetResult(account, $"decoration {bagId} is not owned");
        var push = new InteractionItemRet(BagItems: [new InteractionBagItemInfo(bagId, 1, visible)]);
        if (decor.Items[index].Visible == visible) return new SetResult(account, null, push);
        List<DecorBagItem> items = decor.Items.ToList();
        items[index] = items[index] with { Visible = visible };
        return new SetResult(account with { Decor = decor with { Items = items } }, null, push, Changed: true);
    }

    /// <summary>
    /// interactionitem.SetPosterState：挂点必须是 config_parameter 377 里的海报框；posterId 为 0（取下）或持有的海报（type 1）。
    /// 同一张海报可以挂在多个框上（客户端不限制）。推送 posterState(8) 里的这个框。
    /// </summary>
    internal static SetResult SetPoster(PlayerAccount account, DecorCatalog catalog, int point, int posterId)
    {
        if (!catalog.PosterPoints.Contains(point))
            return new SetResult(account, $"{point} is not a poster frame");
        PlayerDecor decor = Decor(account);
        if (posterId != 0)
        {
            if (!IsPoster(catalog, posterId))
                return new SetResult(account, $"{posterId} is not a poster in the client config");
            if (decor.Items.All(item => item.Id != posterId))
                return new SetResult(account, $"poster {posterId} is not owned");
        }
        var push = new InteractionItemRet(Posters: [new PosterStateInfo(point, posterId)]);
        int current = decor.Posters.FirstOrDefault(poster => poster.Point == point)?.PosterId ?? 0;
        if (current == posterId) return new SetResult(account, null, push);
        List<DecorPoster> posters = decor.Posters.Where(poster => poster.Point != point).ToList();
        posters.Add(new DecorPoster(point, posterId));
        posters = posters
            .OrderBy(poster => IndexOf(catalog.PosterPoints, poster.Point))
            .ToList();
        return new SetResult(account with { Decor = decor with { Posters = posters } }, null, push, Changed: true);
    }

    /// <summary>
    /// interactionitem.SetCrystalBallToy：ballId 必须是水晶球（interaction_item_type 3），ToyId 为 0 或 config_interaction_figurte 的 id。
    /// 推送 ballToyState(4) 里的这个球。
    /// </summary>
    internal static SetResult SetBallToy(PlayerAccount account, DecorCatalog catalog, int ballId, int toyId)
    {
        if (!IsBall(catalog, ballId))
            return new SetResult(account, $"{ballId} is not a crystal ball");
        if (toyId != 0 && !catalog.Figures.Contains(toyId))
            return new SetResult(account, $"{toyId} is not in config_interaction_figurte");
        PlayerDecor decor = Decor(account);
        var push = new InteractionItemRet(BallToys: [new BallToyStateInfo(ballId, toyId)]);
        IReadOnlyList<DecorBallToy> current = decor.BallToys ?? [];
        if (current.Any(toy => toy.BallId == ballId && toy.ToyId == toyId)) return new SetResult(account, null, push);
        List<DecorBallToy> toys = current.Where(toy => toy.BallId != ballId).ToList();
        toys.Add(new DecorBallToy(ballId, toyId));
        return new SetResult(account with { Decor = decor with { BallToys = toys } }, null, push, Changed: true);
    }

    /// <summary>
    /// 登录时的完整状态：每件持有的装饰品（num = 1，state 一律写出）、家具套装 {1, curSelect}、每个海报框一条、已摆的水晶球。
    /// 只放目录里有的 id；不在目录里的套装与海报按 0 下发。
    /// </summary>
    internal static InteractionItemRet Snapshot(PlayerAccount account, DecorCatalog catalog)
    {
        PlayerDecor decor = Decor(account);
        List<InteractionBagItemInfo> items = decor.Items
            .Where(item => catalog.Bags.ContainsKey(item.Id))
            .Select(item => new InteractionBagItemInfo(item.Id, 1,
                DecorCatalog.IsToggleable(catalog.Bags[item.Id]) && item.Visible == 1 ? 1 : 0))
            .ToList();
        int theme = decor.Themes.FirstOrDefault(entry => entry.TypeId == DecorCatalog.MutexFurnitureTheme)?.SelectId ?? 0;
        if (!catalog.Groups.ContainsKey(theme)) theme = 0;
        var posterOf = new Dictionary<int, int>();
        foreach (DecorPoster poster in decor.Posters)
            posterOf.TryAdd(poster.Point, poster.PosterId);
        List<PosterStateInfo> posters = catalog.PosterPoints
            .Select(point => new PosterStateInfo(point,
                IsPoster(catalog, posterOf.GetValueOrDefault(point)) ? posterOf.GetValueOrDefault(point) : 0))
            .ToList();
        List<BallToyStateInfo> toys = (decor.BallToys ?? [])
            .Where(toy => IsBall(catalog, toy.BallId) && (toy.ToyId == 0 || catalog.Figures.Contains(toy.ToyId)))
            .Select(toy => new BallToyStateInfo(toy.BallId, toy.ToyId))
            .ToList();
        return new InteractionItemRet(toys, items, [new DecorateMutexBagGroupInfo(DecorCatalog.MutexFurnitureTheme, theme)], posters);
    }

    /// <summary>interactionitem.RefreshInteractionItems 推送。</summary>
    internal static byte[] BuildPush(InteractionItemRet ret, uint now) =>
        TMessageCodec.EncodeResponse(new TResponse(Method: RefreshMethod, Ret: PlayerDataCodec.Encode(ret), Time: now));

    private static PlayerDecor Decor(PlayerAccount account) => account.Decor ?? new PlayerDecor([], [], []);

    private static bool IsPoster(DecorCatalog catalog, int bagId) =>
        bagId != 0 && catalog.Bags.TryGetValue(bagId, out DecorBag? bag) && bag.Type == DecorCatalog.BagTypePoster;

    private static bool IsBall(DecorCatalog catalog, int itemId) =>
        catalog.InteractionItemTypes.TryGetValue(itemId, out int type) && type == DecorCatalog.ItemTypeSnowGlobe;

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return int.MaxValue;
    }

    private static bool Same(PlayerDecor a, PlayerDecor b) =>
        a.Version == b.Version &&
        a.Items.SequenceEqual(b.Items) &&
        a.Themes.SequenceEqual(b.Themes) &&
        a.Posters.SequenceEqual(b.Posters) &&
        (a.BallToys ?? []).SequenceEqual(b.BallToys ?? []) &&
        (a.BallToys is null) == (b.BallToys is null);
}
