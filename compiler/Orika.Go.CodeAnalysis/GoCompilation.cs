using System.Text.RegularExpressions;
using Orika.Go.CodeAnalysis.Internal;

namespace Orika.Go.CodeAnalysis;

/// <summary>
/// 表示一個 Go 模組的編譯：型別檢查透過 <c>orika-goc check</c>（go/types），
/// 產出二進位檔則呼叫真實的 <c>go build -o</c>。
/// </summary>
public sealed partial class GoCompilation
{
    private GoCompilation(string moduleDirectory)
    {
        ModuleDirectory = moduleDirectory;
    }

    /// <summary>取得此編譯對應的模組目錄（包含 go.mod 的目錄）。</summary>
    public string ModuleDirectory { get; }

    /// <summary>
    /// 為指定的模組目錄建立編譯。
    /// </summary>
    /// <param name="moduleDirectory">Go 模組目錄（包含 go.mod）。</param>
    /// <returns>新的 <see cref="GoCompilation"/>。</returns>
    /// <exception cref="DirectoryNotFoundException">目錄不存在時擲出。</exception>
    public static GoCompilation Create(string moduleDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleDirectory);

        var fullPath = Path.GetFullPath(moduleDirectory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"找不到模組目錄：{fullPath}");
        }

        return new GoCompilation(fullPath);
    }

    /// <summary>
    /// 對整個模組執行 go/types 型別檢查（<c>orika-goc check</c>）並傳回診斷。
    /// </summary>
    /// <returns>唯讀的診斷清單（識別碼 <c>GOTYPE</c>）；模組健全時為空清單。</returns>
    /// <exception cref="OrikaGocNotFoundException">找不到 orika-goc sidecar。</exception>
    /// <exception cref="InvalidOperationException">sidecar 發生基礎架構錯誤（非零結束代碼或輸出無法解析）。</exception>
    public IReadOnlyList<GoDiagnostic> GetDiagnostics()
    {
        var toolPath = OrikaGoToolResolver.Resolve();
        var stdout = GocInvoker.Invoke(toolPath, new[] { "check", ModuleDirectory });
        var dto = Protocol.Deserialize<CheckResultDto>(stdout, "orika-goc check");

        if (dto.Diagnostics is null || dto.Diagnostics.Count == 0)
        {
            return Array.Empty<GoDiagnostic>();
        }

        var diagnostics = new List<GoDiagnostic>(dto.Diagnostics.Count);
        foreach (var d in dto.Diagnostics)
        {
            diagnostics.Add(new GoDiagnostic(
                "GOTYPE",
                MapSeverity(d.Severity),
                new GoLocation(d.Line, d.Col, 0, d.File),
                d.Msg));
        }

        return diagnostics;
    }

    /// <summary>
    /// 呼叫真實的 <c>go build -o</c> 產出二進位檔。
    /// </summary>
    /// <param name="outputPath">輸出檔路徑（<c>go build -o</c> 的引數）。</param>
    /// <param name="options">
    /// 建置選項：<see cref="GoEmitOptions.OS"/>／<see cref="GoEmitOptions.Arch"/> 對應
    /// <c>GOOS</c>／<c>GOARCH</c> 環境變數，另支援 <c>-tags</c> 與 <c>-trimpath</c>。
    /// <see langword="null"/> 表示全部使用預設值。
    /// </param>
    /// <returns>
    /// 建置結果。建置錯誤（<c>go build</c> 結束代碼非零）不擲出例外，而是化為
    /// <see cref="GoEmitResult.Diagnostics"/> 中識別碼 <c>GOBUILD</c> 的診斷。
    /// </returns>
    /// <exception cref="InvalidOperationException">找不到 <c>go</c> 工具鏈或無法啟動時擲出。</exception>
    public GoEmitResult Emit(string outputPath, GoEmitOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var args = new List<string> { "build", "-o", Path.GetFullPath(outputPath) };

        if (options is not null)
        {
            if (options.TrimPath)
            {
                args.Add("-trimpath");
            }

            if (options.Tags is { Count: > 0 })
            {
                args.Add("-tags");
                args.Add(string.Join(',', options.Tags));
            }
        }

        args.Add(".");

        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(options?.OS))
        {
            environment["GOOS"] = options.OS;
        }

        if (!string.IsNullOrEmpty(options?.Arch))
        {
            environment["GOARCH"] = options.Arch;
        }

        ProcessResult result;
        try
        {
            result = ProcessRunner.Run(
                "go",
                args,
                workingDirectory: ModuleDirectory,
                environment: environment.Count > 0 ? environment : null);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "無法執行 go 工具鏈。請確認已安裝 Go 並將其加入 PATH（https://go.dev/dl/）。", ex);
        }

        var diagnostics = ParseGoBuildOutput(result.StandardError);
        var success = result.ExitCode == 0;

        // 建置失敗但 stderr 無法解析成逐行診斷時，保留整段輸出當作單筆診斷，不吞掉錯誤。
        if (!success && diagnostics.Count == 0)
        {
            diagnostics.Add(new GoDiagnostic(
                "GOBUILD",
                GoDiagnosticSeverity.Error,
                new GoLocation(0, 0, 0, ModuleDirectory),
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"go build 以結束代碼 {result.ExitCode} 失敗，且無 stderr 輸出。"
                    : result.StandardError.Trim()));
        }

        return new GoEmitResult(success, diagnostics, outputPath);
    }

    /// <summary>
    /// 取得此模組的語意模型（符號查詢由 <c>orika-goc symbol</c> 支援）。
    /// </summary>
    /// <returns>此模組的 <see cref="GoSemanticModel"/>。</returns>
    public GoSemanticModel GetSemanticModel() => new(ModuleDirectory);

    private static GoDiagnosticSeverity MapSeverity(string? severity)
        => severity?.ToLowerInvariant() switch
        {
            "warning" => GoDiagnosticSeverity.Warning,
            "info" => GoDiagnosticSeverity.Info,
            _ => GoDiagnosticSeverity.Error,
        };

    [GeneratedRegex(@"^(?<file>.+?\.go):(?<line>\d+):(?:(?<col>\d+):)?\s*(?<msg>.+)$")]
    private static partial Regex GoBuildErrorLine();

    private List<GoDiagnostic> ParseGoBuildOutput(string stderr)
    {
        var diagnostics = new List<GoDiagnostic>();
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return diagnostics;
        }

        foreach (var rawLine in stderr.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue; // 空行與「# package」群組標頭。
            }

            var match = GoBuildErrorLine().Match(line);
            if (!match.Success)
            {
                continue; // 縮排的補充說明行等，附屬於前一筆錯誤。
            }

            var file = match.Groups["file"].Value;
            if (!Path.IsPathRooted(file))
            {
                file = Path.GetFullPath(Path.Combine(ModuleDirectory, file));
            }

            var lineNo = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var colNo = match.Groups["col"].Success
                ? int.Parse(match.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0;

            diagnostics.Add(new GoDiagnostic(
                "GOBUILD",
                GoDiagnosticSeverity.Error,
                new GoLocation(lineNo, colNo, 0, file),
                match.Groups["msg"].Value));
        }

        return diagnostics;
    }
}
