using CasePilot.Core.Models;

namespace CasePilot.Core.Support;

/// <summary>SupportClient 的可替换接口 (ViewModel 测试使用假实现).</summary>
public interface ISupportApi
{
    Task<IReadOnlyList<Tenant>> TenantsAsync(CancellationToken ct);
    Task<IReadOnlyList<Subscription>> SubscriptionsAsync(string tenant, CancellationToken ct);
    Task<string> QuotaIdAsync(string tenant, string sub, CancellationToken ct);
    Task<IReadOnlyList<SupportService>> ServicesAsync(string tenant, CancellationToken ct);
    Task<IReadOnlyList<ProblemClassification>> ProblemsAsync(string tenant, SupportService service, CancellationToken ct);
    Task<IReadOnlyList<AzureResource>> ResourcesAsync(string tenant, string sub, IReadOnlyList<string>? types, CancellationToken ct);
    Task<TicketSummary> CreateAsync(string tenant, string? sub, NewCase c, CancellationToken ct);
    Task<IReadOnlyList<TicketSummary>> TicketsAsync(string tenant, string? sub, int days, CancellationToken ct);
    Task<TicketDetail> DetailAsync(string tenant, string? sub, string name, CancellationToken ct);
    Task ReplyAsync(string tenant, string? sub, string name, string text, string? sender, CancellationToken ct);
    Task CloseAsync(string tenant, string? sub, string name, CancellationToken ct);
}
