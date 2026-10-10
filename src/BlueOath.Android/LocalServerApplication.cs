using Android.App;
using Android.Runtime;

namespace BlueOath.LocalServer;

/// <summary>
/// 独立应用形态下的 Application。进程一创建即拉起本地服务端，
/// 于是无论用户是点图标打开界面、还是由前台服务被系统拉起，服务端都会自动就绪。
/// </summary>
[Application(Icon = "@drawable/ic_launcher")]
// ReSharper disable once UnusedMember.Global
public sealed class LocalServerApplication : Application
{
    public LocalServerApplication(System.IntPtr handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
        try
        {
            EmbeddedServer.EnsureStarted(this);
        }
        catch (Exception ex)
        {
            ServerLog.Error("Application 启动服务端失败", ex);
        }
    }
}
