@echo off
setlocal

echo ============================================================
echo   Blue Oath JP offline - one-click launcher
echo   Press Ctrl+C to stop (auto cleanup)
echo   Log: native\bin-x86\BlueOath.Payload.log
echo   Same defaults as the launcher: every cheat except -CheatBattle is ON, real resource
echo   cost is ON, real shop stock and experimental options are off.
echo     run-game.bat -NoCheats              all cheats off
echo     run-game.bat -NoCheatSweep          one cheat off (also -NoCheatProduction -NoCheatStrength
echo                                         -NoCheatVow -NoCheatMood -NoCheatMedals -NoCheatDrops)
echo     run-game.bat -NoCheats -CheatSweep  only one cheat; -CheatBattle turns the old battle stats on
echo     run-game.bat -NoRealResourceCost -RealShopStock -FullBattleStats
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
