using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using CasePilot.Core.Support;
using CasePilot.Core.Tests.Fakes;

namespace CasePilot.Core.Tests;

[TestClass]
public class TimeoutTests
{
    sealed class NoSecrets : ISecretStore
    {
        public string? Read(string target) => null;
        public void Write(string target, string? value) { }
    }

    static SupportClient Support(FakeHttp http) => new(http.Client(), new FakeAuth(), new FakeDelay(), new SeqIds(), new FakeClock());

    [TestMethod]
    public async Task SupportTimeout_BecomesAppError408()
    {
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => Support(new FakeHttp().Timeout()).TenantsAsync(default));
        Assert.AreEqual(408, e.Status);
        StringAssert.Contains(e.Message, "超时");
    }

    [TestMethod]
    public async Task SupportUserCancel_StillCancels()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => Support(new FakeHttp().Timeout()).TenantsAsync(cts.Token));
    }

    [TestMethod]
    public async Task AITimeout_BecomesAppError408()
    {
        var c = new AIClient(new FakeHttp().Timeout().Client(), new FakeAuth(), new FakeDelay(), () => new AIConfig(), new NoSecrets());
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => c.ChatAsync([new("user", "hi")], null, CancellationToken.None));
        Assert.AreEqual(408, e.Status);
    }

    [TestMethod]
    public async Task AuthTimeout_BecomesAppError408()
    {
        var auth = new AuthProvider(new FakeHttp().Timeout().Client(), new FakeAz(), new FakeClock(), new FakeDelay());
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => auth.StartDeviceAsync(AzureCloud.Global, default));
        Assert.AreEqual(408, e.Status);
    }

    [TestMethod]
    public async Task CreateFailedOperation_IncludesServerError()
    {
        var http = new FakeHttp()
            .Reply(200, """{"nameAvailable":true}""")
            .Reply(202, "{}", ("Azure-AsyncOperation", "https://management.azure.com/op/1"))
            .Reply(200, """{"status":"Failed","error":{"code":"QuotaExceeded","message":"plan rejects"}}""");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => Support(http).CreateAsync("t", "s", SupportBodyTests.Draft(), default));
        StringAssert.Contains(e.Message, "QuotaExceeded");
        StringAssert.Contains(e.Message, "plan rejects");
        Assert.AreEqual(500, e.Status);
    }
}
