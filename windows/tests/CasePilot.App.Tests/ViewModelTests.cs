using System.Text.Json.Nodes;
using CasePilot.App.Tests.Fakes;
using CasePilot.App.ViewModels;
using CasePilot.Core.AI;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using static CasePilot.App.Tests.Fakes.Data;

namespace CasePilot.App.Tests;

[TestClass]
public class CreateCaseVMTests
{
    sealed record Ctx(CreateCaseVM Vm, ShellVM Shell, FakeApi Api, FakeChat Chat, FakeDialogs Dialogs, FakeSecrets Secrets);

    static async Task<Ctx> Make(bool contact = true)
    {
        var api = Api(); var settings = new FakeSettings(); var chat = new FakeChat(); var dialogs = new FakeDialogs(); var secrets = new FakeSecrets();
        var shell = new ShellVM(api, new FakeLogin(), settings);
        if (contact) shell.Contact = GoodContact;
        await shell.AfterLoginAsync(Cli());
        return new(new CreateCaseVM(shell, api, chat, dialogs, secrets, () => new AIConfig()), shell, api, chat, dialogs, secrets);
    }

    static async Task Fill(CreateCaseVM vm)
    {
        await vm.PickAsync(Vm);
        vm.Problem = VmBoot;
        vm.Title = "虚拟机无法启动";
        vm.Description = new string('现', 20);
    }

    [TestMethod]
    public async Task Checklist_GatesSubmit()
    {
        var c = await Make();
        Assert.IsFalse(c.Vm.CanSubmit);
        Assert.AreEqual("还差 3 项: 问题类型、标题、描述 (≥20 字)", c.Vm.MissingText);
        await Fill(c.Vm);
        Assert.IsTrue(c.Vm.CanSubmit);
        Assert.AreEqual("可以提交", c.Vm.MissingText);
        c.Vm.Description = "短";
        Assert.IsFalse(c.Vm.CanSubmit);
        CollectionAssert.AreEqual(new[] { "描述 (≥20 字)" }, c.Vm.Missing.ToList());
    }

    [TestMethod]
    public async Task Checklist_RequiresTitleOfFiveTrimmedChars_AndValidContact()
    {
        var c = await Make(contact: false);
        await Fill(c.Vm);
        c.Vm.Title = "  abcd  ";
        CollectionAssert.AreEqual(new[] { "标题", "联系人" }, c.Vm.Missing.ToList());
    }

    [TestMethod]
    public async Task Title_TruncatedTo200()
    {
        var c = await Make();
        c.Vm.Title = new string('a', 250);
        Assert.AreEqual(200, c.Vm.Title.Length);
        Assert.AreEqual("200/200", c.Vm.TitleCounter);
    }

    [TestMethod]
    public async Task Severity_High_Enables24x7_AndLowDisables()
    {
        var c = await Make();
        Assert.IsFalse(c.Vm.Can24x7);
        c.Vm.Severity = Severity.Critical;
        Assert.IsTrue(c.Vm.Require24x7);
        Assert.IsTrue(c.Vm.Can24x7);
        c.Vm.Require24x7 = false;
        Assert.IsFalse(c.Vm.Require24x7);
        c.Vm.Severity = Severity.Minimal;
        Assert.IsFalse(c.Vm.Require24x7);
        c.Vm.Require24x7 = true;
        Assert.IsFalse(c.Vm.Require24x7);
        Assert.AreEqual("功能受损但不影响业务", c.Vm.SeverityCaption);
    }

    [TestMethod]
    public async Task OverPlanWarning_WhenPlanDisallowsCritical()
    {
        var c = await Make();
        c.Shell.Plan = new Core.Plan.SupportPlan("Developer", "developer-or-basic", "ticket", false);
        c.Vm.Severity = Severity.Moderate;
        Assert.IsFalse(c.Vm.OverPlan);
        c.Vm.Severity = Severity.HighestCriticalImpact;
        Assert.IsTrue(c.Vm.OverPlan);
        Assert.AreEqual("当前支持计划不支持该级别", c.Vm.OverPlanText);
    }

