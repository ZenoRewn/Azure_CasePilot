using CasePilot.App.Mvvm;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using CasePilot.Core.Plan;
using CasePilot.Core.Support;
using CasePilot.Core.Abstractions;

namespace CasePilot.App.ViewModels;

public enum Page { Create, History, AISettings, Contacts, Mcp }

public enum LoginMethod { Cli, Device, Sp }

/// <summary>确认对话框 (视图层实现; 测试使用假实现).</summary>
public interface IDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string primary);
}

internal static class Errors
{
    public static string Text(Exception e) => e is AppError a ? a.Display : e.Message;
}

/// <summary>应用外壳: 登录状态、租户 / 订阅加载链与持久化、导航、身份卡与支持计划.</summary>
public sealed class ShellVM : ObservableObject
{
    public const int MaxRecentProblems = 6;
    public const int MaxRecentServices = 4;

    private readonly ISupportApi _api;
    private readonly ILoginService _auth;
    private readonly ISettingsStore _settings;

    public ShellVM(ISupportApi api, ILoginService auth, ISettingsStore settings)
    {
        (_api, _auth, _settings) = (api, auth, settings);
        _contact = settings.Get<Contact>("contact") ?? new Contact();
        _recentProblems = settings.Get<List<RecentProblem>>("recentProblems") ?? [];
        NavigateCommand = new RelayCommand<Page>(p => CurrentPage = p);
    }

    // ---- 身份 ----

    Identity? _identity;
    public Identity? Identity
    {
        get => _identity;
        private set
        {
            if (!SetProperty(ref _identity, value)) return;
            OnPropertyChanged(nameof(IsLoggedIn));
            RaiseIdentityCard();
        }
    }

    public bool IsLoggedIn => Identity is not null;
    public bool AutoLogin { get; private set; } = true;
    public LoginMethod? RequestedLogin { get; private set; }

    public string UserName => Identity?.User ?? "";
    public string Initials => UserName.Length == 0 ? "" : UserName[..1].ToUpperInvariant();

    public string MethodLine
    {
        get
        {
            if (Identity is null) return "";
            var t = Tenants.FirstOrDefault(x => x.Id == TenantId);
            var domain = t is null ? Identity.TenantName ?? "" : t.Domain.Length > 0 ? t.Domain : t.Name;
            return domain.Length == 0 ? Identity.Method : $"{Identity.Method} · {domain}";
        }
    }

    public string PlanLine => Plan.Name is null ? "支持计划未知" : $"支持计划: {Plan.Name}";
    public string PlanTooltip => Plan.Name is null ? "支持计划未知" : $"支持计划: {Plan.Name} ({Plan.SourceLabel})";

    void RaiseIdentityCard()
    {
        OnPropertyChanged(nameof(UserName));
        OnPropertyChanged(nameof(Initials));
        OnPropertyChanged(nameof(MethodLine));
    }

    // ---- 范围 ----

    IReadOnlyList<Tenant> _tenants = [];
    public IReadOnlyList<Tenant> Tenants { get => _tenants; private set { if (SetProperty(ref _tenants, value)) RaiseIdentityCard(); } }

    IReadOnlyList<Subscription> _subscriptions = [];
    public IReadOnlyList<Subscription> Subscriptions { get => _subscriptions; private set => SetProperty(ref _subscriptions, value); }

    string? _tenantId;
    public string? TenantId { get => _tenantId; private set { if (SetProperty(ref _tenantId, value)) RaiseIdentityCard(); } }

    string _subscriptionId = "";
    public string SubscriptionId
    {
        get => _subscriptionId;
        private set { if (SetProperty(ref _subscriptionId, value)) OnPropertyChanged(nameof(Sub)); }
    }

    /// <summary>null 表示租户级.</summary>
    public string? Sub => SubscriptionId.Length == 0 ? null : SubscriptionId;

    IReadOnlyList<SupportService> _services = [];
    public IReadOnlyList<SupportService> Services { get => _services; private set => SetProperty(ref _services, value); }

    SupportPlan _plan = SupportPlan.Unknown;
    public SupportPlan Plan
    {
        get => _plan;
        set
        {
            if (!SetProperty(ref _plan, value)) return;
            OnPropertyChanged(nameof(PlanLine));
            OnPropertyChanged(nameof(PlanTooltip));
        }
    }

    // ---- Case 列表 ----

    IReadOnlyList<TicketSummary> _tickets = [];
    public IReadOnlyList<TicketSummary> Tickets { get => _tickets; private set => SetProperty(ref _tickets, value); }

    int _historyDays = 30;
    public int HistoryDays { get => _historyDays; set => SetProperty(ref _historyDays, value); }

    bool _loadingTickets;
    public bool LoadingTickets { get => _loadingTickets; private set => SetProperty(ref _loadingTickets, value); }

    string? _ticketsError;
    /// <summary>最近一次加载 Case 列表的错误; 成功加载后清空. 列表页据此标记旧数据或显示错误状态.</summary>
    public string? TicketsError { get => _ticketsError; private set => SetProperty(ref _ticketsError, value); }

    string? _banner;
    public string? Banner { get => _banner; set => SetProperty(ref _banner, value); }

