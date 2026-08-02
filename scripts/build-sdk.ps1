#Requires -Version 7.0
<#
.SYNOPSIS
    Packs Orika.NET.Sdk into the local NuGet feed (./packages) and clears the
    global package cache entry so a re-pack of version 1.0.0 takes effect.

.NOTES
    Idempotent: safe to run repeatedly.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# The scripts live in scripts/, so the repository root is one level up.
$repoRoot = Split-Path -Parent $PSScriptRoot
$feedDir  = Join-Path $repoRoot 'packages'
$sdkProj  = Join-Path $repoRoot 'sdk/Orika.NET.Sdk/Orika.NET.Sdk.csproj'

# Ensure the local feed directory exists (nuget.config points at it, and
# restore fails if a configured local source is missing).
$null = New-Item -ItemType Directory -Path $feedDir -Force

Write-Host "Packing $sdkProj -> $feedDir" -ForegroundColor Cyan
dotnet pack $sdkProj -c $Configuration -o $feedDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE"
}

# NuGet caches by id/version; version stays 1.0.0, so evict the cached copy
# or consumers would keep restoring the stale package. The global-packages
# folder can be relocated (NUGET_PACKAGES / nuget.config), so ask NuGet for
# the real location instead of assuming %USERPROFILE%/.nuget/packages.
$globalPackages = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
$cacheCandidates = @(
    (Join-Path $globalPackages 'orika.net.sdk'),
    (Join-Path $env:USERPROFILE '.nuget/packages/orika.net.sdk')
) | Select-Object -Unique
foreach ($cacheEntry in $cacheCandidates) {
    if (Test-Path $cacheEntry) {
        Write-Host "Removing cached package: $cacheEntry" -ForegroundColor Cyan
        Remove-Item -Path $cacheEntry -Recurse -Force -Confirm:$false
    }
}

Write-Host 'Done. Orika.NET.Sdk 1.0.0 is available from the local feed.' -ForegroundColor Green

