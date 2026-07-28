using Xunit;

namespace Orika.Go.CodeAnalysis.Tests;

/// <summary>
/// Type checking must model Go *modules*, not just a directory of files.
///
/// The sidecar used to type-check with go/importer's "source" importer, which knows
/// nothing about go.mod, the module cache or go.work. A module that imports an
/// external dependency therefore built fine with `go build` while GetDiagnostics()
/// reported a bogus "could not import ..." error. These tests pin the contract:
/// whatever `go build` accepts, GetDiagnostics() must accept too — and real type
/// errors must still be found in the presence of external dependencies.
/// </summary>
public class GoModuleResolutionTests
{
    public GoModuleResolutionTests() => Sidecar.EnsureConfigured();

    private const string ExternalDependency = "github.com/google/uuid";

    //  line 1: package main
    //  line 2:
    //  line 3: import (
    //  line 4: \t"fmt"
    //  line 5:
    //  line 6: \t"github.com/google/uuid"
    //  line 7: )
    //  line 8:
    //  line 9: func main() {
    // line 10: \tid := uuid.New()
    // line 11: \tfmt.Println(id.String())
    // line 12: }
    private const string UsesExternalDependency =
        "package main\n" +
        "\n" +
        "import (\n" +
        "\t\"fmt\"\n" +
        "\n" +
        "\t\"github.com/google/uuid\"\n" +
        ")\n" +
        "\n" +
        "func main() {\n" +
        "\tid := uuid.New()\n" +
        "\tfmt.Println(id.String())\n" +
        "}\n";

    // Same module, but line 11 assigns a string to an int:
    // line 11: \tvar n int = id.String()   <- error reported at `id`, byte/UTF-16 col 14
    private const string UsesExternalDependencyWithTypeError =
        "package main\n" +
        "\n" +
        "import (\n" +
        "\t\"fmt\"\n" +
        "\n" +
        "\t\"github.com/google/uuid\"\n" +
        ")\n" +
        "\n" +
        "func main() {\n" +
        "\tid := uuid.New()\n" +
        "\tvar n int = id.String()\n" +
        "\tfmt.Println(n)\n" +
        "}\n";

    /// <summary>
    /// Creates a module that really depends on <c>github.com/google/uuid</c>, resolving
    /// the requirement with the real toolchain so go.mod/go.sum are genuine.
    /// </summary>
    private static TempGoModule CreateModuleWithExternalDependency(string source)
    {
        var module = new TempGoModule();
        try
        {
            module.WriteFile("main.go", source);

            var (tidyExit, tidyOutput) = module.RunGo("mod", "tidy");
            Assert.True(tidyExit == 0,
                $"'go mod tidy' failed ({tidyExit}) while preparing the fixture: {tidyOutput}");

            string goMod = File.ReadAllText(Path.Combine(module.Directory, "go.mod"));
            Assert.Contains(ExternalDependency, goMod, StringComparison.Ordinal);

            return module;
        }
        catch
        {
            module.Dispose();
            throw;
        }
    }

    [Fact]
    public void GetDiagnostics_ModuleWithExternalDependency_AgreesWithGoBuild()
    {
        using var module = CreateModuleWithExternalDependency(UsesExternalDependency);

        // Ground truth: the real compiler is happy with this module.
        var (buildExit, buildOutput) = module.RunGo("build", "-o", Path.Combine(module.Directory, "out.bin"), ".");
        Assert.True(buildExit == 0, $"'go build' unexpectedly failed: {buildOutput}");

        var diagnostics = GoCompilation.Create(module.Directory).GetDiagnostics();

        // The old source-importer loader reported
        //   could not import github.com/google/uuid (no required module provides package ...)
        // at line 6 even though go build succeeded.
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GetDiagnostics_ModuleWithExternalDependency_StillReportsRealTypeErrors()
    {
        using var module = CreateModuleWithExternalDependency(UsesExternalDependencyWithTypeError);

        // Ground truth: the real compiler rejects this module.
        var (buildExit, _) = module.RunGo("build", "-o", Path.Combine(module.Directory, "out.bin"), ".");
        Assert.True(buildExit != 0, "'go build' was expected to fail on the deliberate type error.");

        var diagnostics = GoCompilation.Create(module.Directory).GetDiagnostics();

        var error = Assert.Single(diagnostics);
        Assert.Equal("GOTYPE", error.Id);
        Assert.Equal(GoDiagnosticSeverity.Error, error.Severity);
        Assert.Contains("as int value", error.Message, StringComparison.Ordinal);
        Assert.Equal(11, error.Location.Line);
        Assert.Equal(14, error.Location.Column);

        // No bogus import diagnostic tagging along.
        Assert.DoesNotContain(diagnostics, d =>
            d.Message.Contains("could not import", StringComparison.Ordinal));
    }
}
