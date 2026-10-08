using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 离线基地服务：维护建筑新建、升级、完成、降级、舰娘派驻，以及资源楼与道具工厂的领取、下单、合成和加速。
/// 心情与产出由 <see cref="TimeSettlement"/> 按经过时间结算（BuildingModule 在调用本服务前已在账号锁内结算），
/// 本服务读到的是已结算的账号；所有写入的时间锚点取结算高水位，时钟回拨时不会重复结算。
/// </summary>
internal sealed class BuildingService(GameServices services)
{
    private const int Idle = BuildingProduction.Idle;
    private const int Adding = BuildingProduction.Adding;
    private const int Upgrading = BuildingProduction.Upgrading;
    private const int Working = BuildingProduction.Working;

    /// <summary>建筑变更结果。降级会自动领取存量，Rewards 写进 TReceiveRet。</summary>
    internal sealed record Mutation(
        PlayerAccount Account,
        int BuildingId = 0,
        int Err = 0,
        string ErrMsg = "",
        IReadOnlyList<CommonReward>? Rewards = null,
        bool CurrencyChanged = false,
        bool BagChanged = false)
    {
        internal bool Success => Err == 0;
    }

    // ───────────────────────── 生产：领取 / 下单 / 合成 / 加速 ─────────────────────────

    internal Task<BuildingProduction.Outcome> ReceiveAsync(
        string profileId, BuildingProduction.ReceiveKind kind, int arg, int now, CancellationToken ct) =>
        ApplyAsync(profileId, account => BuildingProduction.Receive(account, kind, arg, Anchor(account, now), services.SettlementRules), ct);

    internal Task<BuildingProduction.Outcome> OrderAsync(
        string profileId, int buildingId, int recipeId, int count, int now, CancellationToken ct) =>
        ApplyAsync(profileId, account => BuildingProduction.Order(account, buildingId, recipeId, count, Anchor(account, now), services.SettlementRules), ct);

    internal Task<BuildingProduction.Outcome> ComposeAsync(
        string profileId, int buildingId, int composeId, int count, CancellationToken ct) =>
        ApplyAsync(profileId, account => BuildingProduction.Compose(account, buildingId, composeId, count, services.SettlementRules), ct);

    internal Task<BuildingProduction.Outcome> SpeedupAsync(
        string profileId, int buildingId, int useCount, int now, CancellationToken ct) =>
        ApplyAsync(profileId, account => BuildingProduction.Speedup(account, buildingId, useCount, Anchor(account, now), services.SettlementRules), ct);

    private async Task<BuildingProduction.Outcome> ApplyAsync(
        string profileId, Func<PlayerAccount, BuildingProduction.Outcome> apply, CancellationToken ct)
    {
        PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
        if (account.Building is null) return new BuildingProduction.Outcome(account, [], false, false, 1, "No buildings");
        BuildingProduction.Outcome outcome = apply(account);
        if (outcome.Success && !ReferenceEquals(outcome.Account, account))
            await services.SaveAccountAsync(outcome.Account, ct);
        return outcome;
    }

    /// <summary>业务写入的时间锚点：本机时间与结算高水位取较大值。</summary>
    private static long Anchor(PlayerAccount account, int now) => Math.Max(now, account.LastSettleTime);

    // ───────────────────────── 建筑生命周期 ─────────────────────────

    internal async Task<Mutation> AddBuildingAsync(
        string profileId, AddBuildingArg arg, int now, CancellationToken ct)
    {
        PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
        PlayerBuilding state = account.Building ?? PlayerAccountFactory.DefaultBuilding(now);
        ConfigBuildinginfo? info = BuildingConfigLoader.GetInfo(arg.Tid);
        ConfigBuilding? land = BuildingConfigLoader.GetLand(arg.Index);
        if (info is null || info.Level != 1 || info.Type is < 2 or > 7)
            return Error(account, $"Invalid level-1 building tid {arg.Tid}");
        if (land is null || state.Lands.Any(item => item.Index == arg.Index))
            return Error(account, $"Land {arg.Index} is unavailable");

        PlayerBuildingEntry? office = FindOffice(state);
        ConfigBuildinginfo? officeInfo = office is null ? null : BuildingConfigLoader.GetInfo(office.Tid);
        if (office is null || officeInfo is null || land.Officelevel > office.Level)
            return Error(account, $"Land {arg.Index} is locked");
        if (land.BuildinggroupId?.Contains(info.Type) != true)
            return Error(account, $"Building type {info.Type} is not allowed on land {arg.Index}");

        int maxCount = GetTypeLimit(officeInfo, checked((int)info.Type));
        int currentCount = state.Buildings.Count(item => BuildingConfigLoader.GetInfo(item.Tid)?.Type == info.Type);
        if (maxCount <= 0 || currentCount >= maxCount)
            return Error(account, $"Building type {info.Type} has reached its limit");

        long anchor = Anchor(account, now);
        int buildingId = state.Buildings.Count == 0 ? 1 : state.Buildings.Max(item => item.Id) + 1;
        int duration = GetBuildDuration(arg.Tid);
        var entry = new PlayerBuildingEntry(
            Id: buildingId,
            Tid: arg.Tid,
            Level: 1,
            HeroIds: [],
            Status: Idle,
            LastUpdateTime: anchor,
            LastBuildUpdateTime: now);
        // 资源楼建成即开始生产（新锚点为 now，客户端不会从旧锚点外推出产量）；建造中不产出。
        entry = entry with { Status = duration > 0 ? Adding : BuildingProduction.StatusAfterLevelChange(entry, info) };
        PlayerBuilding updatedState = state with
        {
            Buildings = [.. state.Buildings, entry],
            Lands = [.. state.Lands, new PlayerBuildingLand(arg.Index, buildingId)],
        };
        return await SaveAsync(account, Refresh(account, updatedState, anchor), buildingId, ct);
    }

