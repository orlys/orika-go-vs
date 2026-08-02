# Releasing

Three artifacts ship together, and they do **not** all go to the same place.

| Artifact | What it is | Where it goes | Why |
|---|---|---|---|
| `OrikaGo.LanguageService.vsix` | The Visual Studio extension | **GitHub Release asset** | GitHub Packages has no VSIX registry — it only hosts npm, NuGet, Maven, Gradle, RubyGems and container images. A release asset is the standard distribution point for a VSIX outside the Marketplace. |
| `Orika.NET.Sdk.<version>.nupkg` | The MSBuild project SDK | **GitHub Packages (NuGet)** *and* release asset | This one genuinely is a NuGet package, and hosting it on a feed removes the "register a local folder feed first" step from the README. |
| `Orika.Go.Templates.<version>.nupkg` | `dotnet new` templates | **GitHub Packages (NuGet)** *and* release asset | Same reasoning. |

## Build

```powershell
./build-release.ps1          # -> ./dist, with SHA-256 for each file
```

The VSIX requires the Visual Studio extension-development workload, so this
must run on a machine with Visual Studio installed — it cannot be built by the
`dotnet` CLI alone, and a plain GitHub-hosted runner will not have the matching
VS version either.

## Publish

```powershell
# 1. Tag the commit being released
git tag v0.1.0
git push origin v0.1.0

# 2. Create the release and attach every artifact
gh release create v0.1.0 (Get-ChildItem ./dist/* | ForEach-Object FullName) `
    --title "v0.1.0" --notes-file release-notes.md

# 3. Push the NuGet packages to GitHub Packages
#    (a classic PAT with write:packages is required; GITHUB_TOKEN works in CI)
dotnet nuget add source "https://nuget.pkg.github.com/orlys/index.json" `
    --name github --username orlys --password $env:GITHUB_TOKEN --store-password-in-clear-text
dotnet nuget push "./dist/*.nupkg" --source github --api-key $env:GITHUB_TOKEN
```

## Consuming the published SDK

Once the packages are on GitHub Packages, a consumer no longer needs a local
folder feed — a user-level source is enough:

```powershell
dotnet nuget add source "https://nuget.pkg.github.com/orlys/index.json" `
    --name orika --username <github-user> --password <PAT-with-read:packages> `
    --store-password-in-clear-text --configfile $env:APPDATA\NuGet\NuGet.Config
```

Note that GitHub Packages requires authentication even for public packages —
this is a GitHub limitation, not a choice made here. If that friction matters,
publish to nuget.org instead; the packages carry no GitHub-specific metadata.

## Versioning

The version lives in three places and they are deliberately independent:

- `sdk/Orika.NET.Sdk/Orika.NET.Sdk.csproj` — the SDK package version, referenced by every `.goproj` as `Sdk="Orika.NET.Sdk/<version>"`, so bumping it is a breaking change for existing projects.
- `templates/Orika.Go.Templates.csproj` — the template package version.
- `vsix/OrikaGo.LanguageService/source.extension.vsixmanifest` — the extension version. **VSIXInstaller silently no-ops when the installed version matches**, which is why `install-vsix.ps1` uninstalls first rather than relying on a version bump.

The git tag names the release as a whole and does not have to match any of
them.
