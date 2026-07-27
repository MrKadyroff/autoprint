@echo off
setlocal

rem Builds AutoPrint and produces a ready-to-copy output directory in .\dist
rem Usage: build.bat [Debug|Release]

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release

set SCRIPT_DIR=%~dp0
set PROJECT=%SCRIPT_DIR%AutoPrint.csproj
set OUT_DIR=%SCRIPT_DIR%dist

echo === Building AutoPrint (%CONFIG%) ===

if exist "%OUT_DIR%" rd /s /q "%OUT_DIR%"

dotnet publish "%PROJECT%" ^
    -c %CONFIG% ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUT_DIR%"

if errorlevel 1 (
    echo.
    echo === BUILD FAILED ===
    exit /b 1
)

echo.
echo === Done: %OUT_DIR% ===
endlocal