    internal async Task<Mutation> UpgradeBuildingAsync(
        string profileId, int buildingId, int now, CancellationToken ct)
    {
        PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
        PlayerBuilding state = account.Building ?? PlayerAccountFactory.DefaultBuilding(now);
        PlayerBuildingEntry? building = state.Buildings.FirstOrDefault(item => item.Id == buildingId);
        ConfigBuildinginfo? current = building is null ? null : BuildingConfigLoader.GetInfo(building.Tid);
        if (building is null || current is null || building.Status is Adding or Upgrading)
            return Error(account, $"Building {buildingId} cannot be upgraded");

        int targetLevel = building.Level + 1;
        ConfigBuildinginfo? target = BuildingConfigLoader.GetInfo(checked((int)current.Type), targetLevel);
        if (target is null) return Error(account, $"Building {buildingId} is already at max level");

        PlayerBuildingEntry? office = FindOffice(state);
        if (current.Type != 1 && (office is null || targetLevel > office.Level))
            return Error(account, $"Office level is too low for building {buildingId}");

        long anchor = Anchor(account, now);
        int duration = GetBuildDuration(checked((int)target.Id));
        PlayerBuildingEntry updated = duration > 0
            ? building with { Status = Upgrading, LastBuildUpdateTime = now }
            : building with
            {
                Tid = checked((int)target.Id),
                Level = targetLevel,
                Status = BuildingProduction.StatusAfterLevelChange(building, target),
                LastUpdateTime = Math.Max(building.LastUpdateTime, anchor),
                LastBuildUpdateTime = now,
            };
        return await SaveAsync(account, Refresh(account, Replace(state, updated), anchor), buildingId, ct);
    }

    internal async Task<Mutation> FinishBuildingAsync(
        string profileId, int buildingId, int now, CancellationToken ct)
    {
        PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
        PlayerBuilding state = account.Building ?? PlayerAccountFactory.DefaultBuilding(now);
        PlayerBuildingEntry? building = state.Buildings.FirstOrDefault(item => item.Id == buildingId);
        if (building is null) return Error(account, $"Unknown building {buildingId}");
        if (building.Status is not (Adding or Upgrading))
            return new Mutation(account, buildingId);

        ConfigBuildinginfo? current = BuildingConfigLoader.GetInfo(building.Tid);
        int targetLevel = building.Status == Adding ? 1 : building.Level + 1;
        ConfigBuildinginfo? target = current is null
            ? null
            : BuildingConfigLoader.GetInfo(checked((int)current.Type), targetLevel);
        if (target is null) return Error(account, $"Building {buildingId} has no completion target");
        int duration = GetBuildDuration(checked((int)target.Id));
        if (now < building.LastBuildUpdateTime + duration)
            return Error(account, $"Building {buildingId} is not finished yet");

        long anchor = Anchor(account, now);
        PlayerBuildingEntry updated = building with
        {
            Tid = checked((int)target.Id),
            Level = targetLevel,
            Status = BuildingProduction.StatusAfterLevelChange(building, target),
            LastUpdateTime = Math.Max(building.LastUpdateTime, anchor),
            LastBuildUpdateTime = now,
        };
        return await SaveAsync(account, Refresh(account, Replace(state, updated), anchor), buildingId, ct);
    }

