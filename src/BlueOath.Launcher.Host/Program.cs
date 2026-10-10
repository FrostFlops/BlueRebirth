using System.Diagnostics;
using System.Security.Principal;

namespace BlueOath.Launcher.Host;

internal static class Program
{
    private const int RequiredRuntimeMajor = 8;
    private const int RequiredRuntimeMinor = 0;
    private const int RequiredRuntimePatch = 24;
    private const string RuntimeVersion = "8.0.24";
    private const string RuntimeInstallerUrl =
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.24/windowsdesktop-runtime-8.0.24-win-x64.exe";
    private const string LauncherExeName = "BlueOath.Launcher.Wpf.exe";
    private const string LauncherSubDirectory = "launcher";

    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "BlueOath-Host.log");

    private static int Main(string[] args)
    {
        try
        {
            if (args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)))
            {
                return RunCheck();
            }

            return Run();
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            ShowError($"启动器初始化失败：\n\n{ex.Message}\n\n详细日志：{LogPath}");
            return 1;
        }
    }

    private static int RunCheck()
    {
        var available = IsDesktopRuntimeAvailable(out var foundVersion);
        var message = available
            ? $"OK: .NET Desktop Runtime detected ({foundVersion}); required >= {RuntimeVersion}."
            : $"MISSING: .NET Desktop Runtime >= {RuntimeVersion} not found (highest {foundVersion}).";
        Log($"CHECK: {message}");
        AttachConsole(-1);
        Console.WriteLine(message);
        FreeConsole();
        return available ? 0 : 2;
    }

    private static int Run()
    {
        var baseDir = AppContext.BaseDirectory;
        Log($"Host started. BaseDir={baseDir}");

        var launcherDir = Path.Combine(baseDir, LauncherSubDirectory);
        var launcherExe = Path.Combine(launcherDir, LauncherExeName);
        if (!File.Exists(launcherExe))
        {
            launcherExe = Path.Combine(baseDir, LauncherExeName);
        }

        if (!File.Exists(launcherExe))
        {
            ShowError($"找不到启动器主程序：\n{launcherExe}");
            Log($"ERROR: launcher exe not found at {launcherExe}");
            return 1;
        }

        if (!IsDesktopRuntimeAvailable(out var foundVersion))
        {
            Log($"Desktop Runtime {RequiredRuntimeMajor}.x not found. Installing {RuntimeVersion}...");
            var installResult = InstallRuntime();
            if (installResult != 0)
            {
                ShowError(
                    "未能自动安装 .NET Desktop Runtime。\n\n" +
                    "请手动安装后重试：\n" +
                    RuntimeInstallerUrl);
                Log($"ERROR: runtime install failed, exit={installResult}");
                return installResult;
            }

            if (!IsDesktopRuntimeAvailable(out foundVersion))
            {
                ShowError("运行时安装完成但未检测到，请重新启动本程序。");
                Log("ERROR: runtime still missing after install");
                return 1;
            }
        }

        Log($"Desktop Runtime {foundVersion} available. Launching {launcherExe}");
        return LaunchLauncher(launcherExe, launcherDir);
    }

    private static int LaunchLauncher(string launcherExe, string workingDirectory)
    {
        var psi = new ProcessStartInfo(launcherExe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        var process = Process.Start(psi);
        if (process is null)
        {
            ShowError("无法启动启动器进程。");
            return 1;
        }

        return 0;
    }

    private static bool IsDesktopRuntimeAvailable(out string foundVersion)
    {
        foundVersion = string.Empty;

        var dotnetRoot = FindDotnetRoot();
        if (dotnetRoot is null)
        {
            Log("dotnet root not found.");
            return false;
        }

        var sharedDir = Path.Combine(dotnetRoot, "shared", "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(sharedDir))
        {
            Log($"WindowsDesktop shared dir not found: {sharedDir}");
            return false;
        }

        var best = new Version(RequiredRuntimeMajor, 0, 0);
        var satisfied = false;
        foreach (var dir in Directory.GetDirectories(sharedDir))
        {
            var name = Path.GetFileName(dir);
            if (!Version.TryParse(name, out var version))
            {
                continue;
            }

            if (version.Major != RequiredRuntimeMajor)
            {
                continue;
            }

            if (version > best)
            {
                best = version;
            }

            if (version.Major == RequiredRuntimeMajor &&
                version.Minor == RequiredRuntimeMinor &&
                version.Build >= RequiredRuntimePatch)
            {
                satisfied = true;
            }
        }

        if (best > new Version(RequiredRuntimeMajor, 0, 0))
        {
            foundVersion = best.ToString();
        }

        if (!satisfied && foundVersion.Length > 0)
        {
            Log($"Found WindowsDesktop {foundVersion} but requires >= {RuntimeVersion}. Reinstall needed.");
        }

        return satisfied;
    }

    private static string? FindDotnetRoot()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var machine = Path.Combine(programFiles, "dotnet");
        if (Directory.Exists(machine))
        {
            return machine;
        }

        var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env;
        }

        return null;
    }

    private static int InstallRuntime()
    {
        var installerPath = Path.Combine(Path.GetTempPath(), $"windowsdesktop-runtime-{RuntimeVersion}-win-x64.exe");
        if (!File.Exists(installerPath) || new FileInfo(installerPath).Length == 0)
        {
            Log($"Downloading {RuntimeInstallerUrl}");
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            using var response = client.GetAsync(RuntimeInstallerUrl).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var source = response.Content.ReadAsStream();
            using var target = File.Create(installerPath);
            source.CopyTo(target);
            Log("Download complete.");
        }

        var elevated = IsElevated();
        var arguments = "/install /quiet /norestart";
        Log($"Running installer (elevated={elevated}): {installerPath} {arguments}");

        var psi = new ProcessStartInfo(installerPath)
        {
            Arguments = arguments,
            UseShellExecute = true,
            Verb = elevated ? string.Empty : "runas"
        };

        try
        {
            var process = Process.Start(psi);
            if (process is null)
            {
                return 1;
            }

            process.WaitForExit();
            Log($"Installer exit code: {process.ExitCode}");
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log("User declined elevation.");
            return 1223;
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the launcher.
        }
    }

    private static void ShowError(string message)
    {
        try
        {
            const uint MB_ICONERROR = 0x00000010;
            MessageBoxW(IntPtr.Zero, message, "Blue Oath 启动器", MB_ICONERROR);
        }
        catch
        {
            // Ignore UI failures.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
}
