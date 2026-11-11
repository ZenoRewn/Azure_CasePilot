using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CasePilot.App.Mvvm;
using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;
using CasePilot.Core.Models;

namespace CasePilot.App.ViewModels;

/// <summary>设置 → AI 与语音. 配置存 settings.json (aiConfig), Key 只存凭据管理器.</summary>
public sealed class AISettingsVM : ObservableObject
{
    public const string ConfigKey = "aiConfig";
    const string Stored = "已保存 ●●●●";

    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private AIConfig _c;

    private readonly Func<CancellationToken, Task>? _chatProbe, _sttProbe;
    private readonly Func<string?> _user;

    public AISettingsVM(ISettingsStore settings, ISecretStore secrets, Func<CancellationToken, Task>? chatProbe = null,
        Func<CancellationToken, Task>? sttProbe = null, Func<string?>? user = null)
    {
        (_settings, _secrets, _chatProbe, _sttProbe, _user) = (settings, secrets, chatProbe, sttProbe, user ?? (() => null));
        // 旧设置没有认证方式: 按已存 Key 推断一次并保存 (升级后行为不变)
        _c = Load(settings).Migrated(secrets);
        _settings.Set(ConfigKey, _c.ToJson());
        TestChatCommand = new AsyncRelayCommand(() => ProbeAsync(_chatProbe, AIKind.Chat), () => _chatProbe is not null);
        TestSttCommand = new AsyncRelayCommand(() => ProbeAsync(_sttProbe, AIKind.Stt), () => _sttProbe is not null);
        ResetCommand = new RelayCommand(Reset);
        SaveChatKeyCommand = new RelayCommand(SaveChatKey);
        ClearChatKeyCommand = new RelayCommand(ClearChatKey);
        SaveSttKeyCommand = new RelayCommand(SaveSttKey);
        ClearSttKeyCommand = new RelayCommand(ClearSttKey);
    }

    public static AIConfig Load(ISettingsStore settings) => AIConfig.FromJson(settings.Get<string>(ConfigKey));

    public static IReadOnlyList<SttProvider> Providers { get; } = Enum.GetValues<SttProvider>();

    void Update(AIConfig c)
    {
        if (c == _c) return;
        _c = c;
        _settings.Set(ConfigKey, c.ToJson());
        OnPropertyChanged(string.Empty);
    }

    public string ChatEndpoint { get => _c.ChatEndpoint; set => Update(_c with { ChatEndpoint = value }); }
    public string ChatDeployment { get => _c.ChatDeployment; set => Update(_c with { ChatDeployment = value }); }
    public SttProvider SttProvider { get => _c.SttProvider; set => Update(_c with { SttProvider = value }); }
    public string SttEndpoint { get => _c.SttEndpoint; set => Update(_c with { SttEndpoint = value }); }
    public string SttDeployment { get => _c.SttDeployment; set => Update(_c with { SttDeployment = value }); }
    public string SttStreamDeployment { get => _c.SttStreamDeployment; set => Update(_c with { SttStreamDeployment = value }); }
    public string SttLanguage { get => _c.SttLanguage; set => Update(_c with { SttLanguage = value }); }
    public string SpeechRegion { get => _c.SpeechRegion; set => Update(_c with { SpeechRegion = value }); }

    public static IReadOnlyList<AIAuth> AuthModes { get; } = [AIAuth.Entra, AIAuth.Key];
    public AIAuth ChatAuth { get => _c.ChatAuth ?? AIAuth.Entra; set => Update(_c with { ChatAuth = value }); }
    public AIAuth SttAuth { get => _c.SttAuth ?? AIAuth.Entra; set => Update(_c with { SttAuth = value }); }
    public bool ChatUsesKey => ChatAuth == AIAuth.Key;
    public bool SttUsesKey => SttAuth == AIAuth.Key;
    public string ChatEntraHint => EntraHint(AIKind.Chat);
    public string SttEntraHint => EntraHint(AIKind.Stt);
    string EntraHint(AIKind kind) =>
        $"以 {(_user() is { Length: > 0 } u ? u : "当前登录身份")} 调用 · 需要 {AIKeys.Role(_c, kind)} 角色";
    public string SttKeyHint => IsSpeech ? "使用 Speech Key 时区域必填" : "";

