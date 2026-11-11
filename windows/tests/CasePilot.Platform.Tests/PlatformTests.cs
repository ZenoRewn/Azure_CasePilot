using System.Diagnostics;
using System.Text;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using CasePilot.Core.Support;

namespace CasePilot.Platform.Tests;

internal static class Tmp
{
    public static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "CasePilot.Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

[TestClass]
public class AzCliTests
{
    private const string Tid = "72f988bf-86f1-41af-91ab-2d7cd011db47";

    [TestMethod]
    public async Task RejectsBadArgs_BeforeResolvingAz()
    {
        var resolved = 0;
        var az = new AzCli(() => { resolved++; return "C:\\none\\az.cmd"; });
        await Assert.ThrowsExceptionAsync<AppError>(() =>
            az.RunAsync(["account", "get-access-token", "--tenant", "x & calc"], default));
        await Assert.ThrowsExceptionAsync<AppError>(() =>
            az.RunAsync(["account", "show", "|", "calc"], default));
        await Assert.ThrowsExceptionAsync<AppError>(() =>
            az.RunAsync(["account", "get-access-token", "--resource", "https://evil.example/"], default));
        Assert.AreEqual(0, resolved);
    }

    [TestMethod]
    public async Task MissingAz_ThrowsFileNotFound()
    {
        var az = new AzCli(() => null);
        await Assert.ThrowsExceptionAsync<FileNotFoundException>(() => az.RunAsync(["account", "show"], default));
    }

    [TestMethod]
    public void Resolve_PrefersPath_ThenProgramFilesFallback()
    {
        var d = Tmp.Dir();
        var onPath = Path.Combine(d, "az.cmd");
        File.WriteAllText(onPath, "@echo off");
        Assert.AreEqual(onPath, AzCli.Resolve($"C:\\nope;;{d}", "C:\\pf"));

        var pf = Tmp.Dir();
        var fb = Path.Combine(pf, "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(fb)!);
        File.WriteAllText(fb, "@echo off");
        Assert.AreEqual(fb, AzCli.Resolve("C:\\nope", pf));
        Assert.IsNull(AzCli.Resolve("C:\\nope", "C:\\nope2"));
    }

    [TestMethod]
    public async Task RunsScript_ReturnsExitStdoutStderr_PathWithSpaces()
    {
        var d = Path.Combine(Tmp.Dir(), "with space");
        Directory.CreateDirectory(d);
        var script = Path.Combine(d, "az.cmd");
        File.WriteAllText(script, "@echo off\r\necho out:%1 %2 %3\r\necho err 1>&2\r\nexit /b 3\r\n");
        var r = await new AzCli(() => script).RunAsync(["account", "--tenant", Tid], default);
        Assert.AreEqual(3, r.Exit);
        Assert.AreEqual($"out:account --tenant {Tid}", r.Stdout.Trim());
        Assert.AreEqual("err", r.Stderr.Trim());
    }

    [TestMethod]
    public async Task DecodesUtf8Output()
    {
        var d = Tmp.Dir();
        var data = Path.Combine(d, "out.txt");
        File.WriteAllText(data, "{\"tenantDisplayName\":\"微软中国\"}", new UTF8Encoding(false));
        var script = Path.Combine(d, "az.cmd");
        File.WriteAllText(script, $"@echo off\r\ntype \"{data}\"\r\n");
        var r = await new AzCli(() => script).RunAsync(["account", "show"], default);
        StringAssert.Contains(r.Stdout, "微软中国");
    }

    [TestMethod]
    public async Task Timeout_KillsTree_AndThrowsAppError()
    {
        var d = Tmp.Dir();
        var script = Path.Combine(d, "az.cmd");
        File.WriteAllText(script, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n");
        var sw = Stopwatch.StartNew();
        var e = await Assert.ThrowsExceptionAsync<AppError>(() =>
            new AzCli(() => script, TimeSpan.FromSeconds(1)).RunAsync(["account", "show"], default));
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(15), sw.Elapsed.ToString());
        StringAssert.Contains(e.Message, "超时");
    }

    [TestMethod]
    public async Task CallerCancellation_ThrowsOperationCanceled()
    {
        var d = Tmp.Dir();
        var script = Path.Combine(d, "az.cmd");
        File.WriteAllText(script, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            new AzCli(() => script).RunAsync(["account", "show"], cts.Token));
    }
}

[TestClass]
public class CredentialStoreTests
{
    [TestMethod]
    public void RoundTrip_UnderTestPrefix_ThenDeleteViaEmpty()
    {
        var store = new CredentialStore();
        var target = "CasePilot.Test/" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.IsNull(store.Read(target));
            store.Write(target, "秘密-secret-123");
            Assert.AreEqual("秘密-secret-123", store.Read(target));
            store.Write(target, "v2");
            Assert.AreEqual("v2", store.Read(target));
            store.Write(target, "");
            Assert.IsNull(store.Read(target));
            store.Write(target, null); // 删除不存在的条目不报错
        }
        finally { store.Write(target, null); }
    }

