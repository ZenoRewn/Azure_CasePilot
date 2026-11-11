using System.Globalization;
using System.Text.Json.Nodes;
using CasePilot.App.Mvvm;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Models;
using CasePilot.Core.Support;

namespace CasePilot.App.ViewModels;

public enum StatusFilter { All, Open, Closed }

public sealed record DayOption(int Days, string Label);

/// <summary>我的 Case: 状态计数、时间范围、搜索与筛选.</summary>
public sealed class HistoryVM : ObservableObject
{
    private readonly ShellVM _shell;

    public HistoryVM(ShellVM shell)
    {
        _shell = shell;
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellVM.Tickets) or nameof(ShellVM.LoadingTickets) or nameof(ShellVM.HistoryDays)
                or nameof(ShellVM.TicketsError))
                OnPropertyChanged(string.Empty);
        };
        RefreshCommand = new AsyncRelayCommand(() => _shell.LoadTicketsAsync());
    }

    public static IReadOnlyList<DayOption> DayOptions { get; } =
        [new(7, "近 7 天"), new(30, "近 30 天"), new(90, "近 90 天"), new(365, "近 1 年")];

    StatusFilter _filter;
    public StatusFilter Filter { get => _filter; set { if (SetProperty(ref _filter, value)) Refresh(); } }

    string _query = "";
    public string Query { get => _query; set { if (SetProperty(ref _query, value)) Refresh(); } }

    void Refresh()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(IsEmpty));
    }

    static bool IsClosed(TicketSummary t) => t.Status.Equals("closed", StringComparison.OrdinalIgnoreCase);

    public string AllLabel => $"全部 {_shell.Tickets.Count}";
    public string OpenLabel => $"处理中 {_shell.Tickets.Count(t => t.IsOpen)}";
    public string ClosedLabel => $"已关闭 {_shell.Tickets.Count(IsClosed)}";

    public int Days => _shell.HistoryDays;
    public bool Loading => _shell.LoadingTickets;

    public IReadOnlyList<TicketSummary> Rows
    {
        get
        {
            var q = Query.Trim();
            return _shell.Tickets
                .Where(t => Filter switch { StatusFilter.Open => t.IsOpen, StatusFilter.Closed => IsClosed(t), _ => true })
                .Where(t => q.Length == 0 || new[] { t.TicketNumber, t.Title, t.Service }
                    .Any(f => f.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                .OrderByDescending(t => t.Created ?? DateTimeOffset.MinValue)
                .ToList();
        }
    }

    public bool IsEmpty => Rows.Count == 0 && !_shell.LoadingTickets && !LoadFailed;

    /// <summary>加载失败的错误文本 (无错误时为 null).</summary>
    public string? LoadError => _shell.TicketsError;
    /// <summary>加载失败且没有可显示的数据: 显示错误状态而非 "没有符合条件的 Case".</summary>
    public bool LoadFailed => LoadError is not null && _shell.Tickets.Count == 0 && !_shell.LoadingTickets;
    /// <summary>加载失败但仍显示上次加载的数据.</summary>
    public bool IsStale => LoadError is not null && _shell.Tickets.Count > 0;

    public AsyncRelayCommand RefreshCommand { get; }

    public async Task SetDaysAsync(int days)
    {
        if (_shell.HistoryDays == days) return;
        _shell.HistoryDays = days;
        await _shell.LoadTicketsAsync();
    }
}

public sealed record OverviewRow(string Key, string Value, bool Mono);

/// <summary>Case 详情: 概览、沟通记录、补充信息与关闭.</summary>
public sealed class DetailVM : ObservableObject
{
    public static readonly TimeSpan ReloadAfterReply = TimeSpan.FromSeconds(2);

    private readonly ShellVM _shell;
    private readonly ISupportApi _api;
    private readonly IDialogs _dialogs;
    private readonly IShell _os;
    private readonly IDelay _delay;
    private readonly TicketSummary _summary;

    public DetailVM(ShellVM shell, ISupportApi api, IDialogs dialogs, IShell os, IDelay delay, TicketSummary summary)
    {
        (_shell, _api, _dialogs, _os, _delay, _summary) = (shell, api, dialogs, os, delay, summary);
        CopyNumberCommand = new RelayCommand(() => { _os.Copy(Summary.TicketNumber); Toast = "已复制 Case 号"; });
        OpenPortalCommand = new RelayCommand(() => _os.OpenUrl(_shell.PortalUrl(Summary.Id)));
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        CloseCommand = new AsyncRelayCommand(CloseAsync, () => CanClose);
    }

    void Changed()
    {
        OnPropertyChanged(string.Empty);
        SendCommand?.NotifyCanExecuteChanged();
        CloseCommand?.NotifyCanExecuteChanged();
    }

    TicketDetail? _detail;
    public TicketDetail? Detail { get => _detail; private set { _detail = value; Changed(); } }

    public TicketSummary Summary => Detail?.Summary ?? _summary;
    public bool IsOpen => Summary.IsOpen;

    string? _error;
    public string? Error { get => _error; private set { _error = value; Changed(); } }

    string? _toast;
    /// <summary>视图负责 3 秒后自动消失.</summary>
    public string? Toast { get => _toast; set => SetProperty(ref _toast, value); }

    int _tab;
    public int SelectedTab { get => _tab; set => SetProperty(ref _tab, value); }

    string _reply = "";
    public string Reply { get => _reply; set { _reply = value; Changed(); } }

    bool _sending, _closing;
    public bool Sending { get => _sending; private set { _sending = value; Changed(); } }
    public bool Closing { get => _closing; private set { _closing = value; Changed(); } }

    public bool CanSend => IsOpen && Detail is not null && Reply.Trim().Length > 0 && !Sending;
    public bool CanClose => IsOpen && !Closing;

    public RelayCommand CopyNumberCommand { get; }
    public RelayCommand OpenPortalCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand CloseCommand { get; }

    public IReadOnlyList<Communication> Communications =>
        Detail?.Communications.OrderByDescending(c => c.Created ?? DateTimeOffset.MinValue).ToList() ?? [];

    public string CommunicationsHeader => $"沟通记录 ({Detail?.Communications.Count ?? 0})";

    public string Description => Str(Detail?.Props, "description");

    public IReadOnlyList<OverviewRow> Overview
    {
        get
        {
            if (Detail?.Props is not { } p) return [];
            var cd = p["contactDetails"] as JsonObject;
            var sla = p["serviceLevelAgreement"] as JsonObject;
            var slaText = sla is null ? "" : Date(Str(sla, "expirationTime"))
                + (sla["slaMinutes"] is JsonValue m && m.TryGetValue<int>(out var mins) ? $" · {mins} 分钟" : "");
            var quota = (p["quotaTicketDetails"]?["quotaChangeRequests"] as JsonArray)?.OfType<JsonObject>()
                .Select(q => $"{Str(q, "region")}: {Str(q, "payload")}") ?? [];
            var plan = Str(p, "supportPlanDisplayName");
            OverviewRow[] rows =
            [
                new("服务", Str(p, "serviceDisplayName"), false),
                new("问题类型", Str(p, "problemClassificationDisplayName"), false),
                new("创建时间", Date(Str(p, "createdDate")), false),
                new("最后更新", Date(Str(p, "modifiedDate")), false),
                new("SLA 首次响应截止", slaText, false),
                new("支持计划", plan.Length > 0 ? plan : Str(p, "supportPlanType"), false),
                new("支持工程师", Str(p["supportEngineer"] as JsonObject, "emailAddress"), false),
                new("24×7 响应", p["require24X7Response"] is JsonValue v && v.TryGetValue<bool>(out var b) && b ? "是" : "否", false),
                new("联系人", Join($"{Str(cd, "lastName")}{Str(cd, "firstName")}", Str(cd, "primaryEmailAddress")), false),
                new("语言 / 时区", Join(Str(cd, "preferredSupportLanguage"), Str(cd, "preferredTimeZone")), false),
                new("关联资源", Str(p["technicalTicketDetails"] as JsonObject, "resourceId"), true),
                new("配额请求", string.Join("\n", quota), true),
            ];
            return rows.Where(r => r.Value.Length > 0).ToList();
        }
    }

    static string Join(params string[] parts) => string.Join(" · ", parts.Where(x => x.Length > 0));

    static string Str(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static string Date(string s) =>
        SupportClient.Date(s) is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    public async Task LoadAsync()
    {
        if (_shell.TenantId is not { } t) return;
        try
        {
            Detail = await _api.DetailAsync(t, _shell.Sub, _summary.Id, CancellationToken.None);
            Error = null;
        }
        catch (Exception e) when (e is not OperationCanceledException) { Error = Errors.Text(e); }
    }

    public async Task SendAsync()
    {
        if (!CanSend || _shell.TenantId is not { } t) return;
        Sending = true;
        try
        {
            // 应用注册没有用户身份, 以 Case 联系人邮箱作为发件人
            var sender = _shell.Identity?.Method == "App Registration"
                ? Str(Detail?.Props["contactDetails"] as JsonObject, "primaryEmailAddress") is { Length: > 0 } s ? s : null
                : null;
            await _api.ReplyAsync(t, _shell.Sub, Summary.Id, Reply, sender, CancellationToken.None);
            Reply = "";
            Toast = "补充信息已发送";
            SelectedTab = 1;
            await _delay.DelayAsync(ReloadAfterReply, CancellationToken.None);
            await LoadAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException) { Error = Errors.Text(e); }
        finally { Sending = false; }
    }

    public async Task CloseAsync()
    {
        if (!CanClose || _shell.TenantId is not { } t) return;
        var number = Summary.TicketNumber;
        if (!await _dialogs.ConfirmAsync($"关闭 Case {number}?",
                "关闭后支持工程师将停止处理。如问题复发, 需新建 Case 或在 Portal 中重新打开。", "关闭 Case")) return;
        Closing = true;
        try
        {
            await _api.CloseAsync(t, _shell.Sub, Summary.Id, CancellationToken.None);
            Toast = $"Case {number} 已关闭";
            await LoadAsync();
            await _shell.LoadTicketsAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException) { Error = Errors.Text(e); }
        finally { Closing = false; }
    }
}