    [TestMethod]
    public async Task Quota_AutoPicksCoresProblem_AndRequiresQuotaDetails()
    {
        var c = await Make();
        await c.Vm.PickAsync(Quota);
        Assert.AreEqual(CaseKind.Quota, c.Vm.Kind);
        Assert.AreEqual(VmCores, c.Vm.Problem);
        Assert.IsTrue(c.Vm.IsVmQuota);
        c.Vm.Title = "提高 DSv5 配额"; c.Vm.Description = new string('x', 25);
        CollectionAssert.AreEqual(new[] { "配额明细" }, c.Vm.Missing.ToList());
        c.Vm.QuotaRegion = "eastus"; c.Vm.QuotaFamily = "DSv5 Series"; c.Vm.QuotaLimit = "0";
        Assert.IsFalse(c.Vm.CanSubmit);
        c.Vm.QuotaLimit = "100";
        Assert.IsTrue(c.Vm.CanSubmit);
        var body = JsonNode.Parse(c.Vm.PreviewJson())!;
        Assert.AreEqual("{\"NewLimit\":100,\"VMFamily\":\"DSv5 Series\"}", (string?)body["properties"]!["quotaTicketDetails"]!["quotaChangeRequests"]![0]!["payload"]);
    }

    [TestMethod]
    public async Task Pick_Technical_LoadsResources_RemembersService()
    {
        var c = await Make();
        c.Api.Resources = [new("/subscriptions/x/rg/vm1", "vm1", "microsoft.compute/virtualmachines", "eastus")];
        await c.Vm.PickAsync(Vm, "/subscriptions/x/rg/vm1");
        Assert.AreEqual(1, c.Vm.Resources.Count);
        Assert.AreEqual("/subscriptions/x/rg/vm1", c.Vm.ResourceId);
        Assert.AreEqual(2, c.Vm.Problems.Count);
        Assert.AreEqual(Vm.DisplayName, c.Shell.RecentServices[0]);
    }

    [TestMethod]
    public async Task QuickServices_QuotaBillingThenPopular()
    {
        var c = await Make();
        CollectionAssert.AreEqual(new[] { "配额申请", "计费 / 订阅", "Virtual Machine running Linux" },
            c.Vm.QuickServices.Select(q => q.Label).ToList());
    }

    [TestMethod]
    public async Task ApplyDraft_FillsForm()
    {
        var c = await Make();
        await c.Vm.ApplyDraftAsync(new AIDraft(Vm, VmBoot, Severity.Critical, "VM 无法 SSH", "描述内容", []));
        Assert.AreEqual(Vm, c.Vm.Service);
        Assert.AreEqual(VmBoot, c.Vm.Problem);
        Assert.AreEqual(Severity.Critical, c.Vm.Severity);
        Assert.IsTrue(c.Vm.Require24x7);
        Assert.AreEqual("VM 无法 SSH", c.Vm.Title);
        Assert.AreEqual("描述内容", c.Vm.Description);
    }

    [TestMethod]
    public async Task AiDraft_WithQuestions_ThenAnswer_ThenSkip()
    {
        var c = await Make();
        c.Chat.Replies.Enqueue("""{"service":"vm","severity":"moderate","title":"VM 无法启动","description":"d","questions":["哪个区域?","何时开始?"]}""");
        c.Chat.Replies.Enqueue("""{"problemId":"pVmBoot"}""");
        c.Vm.AiText = "vm 起不来";
        await c.Vm.GenerateAsync();
        Assert.AreEqual(2, c.Vm.Questions.Count);
        Assert.AreEqual("哪个区域?", c.Vm.Questions[0].Question);
        Assert.AreEqual("补充并更新", c.Vm.GenerateLabel);
        Assert.AreEqual("已生成初稿, 回答下面的问题可让描述更完整", c.Vm.AiStatus);
        Assert.IsFalse(c.Vm.AiStatusOk);

        c.Vm.Questions[0].Answer = "eastus";
        c.Chat.Replies.Enqueue("""{"service":"vm","severity":"moderate","title":"VM 无法启动","description":"d2"}""");
        c.Chat.Replies.Enqueue("""{"problemId":"pVmBoot"}""");
        await c.Vm.GenerateAsync();
        Assert.AreEqual(0, c.Vm.Questions.Count);
        Assert.AreEqual("d2", c.Vm.Description);
        Assert.AreEqual("草稿已填好, 请检查后提交", c.Vm.AiStatus);
        Assert.IsTrue(c.Vm.AiStatusOk);
    }

    [TestMethod]
    public async Task AiSkip_ClearsQuestions()
    {
        var c = await Make();
        c.Chat.Replies.Enqueue("""{"service":"nope","severity":"minimal","title":"t","description":"d","questions":["q?"]}""");
        c.Vm.AiText = "x";
        await c.Vm.GenerateAsync();
        Assert.AreEqual("未能确定服务, 请手动选择", c.Vm.AiStatus);
        c.Vm.SkipQuestions();
        Assert.AreEqual(0, c.Vm.Questions.Count);
        Assert.AreEqual("已使用当前草稿, 请检查后提交", c.Vm.AiStatus);
    }

