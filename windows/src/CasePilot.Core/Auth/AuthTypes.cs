using System.Text.RegularExpressions;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Models;

namespace CasePilot.Core.Auth;

public enum AuthMode { None, Cli, Device, Sp }

public sealed record Identity(string User, string Tenant, string? TenantName, AzureCloud Cloud, string Method);

public sealed record DeviceCode(string UserCode, string VerificationUrl, string Code, int Interval, DateTimeOffset Expires, string Message);

public interface IAuthProvider
{
    AuthMode Mode { get; }
    AzureCloud Cloud { get; }
    string? User { get; }
    string? HomeTenant { get; }

    /// <summary>resource 为 null 时取当前云的 ARM; AI / 语音使用 cognitiveservices.</summary>
    Task<string> GetTokenAsync(string? tenant, string? resource, CancellationToken ct);
}

public sealed record AzResult(int Exit, string Stdout, string Stderr);

/// <summary>运行 Azure CLI. 找不到 az 时抛出 FileNotFoundException (或 Win32Exception).</summary>
public interface IAzCli
{
    Task<AzResult> RunAsync(string[] args, CancellationToken ct);
}

/// <summary>
/// az 参数白名单. Windows 上 az 是 az.cmd, 经 cmd.exe 执行时元字符可被注入,
/// 因此只放行固定子命令、GUID 与已知资源 URL.
/// </summary>
public static partial class AzArgs
{
    private static readonly HashSet<string> Words =
        ["account", "show", "get-access-token", "--resource", "--tenant", "-o", "json"];

    public static IReadOnlySet<string> Resources { get; } = new HashSet<string>(
        Enum.GetValues<AzureCloud>().Select(c => c.Arm() + "/").Append(AuthProvider.Cognitive + "/"));

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidRegex();

    public static bool IsGuid(string? s) => s is not null && GuidRegex().IsMatch(s);

    public static void Validate(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var prev = i > 0 ? args[i - 1] : null;
            var ok = prev switch
            {
                "--tenant" => IsGuid(a),
                "--resource" => Resources.Contains(a),
                _ => Words.Contains(a),
            };
            if (!ok) throw new AppError($"Azure CLI 参数非法: {a}");
        }
    }
}

/// <summary>令牌缓存, 键为 tenant|resource; 距过期不足 300 秒视为失效.</summary>
public sealed class TokenCache(IClock clock)
{
    public static readonly TimeSpan Skew = TimeSpan.FromSeconds(300);
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _items = [];

    public static string Key(string? tenant, string resource) => (tenant ?? "_") + "|" + resource;

    public bool TryGet(string? tenant, string resource, out string token)
    {
        lock (_items)
        {
            if (_items.TryGetValue(Key(tenant, resource), out var e) && e.Expires - clock.UtcNow > Skew)
            {
                token = e.Token;
                return true;
            }
        }
        token = "";
        return false;
    }

    public void Set(string? tenant, string resource, string token, DateTimeOffset expires)
    {
        lock (_items) _items[Key(tenant, resource)] = (token, expires);
    }

    public void Clear()
    {
        lock (_items) _items.Clear();
    }
}

/// <summary>登录流程 (AuthProvider 实现; ViewModel 测试使用假实现).</summary>
public interface ILoginService : IAuthProvider
{
    Task<Identity> LoginCliAsync(CancellationToken ct);
    Task<DeviceCode> StartDeviceAsync(AzureCloud cloud, CancellationToken ct);
    Task<Identity> PollDeviceAsync(DeviceCode code, CancellationToken ct);
    Task<Identity> LoginSpAsync(string tenant, string clientId, string secret, AzureCloud cloud, CancellationToken ct);
    void Logout();
}
