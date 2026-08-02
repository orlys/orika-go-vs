using Xunit;

namespace Orika.Go.CodeAnalysis.Tests;

/// <summary>
/// Columns crossing the sidecar boundary are UTF-16 code units, not bytes.
///
/// go/token counts a Position.Column in bytes within the line, while .NET strings,
/// Visual Studio and LSP count UTF-16 code units. On a line containing non-ASCII text
/// the two disagree, so an editor asking about an identifier after 你好 used to miss it
/// entirely. These tests use a source where the difference is unmistakable and assert
/// the .NET-side numbers — the ones a caller can compute from its own string.
/// </summary>
public class GoColumnUnitTests
{
    public GoColumnUnitTests() => Sidecar.EnsureConfigured();

    //  line  1: package main
    //  line  2:
    //  line  3: import "fmt"
    //  line  4:
    //  line  5: type T struct{ 名前 string; count int }
    //  line  6:
    //  line  7: func main() {
    //  line  8: \tvar t T
    //  line  9: \tvalue := 42
    //  line 10: \tfmt.Println("你好世界", value, t.count)
    //  line 11: }
    //
    // Line 10 is where bytes and UTF-16 units part ways: 你好世界 is 12 bytes but 4
    // UTF-16 units, so `value` sits at byte column 30 / UTF-16 column 22 and `count`
    // at byte column 39 / UTF-16 column 31.
    // Line 5 does the same for a declaration site: 名前 is 6 bytes / 2 UTF-16 units,
    // so the `count` field is declared at byte column 31 / UTF-16 column 27.
    private const string NonAsciiSource =
        "package main\n" +
        "\n" +
        "import \"fmt\"\n" +
        "\n" +
        "type T struct{ 名前 string; count int }\n" +
        "\n" +
        "func main() {\n" +
        "\tvar t T\n" +
        "\tvalue := 42\n" +
        "\tfmt.Println(\"你好世界\", value, t.count)\n" +
        "}\n";

