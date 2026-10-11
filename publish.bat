@echo off
setlocal
cd /d "%~dp0"
if exist publish rmdir /s /q publish
dotnet publish FB2AccentConverter.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true -p:DebugType=None -p:DebugSymbols=false -o "publish"
if errorlevel 1 exit /b %errorlevel%
rem publish must contain exactly one file: DSR.exe
for %%F in (publish\*) do if /I not "%%~nxF"=="DSR.exe" del /q "%%F"
echo.
echo Ready to copy: %cd%\publish\DSR.exe
echo Everything else (Python environment, scripts, caches, models) is created on first run
rem double %% prints as a single % in the message below
echo in %%LocalAppData%%\DSR and can be deleted safely; DSR rebuilds it automatically.
