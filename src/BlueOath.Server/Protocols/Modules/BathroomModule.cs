using BlueOath.Core;
using BlueOath.Protocol;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 浴场模块：bathroom.*（入浴/出浴/替换/送礼/自动续券/一键入浴）。
/// 每个请求先在账号锁内按经过时间结算（入浴回复、浴券到期、建筑心情），再执行业务。
/// 心情变化以 hero.UpdateHeroBagData 在应答前推送；浴场状态只能经 bathroom.BathroomInfo
/// 推送写入客户端 Data.bathroomData（各操作的应答回调是空实现），因此每次操作后都补发该推送。
/// 离线服不收取入浴、续券与送礼的温泉币。
/// </summary>
internal sealed class BathroomModule(GameServices services) : IGameModule
{
    public IReadOnlyList<string> Prefixes => ["bathroom"];

    /// <summary>单次请求内的可变状态：当前账号、变化的舰娘与是否改动了建筑。</summary>
    private sealed class BathOp(PlayerAccount account, SettlementResult settled)
    {
        public PlayerAccount Account { get; set; } = account;
        public HashSet<uint> ChangedHeroes { get; } = [.. settled.ChangedHeroIds];
        public bool BuildingChanged { get; set; } = settled.BuildingChanged;
        public bool Dirty { get; set; }
        public int Err { get; set; }
        public string ErrMsg { get; set; } = "";

        public List<BathHero> Pool => (Account.Bath?.HeroList ?? []).ToList();

        public void SetPool(List<BathHero> pool)
        {
            Account = Account with { Bath = new PlayerBath(pool, Account.Bath?.IsAllAuto ?? 0) };
            Dirty = true;
        }

        public byte[] Fail(string message)
        {
            Err = 1;
            ErrMsg = message;
            return [];
        }
    }

    public async Task<ModuleResult> HandleAsync(GameContext ctx, TRequest request)
    {
        using var _ = await services.LockAccountAsync(ctx.ProfileId, ctx.Ct);
        SettlementResult settled = await services.SettleLockedAsync(await ctx.GetAccountAsync(), ctx.Now, ctx.Ct);
        var op = new BathOp(settled.Account, settled);
        long now = settled.Account.LastSettleTime > ctx.Now ? settled.Account.LastSettleTime : ctx.Now;

        byte[] ret = request.Method switch
        {
            "bathroom.BathStart" => BathStart(op, PlayerDataCodec.DecodeBathStartArg(request.Args ?? []), now),
            "bathroom.BathEnd" => BathEnd(op, PlayerDataCodec.DecodeBathEndArg(request.Args ?? []), now),
            "bathroom.BathChangeHero" => BathChangeHero(op, PlayerDataCodec.DecodeBathChangeHeroArg(request.Args ?? []), now),
            "bathroom.BathService" => BathService(op, PlayerDataCodec.DecodeBathServiceArg(request.Args ?? [])),
            "bathroom.BathAuto" => BathAuto(op, PlayerDataCodec.DecodeBathAutoArg(request.Args ?? [])),
            "bathroom.BathAllAuto" => BathAllAuto(op, PlayerDataCodec.DecodeBathAllAutoArg(request.Args ?? [])),
            "bathroom.GetBathroomInfo" => PlayerDataCodec.Encode(GameServices.ToBathroomInfo(op.Account.Bath)),
            "bathroom.BathStartAll" => BathStartAll(op, PlayerDataCodec.DecodeBathStartAllArg(request.Args ?? []), now),
            _ => [],
        };

        if (op.Dirty) await services.SaveAccountAsync(op.Account, ctx.Ct);
        uint pushTime = checked((uint)ctx.Now);
        return new ModuleResult
        {
            Ret = ret,
            Err = op.Err,
            ErrMsg = op.ErrMsg,
            PrePushes = GameServices.BuildMoodSyncPushes(op.Account, op.ChangedHeroes, op.BuildingChanged, pushTime),
            PostPushes = [GameServices.BuildBathroomInfoPush(op.Account, pushTime)],
        };
    }

