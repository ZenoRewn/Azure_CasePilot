using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using CasePilot.Core.Support;
using CasePilot.Platform;

namespace CasePilot.Platform.Tests;

/// 端到端真实写入: 创建 C 级测试 Case → 回复 → 读取详情 → 关闭。仅在 CASEPILOT_LIVE_WRITE=1 时运行。
[TestClass]
public class LiveWriteTests
{
    [TestMethod, TestCategory("LiveWrite")]
    public async Task CreateReplyClose_EndToEnd()
    {
        if (Environment.GetEnvironmentVariable("CASEPILOT_LIVE_WRITE") != "1")
            Assert.Inconclusive("设置 CASEPILOT_LIVE_WRITE=1 以运行真实写入测试");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var clock = new SystemClock(); var delay = new SystemDelay();
        var auth = new AuthProvider(http, new AzCli(), clock, delay);
        var id = await auth.LoginCliAsync(default);
        var sc = new SupportClient(http, auth, delay, new GuidIds(), clock);
        var sub = (await sc.SubscriptionsAsync(id.Tenant, default))[0].Id;
        var svc = (await sc.ServicesAsync(id.Tenant, default)).First(s => s.DisplayName == "Billing");
        var problems = await sc.ProblemsAsync(id.Tenant, svc, default);
        var prob = problems.FirstOrDefault(p => p.DisplayName.Contains("other", StringComparison.OrdinalIgnoreCase)) ?? problems[0];
        Console.WriteLine($"service={svc.Name} problem={prob.DisplayName}");
        var email = Environment.GetEnvironmentVariable("CASEPILOT_TEST_EMAIL") ?? id.User;
        var nc = new NewCase
        {
            Service = svc, Problem = prob, Severity = Severity.Minimal,
            Title = "[TEST] CasePilot Windows 端到端测试, 请忽略并关闭",
            Description = "这是 CasePilot Windows 原生 App 的自动化端到端测试, 无需处理, 将立即自动关闭。\nAutomated test from CasePilot for Windows, please ignore.",
            Contact = new Contact { FirstName = "CasePilot", LastName = "Test", Email = email },
        };
        var created = await sc.CreateAsync(id.Tenant, sub, nc, default);
        Console.WriteLine($"CREATED {created.TicketNumber} name={created.Id} status={created.Status} sev={created.Severity}");
        try
        {
            await sc.ReplyAsync(id.Tenant, sub, created.Id, "测试回复: 中文与 <b>HTML</b> & 换行\n第二行", email, default);
            await Task.Delay(3000);
            var d = await sc.DetailAsync(id.Tenant, sub, created.Id, default);
            Console.WriteLine($"DETAIL title={d.Summary.Title} comms={d.Communications.Count}");
            Assert.AreEqual(nc.Title, d.Summary.Title);
            foreach (var c in d.Communications) Console.WriteLine($"  [{(c.FromMicrosoft ? "MS" : "我")}] {c.Subject} | {c.Body[..Math.Min(80, c.Body.Length)]}");
        }
        finally
        {
            await sc.CloseAsync(id.Tenant, sub, created.Id, default);
            var after = await sc.DetailAsync(id.Tenant, sub, created.Id, default);
            Console.WriteLine($"CLOSED status={after.Summary.Status}");
            Assert.AreEqual("closed", after.Summary.Status, ignoreCase: true);
        }
    }
}
