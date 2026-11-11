using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;
using CasePilot.Core.Auth;
using CasePilot.Core.Support;
using CasePilot.Platform;

namespace CasePilot.Platform.Tests;

/// 只读真实调用: 用 az login 身份读取服务目录, 以默认 AI 配置调用一次 AIDrafter (仅对话, 不创建 Case)。
/// 仅在 CASEPILOT_LIVE=1 时运行。
[TestClass]
public class LiveAITests
{
    /// CASEPILOT_AI_ENDPOINT / CASEPILOT_AI_DEPLOYMENT 可指向当前租户下的资源.
    private static AIConfig LiveConfig()
    {
        var c = new AIConfig();
        var ep = Environment.GetEnvironmentVariable("CASEPILOT_AI_ENDPOINT");
        var dep = Environment.GetEnvironmentVariable("CASEPILOT_AI_DEPLOYMENT");
        if (!string.IsNullOrEmpty(ep)) c = c with { ChatEndpoint = ep };
        if (!string.IsNullOrEmpty(dep)) c = c with { ChatDeployment = dep };
        return c;
    }

    [TestMethod, TestCategory("Live")]
    public async Task Draft_WithDefaultConfig_ReturnsServiceAndText()
    {
        if (Environment.GetEnvironmentVariable("CASEPILOT_LIVE") != "1")
            Assert.Inconclusive("设置 CASEPILOT_LIVE=1 以运行真实 AI 调用测试");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var clock = new SystemClock(); var delay = new SystemDelay();
        var auth = new AuthProvider(http, new AzCli(), clock, delay);
        var id = await auth.LoginCliAsync(default);
        var sc = new SupportClient(http, auth, delay, new GuidIds(), clock);
        var services = await sc.ServicesAsync(id.Tenant, default);
        var ai = new AIClient(http, auth, delay, () => LiveConfig(), new NoSecrets());
        AIDraft draft;
        try
        {
            draft = await DraftOnce(services, sc, id.Tenant, ai);
        }
        catch (CasePilot.Core.Models.AppError e) when (e.Status is 400 or 401 or 403)
        {
            // 资源不在当前身份可访问的租户 / 无角色: 属于环境问题, 记录后不判失败.
            Assert.Inconclusive("AI 资源不可用于当前 az 身份: " + e.Message);
            return;
        }
        Console.WriteLine($"service={draft.Service?.DisplayName} problem={draft.Problem?.DisplayName} sev={draft.Severity} title={draft.Title} q={draft.Questions.Count}");
        Assert.IsTrue(draft.Title.Length > 0 || draft.Questions.Count > 0);
    }

    static Task<AIDraft> DraftOnce(IReadOnlyList<CasePilot.Core.Models.SupportService> services, SupportClient sc, string tenant, AIClient ai) =>
        AIDrafter.DraftAsync("生产环境 Linux 虚拟机重启后无法 SSH 连接, 串行控制台显示 fsck 错误", [], services,
            (s, ct) => sc.ProblemsAsync(tenant, s, ct), ai, default);

    sealed class NoSecrets : ISecretStore
    {
        public string? Read(string target) => null;
        public void Write(string target, string? value) { }
    }
}
