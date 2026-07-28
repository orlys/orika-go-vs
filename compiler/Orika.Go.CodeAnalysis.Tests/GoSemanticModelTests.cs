using Xunit;

namespace Orika.Go.CodeAnalysis.Tests;

public class GoSemanticModelTests
{
    public GoSemanticModelTests() => Sidecar.EnsureConfigured();

    // Four-space indentation, LF endings — positions verified with go/types (go1.21):
    //  line 1: package main
    //  line 2:
    //  line 3: import "fmt"
    //  line 4:
    //  line 5: func main() {
    //  line 6:     greeting := "hi"          <- declaration: line 6, col 5
    //  line 7:     fmt.Println(greeting)     <- use site: `greeting` starts at col 17
    //  line 8: }
    private const string SymbolSource =
        "package main\n" +
        "\n" +
        "import \"fmt\"\n" +
        "\n" +
        "func main() {\n" +
        "    greeting := \"hi\"\n" +
        "    fmt.Println(greeting)\n" +
        "}\n";

    [Fact]
    public void GetSymbolAt_LocalVariableUse_ReturnsVarKindWithDeclarationSite()
    {
        using var module = new TempGoModule();
        string mainGo = module.WriteFile("main.go", SymbolSource);

        var model = GoCompilation.Create(module.Directory).GetSemanticModel();
        var symbol = model.GetSymbolAt(mainGo, 7, 17);

        Assert.NotNull(symbol);
        Assert.Equal("greeting", symbol.Name);
        Assert.Equal("var", symbol.Kind);
        Assert.Equal("string", symbol.Type);
        Assert.NotNull(symbol.DeclaredAt);
        Assert.Equal(6, symbol.DeclaredAt!.Line);
        Assert.Equal(5, symbol.DeclaredAt.Column);
    }
}
