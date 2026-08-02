using System.Text.Json;

namespace Orika.Go.CodeAnalysis.Internal;

/// <summary>
/// orika-goc JSON 協定的傳輸物件與共用序列化設定（內部使用）。
/// 屬性名稱比對不分大小寫，與協定的小寫欄位（kind、pos、line…）相容。
/// </summary>
internal static class Protocol
{
    /// <summary>共用的 <see cref="JsonSerializerOptions"/>：不分大小寫。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static T Deserialize<T>(string json, string toolDescription)
    {
        T? result;
        try
        {
            result = JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{toolDescription} 輸出的 JSON 無法解析：{ex.Message}{Environment.NewLine}原始輸出：{Truncate(json)}", ex);
        }

        if (result is null)
        {
            throw new InvalidOperationException(
                $"{toolDescription} 輸出了空的 JSON（null）。原始輸出：{Truncate(json)}");
        }

        return result;
    }

    private static string Truncate(string s)
        => s.Length <= 2000 ? s : s[..2000] + "…(截斷)";
}

/// <summary>協定中的位置物件：{"line":L,"col":C,"offset":O}。</summary>
internal sealed class PositionDto
{
    public int Line { get; set; }

    public int Col { get; set; }

    public int Offset { get; set; }
}

/// <summary>協定中的 AST 節點。</summary>
internal sealed class AstNodeDto
{
    public string Kind { get; set; } = string.Empty;

    public PositionDto? Pos { get; set; }

    public PositionDto? End { get; set; }

    public string? Text { get; set; }

    public List<AstNodeDto>? Children { get; set; }
}

/// <summary>parse 指令的剖析錯誤：{"line":L,"col":C,"msg":"..."}。</summary>
internal sealed class ParseErrorDto
{
    public int Line { get; set; }

    public int Col { get; set; }

    public string Msg { get; set; } = string.Empty;
}

/// <summary>parse 指令的外層包裝：{"ast":&lt;root or null&gt;,"errors":[...]}。</summary>
internal sealed class ParseResultDto
{
    public AstNodeDto? Ast { get; set; }

    public List<ParseErrorDto>? Errors { get; set; }
}

/// <summary>check 指令的單筆診斷。</summary>
internal sealed class CheckDiagnosticDto
{
    public string? File { get; set; }

    public int Line { get; set; }

    public int Col { get; set; }

    public string? Severity { get; set; }

    public string Msg { get; set; } = string.Empty;
}

/// <summary>check 指令的外層包裝：{"diagnostics":[...]}。</summary>
internal sealed class CheckResultDto
{
    public List<CheckDiagnosticDto>? Diagnostics { get; set; }
}

/// <summary>symbol 指令中 declaredAt 的位置。</summary>
internal sealed class DeclaredAtDto
{
    public string? File { get; set; }

    public int Line { get; set; }

    public int Col { get; set; }
}

/// <summary>symbol 指令的回應；查無符號時 name 為 null。</summary>
internal sealed class SymbolResultDto
{
    public string? Name { get; set; }

    public string? Kind { get; set; }

    public string? Type { get; set; }

    public DeclaredAtDto? DeclaredAt { get; set; }
}
