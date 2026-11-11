using CasePilot.App.Tests.Fakes;
using CasePilot.App.ViewModels;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using static CasePilot.App.Tests.Fakes.Data;

namespace CasePilot.App.Tests;

[TestClass]
public class ShellVMTests
{
    static (ShellVM Vm, FakeApi Api, FakeSettings Settings, FakeLogin Auth) Make(FakeSettings? s = null)
    {
        var api = Api(); var settings = s ?? new FakeSettings(); var auth = new FakeLogin();
        return (new ShellVM(api, auth, settings), api, settings, auth);
    }

    [TestMethod]
    public async Task AfterLogin_PicksHomeTenant_FirstSub_LoadsServicesAndTickets()
    {
        var (vm, api, settings, _) = Make();
        api.Tickets = [Ticket("1", "Open", 1)];
        await vm.AfterLoginAsync(Cli(T1));
        Assert.IsTrue(vm.IsLoggedIn);
        Assert.AreEqual(T1, vm.TenantId);
        Assert.AreEqual(S1, vm.SubscriptionId);
        Assert.AreEqual(3, vm.Services.Count);
        Assert.AreEqual(1, vm.Tickets.Count);
        Assert.AreEqual(T1, settings.Get<string>("tenant"));
        Assert.AreEqual(S1, settings.Get<string>("sub." + T1));
    }

    [TestMethod]
    public async Task AfterLogin_TenantWithoutDisplayName_UsesDomain()
    {
        var (vm, _, _, _) = Make();
        await vm.AfterLoginAsync(Cli(T1));
        Assert.AreEqual("fabrikam.com", vm.Tenants.Single(t => t.Id == T2).Name);
    }

    [TestMethod]
    public async Task AfterLogin_NoTenantsListed_FallsBackToIdentityTenant()
    {
        var (vm, api, _, _) = Make();
        api.Tenants = [];
        await vm.AfterLoginAsync(Cli(T2));
        Assert.AreEqual(T2, vm.Tenants.Single().Id);
        Assert.AreEqual(T2, vm.TenantId);
    }

    [TestMethod]
    public async Task AfterLogin_RestoresSavedTenant_AndPerTenantSubscription()
    {
        var s = new FakeSettings();
        s.Set("tenant", T1); s.Set("sub." + T1, S2);
        var (vm, _, _, _) = Make(s);
        await vm.AfterLoginAsync(Cli(T2));
        Assert.AreEqual(T1, vm.TenantId);
        Assert.AreEqual(S2, vm.SubscriptionId);
    }

    [TestMethod]
    public async Task SavedTenantLevel_EmptySub_IsRestored()
    {
        var s = new FakeSettings();
        s.Set("sub." + T1, "");
        var (vm, api, _, _) = Make(s);
        await vm.AfterLoginAsync(Cli(T1));
        Assert.AreEqual("", vm.SubscriptionId);
        Assert.IsNull(vm.Sub);
        CollectionAssert.Contains(api.Calls, "tickets:-:30");
    }

    [TestMethod]
    public async Task SavedSubNoLongerExists_FallsBackToFirst()
    {
        var s = new FakeSettings();
        s.Set("sub." + T1, "cccccccc-cccc-cccc-cccc-cccccccccccc");
        var (vm, _, _, _) = Make(s);
        await vm.AfterLoginAsync(Cli(T1));
        Assert.AreEqual(S1, vm.SubscriptionId);
    }

    [TestMethod]
    public async Task SwitchTenant_RestoresThatTenantsSubscription()
    {
        var (vm, api, settings, _) = Make();
        api.Subs[T2] = [new(S2, "Dev", "Enabled")];
        await vm.AfterLoginAsync(Cli(T1));
        await vm.SelectSubscriptionAsync(S2);
        await vm.SelectTenantAsync(T2);
        Assert.AreEqual(S2, vm.SubscriptionId);
        await vm.SelectTenantAsync(T1);
        Assert.AreEqual(S2, vm.SubscriptionId);
        Assert.AreEqual(T1, settings.Get<string>("tenant"));
    }

    [TestMethod]
    public async Task SubscriptionsFail_ShowsBanner()
    {
        var (vm, api, _, _) = Make();
        api.SubsError = new AppError("boom");
        await vm.AfterLoginAsync(Cli(T1));
        Assert.AreEqual("加载订阅失败: boom", vm.Banner);
        Assert.AreEqual("", vm.SubscriptionId);
    }

