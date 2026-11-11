using System.Text.Json;
using CasePilot.App.ViewModels;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using CasePilot.Core.Support;

namespace CasePilot.App.Tests.Fakes;

/// <summary>经 JSON 往返, 与 JsonSettingsStore 行为一致.</summary>
internal sealed class FakeSettings : ISettingsStore
{
    public Dictionary<string, string> Data { get; } = [];
    public T? Get<T>(string key) => Data.TryGetValue(key, out var s) ? JsonSerializer.Deserialize<T>(s) : default;
    public void Set<T>(string key, T value) => Data[key] = JsonSerializer.Serialize(value);
}

internal sealed class FakeSecrets : ISecretStore
{
    public Dictionary<string, string> Data { get; } = [];
    public string? Read(string target) => Data.GetValueOrDefault(target);
    public void Write(string target, string? value)
    {
        if (string.IsNullOrEmpty(value)) Data.Remove(target); else Data[target] = value;
    }
}

internal sealed class FakeShell : IShell
{
    public List<string> Copied { get; } = [];
    public List<string> Opened { get; } = [];
    public void Copy(string text) => Copied.Add(text);
    public void OpenUrl(string url) => Opened.Add(url);
}

internal sealed class FakeDialogs : IDialogs
{
    public bool Answer { get; set; } = true;
    public List<(string Title, string Message)> Asked { get; } = [];
    public Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        Asked.Add((title, message));
        return Task.FromResult(Answer);
    }
}

internal sealed class NoDelay : IDelay
{
    public List<TimeSpan> Waits { get; } = [];
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) { Waits.Add(delay); return Task.CompletedTask; }
}

internal sealed class FakeChat : IChat
{
    public Queue<string> Replies { get; } = new();
    public Exception? Error { get; set; }
    public Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, string? jsonSchema, CancellationToken ct) =>
        Error is not null ? Task.FromException<string>(Error) : Task.FromResult(Replies.Dequeue());
}

internal sealed class FakeLogin : ILoginService
{
    public AuthMode Mode { get; set; }
    public AzureCloud Cloud { get; set; }
    public string? User { get; set; }
    public string? HomeTenant { get; set; }
    public Func<Identity>? Cli { get; set; }
    public Func<Identity>? Sp { get; set; }
    public (string Tenant, string Client, string Secret, AzureCloud Cloud)? SpArgs { get; private set; }
    public DeviceCode Code { get; set; } = new("ABCD-1234", "https://microsoft.com/devicelogin", "dc", 5, DateTimeOffset.MaxValue, "");
    public TaskCompletionSource<Identity> DevicePoll { get; } = new();
    public int LogoutCount { get; private set; }

    public Task<string> GetTokenAsync(string? tenant, string? resource, CancellationToken ct) => Task.FromResult("tok");
    public Task<Identity> LoginCliAsync(CancellationToken ct) => Task.FromResult(Cli!());
    public Task<DeviceCode> StartDeviceAsync(AzureCloud cloud, CancellationToken ct) { Cloud = cloud; return Task.FromResult(Code); }
    public Task<Identity> PollDeviceAsync(DeviceCode code, CancellationToken ct)
    {
        ct.Register(() => DevicePoll.TrySetCanceled(ct));
        return DevicePoll.Task;
    }
    public Task<Identity> LoginSpAsync(string tenant, string clientId, string secret, AzureCloud cloud, CancellationToken ct)
    {
        SpArgs = (tenant, clientId, secret, cloud);
        return Task.FromResult(Sp!());
    }
    public void Logout() => LogoutCount++;
}

internal sealed class FakeApi : ISupportApi
{
    public List<Tenant> Tenants { get; set; } = [];
    public Dictionary<string, List<Subscription>> Subs { get; } = [];
    public List<SupportService> Services { get; set; } = [];
    public Dictionary<string, List<ProblemClassification>> Problems { get; } = [];
    public List<AzureResource> Resources { get; set; } = [];
    public List<TicketSummary> Tickets { get; set; } = [];
    public List<TicketSummary> YearTickets { get; set; } = [];
    public string QuotaId { get; set; } = "";
    public TicketDetail? Detail { get; set; }
    public Exception? SubsError { get; set; }
    public Exception? CreateError { get; set; }
    public Exception? TicketsError { get; set; }
    public List<string> Calls { get; } = [];
    public List<NewCase> Created { get; } = [];
    public List<(string Name, string Text, string? Sender)> Replies { get; } = [];
    public List<string> Closed { get; } = [];

