using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CasePilot.App.ViewModels;
using CasePilot.App.Views;
using CasePilot.Core.Abstractions;

using Page = CasePilot.App.ViewModels.Page;

namespace CasePilot.App;

public partial class MainWindow : Window
{
    public const double DetailDefault = 420, DetailMin = 360, DetailMax = 640;

    private readonly MainVM _vm;
    private readonly ISettingsStore? _settings;
    private readonly Dictionary<Page, FrameworkElement> _pages = [];

    public sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized, double Detail);

    public MainWindow(MainVM vm, ISettingsStore? settings = null)
    {
        _vm = vm;
        _settings = settings;
        InitializeComponent();
        DataContext = vm;
        vm.Shell.PropertyChanged += OnShell;
        vm.PropertyChanged += OnMain;
        ShowPage();
        RestorePlacement();
        Closing += (_, _) => SavePlacement();
        // Ctrl+F 交给我的 Case 搜索框; Ctrl+Enter 由各页面处理
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                vm.Shell.CurrentPage = Page.History;
                (PageView(Page.History) as HistoryView)?.FocusSearch();
                e.Handled = true;
            }
        };
    }

    void OnShell(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellVM.CurrentPage)) ShowPage();
    }

    void OnMain(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainVM.IsDetailOpen)) return;
        var open = _vm.IsDetailOpen;
        DetailCol.MinWidth = open ? DetailMin : 0;
        DetailCol.Width = open ? new GridLength(_detailWidth) : new GridLength(0);
        Splitter.Visibility = DetailHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (!open && DetailCol.ActualWidth > 0) _detailWidth = Math.Clamp(DetailCol.ActualWidth, DetailMin, DetailMax);
    }

    double _detailWidth = DetailDefault;

    public FrameworkElement PageView(Page p)
    {
        if (_pages.TryGetValue(p, out var el)) return el;
        el = p switch
        {
            ViewModels.Page.Create => new CreateView(_vm),
            ViewModels.Page.History => new HistoryView(_vm),
            ViewModels.Page.AISettings => new AISettingsView { DataContext = _vm.AISettings },
            ViewModels.Page.Contacts => new ContactView { DataContext = _vm.Contact },
            _ => new McpView { DataContext = _vm.Mcp },
        };
        _pages[p] = el;
        return el;
    }

    void ShowPage() => PageHost.Content = PageView(_vm.Shell.CurrentPage);

    // ---- 身份卡菜单: 切换租户 ▸ 切换订阅 ▸ 认证方式 ▸ 注销 ----

    void IdentityCard_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = IdentityCard, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var tenants = new MenuItem { Header = "切换租户" };
        foreach (var t in _vm.Shell.Tenants)
        {
            var id = t.Id;
            tenants.Items.Add(new MenuItem { Header = t.Name, IsCheckable = true, IsChecked = id == _vm.Shell.TenantId, Command = new Mvvm.RelayCommand(() => _vm.SelectedTenant = id) });
        }
        var subs = new MenuItem { Header = "切换订阅" };
        foreach (var s in _vm.SubscriptionOptions)
        {
            var id = s.Id;
            subs.Items.Add(new MenuItem { Header = s.Name, IsCheckable = true, IsChecked = id == _vm.Shell.SubscriptionId, Command = new Mvvm.RelayCommand(() => _vm.SelectedSubscription = id) });
        }
        var auth = new MenuItem { Header = "认证方式" };
        foreach (var (m, label) in new[] { (LoginMethod.Cli, "Azure CLI"), (LoginMethod.Device, "浏览器登录"), (LoginMethod.Sp, "应用注册") })
            auth.Items.Add(new MenuItem { Header = label, Command = _vm.LogoutCommand, CommandParameter = (LoginMethod?)m });
        menu.Items.Add(tenants);
        menu.Items.Add(subs);
        menu.Items.Add(auth);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "注销", Command = _vm.LogoutCommand });
        menu.IsOpen = true;
    }

    // ---- 窗口位置持久化 ----

    void RestorePlacement()
    {
        if (_settings?.Get<Placement>("window") is not { } p) return;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.IntersectsWith(new Rect(p.Left, p.Top, p.Width, p.Height))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (p.Left, p.Top, Math.Max(MinWidth, p.Width), Math.Max(MinHeight, p.Height));
        if (p.Maximized) WindowState = WindowState.Maximized;
        _detailWidth = Math.Clamp(p.Detail, DetailMin, DetailMax);
    }

    void SavePlacement()
    {
        if (_vm.IsDetailOpen) _detailWidth = Math.Clamp(DetailCol.ActualWidth, DetailMin, DetailMax);
        var r = RestoreBounds;
        // 关闭时保存位置为尽力而为: 设置文件被锁定时不阻止退出.
        try { _settings?.Set("window", new Placement(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized, _detailWidth)); }
        catch (CasePilot.Core.Models.AppError) { }
    }
}