    // 测试连接
    public AsyncRelayCommand TestChatCommand { get; }
    public AsyncRelayCommand TestSttCommand { get; }
    string? _chatTest, _sttTest;
    bool _chatTestOk, _sttTestOk;
    public string ChatTestKind => ChatTestOk ? "success" : "warning";
    public string SttTestKind => SttTestOk ? "success" : "warning";
    public string? ChatTest { get => _chatTest; private set { if (SetProperty(ref _chatTest, value)) OnPropertyChanged(nameof(ChatTestKind)); } }
    public string? SttTest { get => _sttTest; private set { if (SetProperty(ref _sttTest, value)) OnPropertyChanged(nameof(SttTestKind)); } }
    public bool ChatTestOk { get => _chatTestOk; private set => SetProperty(ref _chatTestOk, value); }
    public bool SttTestOk { get => _sttTestOk; private set => SetProperty(ref _sttTestOk, value); }

    public async Task ProbeAsync(Func<CancellationToken, Task>? probe, AIKind kind)
    {
        if (probe is null) return;
        void Report(bool ok, string text)
        {
            if (kind == AIKind.Chat) { ChatTestOk = ok; ChatTest = text; } else { SttTestOk = ok; SttTest = text; }
        }
        Report(false, "正在测试…");
        try
        {
            await probe(CancellationToken.None);
            Report(true, "连接成功");
        }
        catch (Exception e)
        {
            var status = (e as AppError)?.Status;
            var usesKey = kind == AIKind.Chat ? ChatUsesKey : SttUsesKey;
            var hint = status is 401 or 403
                ? usesKey ? "请检查 Key 与 Endpoint / 区域" : $"当前身份缺少 {AIKeys.Role(_c, kind)} 角色"
                : null;
            var first = e.Message.Split('\n')[0];
            Report(false, hint is null ? first : $"{first} · {hint}");
        }
    }

    /// <summary>登录身份变化时刷新说明行.</summary>
    public void RefreshIdentity() { OnPropertyChanged(nameof(ChatEntraHint)); OnPropertyChanged(nameof(SttEntraHint)); }

    public bool IsSpeech => SttProvider == SttProvider.AzureSpeech;
    /// <summary>Speech 填写区域后忽略 Endpoint.</summary>
    public bool SttEndpointEnabled => !IsSpeech || SpeechRegion.Trim().Length == 0;
    public string SttKeyLabel => IsSpeech ? "Speech Key" : "API Key";

    string _chatKeyInput = "", _sttKeyInput = "";
    public string ChatKeyInput { get => _chatKeyInput; set => SetProperty(ref _chatKeyInput, value); }
    public string SttKeyInput { get => _sttKeyInput; set => SetProperty(ref _sttKeyInput, value); }

    public bool ChatKeyStored => !string.IsNullOrEmpty(_secrets.Read(AIClient.ChatKeyTarget));
    public bool SttKeyStored => !string.IsNullOrEmpty(_secrets.Read(AIClient.SttKeyTarget));
    public string ChatKeyStatus => ChatKeyStored ? Stored : "未设置";
    public string SttKeyStatus => SttKeyStored ? Stored : "未设置";

    public RelayCommand ResetCommand { get; }
    public RelayCommand SaveChatKeyCommand { get; }
    public RelayCommand ClearChatKeyCommand { get; }
    public RelayCommand SaveSttKeyCommand { get; }
    public RelayCommand ClearSttKeyCommand { get; }

    void WriteKey(string target, string? value)
    {
        _secrets.Write(target, value);
        OnPropertyChanged(string.Empty);
    }

    public void SaveChatKey()
    {
        if (ChatKeyInput.Trim().Length == 0) return;
        WriteKey(AIClient.ChatKeyTarget, ChatKeyInput.Trim());
        ChatKeyInput = "";
        ChatAuth = AIAuth.Key;
    }

    public void ClearChatKey() => WriteKey(AIClient.ChatKeyTarget, null);

