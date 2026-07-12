<#
.SYNOPSIS
    Genera el instalador KlaviyoCRC-Setup.exe actualizado en un solo paso:
    dotnet publish (self-contained) + compilación con Inno Setup.

.PARAMETER Version
    Opcional. Versión a asignar al instalador (ej. "1.1.0"). Si se omite, se
    usa la versión definida dentro de installer\KlaviyoCRC.iss.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Version 1.1.0
#>
param(
    [string]$Version
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "KlaviyoCRC.csproj"
$publishDir = Join-Path $root "publish"
$issFile = Join-Path $root "installer\KlaviyoCRC.iss"

$isccCandidates = @(
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "No se encontro ISCC.exe (Inno Setup). Instalalo con: winget install --id JRSoftware.InnoSetup -e"
}

Write-Host "==> Limpiando publish anterior..." -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

Write-Host "==> dotnet publish (Release, win-x64, self-contained)..." -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish fallo (exit code $LASTEXITCODE)."
}

Write-Host "==> Compilando instalador con Inno Setup..." -ForegroundColor Cyan
$isccArgs = @()
if ($Version) {
    $isccArgs += "/DMyAppVersion=$Version"
}
$isccArgs += $issFile

& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe fallo (exit code $LASTEXITCODE)."
}

Write-Host ""
Write-Host "Listo: $root\installer\Output\KlaviyoCRC-Setup.exe" -ForegroundColor Green
