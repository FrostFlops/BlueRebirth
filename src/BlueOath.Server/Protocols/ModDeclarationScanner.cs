using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BlueOath.Server.Protocols;

/// <summary>一个启用的 Mod 里的服务端声明文件（如 decorations.json）。</summary>
internal sealed record ModDeclarationFile(string Path, string ModId, string ModDirectory);

/// <summary>
/// 通用的 Mod 声明文件发现：在 Mods 根目录下递归找指定文件名，按完整路径的序数顺序返回。规则与装备 Mod 相同：
/// 同目录必须有 mod.json，跳过 enabled=false 与 targetClients 不含当前客户端的 Mod；mod.json 读不了只跳过该文件。
/// <para>
/// 客户端 Loader 不读 mod.json 的 enabled，只加载 Mods/bootstrap.lua 的 entries 里列出的入口。声明会让服务端下发
/// 客户端配置里本来没有的 id 时（客户端没加载这个 Mod 就会报错），传 requireClientEntry: true：
/// 还要求同一 Mods 根目录的 bootstrap.lua 存在、并在 entries 里列出了这个 Mod 目录下的入口，否则跳过该文件。
/// </para>
/// </summary>
internal static class ModDeclarationScanner
{
    private sealed record Manifest(string? Id, string[]? TargetClients, bool Enabled = true);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static IReadOnlyList<ModDeclarationFile> Find(
        string modsRoot, string fileName, string clientId, ILogger logger, bool requireClientEntry)
    {
        if (!Directory.Exists(modsRoot))
        {
            logger.LogInformation("mod {FileName}: no Mods directory at {ModsRoot}", fileName, modsRoot);
            return [];
        }
        List<string> paths = Directory.EnumerateFiles(modsRoot, fileName, SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0) return [];

        IReadOnlyList<string>? entries = requireClientEntry ? ReadBootstrapEntries(modsRoot) : null;
        if (requireClientEntry && entries is null)
            logger.LogWarning(
                "mod {FileName}: {ModsRoot} has no readable bootstrap.lua entries; the client would not load these mods, so their declarations are ignored",
                fileName, modsRoot);

        var files = new List<ModDeclarationFile>();
        foreach (string path in paths)
        {
            string directory = System.IO.Path.GetDirectoryName(path)!;
            try
            {
                string manifestPath = System.IO.Path.Combine(directory, "mod.json");
                if (!File.Exists(manifestPath))
                    throw new InvalidDataException("mod.json is missing");
                Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions)
                    ?? throw new InvalidDataException("mod.json is empty");
                string modId = string.IsNullOrWhiteSpace(manifest.Id) ? System.IO.Path.GetFileName(directory) : manifest.Id;
                if (!manifest.Enabled)
                {
                    logger.LogInformation("mod {ModId}: {FileName} skipped (enabled=false)", modId, fileName);
                    continue;
                }
                if (manifest.TargetClients is { Length: > 0 } targets &&
                    !targets.Contains(clientId, StringComparer.OrdinalIgnoreCase))
                {
                    logger.LogInformation("mod {ModId}: {FileName} skipped (targetClients does not include {ClientId})",
                        modId, fileName, clientId);
                    continue;
                }
                if (requireClientEntry)
                {
                    if (entries is null) continue;
                    if (!IsListed(entries, modsRoot, directory))
                    {
                        logger.LogWarning(
                            "mod {ModId}: {FileName} skipped because Mods/bootstrap.lua does not list an entry under {Directory}; the client would not have its config rows",
                            modId, fileName, directory);
                        continue;
                    }
                }
                files.Add(new ModDeclarationFile(path, modId, directory));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                logger.LogWarning("mod {FileName} ignored {Path}: {Reason}", fileName, path, ex.Message);
            }
        }
        return files;
    }

    /// <summary>
    /// 读 bootstrap.lua 里 <c>local entries = { ... }</c> 的字符串（去掉 <c>--</c> 注释）；文件不存在或找不到这张表时返回 null。
    /// </summary>
    internal static IReadOnlyList<string>? ReadBootstrapEntries(string modsRoot)
    {
        string path = System.IO.Path.Combine(modsRoot, "bootstrap.lua");
        if (!File.Exists(path)) return null;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        // 去掉块注释与行注释（entries 里的路径不含 "--"）。
        text = Regex.Replace(text, @"--\[(=*)\[.*?\]\1\]", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"--[^\r\n]*", "");
        Match table = Regex.Match(text, @"\blocal\s+entries\s*=\s*\{(?<body>[^}]*)\}");
        if (!table.Success) return null;
        return Regex.Matches(table.Groups["body"].Value, "\"(?<s>[^\"\\\\]*)\"|'(?<s>[^'\\\\]*)'")
            .Select(match => match.Groups["s"].Value.Replace('\\', '/').Trim())
            .Where(entry => entry.Length > 0)
            .ToList();
    }

    /// <summary>entries 里有没有位于 <paramref name="modDirectory"/>（相对 Mods 根目录）下的入口。</summary>
    internal static bool IsListed(IReadOnlyList<string> entries, string modsRoot, string modDirectory)
    {
        string relative = System.IO.Path.GetRelativePath(modsRoot, modDirectory).Replace('\\', '/').TrimEnd('/') + "/";
        return entries.Any(entry => entry.StartsWith(relative, StringComparison.OrdinalIgnoreCase));
    }
}
