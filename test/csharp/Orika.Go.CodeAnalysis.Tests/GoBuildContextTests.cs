using Xunit;

namespace Orika.Go.CodeAnalysis.Tests;

/// <summary>
/// Analysis and emit must be able to look at the *same* set of files.
///
/// Go decides which files belong to a package from GOOS/GOARCH and build tags, so a
/// compilation that emits for linux while type-checking for the host is checking code
/// that will never be built and ignoring code that will. Emit already accepted
/// OS/Arch/Tags; GetDiagnostics and GetSemanticModel now accept the same options
/// (<see cref="GoAnalysisOptions"/>, the base type of <see cref="GoEmitOptions"/>), so
/// one options object can drive all three.
/// </summary>
public class GoBuildContextTests
{
    public GoBuildContextTests() => Sidecar.EnsureConfigured();

    private const string PortableMain =
        "package main\n" +
        "\n" +
        "func main() {}\n";

    //  line 1: //go:build linux
    //  line 2:
    //  line 3: package main
    //  line 4:
    //  line 5: func linuxOnly() int {
    //  line 6: \tvar n int = "not an int"     <- error at col 14 (the string literal)
    //  line 7: \treturn n
    //  line 8: }
    private const string LinuxOnlyWithTypeError =
        "//go:build linux\n" +
        "\n" +
        "package main\n" +
        "\n" +
        "func linuxOnly() int {\n" +
        "\tvar n int = \"not an int\"\n" +
        "\treturn n\n" +
        "}\n";

    //  line 1: //go:build orikafeature
    //  ...
    //  line 6: \tvar n int = "not an int"     <- error at col 14
    private const string TagGatedWithTypeError =
        "//go:build orikafeature\n" +
        "\n" +
        "package main\n" +
        "\n" +
        "func gated() int {\n" +
        "\tvar n int = \"not an int\"\n" +
        "\treturn n\n" +
        "}\n";

    //  line 1: //go:build linux
    //  line 2:
    //  line 3: package main
    //  line 4:
    //  line 5: func linuxGreeting() string {
    //  line 6: \tgreeting := "hi"          <- declaration at line 6, col 2
    //  line 7: \treturn greeting           <- use at line 7, col 9
    //  line 8: }
    private const string LinuxOnlySymbolSource =
        "//go:build linux\n" +
        "\n" +
        "package main\n" +
        "\n" +
        "func linuxGreeting() string {\n" +
        "\tgreeting := \"hi\"\n" +
        "\treturn greeting\n" +
        "}\n";

