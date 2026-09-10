<#
.SYNOPSIS
    Row 28 Task 7: shared helper the tools/Invoke-*.ps1 live-check harnesses dot-source, so a scratch
    hub's own owner (and, where a harness drives /mcp as a host-file participant, that participant's)
    bearer can be minted once and reused, instead of re-deriving the same fix twelve times.

.DESCRIPTION
    Row 28 Task 4 widened `BearerTokenMiddleware.RequiresAuth` to every non-GET /api request, and
    Task 1 changed tokens.json from a flat plaintext map to a hashed-at-rest shape for host-file rows
    (owner, owner-remote, claude, codex) with spawnable rows (opus, sonnet, fable, the gpt-* rows)
    minted straight into memory and never written at all. Both changes broke this repo's tools/
    live-check harnesses in different ways: the ones that POST to /api now 401 with no credential,
    and the ones that read tokens.json after the hub started and used a value straight off disk as a
    bearer (for /mcp, or for an /api decision route) now get a SHA-256 hex string instead of a
    plaintext token.

    `TokenStore.Load`'s migration guarantee is what this helper leans on: a plaintext STRING entry
    already present in tokens.json when the hub first starts against a data directory is hashed in
    place, not re-minted -- the exact plaintext that was seeded keeps authenticating, forever, exactly
    as any host-file credential does after the migration (this is AC3's schema-evolution guarantee,
    and TokenStoreTests' schema-evolution guard proves it at the unit level). So seeding a known
    plaintext value for 'owner' (and, where a script drives /mcp as 'claude'/'codex'/'owner-remote',
    those ids too) into a FRESH tokens.json before Start-Process ever launches the scratch hub means:
      - the token authenticates from the very first request the hub serves -- no stop/rotate/restart
        dance, no dependency on --rotate-token's own "hub must be stopped" refusal;
      - the file itself holds only that value's SHA-256 from the hub's first start onward -- the
        plaintext this script generated is the only place it exists, held in a script variable and
        (for the mcp-check subprocess pattern) a transient environment variable, cleared in every
        `finally`;
      - nothing here ever reads a credential belonging to a real installation: the value is random,
        generated fresh in this process, for a scratch --data directory this same harness owns and
        (unless -KeepEvidence/-KeepBuild was passed) deletes when it finishes.

    A spawnable participant (opus, sonnet, fable, gpt-*) CANNOT be seeded this way: TokenStore.Load
    mints its ephemeral bearer unconditionally, before it ever looks at what (if anything) tokens.json
    says about that id, so a pre-seeded entry for a spawnable row is silently ignored. There is no
    supported way to learn a spawnable participant's bearer without the hub actually spawning it
    (SpawnerService.Launch calls TokenStore.BearerFor only at the moment of a real spawn, and writes it
    only into that spawn's own data\spawns\<id>\mcp.json for the life of the spawned process). A
    harness that drives /mcp AS a spawnable participant's own id (Invoke-M18MemoryCheck.ps1's and
    Invoke-M25SkillProposalCheck.ps1's use of 'opus') cannot be repaired by this helper, or by any
    tools/-only change; see this task's report for the specific legs affected.
#>

function New-ChopPlaintextToken {
    <# A fresh random bearer in the same shape TokenStore.NewToken produces (32 random bytes,
       base64url, no padding). Matching the real shape isn't load-bearing -- TryResolve hashes
       whatever string it is handed -- but it keeps a reader from mistaking this for something with
       different entropy. #>
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Initialize-ChopScratchTokens {
    <# Seeds tokens.json in $DataDir with plaintext entries for the given participant ids, BEFORE the
       scratch hub's first start against that directory. Must run before Start-Process launches the
       hub against $DataDir -- once the hub has started and migrated the file, writing to it again
       races the hub's own PathMutex-guarded rewrite. $DataDir is created if it does not already
       exist; refuses if tokens.json is already there (a sign this ran too late, after a start).

       Returns a hashtable of id -> plaintext (a copy of what it wrote), so the caller never has to
       re-read tokens.json to learn a value it just chose itself. #>
    param(
        [Parameter(Mandatory)][string]$DataDir,
        [Parameter(Mandatory)][string[]]$ParticipantIds
    )

    if (-not (Test-Path -LiteralPath $DataDir -PathType Container)) {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    }
    $tokensPath = Join-Path $DataDir 'tokens.json'
    if (Test-Path -LiteralPath $tokensPath) {
        throw "Initialize-ChopScratchTokens: '$tokensPath' already exists. Call this BEFORE the hub's first start against this data directory, not after -- tokens.json existing means a hub has already minted its own."
    }
    # A plain Hashtable, not [ordered]: OrderedDictionary does not expose .ContainsKey() (only the
    # IDictionary-shaped .Contains()), and callers of this helper (Invoke-M18MemoryCheck.ps1,
    # Invoke-M25SkillProposalCheck.ps1) call .ContainsKey() to test for a spawnable participant with
    # no seeded entry. Key order in the written JSON doesn't matter to anything that reads it back.
    $tokens = @{}
    foreach ($id in $ParticipantIds) { $tokens[$id] = New-ChopPlaintextToken }
    ($tokens | ConvertTo-Json) | Set-Content -LiteralPath $tokensPath -Encoding utf8
    return $tokens
}

function New-ChopBearerHeaders {
    <# Authorization: Bearer header hashtable for Invoke-RestMethod/Invoke-WebRequest -Headers.
       Merges into (never overwrites) any headers the caller already needs -- e.g. MCP's own Accept
       header for SSE -- matching the "merge, never overwrite" rule row 28 Task 5 set for the client's
       own Authorization header. Returns a NEW hashtable; never mutates $Merge. #>
    param(
        [Parameter(Mandatory)][string]$Token,
        [hashtable]$Merge
    )
    $headers = @{}
    if ($Merge) { foreach ($k in $Merge.Keys) { $headers[$k] = $Merge[$k] } }
    $headers['Authorization'] = "Bearer $Token"
    return $headers
}

function Get-ChopTokenSha256 {
    <# The same digest TokenStore.Hash computes (lowercase hex SHA-256 of the UTF-8 plaintext), so a
       harness can assert "this pre-seeded plaintext is exactly what tokens.json now hashes to" after
       a migration, instead of comparing the plaintext to the post-migration file's value directly
       (which is a hash object, not a string, and will never again equal the plaintext). #>
    param([Parameter(Mandatory)][string]$Plaintext)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Plaintext))
        return -join ($hashBytes | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $sha256.Dispose()
    }
}
