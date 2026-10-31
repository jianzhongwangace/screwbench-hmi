@echo off
setlocal
set "DOTNET_ROOT=%LOCALAPPDATA%\ScrewBench\dotnet"
set "APP=%~dp0src\Desktop\bin\Release\net8.0-windows\ScrewBench.dll"
if exist "%~dp0artifacts/position-input-20260928/ScrewBench.dll" set "APP=%~dp0artifacts/position-input-20260928/ScrewBench.dll"
if exist "%~dp0artifacts/json-demo-20260928/ScrewBench.dll" set "APP=%~dp0artifacts/json-demo-20260928/ScrewBench.dll"
if not exist "%APP%" (
  echo Application not built. See README.md.
  pause
  exit /b 1
)
if exist "%DOTNET_ROOT%\dotnet.exe" (
  start "" "%DOTNET_ROOT%\dotnet.exe" "%APP%"
) else (
  start "" dotnet "%APP%"
)