    [TestMethod]
    public async Task AiError_OffersSettings()
    {
        var c = await Make();
        c.Chat.Error = new AppError("AI 服务错误 401");
        c.Vm.AiText = "x";
        await c.Vm.GenerateAsync();
        Assert.AreEqual("AI 服务错误 401", c.Vm.AiStatus);
        Assert.IsTrue(c.Vm.AiShowSettings);
        Assert.IsFalse(c.Vm.AiBusy);
    }

    [TestMethod]
    public async Task AiSettingsLabel_ShowsDeploymentAndAuth()
    {
        var c = await Make();
        Assert.AreEqual("gpt-6-luna · 登录身份", c.Vm.AiSettingsLabel);
        c.Secrets.Write(AIClient.ChatKeyTarget, "k");
        c.Vm.RefreshAiLabel();
        Assert.AreEqual("gpt-6-luna · API Key", c.Vm.AiSettingsLabel);
    }

    [TestMethod]
    public async Task Submit_ConfirmCancelled_DoesNothing()
    {
        var c = await Make();
        await Fill(c.Vm);
        c.Dialogs.Answer = false;
        await c.Vm.SubmitAsync();
        Assert.AreEqual(0, c.Api.Created.Count);
        Assert.AreEqual("创建 Support Case?", c.Dialogs.Asked.Single().Title);
    }

    [TestMethod]
    public async Task Submit_Success_ClearsRemembersReloads()
    {
        var c = await Make();
        await Fill(c.Vm);
        c.Vm.UseStart = false; c.Vm.StartTime = DateTimeOffset.UnixEpoch;
        var before = c.Api.Calls.Count(x => x.StartsWith("tickets:", StringComparison.Ordinal));
        await c.Vm.SubmitAsync();
        var sent = c.Api.Created.Single();
        Assert.IsNull(sent.StartTime);
        Assert.IsNull(sent.Quota);
        Assert.AreEqual("z@contoso.com", sent.Contact.Email);
        Assert.AreEqual("2511110010000001", c.Vm.CreatedNumber);
        Assert.AreEqual("已创建 Case 2511110010000001", c.Vm.ResultText);
        Assert.AreEqual("", c.Vm.Title);
        Assert.AreEqual("", c.Vm.Description);
        Assert.AreEqual(VmBoot.Id, c.Shell.RecentProblems[0].ProblemId);
        Assert.IsTrue(c.Api.Calls.Count(x => x.StartsWith("tickets:", StringComparison.Ordinal)) > before);
        StringAssert.Contains(c.Dialogs.Asked[0].Message, "标题: 虚拟机无法启动");
    }

    [TestMethod]
    public async Task Submit_OverPlan_ConfirmIncludesWarning()
    {
        var c = await Make();
        await Fill(c.Vm);
        c.Shell.Plan = new Core.Plan.SupportPlan("Developer", "developer-or-basic", "ticket", false);
        c.Vm.Severity = Severity.Critical;
        await c.Vm.SubmitAsync();
        StringAssert.Contains(c.Dialogs.Asked[0].Message, "当前支持计划 (Developer) 不支持该级别, 提交可能被拒绝。");
    }

    [TestMethod]
    public async Task Submit_Failure_KeepsForm()
    {
        var c = await Make();
        await Fill(c.Vm);
        c.Api.CreateError = new AppError("Forbidden", "rid-1");
        await c.Vm.SubmitAsync();
        Assert.IsFalse(c.Vm.ResultOk);
        Assert.AreEqual("Forbidden\nRequest ID: rid-1", c.Vm.ResultText);
        Assert.AreEqual("虚拟机无法启动", c.Vm.Title);
    }

    [TestMethod]
    public async Task Reuse_RecentProblem_SelectsServiceAndProblem()
    {
        var c = await Make();
        c.Shell.Remember(Vm, VmBoot);
        Assert.AreEqual("Virtual Machine running Linux · Boot", c.Vm.RecentItems.Single().Label);
        await c.Vm.ReuseAsync(c.Vm.RecentItems[0]);
        Assert.AreEqual(Vm, c.Vm.Service);
        Assert.AreEqual(VmBoot, c.Vm.Problem);
    }

