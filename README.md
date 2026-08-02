# epic — Orika.NET.Sdk Sample Project

> [!WARNING]
> **Experimental project.** This is a personal exploration of how far Visual Studio can be pushed into hosting a language it was never built for. It is not affiliated with, endorsed by, or supported by Microsoft or the Go team.
>
> Expect breakage: it leans on Visual Studio internals that carry no compatibility promise — undocumented AD7 metrics, pkgdef registration details, CPS extension points, and behaviour observed by decompiling in-box components. A Visual Studio update can break any of it without notice. Nothing here has been through the kind of testing a production tool deserves.
>
> Use it if the idea interests you and you are comfortable diagnosing an IDE that misbehaves. Do not put it in front of a team that just needs to ship.

[繁體中文說明](README.zh-TW.md)

![Visual Studio editing a Go project: a .goproj declaring GoModuleReference for github.com/oklog/ulid/v2, main.go with Go syntax colouring, a breakpoint and gopls reporting no issues, the Dependencies context menu offering only "Add Go Module Reference..." and "Tidy Go Modules", and the "Orika Go" output pane showing completed go mod tidy and go generate runs.](img/1.png)

Everything in that screenshot is this project: the `.goproj` declares its dependency the way a `.csproj` would, gopls drives colouring and diagnostics, breakpoints bind through delve, the Dependencies node carries Go commands instead of NuGet ones, and `go mod tidy` / `go generate` report into a dedicated output pane.

> Developers should read [`docs/pitfalls.md`](docs/pitfalls.md) first: a record of the actual pitfalls hit with VS extensions, CPS capabilities, and delve debugging (symptom / root cause / fix / how to diagnose). Almost all of them are silent failures with no error message. The debugging feature roadmap is in [`docs/debug-parity-plan.md`](docs/debug-parity-plan.md).

This project demonstrates how to use **Orika.NET.Sdk** (a custom MSBuild project SDK) to build, run, test, and publish **Go** programs with the .NET CLI toolchain.

## What Is Orika.NET.Sdk?

Orika.NET.Sdk is an **MSBuild project SDK** distributed as a NuGet package (`PackageType=MSBuildSdk`). It lets a `.goproj` project file be written directly as:

```xml
<Project Sdk="Orika.NET.Sdk/1.0.0">

  <PropertyGroup>
    <LangVersion>1.13</LangVersion>
  </PropertyGroup>

</Project>
```

Internally the SDK imports `Microsoft.NET.Sdk` (so Visual Studio can load the project and the `dotnet` CLI works), while disabling the C# compiler and its related outputs; the real compilation is done by the Go toolchain (`go build` / `go test` / `go vet`). The resulting executable is placed in `bin/$(Configuration)/` (for example `bin/Debug/epic.exe`). `TargetFramework` exists only to satisfy `Microsoft.NET.Sdk` and is meaningless for a Go binary, so the SDK sets `AppendTargetFrameworkToOutputPath=false` to remove it from the path.

## Supported Properties

| Property | Description |
|------|------|
| `LangVersion` | Maps to the Go language version. The SDK runs `go mod edit -go=<version>`, and the Go toolchain gates language features via the `go` directive in `go.mod`. For example, after setting `1.13`, using generics produces the compile error "requires go1.18 or later" — that is real semantics, not a simulation. |
| `OutputType` | `Exe` (default): `go build -o` produces an executable; `Library`: `go build ./...` only performs a compile check and produces no executable. |
| `DefineConstants` | A semicolon-separated list that is converted into Go build tags: `a;b` → `go build -tags "a,b"`. |
| `GoFlags` | Extra flags appended verbatim to the `go build` command line. |
| `GoOs` / `GoArch` | Override the `GOOS` / `GOARCH` environment variables to specify a cross-compilation target manually. |
| `RunGoVet` | When set to `true`, runs `go vet` automatically after the build completes. |
| `GoEnsureWorkspaceMembership` | Defaults to `true`; set it to `false` to keep this project from being added to `go.work` automatically (see "Automatic go.work Workspace Membership Registration" below). |

### Module References (GoModuleReference)

`GoModuleReference` is the Go world's `PackageReference` — declare a dependency module in the `.goproj`, and at build time the SDK resolves it with `go get` and writes it into `go.mod` / `go.sum` (including transitive dependencies):

```xml
<ItemGroup>
  <GoModuleReference Include="rsc.io/quote" Version="v1.5.2" />
  <GoModuleReference Include="golang.org/x/text" /> <!-- Omitting Version = latest -->
</ItemGroup>
```

The idempotency rule is the same as for `LangVersion`: when `go.mod` already contains the module (and the version matches), `go get` is not executed at all, the mtime of `go.mod` is unchanged, and incremental builds are not broken. Changing `Version` triggers re-resolution; **removing** a reference does not remove the require from `go.mod` — that is `go mod tidy`'s job.

