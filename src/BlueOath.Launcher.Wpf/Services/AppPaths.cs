using System;
using System.IO;

namespace BlueOath.Launcher.Wpf.Services;

/// <summary>
/// 统一解析启动器运行时目录。
/// 发布包结构：包根目录同时包含入口 exe（AOT host）、launcher-settings.json，
/// 以及框架依赖的 WPF 主程序目录 launcher\。WPF 主程序位于 launcher 子目录，
/// 因此不能简单使用 AppContext.BaseDirectory，需向上查找包根。
/// </summary>
public static class AppPaths
{
    public const string SettingsFileName = "launcher-settings.json";

    /// <summary>
    /// 解析包根目录：从当前进程目录向上查找，命中顺序为
    /// 1) 含 launcher-settings.json 的目录（发布包根）
    /// 2) 含 blueoath 子目录的目录（开发环境项目根）
    /// 均未命中时回退到进程目录。
    /// </summary>
    public static string ResolveRoot()
    {
        var startDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(startDir);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, SettingsFileName)))
                return current.FullName;
            if (Directory.Exists(Path.Combine(current.FullName, "blueoath")))
                return current.FullName;
            current = current.Parent;
        }
        return startDir;
    }

    public static string SettingsPath => Path.Combine(ResolveRoot(), SettingsFileName);
}
