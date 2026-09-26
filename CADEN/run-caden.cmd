@echo off
setlocal
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_CLI_HOME=%~dp0.tools\cli-home
if exist "%~dp0.tools\dotnet\dotnet.exe" (
  "%~dp0.tools\dotnet\dotnet.exe" run --project "%~dp0Desktop\Desktop.csproj" -- %*
) else (
  dotnet run --project "%~dp0Desktop\Desktop.csproj" -- %*
)
if errorlevel 1 pause
