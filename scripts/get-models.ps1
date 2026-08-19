#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Downloads every ONNX model and browser-runtime asset PoMode needs into the git-ignored
    models/ cache, verifying each file's SHA-256 against the catalog.

.DESCRIPTION
    The app already downloads these on demand (ModelWarmupService on startup, WebRuntimeEndpoints
    on first Tier 2 request). This script exists so a fresh clone can pre-fetch them in one step —
    and so the E2EUI suite's repo-root models/ cache is warm before its first run.

    URLs and hashes are parsed straight out of src/PoMode.API/Infrastructure/ModelCatalog.cs, which
    stays the single source of truth. Nothing is duplicated here, so the script cannot drift from it.

.PARAMETER Destination
    Where to put the files. Defaults to the repo-root models/ folder — the same cache
    ClientDelegatedFlowTests seeds from.

.PARAMETER Force
    Re-download files that are already present and already hash-correct.

.PARAMETER Retries
    How many times to attempt each download. The 165 MB HTDemucs model is served by Hugging Face and
    does drop connections mid-transfer, so this defaults to 4 rather than 1.

.EXAMPLE
    pwsh scripts/get-models.ps1
#>
[CmdletBinding()]
param(
    [string] $Destination,
    [switch] $Force,
    [int] $Retries = 4
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $repoRoot 'models' }

$catalogPath = Join-Path $repoRoot 'src/PoMode.API/Infrastructure/ModelCatalog.cs'
if (-not (Test-Path $catalogPath)) {
    throw "Cannot find the model catalog at $catalogPath. Run this script from inside the PoMode repo."
}

# Matches each `new(Key: "...", FileName: "...", Url: "...", Sha256: "...")` descriptor literal.
# If ModelCatalog.cs ever changes that shape, this regex is the one thing to update.
$pattern = 'new\(\s*Key:\s*"(?<key>[^"]+)",\s*FileName:\s*"(?<file>[^"]+)",\s*Url:\s*"(?<url>[^"]+)",\s*Sha256:\s*"(?<sha>[^"]*)"\s*\)'
$catalogText = Get-Content -Raw $catalogPath
$matched = [regex]::Matches($catalogText, $pattern, 'Singleline')

if ($matched.Count -eq 0) {
    throw "Parsed no model descriptors out of $catalogPath. The catalog's shape probably changed; update the regex in this script."
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Write-Host "Model cache: $Destination"
Write-Host "Catalog:     $($matched.Count) assets`n"

function Get-FileSha256([string] $Path) {
    (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
}

$downloaded = 0
$skipped = 0
$failed = @()

foreach ($m in $matched) {
    $key  = $m.Groups['key'].Value
    $file = $m.Groups['file'].Value
    $url  = $m.Groups['url'].Value
    $sha  = $m.Groups['sha'].Value.ToLowerInvariant()

    $final = Join-Path $Destination $file
    $part = "$final.part"

    if (-not $sha) {
        Write-Warning "$key has no SHA-256 in the catalog; refusing to download an unverified asset."
        continue
    }

    if ((Test-Path $final) -and -not $Force) {
        if ((Get-FileSha256 $final) -eq $sha) {
            Write-Host "[ok]   $file (cached)"
            # A .part alongside a verified final file is debris from a run that was killed mid-transfer
            # (for HTDemucs that is ~150 MB of dead weight). Nothing will ever resume it, so drop it.
            if (Test-Path $part) { Remove-Item -Force $part }
            $skipped++
            continue
        }
        Write-Warning "$file is present but its hash does not match the catalog. Re-downloading."
    }

    Write-Host "[get]  $file  <-  $url"

    # One asset failing must not abandon the rest: a dropped transfer on the 165 MB model should not
    # cost you the six small browser-runtime files too. Failures are collected and re-reported at the
    # end, and the script exits non-zero so CI still notices.
    $ok = $false
    for ($attempt = 1; $attempt -le $Retries -and -not $ok; $attempt++) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $part -MaximumRedirection 5

            $actual = Get-FileSha256 $part
            if ($actual -ne $sha) {
                throw "SHA-256 mismatch: expected $sha, got $actual."
            }

            Move-Item -Path $part -Destination $final -Force
            $size = '{0:N1} MB' -f ((Get-Item $final).Length / 1MB)
            Write-Host "[ok]   $file ($size, hash verified)"
            $downloaded++
            $ok = $true
        }
        catch {
            if (Test-Path $part) { Remove-Item -Force $part }
            if ($attempt -lt $Retries) {
                $backoff = [Math]::Pow(2, $attempt)
                Write-Warning "$key attempt $attempt/$Retries failed: $($_.Exception.Message) Retrying in $backoff s..."
                Start-Sleep -Seconds $backoff
            }
            else {
                Write-Warning "$key failed after $Retries attempts: $($_.Exception.Message)"
                $failed += $key
            }
        }
    }
}

Write-Host "`nDone. $downloaded downloaded, $skipped already cached."

if ($failed.Count -gt 0) {
    Write-Host "Failed: $($failed -join ', '). Re-run this script to resume — verified files are kept."
}

Write-Host "To make the app read this folder instead of its bin/ copy:"
Write-Host "  `$env:Models__RootPath = '$Destination'"

if ($failed.Count -gt 0) { exit 1 }
