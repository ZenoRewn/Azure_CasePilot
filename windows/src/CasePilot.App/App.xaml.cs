using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using CasePilot.App.Controls;
using CasePilot.App.Mvvm;
using CasePilot.App.ViewModels;
using CasePilot.App.Views;
using CasePilot.Core.AI;
using CasePilot.Core.Auth;
using CasePilot.Core.Support;
using CasePilot.Platform;

namespace CasePilot.App;

/// <summary>组合根. 开关: --selftest (只读自检, 打印结果, 退出码) 与 --snapshot &lt;dir&gt; (用假数据渲染各页面 PNG).</summary>
public partial class App : Application
{
    const string InstanceMutex = @"Local\CasePilot.SingleInstance";
    Mutex? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        HookGlobalErrors();
        base.OnStartup(e);
        try { await StartAsync(e.Args); }
        catch (Exception ex)
        {
            CrashLog.Append(ex, "Startup");
            MessageBox.Show($"CasePilot 启动失败:\n{ErrorText(ex)}\n\n日志: {CrashLog.DefaultPath}", "CasePilot",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>全局兜底: 记录到 crash.log 并提示; 界面线程上的非致命异常不终止进程.</summary>
    void HookGlobalErrors()
    {
        DispatcherUnhandledException += (_, a) =>
        {
            CrashLog.Append(a.Exception, "Dispatcher");
            if (a.Exception is OutOfMemoryException or StackOverflowException or AccessViolationException) return;
            a.Handled = true;
            ShowError(a.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, a) => { CrashLog.Append(a.Exception, "Task"); a.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            if (a.ExceptionObject is Exception ex) CrashLog.Append(ex, "AppDomain");
        };
        AsyncRelayCommand.Unobserved = ex => { CrashLog.Append(ex, "Command"); ShowError(ex); };
    }

    static string ErrorText(Exception e) => e is CasePilot.Core.Models.AppError a ? a.Display : e.Message;

    void ShowError(Exception e) =>
        MessageBox.Show(MainWindow is { IsVisible: true } ? MainWindow : null!, ErrorText(e), "CasePilot", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>同一用户会话只运行一个实例 (避免多个进程互相覆盖 settings.json); 已有实例时将其窗口置前.</summary>
    bool AcquireSingleInstance()
    {
        _instance = new Mutex(true, InstanceMutex, out var created);
        if (created) return true;
        _instance.Dispose();
        _instance = null;
        using var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9 /* SW_RESTORE */);
                SetForegroundWindow(p.MainWindowHandle);
                break;
            }
        }
        return false;
    }

    async Task StartAsync(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            AttachParentConsole();
            var (api, auth) = Live(out _);
            var code = await SelfTest.RunAsync(api, auth, Console.Out);
            Console.Out.Flush();
            Shutdown(code);
            return;
        }
        var snap = Array.IndexOf(args, "--snapshot");
        if (snap >= 0)
        {
            AttachParentConsole();
            var dir = snap + 1 < args.Length ? args[snap + 1] : "snapshots";
            var code = await Snapshot.RunAsync(Path.GetFullPath(dir), Console.Out);
            Console.Out.Flush();
            Shutdown(code);
            return;
        }

        if (!AcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        var (realApi, realAuth) = Live(out var http);
        var settings = new JsonSettingsStore();
        var secrets = new CredentialStore();
        var delay = new SystemDelay();
        var ai = new AIClient(http, realAuth, delay, () => AISettingsVM.Load(settings), secrets);
        var os = new ShellOps();
        Window? main = null;
        var deps = new AppDeps(realApi, realAuth, settings, secrets, os, new Dialogs(() => main), delay, ai,
            new WaveRecorder(), ai, McpSettingsVM.DefaultPython(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)), FindRepo());
        var vm = new MainVM(deps);
        MicButton.Factory = vm.NewMic;
        main = new MainWindow(vm, settings);
        MainWindow = main;
        main.Show();
    }

    static (ISupportApi, ILoginService) Live(out HttpClient http)
    {
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var clock = new SystemClock();
        var delay = new SystemDelay();
        var auth = new AuthProvider(http, new AzCli(), clock, delay);
        return (new SupportClient(http, auth, delay, new GuidIds(), clock), auth);
    }

    /// <summary>MCP 服务端所在仓库: 从程序目录向上查找 casepilot/mcp.py.</summary>
    static string FindRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "casepilot", "mcp.py"))) return d.FullName;
        return AppContext.BaseDirectory.TrimEnd('\\');
    }

    static void AttachParentConsole()
    {
        if (!Console.IsOutputRedirected && !AttachConsole(-1)) return;
        var w = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(w);
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int pid);
}
