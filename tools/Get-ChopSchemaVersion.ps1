<#
.SYNOPSIS
    Prints ChopDb.LatestSchemaVersion, read from src\ChopItUp.Core\Storage\ChopDb.cs, so a check
    script compares the hub's schema against the version this checkout builds.
#>
$ErrorActionPreference = 'Stop'

$path = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ChopItUp.Core\Storage\ChopDb.cs'
$match = [regex]::Match((Get-Content -LiteralPath $path -Raw), 'public const int LatestSchemaVersion = (\d+);')
if (-not $match.Success) { throw "LatestSchemaVersion not found in $path" }
[int]$match.Groups[1].Value
