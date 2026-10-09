@echo off
setlocal

echo ============================================================
echo   Blue Oath JP offline - client launcher
echo   Starts the TLS proxy + game against the server running
echo   under Rider's debugger (HTTP port 7080).
echo.
echo   Make sure the server is running in Rider first with:
echo     --port=7080 --game-login-port=7201 --region=jp --client-path=blueoath\blueoath
echo   Cheats are ON by default on that server; turn them off with
echo     --no-cheats   or one by one: --cheat-production=off --cheat-strength=off --cheat-vow=off
echo     --cheat-mood=off --cheat-medals=off --cheat-drops=off --cheat-sweep=off --cheat-battle=off
echo   Optional original-rule switches for that server, off by default:
echo     --real-resource-cost --real-shop-stock
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
