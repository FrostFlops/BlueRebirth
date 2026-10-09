using BlueOath.Core;
using BlueOath.Protocol;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 商店/仓库模块：shop.*（购买/商店信息/刷新）与 bag.GetBagInfo。
/// 默认是离线规则（GM 目录全部商品、不限量、不刷新）；开启「商店真实库存」（--real-shop-stock）时 shop.* 走
/// <see cref="HandleStockAsync"/>：随机陈列、购买扣库存、定时与手动刷新。
/// </summary>
internal sealed class ShopModule(ShopService shop, GameServices services) : IGameModule
{
    /// <summary>GoodsType ITEM（入荷指令按背包道具扣）。</summary>
    private const int GoodsTypeItem = 1;

    /// <summary>config_parameter[183] free_refresh_item 缺失时的入荷指令道具 id。</summary>
    private const int DefaultRefreshTicketId = 10303;

    /// <summary>
    /// 「商店真实库存」请求的公共上下文：已结算并建立商店状态的账号、单调的 now、推送时间、
    /// 需要推送的商店（结算或建立状态时变化的商店，业务再加上本次涉及的商店）以及应答后的结算推送。
    /// </summary>
    private sealed record StockContext(
        GameContext Ctx, PlayerAccount Account, long Now, uint PushTime, HashSet<int> Touched, IReadOnlyList<byte[]> Post);

    /// <summary>发放结果：更新后的账号 + 生成的奖励（无效商品 Type=0）。</summary>
    private sealed record GoodsGrant(PlayerAccount Account, CommonReward Reward);
    /// <summary>
    /// 本次购买发放的舰娘模板 ID，供推送侧同步图鉴（<see cref="GameServices.BuildBuyPushesAsync"/>）。
    /// Resync 表示购买被拒（如「真实消耗资源」下余额不足），需要重推玩家信息与背包纠正客户端的本地预判。
    /// </summary>
    private sealed record PurchaseResult(byte[] Ret, bool Changed, string Error,
        IReadOnlyList<int>? ShipTemplateIds = null, bool Resync = false);

