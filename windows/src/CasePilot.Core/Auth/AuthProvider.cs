using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Models;
using CasePilot.Core.Text;

namespace CasePilot.Core.Auth;

/// <summary>
/// 认证: Azure CLI 会话、浏览器设备码登录 (使用 Azure CLI 公共 Client ID, 客户无需建 App) 或 App Registration.
/// refresh token 仅保存在内存中.
/// </summary>
public sealed class AuthProvider(HttpClient http, IAzCli az, IClock clock, IDelay delay) : ILoginService
{
    public const string DeviceClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    public const string Cognitive = "https://cognitiveservices.azure.com";

    private readonly TokenCache _cache = new(clock);
    private string? _refreshToken;
    private (string Tenant, string ClientId, string Secret)? _sp;

    public AuthMode Mode { get; private set; }
    public AzureCloud Cloud { get; private set; } = AzureCloud.Global;
    public string? User { get; private set; }
    public string? HomeTenant { get; private set; }

    // ---- Azure CLI ----

    public async Task<Identity> LoginCliAsync(CancellationToken ct)
    {
        var acct = await RunAzAsync(["account", "show", "-o", "json"], ct);
        var cloud = AzureCloudExtensions.FromEnvironmentName(Str(acct, "environmentName"));
        var user = acct.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Str(u, "name") ?? "-" : "-";
        var tid = Str(acct, "tenantId") ?? "";
        Reset();
        (Mode, Cloud, User, HomeTenant) = (AuthMode.Cli, cloud, user, tid);
        return new Identity(user, tid, Str(acct, "tenantDisplayName"), cloud, "Azure CLI");
    }