    [TestMethod]
    public async Task LoadTickets_InfersPlanFromYearOfTickets()
    {
        var (vm, api, _, _) = Make();
        api.Tickets = [Ticket("1", "Open", 1)];
        api.YearTickets = [Ticket("1", "Open", 1), Ticket("2", "Closed", 200, plan: "Unified Enterprise")];
        await vm.AfterLoginAsync(Cli(T1));
        Assert.AreEqual("unified", vm.Plan.Tier);
        Assert.AreEqual("支持计划: Unified Enterprise", vm.PlanLine);
        Assert.AreEqual("支持计划: Unified Enterprise (根据该订阅最近的 Case 识别)", vm.PlanTooltip);
    }

    [TestMethod]
    public async Task LoadTickets_DaysChange_Reloads()
    {
        var (vm, api, _, _) = Make();
        await vm.AfterLoginAsync(Cli(T1));
        vm.HistoryDays = 7;
        await vm.LoadTicketsAsync();
        CollectionAssert.Contains(api.Calls, $"tickets:{S1}:7");
    }

    [TestMethod]
    public async Task IdentityCard_InitialsMethodAndDomain()
    {
        var (vm, _, _, _) = Make();
        await vm.AfterLoginAsync(Cli(T1));
        Assert.AreEqual("A", vm.Initials);
        Assert.AreEqual("alice@contoso.com", vm.UserName);
        Assert.AreEqual("Azure CLI · contoso.com", vm.MethodLine);
        Assert.AreEqual("支持计划未知", vm.PlanLine);
    }

    [TestMethod]
    public async Task Logout_ClearsState_AndRequestsMethod()
    {
        var (vm, _, _, auth) = Make();
        await vm.AfterLoginAsync(Cli(T1));
        vm.Logout(LoginMethod.Sp);
        Assert.IsFalse(vm.IsLoggedIn);
        Assert.AreEqual(0, vm.Tenants.Count);
        Assert.AreEqual(0, vm.Tickets.Count);
        Assert.IsFalse(vm.AutoLogin);
        Assert.AreEqual(LoginMethod.Sp, vm.RequestedLogin);
        Assert.AreEqual(1, auth.LogoutCount);
        Assert.AreEqual("unknown", vm.Plan.Tier);
    }

    [TestMethod]
    public void Navigation_DefaultsToCreate_AndSwitches()
    {
        var (vm, _, _, _) = Make();
        Assert.AreEqual(Page.Create, vm.CurrentPage);
        vm.NavigateCommand.Execute(Page.History);
        Assert.AreEqual(Page.History, vm.CurrentPage);
    }

    [TestMethod]
    public void Contact_PersistsAndValidates()
    {
        var (vm, _, settings, _) = Make();
        Assert.IsFalse(vm.ContactValid);
        vm.Contact = GoodContact;
        Assert.IsTrue(vm.ContactValid);
        var again = new ShellVM(Api(), new FakeLogin(), settings);
        Assert.AreEqual("z@contoso.com", again.Contact.Email);
    }

    [TestMethod]
    public void Remember_KeepsSixNewestUnique()
    {
        var (vm, _, settings, _) = Make();
        for (var i = 0; i < 8; i++) vm.Remember(Vm, new ProblemClassification("p" + i, "P" + i));
        vm.Remember(Vm, new ProblemClassification("p5", "P5"));
        Assert.AreEqual(6, vm.RecentProblems.Count);
        Assert.AreEqual("p5", vm.RecentProblems[0].ProblemId);
        Assert.AreEqual(1, vm.RecentProblems.Count(r => r.ProblemId == "p5"));
        Assert.AreEqual(6, new ShellVM(Api(), new FakeLogin(), settings).RecentProblems.Count);
    }

    [TestMethod]
    public async Task PortalUrl_UsesCloudAndSub()
    {
        var (vm, _, _, _) = Make();
        await vm.AfterLoginAsync(Cli(T1));
        StringAssert.StartsWith(vm.PortalUrl("x"), "https://portal.azure.com/#view/Microsoft_Azure_Support/");
        StringAssert.Contains(vm.PortalUrl("x"), "aaaaaaaa%2Daaaa");
    }
}

[TestClass]
public class LoginVMTests
{
    static (LoginVM Vm, ShellVM Shell, FakeLogin Auth, FakeSecrets Secrets, FakeSettings Settings, FakeShell Os) Make()
    {
        var auth = new FakeLogin(); var settings = new FakeSettings(); var secrets = new FakeSecrets(); var os = new FakeShell();
        var shell = new ShellVM(Api(), auth, settings);
        return (new LoginVM(shell, auth, secrets, settings, os), shell, auth, secrets, settings, os);
    }

