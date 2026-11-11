using System.Text.Json.Nodes;

namespace CasePilot.Core.Models;

public sealed record Tenant(string Id, string Name, string Domain);

public sealed record Subscription(string Id, string Name, string State);

public sealed record QuotaRequest(string Region = "", string VmFamily = "", string NewLimit = "");

public sealed record NewCase
{
    public SupportService? Service { get; init; }
    public ProblemClassification? Problem { get; init; }
    public string? ResourceId { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public Severity Severity { get; init; } = Severity.Moderate;
    public bool Require24x7 { get; init; }
    public bool Consent { get; init; } = true;
    public DateTimeOffset? StartTime { get; init; }
    public QuotaRequest? Quota { get; init; }
    public Contact Contact { get; init; } = new();
}

/// <param name="Id">ARM name</param>
public sealed record TicketSummary(
    string Id, string TicketNumber, string Title, string Status, string Severity,
    DateTimeOffset? Created, string Service, string Problem, string? Plan)
{
    public bool IsOpen => Status.Equals("open", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Body">纯文本 (已经过 HtmlText.Plain)</param>
public sealed record Communication(
    string Id, string Sender, string Subject, string Body, bool FromMicrosoft, DateTimeOffset? Created);

public sealed record TicketDetail(TicketSummary Summary, JsonObject Props, IReadOnlyList<Communication> Communications);

public sealed record RecentProblem(string ServiceName, string ServiceDisplayName, string ProblemId, string ProblemDisplayName)
{
    public string Id => ProblemId;
    public string Label => $"{ServiceDisplayName} · {ProblemDisplayName.Split('/')[^1].Trim()}";
}

public sealed class AppError(string message, string? requestId = null, int? status = null, string? hint = null) : Exception(message)
{
    public string? RequestId { get; } = requestId;
    public int? Status { get; } = status;
    public string? Hint { get; } = hint;

    /// <summary>界面显示文本, 附带 Request ID 便于支持排查.</summary>
    /// <summary>非用户取消的超时 (HttpClient.Timeout / CancelAfter).</summary>
    public static AppError Timeout() =>
        new("请求超时, 请稍后重试 (创建 Case 时请先在历史列表确认是否已创建)", status: 408);

    public string Display => RequestId is null ? Message : $"{Message}\nRequest ID: {RequestId}";
}
