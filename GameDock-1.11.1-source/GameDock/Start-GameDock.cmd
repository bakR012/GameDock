@echo off
setlocal
if not exist "%~dp0GameDock.exe" call "%~dp0Build-Release.cmd"
if errorlevel 1 exit /b 1
start "" "%~dp0GameDock.exe"
