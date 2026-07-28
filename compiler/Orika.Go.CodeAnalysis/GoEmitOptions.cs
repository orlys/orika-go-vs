namespace Orika.Go.CodeAnalysis;

/// <summary>
/// <see cref="GoCompilation.Emit"/> 的建置選項，對應 <c>go build</c> 的環境變數與旗標。
/// </summary>
public sealed class GoEmitOptions
{
    /// <summary>
    /// 取得或設定目標作業系統（<c>GOOS</c>，例如 <c>windows</c>、<c>linux</c>、<c>darwin</c>）。
    /// <see langword="null"/> 表示沿用目前環境。
    /// </summary>
    public string? OS { get; set; }

    /// <summary>
    /// 取得或設定目標架構（<c>GOARCH</c>，例如 <c>amd64</c>、<c>arm64</c>）。
    /// <see langword="null"/> 表示沿用目前環境。
    /// </summary>
    public string? Arch { get; set; }

    /// <summary>
    /// 取得或設定建置標籤（<c>go build -tags</c>）。<see langword="null"/> 或空清單表示不傳 -tags。
    /// </summary>
    public IList<string> Tags { get; set; } = new List<string>();

    /// <summary>
    /// 取得或設定是否在產出的二進位檔中移除本機檔案系統路徑（<c>go build -trimpath</c>）。
    /// </summary>
    public bool TrimPath { get; set; }
}
