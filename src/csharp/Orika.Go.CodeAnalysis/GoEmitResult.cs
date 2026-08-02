namespace Orika.Go.CodeAnalysis;

/// <summary>
/// <see cref="GoCompilation.Emit"/>（實際執行 <c>go build -o</c>）的結果。
/// </summary>
public sealed class GoEmitResult
{
    /// <summary>
    /// 建立建置結果。
    /// </summary>
    /// <param name="success">建置是否成功（<c>go build</c> 結束代碼為 0）。</param>
    /// <param name="diagnostics">建置期間產生的診斷（識別碼 <c>GOBUILD</c>）。</param>
    /// <param name="outputPath">要求的輸出檔路徑。</param>
    public GoEmitResult(bool success, IReadOnlyList<GoDiagnostic> diagnostics, string outputPath)
    {
        Success = success;
        Diagnostics = diagnostics;
        OutputPath = outputPath;
    }

    /// <summary>取得建置是否成功。</summary>
    public bool Success { get; }

    /// <summary>取得建置期間產生的診斷（識別碼 <c>GOBUILD</c>）；成功時通常為空清單。</summary>
    public IReadOnlyList<GoDiagnostic> Diagnostics { get; }

    /// <summary>取得要求的輸出檔路徑（<c>go build -o</c> 的引數）。</summary>
    public string OutputPath { get; }
}