    internal async Task<Mutation> DegradeBuildingAsync(
        string profileId, int buildingId, int now, CancellationToken ct)
    {
        PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
        PlayerBuilding state = account.Building ?? PlayerAccountFactory.DefaultBuilding(now);
        PlayerBuildingEntry? building = state.Buildings.FirstOrDefault(item => item.Id == buildingId);
        ConfigBuildinginfo? current = building is null ? null : BuildingConfigLoader.GetInfo(building.Tid);
        if (building is null || current is null || building.Status is Adding or Upgrading || building.Level <= 1)
            return Error(account, $"Building {buildingId} cannot be degraded", 3409);

        int targetLevel = building.Level - 1;
        ConfigBuildinginfo? target = BuildingConfigLoader.GetInfo(checked((int)current.Type), targetLevel);
        if (target is null || building.HeroIds.Count > target.Heronumber)
            return Error(account, $"Building {buildingId} cannot fit its current heroes after degradation", 3409);
        if (current.Type == 1 && !CanDegradeOffice(state, target))
            return Error(account, "Office degradation would lock an occupied land or exceed a building limit", 3409);
        // 客户端在道具工厂生产中时用 3002055 拦截降级。
        if (BuildingProduction.IsFactory(current) && building.Status == Working)
            return Error(account, $"Item factory {buildingId} is producing");

        // 降级前自动领取存量（客户端把应答按 TReceiveRet 解析并弹奖励）。
        long anchor = Anchor(account, now);
        BuildingProduction.Outcome collected =
            BuildingProduction.CollectForDegrade(account, buildingId, target, anchor, services.SettlementRules);
        account = collected.Account;
        state = account.Building!;
        building = state.Buildings.First(item => item.Id == buildingId);
        PlayerBuildingEntry updated = building with
        {
            Tid = checked((int)target.Id),
            Level = targetLevel,
            Status = BuildingProduction.StatusAfterLevelChange(building, target),
            LastUpdateTime = Math.Max(building.LastUpdateTime, anchor),
            LastBuildUpdateTime = now,
        };
        Mutation saved = await SaveAsync(account, Refresh(account, Replace(state, updated), anchor), buildingId, ct);
        return saved with
        {
            Rewards = collected.Rewards,
            CurrencyChanged = collected.CurrencyChanged,
            BagChanged = collected.BagChanged,
        };
    }

    internal async Task<Mutation> SetHeroesAsync(
        string profileId,
        IReadOnlyDictionary<int, IReadOnlyList<uint>> assignments,
        int now,
        CancellationToken ct)
    {
        PlayerAccount account = await services.GetOrCreateAccountAsync(profileId, ct);
        PlayerBuilding state = account.Building ?? PlayerAccountFactory.DefaultBuilding(now);
        var byId = state.Buildings.ToDictionary(building => building.Id);

        foreach ((int buildingId, IReadOnlyList<uint> heroIds) in assignments)
        {
            if (!byId.TryGetValue(buildingId, out PlayerBuildingEntry? building))
                return Error(account, $"Unknown building {buildingId}");
            if (building.Status is Adding or Upgrading || heroIds.Count > GetCapacity(building))
                return Error(account, $"Building {buildingId} cannot accept these heroes");
        }

        uint[] assignedHeroIds = assignments.Values.SelectMany(ids => ids).ToArray();
        if (assignedHeroIds.Distinct().Count() != assignedHeroIds.Length)
            return Error(account, "A hero cannot be assigned to more than one building");

        HashSet<uint> ownedHeroIds = account.Dock.Heroes.Select(hero => hero.HeroId).ToHashSet();
        if (assignedHeroIds.Any(heroId => heroId == 0 || !ownedHeroIds.Contains(heroId)))
            return Error(account, "The assignment contains a hero not owned by this profile");
        // 客户端 CheckBuildHero 会先拦截浴场中的舰娘并引导出浴；服务端同样拒绝，保证舰娘不同时在两处结算。
        if (account.Bath?.HeroList.Any(bath => assignedHeroIds.Contains(bath.HeroId)) == true)
            return Error(account, "A hero in the bathroom cannot be assigned to a building");

        // 调用方已把账号结算到 now。只有成员真正变化的建筑重置 LastUpdateTime 并重算宿舍速度与加成窗口；
        // 其它建筑保留原锚点，否则客户端外推的时间会被清零。锚点取结算高水位，时钟回拨时不会重复结算。
        long anchorNow = Anchor(account, now);
        Dictionary<uint, Hero> heroes = BuildingProduction.HeroMap(account);
        SettlementRules rules = services.SettlementRules;
        double discount = TimeSettlement.TavernDiscount(state, rules);
        HashSet<uint> movingHeroIds = assignedHeroIds.ToHashSet();
        bool workerTouched = false;
        var buildings = new List<PlayerBuildingEntry>(state.Buildings.Count);
        foreach (PlayerBuildingEntry building in state.Buildings)
        {
            IReadOnlyList<uint> heroIds = assignments.TryGetValue(building.Id, out IReadOnlyList<uint>? replacement)
                ? replacement.ToArray()
                : building.HeroIds.Where(heroId => !movingHeroIds.Contains(heroId)).ToArray();
            if (heroIds.SequenceEqual(building.HeroIds))
            {
                buildings.Add(building);
                continue;
            }
            buildings.Add(TimeSettlement.RefreshOccupancy(building, heroIds, anchorNow, rules, heroes, discount));
            if (BuildingConfigLoader.GetInfo(building.Tid)?.Type == TimeSettlement.ElectricFactoryType)
                workerTouched = true;
        }

        PlayerBuilding updatedState = state with
        {
            Buildings = buildings,
            WorkerUpdateTime = workerTouched ? Math.Max(state.WorkerUpdateTime, anchorNow) : state.WorkerUpdateTime,
        };
        return await SaveAsync(account, updatedState, 0, ct);
    }

