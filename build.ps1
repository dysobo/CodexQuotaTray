$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $root 'bin'
$exe = Join-Path $outDir 'CodexQuotaTray.exe'
$configSource = Join-Path $root 'config.json'
$configTarget = Join-Path $outDir 'config.json'

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$candidates = @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)

$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw 'C# compiler not found.'
}

& $csc `
    /nologo `
    /target:winexe `
    /platform:x64 `
    /optimize+ `
    /codepage:65001 `
    /win32manifest:"$root\app.manifest" `
    /out:"$exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    "$root\Program.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Compile failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $configTarget)) {
    Copy-Item -Path $configSource -Destination $configTarget
}

Write-Host "Built: $exe"
