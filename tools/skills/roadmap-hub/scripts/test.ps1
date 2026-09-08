#requires -Version 7
# Gate: the repo's tests. The first .slnx/.sln at the room root, then npm test in the first
# src/*/client that declares a test script (npm ci first when node_modules is absent).
[CmdletBinding()] param()
$ErrorActionPreference = 'Continue'
$PSNativeCommandUseErrorActionPreference = $false   # native exit codes are read from $LASTEXITCODE; 'Stop' must not throw on them (pwsh 7.6.5 default False, measured 2026-09-07)
$root = (Get-Location).Path
$sln = Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in '.slnx', '.sln' } | Sort-Object Name | Select-Object -First 1
if (-not $sln) { Write-Host "test: no .slnx or .sln at $root"; exit 2 }
& dotnet test $sln.FullName -c Debug --nologo -v minimal
$dotnetExit = $LASTEXITCODE
$clientExit = 0
$srcDir = Join-Path $root 'src'
$pkg = $null
if (Test-Path -LiteralPath $srcDir -PathType Container) {
    $pkg = Get-ChildItem -LiteralPath $srcDir -Directory | ForEach-Object { Join-Path $_.FullName 'client\package.json' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if ($pkg) {
    $scripts = (Get-Content -LiteralPath $pkg -Raw | ConvertFrom-Json).scripts
    if ($scripts -and $scripts.PSObject.Properties.Name -contains 'test') {
        Push-Location (Split-Path -Parent $pkg)
        try {
            if (-not (Test-Path -LiteralPath 'node_modules' -PathType Container)) { & npm ci; if ($LASTEXITCODE -ne 0) { $clientExit = $LASTEXITCODE } }
            if ($clientExit -eq 0) { & npm test; $clientExit = $LASTEXITCODE }
        } finally { Pop-Location }
    }
}
Write-Host "test: dotnet exit $dotnetExit, client exit $clientExit"
if ($dotnetExit -ne 0 -or $clientExit -ne 0) { exit 1 }
exit 0