    [TestMethod]
    public async Task Template_And_Preview()
    {
        var c = await Make();
        c.Vm.InsertTemplate();
        StringAssert.StartsWith(c.Vm.Description, "【现象】\n");
        await Fill(c.Vm);
        StringAssert.Contains(c.Vm.PreviewJson(), "\"title\": \"虚拟机无法启动\"");
    }
}

[TestClass]
public class HistoryVMTests
{
    static async Task<(HistoryVM Vm, ShellVM Shell, FakeApi Api)> Make()
    {
        var api = Api();
        api.Tickets = [Ticket("1", "Open", 3, title: "SSH 超时"), Ticket("2", "Closed", 1, title: "配额"), Ticket("3", "Open", 2, service: "AKS")];
        var shell = new ShellVM(api, new FakeLogin(), new FakeSettings());
        await shell.AfterLoginAsync(Cli());
        return (new HistoryVM(shell), shell, api);
    }

    [TestMethod]
    public async Task Counts_AndNewestFirst()
    {
        var (vm, _, _) = await Make();
        Assert.AreEqual("全部 3", vm.AllLabel);
        Assert.AreEqual("处理中 2", vm.OpenLabel);
        Assert.AreEqual("已关闭 1", vm.ClosedLabel);
        CollectionAssert.AreEqual(new[] { "2", "3", "1" }, vm.Rows.Select(r => r.Id).ToList());
    }

    [TestMethod]
    public async Task Filter_And_Search()
    {
        var (vm, _, _) = await Make();
        vm.Filter = StatusFilter.Open;
        CollectionAssert.AreEqual(new[] { "3", "1" }, vm.Rows.Select(r => r.Id).ToList());
        vm.Query = "ssh";
        CollectionAssert.AreEqual(new[] { "1" }, vm.Rows.Select(r => r.Id).ToList());
        vm.Filter = StatusFilter.All; vm.Query = "aks";
        CollectionAssert.AreEqual(new[] { "3" }, vm.Rows.Select(r => r.Id).ToList());
        vm.Query = "252";
        CollectionAssert.AreEqual(new[] { "2" }, vm.Rows.Select(r => r.Id).ToList());
    }

    [TestMethod]
    public async Task Days_ChangeReloads()
    {
        var (vm, shell, api) = await Make();
        CollectionAssert.AreEqual(new[] { 7, 30, 90, 365 }, HistoryVM.DayOptions.Select(d => d.Days).ToList());
        await vm.SetDaysAsync(90);
        Assert.AreEqual(90, shell.HistoryDays);
        CollectionAssert.Contains(api.Calls, $"tickets:{S1}:90");
    }

    [TestMethod]
    public async Task EmptyState()
    {
        var (vm, _, _) = await Make();
        vm.Query = "zzz";
        Assert.IsTrue(vm.IsEmpty);
        Assert.IsFalse(vm.LoadFailed);
        Assert.IsFalse(vm.IsStale);
    }

    [TestMethod]
    public async Task LoadError_WithCachedRows_MarksStale_ThenRecovers()
    {
        var (vm, shell, api) = await Make();
        api.TicketsError = new AppError("AuthorizationFailed: 403");
        await shell.LoadTicketsAsync();
        Assert.AreEqual(3, vm.Rows.Count);
        Assert.IsTrue(vm.IsStale);
        Assert.IsFalse(vm.LoadFailed);
        StringAssert.Contains(vm.LoadError, "403");
        api.TicketsError = null;
        await shell.LoadTicketsAsync();
        Assert.IsFalse(vm.IsStale);
        Assert.IsNull(vm.LoadError);
    }

    [TestMethod]
    public async Task LoadError_WithoutRows_ShowsErrorState_NotEmptyState()
    {
        var api = Api();
        api.TicketsError = new AppError("AuthorizationFailed: 403");
        var shell = new ShellVM(api, new FakeLogin(), new FakeSettings());
        await shell.AfterLoginAsync(Cli());
        var vm = new HistoryVM(shell);
        Assert.IsTrue(vm.LoadFailed);
        Assert.IsFalse(vm.IsEmpty);
        Assert.IsFalse(vm.IsStale);
    }
}

