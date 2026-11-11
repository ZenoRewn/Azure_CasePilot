using System.IO;
using CasePilot.App.Tests.Fakes;
using CasePilot.App.ViewModels;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Models;
using static CasePilot.App.Tests.Fakes.Data;

namespace CasePilot.App.Tests;

[TestClass]
public class DisplayTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(-20, "刚刚")]
    [DataRow(-5 * 60, "5 分钟前")]
    [DataRow(-3 * 3600, "3 小时前")]
    [DataRow(-2 * 86400, "2 天前")]
    [DataRow(-45 * 86400, "1 个月前")]
    [DataRow(-400 * 86400, "1 年前")]
    public void Relative_time(int seconds, string expected) =>
        Assert.AreEqual(expected, Display.Relative(Now.AddSeconds(seconds), Now));

    [TestMethod]
    public void RowAutomationName_ReadsAllColumns()
    {
        var t = new TicketSummary("x", "2511110010001234", "生产环境 VM 无法启动", "Open", "critical", Now.AddHours(-3), "VM", "Boot", null);
        var name = Display.RowName(t, Now);
        Assert.AreEqual("2511110010001234, 生产环境 VM 无法启动, 严重性 " + Display.SeverityLabel("critical") + ", " + Display.StatusLabel("Open") + ", 3 小时前", name);
    }

    [TestMethod]
    public void Relative_time_of_null_is_empty() => Assert.AreEqual("", Display.Relative(null, Now));

    [TestMethod]
    [DataRow("Open", "处理中", PillKind.Success)]
    [DataRow("Closed", "已关闭", PillKind.Neutral)]
    [DataRow("Weird", "Weird", PillKind.Neutral)]
    public void Status_pill(string status, string label, PillKind kind)
    {
        Assert.AreEqual(label, Display.StatusLabel(status));
        Assert.AreEqual(kind, Display.StatusKind(status));
    }

    [TestMethod]
    [DataRow("minimal", "C", PillKind.Neutral)]
    [DataRow("moderate", "B", PillKind.Warning)]
    [DataRow("critical", "A", PillKind.Critical)]
    [DataRow("highestcriticalimpact", "A+", PillKind.Critical)]
    [DataRow("", "-", PillKind.Neutral)]
    public void Severity_pill(string api, string label, PillKind kind)
    {
        Assert.AreEqual(label, Display.SeverityLabel(api));
        Assert.AreEqual(kind, Display.SeverityKind(api));
    }

    [TestMethod]
    public void Subtitle_joins_service_and_last_problem_segment()
    {
        var t = new TicketSummary("n", "1", "t", "Open", "minimal", null, "VM", "Connect / RDP", null);
        Assert.AreEqual("VM · RDP", Display.Subtitle(t));
        Assert.AreEqual("VM", Display.Subtitle(t with { Problem = "" }));
    }

    [TestMethod]
    public void Initials_from_user_name()
    {
        Assert.AreEqual("Z", Display.Initials("zhengyu@contoso.com"));
        Assert.AreEqual("?", Display.Initials(""));
    }
}

sealed class FakeRecorder : IAudioRecorder
{
    public bool IsRecording { get; private set; }
    public Exception? StartError { get; set; }
    public void Start() { if (StartError is not null) throw StartError; IsRecording = true; }
    public byte[] Stop() { IsRecording = false; return [1, 2, 3]; }
}

sealed class FakeTranscriber : ITranscriber
{
    public string Result { get; set; } = "你好";
    public Exception? Error { get; set; }
    public (byte[] Audio, string Name, string Mime)? Got { get; private set; }
    public Task<string> TranscribeAsync(byte[] audio, string filename, string mime, CancellationToken ct)
    {
        Got = (audio, filename, mime);
        return Error is null ? Task.FromResult(Result) : Task.FromException<string>(Error);
    }
}

[TestClass]
public class MicVMTests
{
    [TestMethod]
    public async Task Click_starts_then_stops_transcribes_and_inserts()
    {
        var rec = new FakeRecorder(); var tr = new FakeTranscriber();
        string? inserted = null;
        var vm = new MicVM(rec, tr, s => inserted = s);
        await vm.ToggleAsync();
        Assert.AreEqual(MicState.Recording, vm.State);
        Assert.AreEqual("结束并转写", vm.Tooltip);
        await vm.ToggleAsync();
        Assert.AreEqual(MicState.Idle, vm.State);
        Assert.AreEqual("你好", inserted);
        Assert.AreEqual("voice.wav", tr.Got!.Value.Name);
        Assert.AreEqual("audio/wav", tr.Got!.Value.Mime);
        Assert.AreEqual("语音输入", vm.Tooltip);
    }

