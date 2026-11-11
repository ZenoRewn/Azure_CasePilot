using CasePilot.Core.Models;

namespace CasePilot.Core.Plan;

/// <summary>
/// 支持计划识别. Azure 没有查询支持计划的公开 ARM API, 因此综合两个信号并标注来源:
/// ticket (最近 Case 的 supportPlanType) 与 offer (订阅 quotaId, 仅能识别无付费支持的订阅).
/// </summary>
/// <param name="Source">ticket | offer | none</param>
public sealed record SupportPlan(string? Name, string Tier, string Source, bool? AllowsCritical)
{
    public static SupportPlan Unknown { get; } = new(null, "unknown", "none", null);

    private static readonly (string Key, string Tier)[] Tiers =
    [
        ("unified", "unified"), ("premier", "premier"), ("prodirect", "prodirect"), ("professional direct", "prodirect"),
        ("internal", "internal"), ("standard", "standard"), ("developer", "developer-or-basic"), ("basic", "developer-or-basic"),
    ];

    private static readonly HashSet<string> High = ["unified", "premier", "prodirect", "internal", "standard"];

    private static readonly string[] NoPaid = ["MSDN", "FreeTrial", "AzurePass", "Sponsored", "Students", "DreamSpark", "AzureForStudents"];

    public static string TierOf(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        foreach (var (k, t) in Tiers) if (n.Contains(k)) return t;
        return "unknown";
    }

    public static SupportPlan Infer(IEnumerable<TicketSummary> tickets, string? quotaId)
    {
        var p = tickets.Where(t => t.Plan is not null)
            .OrderByDescending(t => t.Created ?? DateTimeOffset.MinValue)
            .Select(t => t.Plan).FirstOrDefault();
        if (p is not null)
        {
            var t = TierOf(p);
            return new SupportPlan(p, t, "ticket", t == "unknown" ? null : High.Contains(t));
        }
        var q = quotaId ?? "";
        if (NoPaid.Any(o => q.StartsWith(o, StringComparison.Ordinal)))
            return new SupportPlan("Developer / Basic (按订阅类型推断)", "developer-or-basic", "offer", false);
        return Unknown;
    }

    public string SourceLabel => Source switch
    {
        "ticket" => "根据该订阅最近的 Case 识别",
        "offer" => "根据订阅类型推断",
        _ => "",
    };
}
