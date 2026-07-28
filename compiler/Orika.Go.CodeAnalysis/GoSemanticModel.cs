using Orika.Go.CodeAnalysis.Internal;

namespace Orika.Go.CodeAnalysis;

/// <summary>
/// 模組的語意模型，透過 <c>orika-goc symbol</c>（go/types 的 <c>Info.Uses</c>／<c>Info.Defs</c>）
/// 提供符號查詢。
/// </summary>
public sealed class GoSemanticModel
{
    private readonly string _moduleDirectory;

    internal GoSemanticModel(string moduleDirectory)
    {
        _moduleDirectory = moduleDirectory;
    }

    /// <summary>
    /// 查詢指定檔案位置上的符號。
    /// </summary>
    /// <param name="file">.go 檔路徑（必須屬於此編譯的模組目錄）。</param>
    /// <param name="line">1-based 行號。</param>
    /// <param name="col">1-based 欄號。</param>
    /// <returns>
    /// 該位置的符號資訊；該位置沒有符號（orika-goc 回傳 <c>{"name":null}</c>）時為 <see langword="null"/>。
    /// </returns>
    /// <exception cref="OrikaGocNotFoundException">找不到 orika-goc sidecar。</exception>
    /// <exception cref="InvalidOperationException">sidecar 發生基礎架構錯誤（非零結束代碼或輸出無法解析）。</exception>
    public GoSymbolInfo? GetSymbolAt(string file, int line, int col)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);

        var toolPath = OrikaGoToolResolver.Resolve();
        var stdout = GocInvoker.Invoke(toolPath, new[]
        {
            "symbol",
            file,
            line.ToString(System.Globalization.CultureInfo.InvariantCulture),
            col.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-dir",
            _moduleDirectory,
        });

        var dto = Protocol.Deserialize<SymbolResultDto>(stdout, "orika-goc symbol");

        if (dto.Name is null)
        {
            return null;
        }

        GoLocation? declaredAt = null;
        if (dto.DeclaredAt is { } at)
        {
            declaredAt = new GoLocation(at.Line, at.Col, 0, at.File);
        }

        return new GoSymbolInfo(dto.Name, dto.Kind ?? string.Empty, dto.Type ?? string.Empty, declaredAt);
    }
}
