using Android.Content;

namespace BlueOath.LocalServer;

/// <summary>
/// 把 APK 内置配置库（<c>assets/bosrv/config/*.db</c>）释放到 App 私有目录。
/// SQLite 只能读真实文件，不能直接读 zip 内的 asset，所以必须先落盘。
/// 按「目标缺失或大小不符」判定是否需要拷贝，二次启动基本无开销。
/// </summary>
public static class AssetDeployer
{
    public static async Task DeployAsync(
        Context context,
        string assetDir,
        string targetDir,
        Action<int, int>? onProgress = null)
    {
        Directory.CreateDirectory(targetDir);

        var manager = context.Assets;
        if (manager is null)
            throw new InvalidOperationException("AssetManager unavailable");

        var names = manager.List(assetDir) ?? Array.Empty<string>();
        if (names.Length == 0)
            throw new InvalidOperationException("no config asset found: " + assetDir);

        int copied = 0;
        int done = 0;
        foreach (var name in names)
        {
            var target = Path.Combine(targetDir, name);
            var info = new FileInfo(target);
            long assetSize;
            using (var probe = manager.Open(Path.Combine(assetDir, name)))
                assetSize = GetStreamLength(probe);

            var needCopy = !(info.Exists && info.Length == assetSize && assetSize > 0);
            if (needCopy)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var src = manager.Open(Path.Combine(assetDir, name));
                await using var dst = File.Create(target);
                await src.CopyToAsync(dst);
                copied++;
            }

            done++;
            // 仅首轮拷贝时汇报进度，避免二次启动刷屏。
            if (copied > 0)
                onProgress?.Invoke(done, names.Length);
        }

        ServerLog.Info(string.Format(
            "config assets: {0} total, {1} copied -> {2}", names.Length, copied, targetDir));
    }

    /// <summary>Android AssetManager 的 ACCESS_STREAMING 模式不支持 Seek/Length，需顺序读取。</summary>
    private static long GetStreamLength(Stream stream)
    {
        if (stream.CanSeek)
            return stream.Length;

        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            total += read;
        return total;
    }
}
