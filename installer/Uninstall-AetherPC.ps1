# Emergency / folder uninstall for the Normal (non-Setup) distribution.
# Official installed copies should use Windows → Apps → Uninstall.
#Requires -RunAsAdministrator
param(
    [switch]$WipeUserData
)

$ErrorActionPreference = 'Continue'
Write-Host "=== Uninstall AetherPC ==="

function Stop-AetherPC {
    Get-Process -Name 'AetherPC' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    & taskkill.exe /F /IM AetherPC.exe /T 2>$null | Out-Null
    Start-Sleep -Seconds 1
}

function Remove-AetherPCDriver {
    foreach ($name in @('R0AetherPC', 'AetherPC')) {
        & sc.exe stop $name 2>$null | Out-Null
        & sc.exe delete $name 2>$null | Out-Null
    }
}

function Remove-IfAetherPCDir([string]$Path) {
    if (-not $Path) { return }
    $leaf = Split-Path -Leaf $Path
    if (-not $leaf.Equals('AetherPC', [StringComparison]::OrdinalIgnoreCase)) { return }
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Write-Host "Eliminando datos: $Path"
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    } catch {
        cmd /c "rmdir /s /q `"$Path`"" 2>$null | Out-Null
    }
}

Stop-AetherPC
Remove-AetherPCDriver

@(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'AetherPC.lnk'),
    (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'AetherPC.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AetherPC.lnk'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\AetherPC.lnk')
) | ForEach-Object {
    if (Test-Path -LiteralPath $_) {
        Write-Host "Eliminando acceso: $_"
        Remove-Item -LiteralPath $_ -Force -ErrorAction SilentlyContinue
    }
}

$startFolders = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AetherPC'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\AetherPC')
)
foreach ($folder in $startFolders) {
    if (Test-Path -LiteralPath $folder) {
        Write-Host "Eliminando carpeta de inicio: $folder"
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$uninKeys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3A2C1B-9D4E-4A71-B2C8-1E6F0A9D7B33}_is1',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{8F3A2C1B-9D4E-4A71-B2C8-1E6F0A9D7B33}_is1',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3A2C1B-9D4E-4A71-B2C8-1E6F0A9D7B33}_is1'
)
foreach ($k in $uninKeys) {
    if (Test-Path $k) {
        Write-Host "Eliminando registro: $k"
        Remove-Item -LiteralPath $k -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($WipeUserData) {
    Remove-IfAetherPCDir (Join-Path $env:LOCALAPPDATA 'AetherPC')
} else {
    Write-Host "Datos de usuario conservados en %LOCALAPPDATA%\AetherPC"
}

$appDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($appDir)) {
    $appDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ($appDir -and (Test-Path (Join-Path $appDir 'AetherPC.exe'))) {
    Get-ChildItem -LiteralPath $appDir -Filter '*.sys' -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Write-Host "Eliminando instalacion: $appDir"
    $cmd = @"
@echo off
timeout /t 2 /nobreak >nul
taskkill /F /IM AetherPC.exe /T >nul 2>&1
rmdir /s /q "$appDir"
"@
    $tmp = Join-Path $env:TEMP "AetherPC-wipe-$(Get-Random).cmd"
    Set-Content -LiteralPath $tmp -Value $cmd -Encoding ASCII
    Start-Process -FilePath $tmp -WindowStyle Hidden
    Write-Host "La carpeta de la aplicacion se eliminara en unos segundos."
} else {
    Write-Host "No se detecto AetherPC.exe junto al script."
}

Write-Host "=== Listo. ==="