    [TestMethod]
    public async Task Start_failure_shows_permission_hint_and_settings_action()
    {
        var rec = new FakeRecorder { StartError = new InvalidOperationException("无法打开麦克风") };
        var os = new FakeShell();
        var vm = new MicVM(rec, new FakeTranscriber(), _ => { }, os);
        await vm.ToggleAsync();
        Assert.AreEqual(MicState.Idle, vm.State);
        StringAssert.Contains(vm.Error, "无法打开麦克风");
        Assert.IsTrue(vm.PermissionHint);
        vm.OpenPrivacyCommand.Execute(null);
        CollectionAssert.Contains(os.Opened, MicVM.PrivacyUrl);
    }

    [TestMethod]
    public async Task Transcription_error_is_shown_without_permission_hint()
    {
        var vm = new MicVM(new FakeRecorder(), new FakeTranscriber { Error = new Exception("401") }, _ => { });
        await vm.ToggleAsync();
        await vm.ToggleAsync();
        Assert.AreEqual("401", vm.Error);
        Assert.IsFalse(vm.PermissionHint);
        Assert.AreEqual(MicState.Idle, vm.State);
    }

    [TestMethod]
    public void Elapsed_text_formats_minutes_and_seconds() =>
        Assert.AreEqual("01:05", MicVM.FormatElapsed(TimeSpan.FromSeconds(65)));

    [TestMethod]
    [DataRow("", 0, "你好", "你好", 2)]
    [DataRow("ab", 1, "X", "a X b", 4)]
    [DataRow("ab", 2, "X", "ab X", 4)]
    public void Insert_at_caret_pads_with_spaces(string text, int caret, string insert, string expected, int newCaret)
    {
        var (t, c) = MicVM.InsertAt(text, caret, insert);
        Assert.AreEqual(expected, t);
        Assert.AreEqual(newCaret, c);
    }
}

[TestClass]
public class SelfTestTests
{
    [TestMethod]
    public async Task Runs_read_only_steps_and_returns_zero()
    {
        var api = Api();
        api.Problems["vm"] = [VmBoot];
        api.Tickets = [Ticket("a", "Open", 1)];
        api.Detail = new TicketDetail(api.Tickets[0], new System.Text.Json.Nodes.JsonObject(), []);
        var w = new StringWriter();
        var code = await SelfTest.RunAsync(api, new FakeLogin { Cli = () => Cli() }, w);
        Assert.AreEqual(0, code, w.ToString());
        var o = w.ToString();
        foreach (var step in new[] { "login", "tenants", "subscriptions", "services", "problems", "tickets(30)", "detail", "plan", "body" })
            StringAssert.Contains(o, "[ok] " + step);
        Assert.AreEqual(0, api.Created.Count);
        Assert.AreEqual(0, api.Replies.Count);
        Assert.AreEqual(0, api.Closed.Count);
    }

    [TestMethod]
    public async Task Failure_prints_fail_and_returns_one()
    {
        var w = new StringWriter();
        var code = await SelfTest.RunAsync(Api(), new FakeLogin { Cli = () => throw new AppError("az 未登录") }, w);
        Assert.AreEqual(1, code);
        StringAssert.Contains(w.ToString(), "[fail] login: az 未登录");
    }
}

[TestClass]
public class CrashLogTests
{
    [TestMethod]
    public void Append_WritesTimestampedEntries_AndNeverThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CasePilot.Test", Guid.NewGuid().ToString("N"));
        var f = Path.Combine(dir, "crash.log");
        CrashLog.Append(new InvalidOperationException("boom"), "Dispatcher", f);
        CrashLog.Append(new AppError("二次"), "Task", f);
        var text = File.ReadAllText(f);
        StringAssert.Contains(text, "[Dispatcher]");
        StringAssert.Contains(text, "InvalidOperationException: boom");
        StringAssert.Contains(text, "二次");
        CrashLog.Append(new Exception("x"), "s", Path.Combine(f, "not-a-dir", "x.log")); // 路径无效也不抛
    }

    [TestMethod]
    public void DefaultPath_IsLocalAppData() =>
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CasePilot", "crash.log"), CrashLog.DefaultPath);
}

[TestClass]
public class PillNameTests
{
    [TestMethod]
    public void AccessibleName_PrefixesLabel() =>
        Assert.AreEqual("严重性 A", CasePilot.App.Controls.Pill.AccessibleName("严重性", "A"));

    [TestMethod]
    public void AccessibleName_WithoutLabel_IsText() =>
        Assert.AreEqual("处理中", CasePilot.App.Controls.Pill.AccessibleName(null, "处理中"));
}

[TestClass]
public class HistoryLayoutTests
{
    [TestMethod]
    [DataRow(900.0, true)]
    [DataRow(HistoryLayout.CreatedMinGridWidth, true)]
    [DataRow(HistoryLayout.CreatedMinGridWidth - 1, false)]
    [DataRow(480.0, false)]
    public void ShowCreated_DependsOnGridWidth(double width, bool expected) =>
        Assert.AreEqual(expected, HistoryLayout.ShowCreated(width));

    [TestMethod]
    public void ShowCreated_ZeroWidth_NotMeasuredYet_KeepsColumn() =>
        Assert.IsTrue(HistoryLayout.ShowCreated(0));
}
