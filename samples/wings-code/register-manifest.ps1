<#
.SYNOPSIS
  Registers a Wings Code application's notification manifest with Wings Notification Hub (Windows pipelines).

.EXAMPLE
  $env:WNH_URL = 'https://notifications.wings.example'
  $env:WINGS_CODE_PROVISIONING_CLIENT_ID = '...'; $env:WINGS_CODE_PROVISIONING_CLIENT_SECRET = '...'
  ./register-manifest.ps1 -Manifest notification-manifest.json -SecretsOut .wnh-secrets.json

.NOTES
  One-time secrets returned by the hub (new client secret, webhook signing secrets) are written only to -SecretsOut
  (ACL restricted to the current user); they are never printed. PowerShell 7+.
#>
param(
    [string] $Manifest = 'notification-manifest.json',
    [string] $SecretsOut,
    [string] $HubUrl = $(if ($env:WNH_URL) { $env:WNH_URL } else { 'http://localhost:5480' })
)
$ErrorActionPreference = 'Stop'
if (-not $env:WINGS_CODE_PROVISIONING_CLIENT_ID -or -not $env:WINGS_CODE_PROVISIONING_CLIENT_SECRET) {
    throw 'Set WINGS_CODE_PROVISIONING_CLIENT_ID and WINGS_CODE_PROVISIONING_CLIENT_SECRET.'
}
$HubUrl = $HubUrl.TrimEnd('/')

$token = Invoke-RestMethod -Method Post -Uri "$HubUrl/connect/token" -ContentType 'application/x-www-form-urlencoded' -Body @{
    grant_type = 'client_credentials'; client_id = $env:WINGS_CODE_PROVISIONING_CLIENT_ID
    client_secret = $env:WINGS_CODE_PROVISIONING_CLIENT_SECRET; scope = 'applications.register'
}

$doc = Get-Content -Raw -Path $Manifest | ConvertFrom-Json -AsHashtable
$doc.Remove('$schema') | Out-Null
$body = $doc | ConvertTo-Json -Depth 20

try {
    $result = Invoke-RestMethod -Method Post -Uri "$HubUrl/api/v1/registrations/manifest" -ContentType 'application/json' `
        -Headers @{ Authorization = "Bearer $($token.access_token)" } -Body $body -MaximumRetryCount 3 -RetryIntervalSec 2
}
catch {
    $detail = $_.ErrorDetails.Message
    throw "Registration failed: $($_.Exception.Message) $detail"
}

Write-Host "Application $($result.applicationId) $(if ($result.created) { 'created' } else { 'updated' })"
Write-Host "  notification types upserted: $($result.notificationTypesUpserted)"
Write-Host "  templates registered:        $($result.templatesRegistered)"
Write-Host "  webhooks registered:         $($result.webhooksRegistered)"
if ($result.clientId) { Write-Host "  new credential client id:    $($result.clientId)" }
foreach ($w in $result.warnings) { Write-Warning $w }

$hasSecrets = $result.clientSecret -or ($result.webhookSecrets.PSObject.Properties.Count -gt 0)
if ($hasSecrets -and $SecretsOut) {
    [ordered]@{ applicationId = $result.applicationId; clientId = $result.clientId; clientSecret = $result.clientSecret; webhookSecrets = $result.webhookSecrets } |
        ConvertTo-Json -Depth 5 | Set-Content -Path $SecretsOut -Encoding utf8
    if ($IsWindows) { icacls $SecretsOut /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null } else { chmod 600 $SecretsOut }
    Write-Host "  secrets written to $SecretsOut (move them to your secret store; the hub shows them only once)"
}
elseif ($hasSecrets) {
    Write-Warning 'The hub returned one-time secrets but -SecretsOut was not given; they were discarded.'
}
