@echo off
chcp 65001 >nul 2>nul
echo ########################################
echo # Make it ZBS - Test Runner            #
echo ########################################
echo.

set TESTS_DIR=%~dp0unit
set FAIL_COUNT=0
set PASS_COUNT=0
set TOTAL_COUNT=0

echo Running tests from: %TESTS_DIR%
echo.

:: Test 1: context-detector
set /a TOTAL_COUNT+=1
echo ========================================
echo Running: test-context-detector.os
echo ========================================
oscript "%TESTS_DIR%\test-context-detector.os"
if %ERRORLEVEL% EQU 0 (
    set /a PASS_COUNT+=1
    echo [PASS] test-context-detector.os
) else (
    set /a FAIL_COUNT+=1
    echo [FAIL] test-context-detector.os
)
echo.

:: Test 2: openrouter-parser
set /a TOTAL_COUNT+=1
echo ========================================
echo Running: test-openrouter-parser.os
echo ========================================
oscript "%TESTS_DIR%\test-openrouter-parser.os"
if %ERRORLEVEL% EQU 0 (
    set /a PASS_COUNT+=1
    echo [PASS] test-openrouter-parser.os
) else (
    set /a FAIL_COUNT+=1
    echo [FAIL] test-openrouter-parser.os
)
echo.

:: Test 3: logger
set /a TOTAL_COUNT+=1
echo ========================================
echo Running: test-logger.os
echo ========================================
oscript "%TESTS_DIR%\test-logger.os"
if %ERRORLEVEL% EQU 0 (
    set /a PASS_COUNT+=1
    echo [PASS] test-logger.os
) else (
    set /a FAIL_COUNT+=1
    echo [FAIL] test-logger.os
)
echo.

:: Test 4: pipeline-smoke (из подпапки smoke — раньше bat его пропускал,
:: а именно там живут интеграционные тесты стрима/reload/privacy)
set /a TOTAL_COUNT+=1
echo ========================================
echo Running: smoke\test-pipeline-smoke.os
echo ========================================
oscript "%~dp0smoke\test-pipeline-smoke.os"
if %ERRORLEVEL% EQU 0 (
    set /a PASS_COUNT+=1
    echo [PASS] test-pipeline-smoke.os
) else (
    set /a FAIL_COUNT+=1
    echo [FAIL] test-pipeline-smoke.os
)
echo.

:: Summary
echo ########################################
echo # RESULTS                              #
echo ########################################
echo.
echo Total:  %TOTAL_COUNT%
echo Passed: %PASS_COUNT%
echo Failed: %FAIL_COUNT%
echo.

if %FAIL_COUNT% GTR 0 (
    echo RESULT: FAIL
    echo ########################################
    exit /b 1
) else (
    echo RESULT: ALL TESTS PASSED
    echo ########################################
    exit /b 0
)
