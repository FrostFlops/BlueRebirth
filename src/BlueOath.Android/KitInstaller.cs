using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace BlueOath.LocalServer;

/// <summary>
/// 安装套件（.brk）的安装流程编排：
///   读头部 → 顺序遍历 payload → 解出热更资源到 App 目录 → 用 PackageInstaller 装客户端 APK
///   → 把套件里的客户端版本写进 EmbeddedServer（免去手改代码里的版本号）。
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

    /// <summary>执行安装。调用方负责在后台线程运行。</summary>
    public static async Task<string> RunAsync(
        Context context,
        Stream kit,
        string kitDisplayName,
        Action<Progress>? onProgress,
        CancellationToken ct)
    {
        void Stage(string text) => onProgress?.Invoke(new Progress { Stage = text });

        Stage("读取套件头部…");
        var (header, payloadStart) = await InstallKit.ReadHeaderAsync(kit, ct);
        ServerLog.Info($"套件：格式 v{header.Format} · 客户端版本 {header.ClientVersion} · payload {header.PayloadLength} 字节");

        var bundleEntry = header.Entries.FirstOrDefault(e => e.Kind == "bundle");
        var clientApk = header.Entries.FirstOrDefault(e => e.Kind == "apk" && e.Role is null or "client");
        var launcherApk = header.Entries.FirstOrDefault(e => e.Kind == "apk" && e.Role == "launcher");

        if (bundleEntry is null && clientApk is null)
            throw new InvalidDataException("套件里既没有热更资源、也没有客户端 APK");

        var bundleDir = EmbeddedServer.BundleDropDir(context);
        SpaceCheck(context, bundleDir, (bundleEntry?.Length ?? 0) + (clientApk?.Length ?? 0));

        var position = 0L;
        BundleExtractResult? extract = null;

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
            }
            else if (entry.Kind == "apk")
            {
                if (entry.Role == "launcher")
                {
                    var outPath = Path.Combine(Path.GetDirectoryName(bundleDir) ?? bundleDir,
                        entry.FileName ?? "BlueRebirthApp.apk");
                    Stage("导出启动器 APK…");
                    using (var fs = File.Create(outPath))
                    {
                        await InstallKit.CopyAsync(kit, fs, entry.Length,
                            (d, t) => onProgress?.Invoke(new Progress
                            {
                                Stage = "导出启动器 APK", Done = d, Total = t,
                                Detail = Path.GetFileName(outPath),
                            }), ct);
                    }

                    ServerLog.Info("已导出新的启动器 APK（可自行安装以升级）：" + outPath);
                }
                else
                {
                    Stage("准备安装客户端…");
                    await InstallApkAsync(context, kit, entry, onProgress, ct);
                }
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
            : $"安装完成：资源 {extract.Files} 个文件，客户端已提交安装";

        onProgress?.Invoke(new Progress { Stage = summary });
        return summary;
    }

    /// <summary>把套件里的客户端 APK 通过 PackageInstaller 会话直接写入安装（不落临时文件）。</summary>
    private static async Task InstallApkAsync(
        Context context, Stream kit, KitEntry entry,
        Action<Progress>? onProgress, CancellationToken ct)
    {
        var pm = context.PackageManager
                 ?? throw new InvalidOperationException("PackageManager 不可用");

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && !pm.CanRequestPackageInstalls())
            throw new InvalidOperationException(
                "尚未允许「安装未知应用」。请在系统设置中为 BlueRebirthApp 打开该开关后重试。");

        var installer = pm.PackageInstaller
                        ?? throw new InvalidOperationException("PackageInstaller 不可用");

        var sessionParams = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        sessionParams.SetSize(entry.Length);

        var sessionId = installer.CreateSession(sessionParams);
        long written;
        try
        {
            using var session = installer.OpenSession(sessionId)
                                ?? throw new InvalidOperationException("打开安装会话失败");

            using (var output = session.OpenWrite("base.apk", 0, entry.Length))
            {
                written = await InstallKit.CopyAsync(kit, output, entry.Length,
                    (d, t) => onProgress?.Invoke(new Progress
                    {
                        Stage = "写入客户端 APK", Done = d, Total = t,
                        Detail = $"{d * 100.0 / Math.Max(t, 1):F0}%",
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

        ServerLog.Info($"客户端 APK 已提交安装（{written / 1048576.0:F0} MB），" +
                       "等待系统确认（随后会自动弹出安装界面）");
        if (!string.IsNullOrEmpty(entry.Md5))
            ServerLog.Info("套件登记 md5：" + entry.Md5);
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