[TestClass]
public class DetailVMTests
{
    static TicketDetail MakeDetail(string status = "Open")
    {
        var props = JsonNode.Parse("""
        {"serviceDisplayName":"VM","problemClassificationDisplayName":"Boot","createdDate":"2026-11-10T01:00:00Z",
         "require24X7Response":true,"supportPlanType":"Unified",
         "contactDetails":{"firstName":"三","lastName":"张","primaryEmailAddress":"z@contoso.com","preferredSupportLanguage":"zh-hans","preferredTimeZone":"China Standard Time"},
         "technicalTicketDetails":{"resourceId":"/subscriptions/x/vm1"},"description":"现象"}
        """)!.AsObject();
        var t = Ticket("1", status, 1);
        return new TicketDetail(t, props,
        [
            new("c1", "z@contoso.com", "s", "old", false, DateTimeOffset.UnixEpoch),
            new("c2", "eng@microsoft.com", "s", "new", true, DateTimeOffset.UnixEpoch.AddDays(1)),
        ]);
    }

    static async Task<(DetailVM Vm, ShellVM Shell, FakeApi Api, FakeDialogs Dialogs, FakeShell Os, NoDelay Delay)> Make(string status = "Open", Identity? id = null)
    {
        var api = Api(); api.Detail = MakeDetail(status); api.Tickets = [api.Detail.Summary];
        var shell = new ShellVM(api, new FakeLogin(), new FakeSettings());
        await shell.AfterLoginAsync(id ?? Cli());
        var dialogs = new FakeDialogs(); var os = new FakeShell(); var delay = new NoDelay();
        var vm = new DetailVM(shell, api, dialogs, os, delay, api.Detail.Summary);
        await vm.LoadAsync();
        return (vm, shell, api, dialogs, os, delay);
    }

    [TestMethod]
    public async Task Overview_Rows()
    {
        var (vm, _, _, _, _, _) = await Make();
        var rows = vm.Overview.ToDictionary(r => r.Key, r => r.Value);
        Assert.AreEqual("VM", rows["服务"]);
        Assert.AreEqual("是", rows["24×7 响应"]);
        Assert.AreEqual("张三 · z@contoso.com", rows["联系人"]);
        Assert.AreEqual("zh-hans · China Standard Time", rows["语言 / 时区"]);
        Assert.AreEqual("Unified", rows["支持计划"]);
        Assert.IsTrue(vm.Overview.Single(r => r.Key == "关联资源").Mono);
        Assert.IsFalse(rows.ContainsKey("支持工程师"));
        Assert.AreEqual("现象", vm.Description);
    }

    [TestMethod]
    public async Task Communications_NewestFirst_WithCountTab()
    {
        var (vm, _, _, _, _, _) = await Make();
        CollectionAssert.AreEqual(new[] { "c2", "c1" }, vm.Communications.Select(c => c.Id).ToList());
        Assert.AreEqual("沟通记录 (2)", vm.CommunicationsHeader);
    }

    [TestMethod]
    public async Task Reply_SendsClearsAndReloads()
    {
        var (vm, _, api, _, _, delay) = await Make();
        Assert.IsFalse(vm.CanSend);
        vm.Reply = "补充";
        Assert.IsTrue(vm.CanSend);
        await vm.SendAsync();
        Assert.AreEqual(("1", "补充", (string?)null), api.Replies.Single());
        Assert.AreEqual("", vm.Reply);
        Assert.AreEqual("补充信息已发送", vm.Toast);
        Assert.AreEqual(1, vm.SelectedTab);
        Assert.AreEqual(TimeSpan.FromSeconds(2), delay.Waits.Single());
        Assert.AreEqual(2, api.Calls.Count(c => c == "detail:1"));
    }

    [TestMethod]
    public async Task Reply_InSpMode_UsesContactEmailAsSender()
    {
        var (vm, _, api, _, _, _) = await Make(id: new Identity("App", T1, null, AzureCloud.Global, "App Registration"));
        vm.Reply = "x";
        await vm.SendAsync();
        Assert.AreEqual("z@contoso.com", api.Replies.Single().Sender);
    }

    [TestMethod]
    public async Task Closed_DisablesReplyAndClose()
    {
        var (vm, _, _, _, _, _) = await Make("Closed");
        vm.Reply = "x";
        Assert.IsFalse(vm.CanSend);
        Assert.IsFalse(vm.CanClose);
    }

    [TestMethod]
    public async Task Close_RequiresConfirm()
    {
        var (vm, _, api, dialogs, _, _) = await Make();
        dialogs.Answer = false;
        await vm.CloseAsync();
        Assert.AreEqual(0, api.Closed.Count);
        Assert.AreEqual("关闭 Case 251?", dialogs.Asked[0].Title);
        dialogs.Answer = true;
        await vm.CloseAsync();
        Assert.AreEqual("1", api.Closed.Single());
        Assert.AreEqual("Case 251 已关闭", vm.Toast);
    }

