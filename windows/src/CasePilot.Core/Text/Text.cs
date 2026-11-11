using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CasePilot.Core.Text;

public static partial class HtmlText
{
    [GeneratedRegex(@"<(style|script|head)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Blocks();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Br();
    [GeneratedRegex(@"</(p|div|li|tr|h[1-6])>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
    [GeneratedRegex(@"[ \t\u00a0]+\n")]
    private static partial Regex TrailingSpace();
    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();

    private static readonly (string, string)[] Entities =
        [("&nbsp;", " "), ("&lt;", "<"), ("&gt;", ">"), ("&quot;", "\""), ("&#39;", "'"), ("&amp;", "&")];

    /// <summary>邮件 HTML 转纯文本 (从不渲染外部 HTML).</summary>
    public static string Plain(string html)
    {
        var s = Blocks().Replace(html, "");
        s = Br().Replace(s, "\n");
        s = BlockEnd().Replace(s, "\n");
        s = Tag().Replace(s, "");
        foreach (var (e, c) in Entities) s = s.Replace(e, c);
        s = TrailingSpace().Replace(s, "\n");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }
}

public static class ReplyText
{
    /// <summary>回复正文: 纯文本 (Azure 自动包 &lt;pre&gt; 保留换行), 统一换行并去首尾空白.</summary>
    public static string Body(string text) => text.Replace("\r\n", "\n").Trim();
}

public static class Jwt
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>();

    /// <summary>解析 JWT 载荷 (不验签, 仅用于读取 tid / upn).</summary>
    public static IReadOnlyDictionary<string, JsonElement> Claims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return Empty;
        var s = parts[1].Replace('-', '+').Replace('_', '/');
        s += new string('=', (4 - s.Length % 4) % 4);
        try
        {
            var bytes = Convert.FromBase64String(s);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bytes) ?? Empty;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return Empty;
        }
    }

    public static string? Str(this IReadOnlyDictionary<string, JsonElement> claims, string key) =>
        claims.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public static class FormEncoding
{
    /// <summary>application/x-www-form-urlencoded; '+' 编码为 %2B, 空格为 %20.</summary>
    public static string Encode(IEnumerable<KeyValuePair<string, string>> fields)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in fields)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }
}
