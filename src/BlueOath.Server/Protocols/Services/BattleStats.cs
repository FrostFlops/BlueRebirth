using System.Globalization;
using System.Text.Json;
using BlueOath.Core;
using BlueOath.Protocol;
using BlueOath.Server.Configs;

namespace BlueOath.Server.Protocols;

/// <summary>舰娘详情页上的一项属性（config_attribute.girl_if_show = 1）：属性 id、配置字段名（beizhu）、显示脚本与参数。</summary>
internal sealed record BattleShowAttr(int Id, string Field, string Display, IReadOnlyList<int> Params);

/// <summary>config_affection_favor 的一档好感：是否誓约、好感区间与加成（config_value_effect id 与强度）。</summary>
internal sealed record AffectionTier(int Id, bool Married, long Min, long Max, IReadOnlyList<(int EffectId, double Power)> Effects);

/// <summary>
/// 计算战斗属性要用到的配置表（<see cref="BattleStatsLoader"/> 从客户端配置加载，测试可手工构造）。
/// </summary>
/// <param name="ShowAttrs">详情页显示的属性（config_attribute.girl_if_show = 1），按 id 升序。</param>
/// <param name="AttributeLevels">config_ship_levelup：等级 → attribute_level（100 级是 320，不是线性）。</param>
/// <param name="PropValueTypes">config_prop.prop_value_type：0 是万分比属性。</param>
/// <param name="ValueEffects">config_value_effect.values 拆成（属性，值）。</param>
/// <param name="AffectionTiers">config_affection_favor，按 id 升序。</param>
/// <param name="UnmarriedBounds">config_parameter 155：未誓约好感上下限。</param>
/// <param name="MarriedBounds">config_parameter 156：已誓约好感上下限。</param>
/// <param name="PlaneNumbers">config_ship_equip.plane_number：舰船模板 → 各装备栏的舰载机数。</param>
internal sealed record BattleStatsCatalog(
    IReadOnlyList<BattleShowAttr> ShowAttrs,
    IReadOnlyDictionary<int, int> AttributeLevels,
    IReadOnlyDictionary<int, int> PropValueTypes,
    IReadOnlyDictionary<int, IReadOnlyList<(int Attr, double Value)>> ValueEffects,
    IReadOnlyList<AffectionTier> AffectionTiers,
    (long Min, long Max) UnmarriedBounds,
    (long Min, long Max) MarriedBounds,
    IReadOnlyDictionary<int, IReadOnlyList<long>> PlaneNumbers)
{
    /// <summary>config_ship_break_effect：id → (method, type, value)。method 3 是属性（type 为属性 id），method 1 是技能（type 0）。</summary>
    public IReadOnlyDictionary<int, (int Method, int Type, long Value)> BreakEffects { get; init; } =
        new Dictionary<int, (int Method, int Type, long Value)>();

    /// <summary>config_pskill_dict_group：技能 id → level_value_effect / script_list / param_list。</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<BattleScriptEffect>> PSkillEffects { get; init; } =
        new Dictionary<int, IReadOnlyList<BattleScriptEffect>>();

    /// <summary>config_prop 的 attack_value / attack_coefficient（按 ship_type2 取下标，AttrLogic:GetPowerFromAttr）。只收非空的属性。</summary>
    public IReadOnlyDictionary<int, (IReadOnlyList<long> Value, IReadOnlyList<long> Coefficient)> AttackPower { get; init; } =
        new Dictionary<int, (IReadOnlyList<long> Value, IReadOnlyList<long> Coefficient)>();

    /// <summary>config_parameter 176 attack_score_coefficient（火力评分 = ⌊系数 / 10000 × 攻击战力⌋）。</summary>
    public long AttackScoreCoefficient { get; init; } = 1250;
}

/// <summary>一条按脚本计算强度的加成：config_value_effect id、脚本名（ValueEffectScript_n）与参数。</summary>
internal sealed record BattleScriptEffect(int EffectId, string Script, IReadOnlyList<double> Params);

/// <summary>一件装备：所在装备栏（0 起）、模板、强化等级、config_equip 的 equip_prop / enhance_prop 与 ewt_id。</summary>
internal sealed record BattleEquipInput(
    int Slot,
    int TemplateId,
    int EnhanceLv,
    IReadOnlyList<IReadOnlyList<long>> EquipProp,
    IReadOnlyList<IReadOnlyList<long>> EnhanceProp,
    IReadOnlyList<long> EwtIds);

/// <summary>
/// 一艘出战舰船的输入。
/// </summary>
/// <param name="Fields">config_ship_main 那一行的数值字段，按配置字段名（1 级数值与 _levelup）。</param>
/// <param name="Overrides">
/// 直接取用、不再成长的字段值：临时舰船 config_assist_ship_info 里不是 -1 的字段（-1 表示按 config_ship_main 在该等级的值，
/// 与客户端 NpcAssistFleetManager:FixNpcAttr 一致）。自有舰船为空。
/// </param>
/// <param name="Level">舰船等级。</param>
/// <param name="LevelGrowth">是否按等级成长（没有 config_ship_main 行、只用 Overrides 时为 false）。</param>
/// <param name="ScoutNum">侦察机数（属性 5），沿用离线版：carry_plane_count，没有时 1。</param>
/// <param name="FlatAdds">直接加到基础属性上的值：强化（Intensify）与改造（remould_effect_type 2）；只有 <see cref="BattleStats.BattleAttrs"/> 里的属性会下发。</param>
/// <param name="HasAffection">是否按好感加成（临时舰船没有好感）。</param>
/// <param name="Affection">好感度。</param>
/// <param name="Married">是否誓约（MarryTime ≠ 0）。</param>
/// <param name="BreakValueEffects">config_ship_break.value_effect_id_list 与 value_effect_power_list。</param>
/// <param name="PlaneNumbers">config_ship_equip.plane_number。</param>
/// <param name="Equips">装备。</param>
internal sealed record BattleShipInput(
    IReadOnlyDictionary<string, double> Fields,
    IReadOnlyDictionary<string, double> Overrides,
    int Level,
    bool LevelGrowth,
    long ScoutNum,
    IReadOnlyList<(int Attr, long Value)> FlatAdds,
    bool HasAffection,
    long Affection,
    bool Married,
    IReadOnlyList<(int EffectId, double Power)> BreakValueEffects,
    IReadOnlyList<long> PlaneNumbers,
    IReadOnlyList<BattleEquipInput> Equips)
{
    /// <summary>实验「完整战斗属性」（--exp-full-battle-stats）的额外输入；null 时只能用 <see cref="BattleStats.Compute"/>。</summary>
    public BattleFullInput? Full { get; init; }
}

