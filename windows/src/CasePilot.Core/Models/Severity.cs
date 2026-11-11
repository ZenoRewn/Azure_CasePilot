namespace CasePilot.Core.Models;

public enum Severity { Minimal, Moderate, Critical, HighestCriticalImpact }

public static class SeverityExtensions
{
    public static Severity? Parse(string? api) => (api ?? "").ToLowerInvariant() switch
    {
        "minimal" => Severity.Minimal,
        "moderate" => Severity.Moderate,
        "critical" => Severity.Critical,
        "highestcriticalimpact" => Severity.HighestCriticalImpact,
        _ => null,
    };

    public static string ApiValue(this Severity s) => s.ToString().ToLowerInvariant();

    public static string Level(this Severity s) => s switch
    {
        Severity.Minimal => "C",
        Severity.Moderate => "B",
        Severity.Critical => "A",
        _ => "A+",
    };

    public static string Title(this Severity s) => s switch
    {
        Severity.Minimal => "最小影响",
        Severity.Moderate => "中等影响",
        Severity.Critical => "严重影响",
        _ => "业务关键",
    };

    public static string Detail(this Severity s) => s switch
    {
        Severity.Minimal => "功能受损但不影响业务",
        Severity.Moderate => "生产受影响, 有变通方案",
        Severity.Critical => "生产中断 · 需 Standard+",
        _ => "全面中断 · 需 ProDirect/Unified",
    };

    public static bool IsHigh(this Severity s) => s is Severity.Critical or Severity.HighestCriticalImpact;
}
