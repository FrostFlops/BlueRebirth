using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace BlueOath.LocalServer;

/// <summary>
/// 安装套件（.brk）的安装流程编排：
///   读头部 → 顺序遍历 payload → 解出热更资源到 App 目录 → 把**全量配置表**导入 App 自己的
///   存储 → 用 PackageInstaller 装客户端 APK → 把套件里的客户端版本写进 EmbeddedServer。
/// 另外对外提供 <see cref="InstallApkAsync"/>，供「启动器自更新」复用同一套安装逻辑。
/// </summary>
public static class KitInstaller
{
    public sealed class Progress
    {
        public string Stage { get; init; } = "";
        public long Done { get; init; }
        public long Total { get; init; }
        public string? Detail { get; init; }
    }

    /// <summary>执行套件安装。调用方负责在后台线程运行。</summary>
    public static async Task<string> RunAsync(
        Context context,
        Stream kit,
        string kitDisplayName,
        Action<Progress>? onProgress,
        CancellationToken ct)
    {
        void Stage(string text) => onProgress?.Invoke(new Progress { Stage = text });

        Stage("读取套件头部…");
        var (header, _) = await InstallKit.ReadHeaderAsync(kit, ct);
        ServerLog.Info($"套件：格式 v{header.Format} · 客户端版本 {header.ClientVersion} · payload {header.PayloadLength} 字节");

        var bundleEntry = header.Entries.FirstOrDefault(e => e.Kind == "bundle");
        var clientApk = header.Entries.FirstOrDefault(e => e.Kind == "apk");

        if (bundleEntry is null && clientApk is null)
            throw new InvalidDataException("套件里既没有热更资源、也没有客户端 APK");

        var bundleDir = EmbeddedServer.BundleDropDir(context);
        SpaceCheck(context, bundleDir, (bundleEntry?.Length ?? 0) + (clientApk?.Length ?? 0));

        var position = 0L;
        BundleExtractResult? extract = null;
        var importedConfig = 0;

        foreach (var entry in header.Entries.OrderBy(e => e.Offset))
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Offset > position)
            {
                await InstallKit.SkipAsync(kit, entry.Offset - position, ct);
                position = entry.Offset;
            }

            if (entry.Kind == "bundle")
            {
                extract = await InstallKit.ExtractBundleAsync(
                    kit, entry.Length, bundleDir,
                    r => onProgress?.Invoke(new Progress
                    {
                        Stage = "解包热更资源",
                        Done = r.Bytes,
                        Total = entry.Length,
                        Detail = $"{r.Files} 个文件（跳过写入 {r.Skipped}）",
                    }),
                    ct);

                ServerLog.Info($"资源解包完成：{extract.Files} 文件 / {extract.Directories} 目录，" +
                               $"写入 {extract.Written / 1048576.0:F0} MB，跳过 {extract.Skipped}，" +
                               $"md5 不符 {extract.Mismatches}");
                if (extract.Mismatches > 0)
                    ServerLog.Warn($"有 {extract.Mismatches} 个文件 md5 与套件不符（套件可能损坏）");

                // 把配置表单独导入 App 自己的存储：以后服务端升级可能要用到别的配置表，
                // 这样服务端不再依赖「资源包 / App 目录里那份 config」的当前状态。
                Stage("导入配置表…");
                importedConfig = ImportConfig(
                    Path.Combine(bundleDir, "config"),
                    EmbeddedServer.ConfigInstallDir(context));
                if (importedConfig > 0)
                    ServerLog.Info($"已导入配置表 {importedConfig} 个 -> {EmbeddedServer.ConfigInstallDir(context)}");
                else
                    ServerLog.Warn("资源包里没有 config/，跳过配置表导入");
            }
            else if (entry.Kind == "apk")
            {
                Stage("准备安装客户端…");
                await InstallApkAsync(context, kit, entry.Length,
                    entry.FileName ?? "client.apk", onProgress, ct);
                if (!string.IsNullOrEmpty(entry.Md5))
                    ServerLog.Info("套件登记 md5：" + entry.Md5);
            }

