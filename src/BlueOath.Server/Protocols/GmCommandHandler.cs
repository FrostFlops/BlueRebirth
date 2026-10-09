using BlueOath.Core;
using BlueOath.Server.Sessions;
using BlueOath.Storage;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>
/// GM 命令解析器：解析 WebUI 输入框的文本命令，调用 <see cref="GameServices"/>
/// 的实体操作方法，返回执行结果文本。
/// </summary>
internal sealed class GmCommandHandler
{
    private readonly SqliteGameRepository _repo;
    private readonly GameServices _handler;
    private readonly ILogger<GmCommandHandler> _logger;
    private readonly SessionPushHub? _pushHub;

    private static readonly Dictionary<string, int> CurrencyNames = new()
    {
        ["gold"] = 1, ["diamond"] = 2, ["supply"] = 5, ["maingun"] = 8,
        ["torpedo"] = 9, ["plane"] = 10, ["other"] = 11, ["retire"] = 12,
        ["bath"] = 13, ["strategy"] = 14, ["medal"] = 15, ["tower"] = 18,
        ["copytrain"] = 22, ["fashion"] = 23, ["guild"] = 24, ["lucky"] = 25,
        ["teacher"] = 26, ["teacherpop"] = 27, ["bp_exp"] = 28, ["bp_gold"] = 29,
        ["pvept"] = 30, ["guildcoin2"] = 31, ["urequip"] = 32, ["activity_bp"] = 33,
        ["strength"] = 21,
    };

    public GmCommandHandler(SqliteGameRepository repo, GameServices handler,
        ILogger<GmCommandHandler> logger, SessionPushHub? pushHub = null)
    {
        _repo = repo;
        _handler = handler;
        _logger = logger;
        _pushHub = pushHub;
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken ct)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;

        var cmd = parts[0].ToLowerInvariant();
        try
        {
            var result = cmd switch
            {
                "help" => Help(),
                "list_profiles" => await ListProfilesAsync(ct),
                "get_character" => await GetCharacterAsync(parts, ct),
                "get_dock" => await GetDockAsync(parts, ct),
                "get_bag" => await GetBagAsync(parts, ct),
                "add_currency" => await AddCurrencyAsync(parts, ct),
                "add_ship" => await AddShipAsync(parts, ct),
                "add_item" => await AddItemAsync(parts, ct),
                "set_currency" => await SetCurrencyCommandAsync(parts, ct),
                "set_item" => await SetItemCommandAsync(parts, ct),
                _ => $"unknown command: {cmd}. Type 'help' for available commands."
            };
            _logger.LogInformation("GM: {Command} -> {Result}", command, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GM command failed: {Command}", command);
            return $"error: {ex.Message}";
        }
    }

    private static string Help() =>
        string.Join('\n',
            "add_currency <profileId> <type> <amount>  — 加货币 (gold/diamond/supply/...)\n",
            "add_ship <profileId> <templateId> [level]  — 加舰娘到船坞\n",
            "add_item <profileId> <templateId> [count]  — 加道具到仓库\n",
            "set_currency <profileId> <type> <value>    — 把货币设成指定数量（可减少；不含工人体力）\n",
            "set_item <profileId> <templateId> <count>  — 把道具设成指定数量（0 = 删除）\n",
            "存档编辑页面：/save（在线时同步到游戏）\n",
            "list_profiles                                — 列出所有档案\n",
            "get_character <profileId>                    — 查看角色信息\n",
            "get_dock <profileId>                         — 查看船坞\n",
            "get_bag <profileId>                          — 查看仓库\n",
            "help                                         — 显示此帮助\n");

    /// <summary>存档编辑页面用：全部档案 id 与角色名。</summary>
    internal async Task<IReadOnlyList<(string Id, string Name)>> ListProfileSummariesAsync(CancellationToken ct)
    {
        var result = new List<(string, string)>();
        foreach (string id in await _repo.ListAccountIdsAsync(ct))
        {
            PlayerAccount? account = await _repo.LoadAccountAsync(id, ct);
            result.Add((id, account?.Character.Name ?? ""));
        }
        return result;
    }

    /// <summary>存档编辑页面「全部道具」用：所有能进背包的道具（按 id 排序）。</summary>
    internal static IReadOnlyList<ItemCatalogLoader.Entry> GetItemCatalog() =>
        ItemCatalogLoader.Entries.Values.OrderBy(entry => entry.Id).ToList();

    /// <summary>存档编辑页面用：一个档案的可编辑货币与背包道具；档案不存在时返回 null。</summary>
    internal async Task<SaveEditor.Snapshot?> GetSaveSnapshotAsync(string profileId, CancellationToken ct)
    {
        if (!(await _repo.ListAccountIdsAsync(ct)).Contains(profileId, StringComparer.Ordinal))
            return null;
        // 首次加载档案会跑迁移并写入缓存，与游戏请求共用账号锁。
        using (await _handler.LockAccountAsync(profileId, ct))
        {
            PlayerAccount account = await _handler.GetOrCreateAccountAsync(profileId, ct);
            return SaveEditor.Build(profileId, account, _handler.CurrencyNames, ItemCatalogLoader.Entries,
                ZhNameCatalog.Currencies);
        }
    }

