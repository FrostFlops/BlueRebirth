using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueOath.LocalServer;

/// <summary>
/// 启动器自更新：直接读 GitHub Release（不用 Gitee 那套 PC 侧清单）。
///
/// 约定：
///   · Release tag 形如 <c>v1.2.3</c>（与 PC 启动器、安卓端共用 version.txt 同源）；
///   · 安卓包 asset 名形如 <c>BlueRebirthApp-v1.2.3.apk</c>（CI 产出）。
/// 判定：把 tag 去掉前缀 v 后与本机 versionName 比大小，更大才算有新版本。
/// </summary>
public static class UpdateChecker
{
    public const string DefaultReleaseApi =
        "https://api.github.com/repos/LunarConcerto/BlueRebirth/releases/latest";

    /// <summary>安卓包 asset 的识别规则。</summary>
    private const string ApkAssetPrefix = "BlueRebirthApp";
    private const string ApkAssetSuffix = ".apk";

    public sealed record ReleaseInfo(
        string Tag,
        string Version,
        string? ApkUrl,
        string? ApkName,
        string? PageUrl)
    {
        public bool HasApk => !string.IsNullOrEmpty(ApkUrl);
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub API 要求带 User-Agent，否则 403。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BlueRebirthApp");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>取最新 Release 信息；网络/解析失败时抛出，由调用方决定怎么提示。</summary>
    public static async Task<ReleaseInfo> FetchLatestAsync(string? apiOverride, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(apiOverride) ? DefaultReleaseApi : apiOverride;
        var json = await Http.GetStringAsync(url, ct);
        var dto = JsonSerializer.Deserialize<ReleaseDto>(json)
                  ?? throw new InvalidOperationException("Release 信息解析失败");

        var tag = (dto.TagName ?? string.Empty).Trim();
        if (tag.Length == 0)
            throw new InvalidOperationException("Release 里没有 tag_name");

        var asset = dto.Assets?.FirstOrDefault(a =>
            !string.IsNullOrEmpty(a.Name) &&
            a.Name.StartsWith(ApkAssetPrefix, StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(ApkAssetSuffix, StringComparison.OrdinalIgnoreCase));

        return new ReleaseInfo(
            tag,
            NormalizeVersion(tag),
            asset?.Url,
            asset?.Name,
            dto.HtmlUrl);
    }

    /// <summary>比较版本字符串（忽略前缀 v、忽略 -suffix），remote &gt; local 返回 true。</summary>
    public static bool IsNewer(string? remote, string? local)
    {
        if (!TryParse(remote, out var r) || !TryParse(local, out var l))
            return false;
        return r > l;
    }

    /// <summary>下载到指定文件，边下边报进度。</summary>
    public static async Task<long> DownloadAsync(
        string url, string targetPath, Action<long, long>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var done = 0L;
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(targetPath))
        {
            var buffer = new byte[1 << 20];
            while (true)
            {
                var read = await src.ReadAsync(buffer, ct);
                if (read <= 0)
                    break;
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Invoke(done, total);
            }
        }

        return done;
    }

    public static string NormalizeVersion(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];
        var dash = value.IndexOfAny(new[] { '-', '+' });
        return dash > 0 ? value[..dash] : value;
    }

    private static bool TryParse(string? raw, out Version version)
    {
        var value = NormalizeVersion(raw);
        if (Version.TryParse(value, out var parsed))
        {
            version = parsed;
            return true;
        }

        // 允许只有 major.minor 的情况
        if (Version.TryParse(value + ".0", out var padded))
        {
            version = padded;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? Url { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
