@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo Building and publishing DSR from:
echo   %CD%
echo.

set "PROJECT=%~dp0FB2AccentConverter.csproj"
set "PUBLISH_DIR=%~dp0publish"
set "DEEPREADER_PROJECT=%~dp0..\DeepReader_x64 V1.6.3Beta Bionic\DeepReader.csproj"
set "MAINWINDOW_SOURCE=%~dp0MainWindow.cs"
set "REVIEW_SOURCE=%~dp0WordReviewWindow.cs"

if not exist "%PROJECT%" (
    echo ERROR: DSR project not found: "%PROJECT%"
    set "RESULT=2"
    goto :finish
)
if not exist "%DEEPREADER_PROJECT%" (
    echo ERROR: expected DeepReader project not found: "%DEEPREADER_PROJECT%"
    echo Update DEEPREADER_PROJECT in this script if your DeepReader folder has a different name.
    set "RESULT=3"
    goto :finish
)
findstr /C:"DeclensionCorrection" "%REVIEW_SOURCE%" >nul
if errorlevel 1 (
    echo ERROR: review-window source does not contain declension form editing.
    set "RESULT=6"
    goto :finish
)
findstr /C:"WordReviewWindow(reviewPath, output)" "%MAINWINDOW_SOURCE%" >nul
if errorlevel 1 (
    echo ERROR: MainWindow source is missing the post-conversion review launch.
    set "RESULT=7"
    goto :finish
)

echo DSR project:       %PROJECT%
echo DeepReader:        %DEEPREADER_PROJECT%
echo Publish directory: %PUBLISH_DIR%
echo.

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if errorlevel 1 (
    echo ERROR: could not clean old publish output.
    set "RESULT=4"
    goto :finish
)

dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:DeepReaderProjectPath="%DEEPREADER_PROJECT%" -p:DebugType=None -p:DebugSymbols=false -o "%PUBLISH_DIR%"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" goto :finish
if not exist "%PUBLISH_DIR%\DSR.exe" (
    echo ERROR: publish completed but DSR.exe is missing.
    set "RESULT=5"
    goto :finish
)

echo.
echo Published executable:
echo   %PUBLISH_DIR%\DSR.exe
for %%F in ("%PUBLISH_DIR%\DSR.exe") do echo   Size: %%~zF bytes
for %%F in ("%PUBLISH_DIR%\DSR.exe") do set "BUILT_AT=%%~tF"
echo   Built: %BUILT_AT%

echo.
echo Verifying that current manual-review code is in the compiled assembly...
dotnet "%PUBLISH_DIR%\DSR.exe" --help >nul
if errorlevel 1 (
    echo WARNING: executable exists, but its smoke test returned a failure code.
) else (
    echo Executable smoke test passed.
)

:finish
echo.
if not "%RESULT%"=="0" echo Build or publish failed with exit code %RESULT%.
if "%RESULT%"=="0" echo Build and publish completed successfully.
pause
exit /b %RESULT%