/// <summary>实验「完整战斗属性」（--exp-full-battle-stats）的额外输入。</summary>
/// <param name="BreakEffects">config_ship_break.ship_break_effect_id_list 里 method 3 的（属性，值），按 CreateNpcShip4Battle 加进 Attr。</param>
/// <param name="ScoreBuffs">只影响火力评分（3101）的加成，强度已按脚本算好：技能等级、等级（level_value_effect）、强化（config_ship_max_power）。</param>
/// <param name="ShipType2">config_ship_main.ship_type2（GetPowerFromAttr 的系数下标）。</param>
/// <param name="AttackScore">是否下发火力评分（自有舰船）。临时舰船与客户端 CreateNpcShip4Battle 一样不下发。</param>
/// <param name="SourceFields">临时舰船：config_assist_ship_info 里有的字段（含 -1）；详情页属性只下发这些字段对应的。自有舰船为 null。</param>
/// <param name="PSkills">临时舰船：ship_main.direct_activate_talent_id 与 ship_skill_level；null 表示沿用原来的技能。</param>
internal sealed record BattleFullInput(
    IReadOnlyList<(int Type, long Value)> BreakEffects,
    IReadOnlyList<(int EffectId, double Power)> ScoreBuffs,
    long ShipType2,
    bool AttackScore,
    IReadOnlySet<string>? SourceFields,
    IReadOnlyList<(int PSkillId, int Level)>? PSkills);

/// <summary>一件装备的战斗数据（TBattleEquip）：模板、舰载机数与属性。</summary>
internal sealed record BattleEquipStats(int TemplateId, long PlaneNum, IReadOnlyList<(int Attr, long Value)> Attrs);

/// <summary>一艘舰船的战斗数据：TBattleShip.Attr 与各装备（顺序同输入）。</summary>
internal sealed record BattleShipStats(IReadOnlyList<(int Attr, long Value)> Attrs, IReadOnlyList<BattleEquipStats> Equips)
{
    /// <summary>非 null 时替换下发的 TFiledPSkillLv（实验模式下的临时舰船）。</summary>
    public IReadOnlyList<(int PSkillId, int Level)>? PSkills { get; init; }
}

/// <summary>
/// 关闭「战斗数值」作弊（--cheat-battle=off）时 copy.StartBase 下发的舰船战斗属性：与客户端舰娘详情页一致。
/// 按日服 1.4.0 的 logic.AttrLogic 计算（HeroBasicAttr / HeroAttr / AttrLogic:GetHeroFianlAttr / attrdisplay.lua），纯函数。
/// <list type="bullet">
/// <item>基础值：config_ship_main 字段 + ⌊(attribute_level − 1) × 字段_levelup / 100⌋（客户端 HeroBasicAttr）。
/// 离线版原来按 字段 + 字段_levelup × (等级 − 1) 计算，_levelup 实际是百分之一单位，所以 24 级的 Z39 耐久是 26136 而不是 1339。</item>
/// <item>加上强化等级（Intensify）与改造的属性节点。</item>
/// <item>装备属性 = equip_prop + enhance_prop × 强化等级，放在 TBattleEquip.AttrValue 里（客户端 NpcAssistFleetManager 构造战斗舰船时也这样分开放），
/// 舰载机数按 config_ship_equip.plane_number 与突破的「每组 +N 架」，不再一律 100。</item>
/// <item>好感/誓约的百分比加成（属性 138～145）按详情页的 basicattr_display 折算进耐久、火力等，不单独下发百分比属性。</item>
/// </list>
/// 只下发 <see cref="BattleAttrs"/> 里的属性：离线版一直下发的那些，加上详情页的对空（12）与制空（16）。
/// 主炮射程、装填、鱼雷数、速力等其余详情页属性离线版从未下发、战斗端用的单位未核实（临时舰船表里速力是 20 左右、舰船表是 2000 左右），
/// 所以不下发，突破的主炮装填 −N 秒、鱼雷 +1 也就没有复现。
/// 详情页同样计入、但这里没有复现的：浴场 buff（只影响暴击/命中/伤害增减，不在详情页数值里）、舰船结合（Combine）、天赋（服务端从未推送天赋属性，详情页也没有）。
/// </summary>
internal static class BattleStats
{
    private const int ScoutNumAttr = 5;
    private const int PercentBase = 10000;
    private const int PercentValueType = 0;

    /// <summary>
    /// 下发的属性：耐久 1、侦察机 5、火力 8、装甲 9、雷装 10、对雷 11、对空 12、对舰 14、舰攻 15、制空 16、暴击 17、抗暴 18、命中 19、闪避 20。
    /// </summary>
    internal static readonly IReadOnlySet<int> BattleAttrs = new HashSet<int> { 1, 5, 8, 9, 10, 11, 12, 14, 15, 16, 17, 18, 19, 20 };

    /// <summary>
    /// 实验模式另外下发的详情页属性（单位与临时舰船表、战斗属性表 config_prop 一致）：主炮射程 21（config_battle_range id）、
    /// 主炮装填 24（毫秒）、鱼雷数 25、航空射程 47（取 main_gun_range）、潜水值 210。
    /// </summary>
    internal static readonly IReadOnlySet<int> FullRawAttrs = new HashSet<int> { 21, 24, 25, 47, 210 };