    /// <summary>
    /// 新入浴：先结算自然恢复，再 +ship_bath_mood_up（30），从建筑撤下，开始一张新浴券。
    /// 已在池中的舰娘只是换浴位（目标浴位有人则互换），浴券与强化效果不变、不加心情。
    /// </summary>
    private byte[] BathStart(BathOp op, PlayerDataCodec.TBathStartArg arg, long now)
    {
        if (FindHero(op.Account, arg.HeroId) is null) return op.Fail($"Hero {arg.HeroId} not found");
        if (arg.Pos is < 1 or > TimeSettlement.BathSlotCount) return op.Fail($"Invalid bath position {arg.Pos}");
        List<BathHero> pool = op.Pool;
        int selfIdx = pool.FindIndex(item => item.HeroId == arg.HeroId);
        int occupantIdx = pool.FindIndex(item => item.Pos == arg.Pos);
        if (selfIdx >= 0)
        {
            if (pool[selfIdx].Pos == arg.Pos) return Snapshot(op);
            if (occupantIdx >= 0) pool[occupantIdx] = pool[occupantIdx] with { Pos = pool[selfIdx].Pos };
            pool[selfIdx] = pool[selfIdx] with { Pos = arg.Pos };
            op.SetPool(pool);
            return Snapshot(op);
        }
        if (occupantIdx >= 0) return op.Fail($"Bath position {arg.Pos} is occupied");
        if (pool.Count >= TimeSettlement.BathSlotCount) return op.Fail("The bathroom is full");

        EnterBath(op, arg.HeroId, now, services.SettlementRules.BathEnterAdd);
        pool.Add(new BathHero(arg.HeroId, arg.Pos, StartTime: now, EnterTime: now));
        op.SetPool(pool);
        return Snapshot(op);
    }

    private byte[] BathEnd(BathOp op, uint heroId, long now)
    {
        List<BathHero> pool = op.Pool;
        int idx = pool.FindIndex(item => item.HeroId == heroId);
        if (idx < 0) return PlayerDataCodec.EncodeBathEndRet(0, 0, heroId);
        (int addExp, long bathTime) = LeaveBath(op, pool[idx], now);
        pool.RemoveAt(idx);
        op.SetPool(pool);
        return PlayerDataCodec.EncodeBathEndRet(addExp, bathTime, heroId);
    }

    /// <summary>
    /// 替换入浴：旧舰娘按出浴结算（作为应答），新舰娘接替同一浴位，继承剩余浴券时间与强化效果（帮助文本 300013），
    /// 不加入浴心情。旧舰娘浴券已到期时新舰娘开始一张新浴券。
    /// </summary>
    private byte[] BathChangeHero(BathOp op, PlayerDataCodec.TBathChangeHeroArg arg, long now)
    {
        List<BathHero> pool = op.Pool;
        int oldIdx = pool.FindIndex(item => item.HeroId == arg.HeroId);
        if (oldIdx < 0) return op.Fail($"Hero {arg.HeroId} is not in the bathroom");
        if (FindHero(op.Account, arg.NewHeroId) is null) return op.Fail($"Hero {arg.NewHeroId} not found");
        if (pool.Any(item => item.HeroId == arg.NewHeroId)) return op.Fail($"Hero {arg.NewHeroId} is already in the bathroom");

        BathHero old = pool[oldIdx];
        (int addExp, long bathTime) = LeaveBath(op, old, now);
        EnterBath(op, arg.NewHeroId, now, enterAdd: 0);
        pool[oldIdx] = Replacement(old, arg.NewHeroId, now);
        op.SetPool(pool);
        return PlayerDataCodec.EncodeBathEndRet(addExp, bathTime, arg.HeroId);
    }

