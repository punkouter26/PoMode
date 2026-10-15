#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates (or updates) PoMode's Microsoft sign-in app registration, with the delegated Graph
    permission OneDrive import needs, and stores its client secret in the shared Key Vault.

.DESCRIPTION
    Idempotent: an existing "PoMode Sign-in" registration is reused. Personal Microsoft accounts and
    any work or school tenant can sign in (PoAuth uses the "common" authority). The app asks for
    Files.Read only when someone chooses OneDrive (incremental consent), but the permission must be
    declared on the registration for that consent prompt to be allowed.

    Writes:
      - the registration's client id into src/PoMode.API/appsettings.json (PoMode:AzureAd:ClientId)
      - a client secret into Key Vault as PoMode--AzureAd--ClientSecret (never into a file)

    Needs: az CLI signed in with rights to create app registrations and set Key Vault secrets.

.PARAMETER RotateSecret
    Issue a new client secret even when the vault already holds one.
#>
param(
    [string]$DisplayName = 'PoMode Sign-in',
    [string]$VaultName = 'kv-poshared',
    [string]$WebAppHost = 'app-pomode.azurewebsites.net',
    [switch]$RotateSecret
)

$ErrorActionPreference = 'Stop'
$graph = '00000003-0000-0000-c000-000000000000'
$redirects = @(
    "https://$WebAppHost/auth/callback",
    'https://localhost:5001/auth/callback',
    'http://localhost:5000/auth/callback'
)

$appId = az ad app list --display-name $DisplayName --query '[0].appId' -o tsv
if (-not $appId) {
    Write-Host "Creating app registration '$DisplayName'"
    $appId = az ad app create --display-name $DisplayName `
        --sign-in-audience AzureADandPersonalMicrosoftAccount `
        --web-redirect-uris @redirects `
        --query appId -o tsv
} else {
    Write-Host "Reusing app registration '$DisplayName' ($appId)"
    az ad app update --id $appId --web-redirect-uris @redirects | Out-Null
}

# Delegated Graph scopes, looked up by name so no GUID is copied in by hand.
$scopes = az ad sp show --id $graph --query "oauth2PermissionScopes[?value=='openid' || value=='profile' || value=='offline_access' || value=='Files.Read'].id" -o tsv
$existing = az ad app permission list --id $appId --query "[?resourceAppId=='$graph'].resourceAccess[].id" -o tsv
foreach ($scope in $scopes) {
    if ($existing -notcontains $scope) {
        az ad app permission add --id $appId --api $graph --api-permissions "$scope=Scope" 2>$null | Out-Null
    }
}
Write-Host 'Delegated permissions: openid, profile, offline_access, Files.Read'

$hasSecret = az keyvault secret list --vault-name $VaultName --query "[?name=='PoMode--AzureAd--ClientSecret'].name" -o tsv
if ($RotateSecret -or -not $hasSecret) {
    $secret = az ad app credential reset --id $appId --display-name 'PoMode sign-in' --years 2 --append --query password -o tsv
    az keyvault secret set --vault-name $VaultName --name 'PoMode--AzureAd--ClientSecret' --value $secret --output none
    Write-Host "Client secret stored in $VaultName as PoMode--AzureAd--ClientSecret"
} else {
    Write-Host 'Client secret already in the vault; pass -RotateSecret to issue a new one'
}

# The client id is not a secret: it goes in appsettings, where PoAuth reads it.
$settingsPath = Join-Path $PSScriptRoot '..' 'src' 'PoMode.API' 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
if (-not $settings.ContainsKey('PoMode')) { $settings['PoMode'] = @{} }
if (-not $settings['PoMode'].ContainsKey('AzureAd')) { $settings['PoMode']['AzureAd'] = [ordered]@{} }
$settings['PoMode']['AzureAd']['ClientId'] = $appId
$settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8
Write-Host "ClientId $appId written to appsettings.json"
