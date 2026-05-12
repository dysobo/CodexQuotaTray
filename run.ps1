$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'bin\CodexQuotaTray.exe'

if (-not (Test-Path $exe)) {
    & (Join-Path $root 'build.ps1')
}

Start-Process -FilePath $exe
