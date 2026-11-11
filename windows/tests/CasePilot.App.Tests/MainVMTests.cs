using System.Text.Json.Nodes;
using CasePilot.App.Tests.Fakes;
using CasePilot.App.ViewModels;
using CasePilot.Core.AI;
using CasePilot.Core.Models;
using static CasePilot.App.Tests.Fakes.Data;

namespace CasePilot.App.Tests;

[TestClass]
public class MainVMTests
{
    sealed record Ctx(MainVM Vm, FakeApi Api, NoDelay Delay, FakeShell Os);

    static async Task<Ctx> Make()
    {
        var api = Api();
        api.Tickets = [Ticket("a", "Open", 1), Ticket("b", "Closed", 3)];
        api.Detail = new TicketDetail(api.Tickets[0], new JsonObject(), []);
        var settings = new FakeSettings(); var secrets = new FakeSecrets(); var os = new FakeShell(); var delay = new NoDelay();
        var login = new FakeLogin { Cli = () => Cli() };
        var deps = new AppDeps(api, login, settings, secrets, os, new FakeDialogs(), delay, new FakeChat(),
            new FakeRecorder(), new FakeTranscriber(), @"C:\py\python.exe", @"C:\repo");
        var vm = new MainVM(deps);
        await vm.Shell.AfterLoginAsync(Cli());
        return new(vm, api, delay, os);
    }

    [TestMethod]
    public async Task Open_count_badge_counts_open_tickets()
    {
        var c = await Make();
        Assert.AreEqual("1", c.Vm.OpenBadge);
    }

    [TestMethod]
    public async Task Open_detail_loads_and_escape_closes()
    {
        var c = await Make();
        await c.Vm.OpenDetailAsync(c.Api.Tickets[0]);
        Assert.IsTrue(c.Vm.IsDetailOpen);
        Assert.IsNotNull(c.Vm.Detail!.Detail);
        c.Vm.EscapeCommand.Execute(null);
        Assert.IsFalse(c.Vm.IsDetailOpen);
        Assert.IsNull(c.Vm.Detail);
    }

    [TestMethod]
    public async Task Toast_dismisses_after_three_seconds()
    {
        var c = await Make();
        await c.Vm.ShowToastAsync("已复制");
        CollectionAssert.Contains(c.Delay.Waits, TimeSpan.FromSeconds(3));
        Assert.IsNull(c.Vm.Toast);
    }

    [TestMethod]
    public async Task Detail_toast_is_forwarded_to_shell_toast()
    {
        var c = await Make();
        await c.Vm.OpenDetailAsync(c.Api.Tickets[0]);
        c.Vm.Detail!.CopyNumberCommand.Execute(null);
        CollectionAssert.Contains(c.Delay.Waits, TimeSpan.FromSeconds(3));
        CollectionAssert.Contains(c.Os.Copied, "25a");
    }

    [TestMethod]
    public async Task Subscription_options_include_tenant_level_first()
    {
        var c = await Make();
        var o = c.Vm.SubscriptionOptions;
        Assert.AreEqual("", o[0].Id);
        Assert.AreEqual("无 · 租户级", o[0].Name);
        Assert.AreEqual(3, o.Count);
    }

    [TestMethod]
    public async Task Selecting_subscription_option_reloads_and_persists()
    {
        var c = await Make();
        c.Vm.SelectedSubscription = "";
        await c.Vm.LastLoad;
        Assert.AreEqual("", c.Vm.Shell.SubscriptionId);
        StringAssert.StartsWith(c.Api.Calls[^1], "tickets:-");
    }

    [TestMethod]
    public async Task Shortcut_commands_navigate()
    {
        var c = await Make();
        c.Vm.GoHistoryCommand.Execute(null);
        Assert.AreEqual(Page.History, c.Vm.Shell.CurrentPage);
        c.Vm.GoSettingsCommand.Execute(null);
        Assert.AreEqual(Page.AISettings, c.Vm.Shell.CurrentPage);
        c.Vm.GoCreateCommand.Execute(null);
        Assert.AreEqual(Page.Create, c.Vm.Shell.CurrentPage);
    }

    [TestMethod]
    public async Task Opening_ai_settings_from_create_page_refreshes_label_on_return()
    {
        var c = await Make();
        c.Vm.OpenAISettingsCommand.Execute(null);
        Assert.AreEqual(Page.AISettings, c.Vm.Shell.CurrentPage);
        c.Vm.AISettings.ChatDeployment = "gpt-x";
        c.Vm.GoCreateCommand.Execute(null);
        StringAssert.StartsWith(c.Vm.Create.AiSettingsLabel, "gpt-x");
    }

    [TestMethod]
    public async Task Logged_out_shows_login()
    {
        var c = await Make();
        Assert.IsFalse(c.Vm.ShowLogin);
        c.Vm.Shell.Logout();
        Assert.IsTrue(c.Vm.ShowLogin);
    }
}
