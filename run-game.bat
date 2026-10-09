@echo off
setlocal

echo ============================================================
echo   Blue Oath JP offline - one-click launcher
echo   Press Ctrl+C to stop (auto cleanup)
echo   Log: native\bin-x86\BlueOath.Payload.log
echo   Optional cheats, off by default:
echo     run-game.bat -CheatProduction -CheatStrength -CheatVow -CheatMood -CheatMaterials
echo   Optional original game rules, off by default = free resources, unlimited shop:
echo     run-game.bat -RealResourceCost -RealShopStock
echo   Back up runtime\jp\profiles.db before enabling cheats/rules or upgrading.
echo ============================================================
echo.

rem -SkipBuild is always passed below; drop a user-supplied copy (PowerShell rejects a repeated switch).
set "BO_ARGS=%*"
if defined BO_ARGS set "BO_ARGS=%BO_ARGS:-SkipBuild=%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\debug-game.ps1" -SkipBuild %BO_ARGS%

echo.
echo [run-game] stopped. Log: native\bin-x86\BlueOath.Payload.log
pause