    /// <summary>
    /// 实验模式下突破的修正属性，按战斗属性表的含义单独下发（不折算）：主炮 CD 偏移秒 81、鱼雷数偏移 82、备用飞机 84、
    /// 战斗机/鱼雷机/轰炸机每组偏移 88/89/90（舰载机数本身仍是 TBattleEquip.PlaneNum）。
    /// </summary>
    internal static readonly IReadOnlySet<int> DeltaAttrs = new HashSet<int> { 81, 82, 84, 88, 89, 90 };

    /// <summary>
    /// 实验模式不下发的属性：速力 27（自有舰船；单位未定，见 <see cref="ComputeFull"/>）、鱼雷射程 39（舰船表只有 0/2，
    /// 48 艘带鱼雷的船是 0，临时舰船表没有这个字段，客户端从不下发）、62/63（详情页是备用机/航空攻击间隔，
    /// 战斗属性表里却是 PrjFillingDelayTime / OriginalBaseOdds）、213（舰船表没有这个字段，详情页恒为 0）、
    /// 138～145（已折算进主属性）、3100～3299（战力评分，3101 另算）。
    /// </summary>
    private static bool FullExcluded(int attr) => attr is 27 or 39 or 62 or 63 or 213 or (>= 138 and <= 145) or (>= 3100 and < 3300);

    private const int SpeedAttr = 27;
    private const int AttackScoreAttr = 3101;

    /// <summary>离线版一直下发、但不在详情页上的属性（舰攻、暴击、抗暴、命中、闪避），保留。</summary>
    private static readonly (int Attr, string Field)[] Extras =
        [(15, "ship_torpedo_attack"), (17, "crit"), (18, "anti_crit"), (19, "hit"), (20, "dodge")];

    /// <summary>EquipLogic:_getPlaneAttr 的 extraMap：ewt_id 战斗机 18 / 鱼雷机 20 / 轰炸机 19 → 属性 88 / 89 / 90。</summary>
    private static readonly Dictionary<long, int> PlaneAttrByEwt = new() { [18] = 88, [20] = 89, [19] = 90 };

    /// <summary>客户端 HeroBasicAttr 的等级成长系数 attribute_level − 1；没有该等级配置时为 0。</summary>
    internal static int GrowthFactor(BattleStatsCatalog catalog, int level)
        => catalog.AttributeLevels.TryGetValue(level, out int attributeLevel) ? attributeLevel - 1 : 0;

    /// <summary>客户端 HeroBasicAttr：base + ⌊factor × (levelup / 100)⌋。</summary>
    internal static double Grown(double baseValue, double levelup, int factor)
        => baseValue + Math.Floor(factor * (levelup / 100));

    /// <summary>MarryLogic:GetLoveInfo（非秘书舰）：按是否誓约把好感夹到上下限后找所在的档。</summary>
    internal static AffectionTier? Tier(BattleStatsCatalog catalog, long affection, bool married)
    {
        long love = affection;
        foreach (AffectionTier tier in catalog.AffectionTiers)
        {
            if (tier.Married != married) continue;
            (long min, long max) = married ? catalog.MarriedBounds : catalog.UnmarriedBounds;
            love = Math.Clamp(love, min, Math.Max(min, max));
            if (love >= tier.Min && love <= tier.Max) return tier;
        }
        return null;
    }

    /// <summary>AttrLogic:DisposeAttrBuff：万分比属性 ⌊值 × 10000⌋，再乘 ⌊强度⌋，累加。</summary>
    internal static void AddBuffs(BattleStatsCatalog catalog, Dictionary<int, double> into,
        IEnumerable<(int EffectId, double Power)> buffs)
    {
        foreach ((int effectId, double power) in buffs)
        {
            if (!catalog.ValueEffects.TryGetValue(effectId, out IReadOnlyList<(int Attr, double Value)>? values)) continue;
            foreach ((int attr, double raw) in values)
            {
                double value = raw;
                if (catalog.PropValueTypes.TryGetValue(attr, out int type) && type == PercentValueType)
                    value = Math.Floor(value * PercentBase);
                value *= Math.Floor(power);
                into[attr] = into.GetValueOrDefault(attr) + value;
            }
        }
    }

    /// <summary>EquipLogic:GetCurEquipProperty：equip_prop 的值 + 同属性 enhance_prop × 强化等级，只在 enhance_prop 里的属性为 enhance × 等级，去掉 0。</summary>
    internal static IReadOnlyList<(int Attr, long Value)> EquipAttrs(BattleEquipInput equip)
    {
        var enhance = new Dictionary<int, long>();
        foreach (IReadOnlyList<long> prop in equip.EnhanceProp)
            if (prop.Count >= 2) enhance[checked((int)prop[0])] = prop[1];
        var values = new Dictionary<int, long>();
        var order = new List<int>();
        var enhanced = new HashSet<int>();
        void Set(int attr, long value)
        {
            if (!values.ContainsKey(attr)) order.Add(attr);
            values[attr] = value;
        }
        foreach (IReadOnlyList<long> prop in equip.EquipProp)
        {
            if (prop.Count < 2) continue;
            int attr = checked((int)prop[0]);
            long add = 0;
            if (enhance.TryGetValue(attr, out long perLevel))
            {
                add = perLevel * equip.EnhanceLv;
                enhanced.Add(attr);
            }
            Set(attr, prop[1] + add);
        }
        foreach ((int attr, long perLevel) in enhance)
            if (!enhanced.Contains(attr)) Set(attr, perLevel * equip.EnhanceLv);
        return order.Where(attr => values[attr] != 0).Select(attr => (attr, values[attr])).ToList();
    }