    /// <summary>
    /// The 1-based UTF-16 column of the first occurrence of <paramref name="token"/> on
    /// the given 1-based line — i.e. exactly what an editor working on the .NET string
    /// would report, computed independently of the sidecar.
    /// </summary>
    private static int Utf16ColumnOf(string source, int line, string token)
    {
        string text = source.Split('\n')[line - 1];
        int index = text.IndexOf(token, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{token}' not found on line {line}: {text}");
        return index + 1;
    }

    [Fact]
    public void Utf16ColumnsInFixtureDifferFromByteColumns()
    {
        // Guards the fixture itself: if these two agreed the tests below would prove
        // nothing. (Byte columns come from UTF-8 encoding the prefix of the line.)
        string line10 = NonAsciiSource.Split('\n')[9];
        int utf16Col = Utf16ColumnOf(NonAsciiSource, 10, "value");
        int byteCol = System.Text.Encoding.UTF8.GetByteCount(line10[..(utf16Col - 1)]) + 1;

        Assert.Equal(22, utf16Col);
        Assert.Equal(30, byteCol);
        Assert.NotEqual(utf16Col, byteCol);
    }

    [Fact]
    public void GetSymbolAt_VariableAfterNonAsciiTextOnSameLine_ResolvesAtUtf16Column()
    {
        using var module = new TempGoModule();
        string mainGo = module.WriteFile("main.go", NonAsciiSource);

        var model = GoCompilation.Create(module.Directory).GetSemanticModel();

        int utf16Col = Utf16ColumnOf(NonAsciiSource, 10, "value"); // 22
        var symbol = model.GetSymbolAt(mainGo, 10, utf16Col);

        Assert.NotNull(symbol);
        Assert.Equal("value", symbol!.Name);
        Assert.Equal("var", symbol.Kind);
        Assert.Equal("int", symbol.Type);
        Assert.NotNull(symbol.DeclaredAt);
        Assert.Equal(9, symbol.DeclaredAt!.Line);
        Assert.Equal(2, symbol.DeclaredAt.Column);
    }

    [Fact]
    public void GetSymbolAt_ByteColumnOfSameIdentifier_DoesNotResolve()
    {
        // The old, byte-column behaviour: column 30 was `value`. It must not be now —
        // otherwise the protocol would be ambiguous rather than fixed.
        using var module = new TempGoModule();
        string mainGo = module.WriteFile("main.go", NonAsciiSource);

        var model = GoCompilation.Create(module.Directory).GetSemanticModel();

        Assert.Null(model.GetSymbolAt(mainGo, 10, 30));
    }

    [Fact]
    public void GetSymbolAt_FieldDeclaredAfterNonAsciiField_ReportsUtf16DeclarationColumn()
    {
        using var module = new TempGoModule();
        string mainGo = module.WriteFile("main.go", NonAsciiSource);

        var model = GoCompilation.Create(module.Directory).GetSemanticModel();

        int useCol = Utf16ColumnOf(NonAsciiSource, 10, "t.count") + 2; // the `count` part: 31
        var symbol = model.GetSymbolAt(mainGo, 10, useCol);

        Assert.NotNull(symbol);
        Assert.Equal("count", symbol!.Name);
        Assert.Equal("field", symbol.Kind);
        Assert.Equal("int", symbol.Type);

        // Declaration site is on line 5, after the non-ASCII field name 名前: the
        // reported column must be the UTF-16 one (27), not the byte one (31).
        Assert.NotNull(symbol.DeclaredAt);
        Assert.Equal(5, symbol.DeclaredAt!.Line);
        Assert.Equal(Utf16ColumnOf(NonAsciiSource, 5, "count"), symbol.DeclaredAt.Column);
        Assert.Equal(27, symbol.DeclaredAt.Column);
    }

    //  line 1: package main
    //  line 2:
    //  line 3: func main() {
    //  line 4: \tvar s, n = "你好世界", "x" + 1
    //  line 5: \t_, _ = s, n
    //  line 6: }
    //
    // The mismatched-types error is reported at the `"x"` operand: byte column 29,
    // UTF-16 column 21.
    private const string NonAsciiTypeErrorSource =
        "package main\n" +
        "\n" +
        "func main() {\n" +
        "\tvar s, n = \"你好世界\", \"x\" + 1\n" +
        "\t_, _ = s, n\n" +
        "}\n";

    [Fact]
    public void GetDiagnostics_ErrorAfterNonAsciiTextOnSameLine_ReportsUtf16Column()
    {
        using var module = new TempGoModule();
        module.WriteFile("main.go", NonAsciiTypeErrorSource);

        var diagnostics = GoCompilation.Create(module.Directory).GetDiagnostics();

        var error = Assert.Single(diagnostics);
        Assert.Equal("GOTYPE", error.Id);
        Assert.Equal(4, error.Location.Line);
        Assert.Equal(Utf16ColumnOf(NonAsciiTypeErrorSource, 4, "\"x\""), error.Location.Column);
        Assert.Equal(21, error.Location.Column);
    }

    [Fact]
    public void ParseText_PositionsAfterNonAsciiText_AreUtf16Columns()
    {
        // The parse protocol carries columns too; they use the same unit.
        //  line 1: package main
        //  line 2:
        //  line 3: var x = "你好世界" + y
        const string source = "package main\n\nvar x = \"你好世界\" + y\n";

        var tree = GoSyntaxTree.ParseText(source, "utf16.go");

        Assert.NotNull(tree.Root);
        var y = Assert.Single(
            tree.Root!.DescendantNodes(), n => n.Kind == "Ident" && n.Text == "y");

        // `y` is at UTF-16 column 18 but byte column 26.
        Assert.Equal(3, y.Start.Line);
        Assert.Equal(18, y.Start.Column);
        Assert.Equal(Utf16ColumnOf(source, 3, "+ y") + 2, y.Start.Column);

        // Offsets remain byte offsets, deliberately — they are documented as such.
        int lineStart = System.Text.Encoding.UTF8.GetByteCount("package main\n\n");
        int byteColumn = System.Text.Encoding.UTF8.GetByteCount("var x = \"你好世界\" + ") + 1;
        Assert.Equal(26, byteColumn);
        Assert.Equal(lineStart + byteColumn - 1, y.Start.Offset);
    }
}
