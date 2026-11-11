using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CasePilot.App.Mvvm;
using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;
using CasePilot.Core.Models;
using CasePilot.Core.Support;

namespace CasePilot.App.ViewModels;

public sealed record QuickService(string Label, SupportService Service);

public sealed class QuestionItem(string question) : ObservableObject
{
    public string Question { get; } = question;
    string _answer = "";
    public string Answer { get => _answer; set => SetProperty(ref _answer, value); }
}

/// <summary>新建 Case: AI 帮写、服务与问题、严重性、描述、联系人与提交.</summary>
public sealed class CreateCaseVM : ObservableObject
{
    public const int MaxTitle = 200;
    const string VmQuotaProblem = "compute-vm (cores-vcpus)";

    static readonly string[] Popular =
    [
        "Azure OpenAI", "Microsoft Foundry", "Virtual Machine running Linux", "Virtual Machine running Windows",
        "Kubernetes Service (AKS)", "Storage Account Management",
    ];

    public static readonly string Template = string.Join("\n",
        "【现象】", "", "【业务影响】(影响用户/请求量, 是否生产环境)", "", "【首次发生时间 (含时区) / 是否持续】", "",
        "【复现步骤】", "1. ", "", "【错误信息 / Request ID / Correlation ID】", "", "【近期变更】", "", "【已尝试的排查】", "");

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ShellVM _shell;
    private readonly ISupportApi _api;
    private readonly IChat _chat;
    private readonly IDialogs _dialogs;
    private readonly ISecretStore _secrets;
    private readonly Func<AIConfig> _config;
    private readonly IShell? _os;