    /// <summary>送礼：+gift_add_mod（60）心情。礼物 buff 与暴击需要 config_gift / config_value_effect，暂未实现。</summary>
    private byte[] BathService(BathOp op, PlayerDataCodec.TBathServiceArg arg)
    {
        BathHero? bath = op.Account.Bath?.HeroList.FirstOrDefault(item => item.HeroId == arg.HeroId);
        if (bath is null) return PlayerDataCodec.EncodeBathServiceRet(new BathHeroInfo(arg.HeroId), 0, false);
        AddMood(op, arg.HeroId, services.SettlementRules.BathGiftAdd);
        // BuffId=0：客户端 GetBathAttrBuff 在 heroBath.BuffId==0 时返回 nil，不查 buff 配置。
        return PlayerDataCodec.EncodeBathServiceRet(GameServices.ToBathHeroInfo(bath), 0, false);
    }

    private static byte[] BathAuto(BathOp op, PlayerDataCodec.TBathAutoArg arg)
    {
        List<BathHero> pool = op.Pool;
        int idx = pool.FindIndex(item => item.HeroId == arg.HeroId);
        if (idx >= 0 && pool[idx].IsAuto != arg.Status)
        {
            pool[idx] = pool[idx] with { IsAuto = arg.Status };
            op.SetPool(pool);
        }
        return [];
    }

    private static byte[] BathAllAuto(BathOp op, int status)
    {
        if ((op.Account.Bath?.IsAllAuto ?? 0) != status)
        {
            op.Account = op.Account with { Bath = new PlayerBath(op.Account.Bath?.HeroList ?? [], status) };
            op.Dirty = true;
        }
        return [];
    }

    /// <summary>
    /// 一键入浴：逐项处理 (HeroId, Pos)。目标浴位为空时按新入浴处理（+30 心情，与单个入浴一致），
    /// 目标浴位有其他舰娘时按替换处理。应答只包含被顶替出浴的舰娘。
    /// </summary>
    private byte[] BathStartAll(BathOp op, IReadOnlyList<PlayerDataCodec.TBathStartArg> args, long now)
    {
        var ended = new List<(int AddExp, long BathTime, uint HeroId)>();
        foreach (PlayerDataCodec.TBathStartArg arg in args)
        {
            if (FindHero(op.Account, arg.HeroId) is null || arg.Pos is < 1 or > TimeSettlement.BathSlotCount) continue;
            List<BathHero> pool = op.Pool;
            int selfIdx = pool.FindIndex(item => item.HeroId == arg.HeroId);
            int occupantIdx = pool.FindIndex(item => item.Pos == arg.Pos);
            if (selfIdx >= 0)
            {
                if (pool[selfIdx].Pos == arg.Pos) continue;
                if (occupantIdx >= 0) pool[occupantIdx] = pool[occupantIdx] with { Pos = pool[selfIdx].Pos };
                pool[selfIdx] = pool[selfIdx] with { Pos = arg.Pos };
                op.SetPool(pool);
                continue;
            }
            if (occupantIdx >= 0)
            {
                BathHero old = pool[occupantIdx];
                (int addExp, long bathTime) = LeaveBath(op, old, now);
                ended.Add((addExp, bathTime, old.HeroId));
                EnterBath(op, arg.HeroId, now, enterAdd: 0);
                pool[occupantIdx] = Replacement(old, arg.HeroId, now);
            }
            else
            {
                if (pool.Count >= TimeSettlement.BathSlotCount) continue;
                EnterBath(op, arg.HeroId, now, services.SettlementRules.BathEnterAdd);
                pool.Add(new BathHero(arg.HeroId, arg.Pos, StartTime: now, EnterTime: now));
            }
            op.SetPool(pool);
        }
        return PlayerDataCodec.EncodeBathStartAllRet(ended);
    }

