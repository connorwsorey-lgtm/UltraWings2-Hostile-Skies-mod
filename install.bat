@echo off
title Hostile Skies - Ultrawings 2 Mod Installer
echo ============================================
echo  HOSTILE SKIES - Ultrawings 2 Mod Installer
echo ============================================
echo.

:: Try to find UW2 install path
set "UW2PATH="

:: Check Oculus default
if exist "C:\Program Files\Oculus\Software\Software\bit-planet-games-llc-ultrawings-2\Ultrawings 2.exe" (
    set "UW2PATH=C:\Program Files\Oculus\Software\Software\bit-planet-games-llc-ultrawings-2"
)

:: Check Steam default
if "%UW2PATH%"=="" (
    if exist "C:\Program Files (x86)\Steam\steamapps\common\Ultrawings 2\Ultrawings 2.exe" (
        set "UW2PATH=C:\Program Files (x86)\Steam\steamapps\common\Ultrawings 2"
    )
)

:: Check Steam secondary
if "%UW2PATH%"=="" (
    if exist "D:\SteamLibrary\steamapps\common\Ultrawings 2\Ultrawings 2.exe" (
        set "UW2PATH=D:\SteamLibrary\steamapps\common\Ultrawings 2"
    )
)

if "%UW2PATH%"=="" (
    echo [!] Could not auto-detect Ultrawings 2 install location.
    echo.
    set /p "UW2PATH=Enter the full path to your Ultrawings 2 folder: "
)

if not exist "%UW2PATH%\Ultrawings 2.exe" (
    echo [ERROR] Ultrawings 2.exe not found at: %UW2PATH%
    echo Please check the path and try again.
    pause
    exit /b 1
)

echo [OK] Found Ultrawings 2 at: %UW2PATH%
echo.

:: Check for MelonLoader
if exist "%UW2PATH%\MelonLoader\MelonLoader.dll" (
    echo [OK] MelonLoader is installed.
) else (
    echo [!] MelonLoader is NOT installed.
    echo.
    echo MelonLoader is required for this mod to work.
    echo.
    echo Please install MelonLoader first:
    echo   1. Download from: https://github.com/LavaGang/MelonLoader/releases
    echo   2. Run MelonLoader.Installer.exe
    echo   3. Point it to: %UW2PATH%\Ultrawings 2.exe
    echo   4. Click Install
    echo   5. Run this installer again
    echo.
    echo Opening MelonLoader download page...
    start https://github.com/LavaGang/MelonLoader/releases/latest
    pause
    exit /b 1
)

:: Create Mods folder if needed
if not exist "%UW2PATH%\Mods" (
    mkdir "%UW2PATH%\Mods"
    echo [OK] Created Mods folder.
)

:: Copy mod DLL
echo.
echo Installing Hostile Skies mod...

:: Check if DLL is in same folder as installer
if exist "%~dp0UW2TrainerMod.dll" (
    copy /y "%~dp0UW2TrainerMod.dll" "%UW2PATH%\Mods\UW2TrainerMod.dll" >nul
    echo [OK] Copied UW2TrainerMod.dll to Mods folder.
) else if exist "%~dp0bin\Debug\UW2TrainerMod.dll" (
    copy /y "%~dp0bin\Debug\UW2TrainerMod.dll" "%UW2PATH%\Mods\UW2TrainerMod.dll" >nul
    echo [OK] Copied UW2TrainerMod.dll to Mods folder.
) else (
    echo [ERROR] UW2TrainerMod.dll not found!
    echo Place it next to this installer or build from source first.
    pause
    exit /b 1
)

:: Create music folder
if not exist "%UW2PATH%\UserData\HostileSkies\Music" (
    mkdir "%UW2PATH%\UserData\HostileSkies\Music"
    echo [OK] Created UserData\HostileSkies\Music folder.
)

echo.
echo ============================================
echo  INSTALLATION COMPLETE!
echo ============================================
echo.
echo Hostile Skies has been installed to:
echo   %UW2PATH%\Mods\UW2TrainerMod.dll
echo.
echo HOW TO USE:
echo   1. Launch Ultrawings 2
echo   2. Press Spacebar or F1 to open the mod menu
echo   3. In VR: A=Select, B=Close, LStick=Navigate, RStick=Tabs
echo   4. Play one combat mission to unlock enemy spawning
echo   5. In free flight, go to Combat tab and spawn enemies!
echo.
echo Optional: Drop .ogg music files into:
echo   %UW2PATH%\UserData\HostileSkies\Music\
echo.
pause