    // ---- 导航 ----

    Page _page = Page.Create;
    public Page CurrentPage { get => _page; set => SetProperty(ref _page, value); }
    public RelayCommand<Page> NavigateCommand { get; }

    // ---- 联系人与最近使用 ----

    Contact _contact;
    public Contact Contact
    {
        get => _contact;
        set
        {
            if (!SetProperty(ref _contact, value)) return;
            _settings.Set("contact", value);
            OnPropertyChanged(nameof(ContactValid));
        }
    }

    public bool ContactValid => Contact.IsValid;

    List<RecentProblem> _recentProblems;
    public IReadOnlyList<RecentProblem> RecentProblems => _recentProblems;

    public void Remember(SupportService service, ProblemClassification problem)
    {
        var r = new RecentProblem(service.Name, service.DisplayName, problem.Id, problem.DisplayName);
        _recentProblems = [r, .. _recentProblems.Where(x => x.ProblemId != r.ProblemId).Take(MaxRecentProblems - 1)];
        _settings.Set("recentProblems", _recentProblems);
        OnPropertyChanged(nameof(RecentProblems));
    }

    public IReadOnlyList<string> RecentServices => _settings.Get<List<string>>("recentSvc") ?? [];

    public void RememberService(string displayName)
    {
        _settings.Set("recentSvc", RecentServices.Where(x => x != displayName).Prepend(displayName).Take(MaxRecentServices).ToList());
        OnPropertyChanged(nameof(RecentServices));
    }

    public string PortalUrl(string name) => PortalLink.For(Identity?.Cloud ?? AzureCloud.Global, Sub, name);

    // ---- 加载链: 登录 → 租户 → 订阅 → Case / 计划 ----

    public async Task AfterLoginAsync(Identity id, CancellationToken ct = default)
    {
        Identity = id;
        Banner = null;
        IReadOnlyList<Tenant> list;
        try { list = await _api.TenantsAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { list = []; }
        // App Registration 列租户时常无 displayName, 用域名代替 GUID
        var tenants = list.Select(t => t.Name == t.Id && t.Domain.Length > 0 ? new Tenant(t.Id, t.Domain, t.Domain) : t).ToList();
        if (tenants.Count == 0 && id.Tenant.Length > 0) tenants.Add(new Tenant(id.Tenant, id.TenantName ?? id.Tenant, ""));
        Tenants = tenants;
        var saved = _settings.Get<string>("tenant");
        var pick = tenants.FirstOrDefault(t => t.Id == saved)?.Id
                   ?? tenants.FirstOrDefault(t => t.Id == id.Tenant)?.Id
                   ?? tenants.FirstOrDefault()?.Id;
        if (pick is not null) await SelectTenantAsync(pick, ct);
    }

    public async Task SelectTenantAsync(string tenant, CancellationToken ct = default)
    {
        TenantId = tenant;
        _settings.Set("tenant", tenant);
        Subscriptions = [];
        Services = [];
        try { Subscriptions = await _api.SubscriptionsAsync(tenant, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Banner = "加载订阅失败: " + Errors.Text(e); }
        var saved = _settings.Get<string>("sub." + tenant);
        var pick = saved is not null && (saved.Length == 0 || Subscriptions.Any(s => s.Id == saved))
            ? saved : Subscriptions.FirstOrDefault()?.Id ?? "";
        await SelectSubscriptionAsync(pick, ct);
        try { Services = await _api.ServicesAsync(tenant, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Banner = "加载服务失败: " + Errors.Text(e); }
    }

    public async Task SelectSubscriptionAsync(string sub, CancellationToken ct = default)
    {
        if (TenantId is not { } t) return;
        SubscriptionId = sub;
        _settings.Set("sub." + t, sub);
        await LoadTicketsAsync(ct);
    }

    public async Task LoadTicketsAsync(CancellationToken ct = default)
    {
        if (TenantId is not { } t) return;
        LoadingTickets = true;
        try
        {
            Tickets = await _api.TicketsAsync(t, Sub, HistoryDays, ct);
            // 无公开 API 查询支持计划: 综合最近一年 Case 与订阅类型推断, 并标注来源
            IReadOnlyList<TicketSummary> year;
            try { year = HistoryDays >= 365 ? Tickets : await _api.TicketsAsync(t, Sub, 365, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { year = Tickets; }
            var quota = "";
            if (Sub is { } s)
            {
                try { quota = await _api.QuotaIdAsync(t, s, ct); }
                catch (Exception e) when (e is not OperationCanceledException) { }
            }
            Plan = SupportPlan.Infer(year, quota);
            TicketsError = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            TicketsError = Errors.Text(e);
            Banner = "加载 Case 失败: " + Errors.Text(e);
        }
        finally { LoadingTickets = false; }
    }

    public void Logout(LoginMethod? switchTo = null)
    {
        AutoLogin = false;
        RequestedLogin = switchTo;
        _auth.Logout();
        Identity = null;
        Tenants = [];
        Subscriptions = [];
        Tickets = [];
        Services = [];
        TenantId = null;
        SubscriptionId = "";
        Plan = SupportPlan.Unknown;
    }
}
