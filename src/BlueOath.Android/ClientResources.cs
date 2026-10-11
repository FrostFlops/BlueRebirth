using System.Runtime.InteropServices;

namespace BlueOath.LocalServer;

/// <summary>
/// 把热更资源**铺设到游戏客户端自己的资源目录**：
///   <c>/sdcard/Android/data/com.zephyrus.clsy.gp/files/bundles/</c>
///
/// 为什么必须这么做：客户端判断「自己的包内容版本」用的是**它自己那份 assetmap** ——
///   · 该目录为空 → 回落 APK 内置 assetmap（1.4.90）→ 标题页显示 1.4.90，
///     且与服务端宣告的 1.4.140 不一致 → 走 PATCH_TO_LATEST 去热更（要真下载 3.5GB）；
///   · 该目录铺上 1.4.140 资源 → 客户端直接认这个版本，弹「0.00MB 需要下载」→ 点确认即可进游戏。
///
/// 实现要点：
///   · 优先 **硬链接**（省掉一份 3.5GB），失败（sdcardfs / 跨设备会返回 EPERM/EXDEV）再退回复制；
///   · 已存在且大小一致的文件直接跳过（幂等，重跑套件不会重抄一遍）；
///   · 目标目录在**别的 App 的外部目录**下，API 28 上靠 READ/WRITE_EXTERNAL_STORAGE 才写得进去；
///     失败时只告警、不阻断整套安装（客户端仍可从服务端 19193 热更）。
/// </summary>
public static class ClientResources
{
    /// <summary>临时/排障文件，不参与铺设（其余内容一律照搬，与已验证过的 push 流程一致）。</summary>
    private static readonly string[] IgnoredNames = { ".bo_dirs.txt" };

    private static readonly string[] IgnoredSuffixes = { ".wrong-overlay", ".tmp", ".part", ".bo-part" };

    public sealed class DeployResult
    {
        public int Copied { get; set; }
        public int Linked { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public long Bytes { get; set; }

        /// <summary>整体失败（例如无存储权限 / 目录不可写）时的人类可读原因。</summary>
        public string? Error { get; set; }

        public int Total => Copied + Linked + Skipped + Failed;
    }

    /// <summary>游戏客户端的资源落地目录；拿不到外部存储时返回 null。</summary>
    public static string? GameBundlesDir()
    {
        try
        {
            var shared = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
            if (string.IsNullOrEmpty(shared))
                return null;
            return Path.Combine(shared, "Android", "data",
                EmbeddedServer.GamePackage, "files", "bundles");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>客户端资源目录里 assetmap 的版本号（= 客户端认的包内容版本）；读不到返回 null。</summary>
    public static string? PeekGameAssetmapVersion() =>
        AssetmapVersion.Peek(Path.Combine(GameBundlesDir() ?? string.Empty, "assetmap"));

    /// <summary>客户端资源目录是否已铺好（存在 assetmap 即可）。</summary>
    public static bool GameResourcesPresent() =>
        AssetmapVersion.Peek(Path.Combine(GameBundlesDir() ?? string.Empty, "assetmap")) is not null;

    /// <summary>
    /// 把 <paramref name="sourceDir"/>（服务端 App 自己那份 bundle）镜像到客户端资源目录。
    /// </summary>
    public static async Task<DeployResult> DeployAsync(
        string sourceDir,
        Action<string, long, long>? onProgress,
        CancellationToken ct)
    {
        var result = new DeployResult();
        var target = GameBundlesDir();
        if (string.IsNullOrEmpty(target))
        {
            result.Error = "拿不到外部存储目录（Environment.ExternalStorageDirectory 为空）";
            return result;
        }

        if (!Directory.Exists(sourceDir))
        {
            result.Error = "源资源目录不存在：" + sourceDir;
            return result;
        }

        var sourceFull = Path.GetFullPath(sourceDir);
        var targetFull = Path.GetFullPath(target);
        if (string.Equals(sourceFull.TrimEnd(Path.DirectorySeparatorChar),
                targetFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
        {
            result.Error = "源目录与目标目录相同（无需铺设）";
            return result;
        }

        List<(string Source, string Relative, long Size)> plan;
        try
        {
            plan = BuildPlan(sourceFull);
        }
        catch (Exception ex)
        {
            result.Error = "枚举资源失败：" + ex.Message;
            return result;
        }

        var totalBytes = plan.Sum(p => p.Size);
        var doneBytes = 0L;
        var done = 0;

        foreach (var item in plan)
        {
            ct.ThrowIfCancellationRequested();

            var destination = Path.Combine(targetFull, item.Relative);
            done++;
            try
            {
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var existing = File.Exists(destination) ? new FileInfo(destination).Length : -1L;
                if (existing == item.Size && item.Size >= 0)
                {
                    result.Skipped++;
                }
                else
                {
                    // 硬链接优先：同一份数据不需要在设备上存两遍（sdcardfs / 跨卷会失败）。
                    if (TryHardLink(item.Source, destination))
                    {
                        result.Linked++;
                    }
                    else
                    {
                        await CopyFileAsync(item.Source, destination, ct);
                        result.Copied++;
                    }

                    result.Bytes += item.Size;
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                if (result.Failed <= 5)
                    ServerLog.Warn($"铺设客户端资源失败：{item.Relative}（{ex.Message}）");
            }

            doneBytes += item.Size;
            onProgress?.Invoke(item.Relative, doneBytes, totalBytes);
        }

        if (result.Failed > 0 && result.Copied + result.Linked == 0)
        {
            result.Error = "资源写入客户端目录全部失败（通常是缺少存储权限）";
        }

        return result;
    }

    private static List<(string Source, string Relative, long Size)> BuildPlan(string sourceRoot)
    {
        var list = new List<(string, string, long)>();
        var stack = new Stack<string>();
        stack.Push(sourceRoot);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.GetDirectories(dir))
                stack.Push(sub);

            foreach (var file in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (IgnoredNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (IgnoredSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var relative = Path.GetRelativePath(sourceRoot, file);
                list.Add((file, relative, new FileInfo(file).Length));
            }
        }

        // 稳定顺序：先浅后深、字典序，便于日志与进度观察。
        list.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        return list;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 1 << 20, ct);
    }

    /// <summary>
    /// 尝试硬链接。Android 的模拟存储（sdcardfs/FUSE）常常不支持，返回 false 即可退回复制。
    /// </summary>
    private static bool TryHardLink(string existing, string created)
    {
        try
        {
            if (File.Exists(created))
                File.Delete(created);
            return Link(existing, created) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string created);
}

/// <summary>从 UnityFS 的 assetmap 里读出唯一出现的 x.y.z 版本号。</summary>
internal static class AssetmapVersion
{
    public static string? Peek(string assetmapPath)
    {
        try
        {
            if (!File.Exists(assetmapPath))
                return null;

            using var stream = new FileStream(assetmapPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            var buffer = new byte[Math.Min(1 << 20, stream.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);

            string? found = null;
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(
                         System.Text.Encoding.ASCII.GetString(buffer, 0, read),
                         @"(?<![\d.])\d{1,2}\.\d{1,2}\.\d{1,3}(?![\d.])"))
            {
                var value = match.Value;
                if (found is null)
                {
                    found = value;
                }
                else if (found != value)
                {
                    // 出现多个候选就不猜了（调用方显示「未知」）。
                    return null;
                }
            }

            return found;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
