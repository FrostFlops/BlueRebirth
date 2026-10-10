using System.Diagnostics;
using System.Text.Json;

var root = FindRoot();
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var outputArgIndex = Array.FindIndex(args, a => a.Equals("--output", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase));
var outputDir = outputArgIndex >= 0
    ? GetOptionValue(args, outputArgIndex, "--output")
    : Path.Combine(root, "release", stamp);

var configurationArgIndex = Array.FindIndex(args, a => a.Equals("--configuration", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--configuration=", StringComparison.OrdinalIgnoreCase));
var configuration = configurationArgIndex >= 0
    ? GetOptionValue(args, configurationArgIndex, "--configuration")
    : "Release";
var updateManifestArgIndex = Array.FindIndex(args, a => a.Equals("--update-manifest-url", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--update-manifest-url=", StringComparison.OrdinalIgnoreCase));
var updateManifestUrl = updateManifestArgIndex >= 0
    ? GetOptionValue(args, updateManifestArgIndex, "--update-manifest-url")
    : LoadUpdateManifestUrl(root, configuration);
if (!string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase))
{
    configuration = "Release";
}

var skipBuild = args.Contains("--skip-build", StringComparer.OrdinalIgnoreCase);
var skipNative = args.Contains("--skip-native", StringComparer.OrdinalIgnoreCase);
var incrementVersion = args.Contains("--increment-version", StringComparer.OrdinalIgnoreCase);
if (skipBuild && incrementVersion)
{
    Console.Error.WriteLine("--increment-version cannot be combined with --skip-build.");
    return 2;
}

Console.WriteLine($"=== Blue Oath Release Publisher ===");
Console.WriteLine($"  Output: {outputDir}");
Console.WriteLine($"  Increment launcher version: {(incrementVersion ? "yes" : "no")}");
Console.WriteLine();

Directory.CreateDirectory(outputDir);

// Step 1: Build native components (product config)
if (!skipNative)
{
    Console.WriteLine("[1/5] Building native components (product)...");
    var buildNativePs1 = Path.Combine(root, "tools", "build-native.ps1");
    if (!File.Exists(buildNativePs1))
    {
        Console.Error.WriteLine("  ERROR: build-native.ps1 not found");
        return 1;
    }
    var psi = new ProcessStartInfo("powershell")
    {
        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{buildNativePs1}\" -Configuration {configuration} " +
                    (string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase) ? "-DebugHooks" : ""),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    var proc = Process.Start(psi)!;
    proc.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"  {e.Data}"); };
    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine($"  {e.Data}"); };
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    await proc.WaitForExitAsync();
    if (proc.ExitCode != 0)
    {
        Console.Error.WriteLine("  Native build failed.");
        return 1;
    }
    Console.WriteLine("  Native build OK.");
}
else
{
    Console.WriteLine("[1/5] Native build skipped.");
}

// Step 2: Publish server
if (!skipBuild)
{
    Console.WriteLine("[2/5] Publishing server...");
    var serverProj = Path.Combine(root, "src", "BlueOath.Server", "BlueOath.Server.csproj");
    var serverOutput = Path.Combine(outputDir, "server");
    var psi = new ProcessStartInfo("dotnet")
    {
        Arguments = $"publish \"{serverProj}\" -c {configuration} -o \"{serverOutput}\"",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    var proc = Process.Start(psi)!;
    proc.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"  {e.Data}"); };
    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine($"  {e.Data}"); };
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    await proc.WaitForExitAsync();
    if (proc.ExitCode != 0)
    {
        Console.Error.WriteLine("  Server publish failed.");
        return 1;
    }
    Console.WriteLine("  Server publish OK.");
}
else
{
    Console.WriteLine("[2/5] Server publish skipped.");
}