You do not have to write it by hand either: in Solution Explorer, **right-click the "Dependencies" node → "Add Go Module Reference..."**, then enter the module path and version (leave it blank for latest). The command is provided by the VSIX's `OrikaGoPackage`, is localized (English by default, with zh-TW and zh-CN string sets that follow the VS display language), and appears only on projects with the `OrikaGo` capability (`.goproj`); if a reference to the same module already exists, its `Version` is updated in place. After the write, CPS reloads the project automatically thanks to `HandlesOwnReload`, and the next build resolves it via `GoRestoreModules` using `go get`. Technical note: the context menu of the Dependencies node is actually the shell's `IDM_VS_CTXT_REFERENCEROOT` (the managed project system's `DependenciesContextMenuProvider` maps the tree node onto it), so the vsct can simply parent onto it; VSCT localization uses multiple `<Strings language="…">` blocks under the same Button.

The .NET-related child nodes under the Dependencies node (assembly / COM / WinRT references) are hidden as well — the SDK removes the `AssemblyReferences` / `COMReferences` / `WinRTReferences` capabilities; `ProjectReferences` is kept (project references between `.goproj` files are supported).

**Go tool commands** (provided by the VSIX, appearing only on `.goproj`, localized):

| Menu item | Location | Actually runs | Description |
|------|------|------|------|
| Add Go Module Reference... | Dependencies node right-click | Writes a `GoModuleReference` | Resolved by `go get` on the next build |
| Tidy Go Modules | Dependencies node right-click | `go mod tidy` | Removes unused modules from go.mod and adds missing ones. This is the dependency-layer counterpart of Code Cleanup — source-level tidying (gofmt / organizing imports) is gopls's job |
| Run Go Generators | Project node right-click | `go generate ./...` | Runs `//go:generate` directives. A Go build does **not** run them automatically, so this is the only entry point inside the IDE |
| Run Go Vet | Project node right-click | `go vet ./...` | Can be run on its own without rebuilding (the build-time equivalent is `-p:RunGoVet=true`) |

Output is written to the "Orika Go" output pane; failures additionally raise a dialog.

Dependencies of Go projects always go through go.mod — **NuGet is completely invisible to `.goproj`**:

- "Manage NuGet Packages" does not appear in the project context menu: the SDK removes the `PackageReferences` / `AssemblyReferences` capabilities (so NuGet decides the project is unsupported), and the VSIX additionally marks the command invisible via CPS's `IAsyncCommandGroupHandler` (`GoHiddenNuGetCommandsHandler`, `AppliesTo("OrikaGo")`). **Both are required** — NuGet's visibility only looks at "is a solution open" and is independent of project type, so the capability alone would leave the command on the menu and clicking it would report "the project does not support this";
- **No restore is needed**: `SkipResolvePackageAssets=true` makes the build not require `obj/project.assets.json` at all (VS also does not run NuGet restore for .goproj);
- **No per-project nuget.config is needed**: NuGet's only remaining purpose is for MSBuild to resolve the SDK package `Sdk="Orika.NET.Sdk/1.0.0"` itself — registering the local feed once at the user level is enough (`dotnet nuget add source <repo>\packages --name orika-local --configfile %APPDATA%\NuGet\NuGet.Config`), or publish the nupkg to your own NuGet server. Verified: after clearing the global cache, a project with no nuget.config at all still resolves the SDK and builds normally.

`Configuration` also affects the compile flags:

- **Debug**: `-gcflags "all=-N -l"` (disables optimization and inlining, which helps debugging).
- **Release**: `-trimpath -ldflags "-s -w"` (strips path information and the symbol table, shrinking the executable).

## Common Commands

```powershell
# Build (actually runs go build)
dotnet build

# Build and run the produced executable
dotnet run

# Run go test ./... (a test failure fails the command)
dotnet test

# Cross-compile and publish (RID maps to GOOS/GOARCH)
dotnet publish -r linux-arm64
dotnet publish -r win-x64
dotnet publish -r osx-arm64

# Clean build output
dotnet clean
```

RID mapping table: `win-x64`→`windows/amd64`, `win-arm64`→`windows/arm64`, `linux-x64`→`linux/amd64`, `linux-arm64`→`linux/arm64`, `osx-x64`→`darwin/amd64`, `osx-arm64`→`darwin/arm64`. When no RID is specified the host platform is used; only output with `GOOS=windows` gets the `.exe` extension.

## Routing Go Diagnostics into the Visual Studio Error List (GoExec)

Errors reported by `go build` / `go vet` / `go test` look like `main.go:6:14: undefined: x`, whereas MSBuild only recognizes the standard format `file(line,col): error code: message`. The SDK's previous six `<Exec>` invocations performed no conversion at all, so **the entire build produced a single `MSB3073`** — and it pointed at `Sdk.targets` inside the NuGet cache. Double-clicking the build error in the Error List took the developer to `D:\.nuget\packages\…` instead of their own source. When gopls is not running, this is the only surviving error-reporting path, and it was completely unusable.

**`Exec`'s `CustomErrorRegularExpression` cannot solve this**: it only decides which lines get forwarded to `Log.LogError(string)`, and that single-parameter overload carries no file / line / column information, so MSBuild can only attribute the error to **the location of the executing task** — that is, `Sdk.targets` itself. The only way to fill in the standard fields is the 10-parameter overload `Log.LogError(subcategory, code, helpKeyword, file, line, col, endLine, endCol, message)`, and that requires a real Task.

Hence the addition of `sdk/Orika.NET.Sdk/Sdk/GoDiagnostics.targets`, which defines `<GoExec>` as a **`RoslynCodeTaskFactory` inline task** (supported both by `dotnet build`'s MSBuild Core and by Visual Studio 18's MSBuild.exe; the SDK package still contains only MSBuild logic and requires no compiling, signing, or packaging of any assembly). All six `<Exec>` sites in `Sdk.targets` — `GoBuild` (Exe / Library), `GoVet`, `VSTest`, and `Publish` (Exe / Library) — now use `<GoExec>`, each carrying `ErrorCode="GOBUILD"` / `"GOVET"` / `"GOTEST"`.

`GoExec` parses toolchain output line by line and handles all of the following cases:

| Output shape | Handling |
|----------|----------|
| `.\main.go:6:14: undefined: x` | Strip the `.\` / `./` prefix, resolve to an absolute path using `WorkingDirectory`, and emit `main.go(6,14): error GOBUILD: undefined: x` |
| `sub\a.go:11:23: …` | Relative paths in subpackages are resolved correctly as well |
| `# example.com/m/sub` | Package header: emitted as a message, not an error |
| Continuation lines starting with a **Tab** (`have Do(string) error` / `want Do(int) error`) | Merged into the preceding diagnostic (joined with `; `). MSBuild repeats the `file(line,col): error code:` prefix for **every line** of a multi-line error message, so one diagnostic would look like three |
| `vet: ` / `vet.exe: ` prefixes | Stripped before matching |
| `    a_test.go:8: B() = 1, want 2` | The `testing` package keeps only the file name (`file[lastIndexByte(file,'/')+1:]`), so parsing it directly would point at a non-existent file in the root directory; when parsing fails, the location is filled in from an **index of source-file base names** under the working directory, and only a unique match is accepted |
| Any other line | `Log.LogMessage(High)`, preserving the readability of `go test` output |

Two key design points:

1. **The output of `t.Logf` and `t.Errorf` is identical**, so they cannot all be treated as errors — otherwise passing tests would also fail the build. `go test` (non-verbose) prints `--- FAIL: TestX` first and then that test's log lines, whereas `go test -v` **logs first and reports the verdict afterwards**. So while the verdict is unknown, the diagnostic is held pending, and the subsequent `--- FAIL` (promotes it to an error) or `--- PASS` / `--- SKIP` (keeps it a message) decides.
2. **Only when there are no structured diagnostics at all** is a generic "command exited with a non-zero exit code" error added. Otherwise that generic message would bury the real Go diagnostics just as the old `MSB3073` did.

Verification (tested with `dotnet build`, `dotnet test`, VS 18's `MSBuild.exe`, and `devenv /Rebuild`): putting `undefinedThing()` on line 6 of `main.go` produces this build output:

```
<project path>\main.go(6,14): error GOBUILD: undefined: undefinedThing
Build FAILED.
    1 Error(s)
```

No `MSB3073`, and the path is the absolute path of the user's own source.

> Known limitation: `t.Logf` log lines inside a failing test are still promoted to errors along with it (Go's output gives no way to tell them apart); they are context for that failure. In the verbose output of `go test -parallel`, log lines from multiple tests interleave, but attribution is no longer guesswork — see "Correctness Fixes in the MSBuild SDK" below. **"Double-click to jump to the line" in the Error List is Visual Studio UI behavior and can only be confirmed visually inside the IDE**; everything described here is build output reproducible in a headless environment.

## Automatic go.work Workspace Membership Registration

There is a `go.work` at the repository root. **`GOWORK` is inherited by all subdirectories**, so as soon as you add a module anywhere under this directory tree it is "inside the workspace" — but it is not a **member** of the workspace until it is listed in `go.work`'s `use` block, and the Go toolchain simply refuses to build:

```
main module (epic) does not contain package epic/MyTool
```

This is exactly what the "add new project" flow produces: the project created by the template passes `dotnet new` but cannot be built. The SDK therefore adds a `GoEnsureWorkspace` target (`Sdk.targets`) that runs after `GoEnsureMod` and before `GoBuild` / `VSTest` / `Publish`:

1. First make a pre-check that starts no process — if the `GOWORK` environment variable is already set, use it (`GOWORK` can only come from the environment; `go env -w GOWORK=…` answers `go: GOWORK cannot be modified`), otherwise search upward from the project directory for `go.work`. If neither is found there is no workspace, so skip immediately, **paying no process-startup cost at all**.
2. Only when a workspace really exists is `go env GOWORK` used to obtain the authoritative value, and `[MSBuild]::MakeRelative` used to compute the project's path relative to `go.work`.
3. **`go work use` is executed only when the module is genuinely absent from the `use` list.** This idempotency guard matches what `GoEnsureMod` does for `go mod edit`: otherwise `go.work`'s modification time would be updated on every build, breaking incremental builds. The comparison accepts both the `use (…)` block form and the single-line `use <path>` form, an optional `./` prefix, the quoted form for paths containing spaces, and absolute-path spellings; the path is put through `Regex.Escape` first so that the `.` in names like `v1.2` is not treated as a wildcard.
4. What is passed to `go work use` is a **relative path**. `go work use` records the argument you give it verbatim, so passing an absolute path would write a machine-specific, backslash-separated entry into a file that is usually checked into version control; running it from the directory containing `go.work` with a relative path writes the `./<path>` form that go itself uses.

This target does not run during design-time builds (`DesignTimeBuild=true`) — Visual Studio should not rewrite `go.work` while loading a project. If a module is **deliberately** excluded from the workspace, set this in its `.goproj`:

```xml
<GoEnsureWorkspaceMembership>false</GoEnsureWorkspaceMembership>
```

## Rebuilding the SDK

The SDK sources live in `sdk/Orika.NET.Sdk/`. After modifying them, run:

```powershell
.\build-sdk.ps1
```

This script:

1. runs `dotnet pack` to package the SDK into a `.nupkg` in the local feed `./packages/`;
2. deletes the old version from the NuGet global cache (`%USERPROFILE%\.nuget\packages\orika.net.sdk`) so that the repackaged content takes effect immediately.

`nuget.config` already registers `./packages` as a local source (plus nuget.org), and `.goproj` references the version inline via `Sdk="Orika.NET.Sdk/1.0.0"`, so no `global.json` is needed.

## Honest Limitations (Non-Goals)

- **There is no Go IntelliSense in Visual Studio, and you cannot set breakpoints in Go code and debug it.** That would require writing a VSIX language service, which is out of scope for this SDK.
- What Visual Studio can do is: load `.goproj` projects, show `.go` files and `go.mod` in Solution Explorer, run real build / clean / launch, and obtain gopls-provided IntelliSense through the VSIX.
- For writing Go code, an editor with a language service such as VS Code + the Go extension is recommended.

## Project Templates (dotnet new)

`templates/` provides the **Orika.Go.Templates** template package, containing two templates:

| Short name | Template | Description |
|--------|------|------|
| `go-console` | Orika Go console application | `.goproj` + `go.mod` + `main.go`; produces an executable after building |
| `go-lib` | Orika Go class library | `OutputType=Library`; `go build ./...` only performs a compile check; the Go package name is the lowercased project name |

Following Go convention (module paths and package names are all lowercase), the `go.mod` module name produced by both templates and the package name of `go-lib` are the **lowercased form of the project name** (`MyApp` → `module myapp`, `MyLib` → `package mylib`); on the SDK side, the default name for `GoEnsureMod`'s `go mod init` is likewise lowercased first and then sanitized (`MyCased App` → `module mycased_app`). The `.goproj` file name and `AssemblyName` (the name of the output executable) keep the casing the user entered.

Packaging and installation (from the repository root):

```powershell
dotnet pack templates/Orika.Go.Templates.csproj -c Release -o packages
dotnet new install Orika.Go.Templates::1.0.0
```

Usage:

```powershell
# Create a console project (--langVersion maps to go.mod's go directive and <LangVersion>; default 1.21)
dotnet new go-console -n MyTool -o MyTool --langVersion 1.22
dotnet build MyTool/MyTool.goproj
dotnet run --project MyTool/MyTool.goproj      # => Hello, World!

# Create a class library (package name = lowercased project name, e.g. MyLib => package mylib)
dotnet new go-lib -n MyLib -o MyLib
```

Notes:

- The generated project references the SDK via `Sdk="Orika.NET.Sdk/1.0.0"`, so **the project's location must be able to find the `./packages` local feed through `nuget.config`** (creating the project under this repository is sufficient; elsewhere, place a `nuget.config` pointing at that feed next to the project).
- If the lowercased project name is not a valid Go identifier (contains hyphens or spaces, or starts with a digit), the package / module name generated by `go-lib` will be invalid; the template engine does not sanitize it for you.
- Before reinstalling after modifying the templates, run `dotnet new uninstall Orika.Go.Templates` first or bump `PackageVersion`.

## Compiler Platform API (Orika.Go.CodeAnalysis)

`compiler/` provides a Roslyn-shaped Go compiler platform:

- **`compiler/orika-goc/`** — the Go sidecar CLI, itself a `.goproj` (dogfooding Orika.NET.Sdk). It implements three command families — `parse` / `check` / `symbol` — on top of `go/parser` and `go/types`, all emitting JSON; it returns exit code 0 even when the source has errors (only infrastructure errors return non-zero).
- **`compiler/Orika.Go.CodeAnalysis/`** — a net10.0 class library that uses the sidecar to provide `GoSyntaxTree` (syntax trees), `GoCompilation` (diagnostics and `Emit`, which actually runs `go build -o`), and `GoSemanticModel` (semantic queries).
- **`compiler/Orika.Go.CodeAnalysis.Tests/`** — xUnit tests (`dotnet test`; all 23 pass).

The sidecar lookup order is: explicit path argument > the `ORIKA_GOC` environment variable > next to the `Orika.Go.CodeAnalysis` assembly > `PATH`. Building the sidecar and setting the environment variable is enough:

```powershell
dotnet build compiler/orika-goc/orika-goc.goproj
$env:ORIKA_GOC = "$PWD\compiler\orika-goc\bin\Debug\orika-goc.exe"
dotnet test compiler/Orika.Go.CodeAnalysis.Tests
```

A short example:

```csharp
using Orika.Go.CodeAnalysis;

// Syntax tree: parse a source string (GoSyntaxTree.ParseFile(path) also works)
var tree = GoSyntaxTree.ParseText("package main\n\nfunc main() {\n\tprintln(1)\n}\n");
var funcDecl = tree.Root!.FirstChild("FuncDecl");
Console.WriteLine(funcDecl!.FirstChild("Ident")!.Text);        // main

// Compilation: diagnostics, semantic queries, and a real build of a whole Go module
var compilation = GoCompilation.Create(@"D:\path\to\module");
foreach (var d in compilation.GetDiagnostics())                 // GOPARSE / GOTYPE
    Console.WriteLine(d);

var model = compilation.GetSemanticModel();
var symbol = model.GetSymbolAt(@"D:\path\to\module\main.go", 4, 2);
Console.WriteLine(symbol);                                      // e.g. "var x: int"

// Emit = a real go build -o (cross-compilation supported)
var result = compilation.Emit(@"D:\out\tool-linux",
    new GoEmitOptions { OS = "linux", Arch = "arm64", TrimPath = true });
Console.WriteLine(result.Success);
```

Diagnostic codes: `GOPARSE` (syntax errors), `GOTYPE` (type-check errors), `GOBUILD` (`go build` failure). All positions are 1-based line / column; **the unit of the column number is UTF-16 code units** (see "Correctness Fixes in the Compiler Platform" below).

## F5 Debugging (delve Integration)

Pressing **F5** on a `.goproj` project debugs it with [delve](https://github.com/go-delve/delve): breakpoints, stepping (F10/F11), locals, call stacks, and goroutines are all supplied over DAP. **Ctrl+F5** (Start Without Debugging) runs the build output directly, bypassing delve.

Architecture (the same mechanism VS's built-in CMake debugging uses):

- **Launch side**: on F5, `GoDebugLaunchProvider` starts `dlv dap --listen=127.0.0.1:0`, parses the port it reports, and assembles the launch configuration (`mode:"exec"`, program=`GoOutputPath`, args=`StartArguments` split into an array, cwd=the project directory). **It exports two interfaces at once**: the managed project system's `LaunchProfiles` subsystem owns the underlying machinery for F5 (removing that capability makes `Debug.Start` disappear entirely — measured), and its pipeline only consults `IDebugProfileLaunchTargetsProvider` (selected by `[Order]`, gated by `SupportsProfile`); a plain `IDebugLaunchProvider` is never asked — so the provider implements both, and the former is the path actually taken. There is no usable NuGet package for that interface, so `Microsoft.VisualStudio.ProjectSystem.Managed.VS.dll` is referenced directly from the VS install directory (`Private=false`).
- **Engine side**: `goproj.pkgdef` registers the Go engine under `AD7Metrics\Engine`, with `CLSID` pointing at VS's **Debug Adapter Host** fixed implementation; `$debugServer` in the launch configuration makes the host connect directly to dlv's TCP port — **`"Adapter"` is deliberately not set**, because `dlv dap` supports only TCP, not stdio, and having the host spawn it would deadlock during the handshake.
- **Debug information**: the Debug configuration is already compiled with `-gcflags "all=-N -l"` (see "Supported Properties"), so symbols and locals are complete and no SDK-side change is needed.
- **dlv probing**: the same `GoToolLocator` used for gopls (PATH → GOBIN/GOPATH\bin including persisted `go env -w` values → `%USERPROFILE%\go\bin`); when it is not found, the error message gives `go install github.com/go-delve/delve/cmd/dlv@latest`.
- **Lifecycle**: dlv dap is a single-session server and exits automatically when the session ends; a server left behind by a failed connection is reclaimed before the next F5.
- **Console**: dlv is started with a **visible console** (the debuggee inherits it, so `fmt.Println` / `fmt.Scan` happen in that window), so the port is pre-selected by the launch provider (bind :0, then release), and readiness is awaited by polling the OS listener table — a TCP test connection cannot be used, because `dlv dap` accepts only a single client and a probe connection would consume the session. The console closes together with dlv when the session ends.

**The P0 quick-win batch is implemented** (detailed plan in `docs/debug-parity-plan.md`):

- **Exception settings**: `Exceptions=1` plus a `Go Exceptions` category registration (entry names aligned with dlv's filter labels: `Unrecovered Panics` / `Fatal Throws`, taken from measured DAP initialize values); **an unrecovered panic stops the debugger at the panic site** (measured: output stops one step before the panic, VS enters break mode)
- **Conditional breakpoints**: `ConditionalBP=1`; protocol logs demonstrate `"condition"` reaching dlv and being hit
- **Function breakpoints**: `FunctionBP=1`; the `setFunctionBreakpoints` channel is demonstrated to work
- **Hit count breakpoints**: `HitCountBP=1` plus `HitCountBreakpointExpressions` (`== {0}` / `>= {0}` / `% {0}` map onto dlv's hitCondition)
- **Disassembly / call stack breakpoints**: `AddressBP=1` / `CallStackBP=1` (dlv's `supportsInstructionBreakpoints`)
- **goroutine noise reduction**: `hideSystemGoroutines:true` in the launch configuration
- **delve version**: upgraded to 1.27.0 (unlocking exceptionBreakpointFilters, the hitCondition capability, and memory read/write); dlv is started with `--check-go-version=false` — delve only "supports" the two most recent Go versions, otherwise binaries built with an older toolchain are hard-rejected (modal error)
- **Go toolchain**: switched to 1.25 via `go env -w GOTOOLCHAIN=go1.25.12+auto` (the official mechanism, no reinstall required; once the binary is built by 1.25, delve's version WARNING disappears). Knock-on work: `orika-goc`'s `x/tools` upgraded to v0.48.0 (v0.24 fails to compile under go1.25 — the internal layout of token changed), gopls upgraded to a newer release (0.14.2 does not match 1.25), and **the toolchain version is now an incremental-build input** (`go version` is written into `go.build.args`; otherwise, after switching GOTOOLCHAIN, a binary built by the old toolchain would be silently reused). The compiler tests pass 26/26 under 1.25

**The Disassembly window** works (dlv's `disassemble` returns real Go assembly, and `AddressBP=1` allows setting breakpoints in it).

**Memory reads and `DelveProxy`**: on **every break**, VS sends a routine `readMemory` probe (`count=0`) using the current instruction pointer address, but delve's `readMemory` only accepts references **it issued itself** (`referencesCollection`, and `isAddressable()` covers only strings and slices), so a raw address is always rejected and the user sees `Unable to read memory: unknown memoryReference` every single time a breakpoint is hit. The engine metric `MemoryReferencesAreAddresses=0` does not stop this probe (measured).

So the SDK inserts an extremely thin DAP relay (`DelveProxy`) between DAH and dlv: it forwards bytes in both directions, and its **only** modification is to rewrite "a `readMemory` failure whose message is unknown memoryReference" into the successful empty response delve itself returns for a legitimate zero-length read. Measured: the probe returns `success:true`, and the session's ERROR count drops from 5 to 0. Real memory reads (right-click a string / slice variable → view memory, using the reference delve itself provided) are still passed through to delve untouched; manually entering an arbitrary address quietly yields an empty result rather than an error (delve does not support it — see `docs/debug-parity-plan.md`).

**Attach to Process** works: Debug → Attach to Process → select a Go program, then choose "Go Debugger (Delve)" as the code type. `GoProgramProvider` scans the target executable for the Go build-info marker, so this option is offered only for genuine Go processes. After detaching, the target program keeps running.

Known limitations: `SetNextStatement` (dragging the yellow arrow) is turned off — no version of delve implements DAP's `goto`; see the dedicated section in the planning document. `ExceptionConditions` (skipping exceptions by module) is likewise off because delve does not support it.

End-to-end verification (DTE automation, corroborated by DAP protocol logs): set a breakpoint in `main.go` → F5 → dlv starts, `setBreakpoints` succeeds, and `stopped(reason=breakpoint)` actually fires; locals (including Go-native types such as `chan string 2/3`), the call stack (`main.main → runtime.main`), and the goroutine list (`[Go 1..n]`) are all visible; changing `StartArguments` and re-running, then evaluating `os.Args` at the breakpoint, confirms the new arguments `["gamma","delta","epsilon"]` took effect; execution continues to a normal exit. Another recorded pitfall (the three-part chain behind the command not appearing — all three must be right): (1) after VSCT compilation, `MergeWithCTO=true` in `VSPackage.resx` is required for it to be embedded into the assembly resources (`Menus.ctmenu`); (2) package registration must use `RegisterWithCodebase=true` — by default only the assembly display name is written (`PublicKeyToken=null`), which a non-GAC extension assembly cannot be resolved from, so the shell fails to load the package and the CTMENU merge silently reads nothing; (3) the shell caches the merge result by the `Menus` version number, so after fixing the resources you must bump the `ProvideMenuResource` version by 1 (or run `devenv /updateconfiguration`, which `install-vsix.ps1` now does after every install). Verification: `DTE.Commands.Item` confirms the command is in the command table, with the name `ProjectandSolutionContextMenus.Project.加入Go模組參考` (the zh-TW display name, since the IDE was running in Chinese).

## Enabling gopls on .go Files (LSP Content Type Wiring)

The VSIX's `GoLanguageClient` (`ILanguageClient`, responsible for starting `gopls serve`) was previously attached to `[ContentType("go")]`, but **no assembly exports a content type named `go`**, so Visual Studio never called `ActivateAsync` and gopls was never started. Editing, navigation, refactoring, and diagnostics were all blocked by this one gap.

`vsix/OrikaGo.LanguageService/GoContentTypeDefinitions.cs` now genuinely exports the content type and the file-extension mapping:

```csharp
public const string ContentTypeName = "OrikaGo";

[Export(typeof(ContentTypeDefinition))]
[Name(ContentTypeName)]
[BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]  // "code-languageserver-preview"
internal static ContentTypeDefinition GoContentType;

[Export(typeof(FileExtensionToContentTypeDefinition))]
[ContentType(ContentTypeName)]
[FileExtension(".go")]
internal static FileExtensionToContentTypeDefinition GoFileExtension;
```

Why this base definition? (All of the following comes from actually decompiling the metadata of VS 18.5 assemblies.)

- `CodeRemoteContentDefinition` in `Microsoft.VisualStudio.LanguageServer.Client.dll` declares `code-languageserver-preview` → `code-languageserver-base` → `languageserver-base`. And `Microsoft.VisualStudio.LanguageServer.Client.Implementation.dll` uses precisely `languageserver-base` as the condition for every activation entry point, so **only something derived from it gets `ActivateAsync` called**.
- `code-languageserver-preview` also derives from `code-languageserver-textmate-color` / `-structure` / `-brace` / `-indentation` and `code-textmate-commentselection`. `Microsoft.VisualStudio.LanguageServices.LanguageExtension.VSCore.dll` resolves a TextMate grammar for such buffers by file extension, so VS's built-in Go grammar (`Common7\IDE\CommonExtensions\Microsoft\TextMate\Starterkit\Extensions\go\syntaxes\go.json`, `scopeName: source.go`, `fileTypes: ["go"]`) still colorizes `.go`. Activation and colorization are fixed in one shot.
- The name deliberately is not `go`: VS's TextMate content types are registered programmatically as `code++` and `code++.<grammar name>` (a plain `.go` buffer's type is `code++.Go`), and no content type named `go` exists in VS 18; using `OrikaGo` also avoids colliding with a future built-in name.

`GoLanguageClient` itself needs no changes (it reads exactly this constant). After updating the VSIX you must **restart Visual Studio** (the MEF cache has to be rebuilt). If gopls does not start, verify that `gopls.exe` is on `PATH`, in `GOBIN`, in `GOPATH\bin` (including values persisted by `go env -w`), or in `%USERPROFILE%\go\bin`: `go install golang.org/x/tools/gopls@latest`.

## gopls Server Settings and Awareness of External File Changes (InitializationOptions / FilesToWatch)

Once gopls starts, three properties of `GoLanguageClient` were originally all `null`. With gopls v0.14.2's defaults, that means **semantic colorization, all seven inlay hints, staticcheck, and every analyzer switch are off**. They are now all pushed to the server in the single `initialize` call: `ConfigurationSections` stays `null` (this client does not use `workspace/didChangeConfiguration`), and all settings go through `InitializationOptions`, so gopls is fully configured by the time `initialize` returns, with no dependency on a post-startup configuration round trip.

### InitializationOptions: Flat Structure, and the Key Names Must Exist

Before dispatching settings, gopls flattens hierarchical names (`internal/lsp/source/options.go`):

```go
split := strings.Split(name, ".")
name = split[len(split)-1]
```

So the `ui.*` / `build.*` prefixes in the gopls documentation are **grouping in the docs, not a wire format**; what goes over the wire is a flat object. The other key point is that **unknown keys are not ignored** — they fall through to `default: result.unexpected()`, producing an Error-level `window/showMessage`:

```
Invalid settings: unexpected gopls setting "..."
```

That is, an error notification pops up every time the solution is opened. Every key sent is therefore validated against `.Options.User[].Name` from `gopls api-json` (9 of 9 matched). The settings actually sent:

| Setting | Value | Effect |
|------|-----|------|
| `semanticTokens` | `true` | When off, gopls answers `textDocument/semanticTokens/full` with `semantictokens are disabled` |
| `staticcheck` | `true` | Adds staticcheck's SA/S/ST checks on top of the default vet-style analyzers |
| `usePlaceholders` | `true` | Inserts parameters as tab-navigable placeholders when completing a function |
| `gofumpt` | `false` | gofumpt is stricter than gofmt and rewrites code, so it stays opt-in |
| `hints` | all seven on | gopls defaults to an empty map, which disables inlay hints entirely |
| `analyses` | `unusedparams=true`, `shadow=false` | `shadow` is noisy; spelling out the default value makes it explicit that it is deliberately off |
| `directoryFilters` | excludes `node_modules` / `bin` / `obj` | gopls only excludes `node_modules` by default |
| `buildFlags` / `env` | empty | Reserved as the hook-up point for `GoFlags` / build tags |

### FilesToWatch: the Only Channel for External Changes

`FilesToWatch` was originally `null`, with a comment claiming "gopls registers its own watchers". In reality there is an entire class of changes the editor never touches: the SDK's `GoEnsureMod` runs `go mod init` / `go mod edit -go=<LangVersion>`, and `GoEnsureWorkspace` runs `go work use` (both have idempotency guards and act only when the file genuinely needs to change — but adjusting `LangVersion` or adding a project does rewrite `go.mod` / `go.work` during the build); `go build` updates `go.sum`; and `go get` / `go mod tidy` run from a terminal change `go.mod`, `go.sum`, and `.go` files. None of these files were ever opened, so gopls can only learn about them through `workspace/didChangeWatchedFiles`. What is now watched:

```csharp
public IEnumerable<string> FilesToWatch => new[]
{
    "**/*.go", "**/go.mod", "**/go.sum", "**/go.work",
};
```

### RPC Tracing (Off by Default)

The arguments to `gopls serve` are now decided by `BuildGoplsArguments()`. RPC tracing is very noisy and hurts throughput, so it is opt-in: set the environment variable `ORIKA_GOPLS_RPCTRACE` to `1` / `true` / `yes` / `on` before starting Visual Studio and `-rpc.trace` is added; unset or unrecognized values always leave it off.

### Verification

Using an LSP probe (which really starts `gopls serve`, goes through `initialize` → `initialized` → `didOpen`, and then issues real requests), the two configurations were compared against the same Go source. **The settings JSON is extracted by reflection from the compiled `OrikaGo.LanguageService.dll`**, not a hand-copied duplicate:

| Request | `initializationOptions = {}` (before) | This configuration |
|------|--------------------------------------|----------|
| `textDocument/semanticTokens/full` | Server error `semantictokens are disabled` | 45 semantic tokens |
| `textDocument/inlayHint` | 0 | 5 (types and parameter names) |
| `textDocument/publishDiagnostics` | none | `S1002` (staticcheck) plus `unusedparams` |
| Rejected settings | — | 0 (control: deliberately sending a non-existent key does trigger the Error message) |

External file awareness was measured too: creating a new `.go` file on disk that was **never `didOpen`ed** (with content duplicating an existing function) and sending only `workspace/didChangeWatchedFiles` made gopls immediately report `helper redeclared in this block` — proving that this channel really does let the server see changes made outside the editor.

> **The part that can only be confirmed visually inside the IDE**: the verification above proves that "gopls's behavior really changes once it receives these settings" and that "gopls reacts to `didChangeWatchedFiles`". Whether **Visual Studio actually sends that notification according to `FilesToWatch`'s globs**, and how semantic colors and inlay hints actually render in the `.go` editor, are VS UI behaviors that cannot be proven in a headless environment.

## Project Structure

```
epic/
├── epic.slnx                    # Solution file (new XML format; Type="C#" makes VS load .goproj with the SDK project system)
├── epic.goproj                  # Project file using Orika.NET.Sdk
├── go.mod                      # Go module definition (the go directive is managed by the SDK from LangVersion)
├── main.go                     # Entry point
├── greeting.go                 # Greet helper function
├── greeting_test.go            # Tests run by go test
├── Properties/launchSettings.json
├── sdk/Orika.NET.Sdk/          # The SDK itself (Sdk.props / Sdk.targets / packaging project)
├── templates/                  # Orika.Go.Templates template package (go-console / go-lib)
├── compiler/                   # Compiler platform: the orika-goc sidecar, Orika.Go.CodeAnalysis(+.Tests)
├── vsix/OrikaGo.LanguageService/ # VS language service VSIX (gopls LSP + goproj.pkgdef: standalone project type GUID {8A0FBF95-...}, pointing at the CPS package following the msbuildproj pattern)
├── packages/                   # Local NuGet feed (output of build-sdk.ps1)
├── nuget.config
└── build-sdk.ps1
```

## Icons

- **The "New Project" dialog**: the `go-console` and `go-lib` templates each carry a `.template.config/icon.png` (32x32) and declare it in `.template.config/ide.host.json` with `"icon": "icon.png"` (relative paths are resolved against `.template.config`). After repackaging and installing `Orika.Go.Templates`, VS's "New Project" dialog shows the template icons.
- **Solution Explorer**: `OrikaGoImages.imagemanifest` inside the VSIX registers two icon sets, `GoProjectNode` and `GoFileNode`, with the VS image service (the PNGs are embedded in `OrikaGo.LanguageService.dll` as WPF component resources), and `GoProjectTreeIconProvider` (`IProjectTreePropertiesProvider`, `[Order(1000)]`) applies them to the project root node and `.go` file nodes.
- **The OrikaGo ProjectCapability**: `Orika.NET.Sdk`'s `Sdk.props` declares `<ProjectCapability Include="OrikaGo" />` for every `.goproj` project, and the VSIX's MEF exports use `[AppliesTo("OrikaGo")]` so they apply only to Go projects.
- **Note**: after updating the templates or the VSIX you must **restart Visual Studio** (the template cache and the MEF / image library caches have to be rebuilt) before the new icons appear.

## Correctness Fixes in the Compiler Platform

An external review (codex) found three real defects in `compiler/`; all are fixed with tests added (total test count 11 → 23):

| Defect | Symptom | Fix |
|------|------|------|
| Type checking did not understand Go modules | The sidecar checked using `go/importer`'s source importer, which does not understand `go.mod` / the module cache / `go.work`. A module importing an external dependency (for example `github.com/google/uuid`) built fine with `go build`, yet `GetDiagnostics()` reported a bogus `could not import ...`. | Switched to `golang.org/x/tools/go/packages` (`packages.Load`, which internally drives a real `go list`), so module dependencies, workspaces, and `vendor` are resolved exactly the way `go build` does. The shape of the JSON protocol is unchanged, and so is the rule that "source errors are data (exit code 0), and only infrastructure failures return non-zero". |
| Checking and building could see different file sets | `Emit` accepts `Tags` / `OS` / `Arch`, but `GetDiagnostics()` had no corresponding options, and the sidecar always checked with `build.Default`. So `//go:build linux` code was not checked at all on Windows, yet would be compiled by `Emit(OS = "linux")`. | The sidecar's `check` / `symbol` gained `-tags` / `-goos` / `-goarch` (setting `GOOS` / `GOARCH` / `GOFLAGS=-tags=…` via `packages.Config.Env`). On the C# side `GoAnalysisOptions` was added and `GoEmitOptions` now derives from it, so **the same options object** can be handed to `GetDiagnostics(options)`, `GetSemanticModel(options)`, and `Emit(path, options)` alike. The parameterless overloads are unchanged. |
| Column units disagreed with the editor | Go's `token.Position.Column` is a **byte count within the line**, whereas .NET / Visual Studio / LSP use **UTF-16 code units**. On lines containing non-ASCII characters (for example `fmt.Println("你好世界", value)`), querying `GetSymbolAt` with the column VS reports returned `null`. | Convert at the sidecar's protocol boundary: byte column → UTF-16 column when emitting positions, and UTF-16 column → byte column when `symbol` accepts a position (converted by actually reading the raw bytes of that line). All three commands — `parse`, `check`, `symbol` — are consistent. `offset` remains a byte offset. The C# side's XML documentation now states the units explicitly. |

How it was verified (`compiler/Orika.Go.CodeAnalysis.Tests/`): `GoModuleResolutionTests` uses real `go mod tidy` / `go build` as the baseline and requires `GetDiagnostics()` to agree with `go build`'s verdict; `GoBuildContextTests` checks that files under `//go:build linux` and custom tags are "invisible by default, visible only when the build context is specified"; `GoColumnUnitTests` uses source containing `你好世界` / `名前` to confirm that columns are in UTF-16 units (and that the old byte columns no longer resolve successfully). All 11 of these new tests failed against the pre-fix sidecar.

> The sidecar now depends on `golang.org/x/tools` (see `compiler/orika-goc/go.mod` / `go.sum`). Because `check` type-checks dependency packages from source via `go list`, a single check takes several seconds.

## Correctness Fixes in the MSBuild SDK

An external review (codex) found three real defects in `sdk/Orika.NET.Sdk/Sdk/`; all are fixed:

| Defect | Symptom | Fix |
|------|------|------|
| `go work use` had no cross-process lock (`Sdk.targets`) | One `go.work` is shared by multiple `.goproj` files; under a `/m` parallel build each project independently decides "I am not a member" in its own process and rewrites `go.work` simultaneously. `go work use` is a whole-file read-modify-write, so the later writer **silently overwrites** the earlier one, erasing other modules from the `use` list (and every process still exits with code 0). | Added the inline task `<GoWorkUse>` (using `RoslynCodeTaskFactory`, like `GoExec`): it names a system-wide Mutex from a hash of `go.work`'s full path (`Global\OrikaGo.GoWork.<hash>`, so different workspaces do not queue behind each other; it degrades to `Local\` when `SeCreateGlobalPrivilege` is unavailable), **re-checks membership inside the lock** before running `go work use`, and releases it in `finally`; an `AbandonedMutexException` is treated as acquiring ownership (the previous holder died mid-way, and re-reading inside the lock is self-healing). Idempotency, "do nothing when there is no `go.work`", "do nothing when `GOWORK=off`", and "never rewrite the user's hand-written existing entries" all remain unchanged. |
| Incomplete wildcards for cgo input files (`Sdk.props`) | `GoNativeCompile` covered only `.c` / `.h` / `.s` / `.S` / `.syso`. When only `helper.cpp` changed, MSBuild judged everything up to date, **`GoBuild` was skipped entirely and `go build` never ran**, leaving a stale binary behind. | Filled in the complete set that `go/build` actually accepts: `.c .cc .cpp .cxx .m .mm .h .hh .hpp .hxx .s .S .sx .f .F .for .f90 .swig .swigcxx .syso`. In the same project `@(GoNativeCompile)` went from 4 items to 18; after touching only `helper.cpp`, `GoBuild` changed from "Skipping target … up-to-date" to actually re-running and rewriting the executable. |
| Misattributed verdicts in test output (`GoDiagnostics.targets`) | Pending diagnostic lines were kept in a **single shared buffer** and were all resolved by "whichever verdict arrived next". Under `go test -v -parallel=2`, if a parallel test logged and `--- PASS`ed first, the failing test's navigable `file.go:N:` location was discarded as an ordinary message and the Error List was left with only the generic "command exited with a non-zero exit code"; conversely, when `--- FAIL` arrived first, log lines from passing subtests were wrongly promoted to errors. | Changed to **attribution by test name**: track the owner indicated by `=== RUN` / `PAUSE` / `CONT` / `NAME`, stamp it on every pending diagnostic, and have `--- FAIL` / `--- PASS` / `--- SKIP` resolve only the entries for **the test they name** (including `TestX/sub` subtests); anything still unresolved when the stream ends is emitted as a message. The non-verbose sequential case (`--- FAIL` printed first) and the base-file-name backfill index for `go test`'s directory-stripped names both keep their previous behavior. |

Verification: the `go.work` race was reproduced with a test harness outside the repository — 6 concurrent `dotnet build -t:GoEnsureWorkspace` processes sharing one `go.work`; before the fix (the targets at git HEAD), 10 out of 30 rounds lost modules in 3 rounds, while after the fix all 30 rounds kept all 6 modules plus the user's hand-written entries, and `go work edit -json` parsed successfully every time; the real solution was rebuilt from an empty `go.work` with `dotnet build epic.slnx -t:Rebuild -m`, and the result was byte-for-byte identical to the checked-in version. The cgo wildcards were verified by dumping `@(GoNativeCompile)` and by comparing whether a rebuild happens after touching only the `.cpp` (**no C/C++ toolchain is installed on this machine, so `CGO_ENABLED=1 go build` stops at `cgo: C compiler "gcc" not found`; what was verified is therefore MSBuild's up-to-date-check mechanism, not actual cgo compilation**). Test verdict attribution was verified with two `t.Parallel()` tests (one logging and passing first, the other failing later): after the fix the output is `…\inner\race_test.go(11): error GOTEST: deliberate failure from the parallel test`, whereas before the fix the same scenario yielded only a generic `exited with code 1`.

## Fixes from the Third Adversarial Review

The third review (an internal multi-agent adversarial workflow running in parallel with external codex, cross-compared) confirmed 15 new defects, all of which are fixed and verified. New feature added in the same round: `GoModuleReference` (see "Supported Properties").

**MSBuild SDK (`sdk/Orika.NET.Sdk/Sdk/`)**:

| Defect | Symptom | Fix |
|------|------|------|
| Stale binaries across workspace modules | `_GoBuildInput` collected only the project's own files; under `go.work`, after modifying a sibling module the importing project was judged up to date and skipped `GoBuild`, leaving an executable with the old behavior in `bin` (exit code 0). | When a `go.work` is detected (`GOWORK` or an upward search), a never-existing file is placed in the input list so `GoBuild` always runs — incrementality is handed back to go's own build cache (with no changes, you pay a single process startup). MSBuild's incremental behavior for non-workspace projects is unchanged. |
| `dotnet test` / `go vet` lacked build context | `VSTest` and `GoVet` did not carry `$(_GoBuildArgs)`, so `DefineConstants`'s `-tags` and `GoFlags` affected only the build; tag-gated code was `undefined` during tests, and vet checked the wrong file set. | Both now carry `$(_GoBuildArgs)` (`go test` / `go vet` both accept build flags), so they see the same set of files as `GoBuild`. |
| `GoWorkUse` hard-coded `GOTOOLCHAIN=local` | `go work use` **loads** the `go.mod` of the module being added, so a project whose langVersion is newer than the local toolchain could never be added to the workspace (even though the templates offer exactly those options). | Removed the hard-coded value and left it at the default `auto` (measured: under go 1.21.5, adding a `go 1.22` module to the workspace automatically switched to go1.25.12 and succeeded). The rationale in the original comment holds for `go work edit` but not for `go work use`. |
| `go mod init` name not sanitized, plus misattributed errors | When the project name contained spaces or non-ASCII characters (perfectly legal in VS), `go mod init "My App"` failed with `malformed module path`, and because it ran through a plain `Exec`, the error pointed at `Sdk.targets` inside the NuGet cache. | The default value of `GoModuleName` now applies the same sanitization as the template's `goModuleSafe` (`My App` → `My_App`; builds succeed in practice), and `GoEnsureMod` switched to `GoExec` (error code `GOMOD`). |
| RID validation blocked explicit overrides | `_GoValidatePublishRid` recognized only the six built-in RIDs, so even an explicit `-r freebsd-x64 -p:GoOS=freebsd -p:GoArch=amd64` was rejected. | It now errors only when the **effective value** (after merging the RID mapping with explicit `GoOS` / `GoArch`) is still empty; freebsd-x64 publish was measured to succeed. |
| The diagnostic parser did not recognize cgo extensions | The regular expressions in `GoExec` and `GoCompilation` recognized only `.go/.s/.S/.c/.h`, so C/C++ compile errors like `helper.cpp:3:5: error:` were not navigable from the Error List. | Both extension sets were aligned with the full `@(GoNativeCompile)` list. |

**Compiler platform (`compiler/`)**:

| Defect | Symptom | Fix |
|------|------|------|
| `Emit()` columns were byte columns | `ParseGoBuildOutput` stuffed the byte columns from `go build`'s stderr into `GoLocation` verbatim, violating the documented contract that "all columns are UTF-16" and reporting a different column than `GetDiagnostics()` for the same error. | Implemented the conversion on the C# side following the same rules as the sidecar's `toUTF16Col` (including the BOM rule), with a new test: byte column 30 → UTF-16 column 22 on a `你好世界` line. |
| Relative paths resolved against the cwd | The positions in `go list`'s ListError are relative to the **module directory**, but `addDiag` used `filepath.Abs` (the process cwd, which is arbitrary when VS spawns it), so diagnostics pointed at non-existent files and, being inconsistent with the parse paths, produced duplicates. | Relative paths are now always joined to the loader's module directory (`resolvePath`). |
| The message for a missing require was not actionable | The actionable `no required module provides package X; to add it: go get X` was attached to the **dependency stub package**, and the loop that visited only top-level packages threw it away, leaving just `could not import X (invalid package name: "")`. | Switched to `packages.Visit` to walk the whole graph; for dependency packages only errors **whose position falls inside this module** are taken, so internal errors from third-party packages do not flood the Error List. |
| A broken `go.mod` became an infra error | A typo in `go.mod` (a state every manual edit passes through) made `packages.Load` hard-fail, the sidecar exit 1, and the C# side's `GetDiagnostics()` throw an exception outright — violating the "broken source is data, exit 0" contract. | A load failure where the toolchain **did run** is now converted into a diagnostic pointing at the corresponding line of `go.mod` (taking the line number from `go.mod:5:` when it appears in the message), with exit 0; only "the go command does not exist / the directory does not exist" remains an infra error. |
| Columns on the first line of a BOM file were off by one | The 3 bytes of a UTF-8 BOM count toward go/token's byte column, but the VS buffer strips the BOM; `utf16Len` counted U+FEFF as 1 unit, so every column on the first line was off by one (`toByteCol` had the mirror-image bug). | A leading U+FEFF now counts as 0 UTF-16 units; `toByteCol` skips the BOM's 3 bytes before counting (editor column 1 ↔ byte column 4). Measured on a BOM file, `parse` reports File at column 1 and the `main` identifier at column 9, matching the VS buffer. |

**VSIX (`vsix/OrikaGo.LanguageService/`)**:

| Defect | Symptom | Fix |
|------|------|------|
| Watched-file events were never received in solution mode | `FilesToWatch` is consumed only by the Open Folder host; under .sln/.slnx (the primary mode), after a terminal `go get` or an SDK target rewriting go.mod, gopls kept using a stale module graph, producing false red squiggles until VS was restarted. VS also hard-codes dynamicRegistration=false, so gopls cannot register a watcher itself. | Implemented `ILanguageClientCustomMessage2.AttachForCustomMessageAsync` to obtain the JsonRpc, and after `initialized` attach a client-side `FileSystemWatcher` at the workspace root (`*.go` / `go.mod` / `go.sum` / `go.work`, excluding bin/obj/node_modules, consistent with `FilesToWatch`), forwarding `workspace/didChangeWatchedFiles` directly over the rpc (1=Created, 2=Changed, 3=Deleted; a rename is split into 3+1). The watcher and the rpc are released along with the server's lifetime. |
| `FindGopls` ignored GOBIN/GOPATH | It only looked at PATH and `%USERPROFILE%\go\bin`; users with `go env -w GOBIN=…` would follow the error message's `go install` advice and still get "not found", forever. | The probe order is now PATH → `GOBIN` / `GOPATH\bin` (environment variables plus reading `go env` for values persisted by `go env -w`) → `%USERPROFILE%\go\bin`, and the error message was updated to match. |

**Scripts**: `install-vsix.ps1 -NoBuild` no longer requires the extension development workload — it only needs `VSIXInstaller.exe`, which every VS installation has; the workload check now runs only when an MSBuild build is actually needed.

Verification: compiler tests 26/26 (including the new Emit UTF-16 column test); the workspace stale binary, `-tags` reaching test/vet, the freebsd publish override, `My App` sanitization, the GoWorkUse toolchain switch, and the four orika-goc scenarios (relative paths, missing require, broken go.mod, BOM) were all confirmed by comparing actual reproduction scripts before and after the fix.

**A follow-up user report**: Solution Explorer required "Show All Files" before `main.go` was visible. The root cause is in `Microsoft.NET.Sdk.DefaultItems.props` — it first does `None Include="**/*"` and then `None Remove="**/*$(DefaultLanguageSourceExtension)"` to take language source files out of None; `.goproj` has no language props, so that property is an **empty string**, the `Remove` becomes `**/*`, and it wipes out the entire None list that was just built, leaving the project with **no items at all**. The fix: after the nested imports, `Sdk.props` re-runs the None glob with the same Exclude (`-getItem:None` went from an empty list to the full file list, including `main.go` / `go.mod` / `go.work`).


## License

[MIT](LICENSE) — Copyright (c) 2026 Orlys.

You may use, modify and redistribute this, including commercially, provided the
copyright notice and the licence text travel with it. The notice ships inside
the VSIX as `LICENSE.txt` and is declared in both NuGet packages as
`PackageLicenseExpression`, so a consumer receives it automatically.

The software is provided as is, without warranty — see the experimental-project
warning at the top.
