#Requires -Version 7
<#
.SYNOPSIS
    Builds and installs the Orika Go language service extension into Visual Studio.

.DESCRIPTION
    VSIXInstaller silently does nothing when the installed extension carries the
    same Identity version as the package being installed - it reports success and
    leaves the old DLL in place, which looks exactly like a successful update.
    Uninstalling first makes the install unconditional, so the extension version
    never has to be bumped just to redeploy a rebuild.

    Visual Studio must be closed: VSIXInstaller exits 2004 (BlockingProcesses)
    while any devenv is running.
#>
[CmdletBinding()]
param(
    # Skip MSBuild and install whatever is already in bin/Release.
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'src/csharp/OrikaGo.LanguageService/OrikaGo.LanguageService.csproj'
$vsix = Join-Path $repoRoot 'src/csharp/OrikaGo.LanguageService/bin/Release/OrikaGo.LanguageService.vsix'
$extensionId = 'OrikaGo.LanguageService'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
# The extension-development workload is only needed to BUILD the VSIX (MSBuild
# with the VSSDK targets). -NoBuild just runs VSIXInstaller.exe, which ships
# with every VS edition, so requiring the workload there would block plain
# consumers of a prebuilt package.
if ($NoBuild) {
    $vsPath = & $vswhere -latest -products '*' -property installationPath
    if (-not $vsPath) { throw "No Visual Studio installation was found." }
} else {
    $vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Workload.VisualStudioExtension -property installationPath
    if (-not $vsPath) {
        throw "No Visual Studio installation with the extension development workload was found. To install a prebuilt VSIX without it, use -NoBuild."
    }
}

$devenv = Get-Process devenv -ErrorAction SilentlyContinue
if ($devenv) {
    throw "Close Visual Studio first ($($devenv.Count) instance(s) running); VSIXInstaller cannot write to a live installation."
}

if (-not $NoBuild) {
    $msbuild = Join-Path $vsPath 'MSBuild/Current/Bin/MSBuild.exe'
    & $msbuild $project /restore /p:Configuration=Release /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "VSIX build failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path $vsix)) { throw "Extension package not found at $vsix." }

$installer = Join-Path $vsPath 'Common7/IDE/VSIXInstaller.exe'

# Exit 1002 means "not installed", which is a fine starting state.
$uninstall = Start-Process $installer -ArgumentList '/quiet', "/uninstall:$extensionId" -Wait -PassThru
if ($uninstall.ExitCode -notin 0, 1002) {
    throw "Uninstall failed with exit code $($uninstall.ExitCode)."
}

$install = Start-Process $installer -ArgumentList '/quiet', "`"$vsix`"" -Wait -PassThru
if ($install.ExitCode -ne 0) { throw "Install failed with exit code $($install.ExitCode)." }

# Force the pkgdef/CTMENU caches to rebuild. Without this, a menu resource
# whose version number did not change is served from the stale cache - the
# exact failure mode that made a freshly fixed command table stay invisible.
$devenv = Join-Path $vsPath 'Common7/IDE/devenv.exe'
$update = Start-Process $devenv -ArgumentList '/updateconfiguration' -Wait -PassThru
if ($update.ExitCode -ne 0) { Write-Warning "devenv /updateconfiguration exited with $($update.ExitCode)." }

# Prove the deployed payload is the one just built, rather than trusting exit 0.
$builtDll = Join-Path $repoRoot 'src/csharp/OrikaGo.LanguageService/bin/Release/OrikaGo.LanguageService.dll'
$deployed = Get-ChildItem "$env:LOCALAPPDATA/Microsoft/VisualStudio/*/Extensions" -Recurse -Filter 'OrikaGo.LanguageService.dll' -ErrorAction SilentlyContinue
$builtHash = (Get-FileHash $builtDll).Hash
$match = $deployed | Where-Object { (Get-FileHash $_.FullName).Hash -eq $builtHash }

if (-not $match) {
    throw "Installer reported success but no deployed copy matches the freshly built DLL."
}

Write-Host "Installed $extensionId -> $($match[0].DirectoryName)"
Write-Host "Restart Visual Studio for the extension to load."

