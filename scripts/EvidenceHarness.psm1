Set-StrictMode -Version Latest

$script:EvidenceStatuses = @('PASS', 'FAIL', 'BLOCKED')

function Get-EvidenceUtcRunId {
    (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
}

function Resolve-EvidenceDirectory {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$EvidenceDir
    )

    $root = [IO.Path]::GetFullPath($RepoRoot)
    $approvedRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/copilot-shared-core'))
    $candidate = if ([IO.Path]::IsPathRooted($EvidenceDir)) {
        [IO.Path]::GetFullPath($EvidenceDir)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $root $EvidenceDir))
    }

    $parent = [IO.Path]::GetDirectoryName($candidate)
    $leaf = [IO.Path]::GetFileName($candidate)
    if (-not [string]::Equals($parent, $approvedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $leaf -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
        throw "EvidenceDir must be one safe run-id directory directly below $approvedRoot."
    }

    # A direct child prevents nested junction traversal. Reject an existing root
    # that was replaced by a reparse point before a failed scan attempts cleanup.
    if (Test-Path -LiteralPath $approvedRoot) {
        $rootItem = Get-Item -LiteralPath $approvedRoot -Force
        $attributes = $rootItem.Attributes
        # OneDrive marks ordinary cloud-backed directories as reparse points
        # without a link target. Continue to reject actual junctions/symlinks.
        $hasLinkTarget = -not [string]::IsNullOrWhiteSpace([string]$rootItem.LinkType) -or
            @($rootItem.Target).Count -gt 0
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -and $hasLinkTarget) {
            throw "Evidence root must not be a reparse point: $approvedRoot"
        }
    }

    # This verifies the exact file that will be created, rather than trusting a
    # convention in the caller. --no-index also works before the directory exists.
    & git -C $root check-ignore --quiet --no-index -- (Join-Path $candidate 'manifest.json')
    if ($LASTEXITCODE -ne 0) {
        throw "EvidenceDir is not ignored by git: $candidate"
    }

    if (Test-Path -LiteralPath $candidate) {
        throw "EvidenceDir already exists (refusing to overwrite): $candidate"
    }

    [pscustomobject]@{ RepoRoot = $root; ApprovedRoot = $approvedRoot; FullPath = $candidate }
}

function Write-EvidenceJson {
    param([Parameter(Mandatory)]$Run)

    $Run.Manifest.updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    [IO.File]::WriteAllText(
        $Run.ManifestPath,
        ($Run.Manifest | ConvertTo-Json -Depth 16),
        [Text.UTF8Encoding]::new($false))
}

function New-EvidenceRun {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$EvidenceDir,
        [Parameter(Mandatory)][ValidateSet('Deterministic', 'RealModel', 'BlackBoxSmoke')][string]$Lane,
        [string[]]$Gates = @()
    )

    $resolved = Resolve-EvidenceDirectory -RepoRoot $RepoRoot -EvidenceDir $EvidenceDir
    New-Item -ItemType Directory -Path $resolved.FullPath -Force | Out-Null
    foreach ($child in @('requests', 'traces', 'screenshots', 'junit')) {
        New-Item -ItemType Directory -Path (Join-Path $resolved.FullPath $child) -Force | Out-Null
    }

    $commit = (& git -C $resolved.RepoRoot rev-parse --verify HEAD 2>$null).Trim()
    $dirty = [bool]((& git -C $resolved.RepoRoot status --porcelain).Count)
    $gateMap = [ordered]@{}
    foreach ($gate in $Gates) {
        $gateMap[$gate] = [ordered]@{ status = 'BLOCKED'; detail = 'Not executed'; updatedAtUtc = $null }
    }

    $run = [pscustomobject]@{
        RepoRoot = $resolved.RepoRoot
        FullPath = $resolved.FullPath
        ManifestPath = Join-Path $resolved.FullPath 'manifest.json'
        Manifest = [ordered]@{
            schemaVersion = 1
            lane = $Lane
            startedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
            updatedAtUtc = $null
            completedAtUtc = $null
            commitSha = $commit
            dirtyWorktree = $dirty
            result = 'BLOCKED'
            gates = $gateMap
            models = [ordered]@{}
            images = [ordered]@{}
            notes = @()
        }
    }
    Write-EvidenceJson -Run $run
    return $run
}

function Set-EvidenceGate {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][string]$Gate,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'BLOCKED')][string]$Status,
        [Parameter(Mandatory)][string]$Detail
    )

    if (-not $Run.Manifest.gates.Contains($Gate)) {
        $Run.Manifest.gates[$Gate] = [ordered]@{}
    }
    $Run.Manifest.gates[$Gate] = [ordered]@{
        status = $Status
        detail = $Detail
        updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
    Write-EvidenceJson -Run $Run
}

