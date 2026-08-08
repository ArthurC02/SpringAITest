# Local start/evidence wrappers opt into Development without modifying infra/.env.
param([switch]$IgnoreDotEnv)

$dotEnvPath = Join-Path (Join-Path (Join-Path $PSScriptRoot '..') 'infra') '.env'   # PS 5.1 僅支援 2-arg Join-Path
function Test-DotEnvKey([string]$Name) {
    if (-not (Test-Path -LiteralPath $dotEnvPath)) { return $false }
    $escaped = [Regex]::Escape($Name)
    return [bool](Select-String -LiteralPath $dotEnvPath -Pattern "^\s*(?:export\s+)?$escaped\s*=" -Quiet)
}

$defaulted = @()
$preserved = @()
foreach ($name in @('ASPNETCORE_ENVIRONMENT', 'APP_ENVIRONMENT')) {
    $processValue = Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
    if ($null -eq $processValue -and ($IgnoreDotEnv -or -not (Test-DotEnvKey $name))) {
        Set-Item -LiteralPath "Env:$name" -Value 'Development'
        $defaulted += $name
    } else {
        $preserved += $name
    }
}

if ($defaulted.Count -gt 0) {
    Write-Host "  Local development environment defaulted: $($defaulted -join ', ')"
}
if ($preserved.Count -gt 0) {
    Write-Host "  Explicit process/infra\.env environment preserved: $($preserved -join ', ')"
}