    public CreateCaseVM(ShellVM shell, ISupportApi api, IChat chat, IDialogs dialogs, ISecretStore secrets, Func<AIConfig> config, IShell? os = null)
    {
        (_shell, _api, _chat, _dialogs, _secrets, _config, _os) = (shell, api, chat, dialogs, secrets, config, os);
        // 计划 / 联系人 / 服务 / 最近使用变化都会影响校验与快捷项
        _shell.PropertyChanged += (_, _) => Changed();
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, () => CanSubmit);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !AiBusy && AiText.Trim().Length > 0);
        SkipCommand = new RelayCommand(SkipQuestions);
        TemplateCommand = new RelayCommand(InsertTemplate);
        CopyCreatedCommand = new RelayCommand(() => { if (CreatedNumber is { } n) _os?.Copy(n); });
        OpenCreatedCommand = new RelayCommand(() => { if (CreatedName is { } n) _os?.OpenUrl(_shell.PortalUrl(n)); });
        RefreshAiLabel();
    }

    /// <summary>派生属性较多, 统一通知全部属性变化.</summary>
    void Changed()
    {
        OnPropertyChanged(string.Empty);
        SubmitCommand?.NotifyCanExecuteChanged();
        GenerateCommand?.NotifyCanExecuteChanged();
    }

    bool Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed();
        return true;
    }

    // ---- 服务与问题 ----

    SupportService? _service;
    public SupportService? Service { get => _service; private set => Set(ref _service, value); }

    ProblemClassification? _problem;
    public ProblemClassification? Problem { get => _problem; set => Set(ref _problem, value); }

    IReadOnlyList<ProblemClassification> _problems = [];
    public IReadOnlyList<ProblemClassification> Problems { get => _problems; private set => Set(ref _problems, value); }

    bool _loadingProblems;
    public bool LoadingProblems { get => _loadingProblems; private set => Set(ref _loadingProblems, value); }

    public string ProblemPlaceholder => LoadingProblems ? "加载中…" : Service is null ? "先选择服务" : "请选择";

    IReadOnlyList<AzureResource> _resources = [];
    public IReadOnlyList<AzureResource> Resources { get => _resources; private set => Set(ref _resources, value); }

    string? _resourceId;
    public string? ResourceId { get => _resourceId; set => Set(ref _resourceId, value); }

    public CaseKind Kind => CaseKindExtensions.From(Service);

    public bool IsVmQuota => Kind == CaseKind.Quota && (Problem?.DisplayName.ToLowerInvariant().Contains(VmQuotaProblem) ?? false);

    public IReadOnlyList<QuickService> QuickServices
    {
        get
        {
            var all = _shell.Services;
            var outp = new List<QuickService>();
            if (all.FirstOrDefault(s => CaseKind.Quota.Matches(s)) is { } q) outp.Add(new(CaseKind.Quota.Label(), q));
            if (all.FirstOrDefault(s => CaseKind.Billing.Matches(s)) is { } b) outp.Add(new(CaseKind.Billing.Label(), b));
            var chips = _shell.RecentServices.Concat(Popular).Distinct()
                .Select(n => all.FirstOrDefault(s => s.DisplayName == n)).OfType<SupportService>()
                .Take(4).Select(s => new QuickService(s.DisplayName, s));
            return [.. outp, .. chips];
        }
    }

    public IReadOnlyList<RecentProblem> RecentItems => _shell.RecentProblems.Take(3).ToList();

    // ---- 严重性 ----

    public static IReadOnlyList<Severity> Severities { get; } = Enum.GetValues<Severity>();

    Severity _severity = Severity.Moderate;
    public Severity Severity
    {
        get => _severity;
        set
        {
            if (_severity == value) return;
            _severity = value;
            _require24x7 = value.IsHigh();
            Changed();
        }
    }

    bool _require24x7;
    /// <summary>仅高严重性 (A / A+) 可要求 24×7.</summary>
    public bool Require24x7 { get => _require24x7; set => Set(ref _require24x7, value && Severity.IsHigh()); }
    public bool Can24x7 => Severity.IsHigh();
    public string SeverityCaption => Severity.Detail();
    public bool OverPlan => Severity.IsHigh() && _shell.Plan.AllowsCritical == false;
    public string OverPlanText => "当前支持计划不支持该级别";

    // ---- 描述 ----

    string _title = "";
    public string Title { get => _title; set => Set(ref _title, value.Length > MaxTitle ? value[..MaxTitle] : value); }
    public string TitleCounter => $"{Title.Length}/{MaxTitle}";

    string _description = "";
    public string Description { get => _description; set => Set(ref _description, value); }

    bool _useStart;
    public bool UseStart { get => _useStart; set => Set(ref _useStart, value); }
    DateTimeOffset? _startTime;
    public DateTimeOffset? StartTime { get => _startTime; set => Set(ref _startTime, value); }
    bool _consent = true;
    public bool Consent { get => _consent; set => Set(ref _consent, value); }

    string _quotaRegion = "", _quotaFamily = "", _quotaLimit = "";
    public string QuotaRegion { get => _quotaRegion; set => Set(ref _quotaRegion, value); }
    public string QuotaFamily { get => _quotaFamily; set => Set(ref _quotaFamily, value); }
    public string QuotaLimit { get => _quotaLimit; set => Set(ref _quotaLimit, value); }

    public string ContactSummary => _shell.ContactValid
        ? $"{_shell.Contact.LastName}{_shell.Contact.FirstName} · {_shell.Contact.Email}" : "请先设置联系人";

    // ---- 清单 ----

    bool QuotaValid => QuotaRegion.Length > 0 && QuotaFamily.Length > 0
        && int.TryParse(QuotaLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0;

    IEnumerable<(string Name, bool Ok)> Checklist
    {
        get
        {
            yield return ("问题类型", Service is not null && Problem is not null);
            if (IsVmQuota) yield return ("配额明细", QuotaValid);
            yield return ("标题", Title.Trim().Length >= 5);
            yield return ("描述 (≥20 字)", Description.Trim().Length >= 20);
            yield return ("联系人", _shell.ContactValid);
        }
    }

    public IReadOnlyList<string> Missing => Checklist.Where(c => !c.Ok).Select(c => c.Name).ToList();
    public bool CanSubmit => Missing.Count == 0 && !Submitting && _shell.TenantId is not null;
    public string MissingText => Missing.Count == 0 ? "可以提交" : $"还差 {Missing.Count} 项: {string.Join("、", Missing)}";

    public NewCase FinalDraft => new()
    {
        Service = Service,
        Problem = Problem,
        ResourceId = Kind == CaseKind.Technical ? ResourceId : null,
        Title = Title,
        Description = Description,
        Severity = Severity,
        Require24x7 = Require24x7,
        Consent = Consent,
        StartTime = UseStart ? StartTime : null,
        Quota = IsVmQuota ? new QuotaRequest(QuotaRegion.Trim(), QuotaFamily.Trim(), QuotaLimit.Trim()) : null,
        Contact = _shell.Contact,
    };

    public string PreviewJson() => SupportBody.Build(FinalDraft).ToJsonString(Pretty);

    public string ConfirmText
    {
        get
        {
            var c = _shell.Contact;
            var t = $"标题: {Title}\n服务: {Service?.DisplayName ?? ""}\n严重性: {Severity.Level()} · {Severity.Title()}\n联系人: {c.LastName}{c.FirstName} · {c.Email}";
            if (OverPlan) t += $"\n\n注意: 当前支持计划 ({_shell.Plan.Name ?? ""}) 不支持该级别, 提交可能被拒绝。";
            return t;
        }
    }

    // ---- 动作 ----

    public async Task PickAsync(SupportService s, string? resource = null)
    {
        if (_shell.TenantId is not { } t) return;
        Service = s;
        Problem = null;
        Problems = [];
        ResourceId = null;
        if (Kind == CaseKind.Technical) _shell.RememberService(s.DisplayName);
        LoadingProblems = true;
        try { Problems = await _api.ProblemsAsync(t, s, CancellationToken.None); }
        catch (Exception e) when (e is not OperationCanceledException) { Problems = []; }
        finally { LoadingProblems = false; }
        if (Kind == CaseKind.Quota)
            Problem = Problems.FirstOrDefault(p => p.DisplayName.ToLowerInvariant().Contains(VmQuotaProblem));
        await LoadResourcesAsync();
        if (resource is not null) ResourceId = resource;
    }

    public async Task LoadResourcesAsync()
    {
        Resources = [];
        if (Kind != CaseKind.Technical || _shell.TenantId is not { } t || _shell.Sub is not { } sub
            || Service is not { } s || s.ResourceTypes.Count == 0) return;
        try { Resources = await _api.ResourcesAsync(t, sub, s.ResourceTypes, CancellationToken.None); }
        catch (Exception e) when (e is not OperationCanceledException) { Resources = []; }
    }

    public async Task ReuseAsync(RecentProblem r)
    {
        if (_shell.Services.FirstOrDefault(s => s.Name == r.ServiceName) is not { } s) return;
        await PickAsync(s);
        Problem = Problems.FirstOrDefault(p => p.Id == r.ProblemId);
    }

    public void InsertTemplate() => Description = Template;

    public async Task ApplyDraftAsync(AIDraft d)
    {
        if (d.Service is { } s) await PickAsync(s);
        if (d.Problem is { } p) Problem = Problems.FirstOrDefault(x => x.Id == p.Id) ?? p;
        Severity = d.Severity;
        Require24x7 = d.Severity.IsHigh();
        Title = d.Title;
        Description = d.Description;
    }

    // ---- AI 帮写 ----

    string _aiText = "";
    public string AiText { get => _aiText; set => Set(ref _aiText, value); }

    public ObservableCollection<QuestionItem> Questions { get; } = [];

    bool _aiBusy;
    public bool AiBusy { get => _aiBusy; private set => Set(ref _aiBusy, value); }

    string? _aiStatus;
    public string? AiStatus { get => _aiStatus; private set => Set(ref _aiStatus, value); }
    bool _aiStatusOk;
    public bool AiStatusOk { get => _aiStatusOk; private set => Set(ref _aiStatusOk, value); }
    public bool AiShowSettings => !AiStatusOk && (AiStatus?.Contains("AI 服务错误") ?? false);
    public string GenerateLabel => Questions.Count == 0 ? "生成草稿" : "补充并更新";

    string _aiSettingsLabel = "";
    public string AiSettingsLabel { get => _aiSettingsLabel; private set => Set(ref _aiSettingsLabel, value); }

    public void RefreshAiLabel() =>
        AiSettingsLabel = $"{_config().ChatDeployment} · {(string.IsNullOrEmpty(_secrets.Read(AIClient.ChatKeyTarget)) ? "登录身份" : "API Key")}";

    public async Task GenerateAsync()
    {
        if (_shell.TenantId is not { } t || AiText.Trim().Length == 0) return;
        AiBusy = true;
        try
        {
            IReadOnlyList<QA> qa = Questions.Select(q => new QA(q.Question, q.Answer.Trim().Length == 0 ? "未知" : q.Answer.Trim())).ToList();
            var d = await AIDrafter.DraftAsync(AiText, qa, _shell.Services,
                (s, ct) => _api.ProblemsAsync(t, s, ct), _chat, CancellationToken.None);
            await ApplyDraftAsync(d);
            Questions.Clear();
            foreach (var q in d.Questions) Questions.Add(new QuestionItem(q));
            (AiStatusOk, AiStatus) = d.Ready ? (true, "草稿已填好, 请检查后提交")
                : d.Service is null ? (false, "未能确定服务, 请手动选择")
                : d.Questions.Count > 0 ? (false, "已生成初稿, 回答下面的问题可让描述更完整")
                : (false, "请确认问题类型");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            (AiStatusOk, AiStatus) = (false, Errors.Text(e));
        }
        finally
        {
            AiBusy = false;
            Changed();
        }
    }

    public void SkipQuestions()
    {
        Questions.Clear();
        (AiStatusOk, AiStatus) = (true, "已使用当前草稿, 请检查后提交");
    }

    // ---- 提交 ----

    bool _submitting;
    public bool Submitting { get => _submitting; private set => Set(ref _submitting, value); }

    string? _resultText;
    public string? ResultText { get => _resultText; private set => Set(ref _resultText, value); }
    bool _resultOk;
    public bool ResultOk { get => _resultOk; private set => Set(ref _resultOk, value); }
    public string? CreatedNumber { get; private set; }
    public string? CreatedName { get; private set; }

    public AsyncRelayCommand SubmitCommand { get; }
    public AsyncRelayCommand GenerateCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand TemplateCommand { get; }
    public RelayCommand CopyCreatedCommand { get; }
    public RelayCommand OpenCreatedCommand { get; }

    /// <summary>确认 (默认取消) → 创建 → 记录最近问题 → 清空表单 → 刷新列表.</summary>
    public async Task SubmitAsync()
    {
        if (!CanSubmit || _shell.TenantId is not { } t) return;
        if (!await _dialogs.ConfirmAsync("创建 Support Case?", ConfirmText, "创建")) return;
        Submitting = true;
        ResultText = null;
        try
        {
            var created = await _api.CreateAsync(t, _shell.Sub, FinalDraft, CancellationToken.None);
            if (Service is { } sv && Problem is { } pb) _shell.Remember(sv, pb);
            CreatedName = created.Id;
            CreatedNumber = created.TicketNumber.Length == 0 ? created.Id : created.TicketNumber;
            (ResultOk, ResultText) = (true, $"已创建 Case {CreatedNumber}");
            Title = "";
            Description = "";
            StartTime = null;
            UseStart = false;
            (QuotaRegion, QuotaFamily, QuotaLimit) = ("", "", "");
            await _shell.LoadTicketsAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            (ResultOk, ResultText) = (false, Errors.Text(e));
        }
        finally { Submitting = false; }
    }
}
