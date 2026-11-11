using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CasePilot.App.Controls;
using CasePilot.App.ViewModels;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using CasePilot.Core.Support;

namespace CasePilot.App.Views;

/// <summary>--snapshot: 用假数据渲染每个页面为 PNG (RenderTargetBitmap), 供 UI 审阅. 不访问网络.</summary>
public static class Snapshot
{
    public static async Task<int> RunAsync(string dir, TextWriter o)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var deps = Deps();
            var vm = new MainVM(deps);
            MicButton.Factory = vm.NewMic;
            var w = new MainWindow(vm) { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            w.Show();

            // 登录页
            vm.Login.Method = LoginMethod.Cli;
            await Save(w, dir, "01-login-cli", o);
            vm.Login.Method = LoginMethod.Sp;
            vm.Login.SpTenant = "not-a-guid";
            await Save(w, dir, "02-login-sp", o);
            vm.Login.SpTenant = "";

            await vm.Shell.AfterLoginAsync(new Identity("zhengyu@contoso.com", FakeData.T1, "Contoso", AzureCloud.Global, "Azure CLI"));
            vm.Shell.Contact = new Contact { FirstName = "三", LastName = "张", Email = "zhangsan@contoso.com" };

            vm.Shell.CurrentPage = Page.Create;
            await Save(w, dir, "03-create-empty", o);
            await vm.Create.PickAsync(FakeData.Vm);
            vm.Create.Problem = vm.Create.Problems[0];
            vm.Create.Title = "生产环境 Linux 虚拟机重启后无法 SSH 连接";
            vm.Create.InsertTemplate();
            vm.Create.Severity = Severity.Critical;
            await Save(w, dir, "04-create-filled", o);
            await Save(w, dir, "05-create-filled-bottom", o, scrollEnd: true);

            vm.Shell.CurrentPage = Page.History;
            await Save(w, dir, "06-history", o);
            await vm.OpenDetailAsync(vm.History.Rows[0]);
            await Save(w, dir, "07-history-detail", o);
            vm.Detail!.SelectedTab = 1;
            await Save(w, dir, "08-detail-comms", o);
            vm.CloseDetail();
            var api = (FakeData.Api)deps.Api;
            api.TicketsError = new AppError("请求被拒绝 (403)", "7d3c0a2e-0000-4b1f-9a77-3f0c2b1e9d10", 403);
            await vm.Shell.LoadTicketsAsync();
            await Save(w, dir, "09-history-error", o);
            api.TicketsError = null;
            await vm.Shell.LoadTicketsAsync();
            vm.Shell.Banner = null;

            vm.Shell.CurrentPage = Page.AISettings;
            await Save(w, dir, "10-settings-ai", o);
            vm.Shell.CurrentPage = Page.Contacts;
            await Save(w, dir, "11-settings-contact", o);
            vm.Shell.CurrentPage = Page.Mcp;
            await Save(w, dir, "12-settings-mcp", o);
            w.Close();
            o.WriteLine($"snapshot: 已写入 {dir}");
            return 0;
        }
        catch (Exception e)
        {
            o.WriteLine("[fail] snapshot: " + e);
            return 1;
        }
    }

    static async Task Save(Window w, string dir, string name, TextWriter o, bool scrollEnd = false)
    {
        await Settle(w);
        if (scrollEnd)
        {
            foreach (var sv in Descendants<System.Windows.Controls.ScrollViewer>(w)) sv.ScrollToEnd();
            await Settle(w);
        }
        var dpi = VisualTreeHelper.GetDpi(w);
        var root = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // 快照无 Mica: 以导航底色作为背景
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle((Brush)w.FindResource("CpNavFallback"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(dir, name + ".png");
        await using (var f = File.Create(path)) enc.Save(f);
        o.WriteLine("[ok] " + path);
    }

    static async Task Settle(Window w)
    {
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(60);
            await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            w.UpdateLayout();
        }
    }

    static IEnumerable<T> Descendants<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) yield return t;
            foreach (var x in Descendants<T>(c)) yield return x;
        }
    }

    static AppDeps Deps() => new(new FakeData.Api(), new FakeData.Login(), new FakeData.Settings(), new FakeData.Secrets(),
        new FakeData.Shell(), new FakeData.Dialogs(), new FakeData.Delay(), new FakeData.Chat(), new FakeData.Recorder(), new FakeData.Stt(),
        @"C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe", @"C:\Users\me\Azure_CasePilot");
}

/// <summary>快照用假数据 (中文内容、长标题、各状态 / 严重性).</summary>
internal static class FakeData
{
    public const string T1 = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    public const string S1 = "0b1f6471-1bf0-4dda-aec3-111122223333";

