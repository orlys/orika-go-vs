namespace Orika.Go.CodeAnalysis;

/// <summary>
/// 由 go/types 解析出的符號資訊（<c>orika-goc symbol</c> 的結果）。
/// </summary>
public sealed class GoSymbolInfo
{
    /// <summary>
    /// 建立符號資訊。
    /// </summary>
    /// <param name="name">符號名稱。</param>
    /// <param name="kind">符號種類（<c>var</c>、<c>func</c>、<c>type</c>、<c>package</c>、<c>const</c>…）。</param>
    /// <param name="type">符號的型別字串（go/types 的 <c>Type.String()</c>）。</param>
    /// <param name="declaredAt">符號宣告處的位置；不明時為 <see langword="null"/>。</param>
    public GoSymbolInfo(string name, string kind, string type, GoLocation? declaredAt)
    {
        Name = name;
        Kind = kind;
        Type = type;
        DeclaredAt = declaredAt;
    }

    /// <summary>取得符號名稱。</summary>
    public string Name { get; }

    /// <summary>取得符號種類（<c>var</c>、<c>func</c>、<c>type</c>、<c>package</c>、<c>const</c>…）。</summary>
    public string Kind { get; }

    /// <summary>取得符號的型別字串（go/types 的 <c>Type.String()</c>）。</summary>
    public string Type { get; }

    /// <summary>
    /// 取得符號宣告處的位置（檔案／行／欄，欄號為 UTF-16 字碼單位）；不明時為 <see langword="null"/>。
    /// </summary>
    public GoLocation? DeclaredAt { get; }

    /// <summary>以 <c>kind name: type</c> 形式傳回符號的文字表示。</summary>
    public override string ToString() => $"{Kind} {Name}: {Type}";
}
