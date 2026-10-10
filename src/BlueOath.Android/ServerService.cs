using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace BlueOath.LocalServer;

/// <summary>
/// 前台服务：把本地服务端放进前台，避免用户切到游戏后进程被系统回收。
/// 前台服务在通知栏常驻一条「运行中」通知，点击可回到 <see cref="MainActivity"/>。
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class ServerService : Service
{
    public const string ChannelId = "bosrv";
    public const int NotificationId = 19090;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            EnsureChannel();
            StartForeground(NotificationId, BuildNotification());
        }
        catch (Exception ex)
        {
            // 前台化失败不应阻断服务端本身（例如通知权限缺失）。
            ServerLog.Error("前台服务启动失败（服务端仍会运行）", ex);
        }

        EmbeddedServer.EnsureStarted(this);

        // 被系统回收后自动尝试重建。
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        EmbeddedServer.Stop();
        base.OnDestroy();
    }

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null || manager.GetNotificationChannel(ChannelId) is not null)
            return;

        var channel = new NotificationChannel(ChannelId, "本地服务", NotificationImportance.Low)
        {
            Description = "BlueRebirthApp 服务端运行状态",
        };
        manager.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

        var pendingFlags = PendingIntentFlags.UpdateCurrent;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            pendingFlags |= PendingIntentFlags.Immutable;
        var pending = PendingIntent.GetActivity(this, 0, intent, pendingFlags);

        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        builder.SetContentTitle("BlueRebirthApp");
        builder.SetContentText(EmbeddedServer.Message);
        builder.SetSmallIcon(Resource.Drawable.ic_stat_server);
        builder.SetOngoing(true);
        builder.SetShowWhen(false);
        if (pending is not null)
            builder.SetContentIntent(pending);

        return builder.Build();
    }
}
