using System.Text;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using CasePilot.Core.Tests.Fakes;

namespace CasePilot.Core.Tests;

[TestClass]
public class TokenCacheTests
{
    [TestMethod]
    public void ReusesUntil300sBeforeExpiry()
    {
        var clock = new FakeClock();
        var c = new TokenCache(clock);
        c.Set(null, "https://r", "a", clock.UtcNow.AddSeconds(301));
        Assert.IsTrue(c.TryGet(null, "https://r", out var t));
        Assert.AreEqual("a", t);
        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        Assert.IsFalse(c.TryGet(null, "https://r", out _));
    }

    [TestMethod]
    public void KeyedOnTenantAndResource()
    {
        Assert.AreEqual("_|https://r", TokenCache.Key(null, "https://r"));
        Assert.AreEqual("t1|https://r", TokenCache.Key("t1", "https://r"));
        var clock = new FakeClock();
        var c = new TokenCache(clock);
        c.Set("t1", "https://r", "a", clock.UtcNow.AddHours(1));
        Assert.IsFalse(c.TryGet("t2", "https://r", out _));
        Assert.IsFalse(c.TryGet("t1", "https://other", out _));
        c.Clear();
        Assert.IsFalse(c.TryGet("t1", "https://r", out _));
    }
}

[TestClass]
public class AzArgsTests
{
    private const string Tid = "72f988bf-86f1-41af-91ab-2d7cd011db47";

    [TestMethod]
    public void AllowsKnownShapes()
    {
        AzArgs.Validate(["account", "show", "-o", "json"]);
        AzArgs.Validate(["account", "get-access-token", "--resource", "https://management.chinacloudapi.cn/", "--tenant", Tid, "-o", "json"]);
        AzArgs.Validate(["account", "get-access-token", "--resource", "https://cognitiveservices.azure.com/", "-o", "json"]);
    }

    [DataTestMethod]
    [DataRow("72f988bf&calc")]
    [DataRow("not-a-guid")]
    [DataRow("72f988bf-86f1-41af-91ab-2d7cd011db4|")]
    public void RejectsNonGuidTenant(string tenant) =>
        Assert.ThrowsException<AppError>(() => AzArgs.Validate(["account", "get-access-token", "--tenant", tenant]));

    [TestMethod]
    public void RejectsUnknownResourceAndTokens()
    {
        Assert.ThrowsException<AppError>(() => AzArgs.Validate(["account", "get-access-token", "--resource", "https://evil.example/"]));
        Assert.ThrowsException<AppError>(() => AzArgs.Validate(["account", "show", "&", "calc"]));
    }
}

[TestClass]
public class AuthCliTests
{
    private static (AuthProvider, FakeAz, FakeClock) Make()
    {
        var az = new FakeAz();
        var clock = new FakeClock();
        return (new AuthProvider(new FakeHttp().Client(), az, clock, new FakeDelay()), az, clock);
    }

    [TestMethod]
    public async Task LoginParsesAccountIncludingChineseTenantName()
    {
        var (auth, az, _) = Make();
        az.Handler = _ => new AzResult(0, """
            {"environmentName":"AzureChinaCloud","tenantId":"t1","tenantDisplayName":"世纪互联测试租户","user":{"name":"张三@contoso.cn"}}
            """, "");
        var id = await auth.LoginCliAsync(default);
        Assert.AreEqual(new Identity("张三@contoso.cn", "t1", "世纪互联测试租户", AzureCloud.China, "Azure CLI"), id);
        Assert.AreEqual(AuthMode.Cli, auth.Mode);
        Assert.AreEqual(AzureCloud.China, auth.Cloud);
        Assert.AreEqual("t1", auth.HomeTenant);
        CollectionAssert.AreEqual(new[] { "account", "show", "-o", "json" }, az.Calls[0]);
    }

    [TestMethod]
    public async Task TokenRequestsResourceWithSlashAndCachesPerResource()
    {
        var (auth, az, clock) = Make();
        az.Handler = a => a[1] == "show"
            ? new AzResult(0, """{"environmentName":"AzureCloud","tenantId":"t","user":{"name":"u"}}""", "")
            : new AzResult(0, $$"""{"accessToken":"tok-{{a[3]}}","expires_on":{{clock.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""", "");
        await auth.LoginCliAsync(default);
        const string Tid = "11111111-2222-3333-4444-555555555555";
        Assert.AreEqual("tok-https://management.azure.com/", await auth.GetTokenAsync(Tid, null, default));
        Assert.AreEqual("tok-https://management.azure.com/", await auth.GetTokenAsync(Tid, null, default));
        Assert.AreEqual("tok-https://cognitiveservices.azure.com/", await auth.GetTokenAsync(null, AuthProvider.Cognitive, default));
        Assert.AreEqual(3, az.Calls.Count);
        CollectionAssert.AreEqual(new[] { "account", "get-access-token", "--resource", "https://management.azure.com/", "--tenant", Tid, "-o", "json" }, az.Calls[1]);
    }

