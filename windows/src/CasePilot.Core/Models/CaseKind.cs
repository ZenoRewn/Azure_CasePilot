namespace CasePilot.Core.Models;

public enum CaseKind { Technical, Quota, Billing }

public static class CaseKindExtensions
{
    public static string Label(this CaseKind k) => k switch
    {
        CaseKind.Quota => "配额申请",
        CaseKind.Billing => "计费 / 订阅",
        _ => "技术问题",
    };

    /// <summary>非技术类按服务名自动匹配.</summary>
    public static bool Matches(this CaseKind k, SupportService s)
    {
        var n = s.DisplayName.ToLowerInvariant();
        return k switch
        {
            CaseKind.Quota => n.Contains("service and subscription limits"),
            CaseKind.Billing => n == "billing",
            _ => false,
        };
    }

    public static CaseKind From(SupportService? s) =>
        s is null ? CaseKind.Technical
        : CaseKind.Quota.Matches(s) ? CaseKind.Quota
        : CaseKind.Billing.Matches(s) ? CaseKind.Billing
        : CaseKind.Technical;
}
