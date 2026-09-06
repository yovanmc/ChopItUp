<#
.SYNOPSIS
    Re-measures the two headless spawn command lines the hub relies on (M5 plan, claims 13 and 14)
    against a running hub, through the same ProcessStartInfo.ArgumentList + redirected-stdin shape
    the hub's ProcessRunner uses.

.DESCRIPTION
    Not a unit test and not automatable without spend: each run makes one short model call per CLI
    on the owner's subscriptions. Run it when a CLI updates, or when a spawn stops posting and the
    question is "did the contract move?". It never prints a token.

    Expects a hub already running on -Port with its data directory at -DataDir (for tokens.json).
    Posts as the `sonnet` row via claude and as the `gpt-5.4-mini` row via codex, then reads
    /health to confirm both posts landed as those authors.

    Exit 0 when both legs pass; 1 otherwise; 2 on a usage error.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataDir,
    [int]$Port = 8796
)

$ErrorActionPreference = 'Stop'
$tokensPath = Join-Path $DataDir 'tokens.json'
if (-not (Test-Path -LiteralPath $tokensPath -PathType Leaf)) { Write-Error "No tokens.json under '$DataDir'; start the hub against it first."; exit 2 }
$claudeCmd = Get-Command claude -ErrorAction SilentlyContinue
$codexCmd = Get-Command codex -ErrorAction SilentlyContinue
if (-not $claudeCmd -or -not $codexCmd) { Write-Error "claude and codex must both resolve on PATH (claude=$($claudeCmd.Source) codex=$($codexCmd.Source))."; exit 2 }

$tokens = Get-Content -LiteralPath $tokensPath -Raw | ConvertFrom-Json
$mcpUrl = "http://127.0.0.1:$Port/mcp"
$stamp = [guid]::NewGuid().ToString('N').Substring(0, 8)
$work = Join-Path $env:TEMP "chopitup_probe_$stamp"
New-Item -ItemType Directory -Path (Join-Path $work 'claude') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $work 'codex') -Force | Out-Null

# NOTE: the parameter is $argv, never $args - PowerShell reserves $args, and a helper that names a
# parameter $args silently receives nothing (measured 2026-09-05: both CLIs launched with no
# arguments and the probe reported a false negative).
function Invoke-Child {
    param([string]$FileName, [string[]]$argv, [hashtable]$Env, [string]$Stdin, [string]$WorkDir)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    foreach ($a in $argv) { $psi.ArgumentList.Add($a) }
    foreach ($k in $Env.Keys) { $psi.Environment[$k] = $Env[$k] }
    $psi.WorkingDirectory = $WorkDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    $p.StandardInput.Write($Stdin)
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(180000)) { try { $p.Kill($true) } catch { } }
    $p.WaitForExit()
    [pscustomobject]@{ Exit = $p.ExitCode; Out = $out.Result; Err = $err.Result; Seconds = [int]$sw.Elapsed.TotalSeconds }
}

$pass = 0; $total = 2
try {
    # --- Leg 1: claude.exe, token in a per-spawn mcp.json, prompt on stdin ------------------------------
    $claudeTok = $tokens.sonnet
    $claudeDir = Join-Path $work 'claude'
    $mcpJson = '{"mcpServers":{"chopitup":{"type":"http","url":"' + $mcpUrl + '","headers":{"Authorization":"Bearer ' + $claudeTok + '"}}}}'
    Set-Content -LiteralPath (Join-Path $claudeDir 'mcp.json') -Value $mcpJson -NoNewline -Encoding utf8
    $claudeArgs = @('-p', '--tools', '', '--strict-mcp-config', '--mcp-config', (Join-Path $claudeDir 'mcp.json'),
        '--allowedTools', 'mcp__chopitup__post_message,mcp__chopitup__recall,mcp__chopitup__propose_memory', '--no-session-persistence', '--model', 'sonnet',
        '--output-format', 'json', '--disable-slash-commands', '--setting-sources', '')
    $r1 = Invoke-Child -FileName $claudeCmd.Source -argv $claudeArgs -Env @{} -WorkDir $claudeDir `
        -Stdin "You are participant 'sonnet' in room 'general'. Call post_message once with room_id 'general', body exactly 'probe ok from claude' and client_key 'probe-claude-$stamp'. Then reply with the word done."
    $j1 = $null; try { $j1 = $r1.Out | ConvertFrom-Json } catch { }
    $ok1 = ($r1.Exit -eq 0) -and $j1 -and ($j1.result -match 'done') -and (-not $j1.permission_denials)
    Write-Host ("{0}  claude  exit={1} {2}s result={3} denials={4}" -f ($(if ($ok1) { 'PASS' } else { 'FAIL' })), $r1.Exit, $r1.Seconds, ($j1.result ?? '(no json)'), (($j1.permission_denials | ConvertTo-Json -Compress) ?? '[]'))
    if (-not $ok1) { Write-Host ("  stderr: " + $r1.Err.Replace($claudeTok, '<token>').Substring(0, [Math]::Min(600, $r1.Err.Length))) }
    if ($ok1) { $pass++ }

    # --- Leg 2: codex.cmd through cmd /d /c, token in the environment, prompt on stdin ------------------
    $codexTok = $tokens.'gpt-5.4-mini'
    $codexDir = Join-Path $work 'codex'
    $codexArgs = @('/d', '/c', $codexCmd.Source, 'exec', '--ephemeral', '--ignore-user-config',
        '-c', "mcp_servers.chopitup.url=$mcpUrl",
        '-c', 'mcp_servers.chopitup.bearer_token_env_var=CHOPITUP_TOKEN',
        '-c', 'mcp_servers.chopitup.startup_timeout_sec=20',
        '-c', 'mcp_servers.chopitup.tool_timeout_sec=60',
        '--approve-for-me', '-C', $codexDir, '--skip-git-repo-check', '-m', 'gpt-5.4-mini',
        '--color', 'never', '-o', (Join-Path $codexDir 'last.txt'), '-')
    $r2 = Invoke-Child -FileName (Join-Path $env:SystemRoot 'System32\cmd.exe') -argv $codexArgs -Env @{ CHOPITUP_TOKEN = $codexTok } -WorkDir $codexDir `
        -Stdin "You are participant 'gpt-5.4-mini' in room 'general'. Call the chopitup post_message tool once with room_id 'general', body exactly 'probe ok from codex' and client_key 'probe-codex-$stamp'. Then reply with the word done."
    $last = Get-Content -LiteralPath (Join-Path $codexDir 'last.txt') -ErrorAction SilentlyContinue
    $ok2 = ($r2.Exit -eq 0) -and ($last -match 'done') -and ($r2.Err -match 'mcp: chopitup/post_message \(completed\)')
    Write-Host ("{0}  codex   exit={1} {2}s last={3}" -f ($(if ($ok2) { 'PASS' } else { 'FAIL' })), $r2.Exit, $r2.Seconds, ($last ?? '(none)'))
    if (-not $ok2) { Write-Host ("  stderr tail: " + $r2.Err.Replace($codexTok, '<token>').Substring([Math]::Max(0, $r2.Err.Length - 800))) }
    if ($ok2) { $pass++ }

    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 5
    Write-Host ("health key_usage: " + (($health.key_usage | ForEach-Object { "$($_.author)=$($_.keyed)" }) -join ' '))
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Results: $pass/$total PASS"
if ($pass -eq $total) { exit 0 } else { exit 1 }