    [Fact]
    public void GetDiagnostics_LinuxGatedError_InvisibleByDefaultAndVisibleForGoosLinux()
    {
        using var module = new TempGoModule();
        module.WriteFile("main.go", PortableMain);
        string gated = module.WriteFile("linux_gated.go", LinuxOnlyWithTypeError);

        var compilation = GoCompilation.Create(module.Directory);

        // Default build context (this host): the file is excluded, so nothing to report.
        Assert.Empty(compilation.GetDiagnostics());

        // Asking for the linux build context brings the file — and its error — into view.
        var linuxOptions = new GoAnalysisOptions { OS = "linux", Arch = "amd64" };
        var linuxDiagnostics = compilation.GetDiagnostics(linuxOptions);

        var error = Assert.Single(linuxDiagnostics);
        Assert.Equal("GOTYPE", error.Id);
        Assert.Equal(gated, error.Location.File);
        Assert.Equal(6, error.Location.Line);
        Assert.Equal(14, error.Location.Column);
        Assert.Contains("as int value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetDiagnostics_TagGatedError_InvisibleWithoutTagAndVisibleWithIt()
    {
        using var module = new TempGoModule();
        module.WriteFile("main.go", PortableMain);
        string gated = module.WriteFile("tag_gated.go", TagGatedWithTypeError);

        var compilation = GoCompilation.Create(module.Directory);

        Assert.Empty(compilation.GetDiagnostics());

        var tagged = new GoAnalysisOptions { Tags = { "orikafeature" } };
        var error = Assert.Single(compilation.GetDiagnostics(tagged));

        Assert.Equal(gated, error.Location.File);
        Assert.Equal(6, error.Location.Line);
        Assert.Equal(14, error.Location.Column);
    }

    [Fact]
    public void GetSemanticModel_LinuxGatedFile_ResolvesSymbolsOnlyInTheLinuxBuildContext()
    {
        using var module = new TempGoModule();
        module.WriteFile("main.go", PortableMain);
        string gated = module.WriteFile("linux_symbol.go", LinuxOnlySymbolSource);

        var compilation = GoCompilation.Create(module.Directory);

        // In the linux build context the file is part of package main and `greeting`
        // resolves to the local variable declared one line above.
        var linuxModel = compilation.GetSemanticModel(new GoAnalysisOptions { OS = "linux", Arch = "amd64" });
        var symbol = linuxModel.GetSymbolAt(gated, 7, 9);

        Assert.NotNull(symbol);
        Assert.Equal("greeting", symbol!.Name);
        Assert.Equal("var", symbol.Kind);
        Assert.Equal("string", symbol.Type);
        Assert.NotNull(symbol.DeclaredAt);
        Assert.Equal(6, symbol.DeclaredAt!.Line);
        Assert.Equal(2, symbol.DeclaredAt.Column);
    }

    [Fact]
    public void GoEmitOptions_IsUsableAsAnalysisOptions_SoCheckAndEmitSeeTheSameFiles()
    {
        using var module = new TempGoModule();
        module.WriteFile("main.go", PortableMain);
        module.WriteFile("linux_gated.go", LinuxOnlyWithTypeError);

        // One options object, used for both analysis and emit: that is the whole point
        // of GoEmitOptions deriving from GoAnalysisOptions.
        var options = new GoEmitOptions { OS = "linux", Arch = "amd64" };

        var compilation = GoCompilation.Create(module.Directory);
        var diagnostics = compilation.GetDiagnostics(options);

        Assert.Single(diagnostics);

        // And the emit for that same context fails on exactly that file, confirming the
        // check was not analyzing a different file set than the build compiles.
        string outputPath = Path.Combine(module.Directory, "out", "app-linux");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var emitResult = compilation.Emit(outputPath, options);

        Assert.False(emitResult.Success, "Emit was expected to fail: the linux file has a type error.");
        Assert.Contains(emitResult.Diagnostics, d =>
            d.Location.File is not null &&
            d.Location.File.EndsWith("linux_gated.go", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Build tags reach the analysis sidecar's `go list` through GOFLAGS, because
    /// golang.org/x/tools/go/packages exposes no command line of its own. Emit, by
    /// contrast, puts -tags on the `go build` command line and leaves the inherited
    /// GOFLAGS alone. If the sidecar *replaced* GOFLAGS with its own -tags it would
    /// silently discard every other flag the environment asked for, and the two would
    /// disagree about the very same module - which is exactly what
    /// <see cref="GoBuildContextTests"/> exists to prevent.
    /// </summary>
    [Fact]
    public void GetDiagnostics_WithTags_KeepsInheritedGoflags_SoAnalysisStillAgreesWithEmit()
    {
        using var module = new TempGoModule("goflagsmod");

        // A dependency resolved through a local `replace` (nothing is downloaded), then
        // vendored - and the vendored copy is given a function the real dependency source
        // does not have. The module's meaning therefore depends on -mod: under vendor mode
        // it compiles, under -mod=mod the call is undefined.
        Directory.CreateDirectory(Path.Combine(module.Directory, "depsrc"));
        module.WriteFile(Path.Combine("depsrc", "go.mod"), "module example.com/dep\n\ngo 1.21\n");
        module.WriteFile(Path.Combine("depsrc", "dep.go"), "package dep\n\nfunc Base() string { return \"base\" }\n");
        module.WriteFile(
            "go.mod",
            "module goflagsmod\n\ngo 1.21\n\nrequire example.com/dep v0.0.0\n\nreplace example.com/dep => ./depsrc\n");
        module.WriteFile("main.go", "package main\n\nimport \"example.com/dep\"\n\nfunc main() { println(dep.Base()) }\n");

        var (vendorExit, vendorOutput) = module.RunGo("mod", "vendor");
        Assert.True(vendorExit == 0, "go mod vendor failed: " + vendorOutput);

        module.WriteFile(
            Path.Combine("vendor", "example.com", "dep", "dep.go"),
            "package dep\n\nfunc Base() string { return \"base\" }\n\nfunc VendorOnly() string { return \"vendor\" }\n");
        module.WriteFile(
            "main.go",
            "package main\n\nimport \"example.com/dep\"\n\nfunc main() { println(dep.Base()); println(dep.VendorOnly()) }\n");

        var compilation = GoCompilation.Create(module.Directory);

        // One options object for both calls: whatever they disagree about, it is not this.
        var options = new GoEmitOptions { Tags = { "orikafeature" } };

        string outputPath = Path.Combine(module.Directory, "out", "app.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string? savedGoflags = Environment.GetEnvironmentVariable("GOFLAGS");
        try
        {
            // Control: no inherited GOFLAGS. vendor/ exists, so the go command selects
            // vendor mode by itself, the patched function resolves, and both agree "clean".
            Environment.SetEnvironmentVariable("GOFLAGS", null);
            Assert.Empty(compilation.GetDiagnostics(options));
            Assert.True(
                compilation.Emit(outputPath, options).Success,
                "Emit was expected to succeed in the default (vendor) mode.");

            // The real case: the environment asks for -mod=mod, which overrides the vendor
            // default. Emit inherits GOFLAGS and fails...
            Environment.SetEnvironmentVariable("GOFLAGS", "-mod=mod");

            var emitResult = compilation.Emit(outputPath, options);
            Assert.False(emitResult.Success, "Emit was expected to honour the inherited GOFLAGS=-mod=mod.");
            Assert.Contains(
                emitResult.Diagnostics,
                d => d.Message.Contains("undefined: dep.VendorOnly", StringComparison.Ordinal));

            // ...so the analysis must report the same problem. A sidecar that overwrote
            // GOFLAGS with "-tags=orikafeature" would drop -mod=mod, fall back to vendor
            // mode and report nothing here.
            var error = Assert.Single(compilation.GetDiagnostics(options));
            Assert.Equal("GOTYPE", error.Id);
            Assert.Contains("undefined: dep.VendorOnly", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOFLAGS", savedGoflags);
        }
    }

    /// <summary>
    /// Merging the requested -tags into the inherited GOFLAGS must still let the explicit
    /// option win when GOFLAGS already carries a -tags of its own. The go command parses
    /// GOFLAGS left to right into a single flag set, so the last occurrence of a repeated
    /// flag is the one that takes effect and appending is an override - this test pins that
    /// down rather than leaving it as an assumption.
    /// </summary>
    [Fact]
    public void GetDiagnostics_ExplicitTags_OverrideATagsAlreadyPresentInGoflags()
    {
        using var module = new TempGoModule("tagoverridemod");
        module.WriteFile("main.go", PortableMain);
        string gatedA = module.WriteFile(
            "a_taga.go",
            "//go:build orikaa\n\npackage main\n\nfunc fa() int {\n\tvar n int = \"A\"\n\treturn n\n}\n");
        string gatedB = module.WriteFile(
            "b_tagb.go",
            "//go:build orikab\n\npackage main\n\nfunc fb() int {\n\tvar n int = \"B\"\n\treturn n\n}\n");

        var compilation = GoCompilation.Create(module.Directory);

        string? savedGoflags = Environment.GetEnvironmentVariable("GOFLAGS");
        try
        {
            Environment.SetEnvironmentVariable("GOFLAGS", "-tags=orikaa");

            // No explicit tags: the inherited -tags still selects the file set.
            var inherited = Assert.Single(compilation.GetDiagnostics());
            Assert.Equal(gatedA, inherited.Location.File);

            // Explicit tags: appended last, therefore the effective ones.
            var explicitly = Assert.Single(compilation.GetDiagnostics(new GoAnalysisOptions { Tags = { "orikab" } }));
            Assert.Equal(gatedB, explicitly.Location.File);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOFLAGS", savedGoflags);
        }
    }
}
