using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueOath.Launcher.Wpf.Models;

namespace BlueOath.Launcher.Wpf.Services;

public enum ProcessStage
{
    Idle,
    CleaningUp,
    GeneratingTls,
    StartingServer,
    StartingProxy,
    InjectingGame,
    Running,
    Stopping,
    Failed
}

public enum ProcessKind
{
    Server,
    Proxy,
    Game,
    Injector
}

public class ProcessStateInfo : INotifyPropertyChanged
{
    private ProcessKind _kind;
    private int _pid;
    private bool _isRunning;
    private DateTime? _startTime;

    public ProcessKind Kind
    {
        get => _kind;
        set { _kind = value; OnPropertyChanged(); }
    }

    public int Pid
    {
        get => _pid;
        set { _pid = value; OnPropertyChanged(); }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set { _isRunning = value; OnPropertyChanged(); }
    }

    public DateTime? StartTime
    {
        get => _startTime;
        set { _startTime = value; OnPropertyChanged(); }
    }

    public string DisplayName => Kind switch
    {
        ProcessKind.Server => "服务器",
        ProcessKind.Proxy => "TLS 代理",
        ProcessKind.Game => "游戏客户端",
        ProcessKind.Injector => "注入器",
        _ => Kind.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class ProcessManager
{
    private readonly string _rootDir;
    private Models.SettingsConfig _settings;
    private Process? _serverProcess;
    private Process? _proxyProcess;
    private int _gamePid;
    private string _lastError = "";

    /// <summary>
    /// 服务端 ready JSON 里回显的作弊与原规则开关（回显键 → 是否开启）；旧版服务端没有 cheats 键时为 null，
    /// 缺少某个键说明服务端不认识对应开关。
    /// </summary>
    private IReadOnlyDictionary<string, bool>? _serverCheatEcho;

    private readonly ObservableCollection<ProcessStateInfo> _processStates = new();
    private readonly ObservableCollection<LogEntry> _serverLogs = new();
    private readonly ObservableCollection<LogEntry> _proxyLogs = new();
    private readonly ObservableCollection<LogEntry> _clientLogs = new();
    private readonly ObservableCollection<LogEntry> _systemLogs = new();

    private CancellationTokenSource? _cts;

    public ObservableCollection<ProcessStateInfo> ProcessStates => _processStates;
    public ObservableCollection<LogEntry> ServerLogs => _serverLogs;
    public ObservableCollection<LogEntry> ProxyLogs => _proxyLogs;
    public ObservableCollection<LogEntry> ClientLogs => _clientLogs;
    public ObservableCollection<LogEntry> SystemLogs => _systemLogs;

    private ProcessStage _stage = ProcessStage.Idle;
    public ProcessStage Stage
    {
        get => _stage;
        private set
        {
            _stage = value;
            StageChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<ProcessStage>? StageChanged;
    public event EventHandler<LogEntry>? LogReceived;

    public string LastError => _lastError;

    public bool IsRunning => _stage is ProcessStage.StartingServer or ProcessStage.StartingProxy
        or ProcessStage.InjectingGame or ProcessStage.Running;

    public ProcessManager(string rootDir, Models.SettingsConfig settings)
    {
        _rootDir = rootDir;
        _settings = settings;
    }

    public string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (Path.IsPathRooted(path)) return path;
        return Path.GetFullPath(Path.Combine(_rootDir, path));
    }

    /// <summary>游戏客户端目录（含 blueoath_Data），作为服务端 --client-path 参数。</summary>
    public string ResolveClientPath() => ResolvePath(_settings.GameClientPath);

    public string MakeRelativePath(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return absolutePath;
        if (!Path.IsPathRooted(absolutePath)) return absolutePath;
        var rootUri = new Uri(_rootDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var pathUri = new Uri(absolutePath);
        if (rootUri.Scheme != pathUri.Scheme) return absolutePath;
        var relative = rootUri.MakeRelativeUri(pathUri).ToString();
        return Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar);
    }

    public void UpdateSettings(Models.SettingsConfig settings)
    {
        _settings = settings;
    }

    public string? ValidatePaths(LaunchConfig config, bool startServer)
    {
        if (startServer)
        {
            var serverDll = ResolvePath(_settings.ServerDllPath);
            if (!File.Exists(serverDll))
                return $"服务器 DLL 未找到: {serverDll}";
        }
        var proxyScript = ResolvePath(_settings.ProxyScriptPath);
        if (!File.Exists(proxyScript))
            return $"代理脚本未找到: {proxyScript}";
        var injector = ResolvePath(_settings.InjectorPath);
        if (!File.Exists(injector))
            return $"注入器未找到: {injector}";
        var payload = ResolvePath(_settings.PayloadPath);
        if (!File.Exists(payload))
            return $"Payload DLL 未找到: {payload}";
        var baseline = ResolvePath(_settings.BaselinePath);
        if (!File.Exists(baseline))
            return $"基线文件未找到: {baseline}";

        var dataRoot = ResolvePath(_settings.DataRoot);
        if (!Directory.Exists(dataRoot))
            Directory.CreateDirectory(dataRoot);

        string clientExe = config.Region == "cn" ? "clsy.exe" : "blueoath.exe";
        var clientDir = ResolvePath(_settings.GameClientPath);
        string clientExePath = Path.Combine(clientDir, clientExe);
        if (!File.Exists(clientExePath))
            return $"游戏客户端未找到: {clientExePath}";

        var clientError = ValidateClientBuild(config.Region, clientExePath, baseline);
        if (clientError is not null)
            return clientError;

        return null;
    }

    private static string? ValidateClientBuild(string region, string exePath, string baselinePath)
    {
        if (!File.Exists(baselinePath))
            return $"基线文件未找到: {baselinePath}";

        string expectedVersion;
        string expectedHash;
        try
        {
            var json = File.ReadAllText(baselinePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return $"基线文件格式不正确: {baselinePath}";

            var expected = doc.RootElement.EnumerateArray()
                .FirstOrDefault(entry =>
                    entry.TryGetProperty("region", out var regionElement) &&
                    regionElement.GetString()?.Equals(region, StringComparison.OrdinalIgnoreCase) == true);
            if (expected.ValueKind == JsonValueKind.Undefined)
                return $"未在基线中找到 {region} 服的版本信息: {baselinePath}";

            if (!expected.TryGetProperty("version", out var versionElement))
                return $"基线条目缺少 version 字段: {baselinePath}";

            expectedVersion = versionElement.GetString() ?? "";
            if (!expected.TryGetProperty("files", out var filesElement) ||
                filesElement.ValueKind != JsonValueKind.Object)
                return $"基线条目缺少 files 字段: {baselinePath}";

            expectedHash = "";
            var executableName = Path.GetFileName(exePath);
            foreach (var file in filesElement.EnumerateObject())
            {
                if (!Path.GetFileName(file.Name).Equals(executableName, StringComparison.OrdinalIgnoreCase))
                    continue;

                expectedHash = file.Value.GetString() ?? "";
                break;
            }
        }
        catch (Exception ex)
        {
            return $"读取基线文件失败: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(expectedVersion))
            return $"基线中的 {region} 服版本为空: {baselinePath}";
        if (string.IsNullOrWhiteSpace(expectedHash))
            return $"基线中缺少 {Path.GetFileName(exePath)} 的文件指纹: {baselinePath}";

        string actualHash;
        try
        {
            using var stream = File.OpenRead(exePath);
            actualHash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex)
        {
            return $"读取客户端文件失败: {ex.Message}";
        }

        if (!actualHash.Equals(expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"客户端构建不匹配：{Path.GetFileName(exePath)} 文件指纹与 {region.ToUpperInvariant()} 服 v{expectedVersion} 基线不一致。";

        return null;
    }

    public async Task LaunchAsync(LaunchConfig config, bool startServer)
    {
        if (IsRunning) return;
        _lastError = "";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            _processStates.Clear();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string runRoot = Path.Combine(_rootDir, "runtime", "debug", stamp);
            string tlsRoot = Path.Combine(runRoot, "tls");
            string dataRoot = ResolvePath(_settings.DataRoot);
            string traffic = Path.Combine(runRoot, "traffic");
            Directory.CreateDirectory(runRoot);
            Directory.CreateDirectory(tlsRoot);

            string payloadPath = ResolvePath(_settings.PayloadPath);
            string payloadLog = Path.Combine(Path.GetDirectoryName(payloadPath) ?? _rootDir, "BlueOath.Payload.log");
            if (!config.KeepLog && File.Exists(payloadLog)) File.Delete(payloadLog);

            Stage = ProcessStage.CleaningUp;
            LogSystem("正在清理残留进程...");
            KillLeftoverProcesses();

            string serverDll = ResolvePath(_settings.ServerDllPath);
            if (!File.Exists(serverDll))
            {
                LogError("服务器程序集未找到: " + serverDll);
                Stage = ProcessStage.Failed;
                return;
            }

            Stage = ProcessStage.GeneratingTls;
            LogSystem("正在生成 TLS 证书...");
            (string? leafPem, string? leafKeyPem) = await GenerateTlsMaterial(serverDll, tlsRoot);
            if (leafPem is null || leafKeyPem is null)
            {
                LogError("TLS 证书生成失败。");
                Stage = ProcessStage.Failed;
                return;
            }

            int serverPort = config.ServerPort;
            int gmPort = config.GmPort;

            if (startServer)
            {
                Stage = ProcessStage.StartingServer;
                LogSystem("正在启动本地服务器...");
                var cheatArgs = config.CheatArguments();
                var enabledSwitches = config.EnabledSwitches();
                serverPort = await StartServer(serverDll, dataRoot, traffic, config.GameLoginPort, gmPort,
                    config.ProfileId, config.ProfileName, cheatArgs, token);
                if (serverPort < 0)
                {
                    LogError("服务器启动失败。");
                    Stage = ProcessStage.Failed;
                    return;
                }
                LogSystem($"服务器已启动，端口 {serverPort}，GM 端口 {gmPort}");
                ReportServerCheats(enabledSwitches);
            }
            else
            {
                LogSystem($"跳过服务器启动（期望服务器在端口 {serverPort} 运行）");
                LogSystem($"调试启动不启动服务器：设置页的作弊/规则选项不生效，由外部服务器自己的启动参数决定（{string.Join(" / ", LaunchConfig.Options.Select(option => option.Switch))}）。");
            }

            Stage = ProcessStage.StartingProxy;
            LogSystem("正在启动 TLS 环回代理...");
            int proxyPort = await StartProxy(leafPem, leafKeyPem, serverPort, config.ProxyPort, token);
            if (proxyPort < 0)
            {
                LogError("代理启动失败。");
                Stage = ProcessStage.Failed;
                return;
            }
            LogSystem($"代理已启动，端口 {proxyPort}");

            Stage = ProcessStage.InjectingGame;
            LogSystem("正在注入游戏...");
            int gamePid = await InjectGame(config.Region, proxyPort, serverPort, token);
            if (gamePid < 0)
            {
                LogError("游戏注入失败。");
                Stage = ProcessStage.Failed;
                return;
            }
            _gamePid = gamePid;
            UpdateProcessState(ProcessKind.Game, gamePid, true);
            LogSystem($"游戏已注入 (PID {gamePid})");

            Stage = ProcessStage.Running;
            LogSystem("所有进程已启动，正在监控...");

            StartPayloadLogWatcher(payloadLog, token);
        }
        catch (OperationCanceledException)
        {
            LogSystem("启动已取消。");
        }
        catch (Exception ex)
        {
            LogError($"启动失败: {ex.Message}");
            Stage = ProcessStage.Failed;
        }
    }

    public async Task StopAllAsync()
    {
        await Task.Run(() =>
        {
            Stage = ProcessStage.Stopping;
            LogSystem("正在停止所有进程...");

            _cts?.Cancel();

            if (_gamePid > 0)
            {
                try
                {
                    var game = Process.GetProcessById(_gamePid);
                    if (!game.HasExited) { game.Kill(); game.WaitForExit(5000); }
                }
                catch { }
                UpdateProcessState(ProcessKind.Game, _gamePid, false);
                _gamePid = 0;
            }

            if (_proxyProcess is { HasExited: false })
            {
                _proxyProcess.Kill();
                _proxyProcess.WaitForExit(5000);
                UpdateProcessState(ProcessKind.Proxy, _proxyProcess.Id, false);
                _proxyProcess.Dispose();
                _proxyProcess = null;
            }

            if (_serverProcess is { HasExited: false })
            {
                _serverProcess.Kill();
                _serverProcess.WaitForExit(5000);
                UpdateProcessState(ProcessKind.Server, _serverProcess.Id, false);
                _serverProcess.Dispose();
                _serverProcess = null;
            }

            Stage = ProcessStage.Idle;
            LogSystem("所有进程已停止。");
        });
    }

    private void KillLeftoverProcesses()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'dotnet.exe'");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                try
                {
                    var commandLine = obj["CommandLine"]?.ToString() ?? "";
                    if (commandLine.Contains("BlueOath.Server.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        var pid = Convert.ToInt32(obj["ProcessId"]);
                        var proc = Process.GetProcessById(pid);
                        if (!proc.HasExited)
                        {
                            proc.Kill();
                            proc.WaitForExit(5000);
                            LogSystem($"已清理残留服务器 PID {pid}");
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        foreach (var name in new[] { "blueoath", "clsy" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                        LogSystem($"已清理残留游戏 PID {p.Id}");
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private async Task<(string? leafPem, string? leafKeyPem)> GenerateTlsMaterial(string serverDll, string tlsRoot)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            Arguments = $"\"{serverDll}\" --tls-material-only \"--tls-output={tlsRoot}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        var proc = Process.Start(psi);
        if (proc is null) return (null, null);

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(outputTask, errorTask);
        await proc.WaitForExitAsync();

        string output = outputTask.Result;
        string error = errorTask.Result;

        if (proc.ExitCode != 0)
        {
            LogError($"TLS 证书生成失败 (退出码 {proc.ExitCode}): {error}");
            return (null, null);
        }

        try
        {
            var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            string? leafPem = root.TryGetProperty("leafPem", out var lp) ? lp.GetString() : null;
            string? leafKeyPem = root.TryGetProperty("leafKeyPem", out var lk) ? lk.GetString() : null;
            return (leafPem, leafKeyPem);
        }
        catch
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                try
                {
                    var doc = JsonDocument.Parse(line.Trim());
                    var root = doc.RootElement;
                    if (root.TryGetProperty("ready", out _) && root.TryGetProperty("leafPem", out var lp))
                    {
                        string? lk = root.TryGetProperty("leafKeyPem", out var lkp) ? lkp.GetString() : null;
                        return (lp.GetString(), lk);
                    }
                }
                catch { }
            }
            LogError($"TLS 证书 JSON 解析失败: {output}");
            return (null, null);
        }
    }

    private async Task<int> StartServer(string serverDll, string dataRoot, string traffic,
        int gameLoginPort, int gmPort, string profileId, string profileName,
        IReadOnlyList<string> cheatArgs, CancellationToken token)
    {
        _serverCheatEcho = null;
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _rootDir,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.ArgumentList.Add(serverDll);
        psi.ArgumentList.Add("--port=0");
        psi.ArgumentList.Add("--region=jp");
        psi.ArgumentList.Add("--data=" + dataRoot);
        psi.ArgumentList.Add("--client-path=" + ResolveClientPath());
        psi.ArgumentList.Add("--capture=" + traffic);
        psi.ArgumentList.Add("--game-login-port=" + gameLoginPort);
        psi.ArgumentList.Add("--gm-port=" + gmPort);
        psi.ArgumentList.Add("--profile-id=" + profileId);
        psi.ArgumentList.Add("--profile-name=" + profileName);
        // 作弊与原规则开关逐项显式传 =on / =off（服务端默认作弊全开）；旧版服务端遇到未知参数会静默忽略。
        foreach (var cheat in cheatArgs)
            psi.ArgumentList.Add(cheat);

        _serverProcess = Process.Start(psi);
        if (_serverProcess is null) return -1;

        _processStates.Add(new ProcessStateInfo { Kind = ProcessKind.Server, Pid = _serverProcess.Id, IsRunning = true, StartTime = DateTime.Now });
        AttachProcessOutput(_serverProcess, _serverLogs, "server");
        AttachProcessError(_serverProcess, _serverLogs, "server");

        var tcs = new TaskCompletionSource<int>();
        using var reg = token.Register(() => tcs.TrySetResult(-1));

        _serverProcess.EnableRaisingEvents = true;
        _serverProcess.Exited += (s, e) =>
        {
            if (!tcs.Task.IsCompleted)
            {
                try
                {
                    tcs.TrySetResult(-1);
                }
                catch { }
            }
        };

        _serverProcess.OutputDataReceived += (s, e) =>
        {
            if (e.Data is null) return;
            try
            {
                var doc = JsonDocument.Parse(e.Data);
                var root = doc.RootElement;
                if (root.TryGetProperty("ready", out var ready) && ready.GetBoolean())
                {
                    if (root.TryGetProperty("port", out var port) && !tcs.Task.IsCompleted)
                    {
                        // 先记下作弊回显再完成任务，等待方读到的一定是第一条 ready 行的值。
                        _serverCheatEcho = ParseCheatEcho(root);
                        tcs.TrySetResult(port.GetInt32());
                    }
                }
            }
            catch { }
        };

        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();

        var timeout = Task.Delay(TimeSpan.FromSeconds(20));
        var completed = await Task.WhenAny(tcs.Task, timeout);
        if (completed == timeout)
        {
            LogError("服务器在 20 秒内未报告就绪。");
            return -1;
        }

        var result = await tcs.Task;
        if (result < 0) LogError("服务器进程异常退出。");
        return result;
    }

    /// <summary>读取 ready JSON 的 cheats 回显：{"production":bool,...}（键见 <see cref="LaunchConfig.Options"/>）。</summary>
    private static IReadOnlyDictionary<string, bool>? ParseCheatEcho(JsonElement root)
    {
        if (!root.TryGetProperty("cheats", out var cheats) || cheats.ValueKind != JsonValueKind.Object)
            return null;
        return cheats.EnumerateObject()
            .Where(property => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            .ToDictionary(property => property.Name, property => property.Value.ValueKind == JsonValueKind.True);
    }

    /// <summary>服务端就绪后核对作弊与原规则开关：请求了才输出，旧版服务端没有回显或不认识某个开关时给出警告。</summary>
    private void ReportServerCheats(IReadOnlyList<string> enabledSwitches)
    {
        var echo = _serverCheatEcho;
        if (echo is null)
        {
            LogWarning("服务端未确认作弊/规则参数，可能是旧版服务端（请先 dotnet build）");
            return;
        }

        // 用启动时传出的参数快照比对：启动过程中切到启动页会重新读取设置，config 里的值可能已经变了。
        var unknown = LaunchConfig.Options
            .Where(option => !echo.ContainsKey(option.EchoKey))
            .Select(option => option.Switch)
            .ToList();
        if (unknown.Count > 0)
            LogWarning($"服务端未确认 {string.Join(" / ", unknown)}，可能是旧版服务端（请先 dotnet build）");

        string requested = LaunchConfig.DescribeSwitches(enabledSwitches);
        string confirmed = LaunchConfig.DescribeSwitches(LaunchConfig.Options
            .Where(option => echo.GetValueOrDefault(option.EchoKey))
            .Select(option => option.Switch));
        if (requested == confirmed)
        {
            LogSystem(confirmed.Length == 0
                ? "作弊/规则选项已生效：全部关闭（按原游戏规则）。"
                : $"作弊/规则选项已生效：{confirmed}（{string.Join(" ", enabledSwitches)}）。");
        }
        else
        {
            var requestedText = requested.Length == 0 ? "无" : requested;
            var confirmedText = confirmed.Length == 0 ? "无" : confirmed;
            LogWarning($"服务端确认的作弊/规则选项与设置不一致：设置为 {requestedText}，服务端为 {confirmedText}");
        }
    }

    private async Task<int> StartProxy(string leafPem, string leafKeyPem, int serverPort, int proxyPort, CancellationToken token)
    {
        string proxyScript = ResolvePath(_settings.ProxyScriptPath);
        string resolvedPythonPath = ResolvePath(_settings.PythonPath);
        string pythonPath = File.Exists(resolvedPythonPath) ? resolvedPythonPath : _settings.PythonPath;
        var psi = new ProcessStartInfo(pythonPath)
        {
            Arguments = $"\"{proxyScript}\" --port {proxyPort} --backend-port {serverPort} --cert \"{leafPem}\" --key \"{leafKeyPem}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _rootDir,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        _proxyProcess = Process.Start(psi);
        if (_proxyProcess is null) return -1;

        _processStates.Add(new ProcessStateInfo { Kind = ProcessKind.Proxy, Pid = _proxyProcess.Id, IsRunning = true, StartTime = DateTime.Now });
        AttachProcessOutput(_proxyProcess, _proxyLogs, "proxy");
        AttachProcessError(_proxyProcess, _proxyLogs, "proxy");

        var tcs = new TaskCompletionSource<int>();
        using var reg = token.Register(() => tcs.TrySetResult(-1));

        _proxyProcess.EnableRaisingEvents = true;
        _proxyProcess.Exited += (s, e) =>
        {
            if (!tcs.Task.IsCompleted)
            {
                try { tcs.TrySetResult(-1); } catch { }
            }
        };

        _proxyProcess.OutputDataReceived += (s, e) =>
        {
            if (e.Data is null) return;
            try
            {
                var doc = JsonDocument.Parse(e.Data);
                var root = doc.RootElement;
                if (root.TryGetProperty("ready", out var ready) && ready.GetBoolean())
                {
                    if (root.TryGetProperty("port", out var port))
                    {
                        tcs.TrySetResult(port.GetInt32());
                    }
                }
            }
            catch { }
        };

        _proxyProcess.BeginOutputReadLine();
        _proxyProcess.BeginErrorReadLine();

        var timeout = Task.Delay(TimeSpan.FromSeconds(15));
        var completed = await Task.WhenAny(tcs.Task, timeout);
        if (completed == timeout)
        {
            LogError("代理在 15 秒内未报告就绪。");
            return -1;
        }

        var result = await tcs.Task;
        if (result < 0) LogError("代理进程异常退出。");
        return result;
    }

    private async Task<int> InjectGame(string region, int proxyPort, int serverPort, CancellationToken token)
    {
        string clientDir = ResolvePath(_settings.GameClientPath);
        string exe = region == "cn" ? "clsy.exe" : "blueoath.exe";
        string exePath = Path.Combine(clientDir, exe);
        if (!File.Exists(exePath))
        {
            LogError($"客户端未找到: {exePath}");
            return -1;
        }

        string injector = ResolvePath(_settings.InjectorPath);
        string payload = ResolvePath(_settings.PayloadPath);
        string nativeDir = Path.GetDirectoryName(injector) ?? "";
        string bootstrapIni = Path.Combine(nativeDir, "bootstrap.ini");

        if (!File.Exists(injector))
        {
            LogError("注入器未找到，请先构建本地组件。");
            return -1;
        }

        WriteBootstrapIni(bootstrapIni, proxyPort, serverPort);

        string baselinePath = ResolvePath(_settings.BaselinePath);
        string gameHash = "";
        if (File.Exists(baselinePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(baselinePath, token);
                var baseline = JsonDocument.Parse(json).RootElement;
                foreach (var entry in baseline.EnumerateArray())
                {
                    if (entry.TryGetProperty("region", out var r) && r.GetString() == region)
                    {
                        if (entry.TryGetProperty("files", out var files))
                        {
                            foreach (var file in files.EnumerateObject())
                            {
                                if (file.Name.Contains("GameAssembly.dll"))
                                {
                                    gameHash = file.Value.GetString() ?? "";
                                    break;
                                }
                            }
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        var psi = new ProcessStartInfo(injector)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.ArgumentList.Add("--exe=" + exePath);
        psi.ArgumentList.Add("--payload=" + payload);
        psi.ArgumentList.Add("--game-hash=" + gameHash);

        var proc = Process.Start(psi);
        if (proc is null) return -1;

        _processStates.Add(new ProcessStateInfo { Kind = ProcessKind.Injector, Pid = proc.Id, IsRunning = true, StartTime = DateTime.Now });

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(outputTask, errorTask);
        await proc.WaitForExitAsync(token);

        string output = outputTask.Result;
        string error = errorTask.Result;

        UpdateProcessState(ProcessKind.Injector, proc.Id, false);

        if (!string.IsNullOrEmpty(error))
        {
            foreach (var line in error.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                _systemLogs.Add(new LogEntry { Source = "injector", Level = "error", Content = line.Trim() });
        }

        if (!string.IsNullOrEmpty(output))
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                _systemLogs.Add(new LogEntry { Source = "injector", Content = line.Trim() });
        }

        if (proc.ExitCode != 0)
        {
            LogError($"注入器退出，错误码 {proc.ExitCode}");
            return -1;
        }

        var match = System.Text.RegularExpressions.Regex.Match(output + error, @"Injected PID\s+(\d+)");
        if (match.Success)
        {
            return int.Parse(match.Groups[1].Value);
        }

        LogError("注入器未报告游戏 PID。");
        return -1;
    }

    private void WriteBootstrapIni(string path, int proxyPort, int serverPort)
    {
        var lines = new[]
        {
            "[redirect]",
            "enabled=1",
            $"port={proxyPort}",
            $"http_port={serverPort}",
            "[trust]",
            "allow_untrusted=1",
            "[debug]",
            "diagnostics=1"
        };
        File.WriteAllLines(path, lines);
    }

    private string GetCnClientDir()
    {
        foreach (var dir in Directory.GetDirectories(_rootDir))
        {
            var clsy = Path.Combine(dir, "clsy");
            if (Directory.Exists(clsy)) return clsy;
        }
        throw new DirectoryNotFoundException("CN client directory not found");
    }

    private void AttachProcessOutput(Process proc, ObservableCollection<LogEntry> log, string source)
    {
        proc.OutputDataReceived += (s, e) =>
        {
            if (e.Data is not null)
            {
                App.Current.Dispatcher.BeginInvoke(() =>
                {
                    log.Add(new LogEntry { Source = source, Content = e.Data });
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        };
    }

    private void AttachProcessError(Process proc, ObservableCollection<LogEntry> log, string source)
    {
        proc.ErrorDataReceived += (s, e) =>
        {
            if (e.Data is not null)
            {
                App.Current.Dispatcher.BeginInvoke(() =>
                {
                    log.Add(new LogEntry { Source = source, Level = "error", Content = e.Data });
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        };
    }

    private void StartPayloadLogWatcher(string payloadLog, CancellationToken token)
    {
        Task.Run(() =>
        {
            if (!File.Exists(payloadLog))
            {
                File.WriteAllText(payloadLog, "");
            }

            long lastPosition = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(payloadLog))
                    {
                        var info = new FileInfo(payloadLog);
                        if (info.Length > lastPosition)
                        {
                            using var fs = new FileStream(payloadLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            fs.Seek(lastPosition, SeekOrigin.Begin);
                            using var sr = new StreamReader(fs);
                            while (sr.ReadLine() is { } line)
                            {
                                App.Current.Dispatcher.BeginInvoke(() =>
                                {
                                    _clientLogs.Add(new LogEntry { Source = "client", Content = line });
                                }, System.Windows.Threading.DispatcherPriority.Background);
                            }
                            lastPosition = fs.Position;
                        }
                    }
                }
                catch { }
                Task.Delay(500, token).GetAwaiter().GetResult();
            }
        }, token);
    }

    private void UpdateProcessState(ProcessKind kind, int pid, bool running)
    {
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            var existing = _processStates.FirstOrDefault(p => p.Kind == kind);
            if (existing != null)
            {
                existing.Pid = pid;
                existing.IsRunning = running;
                if (!running) existing.StartTime = null;
            }
            else if (running)
            {
                _processStates.Add(new ProcessStateInfo { Kind = kind, Pid = pid, IsRunning = true, StartTime = DateTime.Now });
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void LogSystem(string message)
    {
        var entry = new LogEntry { Source = "system", Content = message };
        App.Current.Dispatcher.BeginInvoke(() => _systemLogs.Add(entry), System.Windows.Threading.DispatcherPriority.Background);
        LogReceived?.Invoke(this, entry);
    }

    // 警告只进系统日志，不写 _lastError，不会让启动判为失败或弹出失败对话框。
    private void LogWarning(string message)
    {
        var entry = new LogEntry { Source = "system", Level = "warning", Content = message };
        App.Current.Dispatcher.BeginInvoke(() => _systemLogs.Add(entry), System.Windows.Threading.DispatcherPriority.Background);
        LogReceived?.Invoke(this, entry);
    }

    private void LogError(string message)
    {
        _lastError = message;
        var entry = new LogEntry { Source = "system", Level = "error", Content = message };
        App.Current.Dispatcher.BeginInvoke(() => _systemLogs.Add(entry), System.Windows.Threading.DispatcherPriority.Background);
        LogReceived?.Invoke(this, entry);
    }
}