    /// <summary>详情页计算的中间结果。</summary>
    private sealed record Core(
        SortedSet<int> Present,
        Dictionary<int, string> FieldOf,
        List<IReadOnlyList<(int Attr, long Value)>> EquipAttrs,
        Dictionary<int, double> EquipFlat,
        Dictionary<int, double> BreakBuff,
        Dictionary<int, double> Final,
        Dictionary<int, int> PercentOf);

    private static Core Calculate(BattleStatsCatalog catalog, BattleShipInput ship)
    {
        int factor = ship.LevelGrowth ? GrowthFactor(catalog, ship.Level) : 0;
        var basic = new Dictionary<int, double>();
        var present = new SortedSet<int>();
        var fieldOf = new Dictionary<int, string>();
        void AddField(int attr, string field)
        {
            if (!ship.Overrides.TryGetValue(field, out double value))
            {
                if (!ship.Fields.TryGetValue(field, out value)) return;
                if (ship.LevelGrowth && ship.Fields.TryGetValue(field + "_levelup", out double levelup))
                    value = Grown(value, levelup, factor);
            }
            basic[attr] = basic.GetValueOrDefault(attr) + value;
            present.Add(attr);
            fieldOf.TryAdd(attr, field);
        }
        foreach (BattleShowAttr show in catalog.ShowAttrs) AddField(show.Id, show.Field);
        foreach ((int attr, string field) in Extras) AddField(attr, field);
        basic[ScoutNumAttr] = ship.ScoutNum;
        present.Add(ScoutNumAttr);
        foreach ((int attr, long value) in ship.FlatAdds)
        {
            basic[attr] = basic.GetValueOrDefault(attr) + value;
            present.Add(attr);
        }

        var equipAttrs = ship.Equips.Select(EquipAttrs).ToList();
        var equipFlat = new Dictionary<int, double>();
        foreach (IReadOnlyList<(int Attr, long Value)> attrs in equipAttrs)
            foreach ((int attr, long value) in attrs)
                equipFlat[attr] = equipFlat.GetValueOrDefault(attr) + value;

        var breakBuff = new Dictionary<int, double>();
        AddBuffs(catalog, breakBuff, ship.BreakValueEffects);
        var buff = new Dictionary<int, double>(breakBuff);
        if (ship.HasAffection && Tier(catalog, ship.Affection, ship.Married) is { } tier)
            AddBuffs(catalog, buff, tier.Effects);

        var final = new Dictionary<int, double>();
        foreach (Dictionary<int, double> part in new[] { basic, equipFlat, buff })
            foreach ((int attr, double value) in part)
                final[attr] = final.GetValueOrDefault(attr) + value;

        // basicattr_display：详情页 = ⌈(基础 + 装备 + 加成) × (1 + 百分比 / 10000)⌉。装备部分另在 TBattleEquip 里下发，这里减掉。
        var percentOf = new Dictionary<int, int>();
        foreach (BattleShowAttr show in catalog.ShowAttrs)
            if (show.Display == "basicattr_display" && show.Params.Count >= 2 && show.Params[0] == show.Id)
                percentOf.TryAdd(show.Id, show.Params[1]);
        return new Core(present, fieldOf, equipAttrs, equipFlat, breakBuff, final, percentOf);
    }

    /// <summary>某属性在战斗里的值：basicattr_display 的属性按详情页折算百分比，再减去另在装备里下发的部分。</summary>
    private static long BattleValue(Core core, int attr)
    {
        double total = core.Final.GetValueOrDefault(attr);
        double shown = core.PercentOf.TryGetValue(attr, out int percentAttr)
            ? Math.Ceiling(total * (1 + core.Final.GetValueOrDefault(percentAttr) / PercentBase))
            : total;
        return checked((long)Math.Round(shown - core.EquipFlat.GetValueOrDefault(attr)));
    }

    private static long BaseSlotPlanes(BattleShipInput ship, BattleEquipInput equip)
        => equip.Slot >= 0 && equip.Slot < ship.PlaneNumbers.Count ? ship.PlaneNumbers[equip.Slot] : 0;

    /// <summary>按详情页计算一艘舰船的战斗属性与装备。</summary>
    internal static BattleShipStats Compute(BattleStatsCatalog catalog, BattleShipInput ship)
    {
        Core core = Calculate(catalog, ship);
        var attrsOut = new List<(int Attr, long Value)>();
        foreach (int attr in core.Present)
            if (BattleAttrs.Contains(attr)) attrsOut.Add((attr, BattleValue(core, attr)));

        var equipsOut = new List<BattleEquipStats>();
        for (int i = 0; i < ship.Equips.Count; i++)
        {
            BattleEquipInput equip = ship.Equips[i];
            long baseNum = BaseSlotPlanes(ship, equip);
            long planeNum = baseNum;
            bool isPlane = false;
            long planes = 0;
            foreach (long ewt in equip.EwtIds)
            {
                if (!PlaneAttrByEwt.TryGetValue(ewt, out int planeAttr)) continue;
                isPlane = true;
                planes += baseNum + checked((long)Math.Round(core.BreakBuff.GetValueOrDefault(planeAttr)));
            }
            if (isPlane) planeNum = planes;
            equipsOut.Add(new BattleEquipStats(equip.TemplateId, planeNum, core.EquipAttrs[i]));
        }
        return new BattleShipStats(attrsOut, equipsOut);
    }