    private static BathHero Replacement(BathHero old, uint newHeroId, long now) =>
        old.StartTime == 0
            ? new BathHero(newHeroId, old.Pos, StartTime: now, EnterTime: now)
            : new BathHero(newHeroId, old.Pos, IsAuto: 0, StartTime: old.StartTime, BathTime: 0,
                BuffId: old.BuffId, BuffTime: old.BuffTime, Power: old.Power, EnterTime: now);

    /// <summary>舰娘进入浴场：结算自然恢复（秘书舰同时结算好感）、加入浴心情、从建筑撤下。</summary>
    private void EnterBath(BathOp op, uint heroId, long now, int enterAdd)
    {
        SettlementRules rules = services.SettlementRules;
        op.Account = TimeSettlement.RemoveFromBuildings(op.Account, new HashSet<uint> { heroId }, now, rules, out bool removed);
        if (removed) op.BuildingChanged = true;
        UpdateHero(op, heroId, hero =>
        {
            Hero settled = TimeSettlement.ApplyNaturalToHero(hero, now, rules, heroId == op.Account.Character.SecretaryId);
            return enterAdd == 0
                ? settled
                : settled with { Mood = TimeSettlement.ClampMood((long)settled.Mood + enterAdd, rules) };
        });
    }

    /// <summary>出浴结算：入浴总时长与经验（每满 frequency 秒 +once_exp），经验按当前突破阶段的等级上限升级。</summary>
    private (int AddExp, long BathTime) LeaveBath(BathOp op, BathHero bath, long now)
    {
        long enter = bath.EnterTime != 0 ? bath.EnterTime : bath.StartTime;
        // 浴券已到期（StartTime=0）时，结算阶段已把总时长写进 BathTime。
        long bathTime = bath.StartTime == 0
            ? Math.Max(0, bath.BathTime)
            : Math.Max(0, now - enter);
        int addExp = TimeSettlement.BathExp(bathTime, services.SettlementRules);
        if (addExp > 0) UpdateHero(op, bath.HeroId, hero => GrantExp(hero, addExp));
        return (addExp, bathTime);
    }

    private Hero GrantExp(Hero hero, int addExp)
    {
        int cap = hero.AdvLv > 0
            ? checked((int)(ShipAdvanceLoader.Get(hero.AdvLv)?.MaxLevel ?? ShipAdvanceLoader.BaseMaxLevel))
            : ShipAdvanceLoader.BaseMaxLevel;
        if (cap <= 0) cap = 200;
        int level = hero.Level;
        long exp = (long)hero.Exp + addExp;
        while (level < cap)
        {
            int need = services.ExpNeeded.GetValueOrDefault(level, 500);
            if (need <= 0 || exp < need) break;
            exp -= need;
            level++;
        }
        return hero with { Level = level, Exp = checked((int)Math.Min(exp, int.MaxValue)) };
    }

    private void AddMood(BathOp op, uint heroId, int amount)
    {
        SettlementRules rules = services.SettlementRules;
        UpdateHero(op, heroId, hero => hero with { Mood = TimeSettlement.ClampMood((long)hero.Mood + amount, rules) });
    }

    private static void UpdateHero(BathOp op, uint heroId, Func<Hero, Hero> update)
    {
        List<Hero> heroes = op.Account.Dock.Heroes.ToList();
        int idx = heroes.FindIndex(hero => hero.HeroId == heroId);
        if (idx < 0) return;
        Hero updated = update(heroes[idx]);
        if (updated == heroes[idx]) return;
        heroes[idx] = updated;
        op.Account = op.Account with { Dock = op.Account.Dock with { Heroes = heroes } };
        op.ChangedHeroes.Add(heroId);
        op.Dirty = true;
    }

    private static Hero? FindHero(PlayerAccount account, uint heroId) =>
        account.Dock.Heroes.FirstOrDefault(hero => hero.HeroId == heroId);

    private static byte[] Snapshot(BathOp op) => PlayerDataCodec.Encode(GameServices.ToBathroomInfo(op.Account.Bath));
}
