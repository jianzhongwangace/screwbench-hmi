@echo off
setlocal
set "DOTNET_ROOT=%LOCALAPPDATA%/ScrewBench/dotnet"
set "APP=%~dp0artifacts/json-demo-20260928/ScrewBench.dll"
if not exist "%APP%" (
  echo Demo build not found.
  pause
  exit /b 1
)
start "" "%DOTNET_ROOT%/dotnet.exe" "%APP%" --demo
