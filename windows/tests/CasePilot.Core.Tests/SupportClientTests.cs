using System.Net.Http;
using CasePilot.Core.Models;
using CasePilot.Core.Support;
using CasePilot.Core.Tests.Fakes;

namespace CasePilot.Core.Tests;

[TestClass]
public class SupportClientTests
{
    private const string Arm = "https://management.azure.com";
    private FakeHttp _http = null!;
    private FakeDelay _delay = null!;
    private FakeClock _clock = null!;
    private FakeAuth _auth = null!;

    [TestInitialize]
    public void Init()
    {
        _http = new FakeHttp();
        _clock = new FakeClock();
        _delay = new FakeDelay();
        _auth = new FakeAuth();
    }

    private SupportClient Client() => new(_http.Client(), _auth, _delay, new SeqIds(), _clock);

    // ---- plumbing ----

    [TestMethod]
    public async Task SendsBearerRequestIdAndFollowsNextLink()
    {
        _http.Reply(200, """{"value":[{"tenantId":"b","displayName":"租户B","defaultDomain":"b.cn"}],"nextLink":"https://management.azure.com/tenants?page=2"}""")
             .Reply(200, """{"value":[{"tenantId":"a"}]}""");
        var ts = await Client().TenantsAsync(default);
        Assert.AreEqual(2, ts.Count);
        Assert.AreEqual(new Tenant("b", "租户B", "b.cn"), ts[0]);
        Assert.AreEqual(new Tenant("a", "a", ""), ts[1]);
        Assert.AreEqual($"{Arm}/tenants?api-version=2022-12-01", _http.Requests[0].Url);
        Assert.AreEqual("https://management.azure.com/tenants?page=2", _http.Requests[1].Url);
        Assert.AreEqual("Bearer tok", _http.Requests[0].Headers["Authorization"]);
        Assert.IsTrue(Guid.TryParse(_http.Requests[0].Headers["x-ms-client-request-id"], out _));
        Assert.AreEqual(null, _auth.Tenants[0]);
    }

    [TestMethod]
    public async Task UsesCloudArmBase()
    {
        _auth.Cloud = AzureCloud.China;
        _http.Reply(200, """{"value":[]}""");
        await Client().TenantsAsync(default);
        StringAssert.StartsWith(_http.Requests[0].Url, "https://management.chinacloudapi.cn/tenants");
    }