// Step 3: Publish WPF launcher
if (!skipBuild)
{
    Console.WriteLine("[3/5] Publishing WPF launcher...");
    var launcherProj = Path.Combine(root, "src", "BlueOath.Launcher.Wpf", "BlueOath.Launcher.Wpf.csproj");
    var launcherOutput = Path.Combine(outputDir, "launcher");

    if (incrementVersion)
    {
        var incrementPsi = new ProcessStartInfo("dotnet")
        {
            Arguments = $"msbuild \"{launcherProj}\" -t:IncrementLauncherVersion",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var incrementProc = Process.Start(incrementPsi)!;
        incrementProc.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"  {e.Data}"); };
        incrementProc.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine($"  {e.Data}"); };
        incrementProc.BeginOutputReadLine();
        incrementProc.BeginErrorReadLine();
        await incrementProc.WaitForExitAsync();
        if (incrementProc.ExitCode != 0)
        {
            Console.Error.WriteLine("  Launcher version increment failed.");
            return 1;
        }
    }

    var psi = new ProcessStartInfo("dotnet")
    {
        Arguments = $"publish \"{launcherProj}\" -c {configuration} -o \"{launcherOutput}\"",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    var proc = Process.Start(psi)!;
    proc.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"  {e.Data}"); };
    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine($"  {e.Data}"); };
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    await proc.WaitForExitAsync();
    if (proc.ExitCode != 0)
    {
        Console.Error.WriteLine("  Launcher publish failed.");
        return 1;
    }
    Console.WriteLine("  Launcher publish OK.");

    // Strip debug symbols from the framework-dependent launcher output: they add
    // several megabytes to every release bundle without runtime benefit.
    foreach (var pdb in Directory.GetFiles(launcherOutput, "*.pdb", SearchOption.AllDirectories))
    {
        File.Delete(pdb);
    }

    Console.WriteLine("  Publishing launcher host (AOT bootstrap)...");
    var hostProj = Path.Combine(root, "src", "BlueOath.Launcher.Host", "BlueOath.Launcher.Host.csproj");
    var hostOutput = Path.Combine(outputDir, "host");
    var hostPsi = new ProcessStartInfo("dotnet")
    {
        Arguments = $"publish \"{hostProj}\" -c {configuration} -r win-x64 -o \"{hostOutput}\"",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    var hostProc = Process.Start(hostPsi)!;
    hostProc.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"  {e.Data}"); };
    hostProc.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine($"  {e.Data}"); };
    hostProc.BeginOutputReadLine();
    hostProc.BeginErrorReadLine();
    await hostProc.WaitForExitAsync();
    if (hostProc.ExitCode != 0)
    {
        Console.Error.WriteLine("  Launcher host publish failed.");
        return 1;
    }

    // The host is the package entry point. Publish it under the localized launch
    // name so users run a single native executable, and keep the framework-dependent
    // WPF build beside it in the launcher dir.
    var hostExe = Path.Combine(hostOutput, "BlueOath.Launcher.Host.exe");
    var hostExeRoot = Path.Combine(outputDir, "启动游戏.exe");
    if (File.Exists(hostExe))
    {
        File.Move(hostExe, hostExeRoot, true);
    }
    foreach (var file in Directory.GetFiles(hostOutput))
    {
        // Skip debug symbols; they would balloon the release bundle for no runtime benefit.
        if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        File.Move(file, Path.Combine(outputDir, Path.GetFileName(file)), true);
    }
    Directory.Delete(hostOutput, true);
    Console.WriteLine("  Launcher host publish OK.");
}
else
{
    Console.WriteLine("[3/5] Launcher publish skipped.");
}

// Step 4: Copy runtime files
Console.WriteLine("[4/5] Copying runtime files...");

// Native components
var nativeSrc = Path.Combine(root, "native", "bin-x86");
var nativeDst = Path.Combine(outputDir, "native");
Directory.CreateDirectory(nativeDst);
CopyIfExists(Path.Combine(nativeSrc, "BlueOath.Injector.exe"), nativeDst);
CopyIfExists(Path.Combine(nativeSrc, "BlueOath.Payload.dll"), nativeDst);
CopyIfExists(Path.Combine(root, "native", "bootstrap.ini"), nativeDst);

// TLS proxy script
var toolsDst = Path.Combine(outputDir, "tools");
Directory.CreateDirectory(toolsDst);
CopyIfExists(Path.Combine(root, "tools", "tls-loopback-proxy.py"), toolsDst);

// Baseline
CopyIfExists(Path.Combine(root, "baseline.json"), outputDir);

var modsSrc = Path.Combine(root, "Mods");
if (Directory.Exists(modsSrc))
    CopyDirectory(modsSrc, Path.Combine(outputDir, "Mods"));

Console.WriteLine("  Runtime files copied.");

