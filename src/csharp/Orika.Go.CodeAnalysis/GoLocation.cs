namespace Orika.Go.CodeAnalysis;

/// <summary>
/// 表示 Go 原始碼中的一個位置（1-based 行／欄，以及選擇性的位元組位移與檔案路徑）。
/// <para>
/// <b>欄號的單位是 UTF-16 字碼單位</b>，與 .NET 的 <see cref="string"/> 索引、Visual Studio 及
/// LSP 相同；Go 本身（<c>go/token.Position.Column</c>）計算的是行內位元組數，兩者在含非 ASCII
/// 字元的行上並不相等。轉換由 orika-goc sidecar 在協定邊界完成，因此本組件公開的所有欄號
/// 一律為 UTF-16 單位。
/// </para>
/// <para>
/// <see cref="Offset"/> 則維持為<em>位元組</em>位移，未做轉換。
/// </para>
/// </summary>
/// <example>
/// 對於 <c>fmt.Println("你好", value)</c>（行首無縮排），<c>value</c> 的
/// <see cref="Column"/> 為 16（UTF-16），而 <c>go/token</c> 會報告位元組欄號 20。
/// </example>
public sealed class GoLocation
{
    /// <summary>
    /// 建立一個新的 <see cref="GoLocation"/>。
    /// </summary>
    /// <param name="line">1-based 行號。</param>
    /// <param name="column">1-based 欄號，單位為 UTF-16 字碼單位（非位元組）。</param>
    /// <param name="offset">自檔案開頭起算的位元組位移（不明時為 0）。</param>
    /// <param name="file">位置所屬的檔案路徑；不明時為 <see langword="null"/>。</param>
    public GoLocation(int line, int column, int offset = 0, string? file = null)
    {
        Line = line;
        Column = column;
        Offset = offset;
        File = file;
    }

    /// <summary>取得 1-based 行號。</summary>
    public int Line { get; }

    /// <summary>
    /// 取得 1-based 欄號，單位為 <b>UTF-16 字碼單位</b>（.NET／Visual Studio 的單位），
    /// 而非 <c>go/token</c> 的行內位元組數。
    /// </summary>
    public int Column { get; }

    /// <summary>取得自檔案開頭起算的<em>位元組</em>位移；來源未提供時為 0。</summary>
    public int Offset { get; }

    /// <summary>取得位置所屬的檔案路徑；來源未提供時為 <see langword="null"/>。</summary>
    public string? File { get; }

    /// <summary>以 <c>file(line,col)</c> 形式傳回位置的文字表示。</summary>
    public override string ToString()
        => File is null ? $"({Line},{Column})" : $"{File}({Line},{Column})";
}
