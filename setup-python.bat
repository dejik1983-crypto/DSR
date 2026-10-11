@echo off
setlocal
cd /d "%~dp0"
where python >nul 2>nul
if errorlevel 1 (
  echo Python 3.10 or newer was not found. Install 64-bit Python and enable Add Python to PATH.
  exit /b 1
)
python -c "import sys; raise SystemExit(0 if sys.version_info >= (3,10) else 1)"
if errorlevel 1 (
  echo Python 3.10 or newer is required.
  exit /b 1
)
python -m pip install --upgrade pip
if errorlevel 1 exit /b %errorlevel%
python -m pip install -r requirements.txt
if errorlevel 1 exit /b %errorlevel%
echo.
echo Python engine installed. Run FB2AccentConverter.exe.
pause
