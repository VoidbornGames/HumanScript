# checks installer\installer.iss against reality: every Source file exists,
# the built setup exe is newer than all of its inputs, and the version
# matches the extension. exit code 2 = rebuild needed, 1 = cannot build,
# 0 = current. -Build runs ISCC when the verdict is "rebuild needed"
param([switch]$Build)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$iss = Join-Path $root 'installer\installer.iss'
$content = Get-Content $iss -Raw

$issVersion = if ($content -match '#define AppVersion "(.+?)"') { $Matches[1] } else { '<unknown>' }
$extVersion = (Get-Content (Join-Path $root 'vsExtention\package.json') -Raw | ConvertFrom-Json).version
$sources = [regex]::Matches($content, 'Source:\s*"([^"]+)"') | ForEach-Object {
    $_.Groups[1].Value -replace '\{#PubDir\}', 'src\HSharp\compiler\bin\Release\net8.0' `
                        -replace '\{#LspDir\}', 'src\HSharp\lsp\bin\Release\net8.0'
}

Write-Host "installer version: $issVersion   extension version: $extVersion"

$missing = @()
$newestInput = $null
foreach ($s in $sources) {
    $p = Join-Path $root $s
    if (-not (Test-Path $p)) { $missing += $s; Write-Host "  MISSING: $s"; continue }
    $t = (Get-Item $p).LastWriteTimeUtc
    if (-not $newestInput -or $t -gt $newestInput) { $newestInput = $t }
    Write-Host "  ok:      $s"
}

$out = Get-ChildItem (Join-Path $root 'installer') -Filter 'HSharp-Compiler-Setup-*.exe' |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1

$rebuild = $false
$reason = ''

if ($missing.Count -gt 0) {
    Write-Host "VERDICT: cannot build installer - publish outputs are missing (run the publish step first)" -ForegroundColor Red
    exit 1
}

if (-not $out) {
    $rebuild = $true
    $reason = 'no setup exe has been built yet'
} elseif ($out.LastWriteTimeUtc -lt $newestInput) {
    $rebuild = $true
    $age = [int][double]::Parse(($newestInput - $out.LastWriteTimeUtc).TotalMinutes, [Globalization.CultureInfo]::InvariantCulture)
    $reason = "inputs changed $age minute(s) after the last installer build ($($out.Name))"
}

if ($issVersion -ne $extVersion) {
    Write-Host "NOTE: installer.iss AppVersion ($issVersion) differs from the extension version ($extVersion)" -ForegroundColor Yellow
}

if (-not $rebuild) {
    Write-Host "VERDICT: installer is current ($($out.Name))" -ForegroundColor Green
    exit 0
}

Write-Host "VERDICT: REBUILD NEEDED - $reason" -ForegroundColor Yellow
$iscc = @('C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe') |
    Where-Object { Test-Path $_ } | Select-Object -First 1

if ($Build) {
    if (-not $iscc) {
        Write-Host 'ISCC.exe not found; install Inno Setup 6 to build the installer' -ForegroundColor Red
        exit 1
    }
    Write-Host "building installer with $iscc ..."
    & $iscc (Join-Path $root 'installer\installer.iss')
    if ($LASTEXITCODE -ne 0) { Write-Host 'installer build FAILED' -ForegroundColor Red; exit 1 }
    Write-Host 'installer built.' -ForegroundColor Green
    exit 0
}

Write-Host "run 'build-dev.bat --installer' (or ISCC.exe installer\installer.iss) to rebuild"
exit 2
