@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "PROJECT=%~dp0FB2AccentConverter.csproj"
set "PUBLISH_DIR=%~dp0publish"
if not exist "%PROJECT%" (
    echo ERROR: DSR project not found: "%PROJECT%"
    exit /b 2
)
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if errorlevel 1 exit /b 4
rem Prevent the SDK from compiling stale generated files left by interrupted runs.
if exist "%~dp0obj" rmdir /s /q "%~dp0obj"
if errorlevel 1 exit /b 6
if exist "%~dp0bin" rmdir /s /q "%~dp0bin"
if errorlevel 1 exit /b 7
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:DebugType=None -p:DebugSymbols=false -o "%PUBLISH_DIR%"
if errorlevel 1 exit /b %errorlevel%
if not exist "%PUBLISH_DIR%\DSR.exe" (
    echo ERROR: DSR.exe missing after publish.
    exit /b 5
)
rem Keep publish single-file: only the executable is distributed.
for %%F in ("%PUBLISH_DIR%\*") do if /I not "%%~nxF"=="DSR.exe" del /q "%%F"
echo.
echo Ready to copy: %PUBLISH_DIR%\DSR.exe
for %%F in ("%PUBLISH_DIR%\DSR.exe") do echo Size: %%~zF bytes
echo Everything else (Python environment, models, dictionaries, caches) is created on first run in the "ресурсы" folder next to DSR.exe.