    public Task<IReadOnlyList<Tenant>> TenantsAsync(CancellationToken ct) { Calls.Add("tenants"); return Task.FromResult<IReadOnlyList<Tenant>>(Tenants); }
    public Task<IReadOnlyList<Subscription>> SubscriptionsAsync(string tenant, CancellationToken ct)
    {
        Calls.Add($"subs:{tenant}");
        if (SubsError is not null) return Task.FromException<IReadOnlyList<Subscription>>(SubsError);
        return Task.FromResult<IReadOnlyList<Subscription>>(Subs.GetValueOrDefault(tenant) ?? []);
    }
    public Task<string> QuotaIdAsync(string tenant, string sub, CancellationToken ct) { Calls.Add($"quota:{sub}"); return Task.FromResult(QuotaId); }
    public Task<IReadOnlyList<SupportService>> ServicesAsync(string tenant, CancellationToken ct) { Calls.Add($"services:{tenant}"); return Task.FromResult<IReadOnlyList<SupportService>>(Services); }
    public Task<IReadOnlyList<ProblemClassification>> ProblemsAsync(string tenant, SupportService service, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProblemClassification>>(Problems.GetValueOrDefault(service.Name) ?? []);
    public Task<IReadOnlyList<AzureResource>> ResourcesAsync(string tenant, string sub, IReadOnlyList<string>? types, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AzureResource>>(Resources);
    public Task<TicketSummary> CreateAsync(string tenant, string? sub, NewCase c, CancellationToken ct)
    {
        if (CreateError is not null) return Task.FromException<TicketSummary>(CreateError);
        Created.Add(c);
        return Task.FromResult(new TicketSummary("cp-1", "2511110010000001", c.Title, "Open", c.Severity.ApiValue(), null, "", "", null));
    }
    public Task<IReadOnlyList<TicketSummary>> TicketsAsync(string tenant, string? sub, int days, CancellationToken ct)
    {
        Calls.Add($"tickets:{sub ?? "-"}:{days}");
        if (TicketsError is not null) return Task.FromException<IReadOnlyList<TicketSummary>>(TicketsError);
        return Task.FromResult<IReadOnlyList<TicketSummary>>(days == 365 && YearTickets.Count > 0 ? YearTickets : Tickets);
    }
    public Task<TicketDetail> DetailAsync(string tenant, string? sub, string name, CancellationToken ct) { Calls.Add($"detail:{name}"); return Task.FromResult(Detail!); }
    public Task ReplyAsync(string tenant, string? sub, string name, string text, string? sender, CancellationToken ct) { Replies.Add((name, text, sender)); return Task.CompletedTask; }
    public Task CloseAsync(string tenant, string? sub, string name, CancellationToken ct) { Closed.Add(name); return Task.CompletedTask; }
}

internal static class Data
{
    public const string T1 = "11111111-1111-1111-1111-111111111111";
    public const string T2 = "22222222-2222-2222-2222-222222222222";
    public const string S1 = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    public const string S2 = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    public static SupportService Svc(string name, string display, params string[] types) => new($"/providers/Microsoft.Support/services/{name}", name, display, types);
    public static readonly SupportService Vm = Svc("vm", "Virtual Machine running Linux", "microsoft.compute/virtualmachines");
    public static readonly SupportService Quota = Svc("quota", "Service and subscription limits (quotas)");
    public static readonly SupportService Billing = Svc("billing", "Billing");
    public static readonly ProblemClassification VmBoot = new("pVmBoot", "Cannot connect / Boot");
    public static readonly ProblemClassification VmCores = new("pCores", "Compute-VM (cores-vCPUs) subscription limit increases");
    public static readonly ProblemClassification Other = new("pOther", "Other");

    public static Identity Cli(string tenant = T1) => new("alice@contoso.com", tenant, "Contoso", AzureCloud.Global, "Azure CLI");

    public static TicketSummary Ticket(string id, string status, int daysAgo, string? plan = null, string title = "标题", string service = "VM") =>
        new(id, "25" + id, title, status, "moderate", new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero).AddDays(-daysAgo), service, "Boot", plan);

    public static Contact GoodContact => new() { FirstName = "三", LastName = "张", Email = "z@contoso.com" };

    public static FakeApi Api()
    {
        var api = new FakeApi
        {
            Tenants = [new(T1, "Contoso", "contoso.com"), new(T2, T2, "fabrikam.com")],
            Services = [Vm, Quota, Billing],
        };
        api.Subs[T1] = [new(S1, "Prod", "Enabled"), new(S2, "Dev", "Enabled")];
        api.Subs[T2] = [];
        api.Problems["vm"] = [VmBoot, Other];
        api.Problems["quota"] = [Other, VmCores];
        api.Problems["billing"] = [Other];
        return api;
    }
}
