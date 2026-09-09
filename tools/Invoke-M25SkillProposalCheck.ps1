<#
.SYNOPSIS
    Row 25 (M25 task 9) live check: drives a SCRATCH hub end to end over the real MCP and /api
    surfaces -- propose_skill, the owner-gated decision endpoints, and every refusal leg the plan
    names as not optional (AC2's two new refusals, AC3's dedup, AC5's 403 half, AC8's interrupted
    retry, the live-hub swap failure, a mutated source, sourceChanged suppression, and a staged-copy
    mismatch). Never the deployed hub; never --import-skill; never a real tokens.json.

.DESCRIPTION
    Mirrors Invoke-M18MemoryCheck.ps1's frame (param block, Add-Check, a fresh -DataDir under
    $env:TEMP, the hub started by PID and stopped in a finally block, "Results: n/m PASS", exit 0
    only when every check passes) but drives a NEW MCP tool (propose_skill) and TWO owner-gated
    decision endpoints instead of memory's. Like the other M-check scripts, it leaves its data dir
    and log in place (no -KeepEvidence switch; nothing here is deleted).

    LESSON M11's converse (binds this whole script): every assertion is on something the hub itself
    controls -- a status code, a JSON field the hub wrote, an exit code, a file's presence/absence/
    mtime -- never on how a model worded a note or an error message's prose beyond a short substring
    the hub's own source pins verbatim.
    LESSON M10: Invoke-RestMethod wraps a top-level JSON array in one Object[]; enumerate before
    filtering.
    LESSON 2026-09-07: Start-Process -ArgumentList joins with spaces and quotes nothing -- every path
    element carries its own literal double quotes.

    Two legs are deliberately NOT driven through the live HTTP surface, and the reason is recorded
    at each site rather than skipped silently:
      - The "staged-copy mismatch" leg (task 2, D5) is the window between Approve's own re-hash of
        the source and SkillImport.Run's re-hash of the STAGED COPY -- both reads happen back to
        back inside one synchronous request, so reaching it through black-box HTTP timing would be a
        flaky race (exactly the kind lesson M24 warns a mutation gate must not depend on). SkillImport.
        Run is documented as "a public static entry point callable in-process" (plan claim 6), so this
        script instead loads the REAL, already-built ChopItUp.Hub.dll/ChopItUp.Core.dll (Add-Type,
        same technique as Invoke-M25DryRun.ps1's direct Microsoft.Data.Sqlite load) and calls
        SkillImport.Run directly with a deliberately corrupted expectedTree, against a throwaway
        skills root that is never the scratch hub's own -- deterministic, and it exercises the exact
        compiled code path task 2 added.
      - AC8's "kill the hub between the mark and the install record" leg is reproduced literally: the
        hub is actually stopped, the install is actually performed in-process against its own (now
        unheld) database and skills root, the row is marked approved by a direct SQL UPDATE with
        installed_at left NULL (the exact crash state), and the hub is actually restarted before the
        Retry call -- no in-memory shortcut, no timing race.

.PARAMETER KeepBuild
    Skip the `dotnet build` step (assumes the hub exe is already current). Off by default.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_m25skillcheck_' + [guid]::NewGuid().ToString('N'))),
    [int]$Port = 8831,
    [int]$TimeoutSeconds = 30,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.m25-skillcheck.log"
$repoRoot = Split-Path -Parent $PSScriptRoot
$slnPath = Join-Path $repoRoot 'ChopItUp.slnx'
$hubBin = Split-Path -Parent $HubExe

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $line = ('{0}  {1}  {2}' -f ($(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail))
    Write-Host $line
    Add-Content -Path $log -Value $line
}

if (-not $SkipBuild) {
    Write-Host "Building $slnPath (Debug, -warnaserror)..."
    & dotnet build $slnPath -c Debug -warnaserror -v minimal
    if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build failed with exit code $LASTEXITCODE." -ErrorAction Continue; exit 2 }
}
if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
if (Test-Path -LiteralPath $DataDir) {
    Write-Error "-DataDir '$DataDir' already exists; this script only ever runs against a fresh directory." -ErrorAction Continue
    exit 2
}
# A SIBLING of $DataDir, never nested inside it: RoomPathRules refuses any room directory inside the
# hub's own data folder (measured directly against this build while writing this script), so
# --rooms-root has to live outside $DataDir entirely.
$roomsRoot = $DataDir + '_rooms-root'
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
New-Item -ItemType Directory -Path $roomsRoot -Force | Out-Null
Add-Content -Path $log -Value ("M25 skill-proposal check {0} exe={1} data={2} port={3}" -f (Get-Date -Format o), $HubExe, $DataDir, $Port)

# --- Load the real Microsoft.Data.Sqlite + ChopItUp.Hub/.Core assemblies straight from the hub's own
# build output (same technique Invoke-M25DryRun.ps1 uses): the staged-copy-mismatch leg and AC8's
# interrupted-retry leg both need to drive production code in-process, against a REAL compiled DLL,
# deterministically -- never a race against the live hub's own timing. [NullString]::Value (not a bare
# $null) is required for PowerShell's own method-invocation binder to pass an actual null for a
# nullable string parameter with a default value; measured directly against this signature before
# writing the rest of this script -- a bare $null silently becomes "" and changes which branch runs.
$nativeDir = Join-Path $hubBin 'runtimes\win-x64\native'
$env:PATH = $nativeDir + ';' + $env:PATH
Add-Type -Path (Join-Path $hubBin 'SQLitePCLRaw.core.dll')
Add-Type -Path (Join-Path $hubBin 'SQLitePCLRaw.provider.e_sqlite3.dll')
Add-Type -Path (Join-Path $hubBin 'SQLitePCLRaw.batteries_v2.dll')
Add-Type -Path (Join-Path $hubBin 'Microsoft.Data.Sqlite.dll')
Add-Type -Path (Join-Path $hubBin 'ChopItUp.Core.dll')
Add-Type -Path (Join-Path $hubBin 'ChopItUp.Hub.dll')
[SQLitePCL.Batteries_V2]::Init()

$git = Get-Command git -ErrorAction SilentlyContinue
Add-Check -Name 'cli.git-on-path' -Passed ([bool]$git) -Detail ($git.Source ?? 'not found')

# LESSONS M10: drives /mcp itself as the participant named. A JSON-RPC error envelope has no result:
# surfaced as the failure text, never a silent empty success.
function Invoke-McpTool([string]$Participant, [string]$Tool, [hashtable]$Arguments) {
    $token = $script:Tokens.$Participant
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/json, text/event-stream' }
    $rpc = @{ jsonrpc = '2.0'; id = [guid]::NewGuid().ToString('N'); method = 'tools/call'; params = @{ name = $Tool; arguments = $Arguments } } | ConvertTo-Json -Depth 6 -Compress
    $raw = Invoke-WebRequest -Uri "$base/mcp" -Method Post -Headers $headers -ContentType 'application/json' -Body $rpc -TimeoutSec $TimeoutSeconds -SkipHttpErrorCheck
    Add-Content -Path $log -Value ("mcp {0} {1} -> {2}: {3}" -f $Participant, $Tool, $raw.StatusCode, ($raw.Content -replace "`r?`n", ' / '))
    $json = if ($raw.Content -match '(?m)^data:\s*(\{.*\})\s*$') { $Matches[1] } else { $raw.Content }   # SSE or plain JSON
    $envelope = $json | ConvertFrom-Json
    if ($envelope.error) { return [pscustomobject]@{ IsError = $true; Text = "$($envelope.error.code): $($envelope.error.message)"; Json = $null } }
    $text = ($envelope.result.content | Where-Object type -eq 'text' | Select-Object -First 1).text
    return [pscustomobject]@{ IsError = [bool]$envelope.result.isError; Text = $text; Json = $(try { $text | ConvertFrom-Json } catch { $null }) }
}

function Get-Proposals([string]$Room, [string]$Status = 'undecided') {
    # LESSONS M10: a top-level JSON array comes back as one nested Object[]; enumerate before filtering.
    @(Invoke-RestMethod -Uri "$base/api/skills/proposals?room=$Room&status=$Status" -TimeoutSec $TimeoutSeconds | ForEach-Object { $_ })
}

function Invoke-Decide([string]$Verb, [long]$Id, [string]$Token, [string]$Tree) {
    $headers = if ($Token) { @{ Authorization = "Bearer $Token" } } else { @{} }
    $body = if ($null -ne $Tree) { (@{ treeSha256 = $Tree } | ConvertTo-Json -Compress) } else { '{}' }
    Invoke-WebRequest -Uri "$base/api/skills/proposals/$Id/$Verb" -Method Post -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec $TimeoutSeconds -SkipHttpErrorCheck
}

function New-SkillSource([string]$Root, [string]$Name, [string]$SkillMd, [hashtable]$ExtraFiles = @{}) {
    $dir = Join-Path $Root $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $dir 'SKILL.md'), $SkillMd, (New-Object System.Text.UTF8Encoding($false)))
    foreach ($rel in $ExtraFiles.Keys) {
        $full = Join-Path $dir $rel
        New-Item -ItemType Directory -Path (Split-Path -Parent $full) -Force -ErrorAction SilentlyContinue | Out-Null
        [System.IO.File]::WriteAllText($full, $ExtraFiles[$rel], (New-Object System.Text.UTF8Encoding($false)))
    }
    return $dir
}