    [TestMethod]
    public void RejectsForeignTargets()
    {
        Assert.ThrowsException<ArgumentException>(() => new CredentialStore().Read("git:https://github.com"));
        Assert.ThrowsException<ArgumentException>(() => new CredentialStore().Write("other/x", "v"));
    }
}

[TestClass]
public class JsonSettingsStoreTests
{
    private static string TempFile() => Path.Combine(Tmp.Dir(), "settings.json");

    [TestMethod]
    public void SetGet_PersistsAcrossInstances_NoTempLeftover()
    {
        var f = TempFile();
        var s = new JsonSettingsStore(f);
        Assert.IsNull(s.Get<string>("missing"));
        s.Set("tenant", "租户A");
        s.Set("days", 30);
        var s2 = new JsonSettingsStore(f);
        Assert.AreEqual("租户A", s2.Get<string>("tenant"));
        Assert.AreEqual(30, s2.Get<int>("days"));
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(f)!).Length);
    }

    [TestMethod]
    public void CreatesMissingDirectory()
    {
        var f = Path.Combine(Tmp.Dir(), "a", "b", "settings.json");
        new JsonSettingsStore(f).Set("k", "v");
        Assert.IsTrue(File.Exists(f));
    }

    [TestMethod]
    public void CorruptFile_IsBackedUp_AndStartsEmpty()
    {
        var f = TempFile();
        File.WriteAllText(f, "{not json");
        var s = new JsonSettingsStore(f);
        Assert.IsNull(s.Get<string>("tenant"));
        s.Set("tenant", "t");
        Assert.AreEqual("t", new JsonSettingsStore(f).Get<string>("tenant"));
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(f)!, "settings.json.corrupt-*").Length);
    }

    [TestMethod]
    public void WrongType_ReturnsDefault()
    {
        var s = new JsonSettingsStore(TempFile());
        s.Set("x", "text");
        Assert.AreEqual(0, s.Get<int>("x"));
    }

    [TestMethod]
    public void UnreadableFile_StartsEmpty_NoThrow()
    {
        var f = TempFile();
        File.WriteAllText(f, """{"tenant":"t"}""");
        var fi = new FileInfo(f);
        var acl = fi.GetAccessControl();
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
            System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
        acl.AddAccessRule(deny);
        fi.SetAccessControl(acl);
        try
        {
            Assert.ThrowsException<UnauthorizedAccessException>(() => File.ReadAllText(f));
            Assert.IsNull(new JsonSettingsStore(f).Get<string>("tenant"));
        }
        finally { acl.RemoveAccessRule(deny); fi.SetAccessControl(acl); }
    }

    [TestMethod]
    public void FailedWrite_KeepsMemoryConsistent_NoTmp_ThrowsAppError()
    {
        var f = TempFile();
        var s = new JsonSettingsStore(f);
        s.Set("tenant", "old");
        File.SetAttributes(f, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsException<AppError>(() => s.Set("tenant", "new"));
            Assert.AreEqual("old", s.Get<string>("tenant"));
            Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(f)!).Length);
        }
        finally { File.SetAttributes(f, FileAttributes.Normal); }
    }

    [TestMethod]
    public void DefaultPath_IsLocalAppData()
    {
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CasePilot", "settings.json"), JsonSettingsStore.DefaultPath);
    }
}

