using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>Mod 声明的图鉴回忆章节：章节 id 与按节点顺序排列的关卡（config_copy_display id）。</summary>
internal sealed record MemoryChapterModDefinition(
    int ChapterId,
    IReadOnlyList<int> Copies,
    string ModId);

/// <summary>
/// 从启用的 Mod 读取 memory-chapters.json（格式 <c>{"memoryChapters":[{"chapterId":12010,"copies":[50301,...]}]}</c>）。
/// 客户端 Mod 往 config_chapter 注入的回忆章节，只有服务端在 illustrate.Memory 里确认过才会显示，
/// 回忆战斗节点还要在 copy.GetCopy 的 PlotCopy 里有记录（LevelDetailsPage 读 tabSerData）。
/// 这里只做发现与校验，由 <see cref="ChapterCopyLoader.RegisterModChapters"/> 并入章节表。
/// </summary>
internal static class MemoryChapterModLoader
{
    internal const string FileName = "memory-chapters.json";

    /// <summary>config_chapter 里列出关卡的字段，与 <see cref="ChapterCopyLoader"/> 决定关卡类型时读的一致（另加追击关卡）。</summary>
    private static readonly string[] ChapterCopyLists = ["level_list", "running_level_list", "treaty_copy"];

    private sealed record Manifest(
        string Id,
        string[]? TargetClients,
        bool Enabled = true);

    private sealed record MemoryChapterFile(IReadOnlyList<MemoryChapterEntry>? MemoryChapters);

