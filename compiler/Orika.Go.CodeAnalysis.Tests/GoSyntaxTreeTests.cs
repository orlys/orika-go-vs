using Xunit;

namespace Orika.Go.CodeAnalysis.Tests;

public class GoSyntaxTreeTests
{
    public GoSyntaxTreeTests() => Sidecar.EnsureConfigured();

    // Built via escapes so the byte layout (LF line endings, tab indentation) is exact —
    // the position assertions below were verified against go/parser (go1.21) for these bytes.
    //
    //  line 1: package main
    //  line 2:
    //  line 3: import "fmt"
    //  line 4:
    //  line 5: func main() {
    //  line 6: \tfmt.Println("hello")
    //  line 7: }
    private const string HelloSource =
        "package main\n" +
        "\n" +
        "import \"fmt\"\n" +
        "\n" +
        "func main() {\n" +
        "\tfmt.Println(\"hello\")\n" +
        "}\n";

    [Fact]
    public void ParseText_ValidProgram_RootIsFile()
    {
        var tree = GoSyntaxTree.ParseText(HelloSource, "hello.go");

        Assert.NotNull(tree.Root);
        Assert.Equal("File", tree.Root.Kind);
        Assert.NotEmpty(tree.Root.Children);
        Assert.Empty(tree.GetDiagnostics());
    }

    [Fact]
    public void ParseText_ValidProgram_ContainsFuncDeclNamedMain()
    {
        var tree = GoSyntaxTree.ParseText(HelloSource, "hello.go");

        Assert.NotNull(tree.Root);
        var funcDecl = Assert.Single(
            tree.Root!.DescendantNodes(), n => n.Kind == "FuncDecl");

        var nameIdent = funcDecl.FirstChild("Ident");
        Assert.NotNull(nameIdent);
        Assert.Equal("main", nameIdent!.Text);
    }

    [Fact]
    public void ParseText_ValidProgram_FuncKeywordPositionMatchesSource()
    {
        var tree = GoSyntaxTree.ParseText(HelloSource, "hello.go");

        Assert.NotNull(tree.Root);
        var funcDecl = Assert.Single(
            tree.Root!.DescendantNodes(), n => n.Kind == "FuncDecl");

        // ast.FuncDecl.Pos() is the `func` keyword: line 5, column 1 (1-based).
        Assert.Equal(5, funcDecl.Start.Line);
        Assert.Equal(1, funcDecl.Start.Column);

        // FuncDecl.End() is just past the closing brace `}` on line 7.
        Assert.Equal(7, funcDecl.End.Line);
        Assert.Equal(2, funcDecl.End.Column);
    }

    [Fact]
    public void ParseText_ValidProgram_AstIsDeepNotShallow()
    {
        // A stub that returns only a top-level File node (or File + decls) passes the
        // shape tests above; requiring interior expression nodes and token text rules
        // that out.
        var tree = GoSyntaxTree.ParseText(HelloSource, "hello.go");
        Assert.NotNull(tree.Root);
        var descendants = tree.Root!.DescendantNodes().ToList();

        Assert.Contains(descendants, n => n.Kind == "CallExpr");
        Assert.Contains(descendants, n => n.Kind == "BlockStmt");
        Assert.Contains(descendants, n => n.Kind == "Ident" && n.Text == "fmt");
        Assert.Contains(descendants, n => n.Kind == "Ident" && n.Text == "Println");
        Assert.Contains(descendants, n => n.Kind == "BasicLit" && n.Text == "\"hello\"");
    }

    [Fact]
    public void ParseText_SyntaxError_DiagnosticAtLine2WithExactColumn()
    {
        // line 1: package main
        // line 2: func {          <- go/parser (AllErrors) reports the first error at
        //                            line 2, col 6: expected 'IDENT', found '{'
        var tree = GoSyntaxTree.ParseText("package main\nfunc {", "broken.go");

        var diagnostics = tree.GetDiagnostics();
        Assert.NotEmpty(diagnostics);

        Assert.All(diagnostics, d => Assert.Equal("GOPARSE", d.Id));
        Assert.All(diagnostics, d => Assert.Equal(GoDiagnosticSeverity.Error, d.Severity));

        Assert.Contains(diagnostics, d =>
            d.Location.Line == 2 &&
            d.Location.Column == 6 &&
            d.Message.Contains("expected", StringComparison.OrdinalIgnoreCase) &&
            d.Message.Contains("IDENT"));
    }

    [Fact]
    public void ParseText_FilePath_IsPreserved()
    {
        var tree = GoSyntaxTree.ParseText(HelloSource, "hello.go");
        Assert.Equal("hello.go", tree.FilePath);
    }
}