    /// <summary>建筑等级/类型变化后：宿舍重算回复速度，全部建筑重算加成窗口与效率（居酒屋等级影响所有工作楼）。</summary>
    private PlayerBuilding Refresh(PlayerAccount account, PlayerBuilding state, long anchor)
    {
        SettlementRules rules = services.SettlementRules;
        Dictionary<uint, Hero> heroes = BuildingProduction.HeroMap(account);
        PlayerBuilding refreshed = state with
        {
            Buildings = state.Buildings
                .Select(building => rules.BuildingInfo(building.Tid)?.Type == TimeSettlement.DormType
                    ? building with
                    {
                        MoodSpeed = TimeSettlement.DormSpeed(
                            rules.BuildingInfo(building.Tid)!,
                            building.HeroIds.Where(heroes.ContainsKey).Select(id => heroes[id]), rules),
                    }
                    : building)
                .ToArray(),
        };
        return BuildingProduction.RefreshDerived(refreshed, heroes, anchor, rules);
    }

    // ───────────────────────── 快照编码 ─────────────────────────

    internal static UserBuildingInfo ToProtocol(PlayerBuilding? state, int now)
    {
        state ??= PlayerAccountFactory.DefaultBuilding(now);
        PlayerBuildingEntry? office = FindOffice(state);
        int officeLevel = office?.Level ?? 1;
        // 工人体力保持恒满：下单、合成、加速都不扣体力（与「本地基地不消耗物资」一致）。
        int fullWorkerStrength = BuildingConfigLoader.GetMaxWorkerStrength(officeLevel) * 10_000;
        int officeProductivity = office?.Productivity ?? 10_000;
        return new UserBuildingInfo(
            BuildingInfos: state.Buildings
                .OrderBy(building => building.Id)
                .Select(building => ToProtocol(building, now, officeProductivity))
                .ToArray(),
            LandList: state.Lands
                .OrderBy(land => land.Index)
                .Select(land => new BuildingLandInfo(land.Index, land.BuildingId))
                .ToArray(),
            WorkerStrength: fullWorkerStrength,
            WorkerRecover: state.WorkerRecover,
            FoodMax: state.FoodMax,
            ElectricMax: state.ElectricMax,
            WorkerUpdateTime: state.WorkerUpdateTime == 0 ? now : state.WorkerUpdateTime,
            NormalPlotUpdateTime: now);
    }

    private static BuildingInfo ToProtocol(PlayerBuildingEntry building, int now, int officeProductivity)
    {
        ConfigBuildinginfo? info = BuildingConfigLoader.GetInfo(building.Tid);
        int productivity = building.Productivity ?? 10_000;
        return new BuildingInfo(
            Id: building.Id,
            Tid: building.Tid,
            Level: building.Level,
            HeroList: building.HeroIds,
            Status: building.Status,
            // 下发存档里的结算锚点：客户端以它为起点外推心情增减与产出。
            LastUpdateTime: building.LastUpdateTime == 0 ? now : building.LastUpdateTime,
            LastBuildUpdateTime: building.LastBuildUpdateTime,
            ProduceSpeed: ProduceSpeed(building, info, productivity, officeProductivity),
            Productivity: productivity,
            ProductCount: building.ProductCount,
            RecipeId: building.RecipeId,
            ItemCount: building.ItemCount,
            FloatCount: BuildingProduction.IsFactory(info)
                ? Math.Clamp((int)Math.Floor(building.Progress * 10_000), 0, 9_999)
                : 0,
            HeroEffectTimes: (building.HeroWindows ?? [])
                .Select(window => new HeroEffectTimeInfo(
                    window.HeroId,
                    checked((int)window.Start),
                    (int)Math.Min(window.End, int.MaxValue)))
                .ToArray());
    }

