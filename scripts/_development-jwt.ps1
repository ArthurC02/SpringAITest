# Development-only ES256 signer for evidence identities that cannot log in.
# Resolve the same process > infra/.env > committed-development fallback order
# that Compose uses. The private key remains Backend/evidence-runner-only and is
# never exported to, or passed into, Platform.
function Get-DevelopmentJwtFileSetting([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $escaped = [Regex]::Escape($Name)
    $line = Get-Content -LiteralPath $Path |
        Where-Object { $_ -match "^\s*(?:export\s+)?$escaped\s*=" } |
        Select-Object -First 1
    if ($null -eq $line) { return $null }
    $value = $line.Substring($line.IndexOf('=') + 1).Trim()
    if ($value.Length -ge 2 -and (($value[0] -eq "'" -and $value[-1] -eq "'") -or ($value[0] -eq '"' -and $value[-1] -eq '"'))) {
        $value = $value.Substring(1, $value.Length - 2)
    }
    return $value
}

# PS 5.1 僅支援 2-arg Join-Path，infra 路徑先組好共用。
$script:developmentJwtInfraDir = Join-Path (Join-Path $PSScriptRoot '..') 'infra'

function Get-DevelopmentJwtSetting([string]$Name) {
    $processItem = Get-Item -LiteralPath "Env:$Name" -ErrorAction SilentlyContinue
    if ($null -ne $processItem) {
        if (-not [string]::IsNullOrWhiteSpace($processItem.Value)) { return $processItem.Value }
        # An explicitly empty Compose value reaches the application as empty;
        # Development then resolves its committed fallback rather than .env.
        $fallback = Get-DevelopmentJwtFileSetting (Join-Path $script:developmentJwtInfraDir '.env.example') $Name
        if (-not [string]::IsNullOrWhiteSpace($fallback)) { return $fallback }
        throw "Missing committed development JWT fallback: $Name"
    }

    $infraValue = Get-DevelopmentJwtFileSetting (Join-Path $script:developmentJwtInfraDir '.env') $Name
    if (-not [string]::IsNullOrWhiteSpace($infraValue)) { return $infraValue }
    $fallback = Get-DevelopmentJwtFileSetting (Join-Path $script:developmentJwtInfraDir '.env.example') $Name
    if ([string]::IsNullOrWhiteSpace($fallback)) { throw "Missing development JWT setting: $Name" }
    return $fallback
}

function ConvertTo-DevelopmentJwtBase64Url([byte[]]$Bytes) {
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-DevelopmentEvidenceJwt([string]$User, [string]$Tenant) {
    $issuer = Get-DevelopmentJwtSetting 'JWT_ISSUER'
    $audience = Get-DevelopmentJwtSetting 'JWT_AUDIENCE'
    $kid = Get-DevelopmentJwtSetting 'JWT_ACTIVE_KID'
    $privateKey = Get-DevelopmentJwtSetting 'JWT_PRIVATE_KEY_PEM_BASE64'
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $header = ConvertTo-DevelopmentJwtBase64Url ([Text.Encoding]::UTF8.GetBytes((@{
        alg = 'ES256'; kid = $kid; typ = 'JWT'
    } | ConvertTo-Json -Compress)))
    $payload = ConvertTo-DevelopmentJwtBase64Url ([Text.Encoding]::UTF8.GetBytes((@{
        iss = $issuer; aud = $audience; sub = $User; role = 'USER'; tenantCode = $Tenant
        iat = $now; nbf = $now; exp = $now + 600
    } | ConvertTo-Json -Compress)))
    $unsigned = "$header.$payload"
    $pem = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($privateKey))
    $ecdsa = [Security.Cryptography.ECDsa]::Create()
    try {
        $ecdsa.ImportFromPem($pem)
        $signature = $ecdsa.SignData(
            [Text.Encoding]::UTF8.GetBytes($unsigned),
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        return "$unsigned.$(ConvertTo-DevelopmentJwtBase64Url $signature)"
    } finally {
        $ecdsa.Dispose()
    }
}