    public static readonly SupportService Vm = new("/providers/Microsoft.Support/services/vm", "vm", "Virtual Machine running Linux", ["microsoft.compute/virtualmachines"]);
    static readonly SupportService[] Services =
    [
        Vm,
        new("/s/win", "win", "Virtual Machine running Windows", ["microsoft.compute/virtualmachines"]),
        new("/s/aoai", "aoai", "Azure OpenAI", ["microsoft.cognitiveservices/accounts"]),
        new("/s/aks", "aks", "Kubernetes Service (AKS)", ["microsoft.containerservice/managedclusters"]),
        new("/s/stg", "stg", "Storage Account Management", ["microsoft.storage/storageaccounts"]),
        new("/s/quota", "quota", "Service and subscription limits (quotas)", []),
        new("/s/billing", "billing", "Billing", []),
    ];

    static readonly DateTimeOffset Now = DateTimeOffset.Now;

    static readonly TicketSummary[] Tickets =
    [
        new("cp-1", "2511110010001234", "生产环境 Linux 虚拟机重启后无法 SSH 连接, 串行控制台显示 fsck 错误", "Open", "critical", Now.AddHours(-3), "Virtual Machine running Linux", "Cannot connect / SSH", "Unified"),
        new("cp-2", "2511080010004567", "Azure OpenAI gpt-6-luna 东南亚部署持续返回 429, 配额未用满", "Open", "moderate", Now.AddDays(-3), "Azure OpenAI", "Quota / Throttling", "Unified"),
        new("cp-3", "2510300010007890", "AKS 节点池升级卡在 Upgrading 状态超过 6 小时", "Open", "highestcriticalimpact", Now.AddDays(-12), "Kubernetes Service (AKS)", "Cluster upgrade", "Unified"),
        new("cp-4", "2510200010001111", "存储账户 Blob 生命周期策略未生效", "Closed", "minimal", Now.AddDays(-22), "Storage Account Management", "Lifecycle management", "Unified"),
        new("cp-5", "2510150010002222", "申请提升 standardDSv5Family vCPU 配额到 200", "Closed", "moderate", Now.AddDays(-27), "Service and subscription limits (quotas)", "Compute-VM (cores-vCPUs) subscription limit increases", "Unified"),
    ];

