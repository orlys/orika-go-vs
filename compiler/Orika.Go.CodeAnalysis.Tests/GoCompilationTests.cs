using Xunit;

namespace Orika.Go.CodeAnalysis.Tests;

public class GoCompilationTests
{
    public GoCompilationTests() => Sidecar.EnsureConfigured();

    //  line 1: package main
    //  line 2:
    //  line 3: import "fmt"
    //  line 4:
    //  line 5: func main() {
    //  line 6: \tvar s string = "a"
    //  line 7: \tvar i int = 1
    //  line 8: \tx := s + i          <- go/types: invalid operation: s + i (mismatched
    //  line 9: \tfmt.Println(x)         types string and int) at line 8, col 7 (the `s`)
    // line 10: }
    private const string TypeErrorSource =
        "package main\n" +
        "\n" +
        "import \"fmt\"\n" +
        "\n" +
        "func main() {\n" +
        "\tvar s string = \"a\"\n" +
        "\tvar i int = 1\n" +
        "\tx := s + i\n" +
        "\tfmt.Println(x)\n" +
        "}\n";

    private const string FixedSource =
        "package main\n" +
        "\n" +
        "import \"fmt\"\n" +
        "\n" +
        "func main() {\n" +
        "\tfmt.Println(\"orika-e2e\", 6*7)\n" +
        "}\n";

    [Fact]
    public void GetDiagnostics_TypeErrorModule_ReportsGoTypeMismatchAtLine8()
    {
        using var module = new TempGoModule();
        module.WriteFile("main.go", TypeErrorSource);

        var compilation = GoCompilation.Create(module.Directory);
        var diagnostics = compilation.GetDiagnostics();

        Assert.NotEmpty(diagnostics);

        var typeError = Assert.Single(diagnostics, d => d.Id == "GOTYPE");
        Assert.Equal(GoDiagnosticSeverity.Error, typeError.Severity);
        Assert.Contains("mismatched types string and int", typeError.Message);
        Assert.Equal(8, typeError.Location.Line);
        Assert.Equal(7, typeError.Location.Column);
    }

    [Fact]
    public void GetDiagnostics_ValidModule_IsEmpty()
    {
        using var module = new TempGoModule();
        module.WriteFile("main.go", FixedSource);

        var compilation = GoCompilation.Create(module.Directory);

        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void Emit_ValidModule_ProducesExecutableThatPrintsExpectedOutput()
    {
        using var module = new TempGoModule();
        module.WriteFile("main.go", FixedSource);

        var compilation = GoCompilation.Create(module.Directory);
        Assert.Empty(compilation.GetDiagnostics());

        string hostExt = OperatingSystem.IsWindows() ? ".exe" : "";
        string outputPath = Path.Combine(module.Directory, "out", "app" + hostExt);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var emitResult = compilation.Emit(outputPath);

        Assert.True(emitResult.Success,
            "Emit reported failure: " + string.Join("; ", emitResult.Diagnostics));
        Assert.True(File.Exists(outputPath),
            $"Emit did not produce a file at '{outputPath}'.");
        Assert.True(new FileInfo(outputPath).Length > 0, "Emitted binary is empty.");

        // The binary is real: run it and capture stdout.
        var (exitCode, stdout, stderr) = ProcessRunner.Run(outputPath);
        Assert.True(exitCode == 0, $"Emitted binary exited with {exitCode}. stderr: {stderr}");
        Assert.Equal("orika-e2e 42", stdout.Trim());
    }

    [Fact]
    public void Emit_LinuxArm64_ProducesElfBinaryWithoutExeSuffixDifferingFromHostBuild()
    {
        using var module = new TempGoModule();
        module.WriteFile("main.go", FixedSource);

        var compilation = GoCompilation.Create(module.Directory);

        string hostExt = OperatingSystem.IsWindows() ? ".exe" : "";
        string hostPath = Path.Combine(module.Directory, "out", "app-host" + hostExt);
        string crossPath = Path.Combine(module.Directory, "out", "app-linux-arm64");
        Directory.CreateDirectory(Path.Combine(module.Directory, "out"));

        var hostResult = compilation.Emit(hostPath);
        var crossResult = compilation.Emit(crossPath, new GoEmitOptions { OS = "linux", Arch = "arm64" });

        Assert.True(hostResult.Success,
            "Host emit reported failure: " + string.Join("; ", hostResult.Diagnostics));
        Assert.True(crossResult.Success,
            "Cross emit reported failure: " + string.Join("; ", crossResult.Diagnostics));

        Assert.True(File.Exists(hostPath), $"Host emit missing at '{hostPath}'.");
        Assert.True(File.Exists(crossPath), $"Cross emit missing at '{crossPath}'.");
        Assert.False(File.Exists(crossPath + ".exe"),
            "linux/arm64 output must not gain a .exe suffix.");

        long hostLength = new FileInfo(hostPath).Length;
        long crossLength = new FileInfo(crossPath).Length;
        Assert.True(hostLength > 0, "Host binary is empty.");
        Assert.True(crossLength > 0, "Cross binary is empty.");
        Assert.NotEqual(hostLength, crossLength);

        // The cross output is genuinely a different target: ELF magic, not a copy of
        // the host PE ("MZ") binary.
        byte[] magic = new byte[4];
        using (var stream = File.OpenRead(crossPath))
        {
            Assert.Equal(4, stream.Read(magic, 0, 4));
        }
        Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, magic);
    }
}