    /// <summary>
    /// 实验「完整战斗属性」（--exp-full-battle-stats）：在 <see cref="Compute"/> 之上按客户端自己构造战斗舰船的方式
    /// （NpcAssistFleetManager:CreateNpcShip4Battle）补上其余详情页属性与突破效果。
    /// <list type="bullet">
    /// <item><see cref="BattleAttrs"/> 与 <see cref="Compute"/> 完全相同。</item>
    /// <item><see cref="FullRawAttrs"/>：HeroBasicAttr 的值（临时舰船取 config_assist_ship_info，-1 按 config_ship_main；表里没有的字段不发），不折算。</item>
    /// <item><see cref="DeltaAttrs"/>：突破的 value_effect（config_ship_break.value_effect_id_list）单独下发；
    /// 舰载机每组偏移下发后，TBattleEquip.PlaneNum 只给 plane_number，不再加突破的架数。</item>
    /// <item>突破效果（ship_break_effect_id_list 中 method 3）：Attr[type] += value，与 CreateNpcShip4Battle 一样；method 1 的技能不下发。</item>
    /// <item>火力评分 3101（自有舰船）：AttrLogic:GetHeroFianlAttr 的 ⌊系数 / 10000 × ⌊Σ3200 × (1 + Σ3201 / 10000)⌋⌋。</item>
    /// <item>速力 27 只给临时舰船（原样取 config_assist_ship_info.speed，15～25，与客户端相同）；舰船表的 speed 约是它的 100 倍、
    /// 敌舰表也是同一量级，战斗端读的是哪一种没有证据，所以自有舰船不下发。</item>
    /// </list>
    /// </summary>
    internal static BattleShipStats ComputeFull(BattleStatsCatalog catalog, BattleShipInput ship)
    {
        BattleFullInput full = ship.Full ?? throw new ArgumentException("ComputeFull needs BattleShipInput.Full", nameof(ship));
        Core core = Calculate(catalog, ship);
        var attrs = new SortedDictionary<int, long>();
        foreach (int attr in core.Present)
        {
            bool fromSource = full.SourceFields is null || !core.FieldOf.TryGetValue(attr, out string? field) ||
                              full.SourceFields.Contains(field);
            if (BattleAttrs.Contains(attr))
                attrs[attr] = BattleValue(core, attr);
            else if (attr == SpeedAttr)
            {
                if (ship.Overrides.TryGetValue("speed", out double speed)) attrs[attr] = checked((long)Math.Round(speed));
            }
            else if (FullRawAttrs.Contains(attr))
            {
                if (fromSource) attrs[attr] = BattleValue(core, attr);
            }
            else if (!FullExcluded(attr) && !DeltaAttrs.Contains(attr) && !core.FieldOf.ContainsKey(attr))
                attrs[attr] = BattleValue(core, attr); // 改造节点的伤害增减等（FlatAdds），战斗属性表里同名
        }

        foreach (int attr in DeltaAttrs)
        {
            long delta = checked((long)Math.Round(core.BreakBuff.GetValueOrDefault(attr)));
            if (delta != 0) attrs[attr] = attrs.GetValueOrDefault(attr) + delta;
        }
        foreach ((int type, long value) in full.BreakEffects)
            if (type > 0) attrs[type] = attrs.GetValueOrDefault(type) + value;

        if (full.AttackScore)
        {
            var scoreAttrs = new Dictionary<int, double>(core.Final);
            AddBuffs(catalog, scoreAttrs, full.ScoreBuffs);
            attrs[AttackScoreAttr] = AttackScore(catalog, scoreAttrs, full.ShipType2);
        }

        var equipsOut = new List<BattleEquipStats>();
        for (int i = 0; i < ship.Equips.Count; i++)
            equipsOut.Add(new BattleEquipStats(ship.Equips[i].TemplateId, BaseSlotPlanes(ship, ship.Equips[i]), core.EquipAttrs[i]));
        return new BattleShipStats(attrs.Select(kv => (kv.Key, kv.Value)).ToList(), equipsOut) { PSkills = full.PSkills };
    }

    /// <summary>
    /// attrdisplay.lua 的 ValueEffectScript_n：1 是 params[1] + params[2] × (level − 1)；2～6 是 ⌈强化等级表[n − 1] × params[1]⌉
    /// （config_ship_max_power，强化等级表按 max_power_prop 的顺序）。未知脚本为 0。
    /// </summary>
    internal static double ScriptPower(string? script, IReadOnlyList<double> parameters, double level, IReadOnlyList<double> levels)
    {
        double P(int i) => i < parameters.Count ? parameters[i] : 0;
        if (script == "ValueEffectScript_1") return P(0) + P(1) * (level - 1);
        if (script is { Length: > 18 } && script.StartsWith("ValueEffectScript_", StringComparison.Ordinal) &&
            int.TryParse(script.AsSpan(18), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n is >= 2 and <= 6)
            return Math.Ceiling((n - 2 < levels.Count ? levels[n - 2] : 0) * P(0));
        return 0;
    }

    /// <summary>
    /// AttrLogic:GetPowerFromAttr 的攻击战力与火力评分：atk = ⌊Σ值 × attack_value[类型] × (1 + Σ值 / 10000 × attack_coefficient[类型])⌋，
    /// 评分 = ⌊attack_score_coefficient / 10000 × atk⌋。
    /// </summary>
    internal static long AttackScore(BattleStatsCatalog catalog, IReadOnlyDictionary<int, double> attrs, long shipType2)
    {
        double basePower = 0, percentPower = 0;
        foreach ((int attr, double value) in attrs)
        {
            if (!catalog.AttackPower.TryGetValue(attr, out (IReadOnlyList<long> Value, IReadOnlyList<long> Coefficient) power)) continue;
            if (shipType2 > 0 && shipType2 <= power.Value.Count) basePower += value * power.Value[(int)shipType2 - 1];
            if (shipType2 > 0 && shipType2 <= power.Coefficient.Count)
                percentPower += value * 1.0 / PercentBase * power.Coefficient[(int)shipType2 - 1];
        }
        double attackPower = Math.Floor(basePower * (1 + percentPower));
        return checked((long)Math.Floor(catalog.AttackScoreCoefficient * 1.0 / PercentBase * attackPower));
    }

    /// <summary>配置行（config_ship_main / config_assist_ship_info）的数值字段，按配置字段名（JsonPropertyName）。</summary>
    internal static IReadOnlyDictionary<string, double> Fields(object configRow)
    {
        var fields = new Dictionary<string, double>(StringComparer.Ordinal);
        JsonElement element = JsonSerializer.SerializeToElement(configRow, configRow.GetType());
        foreach (JsonProperty property in element.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out double value))
                fields[property.Name] = value;
        return fields;
    }
}

