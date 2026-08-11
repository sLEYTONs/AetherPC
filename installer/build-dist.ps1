# Release pipeline: Normal publish → Setup from Normal. Portable is a separate artifact.
# Usage: powershell -ExecutionPolicy Bypass -File installer\build-dist.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'src\AetherPC.App\AetherPC.App.csproj'))) {
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$proj = Join-Path $root 'src\AetherPC.App\AetherPC.App.csproj'
$dist = Join-Path $root 'dist'
$normal = Join-Path $dist 'Normal'
$payload = Join-Path $dist 'Payload'
$portable = Join-Path $dist 'Portable'
$installerOut = Join-Path $dist 'Installer'
$iss = Join-Path $root 'installer\AetherPC.iss'
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) {
    $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
}
if (-not (Test-Path $iscc)) {
    throw "Inno Setup 6 no encontrado (ISCC.exe)."
}

$csproj = Get-Content $proj -Raw -Encoding UTF8
if ($csproj -notmatch '<Version>([^<]+)</Version>') {
    throw "No se pudo leer <Version> de AetherPC.App.csproj"
}
$version = $Matches[1].Trim()
Write-Host "Version: $version"

Get-Process AetherPC, AetherPC_Portable -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4

function Publish-AetherPC([string]$Output, [switch]$SingleFile) {
    if (Test-Path $Output) {
        Remove-Item $Output -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Output | Out-Null
    $dotnetArgs = @(
        'publish', $proj,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishReadyToRun=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $Output,
        '--nologo'
    )
    if ($SingleFile) {
        $dotnetArgs += @(
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true'
        )
    }
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($Output)" }
}

function Clear-DistJunk([string]$Dir) {
    Get-ChildItem $Dir -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -match '\.(pdb|sys)$' -or
            $_.Name -eq 'createdump.exe' -or
            $_.Name -like 'AetherPC_Bench.tmp' -or
            $_.Name -like '*.log'
        } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Ensure-CultureFolders([string]$AppDir) {
    $es = Join-Path $AppDir 'es'
    $en = Join-Path $AppDir 'en'
    if (-not (Test-Path $es)) {
        $packRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsdesktop.app.runtime.win-x64'
        $packEs = Get-ChildItem $packRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'runtimes\win-x64\lib\net8.0\es' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($packEs) {
            Copy-Item $packEs $es -Recurse -Force
        }
    }
    if (-not (Test-Path $es)) {
        throw "Normal publish incompleto: falta carpeta de cultura es\"
    }
    # El pack de WindowsDesktop no trae satelites en\ (ingles = invariante en las DLL principales).
    # Se copia la estructura al lado de es\ para el layout profesional; la app en ingles usa InvariantCulture.
    if (-not (Test-Path $en)) {
        Copy-Item $es $en -Recurse -Force
    }
}

function Publish-BootStub([string]$DestExe) {
    $src = Join-Path $root 'installer\boot\Program.cs'
    $icon = Join-Path $root 'src\AetherPC.App\Assets\Brand\AetherPC.ico'
    $manifest = Join-Path $root 'src\AetherPC.App\app.manifest'
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $destDir = Split-Path -Parent $DestExe
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }

    if (Test-Path $csc) {
        $cscArgs = @(
            '/nologo',
            '/target:winexe',
            '/platform:x64',
            '/optimize+',
            "/out:$DestExe",
            "/win32icon:$icon",
            "/win32manifest:$manifest",
            $src
        )
        & $csc @cscArgs
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $DestExe)) {
            throw "csc.exe no pudo generar el launcher AetherPC.exe"
        }
        return
    }

    $bootProj = Join-Path $root 'installer\boot\AetherPC.Boot.csproj'
    $bootOut = Join-Path $dist '_boot-build'
    if (Test-Path $bootOut) { Remove-Item $bootOut -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $bootOut | Out-Null
    $dotnetArgs = @(
        'publish', $bootProj,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'false',
        '-p:PublishSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $bootOut,
        '--nologo'
    )
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish boot launcher failed" }
    $published = Join-Path $bootOut 'AetherPC.exe'
    if (-not (Test-Path $published)) { throw "No se genero el launcher AetherPC.exe" }
    Copy-Item $published $DestExe -Force
    Remove-Item $bootOut -Recurse -Force -ErrorAction SilentlyContinue
}

