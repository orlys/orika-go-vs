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
}
