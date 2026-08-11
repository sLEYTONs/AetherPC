@echo off
:: Desinstalador completo AetherPC (version normal / carpeta)
:: Solicita Administrador y limpia AppData, drivers, accesos y la carpeta de la app.

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Solicitando permisos de Administrador...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

cd /d "%~dp0"
echo === Desinstalacion completa de AetherPC ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-AetherPC.ps1"
if errorlevel 1 (
  echo Hubo avisos durante la limpieza.
)
echo.
pause