    /// <summary>
    /// 存档编辑：把货币（kind = currency）或背包道具（kind = item）设成 value。在账号锁内先按经过时间结算
    /// （温泉币等会被前哨自动入浴、浴场续券按时间消耗，不结算就改会把这段时间按新数量重算），再修改并落盘；
    /// 账号在线时给客户端补发结算与编辑的推送。返回结果说明。
    /// </summary>
    internal async Task<(bool Ok, string Message)> EditSaveAsync(
        string profileId, string kind, int id, long value, CancellationToken ct)
    {
        if (!(await _repo.ListAccountIdsAsync(ct)).Contains(profileId, StringComparer.Ordinal))
            return (false, $"存档 {profileId} 不存在");
        SaveEditor.EditResult edit;
        SettlementResult settled;
        using (await _handler.LockAccountAsync(profileId, ct))
        {
            int now = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            settled = await _handler.SettleLockedAsync(await _handler.GetOrCreateAccountAsync(profileId, ct), now, ct);
            PlayerAccount account = settled.Account;
            edit = kind switch
            {
                "currency" => SaveEditor.SetCurrency(account, id, value),
                "item" => SaveEditor.SetItem(account, id, value, ItemCatalogLoader.Entries.ContainsKey(id)),
                _ => new SaveEditor.EditResult(false, account, $"未知类型 {kind}"),
            };
            if (edit.Changed)
                await _handler.SaveAccountAsync(edit.Account, ct);
        }
        _logger.LogInformation("GM save edit {Profile} {Kind} {Id} = {Value}: {Message}", profileId, kind, id, value, edit.Message);
        // 结算已落盘的变化也要推给在线客户端，即使这次编辑没有改动。
        string sync = edit.Changed || settled.Changed ? await ResyncAsync(profileId, settled, ct) : "";
        return edit.Ok && edit.Changed ? (true, edit.Message + "；" + sync) : (edit.Ok, edit.Message);
    }

    /// <summary>
    /// 给在线客户端补发推送：结算变化（[建筑, 舰娘, 建筑] 与浴场 / 祈愿 / 商店 / 前哨快照）、玩家信息与背包。
    /// 推送在写出时按最新存档生成；设成 0 的道具以 Num=0 的记录随背包下发，客户端据此删除。
    /// </summary>
    private async Task<string> ResyncAsync(string profileId, SettlementResult settled, CancellationToken ct)
    {
        if (_pushHub is null)
            return "已写入存档，下次登录生效";
        try
        {
            bool online = await _pushHub.RequestResyncAsync(profileId, async token =>
            {
                PlayerAccount current = await _handler.GetOrCreateAccountAsync(profileId, token);
                uint now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                return
                [
                    .. GameServices.BuildMoodSyncPushes(current, settled.ChangedHeroIds, settled.BuildingChanged, now),
                    GameServices.BuildUpdateUserInfoPush(current, now),
                    _handler.BuildBagPush(current, now),
                    .. GameServices.BuildSettlementPostPushes(settled, now, current: current),
                ];
            }, ct);
            return online ? "已同步到游戏" : "游戏未登录，下次登录生效";
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return "已写入存档，但同步到游戏失败（连接已断开），下次登录生效";
        }
    }