function Remove-DirRetry([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    Get-Process AetherPC, AetherPC_Portable -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $n = 0
    while ($n -lt 6) {
        try {
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            return
        } catch {
            $n++
            Start-Sleep -Seconds 2
        }
    }
    throw "No se pudo borrar $Path (archivo en uso). Cierra AetherPC e intenta de nuevo."
}

function Restore-DepsRuntimeFolders([string]$AppDir) {
    # El publish aplana nativos que en deps.json siguen como runtimes/win-x64/native/*.
    # Restaurar esas rutas da subcarpetas reales SIN reescribir deps ni romper carga.
    $depsPath = Join-Path $AppDir 'AetherPC.deps.json'
    if (-not (Test-Path $depsPath)) { return }

    $raw = [System.IO.File]::ReadAllText($depsPath)
    $matches = [regex]::Matches($raw, '"runtimes/([^"]+/native)/([^"/]+)"\s*:')
    $moved = 0
    foreach ($m in $matches) {
        $relDir = ($m.Groups[1].Value -replace '/', '\')
        $fileName = $m.Groups[2].Value
        $src = Join-Path $AppDir $fileName
        $destDir = Join-Path $AppDir ('runtimes\' + $relDir)
        $dest = Join-Path $destDir $fileName
        if ((Test-Path $src) -and -not (Test-Path $dest)) {
            New-Item -ItemType Directory -Force -Path $destDir | Out-Null
            Move-Item $src $dest -Force
            $moved++
        }
    }
    if ($moved -gt 0) {
        Write-Host ("app\runtimes: restored $moved native files from deps.json paths")
    }
}

function Organize-NormalLayout([string]$Staging, [string]$Dest) {
    # Layout profesional viable con .NET 8 self-contained + WPF:
    # Raiz: AetherPC.exe (launcher). Inno agrega Uninstall AetherPC.exe.
    # app\: host + Framework/WPF DLL (DEBEN quedar junto al EXE interno; mover a lib\ rompe Assembly.Load).
    # app\es, app\en: satélites WPF (ResourceManager exige {base}\{cultura}\).
    # app\runtimes\win-x64\native\: nativos que deps.json ya declara ahí (sqlite, skia, etc.).
    # No languages\, no assets\ sueltos, no lib\ de managed.
    Remove-DirRetry $Dest
    $appDir = Join-Path $Dest 'app'
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    Get-ChildItem $Staging -Force | ForEach-Object {
        Move-Item $_.FullName (Join-Path $appDir $_.Name) -Force
    }
    # Quitar restos de experimentos lib\ si existieran en staging
    $badLib = Join-Path $appDir 'lib'
    if (Test-Path $badLib) { Remove-Item $badLib -Recurse -Force -ErrorAction SilentlyContinue }

    Ensure-CultureFolders $appDir
    Restore-DepsRuntimeFolders $appDir
    Publish-BootStub (Join-Path $Dest 'AetherPC.exe')
    Remove-Item $Staging -Recurse -Force -ErrorAction SilentlyContinue
}

function Assert-NormalPublish([string]$Dir) {
    $appDir = Join-Path $Dir 'app'
    $stub = Join-Path $Dir 'AetherPC.exe'
    if (-not (Test-Path $stub)) { throw "Normal incompleto: falta launcher AetherPC.exe en la raiz" }
    if (-not (Test-Path $appDir)) { throw "Normal incompleto: falta carpeta app\" }

    $rootDlls = @(Get-ChildItem $Dir -Filter '*.dll' -File -ErrorAction SilentlyContinue)
    if ($rootDlls.Count -gt 0) {
        throw "La raiz de Normal no debe tener DLL ($($rootDlls.Count)). Deben estar en app\."
    }

    $required = @(
        'AetherPC.exe',
        'AetherPC.dll',
        'AetherPC.runtimeconfig.json',
        'AetherPC.deps.json',
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        'Wpf.Ui.dll',
        'LibreHardwareMonitorLib.dll',
        'CommunityToolkit.Mvvm.dll'
    )
    foreach ($name in $required) {
        $p = Join-Path $appDir $name
        if (-not (Test-Path $p)) { throw "Normal publish incompleto: falta app\$name" }
    }
    $sqlite = @(
        (Join-Path $appDir 'e_sqlite3.dll'),
        (Join-Path $appDir 'runtimes\win-x64\native\e_sqlite3.dll')
    ) | Where-Object { Test-Path $_ }
    if (-not $sqlite) { throw "Normal publish incompleto: falta e_sqlite3.dll" }
    if (-not (Test-Path (Join-Path $appDir 'runtimes'))) {
        throw "Normal publish incompleto: falta app\runtimes\ (nativos de deps.json)"
    }
    if (Test-Path (Join-Path $appDir 'lib')) {
        throw "app\lib no debe existir (rompe Assembly.Load de managed DLLs)"
    }
    foreach ($culture in @('es', 'en')) {
        $cdir = Join-Path $appDir $culture
        if (-not (Test-Path $cdir)) { throw "Normal publish incompleto: falta app\$culture\" }
        $sat = @(Get-ChildItem $cdir -Filter '*.dll' -File -ErrorAction SilentlyContinue)
        if ($sat.Count -lt 5) { throw "Normal: app\$culture\ no tiene satelites suficientes" }
    }

    $inner = Get-Item (Join-Path $appDir 'AetherPC.exe')
    if ($inner.Length -gt 8MB) {
        throw "app\AetherPC.exe parece single-file ($([math]::Round($inner.Length/1MB,1)) MB). El Setup debe usar el host pequeno + DLLs."
    }
    $stubItem = Get-Item $stub
    if ($stubItem.Length -gt 25MB) {
        throw "El launcher de raiz es demasiado grande ($([math]::Round($stubItem.Length/1MB,1)) MB)."
    }
    $dllCount = @(Get-ChildItem $appDir -Filter '*.dll' -File).Count
    if ($dllCount -lt 50) {
        throw "Normal\app tiene demasiado pocas DLL ($dllCount). No es un publish completo."
    }
}

Write-Host "=== CLEAN DIST LEFTOVERS ==="
foreach ($legacy in @(
        'AetherPC-Full', 'AetherPC-Portable', 'AetherPC-Normal-win-x64',
        'AetherPC-win-x64', 'AetherPC-win-x64-smoke', '_portable-build', '_install-verify'
    )) {
    $p = Join-Path $dist $legacy
    if (Test-Path $p) {
        Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed leftover $legacy"
    }
}

Write-Host "=== PUBLISH NORMAL (layout: launcher + app\) ==="
$tmpNormal = Join-Path $dist '_normal-build'
Publish-AetherPC $tmpNormal
Clear-DistJunk $tmpNormal
Get-ChildItem $tmpNormal -File -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -like 'Uninstall*' -or $_.Name -eq 'AetherPC_Portable.exe'
} | Remove-Item -Force
# Payload = carpeta fresca (el Setup siempre sale de aqui). Normal se espeja despues.
Organize-NormalLayout $tmpNormal $payload
Assert-NormalPublish $payload
try {
    Remove-DirRetry $normal
    Copy-Item $payload $normal -Recurse -Force
} catch {
    Write-Host "AVISO: dist\Normal esta en uso; el Setup usa dist\Payload (layout identico)."
}
$innerExe = Get-Item (Join-Path $payload 'app\AetherPC.exe')
$stubExe = Get-Item (Join-Path $payload 'AetherPC.exe')
Write-Host ("Payload OK: " + (Get-ChildItem $payload -Recurse -File).Count + " files, launcher " + $stubExe.Length + " bytes, app host " + $innerExe.Length + " bytes")

Write-Host "=== PUBLISH PORTABLE (separate) ==="
$tmpPortable = Join-Path $dist '_portable-build'
Publish-AetherPC $tmpPortable -SingleFile
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Get-ChildItem $portable -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -notin @('.sys', '.log') } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $tmpPortable 'AetherPC.exe') (Join-Path $portable 'AetherPC_Portable.exe') -Force
Remove-Item $tmpPortable -Recurse -Force -ErrorAction SilentlyContinue
Clear-DistJunk $portable
$portExe = Get-Item (Join-Path $portable 'AetherPC_Portable.exe')
if ($portExe.Length -lt 20MB) {
    throw "AetherPC_Portable.exe es demasiado pequeno; el single-file no se genero bien."
}
if (Test-Path (Join-Path $portable 'AetherPC.exe')) {
    throw "Portable no debe contener AetherPC.exe; solo AetherPC_Portable.exe"
}
Write-Host ("Portable OK: " + $portExe.Length + " bytes")

Write-Host "=== VERIFY ISS USES PAYLOAD (folder layout, not Portable) ==="
$issText = Get-Content $iss -Raw -Encoding UTF8
if ($issText -match 'dist\\Portable\\') {
    throw "AetherPC.iss todavia referencia dist\Portable. El Setup debe salir de Payload."
}
if ($issText -notmatch 'DistRoot' -and $issText -notmatch 'dist\\Payload\\') {
    throw "AetherPC.iss no empaqueta dist\Payload."
}

Write-Host "=== COMPILE SETUP FROM PAYLOAD ==="
New-Item -ItemType Directory -Force -Path $installerOut | Out-Null
$bytes = [System.IO.File]::ReadAllBytes($iss)
if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF) {
    $text = [System.IO.File]::ReadAllText($iss)
    [System.IO.File]::WriteAllText($iss, $text, (New-Object System.Text.UTF8Encoding $true))
}

$isccOut = Join-Path $dist '_iscc-out'
if (Test-Path $isccOut) { Remove-Item $isccOut -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $isccOut | Out-Null
& $iscc "/DMyAppVersion=$version" "/DDistRoot=..\dist\Payload" "/O$isccOut" "/FAetherPC_Setup" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
$builtSetup = Join-Path $isccOut 'AetherPC_Setup.exe'
if (-not (Test-Path $builtSetup)) { throw "No se genero AetherPC_Setup.exe" }
$setup = Join-Path $installerOut 'AetherPC_Setup.exe'
try {
    Copy-Item $builtSetup $setup -Force
} catch {
    Write-Host "AVISO: dist\Installer\AetherPC_Setup.exe en uso. Copia en dist\_iscc-out."
    $setup = $builtSetup
}

$readme = @"
# AetherPC — Distribucion

Version: $version
Publicado: $(Get-Date -Format 'yyyy-MM-dd HH:mm')

## Installer
dist\Installer\AetherPC_Setup.exe
Fuente: dist\Payload (layout launcher + app\, NO Portable)
Instala en C:\Program Files\AetherPC\

## Payload / Normal
dist\Payload\ y dist\Normal\
Raiz: AetherPC.exe (launcher). Runtime self-contained en app\ (es\ y en\ junto al host interno).

## Portable
dist\Portable\AetherPC_Portable.exe
Un solo archivo, independiente del Setup.
"@
[System.IO.File]::WriteAllText(
    (Join-Path $dist 'README-DISTRIBUCION.md'),
    $readme,
    (New-Object System.Text.UTF8Encoding $false))

Write-Host "=== DONE ==="
Get-Item $setup, $portExe | Format-Table Name, @{ N = 'MB'; E = { [math]::Round($_.Length / 1MB, 1) } }, FullName -AutoSize
$layout = if (Test-Path (Join-Path $payload 'AetherPC.exe')) { $payload } else { $normal }
Write-Host ("Layout files: " + (Get-ChildItem $layout -Recurse -File).Count)
Write-Host ("Layout MB: " + [math]::Round(((Get-ChildItem $layout -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1))
Write-Host "SETUP SOURCE: dist\Payload (launcher + app\, NOT Portable)"
