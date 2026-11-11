using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using CasePilot.Core.Text;

namespace CasePilot.Core.Support;

/// <summary>ARM + Microsoft.Support REST 客户端 (api-version 2024-04-01).</summary>
public sealed class SupportClient(HttpClient http, IAuthProvider auth, IDelay delay, IIdGenerator ids, IClock clock) : ISupportApi
{
    private const string V = "api-version=" + ArmPaths.SupportApi;
    private static readonly TimeSpan ServicesTtl = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Dictionary<string, (IReadOnlyList<SupportService> Items, DateTimeOffset At)> _services = [];

    // ---- 底层请求 ----

    private async Task<(JsonNode? Json, HttpResponseMessage Resp)> SendAsync(
        HttpMethod method, string path, string? tenant, JsonNode? body, CancellationToken ct)
    {
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : auth.Cloud.Arm() + path;
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetTokenAsync(tenant, null, ct));
        req.Headers.TryAddWithoutValidation("x-ms-client-request-id", Guid.NewGuid().ToString());
        if (body is not null) req.Content = new StringContent(body.ToJsonString(Relaxed), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await http.SendAsync(req, ct);
            text = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw AppError.Timeout(); }
        JsonNode? json;
        try { json = text.Length == 0 ? null : JsonNode.Parse(text); }
        catch (JsonException) { json = null; }
        var status = (int)resp.StatusCode;
        if (status >= 400)
        {
            var err = (json as JsonObject)?["error"] as JsonObject;
            var rid = resp.Headers.TryGetValues("x-ms-request-id", out var v) ? v.FirstOrDefault() : null;
            resp.Dispose();
            throw new AppError($"{S(err, "code") ?? $"HTTP {status}"}: {S(err, "message") ?? ""}", rid, status);
        }
        return (json, resp);
    }

    private async Task<JsonObject> GetAsync(string path, string? tenant, CancellationToken ct)
    {
        var (json, resp) = await SendAsync(HttpMethod.Get, path, tenant, null, ct);
        resp.Dispose();
        return json as JsonObject ?? [];
    }

    private async Task<List<JsonObject>> ListAsync(string path, string? tenant, CancellationToken ct)
    {
        var all = new List<JsonObject>();
        string? next = path;
        while (next is not null)
        {
            var page = await GetAsync(next, tenant, ct);
            if (page["value"] is JsonArray arr) all.AddRange(arr.OfType<JsonObject>());
            next = S(page, "nextLink");
        }
        return all;
    }

    // ---- 范围 ----

    public async Task<IReadOnlyList<Tenant>> TenantsAsync(CancellationToken ct) =>
        (await ListAsync("/tenants?api-version=2022-12-01", null, ct)).Select(t =>
        {
            var id = S(t, "tenantId") ?? "";
            return new Tenant(id, S(t, "displayName") ?? id, S(t, "defaultDomain") ?? "");
        }).ToList();

    public async Task<IReadOnlyList<Subscription>> SubscriptionsAsync(string tenant, CancellationToken ct) =>
        (await ListAsync("/subscriptions?api-version=2022-12-01", tenant, ct))
            .Where(s => (S(s, "tenantId") ?? tenant) == tenant)
            .Select(s => new Subscription(S(s, "subscriptionId") ?? "", S(s, "displayName") ?? "", S(s, "state") ?? ""))
            .OrderBy(s => s.Name, StringComparer.InvariantCultureIgnoreCase).ToList();

    /// <summary>订阅 quotaId (用于推断支持计划).</summary>
    public async Task<string> QuotaIdAsync(string tenant, string sub, CancellationToken ct) =>
        S((await GetAsync($"/subscriptions/{sub}?api-version=2022-12-01", tenant, ct))["subscriptionPolicies"] as JsonObject, "quotaId") ?? "";

    // ---- 服务目录 ----

    public async Task<IReadOnlyList<SupportService>> ServicesAsync(string tenant, CancellationToken ct)
    {
        var key = auth.Cloud + "|" + tenant;
        lock (_services)
            if (_services.TryGetValue(key, out var c) && clock.UtcNow - c.At <= ServicesTtl) return c.Items;
        var items = (await ListAsync($"/providers/Microsoft.Support/services?{V}", tenant, ct)).Select(s =>
        {
            var p = s["properties"] as JsonObject;
            var types = (p?["resourceTypes"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? [];
            return new SupportService(S(s, "id") ?? "", S(s, "name") ?? "", S(p, "displayName") ?? "", types);
        }).OrderBy(s => s.DisplayName, StringComparer.InvariantCultureIgnoreCase).ToList();
        lock (_services) _services[key] = (items, clock.UtcNow);
        return items;
    }

    public async Task<IReadOnlyList<ProblemClassification>> ProblemsAsync(string tenant, SupportService service, CancellationToken ct) =>
        (await ListAsync($"/providers/Microsoft.Support/services/{service.Name}/problemClassifications?{V}", tenant, ct))
            .Select(p => new ProblemClassification(S(p, "id") ?? "", S(p["properties"] as JsonObject, "displayName") ?? ""))
            .OrderBy(p => p.DisplayName, StringComparer.InvariantCultureIgnoreCase).ToList();

    public async Task<IReadOnlyList<AzureResource>> ResourcesAsync(string tenant, string sub, IReadOnlyList<string>? types, CancellationToken ct)
    {
        var path = $"/subscriptions/{sub}/resources?api-version=2021-04-01";
        if (types is { Count: > 0 }) path += "&$filter=" + Quote(ArmPaths.ResourcesFilter(types));
        return (await ListAsync(path, tenant, ct))
            .Select(r => new AzureResource(S(r, "id") ?? "", S(r, "name") ?? "", (S(r, "type") ?? "").ToLowerInvariant(), S(r, "location") ?? ""))
            .OrderBy(r => r.Name, StringComparer.InvariantCultureIgnoreCase).ToList();
    }

    // ---- Case ----

    public async Task<TicketSummary> CreateAsync(string tenant, string? sub, NewCase c, CancellationToken ct)
    {
        var b = ArmPaths.SupportBase(sub);
        var hex = ids.NewId().ToString("N");
        var name = "casepilot-" + hex[..12];
        var avail = await SendAsync(HttpMethod.Post, $"{b}/checkNameAvailability?{V}", tenant,
            new JsonObject { ["name"] = name, ["type"] = "Microsoft.Support/supportTickets" }, ct);
        avail.Resp.Dispose();
        if (avail.Json?["nameAvailable"] is JsonValue av && av.TryGetValue<bool>(out var ok) && !ok)
            name = "casepilot-" + ids.NewId().ToString("N");

        var path = $"{b}/supportTickets/{name}?{V}";
        var (_, resp) = await SendAsync(HttpMethod.Put, path, tenant, SupportBody.Build(c), ct);
        var op = resp.Headers.TryGetValues("Azure-AsyncOperation", out var v) ? v.FirstOrDefault() : null;
        resp.Dispose();
        if (op is not null)
        {
            for (var i = 0; i < 40; i++)
            {
                var opJson = await GetAsync(op, tenant, ct);
                var st = (S(opJson, "status") ?? "").ToLowerInvariant();
                if (st == "succeeded") break;
                if (st is "failed" or "canceled")
                {
                    var err = opJson["error"] as JsonObject;
                    throw new AppError($"创建失败 ({st}): {S(err, "code")} {S(err, "message")}".TrimEnd(' ', ':'), status: 500);
                }
                await delay.DelayAsync(TimeSpan.FromSeconds(3), ct);
            }
        }
        return Summary(await GetAsync(path, tenant, ct));
    }

    public async Task<IReadOnlyList<TicketSummary>> TicketsAsync(string tenant, string? sub, int days, CancellationToken ct)
    {
        var filter = Quote(ArmPaths.TicketsFilter(days, clock.UtcNow));
        return (await ListAsync($"{ArmPaths.SupportBase(sub)}/supportTickets?{V}&$top=100&$filter={filter}", tenant, ct))
            .Select(Summary).OrderByDescending(t => t.Created ?? DateTimeOffset.MinValue).ToList();
    }

    public async Task<TicketDetail> DetailAsync(string tenant, string? sub, string name, CancellationToken ct)
    {
        CheckName(name);
        var b = $"{ArmPaths.SupportBase(sub)}/supportTickets/{name}";
        var t = await GetAsync($"{b}?{V}", tenant, ct);
        List<Communication> comms;
        try
        {
            comms = (await ListAsync($"{b}/communications?{V}", tenant, ct)).Select(c =>
            {
                var p = c["properties"] as JsonObject;
                return new Communication(S(c, "name") ?? Guid.NewGuid().ToString(), S(p, "sender") ?? "", S(p, "subject") ?? "",
                    HtmlText.Plain(S(p, "body") ?? ""), S(p, "communicationDirection") == "Outbound", Date(S(p, "createdDate")));
            }).OrderByDescending(x => x.Created ?? DateTimeOffset.MinValue).ToList();
        }
        catch (AppError e)
        {
            comms = [new Communication("placeholder", "CasePilot", "无法加载沟通记录", e.Message, false, null)];
        }
        var props = t["properties"] as JsonObject ?? [];
        return new TicketDetail(Summary(t), (JsonObject)props.DeepClone(), comms);
    }

    public async Task ReplyAsync(string tenant, string? sub, string name, string text, string? sender, CancellationToken ct)
    {
        CheckName(name);
        var cname = "cp-" + ids.NewId().ToString("N")[..12];
        var props = new JsonObject { ["subject"] = "客户补充信息 / Update from customer", ["body"] = ReplyText.Body(text) };
        if (sender is not null) props["sender"] = sender;
        var (_, resp) = await SendAsync(HttpMethod.Put,
            $"{ArmPaths.SupportBase(sub)}/supportTickets/{name}/communications/{cname}?{V}", tenant, new JsonObject { ["properties"] = props }, ct);
        resp.Dispose();
    }

    /// <summary>关闭后回读确认: PATCH 成功不代表状态已生效.</summary>
    public async Task CloseAsync(string tenant, string? sub, string name, CancellationToken ct)
    {
        CheckName(name);
        var path = $"{ArmPaths.SupportBase(sub)}/supportTickets/{name}?{V}";
        var status = "";
        for (var i = 0; i < 4; i++)
        {
            try
            {
                var (_, resp) = await SendAsync(HttpMethod.Patch, path, tenant, new JsonObject { ["status"] = "closed" }, ct);
                resp.Dispose();
            }
            catch (AppError e) when (e.Message.Contains("redundant action") || e.Message.Contains("currently Closed")) { }
            await delay.DelayAsync(TimeSpan.FromSeconds(3), ct);
            status = S((await GetAsync(path, tenant, ct))["properties"] as JsonObject, "status") ?? "";
            if (status.Equals("closed", StringComparison.OrdinalIgnoreCase)) return;
            await delay.DelayAsync(TimeSpan.FromSeconds(5), ct);
        }
        throw new AppError($"关闭请求已发送, 但 Case 当前状态仍为 {status}, 请稍后重试", status: 409);
    }

    // ---- 解析 ----

    private static void CheckName(string name)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('?') || name.Contains('#')) throw new AppError("非法 Case 名");
    }

    public static TicketSummary Summary(JsonObject t)
    {
        var p = t["properties"] as JsonObject;
        return new TicketSummary(S(t, "name") ?? "", S(p, "supportTicketId") ?? "", S(p, "title") ?? "", S(p, "status") ?? "",
            S(p, "severity") ?? "", Date(S(p, "createdDate")), S(p, "serviceDisplayName") ?? "",
            S(p, "problemClassificationDisplayName") ?? "", S(p, "supportPlanType"));
    }

    public static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>与 Python urllib.parse.quote 一致 (保留 '/').</summary>
    private static string Quote(string s) => Uri.EscapeDataString(s).Replace("%2F", "/");

    private static string? S(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