    [TestMethod]
    public async Task Copy_And_Portal()
    {
        var (vm, _, _, _, os, _) = await Make();
        vm.CopyNumberCommand.Execute(null);
        vm.OpenPortalCommand.Execute(null);
        Assert.AreEqual("251", os.Copied.Single());
        StringAssert.StartsWith(os.Opened.Single(), "https://portal.azure.com/");
        Assert.AreEqual("已复制 Case 号", vm.Toast);
    }
}

[TestClass]
public class SettingsVMTests
{
    [TestMethod]
    public void AISettings_SavesConfigAndKeys()
    {
        var settings = new FakeSettings(); var secrets = new FakeSecrets();
        var vm = new AISettingsVM(settings, secrets);
        Assert.AreEqual("gpt-6-luna", vm.ChatDeployment);
        Assert.IsFalse(vm.ChatKeyStored);
        vm.ChatDeployment = "gpt-x";
        Assert.AreEqual("gpt-x", AISettingsVM.Load(settings).ChatDeployment);
        vm.ChatKeyInput = "  key1 ";
        vm.SaveChatKey();
        Assert.AreEqual("key1", secrets.Read(AIClient.ChatKeyTarget));
        Assert.IsTrue(vm.ChatKeyStored);
        Assert.AreEqual("", vm.ChatKeyInput);
        Assert.AreEqual("已保存 ●●●●", vm.ChatKeyStatus);
        vm.ClearChatKey();
        Assert.IsNull(secrets.Read(AIClient.ChatKeyTarget));
        Assert.AreEqual("未设置 · 使用登录身份 (Entra ID)", vm.ChatKeyStatus);
    }

    [TestMethod]
    public void AISettings_SpeechProviderFields_AndReset()
    {
        var settings = new FakeSettings(); var secrets = new FakeSecrets();
        secrets.Write(AIClient.SttKeyTarget, "s");
        var vm = new AISettingsVM(settings, secrets);
        Assert.IsTrue(vm.SttKeyStored);
        vm.SttProvider = SttProvider.AzureSpeech;
        Assert.IsTrue(vm.IsSpeech);
        Assert.AreEqual("Speech Key", vm.SttKeyLabel);
        vm.SpeechRegion = "southeastasia";
        Assert.IsFalse(vm.SttEndpointEnabled);
        vm.Reset();
        Assert.AreEqual(SttProvider.AzureOpenAI, AISettingsVM.Load(settings).SttProvider);
        Assert.IsFalse(vm.SttKeyStored);
        Assert.AreEqual("API Key", vm.SttKeyLabel);
    }

    [TestMethod]
    public void ContactSettings_AutoSave()
    {
        var shell = new ShellVM(Api(), new FakeLogin(), new FakeSettings());
        var vm = new ContactSettingsVM(shell);
        vm.LastName = "张"; vm.FirstName = "三"; vm.Email = "z@contoso.com";
        Assert.IsTrue(shell.ContactValid);
        vm.PreferPhone = true;
        Assert.IsFalse(shell.ContactValid);
        Assert.AreEqual("必填", vm.PhonePlaceholder);
        vm.Language = "en-us";
        Assert.AreEqual("en-us", shell.Contact.Language);
        Assert.AreEqual(5, vm.Languages.Count);
    }

    [TestMethod]
    public async Task Mcp_SnippetUsesAbsolutePython()
    {
        var api = Api(); var shell = new ShellVM(api, new FakeLogin(), new FakeSettings());
        await shell.AfterLoginAsync(Cli());
        var os = new FakeShell();
        var vm = new McpSettingsVM(shell, os, @"C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe", @"C:\repo");
        var j = JsonNode.Parse(vm.Snippet)!["mcpServers"]!["casepilot"]!;
        Assert.AreEqual(@"C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe", (string?)j["command"]);
        Assert.AreEqual("-m", (string?)j["args"]![0]);
        Assert.AreEqual(@"C:\repo", (string?)j["cwd"]);
        Assert.AreEqual(S1, (string?)j["env"]!["CASEPILOT_SUBSCRIPTION_ID"]);
        vm.CopyCommand.Execute(null);
        Assert.AreEqual(vm.Snippet, os.Copied.Single());
        StringAssert.Contains(McpSettingsVM.Note, "confirm=true");
    }
}