    /// <summary>
    /// 下发的 ProduceSpeed：宿舍为每 RecoverUnit 秒回复的心情（客户端用它外推宿舍心情）；
    /// 资源楼与电力室只用于界面显示（燃油、资金每 600 秒产量；资金楼叠加办公室效率）。
    /// </summary>
    private static int ProduceSpeed(PlayerBuildingEntry building, ConfigBuildinginfo? info, int productivity, int officeProductivity)
    {
        if (info is null) return 0;
        switch (info.Type)
        {
            case TimeSettlement.DormType:
                return building.MoodSpeed ?? checked((int)info.Addmood);
            case TimeSettlement.ElectricFactoryType:
                return checked((int)Math.Floor(info.Addworkerhp * productivity / 10_000.0));
            case 3 or 4 when BuildingProduction.IsResource(info):
            {
                bool gold = info.Productid![1] == BuildingProduction.GoldId;
                int unit = Math.Max(1, ParameterCatalogLoader.Get(gold ? 210 : 209, 600));
                double ratio = 600.0 / unit;
                double factor = gold ? productivity + officeProductivity - 10_000.0 : productivity;
                return checked((int)Math.Floor(info.Productivity * ratio * factor / 10_000.0));
            }
            default:
                return 0;
        }
    }

    internal static byte[] BuildInfoPush(PlayerBuilding? state, uint now) =>
        TMessageCodec.EncodeResponse(new TResponse(
            Method: "building.UpdateBuildingInfo",
            Ret: PlayerDataCodec.Encode(ToProtocol(state, checked((int)now))),
            Time: now));

    private static Mutation Error(PlayerAccount account, string message, int err = 1) =>
        new(account, Err: err, ErrMsg: message);

    private async Task<Mutation> SaveAsync(
        PlayerAccount account, PlayerBuilding state, int buildingId, CancellationToken ct)
    {
        PlayerAccount updated = account with { Building = state };
        await services.SaveAccountAsync(updated, ct);
        return new Mutation(updated, buildingId);
    }

    private static PlayerBuilding Replace(PlayerBuilding state, PlayerBuildingEntry replacement) =>
        state with
        {
            Buildings = state.Buildings
                .Select(item => item.Id == replacement.Id ? replacement : item)
                .ToArray(),
        };

    private static PlayerBuildingEntry? FindOffice(PlayerBuilding state) =>
        state.Buildings.FirstOrDefault(item => BuildingConfigLoader.GetInfo(item.Tid)?.Type == 1)
        ?? state.Buildings.FirstOrDefault(item => item.Tid is >= 1 and <= 5);

    private static int GetBuildDuration(int tid) =>
        checked((int)(BuildingConfigLoader.GetLevelUp(tid)?.Leveluptime ?? 0));

    private static int GetCapacity(PlayerBuildingEntry building) =>
        checked((int)(BuildingConfigLoader.GetInfo(building.Tid)?.Heronumber ?? (building.Tid switch
        {
            >= 41 and <= 45 => 5,
            >= 1 and <= 5 => building.Level,
            _ => 0,
        })));

    private static int GetTypeLimit(ConfigBuildinginfo officeInfo, int type)
    {
        int index = type - 2;
        return officeInfo.Buildquantity is { } limits && index >= 0 && index < limits.Count
            ? checked((int)limits[index])
            : 0;
    }

    private static bool CanDegradeOffice(PlayerBuilding state, ConfigBuildinginfo targetOffice)
    {
        foreach (PlayerBuildingLand occupied in state.Lands)
        {
            ConfigBuilding? land = BuildingConfigLoader.GetLand(occupied.Index);
            if (land is not null && land.Officelevel > targetOffice.Level) return false;
        }
        foreach (IGrouping<long, PlayerBuildingEntry> group in state.Buildings
                     .Where(item => BuildingConfigLoader.GetInfo(item.Tid)?.Type is >= 2 and <= 7)
                     .GroupBy(item => BuildingConfigLoader.GetInfo(item.Tid)!.Type))
        {
            if (group.Count() > GetTypeLimit(targetOffice, checked((int)group.Key))) return false;
            if (group.Any(item => item.Level > targetOffice.Level)) return false;
        }
        return true;
    }
}
