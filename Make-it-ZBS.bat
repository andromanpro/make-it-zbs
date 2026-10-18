@echo off
REM Make it ZBS - launcher. ASCII only: Cyrillic echo in a .bat
REM mojibakes under cp866 console. Real (Russian) UI is printed by
REM oscript itself with correct encoding.
chcp 65001 >nul

cd /d "%~dp0"

echo ========================================
echo  Make it ZBS - AI Assistant
echo ========================================
echo.
echo  Starting...
echo.
echo  After it starts:
echo   1. Select text in any application
echo   2. Press Ctrl+F12
echo   3. Get the improved result
echo.
echo  To quit: right-click the tray icon -> Exit
echo  (Ctrl+C / closing this window leaves a ghost tray icon)
echo ========================================
echo.

REM Run OneScript
py -3.14 tools\build-dist.py --run

pause