    [TestMethod]
    public async Task Init_AutoCli_Succeeds()
    {
        var (vm, shell, auth, _, _, _) = Make();
        auth.Cli = () => Cli();
        await vm.InitAsync();
        Assert.IsTrue(shell.IsLoggedIn);
        Assert.IsNull(vm.Error);
        Assert.IsFalse(vm.Busy);
    }

    [TestMethod]
    public async Task Init_SilentCliFailure_ShowsHint()
    {
        var (vm, shell, auth, _, _, _) = Make();
        auth.Cli = () => throw new AppError("未找到 Azure CLI (az)。");
        await vm.InitAsync();
        Assert.IsFalse(shell.IsLoggedIn);
        StringAssert.StartsWith(vm.Error, "未检测到可用的 Azure CLI 会话。请执行 az login 后重试, 或改用浏览器登录。\n");
    }

    [TestMethod]
    public async Task Init_AfterLogoutWithRequestedMethod_DoesNotAutoLogin()
    {
        var (vm, shell, auth, _, _, _) = Make();
        auth.Cli = () => throw new InvalidOperationException("should not be called");
        shell.Logout(LoginMethod.Device);
        await vm.InitAsync();
        Assert.AreEqual(LoginMethod.Device, vm.Method);
        Assert.IsNull(vm.Error);
    }

    [TestMethod]
    public async Task Sp_RejectsNonGuid()
    {
        var (vm, _, auth, _, _, _) = Make();
        vm.SpTenant = "contoso"; vm.SpClient = T2; vm.SpSecret = "s";
        Assert.IsFalse(vm.SpTenantValid);
        await vm.LoginSpAsync();
        Assert.AreEqual("Tenant ID 与 Client ID 须为 GUID", vm.Error);
        Assert.IsNull(auth.SpArgs);
    }

    [TestMethod]
    public async Task Sp_SavesSecretToCredentialManager_AndRestores()
    {
        var (vm, shell, auth, secrets, settings, os) = Make();
        auth.Sp = () => new Identity("App · 22222222…", T1, null, AzureCloud.China, "App Registration");
        vm.SpTenant = T1; vm.SpClient = T2; vm.SpSecret = "sec"; vm.SpCloud = AzureCloud.China;
        await vm.LoginSpAsync();
        Assert.IsTrue(shell.IsLoggedIn);
        Assert.AreEqual((T1, T2, "sec", AzureCloud.China), auth.SpArgs);
        Assert.AreEqual("sec", secrets.Read("CasePilot/sp/" + T2));

        shell.Logout(LoginMethod.Sp);
        var again = new LoginVM(shell, auth, secrets, settings, os);
        await again.InitAsync();
        Assert.AreEqual(T1, again.SpTenant);
        Assert.AreEqual(T2, again.SpClient);
        Assert.AreEqual("sec", again.SpSecret);
        Assert.AreEqual(AzureCloud.China, again.SpCloud);
        Assert.AreEqual(LoginMethod.Sp, again.Method);
    }

    [TestMethod]
    public async Task Sp_UncheckedSave_DoesNotStoreSecret()
    {
        var (vm, _, auth, secrets, _, _) = Make();
        auth.Sp = () => Cli();
        vm.SpTenant = T1; vm.SpClient = T2; vm.SpSecret = "sec"; vm.SaveSecret = false;
        await vm.LoginSpAsync();
        Assert.AreEqual(0, secrets.Data.Count);
    }

    [TestMethod]
    public async Task Device_CopiesCodeOpensBrowser_ThenCompletes()
    {
        var (vm, shell, auth, _, _, os) = Make();
        vm.Cloud = AzureCloud.UsGov;
        var run = vm.LoginDeviceAsync();
        Assert.AreEqual("ABCD-1234", vm.DeviceCode?.UserCode);
        CollectionAssert.AreEqual(new[] { "ABCD-1234" }, os.Copied);
        CollectionAssert.AreEqual(new[] { "https://microsoft.com/devicelogin" }, os.Opened);
        Assert.AreEqual(AzureCloud.UsGov, auth.Cloud);
        auth.DevicePoll.SetResult(Cli());
        await run;
        Assert.IsTrue(shell.IsLoggedIn);
    }

    [TestMethod]
    public async Task Device_Cancel_ClearsCodeWithoutError()
    {
        var (vm, shell, _, _, _, _) = Make();
        var run = vm.LoginDeviceAsync();
        vm.CancelDevice();
        await run;
        Assert.IsNull(vm.DeviceCode);
        Assert.IsNull(vm.Error);
        Assert.IsFalse(vm.Busy);
        Assert.IsFalse(shell.IsLoggedIn);
    }
}
