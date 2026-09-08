<#
.SYNOPSIS
    The probe-sleep skill's one gate (row 20, task 5, plan critique fold M5 - the rescue leg). Sleeps
    past the Claude Code CLI's documented 5-minute HTTP MCP idle-abort default, so a run that reaches
    its ping proves the raised MCP_TOOL_TIMEOUT / CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT / per-server
    "timeout" knobs (row 20, task 3) actually keep the tool call - here, run_gate itself - alive
    through the hub's own spawn.
#>
[CmdletBinding()]
param()

Start-Sleep -Seconds 400
Write-Host 'slept 400'
exit 0
