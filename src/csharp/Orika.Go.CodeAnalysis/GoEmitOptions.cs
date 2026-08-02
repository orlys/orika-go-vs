namespace Orika.Go.CodeAnalysis;

/// <summary>
/// <see cref="GoCompilation.Emit"/> 的建置選項，對應 <c>go build</c> 的環境變數與旗標。
/// <para>
/// 繼承自 <see cref="GoAnalysisOptions"/>：<see cref="GoAnalysisOptions.OS"/>、
/// <see cref="GoAnalysisOptions.Arch"/> 與 <see cref="GoAnalysisOptions.Tags"/> 定義於基底型別，
/// 因此同一個 <see cref="GoEmitOptions"/> 實體可以同時交給
/// <see cref="GoCompilation.GetDiagnostics(GoAnalysisOptions?)"/> 與
/// <see cref="GoCompilation.Emit(string, GoEmitOptions?)"/>，保證檢查與建置看到相同的檔案集合。
/// </para>
/// </summary>
public sealed class GoEmitOptions : GoAnalysisOptions
{
    /// <summary>
    /// 取得或設定是否在產出的二進位檔中移除本機檔案系統路徑（<c>go build -trimpath</c>）。
    /// 此選項只影響建置產出，不影響分析。
    /// </summary>
    public bool TrimPath { get; set; }
}
