@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"
rem Позволяет вызывающему скрипту отключить паузу при ошибке (полная сборка).
if /I "%~1"=="--no-pause" set "DSR_NO_PAUSE=1"
echo Building and publishing DSR from:
echo   %CD%
echo.

set "PROJECT=%~dp0FB2AccentConverter.csproj"
set "PUBLISH_DIR=%~dp0publish"
set "MAINWINDOW_SOURCE=%~dp0MainWindow.cs"
set "REVIEW_SOURCE=%~dp0MainWindow.Review.cs"

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
if not exist "%REVIEW_SOURCE%" (
    echo ERROR: review source is missing: "%REVIEW_SOURCE%"
    set "RESULT=6"
    goto :finish
)
findstr /C:"DeclensionCorrection" "%REVIEW_SOURCE%" >nul
if errorlevel 1 (
    echo ERROR: review source does not contain declension form editing.
    set "RESULT=6"
    goto :finish
)
findstr /C:"SetReviewPaths" "%MAINWINDOW_SOURCE%" >nul
if errorlevel 1 (
    echo ERROR: MainWindow source is missing the post-conversion review handoff.
    set "RESULT=7"
    goto :finish
)

echo DSR project:       %PROJECT%
echo Publish directory: %PUBLISH_DIR%
echo Yo dictionary directory: %~dp0dictionary
echo.

rem Preserve the reusable runtime data next to the exe (Cyrillic folder name "ресурсы":
rem local Python environment + models). A plain delete would force re-downloading gigabytes
rem after every rebuild. Move the old publish aside, then move back any top-level directory
rem that the fresh publish did not create.
set "OLD_PUBLISH=%~dp0publish.old"
if exist "%OLD_PUBLISH%" rmdir /s /q "%OLD_PUBLISH%"
if exist "%PUBLISH_DIR%" move "%PUBLISH_DIR%" "%OLD_PUBLISH%" >nul
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

dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:DebugType=None -p:DebugSymbols=false -o "%PUBLISH_DIR%"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" goto :finish
if not exist "%PUBLISH_DIR%\DSR.exe" (
    echo ERROR: publish completed but DSR.exe is missing.
    set "RESULT=5"
    goto :finish
)
rem Словари и движок add-stress теперь встроены в DSR.exe: рядом с exe их быть не должно.
rem Так один файл DSR.exe можно перенести на другую машину без папки dictionary.

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

rem Restore directories from the previous publish that are not part of the fresh build
rem (the runtime data folder with python and models). Names are taken from the file system,
rem so no encoding issues.
rem First drop the runtime folder if the fresh build or its smoke test created one: otherwise
rem it would look like "already present", the real folder would not be restored, and the
rem models would be deleted together with publish.old.
for /d %%D in ("%PUBLISH_DIR%\*") do (
    if exist "%%D\ruaccent_worker.py" rmdir /s /q "%%D"
)
if exist "%OLD_PUBLISH%" (
    if not exist "%PUBLISH_DIR%" mkdir "%PUBLISH_DIR%"
    if exist "%PUBLISH_DIR%\.restore-ok" del "%PUBLISH_DIR%\.restore-ok"
    for /d %%D in ("%OLD_PUBLISH%\*") do (
        if /I "%%~nxD"=="dictionary" (
            rem Словари теперь встроены в DSR.exe — внешняя папка не нужна.
        ) else if /I "%%~nxD"=="russian_text_stresser" (
            rem Движок add-stress теперь встроен в DSR.exe и распаковывается в ресурсы.
        ) else if not exist "%PUBLISH_DIR%\%%~nxD" (
            move "%%D" "%PUBLISH_DIR%\" >nul 2>nul
            if errorlevel 1 echo failed>>"%PUBLISH_DIR%\.restore-ok"
        )
    )
    set "RESTORE_INCOMPLETE=0"
    if exist "%PUBLISH_DIR%\.restore-ok" set "RESTORE_INCOMPLETE=1"
    if exist "%PUBLISH_DIR%\.restore-ok" del "%PUBLISH_DIR%\.restore-ok"
    if "%RESTORE_INCOMPLETE%"=="1" (
        echo WARNING: not everything from the previous build could be moved back.
        echo          The previous build is kept in "%OLD_PUBLISH%" - do not delete it
        echo          until ресурсы\python and ресурсы\модели are back in "%PUBLISH_DIR%".
        echo          Usually a leftover python.exe is holding files: close DSR and retry.
    ) else (
        rmdir /s /q "%OLD_PUBLISH%"
    )
)

rem Ensure the optional add-stress engine is present (used by the anchor worker).
rem It is embedded in DSR.exe (see FB2AccentConverter.csproj) and unpacked into
rem ресурсы\add-stress on first use, so the publish folder stays a single exe.

:finish
echo.
if not "%RESULT%"=="0" (
    echo Build or publish failed with exit code %RESULT%.
    if not defined DSR_NO_PAUSE pause
) else (
    echo Build and publish completed successfully.
    echo Published executable: %PUBLISH_DIR%\DSR.exe
)
exit /b %RESULT%
