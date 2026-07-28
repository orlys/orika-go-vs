namespace Orika.Go.CodeAnalysis;

/// <summary>
/// 表示 Go 原始碼中的一個位置（1-based 行／欄，以及選擇性的位元組位移與檔案路徑）。
/// </summary>
public sealed class GoLocation
{
    /// <summary>
    /// 建立一個新的 <see cref="GoLocation"/>。
    /// </summary>
    /// <param name="line">1-based 行號。</param>
    /// <param name="column">1-based 欄號。</param>
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

    /// <summary>取得 1-based 欄號。</summary>
    public int Column { get; }

    /// <summary>取得自檔案開頭起算的位元組位移；來源未提供時為 0。</summary>
    public int Offset { get; }

    /// <summary>取得位置所屬的檔案路徑；來源未提供時為 <see langword="null"/>。</summary>
    public string? File { get; }

    /// <summary>以 <c>file(line,col)</c> 形式傳回位置的文字表示。</summary>
    public override string ToString()
        => File is null ? $"({Line},{Column})" : $"{File}({Line},{Column})";
}