    [TestMethod]
    public async Task ExpiresOnFallbackIs1800s()
    {
        var (auth, az, clock) = Make();
        az.Handler = a => a[1] == "show"
            ? new AzResult(0, """{"tenantId":"t","user":{"name":"u"}}""", "")
            : new AzResult(0, """{"accessToken":"x"}""", "");
        await auth.LoginCliAsync(default);
        await auth.GetTokenAsync(null, null, default);
        clock.UtcNow = clock.UtcNow.AddSeconds(1800 - 301);
        await auth.GetTokenAsync(null, null, default);
        Assert.AreEqual(2, az.Calls.Count); // still cached
        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        await auth.GetTokenAsync(null, null, default);
        Assert.AreEqual(3, az.Calls.Count);
    }

    [TestMethod]
    public async Task ExpiresOnAsStringIsAccepted()
    {
        var (auth, az, clock) = Make();
        var exp = clock.UtcNow.AddSeconds(400).ToUnixTimeSeconds();
        az.Handler = a => a[1] == "show"
            ? new AzResult(0, """{"tenantId":"t","user":{"name":"u"}}""", "")
            : new AzResult(0, $$"""{"accessToken":"x","expires_on":"{{exp}}"}""", "");
        await auth.LoginCliAsync(default);
        await auth.GetTokenAsync(null, null, default);
        clock.UtcNow = clock.UtcNow.AddSeconds(150);
        await auth.GetTokenAsync(null, null, default);
        Assert.AreEqual(3, az.Calls.Count); // 400 - 150 < 300 so refreshed
    }

    [TestMethod]
    public async Task MissingAzIsAppError()
    {
        var (auth, az, _) = Make();
        az.Handler = _ => throw new FileNotFoundException("az");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => auth.LoginCliAsync(default));
        StringAssert.Contains(e.Message, "未找到 Azure CLI");
    }

    [TestMethod]
    public async Task NonZeroExitIsAppErrorWithStderrTail()
    {
        var (auth, az, _) = Make();
        az.Handler = _ => new AzResult(1, "", "ERROR: Please run 'az login'");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => auth.LoginCliAsync(default));
        StringAssert.StartsWith(e.Message, "Azure CLI 错误, 可在终端执行 az login 后重试。");
        StringAssert.Contains(e.Message, "Please run 'az login'");
    }

    [TestMethod]
    public async Task NonGuidTenantNeverReachesAz()
    {
        var (auth, az, _) = Make();
        az.Handler = _ => new AzResult(0, """{"tenantId":"t","user":{"name":"u"}}""", "");
        await auth.LoginCliAsync(default);
        await Assert.ThrowsExceptionAsync<AppError>(() => auth.GetTokenAsync("x & calc", null, default));
        Assert.AreEqual(1, az.Calls.Count);
    }

    [TestMethod]
    public async Task NotLoggedIn()
    {
        var (auth, _, _) = Make();
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => auth.GetTokenAsync(null, null, default));
        Assert.AreEqual("尚未登录", e.Message);
    }
}

[TestClass]
public class AuthDeviceTests
{
    internal static string Jwt(string json) =>
        "h." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s";