    public void SaveSttKey()
    {
        if (SttKeyInput.Trim().Length == 0) return;
        WriteKey(AIClient.SttKeyTarget, SttKeyInput.Trim());
        SttKeyInput = "";
        SttAuth = AIAuth.Key;
    }

    public void ClearSttKey() => WriteKey(AIClient.SttKeyTarget, null);

    public void Reset()
    {
        _secrets.Write(AIClient.ChatKeyTarget, null);
        _secrets.Write(AIClient.SttKeyTarget, null);
        (ChatKeyInput, SttKeyInput) = ("", "");
        _c = new AIConfig { ChatAuth = AIAuth.Entra, SttAuth = AIAuth.Entra };
        (ChatTest, SttTest) = (null, null);
        _settings.Set(ConfigKey, _c.ToJson());
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>设置 → 联系人. 每次修改立即保存 (经 ShellVM 持久化).</summary>
public sealed class ContactSettingsVM : ObservableObject
{
    private readonly ShellVM _shell;

    public ContactSettingsVM(ShellVM shell)
    {
        _shell = shell;
        _shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellVM.Contact)) OnPropertyChanged(string.Empty); };
    }

    Contact C { get => _shell.Contact; set => _shell.Contact = value; }

    public string LastName { get => C.LastName; set => C = C with { LastName = value.Trim() }; }
    public string FirstName { get => C.FirstName; set => C = C with { FirstName = value.Trim() }; }
    public string Email { get => C.Email; set => C = C with { Email = value.Trim() }; }
    public string Cc { get => C.Cc; set => C = C with { Cc = value }; }
    public string Phone { get => C.Phone; set => C = C with { Phone = value.Trim() }; }
    public bool PreferPhone { get => C.PreferPhone; set => C = C with { PreferPhone = value }; }
    public string Language { get => C.Language; set => C = C with { Language = value }; }
    public string Country { get => C.Country; set => C = C with { Country = value }; }
    public string TimeZone { get => C.TimeZone; set => C = C with { TimeZone = value }; }

    public bool IsValid => C.IsValid;
    public string PhonePlaceholder => PreferPhone ? "必填" : "可选";

    public IReadOnlyList<KeyValuePair<string, string>> Languages { get; } =
        Contact.Languages.Select(x => KeyValuePair.Create(x.Code, x.Name)).ToList();
    public IReadOnlyList<KeyValuePair<string, string>> Countries { get; } =
        Contact.Countries.Select(x => KeyValuePair.Create(x.Code, x.Name)).ToList();
    public IReadOnlyList<string> TimeZones => Contact.TimeZones;
}

/// <summary>设置 → MCP: 本机 stdio 配置片段, command 为 az 自带 python.exe 的绝对路径.</summary>
public sealed class McpSettingsVM : ObservableObject
{
    public const string Note = "复用 Azure CLI 登录。写操作 (创建 / 补充 / 关闭) 需客户端在用户确认后传 confirm=true。";

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ShellVM _shell;
    private readonly IShell _os;
    private readonly string _python;
    private readonly string _repo;

    public McpSettingsVM(ShellVM shell, IShell os, string pythonPath, string repoPath)
    {
        (_shell, _os, _python, _repo) = (shell, os, pythonPath, repoPath);
        _shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellVM.SubscriptionId)) OnPropertyChanged(nameof(Snippet)); };
        CopyCommand = new RelayCommand(() => _os.Copy(Snippet));
    }

    public static string DefaultPython(string programFiles) =>
        System.IO.Path.Combine(programFiles, "Microsoft SDKs", "Azure", "CLI2", "python.exe");

    public string Snippet => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            ["casepilot"] = new JsonObject
            {
                ["command"] = _python,
                ["args"] = new JsonArray("-m", "casepilot.mcp"),
                ["cwd"] = _repo,
                ["env"] = new JsonObject { ["CASEPILOT_SUBSCRIPTION_ID"] = _shell.SubscriptionId },
            },
        },
    }.ToJsonString(Pretty);

    public RelayCommand CopyCommand { get; }
}