function Set-EvidenceMetadata {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][ValidateSet('models', 'images')][string]$Section,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    $Run.Manifest[$Section][$Name] = $Value
    Write-EvidenceJson -Run $Run
}

function Add-EvidenceNote {
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][string]$Note)
    $Run.Manifest.notes += $Note
    Write-EvidenceJson -Run $Run
}

function Write-EvidenceArtifact {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][ValidatePattern('^(requests|traces|junit)/[A-Za-z0-9._-]+\.json$')][string]$RelativePath,
        [Parameter(Mandatory)]$Value
    )

    $path = Join-Path $Run.FullPath $RelativePath
    $directory = Split-Path -Parent $path
    if (-not $directory.StartsWith($Run.FullPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Artifact path escaped EvidenceDir.'
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText(
        $path,
        ($Value | ConvertTo-Json -Depth 16),
        [Text.UTF8Encoding]::new($false))
}

function Write-EvidenceJUnit {
    param(
        [Parameter(Mandatory)]$Run,
        [Parameter(Mandatory)][ValidatePattern('^junit/[A-Za-z0-9._-]+\.xml$')][string]$RelativePath,
        [Parameter(Mandatory)][string]$Suite,
        [Parameter(Mandatory)][int]$Tests,
        [Parameter(Mandatory)][int]$Failures,
        [int]$Skipped = 0
    )

    $escape = [Security.SecurityElement]::Escape($Suite)
    $xml = '<?xml version="1.0" encoding="utf-8"?><testsuite name="{0}" tests="{1}" failures="{2}" skipped="{3}" />' -f $escape, $Tests, $Failures, $Skipped
    [IO.File]::WriteAllText(
        (Join-Path $Run.FullPath $RelativePath),
        $xml,
        [Text.UTF8Encoding]::new($false))
}

function Test-EvidenceSecrets {
    param([Parameter(Mandatory)]$Run)

    $patterns = @(
        '(?im)^\s*authorization\s*[:=]',
        '(?i)\bbearer\s+[A-Za-z0-9._~-]{12,}',
        '\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b',
        '(?i)"?(?:password|cookie|set-cookie)"?\s*[:=]\s*"?(?!\[REDACTED\])',
        '(?i)"(?:prompt|message|content|input)"\s*:\s*"(?!\[REDACTED\]|hmac:)[^"]+'
    )
    $allFiles = Get-ChildItem -LiteralPath $Run.FullPath -Recurse -File
    foreach ($file in $allFiles) {
        $relative = $file.FullName.Substring($Run.FullPath.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -replace '\\', '/'
        if ($relative -notmatch '^(manifest\.json|(requests|traces)/[A-Za-z0-9._-]+\.json|junit/[A-Za-z0-9._-]+\.xml|screenshots/[A-Za-z0-9._-]+\.png)$') {
            return [pscustomobject]@{ Safe = $false; File = $file.FullName; Pattern = 'artifact allowlist' }
        }
    }
    $textFiles = $allFiles | Where-Object { $_.Extension -in @('.json', '.xml') }
    foreach ($file in $textFiles) {
        $content = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($pattern in $patterns) {
            if ($content -match $pattern) {
                return [pscustomobject]@{ Safe = $false; File = $file.FullName; Pattern = $pattern }
            }
        }
    }
    [pscustomobject]@{ Safe = $true; File = $null; Pattern = $null }
}

function Complete-EvidenceRun {
    param([Parameter(Mandatory)]$Run)

    $statuses = @($Run.Manifest.gates.Values | ForEach-Object { $_.status })
    $Run.Manifest.result = if ($statuses -contains 'FAIL') { 'FAIL' } elseif ($statuses -contains 'BLOCKED') { 'BLOCKED' } else { 'PASS' }
    $Run.Manifest.completedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    Write-EvidenceJson -Run $Run

    $scan = Test-EvidenceSecrets -Run $Run
    if (-not $scan.Safe) {
        # The run was created only after Resolve-EvidenceDirectory verified the
        # ignored child path. It is therefore safe to remove this failed bundle.
        Remove-Item -LiteralPath $Run.FullPath -Recurse -Force
        throw "Secret scan failed in $($scan.File); bundle removed."
    }
    return $Run.Manifest.result
}

Export-ModuleMember -Function @(
    'Get-EvidenceUtcRunId',
    'New-EvidenceRun',
    'Set-EvidenceGate',
    'Set-EvidenceMetadata',
    'Add-EvidenceNote',
    'Write-EvidenceArtifact',
    'Write-EvidenceJUnit',
    'Test-EvidenceSecrets',
    'Complete-EvidenceRun'
)