    [TestMethod]
    public async Task StartThenPollHandlesPendingAndSlowDown()
    {
        var clock = new FakeClock();
        var delay = new FakeDelay();
        var http = new FakeHttp()
            .Reply(200, """{"user_code":"ABC","verification_uri":"https://microsoft.com/devicelogin","device_code":"dc","interval":5,"expires_in":900,"message":"m"}""")
            .Reply(400, """{"error":"authorization_pending","error_description":"wait"}""")
            .Reply(400, """{"error":"slow_down"}""")
            .Reply(200, $$"""{"access_token":"at","refresh_token":"r1","expires_in":3600,"id_token":"{{Jwt("""{"tid":"home","preferred_username":"u@x"}""")}}"}""");
        var auth = new AuthProvider(http.Client(), new FakeAz(), clock, delay);

        var code = await auth.StartDeviceAsync(AzureCloud.Global, default);
        Assert.AreEqual("ABC", code.UserCode);
        Assert.AreEqual("https://login.microsoftonline.com/organizations/oauth2/v2.0/devicecode", http.Requests[0].Url);
        StringAssert.Contains(http.Requests[0].Body, "scope=https%3A%2F%2Fmanagement.azure.com%2Fuser_impersonation%20offline_access%20openid%20profile");
        StringAssert.Contains(http.Requests[0].Body, "client_id=04b07795-8ddb-461a-bbee-02f9e1bf7b46");

        var id = await auth.PollDeviceAsync(code, default);
        Assert.AreEqual(new Identity("u@x", "home", null, AzureCloud.Global, "浏览器登录"), id);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) }, delay.Calls);
        StringAssert.Contains(http.Requests[1].Body, "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code");
        Assert.AreEqual("application/x-www-form-urlencoded", http.Requests[1].Headers["Content-Type"].Split(';')[0]);

        // 首个令牌已缓存, 无需再请求
        Assert.AreEqual("at", await auth.GetTokenAsync("home", null, default));
        Assert.AreEqual(4, http.Requests.Count);
    }

    [TestMethod]
    public async Task PollOtherErrorPropagates()
    {
        var http = new FakeHttp().Reply(400, """{"error":"expired_token","error_description":"gone"}""");
        var auth = new AuthProvider(http.Client(), new FakeAz(), new FakeClock(), new FakeDelay());
        var code = new DeviceCode("A", "https://x", "dc", 5, new FakeClock().UtcNow.AddMinutes(15), "");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => auth.PollDeviceAsync(code, default));
        Assert.AreEqual("expired_token: gone", e.Message);
    }

    [TestMethod]
    public async Task PollExpires()
    {
        var clock = new FakeClock();
        var http = new FakeHttp().Reply(400, """{"error":"authorization_pending"}""");
        var auth = new AuthProvider(http.Client(), new FakeAz(), clock, new FakeDelay(clock));
        var code = new DeviceCode("A", "https://x", "dc", 5, clock.UtcNow.AddSeconds(8), "");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => auth.PollDeviceAsync(code, default));
        Assert.AreEqual("验证码已过期, 请重新获取", e.Message);
    }

    [TestMethod]
    public async Task RefreshTokenRotates()
    {
        var http = new FakeHttp()
            .Reply(200, $$"""{"access_token":"at","refresh_token":"r1","expires_in":3600,"id_token":"{{Jwt("""{"tid":"home","upn":"u"}""")}}"}""")
            .Reply(200, """{"access_token":"a2","refresh_token":"r2","expires_in":3600}""")
            .Reply(200, """{"access_token":"a3","expires_in":3600}""")
            .Reply(200, """{"access_token":"a4","expires_in":3600}""");
        var auth = new AuthProvider(http.Client(), new FakeAz(), new FakeClock(), new FakeDelay());
        var id = await auth.PollDeviceAsync(new DeviceCode("A", "https://x", "dc", 1, new FakeClock().UtcNow.AddMinutes(15), ""), default);
        Assert.AreEqual("u", id.User);

        Assert.AreEqual("a2", await auth.GetTokenAsync("other", null, default));
        Assert.AreEqual("https://login.microsoftonline.com/other/oauth2/v2.0/token", http.Requests[1].Url);
        StringAssert.Contains(http.Requests[1].Body, "refresh_token=r1");
        StringAssert.Contains(http.Requests[1].Body, "scope=https%3A%2F%2Fmanagement.azure.com%2Fuser_impersonation%20offline_access");

        Assert.AreEqual("a3", await auth.GetTokenAsync(null, AuthProvider.Cognitive, default));
        Assert.AreEqual("https://login.microsoftonline.com/home/oauth2/v2.0/token", http.Requests[2].Url);
        StringAssert.Contains(http.Requests[2].Body, "refresh_token=r2");

        // 响应中无新的 refresh_token 时保留旧值
        await auth.GetTokenAsync("third", null, default);
        StringAssert.Contains(http.Requests[3].Body, "refresh_token=r2");
    }

    [TestMethod]
    public async Task RefreshFailureExplainsTenant()
    {
        var http = new FakeHttp()
            .Reply(200, $$"""{"access_token":"at","refresh_token":"r1","id_token":"{{Jwt("""{"tid":"home"}""")}}"}""")
            .Reply(400, """{"error":"interaction_required","error_description":"AADSTS50076 MFA"}""");
        var auth = new AuthProvider(http.Client(), new FakeAz(), new FakeClock(), new FakeDelay());
        await auth.PollDeviceAsync(new DeviceCode("A", "https://x", "dc", 1, new FakeClock().UtcNow.AddMinutes(15), ""), default);
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => auth.GetTokenAsync("t9", null, default));
        StringAssert.StartsWith(e.Message, "无法获取租户 t9 的令牌 (可能需要 MFA 或未加入该租户)");
        StringAssert.Contains(e.Message, "AADSTS50076");
    }

    [TestMethod]
    public async Task LogoutClearsEverything()
    {
        var http = new FakeHttp()
            .Reply(200, $$"""{"access_token":"at","refresh_token":"r1","id_token":"{{Jwt("""{"tid":"home"}""")}}"}""");
        var auth = new AuthProvider(http.Client(), new FakeAz(), new FakeClock(), new FakeDelay());
        await auth.PollDeviceAsync(new DeviceCode("A", "https://x", "dc", 1, new FakeClock().UtcNow.AddMinutes(15), ""), default);
        auth.Logout();
        Assert.AreEqual(AuthMode.None, auth.Mode);
        Assert.IsNull(auth.HomeTenant);
        await Assert.ThrowsExceptionAsync<AppError>(() => auth.GetTokenAsync("home", null, default));
    }
}

