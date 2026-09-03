@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "PROJECT=%SCRIPT_DIR%src\PayneLab\PayneLab.csproj"
set "OUT_DIR=%SCRIPT_DIR%BuildOutput"
set "MOD_NAME=PayneLab.dll"

echo Waiting 2s for system to stabilize...
ping -n 3 127.0.0.1 >nul

echo Cleaning temp Roslyn/MSBuild junk to free disk...
del /f /q "%TEMP%\xml_file*.xml" 2>nul
del /f /q "%TEMP%\*.cpuprofile" 2>nul
del /f /q "%TEMP%\*.tmp.node" 2>nul

REM Disable telemetry to prevent background AppInsights OOMs
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set POWERSHELL_TELEMETRY_OPTOUT=1

REM Avoid multilevel lookup to reduce file system scan overhead
set DOTNET_MULTILEVEL_LOOKUP=0

REM Keep GC single-threaded (Workstation GC) and conservative
set DOTNET_GCServer=0
set DOTNET_GCConserve=1

set NUGET_XMLDOC_MODE=skip
set DOTNET_CLI_UI_LANGUAGE=en
set DOTNET_NOLOGO=1
set MSBUILDDISABLENODEREUSE=1
set MSBUILDNUMPROCS=1

echo Building PayneLab (1 core, low-mem)...
dotnet build "%PROJECT%" ^
    -p:ParallelCompilation=false ^
    -p:BuildInParallel=false ^
    -p:UseSharedCompilation=false ^
    -p:ServerGarbageCollection=false ^
    -p:ConcurrentBuild=false ^
    -p:RunAnalyzers=false ^
    -m:1 ^
    --nologo ^
    -c Release

if %ERRORLEVEL%==0 (
    echo.
    echo BUILD SUCCEEDED!
    echo Output in: %OUT_DIR%\%MOD_NAME%
    echo.
    echo Copy it to your Bonelab Mods folder:
    echo   BONELAB\UserData\Mods\ or Mods\PayneLab.dll
) else (
    echo.
    echo BUILD FAILED - check errors above
)
pause