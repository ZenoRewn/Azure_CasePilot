using CasePilot.App.Mvvm;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;

namespace CasePilot.App.ViewModels;

/// <summary>App Registration 的非机密部分; Secret 存凭据管理器 CasePilot/sp/&lt;clientId&gt;.</summary>
public sealed record SpInfo(string Tenant, string ClientId, string Cloud);

/// <summary>登录页: Azure CLI | 浏览器登录 (设备码) | 应用注册.</summary>
public sealed class LoginVM : ObservableObject
{
    public const string SilentCliHint = "未检测到可用的 Azure CLI 会话。请执行 az login 后重试, 或改用浏览器登录。\n";

    private readonly ShellVM _shell;
    private readonly ILoginService _auth;
    private readonly ISecretStore _secrets;
    private readonly ISettingsStore _settings;
    private readonly IShell _os;
    private CancellationTokenSource? _deviceCts;
    private bool _initialized;

    public LoginVM(ShellVM shell, ILoginService auth, ISecretStore secrets, ISettingsStore settings, IShell os)
    {
        (_shell, _auth, _secrets, _settings, _os) = (shell, auth, secrets, settings, os);
        CliCommand = new AsyncRelayCommand(() => LoginCliAsync(false), () => !Busy);
        DeviceCommand = new AsyncRelayCommand(LoginDeviceAsync, () => !Busy);
        SpCommand = new AsyncRelayCommand(LoginSpAsync, () => !Busy);
        CancelDeviceCommand = new RelayCommand(CancelDevice);
    }

    public static IReadOnlyList<AzureCloud> Clouds { get; } = Enum.GetValues<AzureCloud>();

    LoginMethod _method = LoginMethod.Cli;
    public LoginMethod Method { get => _method; set => SetProperty(ref _method, value); }

    AzureCloud _cloud = AzureCloud.Global;
    public AzureCloud Cloud { get => _cloud; set => SetProperty(ref _cloud, value); }

    bool _busy;
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            CliCommand.NotifyCanExecuteChanged();
            DeviceCommand.NotifyCanExecuteChanged();
            SpCommand.NotifyCanExecuteChanged();
        }
    }

    string? _error;
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    DeviceCode? _deviceCode;
    public DeviceCode? DeviceCode { get => _deviceCode; private set => SetProperty(ref _deviceCode, value); }

    string _spTenant = "";
    public string SpTenant { get => _spTenant; set { if (SetProperty(ref _spTenant, value)) OnPropertyChanged(nameof(SpTenantValid)); } }
    string _spClient = "";
    public string SpClient { get => _spClient; set { if (SetProperty(ref _spClient, value)) OnPropertyChanged(nameof(SpClientValid)); } }
    string _spSecret = "";
    public string SpSecret { get => _spSecret; set => SetProperty(ref _spSecret, value); }
    AzureCloud _spCloud = AzureCloud.Global;
    public AzureCloud SpCloud { get => _spCloud; set => SetProperty(ref _spCloud, value); }
    bool _saveSecret = true;
    public bool SaveSecret { get => _saveSecret; set => SetProperty(ref _saveSecret, value); }

    public bool SpTenantValid => AzArgs.IsGuid(SpTenant.Trim());
    public bool SpClientValid => AzArgs.IsGuid(SpClient.Trim());

    public AsyncRelayCommand CliCommand { get; }
    public AsyncRelayCommand DeviceCommand { get; }
    public AsyncRelayCommand SpCommand { get; }
    public RelayCommand CancelDeviceCommand { get; }

    static string SecretTarget(string clientId) => "CasePilot/sp/" + clientId;

    /// <summary>页面出现时: 恢复已保存的应用注册; 注销后切换到指定方式; 否则静默尝试 CLI.</summary>
    public async Task InitAsync()
    {
        if (_initialized) return;
        _initialized = true;
        if (_settings.Get<SpInfo>("sp") is { } sp)
        {
            SpTenant = sp.Tenant;
            SpClient = sp.ClientId;
            SpCloud = AzureCloudExtensions.FromEnvironmentName(sp.Cloud);
            SpSecret = _secrets.Read(SecretTarget(sp.ClientId)) ?? "";
        }
        if (_shell.RequestedLogin is { } m) { Method = m; return; }
        if (!_shell.AutoLogin) return;
        await LoginCliAsync(silent: true);
    }

    public async Task LoginCliAsync(bool silent = false)
    {
        Busy = true;
        try
        {
            var id = await _auth.LoginCliAsync(CancellationToken.None);
            Error = null;
            await _shell.AfterLoginAsync(id);
        }
        catch (Exception e) { Error = (silent ? SilentCliHint : "") + Errors.Text(e); }
        finally { Busy = false; }
    }

    public async Task LoginSpAsync()
    {
        if (!SpTenantValid || !SpClientValid) { Error = "Tenant ID 与 Client ID 须为 GUID"; return; }
        Busy = true;
        try
        {
            var (tenant, client) = (SpTenant.Trim(), SpClient.Trim());
            var id = await _auth.LoginSpAsync(tenant, client, SpSecret, SpCloud, CancellationToken.None);
            _settings.Set("sp", new SpInfo(tenant, client, SpCloud.EnvironmentName()));
            _secrets.Write(SecretTarget(client), SaveSecret ? SpSecret : null);
            Error = null;
            await _shell.AfterLoginAsync(id);
        }
        catch (Exception e) { Error = Errors.Text(e); }
        finally { Busy = false; }
    }

    /// <summary>获取验证码 → 自动复制并打开验证页 → 轮询直到完成或取消.</summary>
    public async Task LoginDeviceAsync()
    {
        Busy = true;
        Error = null;
        _deviceCts?.Dispose();
        _deviceCts = new CancellationTokenSource();
        var ct = _deviceCts.Token;
        try
        {
            var code = await _auth.StartDeviceAsync(Cloud, ct);
            DeviceCode = code;
            _os.Copy(code.UserCode);
            _os.OpenUrl(code.VerificationUrl);
            var id = await _auth.PollDeviceAsync(code, ct);
            await _shell.AfterLoginAsync(id);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error = Errors.Text(e); }
        finally
        {
            DeviceCode = null;
            Busy = false;
        }
    }

    public void CancelDevice() => _deviceCts?.Cancel();
}