[TestClass]
public class WavTests
{
    [TestMethod]
    public void Header_16kMono16bit()
    {
        var w = Wav.Encode([1, 2, 3, 4]);
        Assert.AreEqual(48, w.Length);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(w, 0, 4));
        Assert.AreEqual(40, BitConverter.ToInt32(w, 4));
        Assert.AreEqual("WAVEfmt ", Encoding.ASCII.GetString(w, 8, 8));
        Assert.AreEqual(16, BitConverter.ToInt32(w, 16));
        Assert.AreEqual(1, BitConverter.ToInt16(w, 20));      // PCM
        Assert.AreEqual(1, BitConverter.ToInt16(w, 22));      // 单声道
        Assert.AreEqual(16000, BitConverter.ToInt32(w, 24));
        Assert.AreEqual(32000, BitConverter.ToInt32(w, 28));
        Assert.AreEqual(2, BitConverter.ToInt16(w, 32));
        Assert.AreEqual(16, BitConverter.ToInt16(w, 34));
        Assert.AreEqual("data", Encoding.ASCII.GetString(w, 36, 4));
        Assert.AreEqual(4, BitConverter.ToInt32(w, 40));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, w[44..]);
    }

    [TestMethod]
    public void MaxBytes_Under14MiB_About7Minutes()
    {
        Assert.IsTrue(WaveRecorder.MaxPcmBytes + 44 <= 14 * 1024 * 1024);
        Assert.IsTrue(WaveRecorder.MaxPcmBytes / 32000 >= 420);
    }

    [TestMethod]
    public void Stop_WhenNotRecording_ReturnsEmptyWav()
    {
        var r = new WaveRecorder();
        Assert.IsFalse(r.IsRecording);
        Assert.AreEqual(44, r.Stop().Length);
    }
}

[TestClass]
public class ShellOpsTests
{
    [TestMethod]
    public void OnlyHttpsAndSettingsUrlsAllowed()
    {
        Assert.IsTrue(ShellOps.IsAllowedUrl("https://portal.azure.com/#blade"));
        Assert.IsTrue(ShellOps.IsAllowedUrl("ms-settings:privacy-microphone"));
        Assert.IsFalse(ShellOps.IsAllowedUrl("http://example.com"));
        Assert.IsFalse(ShellOps.IsAllowedUrl("file:///C:/Windows/System32/calc.exe"));
        Assert.IsFalse(ShellOps.IsAllowedUrl("ms-settings:x&calc"));
        Assert.IsFalse(ShellOps.IsAllowedUrl("calc.exe"));
        Assert.IsFalse(ShellOps.IsAllowedUrl(""));
        Assert.ThrowsException<ArgumentException>(() => new ShellOps().OpenUrl("cmd.exe"));
    }
}

[TestClass]
public class SystemServicesTests
{
    [TestMethod]
    public async Task ClockDelayIds()
    {
        Assert.IsTrue(Math.Abs((new SystemClock().UtcNow - DateTimeOffset.UtcNow).TotalSeconds) < 5);
        await new SystemDelay().DelayAsync(TimeSpan.FromMilliseconds(1), default);
        Assert.AreNotEqual(Guid.Empty, new GuidIds().NewId());
    }
}

[TestClass]
public class LiveTests
{
    [TestMethod, TestCategory("Live")]
    public async Task CliLogin_TenantsAndServices_ReadOnly()
    {
        if (Environment.GetEnvironmentVariable("CASEPILOT_LIVE") != "1")
            Assert.Inconclusive("设置 CASEPILOT_LIVE=1 以运行在线测试");
        using var http = new HttpClient();
        var clock = new SystemClock();
        var delay = new SystemDelay();
        var auth = new AuthProvider(http, new AzCli(), clock, delay);
        var id = await auth.LoginCliAsync(default);
        Assert.IsTrue(AzArgs.IsGuid(id.Tenant));
        var sc = new SupportClient(http, auth, delay, new GuidIds(), clock);
        var tenants = await sc.TenantsAsync(default);
        Assert.IsTrue(tenants.Count > 0);
        var services = await sc.ServicesAsync(id.Tenant, default);
        Assert.IsTrue(services.Count > 50, services.Count.ToString());
        Console.WriteLine($"LIVE tenants={tenants.Count} services={services.Count} cloud={id.Cloud}");
    }
}

[TestClass]
public static class Cleanup
{
    [AssemblyCleanup]
    public static void RemoveTempFiles()
    {
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "CasePilot.Test"), recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