[TestClass]
public class AuthSpTests
{
    [TestMethod]
    public async Task UsesDefaultScopeAndChecksToken()
    {
        var http = new FakeHttp()
            .Reply(200, """{"access_token":"s1","expires_in":3600}""")
            .Reply(200, """{"access_token":"s2","expires_in":3600}""");
        var auth = new AuthProvider(http.Client(), new FakeAz(), new FakeClock(), new FakeDelay());
        var id = await auth.LoginSpAsync(" tid ", " 0123456789ab ", "p+w", AzureCloud.China, default);
        Assert.AreEqual(new Identity("App · 01234567…", "tid", null, AzureCloud.China, "App Registration"), id);
        Assert.AreEqual("https://login.chinacloudapi.cn/tid/oauth2/v2.0/token", http.Requests[0].Url);
        Assert.AreEqual("grant_type=client_credentials&client_id=0123456789ab&client_secret=p%2Bw&scope=https%3A%2F%2Fmanagement.chinacloudapi.cn%2F.default",
            http.Requests[0].Body);
        Assert.AreEqual("s1", await auth.GetTokenAsync("tid", null, default));
        Assert.AreEqual("s2", await auth.GetTokenAsync(null, AuthProvider.Cognitive, default));
        StringAssert.EndsWith(http.Requests[1].Body, "scope=https%3A%2F%2Fcognitiveservices.azure.com%2F.default");
    }

    [TestMethod]
    public async Task ForeignTenantThrows()
    {
        var http = new FakeHttp().Reply(200, """{"access_token":"s1"}""");
        var auth = new AuthProvider(http.Client(), new FakeAz(), new FakeClock(), new FakeDelay());
        await auth.LoginSpAsync("tid", "c", "s", AzureCloud.Global, default);
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => auth.GetTokenAsync("other", null, default));
        Assert.AreEqual("App Registration 仅能访问其所在租户", e.Message);
    }

    [TestMethod]
    public async Task MissingFieldsAndFailedLoginResetMode()
    {
        var http = new FakeHttp().Reply(401, """{"error":"invalid_client","error_description":"bad secret"}""");
        var auth = new AuthProvider(http.Client(), new FakeAz(), new FakeClock(), new FakeDelay());
        var e1 = await Assert.ThrowsExceptionAsync<AppError>(() => auth.LoginSpAsync("t", "", "s", AzureCloud.Global, default));
        Assert.AreEqual("请填写 Tenant ID、Client ID 与 Client Secret", e1.Message);
        var e2 = await Assert.ThrowsExceptionAsync<AppError>(() => auth.LoginSpAsync("t", "c", "s", AzureCloud.Global, default));
        StringAssert.StartsWith(e2.Message, "App Registration 获取令牌失败\ninvalid_client: bad secret");
        Assert.AreEqual(AuthMode.None, auth.Mode);
    }
}