    public IReadOnlyList<string> Prefixes => ["shop", "bag"];

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        if (services.Cheats.RealShopStock && request.Method.StartsWith("shop.", StringComparison.Ordinal))
            return await HandleStockAsync(ctx, request);
        ModuleResult result;
        switch (request.Method)
        {
            case "shop.BuyGoods":
            case "shop.QualityBuyGoods":
                PurchaseResult purchase = request.Method == "shop.BuyGoods"
                    ? await BuildBuyGoodsRetAsync(ctx, request)
                    : await BuildQualityBuyGoodsRetAsync(ctx, request);
                result = new ModuleResult
                {
                    Ret = purchase.Ret,
                    Err = purchase.Changed ? 0 : 1,
                    ErrMsg = purchase.Error,
                    // Lua 购买成功回调会立即读取背包/货币，必须先刷新缓存；被拒时重推玩家信息与背包纠正客户端。
                    PrePushes = purchase.Changed
                        ? await services.BuildBuyPushesAsync(ctx.ProfileId, (uint)ctx.Now, ctx.Ct,
                            newShipTemplateIds: purchase.ShipTemplateIds)
                        : purchase.Resync
                            ? await ResyncPushesAsync(ctx)
                            : [],
                };
                break;
            case "shop.GetShopsInfo":
                result = ModuleResult.Ok(services.BuildShopsInfoRet(null, checked((uint)ctx.Now)));
                break;
            case "bag.GetBagInfo":
                result = ModuleResult.Ok(await shop.BuildGetBagInfoRetAsync(ctx.ProfileId, ctx.Ct));
                break;
            case "bag.GetNormalTreasureInfo":
                ShopService.TreasureOpenResult treasure =
                    await shop.BuildOpenNormalTreasureRetAsync(request, ctx.ProfileId, ctx.Ct);
                result = new ModuleResult
                {
                    Ret = treasure.Ret,
                    Err = treasure.Changed ? 0 : 1,
                    ErrMsg = treasure.Error,
                    // 宝箱成功回调会立即重读背包并展示装备；先刷新所有可能受奖励影响的缓存。
                    PrePushes = treasure.Changed
                        ? await services.BuildBuyPushesAsync(ctx.ProfileId, (uint)ctx.Now, ctx.Ct,
                            treasure.RemovedTreasureTemplateId > 0
                                ? [treasure.RemovedTreasureTemplateId]
                                : null)
                        : [],
                };
                break;
            case "bag.GetSelectTreasureInfo":
                ShopService.TreasureOpenResult selected =
                    await shop.BuildOpenSelectTreasureRetAsync(request, ctx.ProfileId, ctx.Ct);
                // 非道具箱（舰船/装备选择箱）由 BuildOpenSelectTreasureRetAsync 返回
                // Changed=false 且 Error 为空，此处保持改动前的空响应，不引入新的错误码。
                result = !selected.Changed && selected.Error.Length == 0
                    ? ModuleResult.Empty
                    : new ModuleResult
                    {
                        Ret = selected.Ret,
                        Err = selected.Changed ? 0 : 1,
                        ErrMsg = selected.Error,
                        PrePushes = selected.Changed
                            ? await services.BuildBuyPushesAsync(ctx.ProfileId, (uint)ctx.Now, ctx.Ct,
                                selected.RemovedTreasureTemplateId > 0
                                    ? [selected.RemovedTreasureTemplateId]
                                    : null)
                            : [],
                    };
                break;
            case "shop.RefreshShop":
            default:
                result = ModuleResult.Empty;
                break;
        }
        return result;
    }

    /// <summary>购买被拒时的纠正推送：玩家信息（货币）与背包。</summary>
    private async Task<IReadOnlyList<byte[]>> ResyncPushesAsync(GameContext ctx)
    {
        PlayerAccount account = await ctx.GetAccountAsync();
        return
        [
            GameServices.BuildUpdateUserInfoPush(account, (uint)ctx.Now),
            services.BuildBagPush(account, (uint)ctx.Now),
        ];
    }

    /// <summary>
    /// 该商品已买过的件数（决定分档价格）。开启「商店真实库存」时取商店状态里自上次重置以来的已购数；
    /// 未开启时不记录，按 0 计（每次都是第一档价格）。必须在本次购买计入状态之前调用。
    /// </summary>
    private int PurchasedCount(PlayerAccount account, int shopId, int goodId) =>
        services.Cheats.RealShopStock ? ShopStock.BoughtCount(account.Shop, shopId, goodId) : 0;

    /// <summary>
    /// 「真实消耗资源」：按 config_shop_goods 的价格扣货币与道具（<see cref="ShopPricing"/>）。
    /// 未开启该选项、或商品不在配置中（GM 自建商品）时不扣。返回 null 表示不需要扣或已扣成功（账号经 ref 更新）。
    /// </summary>
    private string? TryCharge(ref PlayerAccount account, IEnumerable<(int ShopId, int GoodId, int BuyNum, int PriceIndex)> lines)
    {
        if (!services.Cheats.RealResourceCost) return null;
        var costs = new List<CostItem>();
        // 记录已购数时，同一单里重复出现的商品按顺序继续往后取分档。
        var planned = new Dictionary<(int ShopId, int GoodId), int>();
        foreach ((int shopId, int goodId, int buyNum, int priceIndex) in lines)
        {
            if (ShopCatalogLoader.GetGood(goodId) is not { } good) continue;
            int earlier = planned.GetValueOrDefault((shopId, goodId));
            costs.AddRange(ShopPricing.Costs(good, priceIndex, PurchasedCount(account, shopId, goodId) + earlier, buyNum));
            if (services.Cheats.RealShopStock) planned[(shopId, goodId)] = earlier + buyNum;
        }
        PaymentResult paid = CostLogic.TryPay(account, costs);
        if (!paid.Ok) return "not enough resources: " + paid.Shortfall;
        account = paid.Account;
        return null;
    }

    /// <summary>
    /// 处理 shop.BuyGoods：发放商品内容到对应存储（开启「真实消耗资源」时先按价格扣费）。
    /// - ITEM/EQUIP_ENHANCE_ITEM → 仓库（bag）
    /// - CURRENCY → 货币（UserInfo 对应字段）
    /// - FASHION → 时装解锁
    /// 返回 TBuyGoodsRet{Reward, GoodId, BuyNum}，并把更新后的账号落盘。
    /// </summary>
    private async Task<PurchaseResult> BuildBuyGoodsRetAsync(GameContext ctx, TRequest request)
    {
        if (request.Args is null) return new([], false, "purchase request is missing");
        BuyGoodsArg arg = TMessageCodec.DecodeBuyGoodsArg(request.Args);
        if (arg.BuyNum <= 0) arg = arg with { BuyNum = 1 };

        using var _ = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        PlayerAccount account = await ctx.GetAccountAsync();
        if (TryCharge(ref account, [(arg.ShopId, arg.GoodId, arg.BuyNum, arg.PriceIndex)]) is { } shortfall)
            return new([], false, shortfall, Resync: true);
        GoodsGrant grant = ApplyGoods(account, arg.GoodId, arg.BuyNum, ctx.Now);
        if (grant.Reward.Type == 0) return new([], false, "shop goods were not found");
        await services.SaveAccountAsync(grant.Account, ctx.Ct);

        return new(TMessageCodec.EncodeBuyGoodsRet(grant.Reward, arg.GoodId, arg.BuyNum), true, "",
            grant.Reward.Type == GameServices.GoodsTypeShip ? [grant.Reward.ConfigId] : null);
    }

    /// <summary>处理 shop.QualityBuyGoods（多选/批量购买）：对每个 GoodId 免费发放。</summary>
    private async Task<PurchaseResult> BuildQualityBuyGoodsRetAsync(GameContext ctx, TRequest request)
    {
        if (request.Args is null) return new([], false, "purchase request is missing");
        QualityBuyGoodsArg arg = TMessageCodec.DecodeQualityBuyGoodsArg(request.Args);
        if (arg.GoodIdList.Count == 0) return new([], false, "purchase list is empty");

        using var _ = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        PlayerAccount account = await ctx.GetAccountAsync();
        // 多选购买每件各买 1 个，全部够才扣（一项不足整单不买）。
        if (TryCharge(ref account, arg.GoodIdList.Select(goodId => (arg.ShopId, goodId, 1, 0))) is { } shortfall)
            return new([], false, shortfall, Resync: true);
        var rewards = new List<CommonReward>();
        foreach (var goodId in arg.GoodIdList)
        {
            var grant = ApplyGoods(account, goodId, 1, ctx.Now);
            if (grant.Reward.Type == 0) continue;
            account = grant.Account;
            rewards.Add(grant.Reward);
        }
        if (rewards.Count == 0) return new([], false, "shop goods were not found");
        await services.SaveAccountAsync(account, ctx.Ct);

        return new(TMessageCodec.EncodeQualityBuyGoodsRet(rewards, arg.GoodIdList), true, "",
            rewards.Where(reward => reward.Type == GameServices.GoodsTypeShip)
                .Select(reward => reward.ConfigId).ToList());
    }

    /// <summary>发放单个商品，返回更新后的账号和奖励。无效商品返回 Type=0 的空奖励。
    /// 优先 GM 商品（gm-goods.json），否则回落到 config_shop_goods（如赤改造商店）。扣费由调用方先做。</summary>
    private GoodsGrant ApplyGoods(PlayerAccount account, int goodId, int buyNum, int now)
    {
        if (buyNum <= 0) buyNum = 1;
        if (services.GmGoodsMap.TryGetValue(goodId, out var goods))
        {
            var totalNum = goods.Num * buyNum;

            if (goods.Type == GameServices.GoodsTypeCurrency)
            {
                account = GameServices.AddCurrency(account, goods.ItemId, totalNum);
            }
            else if (goods.Type == GameServices.GoodsTypeFashion)
            {
                account = services.AddFashion(account, goods.ItemId);
            }
            else if (goods.Type == GameServices.GoodsTypeShip)
            {
                // 舰娘购买 → 加入船坞（HeroDock），不能进背包（baglogic 按 config_table_index
                // 解析模板会崩溃）。reward.Id 携带最后一个生成的 HeroId 供客户端渲染。
                uint lastHeroId = 0;
                for (var i = 0; i < totalNum; i++)
                {
                    uint heroId = services.NextHeroId();
                    account = services.AddShip(account, heroId, goods.ItemId, now);
                    lastHeroId = heroId;
                }
                return new GoodsGrant(account, new CommonReward(goods.Type, goods.ItemId, 1, checked((int)lastHeroId)));
            }
            else if (goods.Type == GameServices.GoodsTypeEquip)
            {
                for (var i = 0; i < totalNum; i++)
                    account = AddEquipItem(account, goods.ItemId);
            }
            else
            {
                account = GameServices.AddBagItem(account, goods.ItemId, totalNum);
            }
            return new GoodsGrant(account, new CommonReward(goods.Type, goods.ItemId, totalNum));
        }

        // 回落：config_shop_goods（goods=[Type, ConfigId, Num?]）。
        var shopGood = ShopCatalogLoader.GetGood(goodId);
        if (shopGood?.Goods is not { Count: >= 2 })
            return new GoodsGrant(account, new CommonReward());
        int type = checked((int)shopGood.Goods[0]);
        int configId = checked((int)shopGood.Goods[1]);
        int perNum = shopGood.Goods.Count >= 3 ? checked((int)shopGood.Goods[2]) : 1;
        int totalCount = perNum * buyNum;

        switch (type)
        {
            case GameServices.GoodsTypeCurrency:
                account = GameServices.AddCurrency(account, configId, totalCount);
                break;
            case GameServices.GoodsTypeFashion:
                account = services.AddFashion(account, configId);
                break;
            case GameServices.GoodsTypeShip:
                uint lastHeroId = 0;
                for (var i = 0; i < totalCount; i++)
                {
                    uint heroId = services.NextHeroId();
                    account = services.AddShip(account, heroId, configId, now);
                    lastHeroId = heroId;
                }
                return new GoodsGrant(account, new CommonReward(type, configId, 1, checked((int)lastHeroId)));
            case GameServices.GoodsTypeEquip:
                for (var i = 0; i < totalCount; i++)
                    account = AddEquipItem(account, configId);
                break;
            default:
                account = GameServices.AddBagItem(account, configId, totalCount);
                break;
        }
        return new GoodsGrant(account, new CommonReward(type, configId, totalCount));
    }

    // ───────────────────────── 「商店真实库存」（--real-shop-stock） ─────────────────────────

    /// <summary>
    /// 「商店真实库存」下的 shop.*：账号锁内先结算（定时刷新、每日次数归零、免费刷新回复等）并建立商店状态、
    /// 为随机商店抽陈列，再执行业务。商店数据放在应答之前推送（客户端只从 shop.UpdateShopInfo 推送读取商店，
    /// 应答被忽略）；心情、建筑、浴场、祈愿的结算推送放在应答之后，避免打断商店界面。
    /// </summary>
    private async Task<ModuleResult> HandleStockAsync(GameContext ctx, TRequest request)
    {
        using var accountLock = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
        long now = Math.Max(ctx.Now, settled.Account.LastSettleTime);
        uint pushTime = checked((uint)ctx.Now);
        var touched = new HashSet<int>(settled.ShopChangedIds ?? new HashSet<int>());
        PlayerAccount account = shop.EnsureState(settled.Account, now, touched);
        if (!ReferenceEquals(account, settled.Account)) await services.SaveAccountAsync(account, ctx.Ct);
        var post = new List<byte[]>(
            GameServices.BuildMoodSyncPushes(account, settled.ChangedHeroIds, settled.BuildingChanged, pushTime));
        post.AddRange(GameServices.BuildSettlementPostPushes(settled, pushTime, includeShops: false));
        var stock = new StockContext(ctx, account, now, pushTime, touched, post);
        return request.Method switch
        {
            "shop.GetShopsInfo" => GetShopsInfoStock(stock),
            "shop.BuyGoods" => await BuyGoodsStockAsync(stock, request),
            "shop.QualityBuyGoods" => await QualityBuyGoodsStockAsync(stock, request),
            "shop.RefreshShop" => await RefreshShopStockAsync(stock, request),
            _ => new ModuleResult { PrePushes = ShopPushes(stock), PostPushes = stock.Post },
        };
    }

    /// <summary>需要推送的商店（stock.Touched）合成一条 shop.UpdateShopInfo；没有时为空。</summary>
    private static List<byte[]> ShopPushes(StockContext stock) =>
        GameServices.BuildStockShopPush(stock.Account, stock.Touched, stock.PushTime) is { } push ? [push] : [];

    /// <summary>
    /// 拒绝请求（Err=1）：推送该商店的当前状态与玩家信息、背包，纠正客户端的本地预判（售罄、余额）。
    /// 账号此时未做任何业务改动（结算与建立状态已落盘）。
    /// </summary>
    private ModuleResult RejectStock(StockContext stock, int shopId, string error)
    {
        services.FileLogger.LogInformation("shop stock rejected shop={ShopId}: {Error}", shopId, error);
        if (ShopCatalogLoader.Catalog.Shop(shopId) is not null) stock.Touched.Add(shopId);
        List<byte[]> pre = ShopPushes(stock);
        pre.Add(GameServices.BuildUpdateUserInfoPush(stock.Account, stock.PushTime));
        pre.Add(services.BuildBagPush(stock.Account, stock.PushTime));
        return new ModuleResult { Err = 1, ErrMsg = error, PrePushes = pre, PostPushes = stock.Post };
    }

    /// <summary>shop.GetShopsInfo：先推送全部商店（客户端读推送），应答是同一份 TRetShopsInfo。</summary>
    private ModuleResult GetShopsInfoStock(StockContext stock)
    {
        byte[] shops = services.BuildShopsInfoRet(stock.Account, stock.PushTime);
        return new ModuleResult
        {
            Ret = shops,
            PrePushes = [GameServices.BuildShopInfoPush(shops, stock.PushTime)],
            PostPushes = stock.Post,
        };
    }

    /// <summary>商店当前的状态（没有时是空状态，第一次购买时建立）。</summary>
    private static PlayerShopState StateOf(PlayerAccount account, int shopId) =>
        ShopStock.Find(account.Shop, shopId) ?? new PlayerShopState(shopId);

    /// <summary>
    /// shop.BuyGoods：商品必须在该商店当前陈列中且库存足够；开启「真实消耗资源」时按已购数分档扣费；
    /// 发放后已购数 += BuyNum。应答前推送购买数据与该商店（含结算中变化的商店）。
    /// </summary>
    private async Task<ModuleResult> BuyGoodsStockAsync(StockContext stock, TRequest request)
    {
        if (request.Args is null) return RejectStock(stock, 0, "purchase request is missing");
        BuyGoodsArg arg = TMessageCodec.DecodeBuyGoodsArg(request.Args);
        if (arg.BuyNum <= 0) arg = arg with { BuyNum = 1 };
        IReadOnlyList<int> lineup = GameServices.StockShopLineup(stock.Account, arg.ShopId) ?? [];
        ShopBuyCheck check = ShopStock.TryBuy(
            StateOf(stock.Account, arg.ShopId), lineup, ShopCatalogLoader.Catalog, arg.GoodId, arg.BuyNum);
        if (!check.Ok) return RejectStock(stock, arg.ShopId, check.Error);

        PlayerAccount account = stock.Account;
        if (TryCharge(ref account, [(arg.ShopId, arg.GoodId, arg.BuyNum, arg.PriceIndex)]) is { } shortfall)
            return RejectStock(stock, arg.ShopId, shortfall);
        GoodsGrant grant = ApplyGoods(account, arg.GoodId, arg.BuyNum, stock.Ctx.Now);
        if (grant.Reward.Type == 0) return RejectStock(stock, arg.ShopId, "shop goods were not found");
        account = grant.Account with { Shop = ShopStock.Put(grant.Account.Shop, check.State) };
        await services.SaveAccountAsync(account, stock.Ctx.Ct);

        stock.Touched.Add(arg.ShopId);
        var pre = new List<byte[]>(await services.BuildBuyPushesAsync(stock.Ctx.ProfileId, stock.PushTime, stock.Ctx.Ct,
            newShipTemplateIds: grant.Reward.Type == GameServices.GoodsTypeShip ? [grant.Reward.ConfigId] : null));
        pre.AddRange(ShopPushes(stock with { Account = account }));
        return new ModuleResult
        {
            Ret = TMessageCodec.EncodeBuyGoodsRet(grant.Reward, arg.GoodId, arg.BuyNum),
            PrePushes = pre,
            PostPushes = stock.Post,
        };
    }

    /// <summary>shop.QualityBuyGoods（快速购买）：每个 GoodId 买 1 件，库存与扣费都是全部够才买。</summary>
    private async Task<ModuleResult> QualityBuyGoodsStockAsync(StockContext stock, TRequest request)
    {
        if (request.Args is null) return RejectStock(stock, 0, "purchase request is missing");
        QualityBuyGoodsArg arg = TMessageCodec.DecodeQualityBuyGoodsArg(request.Args);
        if (arg.GoodIdList.Count == 0) return RejectStock(stock, arg.ShopId, "purchase list is empty");
        IReadOnlyList<int> lineup = GameServices.StockShopLineup(stock.Account, arg.ShopId) ?? [];
        PlayerShopState state = StateOf(stock.Account, arg.ShopId);
        foreach (int goodId in arg.GoodIdList)
        {
            ShopBuyCheck check = ShopStock.TryBuy(state, lineup, ShopCatalogLoader.Catalog, goodId, 1);
            if (!check.Ok) return RejectStock(stock, arg.ShopId, check.Error);
            state = check.State;
        }

        PlayerAccount account = stock.Account;
        if (TryCharge(ref account, arg.GoodIdList.Select(goodId => (arg.ShopId, goodId, 1, 0))) is { } shortfall)
            return RejectStock(stock, arg.ShopId, shortfall);
        var rewards = new List<CommonReward>();
        foreach (int goodId in arg.GoodIdList)
        {
            GoodsGrant grant = ApplyGoods(account, goodId, 1, stock.Ctx.Now);
            if (grant.Reward.Type == 0) continue;
            account = grant.Account;
            rewards.Add(grant.Reward);
        }
        if (rewards.Count == 0) return RejectStock(stock, arg.ShopId, "shop goods were not found");
        account = account with { Shop = ShopStock.Put(account.Shop, state) };
        await services.SaveAccountAsync(account, stock.Ctx.Ct);

        stock.Touched.Add(arg.ShopId);
        var pre = new List<byte[]>(await services.BuildBuyPushesAsync(stock.Ctx.ProfileId, stock.PushTime, stock.Ctx.Ct,
            newShipTemplateIds: rewards.Where(reward => reward.Type == GameServices.GoodsTypeShip)
                .Select(reward => reward.ConfigId).ToList()));
        pre.AddRange(ShopPushes(stock with { Account = account }));
        return new ModuleResult
        {
            Ret = TMessageCodec.EncodeQualityBuyGoodsRet(rewards, arg.GoodIdList),
            PrePushes = pre,
            PostPushes = stock.Post,
        };
    }

    /// <summary>
    /// shop.RefreshShop（日服只发 ShopId）：按 免费 → 入荷指令 → 钻石 的顺序刷新（<see cref="ShopStock.TryManualRefresh"/>），
    /// 随机商店重抽陈列。道具与钻石只在同时开启「真实消耗资源」时扣，次数与上限始终生效。
    /// 应答是该商店的 TRetShopInfo；不能刷新或超过上限时 Err=1。
    /// </summary>
    private async Task<ModuleResult> RefreshShopStockAsync(StockContext stock, TRequest request)
    {
        ShopRefreshArg arg = PlayerDataCodec.DecodeShopRefreshArg(request.Args ?? []);
        ShopCatalog catalog = ShopCatalogLoader.Catalog;
        if (catalog.Shop(arg.ShopId) is not { } rule) return RejectStock(stock, arg.ShopId, $"shop {arg.ShopId} is unknown");
        PlayerAccount account = stock.Account;
        PlayerShopState state = ShopStock.Find(account.Shop, arg.ShopId) ?? ShopStock.NewState(rule, stock.Now);
        int ticketId = services.Parameter(183, DefaultRefreshTicketId);
        bool hasTicket = account.Bag?.Items.Where(item => item.TemplateId == ticketId).Sum(item => item.Num) > 0;
        ShopRefreshPlan plan = ShopStock.TryManualRefresh(state, rule, catalog, stock.Now, hasTicket);
        if (!plan.Ok) return RejectStock(stock, arg.ShopId, plan.Error);

        bool currencyPaid = false, bagPaid = false;
        if (services.Cheats.RealResourceCost && plan.Payment is ShopRefreshPayment.Ticket or ShopRefreshPayment.Currency)
        {
            CostItem cost = plan.Payment == ShopRefreshPayment.Ticket
                ? new CostItem(GoodsTypeItem, ticketId, 1)
                : new CostItem(GameServices.GoodsTypeCurrency, rule.CurrencyType, plan.Cost);
            PaymentResult paid = CostLogic.TryPay(account, [cost]);
            if (!paid.Ok) return RejectStock(stock, arg.ShopId, "not enough resources: " + paid.Shortfall);
            account = paid.Account;
            currencyPaid = paid.CurrencyChanged;
            bagPaid = paid.BagChanged;
        }
        PlayerShopState next = rule.IsRandom ? plan.State with { Lineup = shop.DrawLineup(rule, account) } : plan.State;
        account = account with { Shop = ShopStock.Put(account.Shop, next) };
        await services.SaveAccountAsync(account, stock.Ctx.Ct);
        services.FileLogger.LogInformation(
            "shop.RefreshShop shop={ShopId} payment={Payment} cost={Cost} refreshNum={RefreshNum} usedFree={UsedFree} free={Free}",
            arg.ShopId, plan.Payment, plan.Cost, next.RefreshNum, next.UsedFRefreshNum, next.FRefreshNum);

        stock.Touched.Add(arg.ShopId);
        List<byte[]> pre = ShopPushes(stock with { Account = account });
        if (currencyPaid) pre.Add(GameServices.BuildUpdateUserInfoPush(account, stock.PushTime));
        if (bagPaid) pre.Add(services.BuildBagPush(account, stock.PushTime));
        IReadOnlyList<int> lineup = GameServices.StockShopLineup(account, arg.ShopId) ?? [];
        return new ModuleResult
        {
            Ret = PlayerDataCodec.Encode(ShopStock.ToShopInfo(arg.ShopId, lineup, next, catalog)),
            PrePushes = pre,
            PostPushes = stock.Post,
        };
    }

    /// <summary>装备入库：创建一件装备实例（EquipId 自增），存入装备仓库。</summary>
    private PlayerAccount AddEquipItem(PlayerAccount account, int templateId)
    {
        var equip = account.Equip ?? new PlayerEquip([], EquipBagSize: 2000);
        var items = equip.Items.ToList();
        var id = services.NextEquipId();
        items.Add(new EquipItem(EquipId: id, TemplateId: templateId));
        return account with { Equip = equip with { Items = items } };
    }

}
