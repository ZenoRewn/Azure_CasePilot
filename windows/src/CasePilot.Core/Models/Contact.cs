using System.Text.RegularExpressions;

namespace CasePilot.Core.Models;

public sealed partial record Contact
{
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Email { get; init; } = "";
    public string Cc { get; init; } = "";
    public string Phone { get; init; } = "";
    public bool PreferPhone { get; init; }
    public string Language { get; init; } = "zh-hans";
    public string Country { get; init; } = "CHN";
    public string TimeZone { get; init; } = "China Standard Time";

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")]
    private static partial Regex EmailRegex();

    public bool IsValid =>
        FirstName.Length > 0 && LastName.Length > 0 && EmailRegex().IsMatch(Email)
        && (!PreferPhone || Phone.Length > 0);

    public IReadOnlyList<string> CcList =>
        Cc.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("zh-hans", "简体中文"), ("en-us", "English"), ("zh-hant", "繁體中文"), ("ja-jp", "日本語"), ("ko-kr", "한국어"),
    ];

    public static IReadOnlyList<(string Code, string Name)> Countries { get; } =
    [
        ("CHN", "中国"), ("HKG", "中国香港"), ("MAC", "中国澳门"), ("TWN", "中国台湾"), ("SGP", "新加坡"),
        ("JPN", "日本"), ("KOR", "韩国"), ("USA", "美国"), ("GBR", "英国"), ("DEU", "德国"), ("AUS", "澳大利亚"),
    ];

    public static IReadOnlyList<string> TimeZones { get; } =
    [
        "China Standard Time", "Taipei Standard Time", "Tokyo Standard Time", "Korea Standard Time",
        "Singapore Standard Time", "India Standard Time", "AUS Eastern Standard Time", "GMT Standard Time",
        "W. Europe Standard Time", "Eastern Standard Time", "Central Standard Time", "Pacific Standard Time", "UTC",
    ];
}
