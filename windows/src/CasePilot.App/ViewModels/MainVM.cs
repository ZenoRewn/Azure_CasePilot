using CasePilot.App.Mvvm;
using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;
using CasePilot.Core.Support;

namespace CasePilot.App.ViewModels;

/// <summary>组合根传入的依赖 (真实: Platform 服务; 快照 / 测试: 假实现).</summary>
public sealed record AppDeps(
    ISupportApi Api, ILoginService Auth, ISettingsStore Settings, ISecretStore Secrets, IShell Os, IDialogs Dialogs,
    IDelay Delay, IChat Chat, IAudioRecorder Recorder, ITranscriber Transcriber, string PythonPath, string RepoPath,
    IStreamingTranscriber? Streaming = null, Func<CancellationToken, Task>? ChatProbe = null, Func<CancellationToken, Task>? SttProbe = null);

/// <summary>主窗口: 组合各页面 ViewModel, 处理详情面板、Toast、范围选择与快捷键.</summary>
public sealed class MainVM : ObservableObject
{
    public static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3);

    private readonly AppDeps _d;

    public MainVM(AppDeps d)
    {
        _d = d;
        Shell = new ShellVM(d.Api, d.Auth, d.Settings);
        Login = new LoginVM(Shell, d.Auth, d.Secrets, d.Settings, d.Os);
        AISettings = new AISettingsVM(d.Settings, d.Secrets, d.ChatProbe, d.SttProbe, () => Shell.Identity?.User);
        Create = new CreateCaseVM(Shell, d.Api, d.Chat, d.Dialogs, d.Secrets, () => AISettingsVM.Load(d.Settings), d.Os);
        History = new HistoryVM(Shell);
        Contact = new ContactSettingsVM(Shell);
        Mcp = new McpSettingsVM(Shell, d.Os, d.PythonPath, d.RepoPath);

        Shell.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ShellVM.Tickets): OnPropertyChanged(nameof(OpenBadge)); break;
                case nameof(ShellVM.Identity): AISettings.RefreshIdentity(); OnPropertyChanged(nameof(ShowLogin)); if (!Shell.IsLoggedIn) CloseDetail(); break;
                case nameof(ShellVM.Subscriptions): OnPropertyChanged(nameof(SubscriptionOptions)); OnPropertyChanged(nameof(SelectedSubscription)); break;
                case nameof(ShellVM.SubscriptionId): OnPropertyChanged(nameof(SelectedSubscription)); break;
                case nameof(ShellVM.TenantId): OnPropertyChanged(nameof(SelectedTenant)); break;
                case nameof(ShellVM.CurrentPage):
                    if (Shell.CurrentPage == Page.Create) Create.RefreshAiLabel();
                    break;
            }
        };

        GoCreateCommand = new RelayCommand(() => Shell.CurrentPage = Page.Create);
        GoHistoryCommand = new RelayCommand(() => Shell.CurrentPage = Page.History);
        GoSettingsCommand = new RelayCommand(() => Shell.CurrentPage = Page.AISettings);
        OpenAISettingsCommand = GoSettingsCommand;
        RefreshCommand = new AsyncRelayCommand(() => Shell.LoadTicketsAsync());
        EscapeCommand = new RelayCommand(CloseDetail);
        DismissBannerCommand = new RelayCommand(() => Shell.Banner = null);
        CopyBannerCommand = new RelayCommand(() => { if (Shell.Banner is { } b) d.Os.Copy(b); });
        RetryCommand = new AsyncRelayCommand(RetryAsync);
        LogoutCommand = new RelayCommand<LoginMethod?>(m => Shell.Logout(m));
    }

    public ShellVM Shell { get; }
    public LoginVM Login { get; }
    public CreateCaseVM Create { get; }
    public HistoryVM History { get; }
    public AISettingsVM AISettings { get; }
    public ContactSettingsVM Contact { get; }
    public McpSettingsVM Mcp { get; }
    public AppDeps Deps => _d;

    public bool ShowLogin => !Shell.IsLoggedIn;

    public string OpenBadge => Shell.Tickets.Count(t => t.IsOpen) is var n and > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    // ---- 范围选择 ----

    public string? SelectedTenant
    {
        get => Shell.TenantId;
        set { if (value is not null && value != Shell.TenantId) LastLoad = Shell.SelectTenantAsync(value); }
    }

    public IReadOnlyList<Subscription> SubscriptionOptions =>
        [new Subscription("", "无 · 租户级", ""), .. Shell.Subscriptions];

    public string SelectedSubscription
    {
        get => Shell.SubscriptionId;
        set { if (value is not null && value != Shell.SubscriptionId) LastLoad = Shell.SelectSubscriptionAsync(value); }
    }

    /// <summary>最近一次由绑定触发的加载 (测试等待用).</summary>
    public Task LastLoad { get; private set; } = Task.CompletedTask;

    // ---- 详情面板 ----

    DetailVM? _detail;
    public DetailVM? Detail
    {
        get => _detail;
        private set { if (SetProperty(ref _detail, value)) OnPropertyChanged(nameof(IsDetailOpen)); }
    }

    public bool IsDetailOpen => Detail is not null;

    public async Task OpenDetailAsync(TicketSummary t)
    {
        var vm = new DetailVM(Shell, _d.Api, _d.Dialogs, _d.Os, _d.Delay, t);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DetailVM.Toast) && vm.Toast is { } s) _ = ShowToastAsync(s);
        };
        Detail = vm;
        await vm.LoadAsync();
    }

    public void CloseDetail() => Detail = null;

    // ---- Toast ----

    string? _toast;
    public string? Toast { get => _toast; private set => SetProperty(ref _toast, value); }

    public async Task ShowToastAsync(string text)
    {
        Toast = text;
        await _d.Delay.DelayAsync(ToastDuration, CancellationToken.None);
        if (Toast == text) Toast = null;
    }

    async Task RetryAsync()
    {
        Shell.Banner = null;
        if (Shell.TenantId is { } t) await Shell.SelectTenantAsync(t);
    }

    // ---- 命令 / 快捷键 ----

    public RelayCommand GoCreateCommand { get; }
    public RelayCommand GoHistoryCommand { get; }
    public RelayCommand GoSettingsCommand { get; }
    public RelayCommand OpenAISettingsCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand EscapeCommand { get; }
    public RelayCommand DismissBannerCommand { get; }
    public RelayCommand CopyBannerCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public RelayCommand<LoginMethod?> LogoutCommand { get; }

    public MicVM NewMic(Action<string> insert, Action<string?>? preview = null) => new(_d.Recorder, _d.Transcriber, insert, _d.Os, _d.Streaming, preview);
}
