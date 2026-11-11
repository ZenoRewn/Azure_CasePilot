using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;

namespace CasePilot.Platform;

/// <summary>
/// 通过 <c>cmd.exe /d /s /c ""az.cmd" args"</c> 运行 Azure CLI.
/// 参数先经 <see cref="AzArgs.Validate"/> 白名单校验 (GUID 与固定资源 URL), 因此不含 cmd 元字符.
/// </summary>
public sealed class AzCli(Func<string?>? resolve = null, TimeSpan? timeout = null) : IAzCli
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<string?> _resolve = resolve ?? (() =>
        Resolve(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    /// <summary>先在 PATH 中找 az.cmd, 否则回退到 %ProgramFiles%\Microsoft SDKs\Azure\CLI2\wbin\az.cmd.</summary>
    public static string? Resolve(string? pathEnv, string? programFiles)
    {
        foreach (var dir in (pathEnv ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), "az.cmd");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        if (string.IsNullOrEmpty(programFiles)) return null;
        var fb = Path.Combine(programFiles, "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd");
        return File.Exists(fb) ? fb : null;
    }

    public async Task<AzResult> RunAsync(string[] args, CancellationToken ct)
    {
        AzArgs.Validate(args);
        var az = _resolve() ?? throw new FileNotFoundException("未找到 Azure CLI (az)。", "az.cmd");
        if (az.Contains('"') || az.IndexOfAny(['&', '|', '<', '>', '^', '%', '\r', '\n']) >= 0)
            throw new AppError($"Azure CLI 路径非法: {az}");

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /s /c \"\"{az}\" {string.Join(' ', args)}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["AZURE_CORE_NO_COLOR"] = "1";
        psi.Environment["AZURE_CORE_ONLY_SHOW_ERRORS"] = "1";

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Win32Exception e) { throw new FileNotFoundException("无法启动 Azure CLI。", az, e); }
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await p.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            throw new AppError($"Azure CLI 超时 ({(int)_timeout.TotalSeconds} 秒), 已终止。");
        }
        return new AzResult(p.ExitCode, await stdout, await stderr);
    }
}
