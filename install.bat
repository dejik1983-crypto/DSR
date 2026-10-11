@echo off
setlocal
cd /d "%~dp0"
where python >nul 2>nul
if errorlevel 1 (
  echo Python 3.10 or newer was not found. Install Python and enable Add Python to PATH.
  exit /b 1
)
python -m pip install --upgrade pip
if errorlevel 1 exit /b %errorlevel%
python -m pip install -r requirements.txt
if errorlevel 1 exit /b %errorlevel%
dotnet publish FB2AccentConverter.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true -p:DebugType=None -p:DebugSymbols=false -o "publish"
if errorlevel 1 exit /b %errorlevel%
if exist publish\DeepReader.pdb del /q publish\DeepReader.pdb
if exist publish\model-cache rmdir /s /q publish\model-cache
copy /Y requirements.txt publish\requirements.txt >nul
copy /Y README.md publish\README.txt >nul
copy /Y setup-python.bat publish\setup-python.bat >nul
copy /Y ruaccent_worker.py publish\ruaccent_worker.py >nul
echo.
echo Publish complete. Copy the publish folder to the target PC and run FB2AccentConverter.exe.
echo Python 3.10+ must be installed. Run setup-python.bat in publish once to install the Python engine.
exit /b 0
