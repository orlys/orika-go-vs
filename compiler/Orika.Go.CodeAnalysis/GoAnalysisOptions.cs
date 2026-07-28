namespace Orika.Go.CodeAnalysis;

/// <summary>
/// 分析用的建置內容選項：決定「哪些檔案屬於這次分析」。
/// <para>
/// Go 以 <c>GOOS</c>／<c>GOARCH</c> 與建置標籤（<c>//go:build</c>）決定套件包含哪些原始檔，
/// 因此型別檢查與建置若使用不同的建置內容，檢查到的檔案集合就會與實際編譯的不同
/// （例如 <c>//go:build linux</c> 的檔案在 Windows 上預設完全看不到）。
/// 將同一組選項同時交給 <see cref="GoCompilation.GetDiagnostics(GoAnalysisOptions?)"/>／
/// <see cref="GoCompilation.GetSemanticModel(GoAnalysisOptions?)"/> 與
/// <see cref="GoCompilation.Emit(string, GoEmitOptions?)"/>，即可確保兩者一致。
/// </para>
/// <para>
/// <see cref="GoEmitOptions"/> 由此型別衍生，所以建置選項物件可以直接拿來做分析。
/// </para>
/// </summary>
public class GoAnalysisOptions
{
    /// <summary>
    /// 取得或設定目標作業系統（<c>GOOS</c>，例如 <c>windows</c>、<c>linux</c>、<c>darwin</c>）。
    /// <see langword="null"/> 或空字串表示沿用目前環境。
    /// </summary>
    public string? OS { get; set; }

    /// <summary>
    /// 取得或設定目標架構（<c>GOARCH</c>，例如 <c>amd64</c>、<c>arm64</c>）。
    /// <see langword="null"/> 或空字串表示沿用目前環境。
    /// </summary>
    public string? Arch { get; set; }

    /// <summary>
    /// 取得或設定建置標籤（對應 <c>go build -tags</c>）。
    /// <see langword="null"/> 或空清單表示不指定標籤。
    /// </summary>
    public IList<string> Tags { get; set; } = new List<string>();

    /// <summary>
    /// 將此選項轉為 orika-goc 的 <c>-tags</c>／<c>-goos</c>／<c>-goarch</c> 引數，附加到 <paramref name="arguments"/>。
    /// </summary>
    internal void AppendSidecarArguments(IList<string> arguments)
    {
        if (Tags is { Count: > 0 })
        {
            arguments.Add("-tags");
            arguments.Add(string.Join(',', Tags));
        }

        if (!string.IsNullOrEmpty(OS))
        {
            arguments.Add("-goos");
            arguments.Add(OS);
        }

        if (!string.IsNullOrEmpty(Arch))
        {
            arguments.Add("-goarch");
            arguments.Add(Arch);
        }
    }
}
