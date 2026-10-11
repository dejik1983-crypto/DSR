@echo off
setlocal
cd /d "%~dp0"
echo Building and publishing DSR...
call publish.bat
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" (
    echo Build or publish failed with exit code %RESULT%.
) else (
    echo Build and publish completed successfully.
    echo Output: %~dp0publish\DSR.exe
)
echo.
pause
exit /b %RESULT%