            position = entry.Offset + entry.Length;
        }

        if (!string.IsNullOrWhiteSpace(header.ClientVersion))
        {
            EmbeddedServer.SetClientVersion(context, header.ClientVersion);
            ServerLog.Info("已把服务端宣告版本对齐为套件的客户端版本：" + header.ClientVersion);
        }

        var summary = extract is null
            ? "安装完成"
            : $"安装完成：资源 {extract.Files} 个文件、配置表 {importedConfig} 个，客户端已提交安装";

        onProgress?.Invoke(new Progress { Stage = summary });
        return summary;
    }

    /// <summary>
    /// 把套件里的 APK 直接流式写进 PackageInstaller 会话安装（不落临时文件）。
    /// 供套件安装与启动器自更新共用。
    /// </summary>
    public static async Task InstallApkAsync(
        Context context,
        Stream apk,
        long length,
        string label,
        Action<Progress>? onProgress,
        CancellationToken ct)
    {
        var pm = context.PackageManager
                 ?? throw new InvalidOperationException("PackageManager 不可用");

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && !pm.CanRequestPackageInstalls())
            throw new InvalidOperationException(
                "尚未允许「安装未知应用」。请在系统设置中为 BlueRebirthApp 打开该开关后重试。");

        var installer = pm.PackageInstaller
                        ?? throw new InvalidOperationException("PackageInstaller 不可用");

        var sessionParams = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        sessionParams.SetSize(length);

        var sessionId = installer.CreateSession(sessionParams);
        long written;
        try
        {
            using var session = installer.OpenSession(sessionId)
                                ?? throw new InvalidOperationException("打开安装会话失败");

            using (var output = session.OpenWrite("base.apk", 0, length))
            {
                written = await InstallKit.CopyAsync(apk, output, length,
                    (d, t) => onProgress?.Invoke(new Progress
                    {
                        Stage = "写入安装包",
                        Done = d,
                        Total = t,
                        Detail = label + $"  {d * 100.0 / Math.Max(t, 1):F0}%",
                    }), ct);
                session.Fsync(output);
            }

            // 会话结果通过**可变广播**回给我们：系统要往这个 Intent 里写 extras，
            // 用 Immutable 就拿不到状态了（见 KitInstallResultReceiver 的说明）。
            var resultIntent = new Intent(KitInstallResultReceiver.Action);
            resultIntent.SetPackage(context.PackageName);
            var pending = PendingIntent.GetBroadcast(context, sessionId, resultIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Mutable);
            session.Commit(pending?.IntentSender
                           ?? throw new InvalidOperationException("构造安装确认失败"));
        }
        catch
        {
            try
            {
                installer.AbandonSession(sessionId);
            }
            catch (Exception)
            {
                // 忽略清理失败
            }

            throw;
        }

        ServerLog.Info($"{label} 已提交安装（{written / 1048576.0:F0} MB），" +
                       "等待系统确认（随后会自动弹出安装界面）");
    }

    /// <summary>从文件安装 APK（自更新用；APK 落盘后再装，便于失败重试）。</summary>
    public static async Task InstallApkFileAsync(
        Context context, string apkPath, string label,
        Action<Progress>? onProgress, CancellationToken ct)
    {
        var length = new FileInfo(apkPath).Length;
        await using var stream = File.OpenRead(apkPath);
        await InstallApkAsync(context, stream, length, label, onProgress, ct);
    }

    /// <summary>
    /// 把配置表（config_*.db）导入 App 自己的存储。已存在且大小一致的文件跳过 —— 幂等。
    /// 返回本次真正写入的文件数。
    /// </summary>
    public static int ImportConfig(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir))
            return 0;

        Directory.CreateDirectory(targetDir);
        var written = 0;
        foreach (var source in Directory.EnumerateFiles(sourceDir))
        {
            var target = Path.Combine(targetDir, Path.GetFileName(source));
            var size = new FileInfo(source).Length;
            if (File.Exists(target) && new FileInfo(target).Length == size)
                continue;
            File.Copy(source, target, true);
            written++;
        }

        return written;
    }

    /// <summary>剩余空间预检（只告警不阻断）。</summary>
    private static void SpaceCheck(Context context, string targetDir, long needed)
    {
        if (needed <= 0)
            return;

        try
        {
            Directory.CreateDirectory(targetDir);
            var stat = new StatFs(targetDir);
            var free = stat.AvailableBlocksLong * stat.BlockSizeLong;
            var need = (long)(needed * 1.05) + 64L * 1048576;
            if (free < need)
            {
                ServerLog.Warn($"存储空间可能不足：需要约 {need / 1073741824.0:F1} GB，" +
                               $"当前可用 {free / 1073741824.0:F1} GB（同样大小的套件文件也占空间）");
                Android.Widget.Toast.MakeText(context,
                    "存储空间可能不足，请先清理", Android.Widget.ToastLength.Long)?.Show();
            }
            else
            {
                ServerLog.Info($"空间预检通过：可用 {free / 1073741824.0:F1} GB / 需要 {need / 1073741824.0:F1} GB");
            }
        }
        catch (Exception ex)
        {
            ServerLog.Info("空间预检跳过：" + ex.Message);
        }
    }

    /// <summary>查询已安装客户端的版本（用于安装后确认）。</summary>
    public static string? DescribeInstalledClient(Context context)
    {
        try
        {
            var pm = context.PackageManager;
            if (pm is null)
                return null;

            var info = pm.GetPackageInfo(EmbeddedServer.GamePackage, PackageInfoFlags.MatchDefaultOnly);
            if (info is null)
                return null;

            var code = Build.VERSION.SdkInt >= BuildVersionCodes.P
                ? info.LongVersionCode
                : info.VersionCode;
            return $"{info.VersionName} (versionCode {code})";
        }
        catch (Exception)
        {
            return null;
        }
    }
}
