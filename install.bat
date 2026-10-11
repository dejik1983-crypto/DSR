@echo off
setlocal
cd /d "%~dp0"
rem Builds the same single-file application as publish.bat.
rem Python dependencies are installed automatically on first run of DSR.exe,
rem not here: everything goes to %%LocalAppData%%\DSR on the target machine.
call "%~dp0publish.bat"
exit /b %errorlevel%
