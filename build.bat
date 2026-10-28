@echo off
cd /d "%~dp0"
dotnet publish -c Release -r win-x64 --self-contained false -o publish
if errorlevel 1 (echo BUILD FAILED) else (echo OK: publish\CornerPlayer.exe)
pause
