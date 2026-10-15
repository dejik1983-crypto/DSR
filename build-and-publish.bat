@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo Building and publishing DSR from:
echo   %CD%
echo.

set "PROJECT=%~dp0FB2AccentConverter.csproj"
set "PUBLISH_DIR=%~dp0publish"
set "MAINWINDOW_SOURCE=%~dp0MainWindow.cs"
set "REVIEW_SOURCE=%~dp0WordReviewWindow.cs"

if not exist "%~dp0dictionary\*.txt" (
    echo ERROR: dictionary text file is missing in "%~dp0dictionary".
    set "RESULT=10"
    goto :finish
)
if not exist "%PROJECT%" (
    echo ERROR: DSR project not found: "%PROJECT%"
    set "RESULT=2"
    goto :finish
)
set "DEEPREADER_PROJECT="
for /d %%D in ("%~dp0..\DeepReader_x64 V*Beta Bionic") do if exist "%%~fD\DeepReader.csproj" (
    if defined DEEPREADER_PROJECT (
        echo ERROR: more than one matching DeepReader project was found.
        set "RESULT=3"
        goto :finish
    )
    set "DEEPREADER_PROJECT=%%~fD\DeepReader.csproj"
)
if not defined DEEPREADER_PROJECT (
    echo ERROR: no neighboring DeepReader_x64 V*Beta Bionic project was found.
    echo Place exactly one DeepReader project next to DSR, or adjust this script.
    set "RESULT=3"
    goto :finish
)
if not exist "%DEEPREADER_PROJECT%" (
    echo ERROR: expected DeepReader project not found: "%DEEPREADER_PROJECT%"
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
echo Yo dictionary directory: %~dp0dictionary
echo.

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if errorlevel 1 (
    echo ERROR: could not clean old publish output.
    set "RESULT=4"
    goto :finish
)
rem Remove stale intermediate outputs so the publish always uses current sources.
if exist "%~dp0obj" rmdir /s /q "%~dp0obj"
if errorlevel 1 (
    echo ERROR: could not clean DSR obj directory.
    set "RESULT=8"
    goto :finish
)
if exist "%~dp0bin" rmdir /s /q "%~dp0bin"
if errorlevel 1 (
    echo ERROR: could not clean DSR bin directory.
    set "RESULT=9"
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
if not exist "%PUBLISH_DIR%\dictionary\*.txt" (
    echo ERROR: publish output is missing dictionary text files.
    set "RESULT=11"
    goto :finish
)

echo.
echo Published executable:
echo   %PUBLISH_DIR%\DSR.exe
for %%F in ("%PUBLISH_DIR%\DSR.exe") do echo   Size: %%~zF bytes
for %%F in ("%PUBLISH_DIR%\DSR.exe") do set "BUILT_AT=%%~tF"
echo   Built: %BUILT_AT%

echo.
echo Verifying the published executable...
"%PUBLISH_DIR%\DSR.exe" --help >nul
if errorlevel 1 (
    echo ERROR: DSR.exe smoke test failed.
    set "RESULT=12"
    goto :finish
)
echo Executable smoke test passed.

:finish
echo.
if not "%RESULT%"=="0" echo Build or publish failed with exit code %RESULT%.
if "%RESULT%"=="0" echo Build and publish completed successfully.
pause
exit /b %RESULT%
