@echo off
setlocal
title Uninstall AetherPC
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Se necesitan permisos de Administrador.
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

cd /d "%~dp0"
echo.
echo  Uninstall AetherPC
echo  -------------------
echo  Esto elimina la carpeta de la aplicacion y los accesos directos.
echo  Las optimizaciones aplicadas a Windows NO se revierten.
echo.
choice /C KN /N /M "Datos de usuario: [K] Conservar   [N] Eliminar todos  "
set "WIPE=0"
if errorlevel 2 set "WIPE=1"

if "%WIPE%"=="1" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-AetherPC.ps1" -WipeUserData
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-AetherPC.ps1"
)
echo.
pause