function SkillMd([string]$Name, [string]$Desc = 'd.') { "---`nname: $Name`ndescription: $Desc`n---`n# $Name`n`nBody text.`n" }

$base = "http://127.0.0.1:$Port"
$hub = $null
try {
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port", '--rooms-root', "`"$roomsRoot`"") -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Add-Check -Name 'hub.started' -Passed ($null -ne $health) -Detail "pid=$($hub.Id)"
    Add-Check -Name 'health.schema-is-10' -Passed ($health.schema -eq 10) -Detail "schema=$($health.schema)"

    $script:Tokens = Get-Content -LiteralPath (Join-Path $DataDir 'tokens.json') -Raw | ConvertFrom-Json

    # A room bound to a scratch, hub-created directory under --rooms-root -- never the real profile.
    $room = Invoke-RestMethod -Uri "$base/api/rooms" -Method Post -ContentType 'application/json' -Body (@{ name = 'skill-room' } | ConvertTo-Json) -TimeoutSec $TimeoutSeconds
    $roomId = $room.id
    $roomDir = $room.directory
    Add-Check -Name 'room.created-under-scratch-rooms-root' -Passed ($null -ne $roomDir -and $roomDir.StartsWith($roomsRoot, [StringComparison]::OrdinalIgnoreCase)) -Detail "roomId=$roomId dir=$roomDir"

    # === Leg A: propose -> list carries every file's text -> 401 -> 403 -> approve with the owner
    #     token -> GET /api/skills lists it ================================================
    $skillMd = SkillMd 'basic-skill'
    $notes = "Notes for basic-skill.`nSecond line.`n"
    $sourceA = New-SkillSource -Root $roomDir -Name 'basic-skill' -SkillMd $skillMd -ExtraFiles @{ 'notes.md' = $notes }
    $proposeA = Invoke-McpTool -Participant 'claude' -Tool 'propose_skill' -Arguments @{ room_id = $roomId; source_dir = $sourceA }
    Add-Check -Name 'propose.ok' -Passed (-not $proposeA.IsError -and $proposeA.Json.status -eq 'pending') -Detail "id=$($proposeA.Json.id) files=$($proposeA.Json.files) status=$($proposeA.Json.status)"
    $idA = $proposeA.Json.id
    $treeA = $proposeA.Json.tree_sha256

    $listedA = Get-Proposals -Room $roomId | Where-Object id -eq $idA
    $entriesA = @($listedA.entries)
    $entryText = @{}
    foreach ($e in $entriesA) { $entryText[$e.path] = $e.text }
    Add-Check -Name 'list.payload-carries-each-files-text' -Passed ($entryText['SKILL.md'] -eq $skillMd -and $entryText['notes.md'] -eq $notes) `
        -Detail "paths=$(($entriesA | ForEach-Object path) -join ',')"

    $noToken = Invoke-Decide -Verb 'approve' -Id $idA -Token $null -Tree $treeA
    Add-Check -Name 'decide.401-no-credential' -Passed ($noToken.StatusCode -eq 401) -Detail "status=$($noToken.StatusCode) body=$($noToken.Content)"

    $nonOwnerToken = $script:Tokens.claude
    $nonOwner = Invoke-Decide -Verb 'approve' -Id $idA -Token $nonOwnerToken -Tree $treeA
    Add-Check -Name 'decide.403-non-owner' -Passed ($nonOwner.StatusCode -eq 403) -Detail "status=$($nonOwner.StatusCode) body=$($nonOwner.Content)"

    $ownerToken = $script:Tokens.owner
    $approvedA = Invoke-Decide -Verb 'approve' -Id $idA -Token $ownerToken -Tree $treeA
    $approvedABody = $approvedA.Content | ConvertFrom-Json
    Add-Check -Name 'decide.approve-with-owner-token' -Passed ($approvedA.StatusCode -eq 200 -and $approvedABody.status -eq 'approved' -and $null -ne $approvedABody.installedAt) `
        -Detail "status=$($approvedA.StatusCode) proposalStatus=$($approvedABody.status) installedAt=$($approvedABody.installedAt)"

    $skillsListed = @(Invoke-RestMethod -Uri "$base/api/skills" -TimeoutSec $TimeoutSeconds | ForEach-Object { $_ })
    Add-Check -Name 'list.get-api-skills-shows-it' -Passed (($skillsListed | Where-Object name -eq 'basic-skill').Count -eq 1) -Detail "names=$(($skillsListed | ForEach-Object name) -join ',')"

    # === Leg B: AC3 dedup -- a repeat offer of the same (name, tree) returns the first proposal ====
    $dupMd = SkillMd 'dup-test'
    $sourceB = New-SkillSource -Root $roomDir -Name 'dup-test' -SkillMd $dupMd
    $first = Invoke-McpTool -Participant 'opus' -Tool 'propose_skill' -Arguments @{ room_id = $roomId; source_dir = $sourceB }
    $second = Invoke-McpTool -Participant 'codex' -Tool 'propose_skill' -Arguments @{ room_id = $roomId; source_dir = $sourceB }
    Add-Check -Name 'ac3.repeat-offer-returns-first-proposal' -Passed (-not $first.IsError -and -not $second.IsError -and $second.Json.duplicate -eq $true -and $second.Json.id -eq $first.Json.id) `
        -Detail "firstId=$($first.Json.id) secondId=$($second.Json.id) duplicate=$($second.Json.duplicate)"
    $dupRows = @(Get-Proposals -Room $roomId -Status 'all' | Where-Object name -eq 'dup-test')
    Add-Check -Name 'ac3.no-second-card' -Passed ($dupRows.Count -eq 1) -Detail "rows=$($dupRows.Count)"
    Invoke-Decide -Verb 'reject' -Id $first.Json.id -Token $ownerToken -Tree $null | Out-Null

    # === Leg C: AC2 refusal -- a file type off D7's allowlist =======================================
    $sourceC = New-SkillSource -Root $roomDir -Name 'bad-ext-test' -SkillMd (SkillMd 'bad-ext-test') -ExtraFiles @{ 'payload.exe' = 'not reviewable' }
    $refusedC = Invoke-McpTool -Participant 'claude' -Tool 'propose_skill' -Arguments @{ room_id = $roomId; source_dir = $sourceC }
    Add-Check -Name 'ac2.refuses-file-off-the-allowlist' -Passed ($refusedC.IsError -and $refusedC.Text -like '*not reviewable text*') -Detail $refusedC.Text
    $rowsC = @(Get-Proposals -Room $roomId -Status 'all' | Where-Object name -eq 'bad-ext-test')
    Add-Check -Name 'ac2.no-row-for-refused-extension' -Passed ($rowsC.Count -eq 0) -Detail "rows=$($rowsC.Count)"

    # === Leg D: AC2 refusal -- a source reached through a junction ==================================
    $outsideDir = Join-Path $DataDir 'outside-secret'
    New-Item -ItemType Directory -Path (Join-Path $outsideDir 'junction-skill') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $outsideDir 'junction-skill\SKILL.md'), (SkillMd 'junction-skill'), (New-Object System.Text.UTF8Encoding($false)))
    $junctionLink = Join-Path $roomDir 'link-out'
    New-Item -ItemType Junction -Path $junctionLink -Target $outsideDir | Out-Null
    $sourceD = Join-Path $junctionLink 'junction-skill'
    $refusedD = Invoke-McpTool -Participant 'claude' -Tool 'propose_skill' -Arguments @{ room_id = $roomId; source_dir = $sourceD }
    Add-Check -Name 'ac2.refuses-source-reached-through-a-junction' -Passed ($refusedD.IsError -and $refusedD.Text -like '*link or junction*') -Detail $refusedD.Text

    # === Leg E: a source mutated between propose and approve; sourceChanged suppression =============
    $mutateOriginal = SkillMd 'mutate-test'
    $sourceE = New-SkillSource -Root $roomDir -Name 'mutate-test' -SkillMd $mutateOriginal
    $proposeE = Invoke-McpTool -Participant 'claude' -Tool 'propose_skill' -Arguments @{ room_id = $roomId; source_dir = $sourceE }
    $idE = $proposeE.Json.id
    $treeE = $proposeE.Json.tree_sha256
    [System.IO.File]::WriteAllText((Join-Path $sourceE 'SKILL.md'), (SkillMd 'mutate-test' 'swapped'), (New-Object System.Text.UTF8Encoding($false)))

    $listedE = Get-Proposals -Room $roomId | Where-Object id -eq $idE
    Add-Check -Name 'e.sourceChanged-suppresses-entries' -Passed ($listedE.sourceChanged -eq $true -and $listedE.sourceMissing -eq $false -and @($listedE.entries).Count -eq 0 -and @($listedE.gates).Count -eq 0 -and $listedE.approvable -eq $false) `
        -Detail "sourceChanged=$($listedE.sourceChanged) entries=$(@($listedE.entries).Count) approvable=$($listedE.approvable)"

    $approveE = Invoke-Decide -Verb 'approve' -Id $idE -Token $ownerToken -Tree $treeE
    Add-Check -Name 'e.approve-refuses-source-changed-since-proposed' -Passed ($approveE.StatusCode -eq 409 -and $approveE.Content -like '*source has changed*') -Detail "status=$($approveE.StatusCode) body=$($approveE.Content)"
    $stillPendingE = (Get-Proposals -Room $roomId -Status 'all' | Where-Object id -eq $idE).status
    Add-Check -Name 'e.row-stays-decidable' -Passed ($stillPendingE -eq 'pending') -Detail "status=$stillPendingE"

    # === Leg F: staged-copy mismatch -- direct in-process call, never the live hub's own timing =====
    $probeScratch = Join-Path $DataDir 'staged-copy-probe'
    $probeSource = New-SkillSource -Root $probeScratch -Name 'staged-probe' -SkillMd (SkillMd 'staged-probe')
    $probeSkillsRoot = Join-Path $probeScratch 'skills'
    $probeDb = New-Object ChopItUp.Core.Storage.ChopDb((Join-Path $probeScratch 'chopitup.db'))
    $probeDb.EnsureDatabase()
    $probeHashes = New-Object ChopItUp.Hub.Skills.SkillHashes($probeDb)
    $wrongTree = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $wrongTree['SKILL.md'] = ('0' * 64)
    $mismatchResult = [ChopItUp.Hub.Skills.SkillImport]::Run($probeSource, $probeSkillsRoot, $false, $probeHashes, [NullString]::Value, $wrongTree)
    Add-Check -Name 'f.staged-copy-mismatch-refuses-before-either-move' -Passed ($mismatchResult.Outcome -eq [ChopItUp.Hub.Skills.SkillImportOutcome]::BadArgument -and $mismatchResult.Message -like '*staged copy*does not match*') `
        -Detail "outcome=$($mismatchResult.Outcome) message=$($mismatchResult.Message)"
    Add-Check -Name 'f.staged-copy-mismatch-leaves-no-debris' -Passed (-not (Test-Path (Join-Path $probeSkillsRoot 'staged-probe')) -and -not (Test-Path (Join-Path $probeSkillsRoot 'staged-probe.importing'))) -Detail ''

    # Control: the SAME expectedTree, correctly computed, installs cleanly (proves F's refusal is
    # about the mismatch and not some other defect blocking every call).
    $rightTree = [ChopItUp.Hub.Skills.SkillImport]::HashSourceTree($probeSource)
    $okResult = [ChopItUp.Hub.Skills.SkillImport]::Run($probeSource, $probeSkillsRoot, $false, $probeHashes, [NullString]::Value, $rightTree)
    Add-Check -Name 'f.control-matching-tree-installs' -Passed ($okResult.Outcome -eq [ChopItUp.Hub.Skills.SkillImportOutcome]::Ok) -Detail "outcome=$($okResult.Outcome)"

    # === Leg G: AC8 -- kill the hub between the mark and the install record; delete the source too;
    #     restart; re-approve; the listing still says approvable/sourceMissing and it finishes WITHOUT
    #     re-installing ================================================================================
    $retryMd = SkillMd 'retry-test'
    $sourceG = New-SkillSource -Root $roomDir -Name 'retry-test' -SkillMd $retryMd
    $proposeG = Invoke-McpTool -Participant 'claude' -Tool 'propose_skill' -Arguments @{ room_id = $roomId; source_dir = $sourceG }
    $idG = $proposeG.Json.id
    $treeG = $proposeG.Json.tree_sha256

    Write-Host "Stopping hub pid $($hub.Id) to reproduce AC8's crash window..."
    Stop-Process -Id $hub.Id
    $hub.WaitForExit(15000) | Out-Null
    if (-not $hub.HasExited) { throw "Hub process $($hub.Id) did not exit within 15s of Stop-Process (AC8 leg)." }
    Start-Sleep -Milliseconds 300   # let the file locks (chopitup.db, hub.lock) release fully

    $hubSkillsRoot = Join-Path $DataDir 'skills'
    $hubDbPath = Join-Path $DataDir 'chopitup.db'
    $hubDbForInstall = New-Object ChopItUp.Core.Storage.ChopDb($hubDbPath)
    $hubHashesForInstall = New-Object ChopItUp.Hub.Skills.SkillHashes($hubDbForInstall)
    $installResult = [ChopItUp.Hub.Skills.SkillImport]::Run($sourceG, $hubSkillsRoot, $false, $hubHashesForInstall, [NullString]::Value, $null)
    Add-Check -Name 'g.install-performed-while-hub-is-down' -Passed ($installResult.Outcome -eq [ChopItUp.Hub.Skills.SkillImportOutcome]::Ok) -Detail "outcome=$($installResult.Outcome)"
    $installedSkillMd = Join-Path $hubSkillsRoot 'retry-test\SKILL.md'
    $mtimeBeforeRetry = (Get-Item -LiteralPath $installedSkillMd).LastWriteTimeUtc

    # Mark approved with installed_at left NULL -- the exact "killed between the mark and the install
    # record" crash state -- via a raw SQL UPDATE while the hub holds no connection to this file.
    $markConn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$hubDbPath;Mode=ReadWriteCreate;Pooling=False")
    $markConn.Open()
    $markCmd = $markConn.CreateCommand()
    $markCmd.CommandText = "UPDATE skill_proposals SET status = 'approved', decided_at = `$at WHERE id = `$id"
    $markCmd.Parameters.AddWithValue('$at', [DateTimeOffset]::UtcNow.ToString('o')) | Out-Null
    $markCmd.Parameters.AddWithValue('$id', $idG) | Out-Null
    $markedRows = $markCmd.ExecuteNonQuery()
    $markConn.Close(); $markConn.Dispose()
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
    Add-Check -Name 'g.marked-approved-with-installed-at-still-null' -Passed ($markedRows -eq 1) -Detail "rowsUpdated=$markedRows"

    # Row 25 task 9 correction: leg G as first written reproduced AC8's crash window but never deleted
    # $sourceG, so the retry it drove always had a live, unchanged source -- IsApprovable's cheap
    # "!sourceMissing && !sourceChanged" branch was enough on its own, and the fix to the Retry arm's
    # rule (task 7/8, AC8) could be reverted without this leg noticing. Deleting the source here, before
    # the restart, forces the retry through the AlreadyInstalled branch instead -- the one AC8 exists to
    # rescue -- so the leg actually binds the defect it claims to cover.
    Remove-Item -LiteralPath $sourceG -Recurse -Force
    Add-Check -Name 'g.source-deleted-before-restart' -Passed (-not (Test-Path -LiteralPath $sourceG)) -Detail "sourceG=$sourceG"

    Write-Host "Restarting hub..."
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port", '--rooms-root', "`"$roomsRoot`"") -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub2.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub2.stdout.log')
    $health2 = $null
    foreach ($i in 1..40) {
        try { $health2 = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Add-Check -Name 'g.hub-restarted' -Passed ($null -ne $health2) -Detail "pid=$($hub.Id)"

    # AC8 with the source gone too: the listing must still mark this row approvable, via the
    # AlreadyInstalled branch, and say why the source is missing (sourceMissing) rather than trusting a
    # live read of a directory that is no longer there.
    $listedG = Get-Proposals -Room $roomId -Status 'all' | Where-Object id -eq $idG
    Add-Check -Name 'g.listing-approvable-with-source-missing' -Passed ($listedG.approvable -eq $true -and $listedG.sourceMissing -eq $true) `
        -Detail "approvable=$($listedG.approvable) sourceMissing=$($listedG.sourceMissing)"

    $retryResp = Invoke-Decide -Verb 'approve' -Id $idG -Token $ownerToken -Tree $null   # the Retry button resends no body
    $retryBody = $retryResp.Content | ConvertFrom-Json
    Add-Check -Name 'g.retry-finishes-with-200' -Passed ($retryResp.StatusCode -eq 200 -and $null -ne $retryBody.installedAt) -Detail "status=$($retryResp.StatusCode) installedAt=$($retryBody.installedAt)"

    $mtimeAfterRetry = (Get-Item -LiteralPath $installedSkillMd).LastWriteTimeUtc
    Add-Check -Name 'g.retry-detected-the-completed-install-without-reinstalling' -Passed ($mtimeAfterRetry -eq $mtimeBeforeRetry) `
        -Detail "before=$($mtimeBeforeRetry.ToString('o')) after=$($mtimeAfterRetry.ToString('o'))"

    $skillsAfterRetry = @(Invoke-RestMethod -Uri "$base/api/skills" -TimeoutSec $TimeoutSeconds | ForEach-Object { $_ })
    Add-Check -Name 'g.retry-test-listed-exactly-once' -Passed ((($skillsAfterRetry | Where-Object name -eq 'retry-test')).Count -eq 1) -Detail "count=$((($skillsAfterRetry | Where-Object name -eq 'retry-test')).Count)"

    # === Leg H: the live-hub swap failure -- approve while an exchange holds a reader open lands
    #     approved-but-uninstalled, never an error ===================================================
    $swapV1 = SkillMd 'swap-test' 'v1'
    $sourceH1 = New-SkillSource -Root $roomDir -Name 'swap-test' -SkillMd $swapV1
    $proposeH1 = Invoke-McpTool -Participant 'claude' -Tool 'propose_skill' -Arguments @{ room_id = $roomId; source_dir = $sourceH1 }
    $approveH1 = Invoke-Decide -Verb 'approve' -Id $proposeH1.Json.id -Token $ownerToken -Tree $proposeH1.Json.tree_sha256
    Add-Check -Name 'h.v1-installed' -Passed ($approveH1.StatusCode -eq 200) -Detail "status=$($approveH1.StatusCode)"

    $swapV2 = SkillMd 'swap-test' 'v2'
    # propose_skill confines the source to the ROOM's own directory, and the directory NAME is the
    # skill name (refusal 2), so v2's source is a fresh 'swap-test' folder under the room -- v1's own
    # source folder of that name was already deleted by Approve's CleanupSource once v1 was decided,
    # so this does not collide with anything on disk; the installed copy lives under skillsRoot, a
    # different directory entirely.
    $sourceH2Final = New-SkillSource -Root $roomDir -Name 'swap-test' -SkillMd $swapV2
    $proposeH2 = Invoke-McpTool -Participant 'claude' -Tool 'propose_skill' -Arguments @{ room_id = $roomId; source_dir = $sourceH2Final; force = $true }
    Add-Check -Name 'h.v2-proposed-as-a-replace' -Passed (-not $proposeH2.IsError -and $proposeH2.Json.replaces_installed -eq $true) -Detail "replaces_installed=$($proposeH2.Json.replaces_installed)"

    $installedSwapMd = Join-Path $hubSkillsRoot 'swap-test\SKILL.md'
    # Open with the SAME sharing SkillStore.Read uses (Read, ReadWrite share) -- the codebase's own
    # measured finding is that Directory.Move still fails against this on Windows (SkillImport.cs's
    # MoveWithRetry doc comment), reproducing a live exchange's reader without needing one.
    $lockStream = [System.IO.File]::Open($installedSwapMd, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $approveH2 = Invoke-Decide -Verb 'approve' -Id $proposeH2.Json.id -Token $ownerToken -Tree $proposeH2.Json.tree_sha256
    }
    finally {
        $lockStream.Close()
        $lockStream.Dispose()
    }
    Add-Check -Name 'h.swap-under-a-live-reader-is-conflict-not-an-error' -Passed ($approveH2.StatusCode -eq 409) -Detail "status=$($approveH2.StatusCode) body=$($approveH2.Content)"

    $rowH2 = Get-Proposals -Room $roomId -Status 'all' | Where-Object id -eq $proposeH2.Json.id
    Add-Check -Name 'h.row-lands-approved-but-uninstalled' -Passed ($rowH2.status -eq 'approved' -and $null -eq $rowH2.installedAt) -Detail "status=$($rowH2.status) installedAt=$($rowH2.installedAt)"

    $survivingText = [System.IO.File]::ReadAllText($installedSwapMd)
    Add-Check -Name 'h.original-install-survives-the-failed-swap' -Passed ($survivingText -eq $swapV1) -Detail "matches v1=$($survivingText -eq $swapV1)"

    # === Leg I: reject -- 401, then the owner token, then it is gone from GET /api/skills ============
    $sourceI = New-SkillSource -Root $roomDir -Name 'reject-test' -SkillMd (SkillMd 'reject-test')
    $proposeI = Invoke-McpTool -Participant 'claude' -Tool 'propose_skill' -Arguments @{ room_id = $roomId; source_dir = $sourceI }
    $rejectNoToken = Invoke-Decide -Verb 'reject' -Id $proposeI.Json.id -Token $null -Tree $null
    Add-Check -Name 'i.reject-401-no-credential' -Passed ($rejectNoToken.StatusCode -eq 401) -Detail "status=$($rejectNoToken.StatusCode)"
    $rejectOwner = Invoke-Decide -Verb 'reject' -Id $proposeI.Json.id -Token $ownerToken -Tree $null
    $rejectBody = $rejectOwner.Content | ConvertFrom-Json
    Add-Check -Name 'i.reject-with-owner-token' -Passed ($rejectOwner.StatusCode -eq 200 -and $rejectBody.status -eq 'rejected') -Detail "status=$($rejectOwner.StatusCode) proposalStatus=$($rejectBody.status)"
    $skillsAfterReject = @(Invoke-RestMethod -Uri "$base/api/skills" -TimeoutSec $TimeoutSeconds | ForEach-Object { $_ })
    Add-Check -Name 'i.rejected-skill-never-listed' -Passed ((($skillsAfterReject | Where-Object name -eq 'reject-test')).Count -eq 0) -Detail ''
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
    Write-Host "Data dir (left in place): $DataDir"
}
exit $(if ($passed -eq $total -and $total -gt 0) { 0 } else { 1 })
