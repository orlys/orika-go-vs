using Orika.Go.CodeAnalysis.Internal;

namespace Orika.Go.CodeAnalysis;

/// <summary>
/// 一份 Go 原始檔的語法樹，由 orika-goc sidecar（go/parser，
/// 模式 <c>ParseComments|AllErrors</c>）產生。
/// </summary>
public sealed class GoSyntaxTree
{
    private readonly IReadOnlyList<GoDiagnostic> _diagnostics;

    private GoSyntaxTree(GoSyntaxNode? root, string filePath, IReadOnlyList<GoDiagnostic> diagnostics)
    {
        Root = root;
        FilePath = filePath;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// 取得語法樹的根節點（<c>File</c>）。
    /// 僅在原始碼完全無法剖析（orika-goc 回傳 <c>"ast": null</c>）時為 <see langword="null"/>。
    /// </summary>
    public GoSyntaxNode? Root { get; }

    /// <summary>
    /// 取得此語法樹對應的檔案路徑。以 <see cref="ParseText"/> 剖析純文字且未指定路徑時為
    /// <c>&lt;source&gt;</c>。
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// 取得剖析期間收集到的診斷（識別碼 <c>GOPARSE</c>，嚴重性一律為錯誤）。
    /// </summary>
    /// <returns>唯讀的診斷清單；無錯誤時為空清單。</returns>
    public IReadOnlyList<GoDiagnostic> GetDiagnostics() => _diagnostics;

    /// <summary>
    /// 剖析一段 Go 原始碼文字。原始碼經由標準輸入送給
    /// <c>orika-goc parse -</c>，不落地暫存檔。
    /// </summary>
    /// <param name="source">Go 原始碼。</param>
    /// <param name="path">回報位置時使用的檔案路徑；空字串時 orika-goc 以 <c>&lt;source&gt;</c> 回報。</param>
    /// <returns>剖析結果語法樹（即使有語法錯誤也會傳回，錯誤見 <see cref="GetDiagnostics"/>）。</returns>
    /// <exception cref="OrikaGocNotFoundException">找不到 orika-goc sidecar。</exception>
    /// <exception cref="InvalidOperationException">sidecar 發生基礎架構錯誤（非零結束代碼或輸出無法解析）。</exception>
    public static GoSyntaxTree ParseText(string source, string path = "")
    {
        ArgumentNullException.ThrowIfNull(source);

        var toolPath = OrikaGoToolResolver.Resolve();
        var args = new List<string> { "parse", "-" };
        if (!string.IsNullOrEmpty(path))
        {
            args.Add("--path");
            args.Add(path);
        }

        var stdout = GocInvoker.Invoke(toolPath, args, stdin: source);
        var reportedPath = string.IsNullOrEmpty(path) ? "<source>" : path;
        return FromParseJson(stdout, reportedPath);
    }

    /// <summary>
    /// 剖析磁碟上的 Go 原始檔（<c>orika-goc parse &lt;file.go&gt;</c>）。
    /// </summary>
    /// <param name="path">要剖析的 .go 檔路徑。</param>
    /// <returns>剖析結果語法樹（即使有語法錯誤也會傳回，錯誤見 <see cref="GetDiagnostics"/>）。</returns>
    /// <exception cref="OrikaGocNotFoundException">找不到 orika-goc sidecar。</exception>
    /// <exception cref="InvalidOperationException">sidecar 發生基礎架構錯誤（非零結束代碼或輸出無法解析）。</exception>
    public static GoSyntaxTree ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var toolPath = OrikaGoToolResolver.Resolve();
        var stdout = GocInvoker.Invoke(toolPath, new[] { "parse", path });
        return FromParseJson(stdout, path);
    }

    private static GoSyntaxTree FromParseJson(string json, string filePath)
    {
        var dto = Protocol.Deserialize<ParseResultDto>(json, "orika-goc parse");

        var root = dto.Ast is null ? null : ToNode(dto.Ast, filePath);

        var diagnostics = new List<GoDiagnostic>();
        if (dto.Errors is not null)
        {
            foreach (var error in dto.Errors)
            {
                diagnostics.Add(new GoDiagnostic(
                    "GOPARSE",
                    GoDiagnosticSeverity.Error,
                    new GoLocation(error.Line, error.Col, 0, filePath),
                    error.Msg));
            }
        }

        return new GoSyntaxTree(root, filePath, diagnostics);
    }

    private static GoSyntaxNode ToNode(AstNodeDto dto, string filePath)
    {
        var children = dto.Children is { Count: > 0 }
            ? dto.Children.Select(c => ToNode(c, filePath)).ToArray()
            : Array.Empty<GoSyntaxNode>();

        return new GoSyntaxNode(
            dto.Kind,
            ToLocation(dto.Pos, filePath),
            ToLocation(dto.End, filePath),
            dto.Text ?? string.Empty,
            children);
    }

    private static GoLocation ToLocation(PositionDto? pos, string filePath)
        => pos is null
            ? new GoLocation(0, 0, 0, filePath)
            : new GoLocation(pos.Line, pos.Col, pos.Offset, filePath);
}
