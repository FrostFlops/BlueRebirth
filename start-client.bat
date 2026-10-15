@echo off
setlocal

echo ============================================================
echo   Blue Oath JP offline - client launcher
echo   Starts the TLS proxy + game against the server running
echo   under Rider's debugger (HTTP port 7080).
echo.
echo   Make sure the server is running in Rider first with:
echo     --port=7080 --game-login-port=7201 --region=jp --client-path=blueoath\blueoath
echo   That server defaults to every cheat except --cheat-battle ON and --real-resource-cost ON.
echo     --no-cheats   or one by one: --cheat-production=off --cheat-strength=off --cheat-vow=off
echo     --cheat-mood=off --cheat-medals=off --cheat-drops=off --cheat-sweep=off, --cheat-battle=on
echo     --real-resource-cost=off --real-shop-stock --exp-full-battle-stats
echo   Back up profiles.db in that server's --data directory first.
echo.
echo   Press Ctrl+C to stop (auto cleanup).
echo   Log: native\bin-x86\BlueOath.Payload.log
echo ============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\start-client.ps1" -SkipBuild

echo.
echo [start-client] stopped.
pause