    private sealed record MemoryChapterEntry(int ChapterId, IReadOnlyList<int>? Copies);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 按路径序扫描 <paramref name="modsRoot"/> 下所有 memory-chapters.json，规则与装备 Mod 相同：同目录必须有 mod.json，
    /// 跳过 enabled=false 与 targetClients 不含 <paramref name="clientId"/> 的 Mod；文件格式错误只忽略该文件。
    /// 章节 id 已存在于客户端 config_chapter、已被前面的 Mod 占用，关卡不在 config_copy_display 中，或关卡已属于
    /// config_chapter 的某一章（level_list / running_level_list / treaty_copy）或先前登记的 Mod 章节时，只跳过该章节：
    /// 关卡类型按所属章节决定，一个关卡只能属于一章。
    /// </summary>
    internal static IReadOnlyList<MemoryChapterModDefinition> Load(
        string modsRoot, string clientId, string configDir, ILogger logger)
    {
        if (!Directory.Exists(modsRoot))
        {
            logger.LogInformation("memory-chapter mods: no Mods directory at {ModsRoot}", modsRoot);
            return [];
        }
        List<string> paths = Directory.EnumerateFiles(modsRoot, FileName, SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
        {
            logger.LogInformation("memory-chapter mods: no {FileName} under {ModsRoot}", FileName, modsRoot);
            return [];
        }

        // 只在有声明文件时才读配置表（config_copy_display 约 9 MB）。
        var configChapterIds = new HashSet<int>();
        var configChapterCopies = new HashSet<int>();
        HashSet<int> configCopyIds;
        try
        {
            ConfigDbLoader.LoadRows(configDir, "config_chapter.db", (id, _, json) =>
            {
                configChapterIds.Add(id);
                using var doc = JsonDocument.Parse(json);
                foreach (string key in ChapterCopyLists)
                    if (doc.RootElement.TryGetProperty(key, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement item in list.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int copyId) && copyId > 0)
                                configChapterCopies.Add(copyId);
            });
            configCopyIds = ReadIds(configDir, "config_copy_display.db");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "memory-chapter mods ignored: client config in {ConfigDir} could not be read", configDir);
            return [];
        }
        if (configChapterIds.Count == 0 || configCopyIds.Count == 0)
        {
            logger.LogWarning(
                "memory-chapter mods ignored: config_chapter / config_copy_display not found in {ConfigDir}", configDir);
            return [];
        }

        var chapters = new List<MemoryChapterModDefinition>();
        var chapterIds = new HashSet<int>();
        var acceptedCopies = new HashSet<int>();
        foreach (string path in paths)
        {
            string directory = Path.GetDirectoryName(path)!;
            string manifestPath = Path.Combine(directory, "mod.json");
            try
            {
                if (!File.Exists(manifestPath))
                    throw new InvalidDataException("mod.json is missing");
                Manifest manifest = JsonSerializer.Deserialize<Manifest>(
                    File.ReadAllText(manifestPath), JsonOptions)
                    ?? throw new InvalidDataException("mod.json is empty");
                if (!manifest.Enabled || manifest.TargetClients is { Length: > 0 } targets &&
                    !targets.Contains(clientId, StringComparer.OrdinalIgnoreCase))
                    continue;
                string modId = string.IsNullOrWhiteSpace(manifest.Id) ? Path.GetFileName(directory) : manifest.Id;

                MemoryChapterFile file = JsonSerializer.Deserialize<MemoryChapterFile>(
                    File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException($"{FileName} is empty");
                if (file.MemoryChapters?.Any(entry => entry is null) == true)
                    throw new InvalidDataException("memory chapter entry is null");
                var local = (file.MemoryChapters ?? [])
                    .Select(entry => new MemoryChapterModDefinition(entry.ChapterId, entry.Copies ?? [], modId))
                    .ToList();
                if (local.Any(x => x.ChapterId <= 0 || x.Copies.Any(copyId => copyId <= 0)))
                    throw new InvalidDataException("memory chapter and copy ids must be positive");
                if (local.Any(x => x.Copies.Count == 0))
                    throw new InvalidDataException("memory chapter has no copies");
                if (local.Any(x => x.Copies.Distinct().Count() != x.Copies.Count))
                    throw new InvalidDataException("memory chapter lists a copy twice");
                if (local.Select(x => x.ChapterId).Distinct().Count() != local.Count)
                    throw new InvalidDataException($"{FileName} contains duplicate chapter ids");

                int accepted = 0;
                foreach (MemoryChapterModDefinition chapter in local)
                {
                    // 新章节 id 不得占用客户端已有章节：客户端 Mod 按 id 注入行，撞上已有章节会覆盖原有剧情。
                    if (configChapterIds.Contains(chapter.ChapterId))
                    {
                        logger.LogWarning(
                            "memory-chapter mod {ModId}: chapter {ChapterId} already exists in config_chapter; skipped",
                            modId, chapter.ChapterId);
                        continue;
                    }
                    if (chapterIds.Contains(chapter.ChapterId))
                    {
                        logger.LogWarning(
                            "memory-chapter mod {ModId}: chapter {ChapterId} is already declared by another mod; skipped",
                            modId, chapter.ChapterId);
                        continue;
                    }
                    List<int> missing = chapter.Copies.Where(copyId => !configCopyIds.Contains(copyId)).ToList();
                    if (missing.Count > 0)
                    {
                        logger.LogWarning(
                            "memory-chapter mod {ModId}: chapter {ChapterId} lists copies missing from config_copy_display ({CopyIds}); skipped",
                            modId, chapter.ChapterId, string.Join(",", missing));
                        continue;
                    }
                    List<int> used = chapter.Copies
                        .Where(copyId => configChapterCopies.Contains(copyId) || acceptedCopies.Contains(copyId))
                        .ToList();
                    if (used.Count > 0)
                    {
                        logger.LogWarning(
                            "memory-chapter mod {ModId}: chapter {ChapterId} lists copies already used by another chapter ({CopyIds}); skipped",
                            modId, chapter.ChapterId, string.Join(",", used));
                        continue;
                    }
                    chapters.Add(chapter);
                    chapterIds.Add(chapter.ChapterId);
                    acceptedCopies.UnionWith(chapter.Copies);
                    accepted++;
                }
                logger.LogInformation(
                    "memory-chapter mod {ModId}: loaded {Accepted} of {Declared} memory chapters",
                    modId, accepted, local.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning("memory-chapter mod file ignored {Path}: {Reason}", path, ex.Message);
            }
        }

        return chapters;
    }

    private static HashSet<int> ReadIds(string configDir, string dbFile)
    {
        var ids = new HashSet<int>();
        ConfigDbLoader.LoadRows(configDir, dbFile, (id, _, _) => ids.Add(id));
        return ids;
    }
}
