@echo off
setlocal
cd /d %~dp0

if not defined GAME set "GAME=D:\SteamLibrary\steamapps\common\Car Mechanic Simulator 2021"
set "ML=%GAME%\MelonLoader"
set "CSC=.tools\roslyn\tasks\net472\csc.exe"

if not exist "%ML%\MelonLoader.dll" (
    echo [build] ERROR: game not found at "%GAME%"
    echo [build] set GAME=^<path to Car Mechanic Simulator 2021^> and retry
    exit /b 1
)

if not exist "%CSC%" (
    echo [build] downloading Roslyn compiler...
    if not exist .tools mkdir .tools
    powershell -NoProfile -Command "Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.12.0/microsoft.net.compilers.toolset.4.12.0.nupkg' -OutFile '.tools\roslyn.nupkg'" || exit /b 1
    copy /y .tools\roslyn.nupkg .tools\roslyn.zip >nul
    powershell -NoProfile -Command "Expand-Archive -Force '.tools\roslyn.zip' '.tools\roslyn'" || exit /b 1
)

"%CSC%" -nologo -target:library -platform:anycpu -out:BoltEvents.dll ^
  -r:"%ML%\Managed\Il2Cppmscorlib.dll" ^
  -r:"%ML%\MelonLoader.dll" ^
  -r:"%ML%\0Harmony.dll" ^
  -r:"%ML%\Managed\UnityEngine.CoreModule.dll" ^
  -r:"%ML%\Managed\UnityEngine.InputLegacyModule.dll" ^
  -r:"%ML%\Managed\UnityEngine.PhysicsModule.dll" ^
  -r:"%ML%\Managed\Assembly-CSharp-firstpass.dll" ^
  -r:"%ML%\Managed\UnhollowerBaseLib.dll" ^
  -r:"%ML%\Managed\Il2CppSystem.dll" ^
  BoltEvents.cs || exit /b 1

echo [build] OK: BoltEvents.dll
echo [build] install: copy BoltEvents.dll into "%GAME%\Mods"
