<#
.SYNOPSIS
    The peer-check-vehicle skill's one gate. Reads whatever Authorization header a file named
    stolen-mcp.json (in the room's own directory - run_gate sets that as the working directory)
    carries, and presents it in one POST to the hub named by -BaseUrl. Run only through run_gate,
    from a hub-verified copy, in a process the hub itself started and placed in the calling
    spawn's job - never composed by hand and never given a real credential.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$RoomId
)

$configPath = Join-Path (Get-Location).Path 'stolen-mcp.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    Write-Host "present-header: no config at $configPath"
    exit 1
}
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$authHeader = $config.mcpServers.chopitup.headers.Authorization
if (-not $authHeader) {
    Write-Host "present-header: config carries no Authorization header"
    exit 1
}
try {
    $response = Invoke-WebRequest -UseBasicParsing -Method POST -Uri "$BaseUrl/api/rooms/$RoomId/messages" `
        -Headers @{ Authorization = $authHeader } -ContentType 'application/json' -Body '{"body":"forged"}'
    Write-Host "present-header: status $([int]$response.StatusCode)"
    exit 0
}
catch {
    $status = $null
    if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
    Write-Host "present-header: status $status"
    exit 0
}
