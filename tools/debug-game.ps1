param(
  [ValidateSet('redirect')][string]$Mode = 'redirect',
  [switch]$SkipBuild,
  [switch]$KeepLog,
  # Same defaults as the launcher settings page: every cheat except Battle is ON (the offline
  # version's original rules). -NoCheats turns all cheats off, -NoCheatX turns one off, -CheatX
  # turns one on (also after -NoCheats). Every option is forwarded explicitly (--x=on|off).
  [switch]$NoCheats,
  [switch]$CheatProduction, [switch]$NoCheatProduction,
  [switch]$CheatStrength,   [switch]$NoCheatStrength,
  [switch]$CheatVow,        [switch]$NoCheatVow,
  [switch]$CheatMood,       [switch]$NoCheatMood,
  [switch]$CheatMedals,     [switch]$NoCheatMedals,
  [switch]$CheatDrops,      [switch]$NoCheatDrops,
  [switch]$CheatSweep,      [switch]$NoCheatSweep,
  [switch]$CheatBattle,     [switch]$NoCheatBattle,
  # Original-rule switches, same as the launcher settings page: --real-resource-cost (real
  # resource costs, ON by default) and --real-shop-stock (real shop stock, off by default).
  [switch]$RealResourceCost, [switch]$NoRealResourceCost,
  [switch]$RealShopStock,    [switch]$NoRealShopStock,
  # Experimental (off by default): --exp-full-battle-stats (full battle stat list; needs Battle off).
  [switch]$FullBattleStats
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------- build ----
if (-not $SkipBuild) {
  Write-Host '[1/5] building native payload (debug hooks)...' -ForegroundColor Cyan
  & (Join-Path $PSScriptRoot 'build-native.ps1') -DebugHooks
  Write-Host '[1/5] building local server...' -ForegroundColor Cyan
  & dotnet build (Join-Path $root 'src\BlueOath.Server\BlueOath.Server.csproj') -c Debug *> $null
  if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
} else {
  Write-Host '[1/5] build skipped' -ForegroundColor DarkGray
}

# ------------------------------------------------------------ run paths ----
$stamp     = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot   = Join-Path $root "runtime\debug\$stamp"
$dataRoot  = Join-Path $root 'runtime\jp'
$clientPath = Join-Path $root 'blueoath\blueoath'
$tlsRoot   = Join-Path $runRoot 'tls'
$traffic   = Join-Path $runRoot 'traffic'
$serverOut = Join-Path $runRoot 'server.stdout.log'
$serverErr = Join-Path $runRoot 'server.stderr.log'
$proxyErr  = Join-Path $runRoot 'proxy.stderr.log'
$payloadLog = Join-Path $root 'native\bin-x86\BlueOath.Payload.log'
$saveDb    = Join-Path $dataRoot 'profiles.db'

# Cheat and original-rule options forwarded to the server as --x=on|off; the server echoes
# all of them in the "cheats" object of its ready JSON (Key = echo key).
# On / Off name the script switches that turn the option on / off; Cheat marks the options -NoCheats turns off.
$options = @(
  @{ Switch = '--cheat-production'; Key = 'production'; Default = $true; Cheat = $true; On = 'CheatProduction'; Off = 'NoCheatProduction' }
  @{ Switch = '--cheat-strength'; Key = 'strength'; Default = $true; Cheat = $true; On = 'CheatStrength'; Off = 'NoCheatStrength' }
  @{ Switch = '--cheat-vow'; Key = 'vow'; Default = $true; Cheat = $true; On = 'CheatVow'; Off = 'NoCheatVow' }
  @{ Switch = '--cheat-mood'; Key = 'mood'; Default = $true; Cheat = $true; On = 'CheatMood'; Off = 'NoCheatMood' }
  @{ Switch = '--cheat-medals'; Key = 'medals'; Default = $true; Cheat = $true; On = 'CheatMedals'; Off = 'NoCheatMedals' }
  @{ Switch = '--cheat-drops'; Key = 'drops'; Default = $true; Cheat = $true; On = 'CheatDrops'; Off = 'NoCheatDrops' }
  @{ Switch = '--cheat-sweep'; Key = 'sweep'; Default = $true; Cheat = $true; On = 'CheatSweep'; Off = 'NoCheatSweep' }
  @{ Switch = '--cheat-battle'; Key = 'battle'; Default = $false; Cheat = $true; On = 'CheatBattle'; Off = 'NoCheatBattle' }
  @{ Switch = '--real-resource-cost'; Key = 'realResourceCost'; Default = $true; On = 'RealResourceCost'; Off = 'NoRealResourceCost' }
  @{ Switch = '--real-shop-stock'; Key = 'realShopStock'; Default = $false; On = 'RealShopStock'; Off = 'NoRealShopStock' }
  @{ Switch = '--exp-full-battle-stats'; Key = 'fullBattleStats'; Default = $false; On = 'FullBattleStats' }
)
$cheatArgs = @()
$enabledKeys = @()
foreach ($option in $options) {
  $on = $option.Default
  if ($option.Cheat -and $NoCheats.IsPresent) { $on = $false }
  if ((Get-Variable -Name $option.On -ValueOnly).IsPresent) { $on = $true }
  if ($option.Off -and (Get-Variable -Name $option.Off -ValueOnly).IsPresent) { $on = $false }
  $cheatArgs += ($option.Switch + '=' + $(if ($on) { 'on' } else { 'off' }))
  if ($on) { $enabledKeys += $option.Key }
}
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
New-Item -ItemType Directory -Path $tlsRoot -Force | Out-Null
if (-not $KeepLog -and (Test-Path -LiteralPath $payloadLog)) { Remove-Item -LiteralPath $payloadLog -Force }

# --------------------------------------- cleanup leftover processes ----
# Kill leftover server/game processes from a previous abnormal exit so they
# don't keep holding port 7201/7080 and block the next startup.
Write-Host '[cleanup] killing leftover server/game processes...' -ForegroundColor Cyan
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
  Where-Object { $_.CommandLine -match 'BlueOath\.Server\.dll' } |
  ForEach-Object {
    Write-Host ('  killing leftover server PID ' + $_.ProcessId) -ForegroundColor DarkGray
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
  }
Get-Process -Name 'blueoath', 'clsy' -ErrorAction SilentlyContinue |
  ForEach-Object {
    Write-Host ('  killing leftover game PID ' + $_.Id) -ForegroundColor DarkGray
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
  }

$serverDll = Join-Path $root 'src\BlueOath.Server\bin\Debug\net8.0\BlueOath.Server.dll'
if (-not (Test-Path -LiteralPath $serverDll)) { throw "Server assembly missing: $serverDll" }

$server = $null
$proxy  = $null
$gamePid = $null
try {
  # ------------------------------------------------------- start server ----
  Write-Host '[2/5] starting local server...' -ForegroundColor Cyan
  $materialLine = & dotnet $serverDll '--tls-material-only' "--tls-output=$tlsRoot" 2>&1 | Out-String
  if ($LASTEXITCODE -ne 0) { throw "TLS material generation failed: $materialLine" }
  $material = $materialLine | ConvertFrom-Json

  $serverArgs = @($serverDll,  '--port=0', '--region=jp', "--data=$dataRoot", "--client-path=$clientPath", "--capture=$traffic", '--game-login-port=7201', '--gm-port=9780')
  $serverArgs += $cheatArgs
  $serverStart = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
  $serverStart.UseShellExecute = $false
  $serverStart.CreateNoWindow = $true
  $serverStart.RedirectStandardOutput = $true
  $serverStart.RedirectStandardError = $true
  $serverStart.Arguments = ($serverArgs | ForEach-Object {
    if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
  }) -join ' '
  $server = [System.Diagnostics.Process]::Start($serverStart)
  $readyTask = $server.StandardOutput.ReadLineAsync()
  if (-not $readyTask.Wait([TimeSpan]::FromSeconds(15))) { throw 'Server did not report ready within 15s.' }
  $ready = ($readyTask.Result | ConvertFrom-Json)
  if (-not $ready.ready) { throw "Unexpected server ready response: $($readyTask.Result)" }

  # ------------------------------------------------------- start proxy ----
  Write-Host '[3/5] starting TLS loopback proxy...' -ForegroundColor Cyan
  $proxyStart = [System.Diagnostics.ProcessStartInfo]::new('python')
  $proxyStart.UseShellExecute = $false
  $proxyStart.CreateNoWindow = $true
  $proxyStart.RedirectStandardOutput = $true
  $proxyStart.RedirectStandardError = $true
  $proxyArgs = @(
    (Join-Path $PSScriptRoot 'tls-loopback-proxy.py'), '--port', '0',
    '--backend-port', [string]$ready.port, '--cert', [string]$material.leafPem,
    '--key', [string]$material.leafKeyPem
  )
  $proxyStart.Arguments = ($proxyArgs | ForEach-Object {
    if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
  }) -join ' '
  $proxy = [System.Diagnostics.Process]::Start($proxyStart)
  $proxyReadyTask = $proxy.StandardOutput.ReadLineAsync()
  if (-not $proxyReadyTask.Wait([TimeSpan]::FromSeconds(10))) { throw 'Proxy did not report ready within 10s.' }
  $proxyReady = ($proxyReadyTask.Result | ConvertFrom-Json)
  if (-not $proxyReady.ready) { throw "Unexpected proxy ready response: $($proxyReadyTask.Result)" }

  # ------------------------------------------------------- inject game ----
  Write-Host '[4/5] injecting game (mode='$Mode')...' -ForegroundColor Cyan
  $injectArgs = @(
    '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'inject-game.ps1'),
    '-Region', 'jp', '-Redirect', '-Port', [string]$proxyReady.port,
    '-HttpPort', [string]$ready.port, '-AllowUntrusted'
  )
  $injectOutput = & powershell @injectArgs 2>&1 | Out-String
  if ($LASTEXITCODE -ne 0) { throw "Injector failed: $injectOutput" }
  if ($injectOutput -match 'Injected PID\s+(\d+)') {
    $gamePid = [int]$Matches[1]
  } else {
    throw "Injector did not report the game PID: $injectOutput"
  }

  # ------------------------------------------------------- status ---------
  Write-Host ''
  Write-Host ('Game injected (PID ' + $gamePid + '). Mode=' + $Mode + '.') -ForegroundColor Green
  Write-Host ('  server http port : ' + $ready.port) -ForegroundColor Green
  Write-Host ('  proxy tls port   : ' + $proxyReady.port) -ForegroundColor Green
  Write-Host ('  game login port  : 7201') -ForegroundColor Green
  Write-Host ('  GM WebUI         : http://localhost:' + $ready.gmPort) -ForegroundColor Green
  Write-Host ('  payload log (live): ' + $payloadLog) -ForegroundColor Green
  Write-Host ('  run dir (server/proxy logs): ' + $runRoot) -ForegroundColor Green
  Write-Host ('  save db          : ' + $saveDb + '  (back up before upgrading or enabling cheats/rules)') -ForegroundColor Green
  $requestedText = if ($enabledKeys.Count -gt 0) { $enabledKeys -join ', ' } else { 'none (original game rules)' }
  Write-Host ('  cheats/rules requested : ' + $requestedText) -ForegroundColor Yellow
  if ($null -eq $ready.cheats) {
    Write-Host '  WARNING: server did not echo "cheats" in its ready JSON; probably an old server build (run dotnet build).' -ForegroundColor Yellow
  } else {
    $echoKeys = @($ready.cheats.PSObject.Properties.Name)
    $echoed = @($options | Where-Object { $ready.cheats.($_.Key) -eq $true } | ForEach-Object { $_.Key })
    $echoText = if ($echoed.Count -gt 0) { $echoed -join ', ' } else { 'none (original game rules)' }
    Write-Host ('  cheats/rules (server)  : ' + $echoText) -ForegroundColor Yellow
    # Each option is echoed under its own key; a missing key means the server does not know the switch.
    $unknown = @($options | Where-Object { $echoKeys -notcontains $_.Key } | ForEach-Object { $_.Switch })
    if ($unknown.Count -gt 0) {
      Write-Host ('  WARNING: server did not echo ' + ($unknown -join ' ') + '; probably an old server build (run dotnet build).') -ForegroundColor Yellow
    } elseif (($echoed -join ',') -ne ($enabledKeys -join ',')) {
      Write-Host '  WARNING: the server confirmed different cheats/rules than requested.' -ForegroundColor Yellow
    }
  }
  Write-Host ''
  Write-Host '[5/5] watching payload log live. Press Ctrl+C to stop and clean up.' -ForegroundColor Yellow
  Write-Host ('====================================================')

  if (-not (Test-Path -LiteralPath $payloadLog)) { New-Item -ItemType File -Path $payloadLog -Force | Out-Null }
  try {
    Get-Content -LiteralPath $payloadLog -Wait -Tail 0 | ForEach-Object { Write-Host $_ }
  } catch {
    # Ctrl+C or the file was removed; fall through to cleanup.
  }
}
finally {
  Write-Host ''
  Write-Host 'cleaning up...' -ForegroundColor Yellow
  if ($gamePid) {
    $game = Get-Process -Id $gamePid -ErrorAction SilentlyContinue
    if ($game) { Stop-Process -Id $gamePid -Force -ErrorAction SilentlyContinue }
  }
  if ($server -and -not $server.HasExited) { $server.Kill(); $server.WaitForExit(5000) | Out-Null }
  if ($proxy -and -not $proxy.HasExited) { $proxy.Kill(); $proxy.WaitForExit(5000) | Out-Null }
  if ($proxy) {
    $proxyErrors = $proxy.StandardError.ReadToEnd()
    if ($proxyErrors) { Set-Content -LiteralPath $proxyErr -Encoding UTF8 -Value $proxyErrors }
    $proxy.Dispose()
  }
  if ($server) {
    $remainingOut = $server.StandardOutput.ReadToEnd()
    $remainingErr = $server.StandardError.ReadToEnd()
    if ($remainingOut) { Add-Content -LiteralPath $serverOut -Encoding UTF8 -Value $remainingOut }
    if ($remainingErr) { Set-Content -LiteralPath $serverErr -Encoding UTF8 -Value $remainingErr }
    $server.Dispose()
  }
  Write-Host ('done. logs: ' + $runRoot) -ForegroundColor Green
}
