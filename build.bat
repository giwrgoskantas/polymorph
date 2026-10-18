@echo off
rem Polymorph build (needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0)
setlocal
cd /d "%~dp0"

rem Close a running Polymorph first (it asks to save if the scene changed), otherwise its files are locked.
set /a TRIES=0
:waitclose
tasklist /fi "imagename eq Polymorph.exe" 2>nul | find /i "Polymorph.exe" >nul
if not errorlevel 1 (
  if %TRIES%==0 (
    echo Closing Polymorph - save your scene if it asks...
    taskkill /im Polymorph.exe >nul 2>&1
  )
  set /a TRIES+=1
  if %TRIES% GEQ 60 (
    echo Polymorph is still open. Close it, then run build.bat again.
    pause
    exit /b 1
  )
  timeout /t 1 /nobreak >nul
  goto waitclose
)

echo == Tests of the geometry core
dotnet run --project MoloMax.Core.Tests -c Release || goto :fail
echo == Building Polymorph
dotnet publish MoloMax\MoloMax.csproj -c Release -r win-x64 --self-contained false -o out || goto :fail
echo.
echo Done. Starting Polymorph.
start "" "%~dp0out\Polymorph.exe"
exit /b 0
:fail
echo.
echo Build failed, see the messages above.
pause
exit /b 1
