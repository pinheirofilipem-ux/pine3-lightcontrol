@echo off
setlocal enabledelayedexpansion
title Pine3 Light Control - Build
cd /d "%~dp0"

echo.
echo  ============================================
echo   Pine3 Light Control - Build
echo   made by magnificent3
echo  ============================================
echo.

:: -- .NET 10 SDK ---------------------------------------------------------------
set "HAVE10="
for /f "tokens=1" %%v in ('dotnet --list-sdks 2^>nul') do (
    set "V=%%v"
    if "!V:~0,3!"=="10." set "HAVE10=1"
)
if not defined HAVE10 (
    echo  [..] .NET 10 SDK not found. Installing via winget...
    winget install --id Microsoft.DotNet.SDK.10 -e --accept-package-agreements --accept-source-agreements
    if errorlevel 1 (
        echo  [ERROR] Could not install the .NET 10 SDK. Get it from https://dotnet.microsoft.com/download
        pause & exit /b 1
    )
    echo  [OK] .NET 10 SDK installed. Close this window and run build.bat again.
    pause & exit /b 0
)
echo  [OK] .NET 10 SDK found

:: -- PawnIO (memory RGB) --------------------------------------------------------
echo.
echo  [..] PawnIO files for memory RGB...
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\get-pawnio.ps1"
if errorlevel 1 (
    echo  [WARN] Could not get the PawnIO files - memory RGB will be unavailable in this build.
)

:: -- Tests ---------------------------------------------------------------------
echo.
echo  [1/3] Running tests...
dotnet test "tests\Pine3LightControl.Tests" -c Release --nologo -v q
if errorlevel 1 (
    echo  [ERROR] Tests failed.
    pause & exit /b 1
)

:: -- Publish (self-contained, win-x64) -----------------------------------------
echo.
echo  [2/3] Building Pine3 Light Control...
if exist "dist\app" rmdir /s /q "dist\app"
dotnet publish "src\Pine3LightControl\Pine3LightControl.csproj" -c Release -o "dist\app" --nologo
if errorlevel 1 (
    echo.
    echo  [ERROR] Build failed. Scroll up for the first error.
    pause & exit /b 1
)
echo  [OK] dist\app\Pine3LightControl.exe

:: -- Installer (Inno Setup 6) --------------------------------------------------
echo.
echo  [3/3] Building installer...
set "ISCC="
for %%p in (
    "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
    "%ProgramFiles%\Inno Setup 6\ISCC.exe"
    "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
) do if exist %%p set "ISCC=%%~p"
if not defined ISCC (
    echo  [WARN] Inno Setup 6 not found - skipping installer.
    echo         Install it with: winget install JRSoftware.InnoSetup
    goto :done
)
"%ISCC%" /Q "installer.iss"
if errorlevel 1 (
    echo  [WARN] Inno Setup failed. The app is still in dist\app.
    goto :done
)
echo  [OK] dist\Pine3LightControl-Setup-*.exe

:done
echo.
echo  ============================================
echo   Build complete
echo  ============================================
echo.
pause
