@echo off
cd /d "%~dp0"
if exist "publish\win-x64\OmniSync.exe" (
    start "" "publish\win-x64\OmniSync.exe"
) else (
    echo Compilando e iniciando o OmniSync...
    dotnet run
)