    internal sealed class Api : ISupportApi
    {
        public Task<IReadOnlyList<Tenant>> TenantsAsync(CancellationToken ct) => R<Tenant>([new(T1, "Contoso 中国", "contoso.com"), new("22222222-2222-2222-2222-222222222222", "Fabrikam", "fabrikam.com")]);
        public Task<IReadOnlyList<Subscription>> SubscriptionsAsync(string tenant, CancellationToken ct) => R<Subscription>([new(S1, "生产订阅 Prod-01", "Enabled"), new("9", "开发测试 Dev", "Enabled")]);
        public Task<string> QuotaIdAsync(string tenant, string sub, CancellationToken ct) => Task.FromResult("EnterpriseAgreement_2014-09-01");
        public Task<IReadOnlyList<SupportService>> ServicesAsync(string tenant, CancellationToken ct) => R<SupportService>(Services);
        public Task<IReadOnlyList<ProblemClassification>> ProblemsAsync(string tenant, SupportService service, CancellationToken ct) =>
            R<ProblemClassification>([new("p1", "Cannot connect to my VM / SSH connection failures"), new("p2", "VM Performance / High CPU"), new("p3", "Compute-VM (cores-vCPUs) subscription limit increases")]);
        public Task<IReadOnlyList<AzureResource>> ResourcesAsync(string tenant, string sub, IReadOnlyList<string>? types, CancellationToken ct) =>
            R<AzureResource>([new("/subscriptions/x/resourceGroups/rg-prod/providers/Microsoft.Compute/virtualMachines/vm-web-01", "vm-web-01", "microsoft.compute/virtualmachines", "eastasia")]);
        public Task<TicketSummary> CreateAsync(string tenant, string? sub, NewCase c, CancellationToken ct) => throw new InvalidOperationException("快照模式不创建 Case");
        public AppError? TicketsError { get; set; }
        public Task<IReadOnlyList<TicketSummary>> TicketsAsync(string tenant, string? sub, int days, CancellationToken ct) =>
            TicketsError is { } e ? Task.FromException<IReadOnlyList<TicketSummary>>(e) : R<TicketSummary>(Tickets);
        public Task<TicketDetail> DetailAsync(string tenant, string? sub, string name, CancellationToken ct)
        {
            var t = Tickets.First(x => x.Id == name);
            var props = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                serviceDisplayName = t.Service,
                problemClassificationDisplayName = t.Problem,
                createdDate = t.Created?.ToString("o"),
                modifiedDate = Now.AddMinutes(-20).ToString("o"),
                supportPlanDisplayName = "Unified Enterprise",
                require24X7Response = true,
                description = "【现象】\n今天 09:20 起 vm-web-01 无法 SSH, 串行控制台显示 fsck 错误。\n【业务影响】\n官网前端 1/3 流量受影响。",
                serviceLevelAgreement = new { expirationTime = Now.AddHours(1).ToString("o"), slaMinutes = 60 },
                supportEngineer = new { emailAddress = "engineer@microsoft.com" },
                contactDetails = new { firstName = "三", lastName = "张", primaryEmailAddress = "zhangsan@contoso.com", preferredSupportLanguage = "zh-hans", preferredTimeZone = "China Standard Time" },
                technicalTicketDetails = new { resourceId = "/subscriptions/0b1f6471/resourceGroups/rg-prod/providers/Microsoft.Compute/virtualMachines/vm-web-01" },
            }))!.AsObject();
            Communication[] comms =
            [
                new("m1", "Microsoft 支持工程师", "RE: 生产环境 Linux 虚拟机无法 SSH", "您好, 我们已收到您的请求, 正在检查主机日志。请确认最近是否修改过 /etc/fstab。", true, Now.AddHours(-1)),
                new("m2", "zhangsan@contoso.com", "补充信息", "附上串行控制台截图, 错误为 UNEXPECTED INCONSISTENCY; RUN fsck MANUALLY。", false, Now.AddHours(-2)),
            ];
            return Task.FromResult(new TicketDetail(t, props, comms));
        }
        public Task ReplyAsync(string tenant, string? sub, string name, string text, string? sender, CancellationToken ct) => Task.CompletedTask;
        public Task CloseAsync(string tenant, string? sub, string name, CancellationToken ct) => Task.CompletedTask;

        static Task<IReadOnlyList<T>> R<T>(IReadOnlyList<T> x) => Task.FromResult(x);
    }

    internal sealed class Login : ILoginService
    {
        public AuthMode Mode => AuthMode.None;
        public AzureCloud Cloud => AzureCloud.Global;
        public string? User => null;
        public string? HomeTenant => null;
        public Task<string> GetTokenAsync(string? tenant, string? resource, CancellationToken ct) => Task.FromResult("");
        public Task<Identity> LoginCliAsync(CancellationToken ct) => Task.FromException<Identity>(new AppError("未找到 Azure CLI 登录会话"));
        public Task<DeviceCode> StartDeviceAsync(AzureCloud cloud, CancellationToken ct) => throw new NotSupportedException();
        public Task<Identity> PollDeviceAsync(DeviceCode code, CancellationToken ct) => throw new NotSupportedException();
        public Task<Identity> LoginSpAsync(string tenant, string clientId, string secret, AzureCloud cloud, CancellationToken ct) => throw new NotSupportedException();
        public void Logout() { }
    }

    internal sealed class Settings : ISettingsStore
    {
        readonly Dictionary<string, string> _d = [];
        public T? Get<T>(string key) => _d.TryGetValue(key, out var s) ? JsonSerializer.Deserialize<T>(s) : default;
        public void Set<T>(string key, T value) => _d[key] = JsonSerializer.Serialize(value);
    }

    internal sealed class Secrets : ISecretStore
    {
        readonly Dictionary<string, string> _d = new() { ["CasePilot/ai/aoai-key"] = "x" };
        public string? Read(string target) => _d.GetValueOrDefault(target);
        public void Write(string target, string? value) { if (string.IsNullOrEmpty(value)) _d.Remove(target); else _d[target] = value; }
    }

    internal sealed class Shell : IShell { public void Copy(string text) { } public void OpenUrl(string url) { } }
    internal sealed class Dialogs : IDialogs { public Task<bool> ConfirmAsync(string t, string m, string p) => Task.FromResult(false); }
    internal sealed class Delay : IDelay { public Task DelayAsync(TimeSpan d, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct); }
    internal sealed class Chat : IChat { public Task<string> ChatAsync(IReadOnlyList<ChatMessage> m, string? s, CancellationToken ct) => Task.FromResult("{}"); }
    internal sealed class Recorder : IAudioRecorder { public bool IsRecording => false; public void Start() { } public byte[] Stop() => []; }
    internal sealed class Stt : ITranscriber { public Task<string> TranscribeAsync(byte[] a, string f, string m, CancellationToken ct) => Task.FromResult(""); }
}