/// <summary>加载 <see cref="BattleStatsCatalog"/>（config_attribute、config_ship_levelup、config_prop、config_value_effect、
/// config_affection_favor、config_parameter 155/156、config_ship_equip）。</summary>
internal static class BattleStatsLoader
{
    private static BattleStatsCatalog? _catalog;

    /// <summary>已加载的配置；没加载或配置缺失（没有任何详情页属性）时为 null，调用方回退到离线版原来的下发。</summary>
    internal static BattleStatsCatalog? Catalog => _catalog;

    internal static void Load(string configDir)
    {
        if (_catalog is not null) return;
        try
        {
            BattleStatsCatalog catalog = Build(configDir);
            if (catalog.ShowAttrs.Count > 0 && catalog.AttributeLevels.Count > 0) _catalog = catalog;
        }
        catch (Exception)
        {
            // 与其他配置加载器一致：缺表或坏表不让服务端启动失败，出击时回退到离线版原来的下发。
            _catalog = null;
        }
    }

    private static BattleStatsCatalog Build(string configDir)
    {
        var showAttrs = ConfigDbLoader.LoadAll<ConfigAttribute>(configDir, "config_attribute.db").Values
            .Where(attr => attr.GirlIfShow == 1 && attr.Id > 0 && !string.IsNullOrEmpty(attr.Beizhu))
            .OrderBy(attr => attr.Id)
            .Select(attr => new BattleShowAttr(checked((int)attr.Id), attr.Beizhu!, attr.AttrDisplay ?? "",
                (attr.Params ?? []).Select(value => checked((int)value)).ToList()))
            .ToList();

        var attributeLevels = new Dictionary<int, int>();
        foreach (ConfigShipLevelup row in ConfigDbLoader.LoadAll<ConfigShipLevelup>(configDir, "config_ship_levelup.db").Values)
            if (row.Level > 0) attributeLevels[checked((int)row.Level)] = checked((int)row.AttributeLevel);

        var propTypes = ConfigDbLoader.LoadAll<ConfigProp>(configDir, "config_prop.db")
            .ToDictionary(kv => kv.Key, kv => checked((int)kv.Value.PropValueType));

        var valueEffects = new Dictionary<int, IReadOnlyList<(int Attr, double Value)>>();
        foreach ((int id, ConfigValueEffect effect) in ConfigDbLoader.LoadAll<ConfigValueEffect>(configDir, "config_value_effect.db"))
            valueEffects[id] = ParseValues(effect.Values);

        var tiers = ConfigDbLoader.LoadAll<ConfigAffectionFavor>(configDir, "config_affection_favor.db")
            .Where(kv => kv.Value.AffectionId > 0)
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                ConfigAffectionFavor row = kv.Value;
                List<long> ids = row.AffectionValueEffect ?? [];
                List<long> powers = row.ValueEffectPower ?? [];
                var effects = ids.Select((id, i) => (checked((int)id), i < powers.Count ? (double)powers[i] : 0d)).ToList();
                return new AffectionTier(checked((int)row.AffectionId), row.AffectionMarry == 1, row.AffectionMin, row.AffectionMax, effects);
            })
            .ToList();

        var bounds = new Dictionary<int, (long Min, long Max)>();
        ConfigDbLoader.LoadRows(configDir, "config_parameter.db", (id, _, json) =>
        {
            if (id is not (155 or 156)) return;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("arrValue", out JsonElement array) && array.ValueKind == JsonValueKind.Array &&
                    array.GetArrayLength() >= 2 && array[0].TryGetInt64(out long min) && array[1].TryGetInt64(out long max))
                    bounds[id] = (min, max);
            }
            catch (JsonException) { }
        });

        var planeNumbers = ConfigDbLoader.LoadAll<ConfigShipEquip>(configDir, "config_ship_equip.db")
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<long>)(kv.Value.PlaneNumber ?? []));

        var breakEffects = ConfigDbLoader.LoadAll<ConfigShipBreakEffect>(configDir, "config_ship_break_effect.db")
            .ToDictionary(kv => kv.Key, kv => (checked((int)kv.Value.Method), checked((int)kv.Value.Type), kv.Value.Value));

        var pskillEffects = new Dictionary<int, IReadOnlyList<BattleScriptEffect>>();
        ConfigDbLoader.LoadRows(configDir, "config_pskill_dict_group.db", (id, _, json) =>
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                List<BattleScriptEffect> effects = ScriptEffects(doc.RootElement);
                if (effects.Count > 0) pskillEffects[id] = effects;
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        });

        var attackPower = new Dictionary<int, (IReadOnlyList<long> Value, IReadOnlyList<long> Coefficient)>();
        foreach ((int id, ConfigProp prop) in ConfigDbLoader.LoadAll<ConfigProp>(configDir, "config_prop.db"))
            if (prop.AttackValue is { Count: > 0 } || prop.AttackCoefficient is { Count: > 0 })
                attackPower[id] = (prop.AttackValue ?? [], prop.AttackCoefficient ?? []);

        long attackScoreCoefficient = 1250;
        ConfigDbLoader.LoadRows(configDir, "config_parameter.db", (id, _, json) =>
        {
            if (id != 176) return;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("value", out JsonElement value) && value.TryGetInt64(out long coefficient))
                    attackScoreCoefficient = coefficient;
            }
            catch (JsonException) { }
        });

        return new BattleStatsCatalog(showAttrs, attributeLevels, propTypes, valueEffects, tiers,
            bounds.GetValueOrDefault(155, (0, 1_000_000)), bounds.GetValueOrDefault(156, (1_000_000, 2_000_000)), planeNumbers)
        {
            BreakEffects = breakEffects,
            PSkillEffects = pskillEffects,
            AttackPower = attackPower,
            AttackScoreCoefficient = attackScoreCoefficient,
        };
    }

    /// <summary>一行配置的 level_value_effect / script_list / param_list（参数按 double 读，可能是小数）。</summary>
    private static List<BattleScriptEffect> ScriptEffects(JsonElement row)
    {
        var effects = new List<BattleScriptEffect>();
        if (!row.TryGetProperty("level_value_effect", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array) return effects;
        row.TryGetProperty("script_list", out JsonElement scripts);
        row.TryGetProperty("param_list", out JsonElement parameters);
        for (int i = 0; i < ids.GetArrayLength(); i++)
        {
            if (!ids[i].TryGetInt32(out int effectId)) continue;
            string script = scripts.ValueKind == JsonValueKind.Array && i < scripts.GetArrayLength() ? scripts[i].GetString() ?? "" : "";
            var values = new List<double>();
            if (parameters.ValueKind == JsonValueKind.Array && i < parameters.GetArrayLength() &&
                parameters[i].ValueKind == JsonValueKind.Array)
                foreach (JsonElement value in parameters[i].EnumerateArray())
                    if (value.TryGetDouble(out double number)) values.Add(number);
            effects.Add(new BattleScriptEffect(effectId, script, values));
        }
        return effects;
    }

    /// <summary>config_value_effect.values：「属性,值|属性,值」。</summary>
    internal static IReadOnlyList<(int Attr, double Value)> ParseValues(string? values)
    {
        var result = new List<(int, double)>();
        foreach (string part in (values ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split(',');
            if (pair.Length >= 2 &&
                int.TryParse(pair[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int attr) &&
                double.TryParse(pair[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                result.Add((attr, value));
        }
        return result;
    }
}

/// <summary>从存档与已加载的配置（<see cref="ShipMainLoader"/>、<see cref="ShipBreakLoader"/>、<see cref="RemouldConfigLoader"/>、
/// <see cref="EquipLoader"/>）组装 <see cref="BattleShipInput"/>。</summary>
internal static class BattleStatsInputs
{
    private static readonly IReadOnlyDictionary<string, double> NoFields = new Dictionary<string, double>();

    /// <summary>
    /// 自有舰船。没有 config_ship_main 行时返回 null（调用方回退到离线版原来的下发）。
    /// <paramref name="full"/> 时附上实验模式的突破效果与火力评分输入（<see cref="BattleFullInput"/>）。
    /// </summary>
    internal static BattleShipInput? ForHero(BattleStatsCatalog catalog, Hero hero, IReadOnlyDictionary<uint, EquipItem> equipById,
        bool full = false)
    {
        ConfigShipMain? main = ShipMainLoader.Get(hero.TemplateId);
        if (main is null) return null;
        var flat = new List<(int Attr, long Value)>();
        foreach (AttrIntensify intensify in hero.Intensify ?? [])
            if (intensify.AttrType != 0 && intensify.IntensifyLvl != 0) flat.Add((intensify.AttrType, intensify.IntensifyLvl));
        // RemouldLogic:CountFinalAttr：已完成改造节点里 remould_effect_type 为 [2, 属性, 值] 的项。
        foreach (int effectId in hero.RemouldEffects ?? [])
            foreach (List<long> effect in RemouldConfigLoader.GetEffect(effectId)?.RemouldEffectType ?? [])
                if (effect is { Count: >= 3 } && effect[0] == 2) flat.Add((checked((int)effect[1]), effect[2]));
        var equips = new List<BattleEquipInput>();
        IReadOnlyList<uint> slots = hero.EquipSlots ?? [];
        for (int slot = 0; slot < slots.Count; slot++)
        {
            if (slots[slot] == 0 || !equipById.TryGetValue(slots[slot], out EquipItem? item)) continue;
            if (Equip(slot, item.TemplateId, item.EnhanceLv) is { } equip) equips.Add(equip);
        }
        var input = new BattleShipInput(BattleStats.Fields(main), NoFields, hero.Level, LevelGrowth: true, ScoutNum(main), flat,
            HasAffection: true, hero.Affection, Married: hero.MarryTime != 0, BreakValueEffects(hero.TemplateId),
            catalog.PlaneNumbers.GetValueOrDefault(hero.TemplateId) ?? [], equips);
        if (!full) return input;
        return input with
        {
            Full = new BattleFullInput(BreakEffects(catalog, hero.TemplateId), ScoreBuffs(catalog, hero, main), main.ShipType2,
                AttackScore: true, SourceFields: null, PSkills: null),
        };
    }

    /// <summary>
    /// 只影响火力评分的加成（ShipLogic.GetHeroAttrBuff）：技能等级（config_pskill_dict_group，等级 = PSkill.Level）、
    /// 舰船等级（config_ship_main.level_value_effect，等级 = attribute_level）、强化（config_ship_max_power，按 max_power_prop 顺序的强化等级）。
    /// 装备技能（服务端装备没有 PSkillList）、浴场与结合不计。
    /// </summary>
    internal static IReadOnlyList<(int EffectId, double Power)> ScoreBuffs(BattleStatsCatalog catalog, Hero hero, ConfigShipMain main)
    {
        var buffs = new List<(int EffectId, double Power)>();
        var skillLevels = new Dictionary<int, int>();
        foreach (PSkillEntry skill in hero.PSkills ?? []) skillLevels[checked((int)skill.PSkillId)] = skill.Level;
        foreach ((int skillId, int level) in skillLevels)
            if (catalog.PSkillEffects.TryGetValue(skillId, out IReadOnlyList<BattleScriptEffect>? effects))
                foreach (BattleScriptEffect effect in effects)
                    buffs.Add((effect.EffectId, BattleStats.ScriptPower(effect.Script, effect.Params, level, [])));

        int attributeLevel = catalog.AttributeLevels.GetValueOrDefault(hero.Level);
        List<long> levelEffects = main.LevelValueEffect ?? [];
        for (int i = 0; i < levelEffects.Count; i++)
            buffs.Add((checked((int)levelEffects[i]), BattleStats.ScriptPower(At(main.ScriptList, i), At(main.ParamList, i) ?? [],
                attributeLevel, [])));

        if (ShipIntensifyLoader.GetMax(hero.TemplateId) is { } maxPower)
        {
            var intensify = new Dictionary<int, double>();
            foreach (AttrIntensify entry in hero.Intensify ?? []) intensify[entry.AttrType] = entry.IntensifyLvl;
            var levels = (maxPower.MaxPowerProp ?? [])
                .Select(prop => prop is { Count: > 0 } ? intensify.GetValueOrDefault(checked((int)prop[0])) : 0d).ToList();
            List<long> powerEffects = maxPower.LevelValueEffect ?? [];
            for (int i = 0; i < powerEffects.Count; i++)
                buffs.Add((checked((int)powerEffects[i]), BattleStats.ScriptPower(At(maxPower.ScriptList, i), At(maxPower.ParamList, i) ?? [],
                    0, levels)));
        }
        return buffs;
    }

    private static T? At<T>(List<T>? list, int index) where T : class => list is not null && index < list.Count ? list[index] : null;

    /// <summary>config_ship_break.ship_break_effect_id_list 中 method 3（属性）的（type，value）。</summary>
    internal static IReadOnlyList<(int Type, long Value)> BreakEffects(BattleStatsCatalog catalog, int templateId)
    {
        var effects = new List<(int Type, long Value)>();
        foreach (long id in ShipBreakLoader.Get(templateId)?.ShipBreakEffectIdList ?? [])
            if (catalog.BreakEffects.TryGetValue(checked((int)id), out (int Method, int Type, long Value) effect) &&
                effect.Method == 3 && effect.Type > 0)
                effects.Add((effect.Type, effect.Value));
        return effects;
    }

    /// <summary>
    /// 剧情关的临时舰船（config_assist_ship_info）：不是 -1 的字段直接取用，-1 的按 config_ship_main 在 ship_level 的值
    /// （NpcAssistFleetManager:FixNpcAttr）；装备按 equip_level 强化。没有好感与突破加成。
    /// </summary>
    internal static BattleShipInput ForAssist(BattleStatsCatalog catalog, ConfigAssistShipInfo assist, bool full = false)
    {
        int templateId = checked((int)assist.ShipMainId);
        ConfigShipMain? main = ShipMainLoader.Get(templateId);
        IReadOnlyDictionary<string, double> assistFields = BattleStats.Fields(assist);
        var overrides = assistFields.Where(kv => kv.Value != -1).ToDictionary(kv => kv.Key, kv => kv.Value);
        var equips = new List<BattleEquipInput>();
        IReadOnlyList<long> ids = assist.Equip ?? [];
        IReadOnlyList<long> levels = assist.EquipLevel ?? [];
        for (int slot = 0; slot < ids.Count; slot++)
        {
            if (ids[slot] == 0) continue;
            int level = slot < levels.Count ? checked((int)levels[slot]) : 0;
            if (Equip(slot, checked((int)ids[slot]), level) is { } equip) equips.Add(equip);
        }
        var input = new BattleShipInput(main is null ? NoFields : BattleStats.Fields(main), overrides, checked((int)assist.ShipLevel),
            LevelGrowth: main is not null, main is null ? 1 : ScoutNum(main), [], HasAffection: false, 0, Married: false, [],
            catalog.PlaneNumbers.GetValueOrDefault(templateId) ?? [], equips);
        if (!full) return input;
        // CreateNpcShip4Battle：技能 = direct_activate_talent_id 与 ship_skill_level（长度不同时客户端报错放弃，这里沿用原来的技能）。
        List<long> talents = main?.DirectActivateTalentId ?? [];
        List<long> talentLevels = assist.ShipSkillLevel ?? [];
        IReadOnlyList<(int PSkillId, int Level)>? skills = talents.Count > 0 && talents.Count == talentLevels.Count
            ? talents.Select((id, i) => (checked((int)id), talentLevels[i] > 0 ? checked((int)talentLevels[i]) : 1)).ToList()
            : null;
        return input with
        {
            Full = new BattleFullInput(BreakEffects(catalog, templateId), [], main?.ShipType2 ?? 0, AttackScore: false,
                SourceFields: assistFields.Keys.ToHashSet(StringComparer.Ordinal), PSkills: skills),
        };
    }

    /// <summary>离线版的侦察机数：carry_plane_count，没有时 1。</summary>
    private static long ScoutNum(ConfigShipMain main) => main.CarryPlaneCount > 0 ? main.CarryPlaneCount : 1;

    private static IReadOnlyList<(int EffectId, double Power)> BreakValueEffects(int templateId)
    {
        ConfigShipBreak? stage = ShipBreakLoader.Get(templateId);
        List<long> ids = stage?.ValueEffectIdList ?? [];
        List<long> powers = stage?.ValueEffectPowerList ?? [];
        return ids.Select((id, i) => (checked((int)id), i < powers.Count ? (double)powers[i] : 0d)).ToList();
    }

    private static BattleEquipInput? Equip(int slot, int templateId, int enhanceLv)
    {
        ConfigEquip? config = EquipLoader.Get(templateId);
        if (config is null) return null;
        return new BattleEquipInput(slot, templateId, enhanceLv,
            (config.EquipProp ?? []).Select(prop => (IReadOnlyList<long>)prop).ToList(),
            (config.EnhanceProp ?? []).Select(prop => (IReadOnlyList<long>)prop).ToList(),
            config.EwtId ?? []);
    }
}
