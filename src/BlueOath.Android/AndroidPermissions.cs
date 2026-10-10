using Android.App;

// 独立应用运行服务端 + 安装套件所需的最小权限集：
// - INTERNET：监听 loopback 端口并提供 HTTPS（Android 上开 socket 必须有此权限）
// - FOREGROUND_SERVICE（+ API34 的 SPECIAL_USE 子类型）：切到后台/游戏时保活服务端
// - POST_NOTIFICATIONS：API33+ 展示前台服务通知
// - REQUEST_INSTALL_PACKAGES：安装套件里用 PackageInstaller 安装客户端 APK
//   （仍需用户在系统弹窗上确认一次，无法静默）
[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_SPECIAL_USE")]
[assembly: UsesPermission(Android.Manifest.Permission.PostNotifications)]
[assembly: UsesPermission(Android.Manifest.Permission.RequestInstallPackages)]