    private async Task<string> SetCurrencyCommandAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 4) return "usage: set_currency <profileId> <type> <value>";
        if (!long.TryParse(parts[3], out long value)) return "invalid value";
        int currencyId = int.TryParse(parts[2], out int numeric)
            ? numeric
            : CurrencyNames.TryGetValue(parts[2].ToLowerInvariant(), out int named) ? named : -1;
        if (currencyId < 0)
            return $"unknown currency type: {parts[2]}. Available: {string.Join(' ', CurrencyNames.Keys)} or a numeric id";
        (bool ok, string message) = await EditSaveAsync(parts[1], "currency", currencyId, value, ct);
        return (ok ? "ok: " : "error: ") + message;
    }

    private async Task<string> SetItemCommandAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 4) return "usage: set_item <profileId> <templateId> <count>";
        if (!int.TryParse(parts[2], out int templateId)) return "invalid templateId";
        if (!long.TryParse(parts[3], out long value)) return "invalid count";
        (bool ok, string message) = await EditSaveAsync(parts[1], "item", templateId, value, ct);
        return (ok ? "ok: " : "error: ") + message;
    }

    private async Task<string> ListProfilesAsync(CancellationToken ct)
    {
        // 当前存档在 accounts 表；旧 profiles 表里若还有档案一并列出。
        var profiles = (await _repo.ListAccountIdsAsync(ct)).Union(await _repo.ListProfilesAsync(ct)).ToList();
        return profiles.Count == 0
            ? "(no profiles)"
            : string.Join('\n', profiles);
    }

    private async Task<string> GetCharacterAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2) return "usage: get_character <profileId>";
        var account = await _handler.GetOrCreateAccountAsync(parts[1], ct);
        var c = account.Character;
        return $"Uid={c.Uid} Name={c.Name} Level={c.Level} Class={c.Class} SecretaryId={c.SecretaryId}\n" +
               $"Diamond={c.Diamond} Gold={c.Gold} Supply={c.Supply} Medal={c.Medal} PvePt={c.PvePt}";
    }

    private async Task<string> GetDockAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2) return "usage: get_dock <profileId>";
        var account = await _handler.GetOrCreateAccountAsync(parts[1], ct);
        var dock = account.Dock;
        var lines = dock.Heroes.Select(h =>
            $"HeroId={h.HeroId} TemplateId={h.TemplateId} Lv={h.Level} Fashioning={h.Fashioning} Affection={h.Affection}");
        return $"BagSize={dock.BagSize} Count={dock.Heroes.Count}\n" + string.Join('\n', lines);
    }

    private async Task<string> GetBagAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2) return "usage: get_bag <profileId>";
        var account = await _handler.GetOrCreateAccountAsync(parts[1], ct);
        var bag = account.Bag;
        if (bag is null || bag.Items.Count == 0) return "(empty bag)";
        var lines = bag.Items.Select(i => $"TemplateId={i.TemplateId} Num={i.Num}");
        return $"BagSize={bag.BagSize} Count={bag.Items.Count}\n" + string.Join('\n', lines);
    }

    private async Task<string> AddCurrencyAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 4) return "usage: add_currency <profileId> <type> <amount>";
        var profileId = parts[1];
        if (!int.TryParse(parts[3], out var amount)) return "invalid amount";
        if (amount <= 0) return "amount must be > 0";

        if (!CurrencyNames.TryGetValue(parts[2].ToLowerInvariant(), out var currencyType))
            return $"unknown currency type: {parts[2]}. Available: {string.Join(' ', CurrencyNames.Keys)}";

        // 与游戏请求共用账号锁和内存缓存：直接写库会被下一次游戏内存档（缓存中的旧账号）覆盖。
        using var _ = await _handler.LockAccountAsync(profileId, ct);
        var account = await _handler.GetOrCreateAccountAsync(profileId, ct);
        // 先按经过时间结算（工人体力等）：否则直接加上的体力会让下一次结算漏算或封顶吞掉这段时间的回复。
        int now = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        account = (await _handler.SettleLockedAsync(account, now, ct)).Account;
        account = GameServices.AddCurrency(account, currencyType, amount);
        await _handler.SaveAccountAsync(account, ct);
        return $"ok: {parts[2]} +{amount}";
    }

    private async Task<string> AddShipAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 3) return "usage: add_ship <profileId> <templateId> [level]";
        var profileId = parts[1];
        if (!int.TryParse(parts[2], out var templateId) || templateId <= 0) return "invalid templateId";
        var level = parts.Length > 3 && int.TryParse(parts[3], out var l) ? l : 1;

        // 与游戏请求共用账号锁和内存缓存：直接写库会被下一次游戏内存档（缓存中的旧账号）覆盖。
        using var _ = await _handler.LockAccountAsync(profileId, ct);
        var account = await _handler.GetOrCreateAccountAsync(profileId, ct);
        var heroId = _handler.NextHeroId();
        var now = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        account = _handler.AddShip(account, heroId, templateId, now);
        // 如果指定了等级，单独设置
        if (level > 1)
        {
            var dock = account.Dock;
            var heroes = dock.Heroes.ToList();
            var idx = heroes.FindIndex(h => h.HeroId == heroId);
            if (idx >= 0)
                heroes[idx] = heroes[idx] with { Level = level };
            account = account with { Dock = dock with { Heroes = heroes } };
        }
        await _handler.SaveAccountAsync(account, ct);
        return $"ok: added ship HeroId={heroId} TemplateId={templateId} Level={level}";
    }

    private async Task<string> AddItemAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 3) return "usage: add_item <profileId> <templateId> [count]";
        var profileId = parts[1];
        if (!int.TryParse(parts[2], out var templateId) || templateId <= 0) return "invalid templateId";
        var count = parts.Length > 3 && int.TryParse(parts[3], out var c) ? c : 1;

        // 与游戏请求共用账号锁和内存缓存：直接写库会被下一次游戏内存档（缓存中的旧账号）覆盖。
        using var _ = await _handler.LockAccountAsync(profileId, ct);
        var account = await _handler.GetOrCreateAccountAsync(profileId, ct);
        account = GameServices.AddBagItem(account, templateId, count);
        await _handler.SaveAccountAsync(account, ct);
        return $"ok: item TemplateId={templateId} +{count}";
    }
}