// Step 5: Bundle Python embeddable
Console.WriteLine("[5/5] Bundling Python...");
var pythonDir = Path.Combine(outputDir, "tools", "python");
Directory.CreateDirectory(pythonDir);
var pythonZip = Path.Combine(root, "tools", "python-3.12.8-embed-amd64.zip");
var cacheDir = Path.Combine(root, "tools", ".cache");
Directory.CreateDirectory(cacheDir);

if (!File.Exists(pythonZip))
{
    Console.WriteLine("  Downloading Python 3.12.8 embeddable...");
    var pythonUrl = "https://www.python.org/ftp/python/3.12.8/python-3.12.8-embed-amd64.zip";
    using var client = new System.Net.Http.HttpClient();
    var response = await client.GetAsync(pythonUrl);
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync();
    await using var fs = File.Create(pythonZip);
    await stream.CopyToAsync(fs);
    Console.WriteLine("  Download complete.");
}

Console.WriteLine("  Extracting Python...");
System.IO.Compression.ZipFile.ExtractToDirectory(pythonZip, pythonDir, true);

// Enable site-packages for embeddable Python (uncomment "import site" in _pth file)
var pthFile = Directory.GetFiles(pythonDir, "*._pth").FirstOrDefault();
if (pthFile is not null)
{
    var content = File.ReadAllText(pthFile);
    content = content.Replace("#import site", "import site");
    File.WriteAllText(pthFile, content);
}

Console.WriteLine("  Python bundled.");

// Step 6: Generate launcher settings
Console.WriteLine("[6/6] Generating launcher settings...");

// The framework-dependent WPF launcher lives in the launcher subdirectory and is
// started by the AOT host executable at the package root. Leave it in place.

var settings = new
{
    // A release bundle is rooted at the launcher directory. Users may place the
    // supported client in the bundle's blueoath folder; when it is absent the
    // launcher asks them to select blueoath.exe/clsy.exe explicitly.
    gameClientPath = "blueoath",
    serverDllPath = "server\\BlueOath.Server.dll",
    pythonPath = "tools\\python\\python.exe",
    injectorPath = "native\\BlueOath.Injector.exe",
    payloadPath = "native\\BlueOath.Payload.dll",
    proxyScriptPath = "tools\\tls-loopback-proxy.py",
    dataRoot = "runtime\\jp",
    baselinePath = "baseline.json",
    updateManifestUrl,
    autoUpdateEnabled = true,
    region = "jp",
    serverPort = 0,
    gameLoginPort = 7201,
    gmPort = 9780,
    skipBuild = true,
    keepLog = false
};

var settingsPath = Path.Combine(outputDir, "launcher-settings.json");
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, jsonOptions));
Console.WriteLine($"  Settings written to {settingsPath}");

if (!File.Exists(settingsPath))
{
    Console.Error.WriteLine("  ERROR: launcher-settings.json was not created.");
    return 1;
}

Console.WriteLine("  Verified: launcher-settings.json exists.");

Console.WriteLine();
Console.WriteLine("=== Publish complete ===");
Console.WriteLine($"  Output: {outputDir}");
return 0;

static string FindRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "blueoath")))
            return current.FullName;
        current = current.Parent;
    }
    return Environment.CurrentDirectory;
}

static void CopyIfExists(string src, string dstDir)
{
    if (File.Exists(src))
    {
        File.Copy(src, Path.Combine(dstDir, Path.GetFileName(src)), true);
        Console.WriteLine($"  Copied: {Path.GetFileName(src)}");
    }
    else
    {
        Console.WriteLine($"  WARNING: not found: {src}");
    }
}

static void CopyDirectory(string src, string dst)
{
    Directory.CreateDirectory(dst);
    foreach (var file in Directory.GetFiles(src))
        File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
    foreach (var dir in Directory.GetDirectories(src))
        CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
}

static string LoadUpdateManifestUrl(string root, string configuration)
{
    var configPath = Path.Combine(root, "launcher-update.json");
    if (!File.Exists(configPath))
        return string.Empty;

    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var propertyName = string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase)
            ? "debugManifestUrl"
            : "releaseManifestUrl";
        return document.RootElement.TryGetProperty(propertyName, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
    catch
    {
        return string.Empty;
    }
}

static string GetOptionValue(string[] options, int index, string optionName)
{
    var option = options[index];
    var prefix = optionName + "=";
    if (option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        return option[prefix.Length..];

    if (index + 1 < options.Length && !options[index + 1].StartsWith("--", StringComparison.Ordinal))
        return options[index + 1];

    return string.Empty;
}
