using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using CasePilot.Core.Plan;
using CasePilot.Core.Support;

namespace CasePilot.App.ViewModels;

/// <summary>--selftest: 只读检查 (登录、租户、订阅、服务、问题、Case 列表、详情、计划、请求体预览). 从不创建 / 补充 / 关闭.</summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(ISupportApi api, ILoginService auth, TextWriter o, CancellationToken ct = default)
    {
        var step = "login";
        try
        {
            var id = await auth.LoginCliAsync(ct);
            o.WriteLine($"[ok] login: {id.User} · {id.Method} · {id.Tenant}");

            step = "tenants";
            var tenants = await api.TenantsAsync(ct);
            var tenant = tenants.FirstOrDefault(t => t.Id == id.Tenant)?.Id ?? id.Tenant;
            o.WriteLine($"[ok] tenants: {tenants.Count} → {tenant}");

            step = "subscriptions";
            var subs = await api.SubscriptionsAsync(tenant, ct);
            var sub = subs.FirstOrDefault()?.Id;
            o.WriteLine($"[ok] subscriptions: {subs.Count} → {sub ?? "租户级"}");

            step = "services";
            var services = await api.ServicesAsync(tenant, ct);
            o.WriteLine($"[ok] services: {services.Count}");

            step = "problems";
            var svc = services.FirstOrDefault(s => s.DisplayName == "Virtual Machine running Linux") ?? services.FirstOrDefault()
                      ?? throw new AppError("服务列表为空");
            var problems = await api.ProblemsAsync(tenant, svc, ct);
            o.WriteLine($"[ok] problems: {svc.DisplayName} → {problems.Count}");

            step = "tickets(30)";
            var tickets = await api.TicketsAsync(tenant, sub, 30, ct);
            o.WriteLine($"[ok] tickets(30): {tickets.Count}");

            step = "detail";
            if (tickets.OrderByDescending(t => t.Created ?? DateTimeOffset.MinValue).FirstOrDefault() is { } newest)
            {
                var d = await api.DetailAsync(tenant, sub, newest.Id, ct);
                o.WriteLine($"[ok] detail: {d.Summary.TicketNumber} · {d.Communications.Count} 条沟通记录");
            }
            else o.WriteLine("[ok] detail: (无 Case, 跳过)");

            step = "plan";
            var quota = sub is null ? "" : await api.QuotaIdAsync(tenant, sub, ct);
            var plan = SupportPlan.Infer(tickets, quota);
            o.WriteLine($"[ok] plan: {plan.Name ?? "未知"}{(string.IsNullOrEmpty(plan.SourceLabel) ? "" : $" ({plan.SourceLabel})")}");

            step = "body";
            var body = SupportBody.Build(new NewCase
            {
                Service = svc,
                Problem = problems.FirstOrDefault(),
                Title = "[selftest] 预览",
                Description = new string('预', 20),
                Contact = new Contact { FirstName = "Self", LastName = "Test", Email = "selftest@example.com" },
            });
            var json = body.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            o.WriteLine($"[ok] body: {json.Length} 字符 (仅预览, 未提交)");
            o.WriteLine("selftest 通过");
            return 0;
        }
        catch (Exception e)
        {
            o.WriteLine($"[fail] {step}: {Errors.Text(e)}");
            return 1;
        }
    }
}
