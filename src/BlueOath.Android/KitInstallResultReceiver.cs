using Android.App;
using Android.Content;
using Android.Content.PM;

namespace BlueOath.LocalServer;

/// <summary>
/// 接收 PackageInstaller 会话的安装结果。
///
/// ★ 关键：API 28~30 上安装会话不会自己弹确认框。系统先回一个
///   <see cref="PackageInstallStatus.PendingUserAction"/>，App 必须**自己**把结果里附带的
///   <c>Intent.EXTRA_INTENT</c> 启动起来，系统确认界面才会出现。
///   漏掉这一步的现象就是「点了安装套件，日志说已提交，但什么都没发生」。
///
/// 因此提交会话时必须用 <c>PendingIntentFlags.Mutable</c> —— 系统要往这个 Intent 里填 extras，
/// 用 Immutable 就会拿不到状态。
/// </summary>
public sealed class KitInstallResultReceiver : BroadcastReceiver
{
    public const string Action = "com.blueoath.server.KIT_INSTALL_RESULT";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null)
            return;

        var status = (PackageInstallStatus)intent.GetIntExtra(
            PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage) ?? string.Empty;
        var packageName = intent.GetStringExtra(PackageInstaller.ExtraPackageName);

        var detail = string.IsNullOrEmpty(packageName) ? string.Empty : $" · {packageName}";
        if (!string.IsNullOrEmpty(message))
            detail += " · " + message;

        if (status == PackageInstallStatus.PendingUserAction)
        {
            ServerLog.Info("安装结果：等待用户确认" + detail);
            var confirm = intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
            if (confirm is null)
            {
                ServerLog.Warn("系统要求用户确认，但结果里没有确认 Intent");
                return;
            }

            try
            {
                confirm.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(confirm);
                ServerLog.Info("已拉起系统安装确认界面，请在弹窗上点「安装」");
            }
            catch (Exception ex)
            {
                ServerLog.Error("拉起系统安装确认界面失败", ex);
            }

            return;
        }

        if (status == PackageInstallStatus.Success)
            ServerLog.Info("客户端安装成功" + detail);
        else
            ServerLog.Warn("客户端安装未成功：" + status + detail);
    }
}
