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
    IReadOnlyDictionary<int, IReadOnlyList<long>> PlaneNumbers);

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
    IReadOnlyList<BattleEquipInput> Equips);

/// <summary>一件装备的战斗数据（TBattleEquip）：模板、舰载机数与属性。</summary>
internal sealed record BattleEquipStats(int TemplateId, long PlaneNum, IReadOnlyList<(int Attr, long Value)> Attrs);

/// <summary>一艘舰船的战斗数据：TBattleShip.Attr 与各装备（顺序同输入）。</summary>
internal sealed record BattleShipStats(IReadOnlyList<(int Attr, long Value)> Attrs, IReadOnlyList<BattleEquipStats> Equips);

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

    /// <summary>按详情页计算一艘舰船的战斗属性与装备。</summary>
    internal static BattleShipStats Compute(BattleStatsCatalog catalog, BattleShipInput ship)
    {
        int factor = ship.LevelGrowth ? GrowthFactor(catalog, ship.Level) : 0;
        var basic = new Dictionary<int, double>();
        var present = new SortedSet<int>();
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

        var attrsOut = new List<(int Attr, long Value)>();
        foreach (int attr in present)
        {
            if (!BattleAttrs.Contains(attr)) continue;
            double total = final.GetValueOrDefault(attr);
            double shown = percentOf.TryGetValue(attr, out int percentAttr)
                ? Math.Ceiling(total * (1 + final.GetValueOrDefault(percentAttr) / PercentBase))
                : total;
            attrsOut.Add((attr, checked((long)Math.Round(shown - equipFlat.GetValueOrDefault(attr)))));
        }

        var equipsOut = new List<BattleEquipStats>();
        for (int i = 0; i < ship.Equips.Count; i++)
        {
            BattleEquipInput equip = ship.Equips[i];
            long baseNum = equip.Slot >= 0 && equip.Slot < ship.PlaneNumbers.Count ? ship.PlaneNumbers[equip.Slot] : 0;
            long planeNum = baseNum;
            bool isPlane = false;
            long planes = 0;
            foreach (long ewt in equip.EwtIds)
            {
                if (!PlaneAttrByEwt.TryGetValue(ewt, out int planeAttr)) continue;
                isPlane = true;
                planes += baseNum + checked((long)Math.Round(breakBuff.GetValueOrDefault(planeAttr)));
            }
            if (isPlane) planeNum = planes;
            equipsOut.Add(new BattleEquipStats(equip.TemplateId, planeNum, equipAttrs[i]));
        }
        return new BattleShipStats(attrsOut, equipsOut);
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

        return new BattleStatsCatalog(showAttrs, attributeLevels, propTypes, valueEffects, tiers,
            bounds.GetValueOrDefault(155, (0, 1_000_000)), bounds.GetValueOrDefault(156, (1_000_000, 2_000_000)), planeNumbers);
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

    /// <summary>自有舰船。没有 config_ship_main 行时返回 null（调用方回退到离线版原来的下发）。</summary>
    internal static BattleShipInput? ForHero(BattleStatsCatalog catalog, Hero hero, IReadOnlyDictionary<uint, EquipItem> equipById)
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
        return new BattleShipInput(BattleStats.Fields(main), NoFields, hero.Level, LevelGrowth: true, ScoutNum(main), flat,
            HasAffection: true, hero.Affection, Married: hero.MarryTime != 0, BreakValueEffects(hero.TemplateId),
            catalog.PlaneNumbers.GetValueOrDefault(hero.TemplateId) ?? [], equips);
    }

    /// <summary>
    /// 剧情关的临时舰船（config_assist_ship_info）：不是 -1 的字段直接取用，-1 的按 config_ship_main 在 ship_level 的值
    /// （NpcAssistFleetManager:FixNpcAttr）；装备按 equip_level 强化。没有好感与突破加成。
    /// </summary>
    internal static BattleShipInput ForAssist(BattleStatsCatalog catalog, ConfigAssistShipInfo assist)
    {
        int templateId = checked((int)assist.ShipMainId);
        ConfigShipMain? main = ShipMainLoader.Get(templateId);
        var overrides = BattleStats.Fields(assist).Where(kv => kv.Value != -1).ToDictionary(kv => kv.Key, kv => kv.Value);
        var equips = new List<BattleEquipInput>();
        IReadOnlyList<long> ids = assist.Equip ?? [];
        IReadOnlyList<long> levels = assist.EquipLevel ?? [];
        for (int slot = 0; slot < ids.Count; slot++)
        {
            if (ids[slot] == 0) continue;
            int level = slot < levels.Count ? checked((int)levels[slot]) : 0;
            if (Equip(slot, checked((int)ids[slot]), level) is { } equip) equips.Add(equip);
        }
        return new BattleShipInput(main is null ? NoFields : BattleStats.Fields(main), overrides, checked((int)assist.ShipLevel),
            LevelGrowth: main is not null, main is null ? 1 : ScoutNum(main), [], HasAffection: false, 0, Married: false, [],
            catalog.PlaneNumbers.GetValueOrDefault(templateId) ?? [], equips);
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