    private async Task<JsonElement> RunAzAsync(string[] args, CancellationToken ct)
    {
        AzArgs.Validate(args);
        AzResult r;
        try
        {
            r = await az.RunAsync(args, ct);
        }
        catch (Exception e) when (e is FileNotFoundException or Win32Exception or DirectoryNotFoundException)
        {
            throw new AppError("未找到 Azure CLI (az)。请安装 Azure CLI, 或改用浏览器登录。");
        }
        if (r.Exit != 0)
        {
            var tail = r.Stderr.Length > 600 ? r.Stderr[^600..] : r.Stderr;
            throw new AppError("Azure CLI 错误, 可在终端执行 az login 后重试。\n" + tail);
        }
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AppError("无法解析 Azure CLI 输出");
        }
    }

    // ---- 设备码 ----

    public async Task<DeviceCode> StartDeviceAsync(AzureCloud cloud, CancellationToken ct)
    {
        var j = await PostFormAsync($"{cloud.Login()}/organizations/oauth2/v2.0/devicecode",
        [
            new("client_id", DeviceClientId),
            new("scope", $"{cloud.Arm()}/user_impersonation offline_access openid profile"),
        ], ct);
        Cloud = cloud;
        return new DeviceCode(
            Str(j, "user_code") ?? "", Str(j, "verification_uri") ?? "https://microsoft.com/devicelogin",
            Str(j, "device_code") ?? "", Int(j, "interval") ?? 5,
            clock.UtcNow.AddSeconds(Int(j, "expires_in") ?? 900), Str(j, "message") ?? "");
    }

    /// <summary>轮询直到用户在浏览器完成登录; slow_down 时按 RFC 8628 将间隔加 5 秒.</summary>
    public async Task<Identity> PollDeviceAsync(DeviceCode code, CancellationToken ct)
    {
        var interval = Math.Max(1, code.Interval);
        while (true)
        {
            await delay.DelayAsync(TimeSpan.FromSeconds(interval), ct);
            if (clock.UtcNow >= code.Expires) break;
            JsonElement j;
            try
            {
                j = await PostFormAsync($"{Cloud.Login()}/organizations/oauth2/v2.0/token",
                [
                    new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    new("client_id", DeviceClientId),
                    new("device_code", code.Code),
                ], ct);
            }
            catch (AppError e) when (e.Message.StartsWith("authorization_pending", StringComparison.Ordinal))
            {
                continue;
            }
            catch (AppError e) when (e.Message.StartsWith("slow_down", StringComparison.Ordinal))
            {
                interval += 5;
                continue;
            }
            var claims = Jwt.Claims(Str(j, "id_token") ?? Str(j, "access_token") ?? "");
            var tid = claims.Str("tid") ?? "";
            var user = claims.Str("preferred_username") ?? claims.Str("upn") ?? "-";
            Reset();
            (Mode, User, HomeTenant, _refreshToken) = (AuthMode.Device, user, tid, Str(j, "refresh_token"));
            _cache.Set(tid, Cloud.Arm(), Str(j, "access_token") ?? "", clock.UtcNow.AddSeconds(Int(j, "expires_in") ?? 3600));
            return new Identity(user, tid, null, Cloud, "浏览器登录");
        }
        throw new AppError("验证码已过期, 请重新获取");
    }

    // ---- App Registration (client credentials) ----

    public async Task<Identity> LoginSpAsync(string tenant, string clientId, string secret, AzureCloud cloud, CancellationToken ct)
    {
        var t = tenant.Trim();
        var id = clientId.Trim();
        if (t.Length == 0 || id.Length == 0 || secret.Length == 0)
            throw new AppError("请填写 Tenant ID、Client ID 与 Client Secret");
        Reset();
        (Mode, Cloud, _sp, HomeTenant) = (AuthMode.Sp, cloud, (t, id, secret), t);
        try
        {
            await GetTokenAsync(t, null, ct);
        }
        catch
        {
            Reset();
            throw;
        }
        User = $"App · {id[..Math.Min(8, id.Length)]}…";
        return new Identity(User, t, null, cloud, "App Registration");
    }

    public void Logout() => Reset();

    private void Reset()
    {
        Mode = AuthMode.None;
        _sp = null;
        _refreshToken = null;
        HomeTenant = null;
        User = null;
        _cache.Clear();
    }

    // ---- 令牌 ----

    public async Task<string> GetTokenAsync(string? tenant, string? resource, CancellationToken ct)
    {
        var res = (resource ?? Cloud.Arm()).TrimEnd('/');
        if (_cache.TryGet(tenant, res, out var cached)) return cached;
        string token;
        DateTimeOffset exp;
        switch (Mode)
        {
            case AuthMode.Sp when _sp is { } sp:
            {
                if (tenant is not null && tenant != sp.Tenant) throw new AppError("App Registration 仅能访问其所在租户");
                JsonElement j;
                try
                {
                    j = await PostFormAsync($"{Cloud.Login()}/{sp.Tenant}/oauth2/v2.0/token",
                    [
                        new("grant_type", "client_credentials"), new("client_id", sp.ClientId),
                        new("client_secret", sp.Secret), new("scope", $"{res}/.default"),
                    ], ct);
                }
                catch (AppError e) { throw new AppError($"App Registration 获取令牌失败\n{e.Message}", e.RequestId, e.Status); }
                (token, exp) = (Str(j, "access_token") ?? "", clock.UtcNow.AddSeconds(Int(j, "expires_in") ?? 3600));
                break;
            }
            case AuthMode.Cli:
            {
                var args = new List<string> { "account", "get-access-token", "--resource", res + "/" };
                if (tenant is not null) args.AddRange(["--tenant", tenant]);
                args.AddRange(["-o", "json"]);
                var j = await RunAzAsync([.. args], ct);
                var e = Long(j, "expires_on");
                (token, exp) = (Str(j, "accessToken") ?? "",
                    e is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : clock.UtcNow.AddSeconds(1800));
                break;
            }
            case AuthMode.Device:
            {
                var tid = tenant ?? HomeTenant ?? "organizations";
                JsonElement j;
                try
                {
                    j = await PostFormAsync($"{Cloud.Login()}/{tid}/oauth2/v2.0/token",
                    [
                        new("grant_type", "refresh_token"), new("client_id", DeviceClientId),
                        new("refresh_token", _refreshToken ?? ""), new("scope", $"{res}/user_impersonation offline_access"),
                    ], ct);
                }
                catch (AppError e) { throw new AppError($"无法获取租户 {tid} 的令牌 (可能需要 MFA 或未加入该租户)\n{e.Message}", e.RequestId, e.Status); }
                if (Str(j, "refresh_token") is { } r) _refreshToken = r;
                (token, exp) = (Str(j, "access_token") ?? "", clock.UtcNow.AddSeconds(Int(j, "expires_in") ?? 3600));
                break;
            }
            default:
                throw new AppError("尚未登录");
        }
        _cache.Set(tenant, res, token, exp);
        return token;
    }

    // ---- helpers ----

    private async Task<JsonElement> PostFormAsync(string url, KeyValuePair<string, string>[] fields, CancellationToken ct)
    {
        using var content = new StringContent(FormEncoding.Encode(fields), Encoding.UTF8, "application/x-www-form-urlencoded");
        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await http.PostAsync(url, content, ct);
            text = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw AppError.Timeout(); }
        using var _ = resp;
        JsonElement j;
        try
        {
            using var doc = JsonDocument.Parse(text);
            j = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            j = default;
        }
        if ((int)resp.StatusCode >= 400)
        {
            var desc = Str(j, "error_description") ?? "";
            throw new AppError($"{Str(j, "error") ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture)}: {desc[..Math.Min(400, desc.Length)]}",
                status: (int)resp.StatusCode);
        }
        return j;
    }

    private static string? Str(JsonElement j, string key) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement j, string key) => Long(j, key) is { } l ? (int)l : null;

    /// <summary>数字或数字字符串 (az 不同版本 expires_on 类型不同).</summary>
    private static long? Long(JsonElement j, string key)
    {
        if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (long)d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return (long)s;
        return null;
    }
}