    [TestMethod]
    public async Task ErrorBecomesAppErrorWithRequestId()
    {
        _http.Reply(403, """{"error":{"code":"AuthorizationFailed","message":"no access"}}""", ("x-ms-request-id", "rid-1"));
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => Client().TenantsAsync(default));
        Assert.AreEqual("AuthorizationFailed: no access", e.Message);
        Assert.AreEqual("rid-1", e.RequestId);
        Assert.AreEqual(403, e.Status);
        Assert.AreEqual("AuthorizationFailed: no access\nRequest ID: rid-1", e.Display);
    }

    [TestMethod]
    public async Task ErrorWithoutJsonUsesStatus()
    {
        _http.Reply(502, "<html>bad gateway</html>");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => Client().TenantsAsync(default));
        Assert.AreEqual("HTTP 502: ", e.Message);
    }

    [TestMethod]
    public async Task SubscriptionsFilteredByTenantAndSorted()
    {
        _http.Reply(200, """
            {"value":[
              {"subscriptionId":"2","displayName":"beta","state":"Enabled","tenantId":"t"},
              {"subscriptionId":"3","displayName":"x","state":"Enabled","tenantId":"other"},
              {"subscriptionId":"1","displayName":"Alpha","state":"Disabled"}]}
            """);
        var s = await Client().SubscriptionsAsync("t", default);
        CollectionAssert.AreEqual(new[] { "1", "2" }, s.Select(x => x.Id).ToArray());
        Assert.AreEqual("t", _auth.Tenants[0]);
    }

    [TestMethod]
    public async Task QuotaId()
    {
        _http.Reply(200, """{"subscriptionPolicies":{"quotaId":"MSDN_2014-09-01"}}""");
        Assert.AreEqual("MSDN_2014-09-01", await Client().QuotaIdAsync("t", "s1", default));
        Assert.AreEqual($"{Arm}/subscriptions/s1?api-version=2022-12-01", _http.Requests[0].Url);
    }

    [TestMethod]
    public async Task ServicesSortedAndCachedSixHours()
    {
        const string Body = """
            {"value":[
              {"id":"/providers/Microsoft.Support/services/b","name":"b","properties":{"displayName":"Virtual Machine","resourceTypes":["Microsoft.Compute/virtualMachines"]}},
              {"id":"/providers/Microsoft.Support/services/a","name":"a","properties":{"displayName":"Billing"}}]}
            """;
        _http.Reply(200, Body).Reply(200, Body);
        var c = Client();
        var s = await c.ServicesAsync("t", default);
        Assert.AreEqual("Billing", s[0].DisplayName);
        Assert.AreEqual(0, s[0].ResourceTypes.Count);
        Assert.AreEqual("Microsoft.Compute/virtualMachines", s[1].ResourceTypes[0]);
        Assert.AreEqual($"{Arm}/providers/Microsoft.Support/services?api-version=2024-04-01", _http.Requests[0].Url);
        await c.ServicesAsync("t", default);
        Assert.AreEqual(1, _http.Requests.Count);
        _clock.UtcNow = _clock.UtcNow.AddHours(6).AddSeconds(1);
        await c.ServicesAsync("t", default);
        Assert.AreEqual(2, _http.Requests.Count);
    }

    [TestMethod]
    public async Task Problems()
    {
        _http.Reply(200, """{"value":[{"id":"/p/2","properties":{"displayName":"Z"}},{"id":"/p/1","properties":{"displayName":"a"}}]}""");
        var p = await Client().ProblemsAsync("t", new SupportService("/s/x", "svc-guid", "X", []), default);
        CollectionAssert.AreEqual(new[] { "/p/1", "/p/2" }, p.Select(x => x.Id).ToArray());
        Assert.AreEqual($"{Arm}/providers/Microsoft.Support/services/svc-guid/problemClassifications?api-version=2024-04-01", _http.Requests[0].Url);
    }

    [TestMethod]
    public async Task ResourcesWithFilterLowercasesType()
    {
        _http.Reply(200, """{"value":[{"id":"/r","name":"vm1","type":"Microsoft.Compute/virtualMachines","location":"eastus"}]}""");
        var r = await Client().ResourcesAsync("t", "s1", ["Microsoft.Compute/virtualMachines"], default);
        Assert.AreEqual(new AzureResource("/r", "vm1", "microsoft.compute/virtualmachines", "eastus"), r[0]);
        Assert.AreEqual($"{Arm}/subscriptions/s1/resources?api-version=2021-04-01&$filter=resourceType%20eq%20%27Microsoft.Compute/virtualMachines%27",
            _http.Requests[0].Url);
    }

    [TestMethod]
    public async Task TicketsFilterAndNewestFirst()
    {
        _http.Reply(200, """
            {"value":[
              {"name":"old","properties":{"supportTicketId":"1","title":"t","status":"Closed","severity":"minimal","createdDate":"2026-10-01T00:00:00Z"}},
              {"name":"new","properties":{"supportTicketId":"2","title":"t","status":"Open","severity":"moderate","createdDate":"2026-11-01T00:00:00.123Z","supportPlanType":"Unified Enterprise","serviceDisplayName":"VM","problemClassificationDisplayName":"P"}}]}
            """);
        var t = await Client().TicketsAsync("t", "s1", 30, default);
        Assert.AreEqual("new", t[0].Id);
        Assert.IsTrue(t[0].IsOpen);
        Assert.AreEqual("Unified Enterprise", t[0].Plan);
        Assert.IsNull(t[1].Plan);
        Assert.AreEqual($"{Arm}/subscriptions/s1/providers/Microsoft.Support/supportTickets?api-version=2024-04-01&$top=100&$filter=createdDate%20ge%202026-10-12T00%3A00%3A00Z",
            _http.Requests[0].Url);
    }

    // ---- create ----

    private const string Ticket = """{"name":"casepilot-000000010000","properties":{"supportTicketId":"2611110010000001","title":"标题测试","status":"Open","severity":"moderate"}}""";

    [TestMethod]
    public async Task CreatePollsUntilSucceeded()
    {
        const string Op = "https://management.azure.com/op/1?api-version=2024-04-01";
        _http.Reply(200, """{"nameAvailable":true}""")
             .Reply(202, "{}", ("Azure-AsyncOperation", Op))
             .Reply(200, """{"status":"InProgress"}""")
             .Reply(200, """{"status":"Succeeded"}""")
             .Reply(200, Ticket);
        var t = await Client().CreateAsync("t", "s1", SupportBodyTests.Draft(), default);
        Assert.AreEqual("2611110010000001", t.TicketNumber);
        var b = $"{Arm}/subscriptions/s1/providers/Microsoft.Support";
        Assert.AreEqual($"{b}/checkNameAvailability?api-version=2024-04-01", _http.Requests[0].Url);
        Assert.AreEqual("""{"name":"casepilot-000000010000","type":"Microsoft.Support/supportTickets"}""", _http.Requests[0].Body);
        Assert.AreEqual(HttpMethod.Put, _http.Requests[1].Method);
        Assert.AreEqual($"{b}/supportTickets/casepilot-000000010000?api-version=2024-04-01", _http.Requests[1].Url);
        StringAssert.Contains(_http.Requests[1].Body, "\"title\":\"标题测试\"");
        Assert.AreEqual(Op, _http.Requests[2].Url);
        Assert.AreEqual(HttpMethod.Get, _http.Requests[4].Method);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(3) }, _delay.Calls);
    }

    [TestMethod]
    public async Task CreateUsesLongNameWhenTaken()
    {
        _http.Reply(200, """{"nameAvailable":false}""").Reply(201, "{}").Reply(200, Ticket);
        await Client().CreateAsync("t", null, SupportBodyTests.Draft(), default);
        StringAssert.Contains(_http.Requests[1].Url, "/providers/Microsoft.Support/supportTickets/casepilot-00000002000000000000000000000000?");
        Assert.AreEqual(0, _delay.Calls.Count); // 无 Azure-AsyncOperation 时不轮询
    }

    [DataTestMethod]
    [DataRow("Failed")]
    [DataRow("Canceled")]
    public async Task CreateFailedOrCanceledThrows(string status)
    {
        _http.Reply(200, "{}").Reply(202, "{}", ("Azure-AsyncOperation", "https://management.azure.com/op/1"))
             .Reply(200, $$"""{"status":"{{status}}"}""");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => Client().CreateAsync("t", null, SupportBodyTests.Draft(), default));
        Assert.AreEqual($"创建失败 ({status.ToLowerInvariant()})", e.Message);
        Assert.AreEqual(3, _http.Requests.Count);
    }

    [TestMethod]
    public async Task CreatePollsAtMost40Times()
    {
        _http.Reply(200, "{}").Reply(202, "{}", ("Azure-AsyncOperation", "https://management.azure.com/op/1"));
        for (var i = 0; i < 40; i++) _http.Reply(200, """{"status":"InProgress"}""");
        _http.Reply(200, Ticket);
        await Client().CreateAsync("t", null, SupportBodyTests.Draft(), default);
        Assert.AreEqual(40, _delay.Calls.Count);
        Assert.AreEqual(43, _http.Requests.Count);
    }

    // ---- close ----

    [TestMethod]
    public async Task CloseReadsBack()
    {
        _http.Reply(200, "{}").Reply(200, """{"properties":{"status":"Closed"}}""");
        await Client().CloseAsync("t", "s1", "n1", default);
        Assert.AreEqual(HttpMethod.Patch, _http.Requests[0].Method);
        Assert.AreEqual("""{"status":"closed"}""", _http.Requests[0].Body);
        Assert.AreEqual($"{Arm}/subscriptions/s1/providers/Microsoft.Support/supportTickets/n1?api-version=2024-04-01", _http.Requests[1].Url);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(3) }, _delay.Calls);
    }

    [TestMethod]
    public async Task CloseToleratesRedundantAction()
    {
        _http.Reply(400, """{"error":{"code":"BadRequest","message":"This is a redundant action"}}""")
             .Reply(200, """{"properties":{"status":"Closed"}}""");
        await Client().CloseAsync("t", null, "n1", default);
    }

    [TestMethod]
    public async Task CloseOtherErrorPropagates()
    {
        _http.Reply(403, """{"error":{"code":"Forbidden","message":"x"}}""");
        await Assert.ThrowsExceptionAsync<AppError>(() => Client().CloseAsync("t", null, "n1", default));
    }

    [TestMethod]
    public async Task CloseGivesUpAfterFourAttempts()
    {
        for (var i = 0; i < 4; i++) _http.Reply(200, "{}").Reply(200, """{"properties":{"status":"Open"}}""");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => Client().CloseAsync("t", null, "n1", default));
        Assert.AreEqual("关闭请求已发送, 但 Case 当前状态仍为 Open, 请稍后重试", e.Message);
        Assert.AreEqual(409, e.Status);
        Assert.AreEqual(8, _http.Requests.Count);
        Assert.AreEqual(8, _delay.Calls.Count);
    }

    [TestMethod]
    public async Task CloseRejectsBadName() =>
        await Assert.ThrowsExceptionAsync<AppError>(() => Client().CloseAsync("t", null, "a/b", default));

    // ---- detail / reply ----

    [TestMethod]
    public async Task DetailNewestFirstAndOutboundIsMicrosoft()
    {
        _http.Reply(200, """{"name":"n1","properties":{"title":"T","status":"Open","createdDate":"2026-11-01T00:00:00Z"}}""")
             .Reply(200, """
                {"value":[
                  {"name":"c1","properties":{"sender":"me","subject":"s1","body":"<p>hi</p>","communicationDirection":"Inbound","createdDate":"2026-11-01T01:00:00Z"}},
                  {"name":"c2","properties":{"sender":"MS","subject":"s2","body":"A &amp; B","communicationDirection":"Outbound","createdDate":"2026-11-02T01:00:00Z"}}]}
                """);
        var d = await Client().DetailAsync("t", null, "n1", default);
        Assert.AreEqual("T", d.Summary.Title);
        Assert.AreEqual("Open", (string?)d.Props["status"]);
        Assert.AreEqual("c2", d.Communications[0].Id);
        Assert.IsTrue(d.Communications[0].FromMicrosoft);
        Assert.AreEqual("A & B", d.Communications[0].Body);
        Assert.IsFalse(d.Communications[1].FromMicrosoft);
        Assert.AreEqual("hi", d.Communications[1].Body);
        Assert.AreEqual($"{Arm}/providers/Microsoft.Support/supportTickets/n1/communications?api-version=2024-04-01", _http.Requests[1].Url);
    }

    [TestMethod]
    public async Task DetailPlaceholderWhenCommunicationsFail()
    {
        _http.Reply(200, """{"name":"n1","properties":{"title":"T"}}""")
             .Reply(403, """{"error":{"code":"Forbidden","message":"denied"}}""");
        var d = await Client().DetailAsync("t", null, "n1", default);
        Assert.AreEqual(1, d.Communications.Count);
        Assert.AreEqual("CasePilot", d.Communications[0].Sender);
        Assert.AreEqual("无法加载沟通记录", d.Communications[0].Subject);
        Assert.AreEqual("Forbidden: denied", d.Communications[0].Body);
    }

    [TestMethod]
    public async Task ReplySendsPlainTextAndSubject()
    {
        _http.Reply(201, "{}");
        await Client().ReplyAsync("t", "s1", "n1", "a<b>\nc", "x@y.com", default);
        StringAssert.StartsWith(_http.Requests[0].Url, $"{Arm}/subscriptions/s1/providers/Microsoft.Support/supportTickets/n1/communications/cp-000000010000?");
        Assert.AreEqual("""{"properties":{"subject":"客户补充信息 / Update from customer","body":"a<b>\nc","sender":"x@y.com"}}""", _http.Requests[0].Body);
    }
}
