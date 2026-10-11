@echo off
setlocal
cd /d "%~dp0"
dotnet publish FB2AccentConverter.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true -p:DebugType=None -p:DebugSymbols=false -o "publish"
if errorlevel 1 exit /b %errorlevel%
if exist publish\DeepReader.pdb del /q publish\DeepReader.pdb
if exist publish\model-cache rmdir /s /q publish\model-cache
copy /Y requirements.txt publish\requirements.txt >nul
copy /Y README.md publish\README.txt >nul
copy /Y setup-python.bat publish\setup-python.bat >nul
copy /Y ruaccent_worker.py publish\ruaccent_worker.py >nul
echo.
echo Ready-to-copy application: %cd%\publish
echo Copy that folder to the target PC, run setup-python.bat once, then run FB2AccentConverter.exe.
