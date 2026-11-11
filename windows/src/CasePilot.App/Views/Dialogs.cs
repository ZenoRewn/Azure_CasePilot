using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CasePilot.App.Controls;
using CasePilot.App.ViewModels;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Models;
using CasePilot.Core.Picking;
using CasePilot.Core.Support;

namespace CasePilot.App.Views;

/// <summary>模态对话框 (Owned Window, 替代 ContentDialog). Esc 关闭; 确认框默认按钮为取消.</summary>
public sealed class Dialogs(Func<Window?> owner) : IDialogs
{
    public Task<bool> ConfirmAsync(string title, string message, string primary) =>
        Task.FromResult(Confirm(owner(), title, message, primary));

    static Window Make(Window? owner, string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        w.SetResourceReference(Window.FontFamilyProperty, "UiFont");
        w.SetResourceReference(Window.BackgroundProperty, "CpCardBg");
        w.FontSize = 14;
        w.InputBindings.Add(new KeyBinding(new Mvvm.RelayCommand(w.Close), Key.Escape, ModifierKeys.None));
        return w;
    }

    static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var b in buttons) { b.Margin = new Thickness(8, 0, 0, 0); b.MinWidth = 96; p.Children.Add(b); }
        return p;
    }

    public static bool Confirm(Window? owner, string title, string message, string primary)
    {
        var w = Make(owner, title, 460);
        var ok = new Button { Content = primary };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        var cancel = new Button { Content = "取消", IsDefault = true, IsCancel = true };
        var result = false;
        ok.Click += (_, _) => { result = true; w.Close(); };
        var head = new TextBlock { Text = title };
        head.SetResourceReference(FrameworkElement.StyleProperty, "SectionTitle");
        var body = new TextBlock { Text = message };
        body.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        w.Content = new StackPanel { Margin = new Thickness(24), Children = { head, body, Buttons(ok, cancel) } };
        w.Loaded += (_, _) => cancel.Focus();
        w.ShowDialog();
        return result;
    }

    /// <summary>等宽只读文本 + 复制 (请求体预览).</summary>
    public static void ShowText(Window? owner, string title, string text, IShell os)
    {
        var w = Make(owner, title, 720);
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13,
        };
        box.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        var copy = new Button { Content = "复制" };
        copy.Click += (_, _) => os.Copy(text);
        var close = new Button { Content = "关闭", IsCancel = true, IsDefault = true };
        close.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        close.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new Thickness(24), Children = { box, Buttons(copy, close) } };
        w.ShowDialog();
    }
}

/// <summary>从订阅资源选择: 搜索资源, 每个资源列出最相关的支持服务 chip.</summary>
public static class ResourcePicker
{
    public sealed record Pick(SupportService Service, string ResourceId);

    public static async Task<Pick?> ShowAsync(Window? owner, ISupportApi api, string tenant, string sub, IReadOnlyList<SupportService> services)
    {
        var w = new Window
        {
            Title = "从订阅资源中选择", Owner = owner, Width = 640, Height = 560, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize, FontSize = 14,
        };
        w.SetResourceReference(Window.FontFamilyProperty, "UiFont");
        w.SetResourceReference(Window.BackgroundProperty, "CpCardBg");
        w.InputBindings.Add(new KeyBinding(new Mvvm.RelayCommand(w.Close), Key.Escape, ModifierKeys.None));
        Pick? result = null;

        var hint = new TextBlock { Text = "选择出问题的资源, 再选择对应的支持服务 (最相关的排在前面)。", Margin = new Thickness(0, 0, 0, 12) };
        hint.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        Hint.SetText(search, "搜索名称 / 类型 / 资源组");
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索资源");
        var list = new StackPanel();
        var status = new TextBlock { Text = "资源加载中…" };
        status.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var cancel = new Button { Content = "取消", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 96, Margin = new Thickness(0, 12, 0, 0) };
        var root = new DockPanel { Margin = new Thickness(24) };
        foreach (var (e, d) in new (UIElement, Dock)[] { (hint, Dock.Top), (search, Dock.Top), (status, Dock.Top), (cancel, Dock.Bottom) })
        {
            DockPanel.SetDock(e, d);
            root.Children.Add(e);
        }
        root.Children.Add(scroll);
        w.Content = root;

        IReadOnlyList<AzureResource> all = [];
        void Render()
        {
            list.Children.Clear();
            var terms = search.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var r in all.Where(r => terms.All((r.Name + " " + r.Type + " " + r.Id).ToLowerInvariant().Contains)).Take(100))
            {
                var name = new TextBlock { Text = r.Name, FontWeight = FontWeights.SemiBold };
                var meta = new TextBlock { Text = $"{r.Type} · {r.Location}", Margin = new Thickness(0, 2, 0, 6) };
                meta.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
                var chips = new WrapPanel();
                var ranked = ResourceRanker.Rank(services, r);
                foreach (var s in ranked.Take(4))
                {
                    var b = new Button { Content = s.DisplayName };
                    b.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
                    b.Click += (_, _) => { result = new Pick(s, r.Id); w.Close(); };
                    chips.Children.Add(b);
                }
                if (ranked.Count == 0)
                {
                    var none = new TextBlock { Text = "无对应服务" };
                    none.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
                    chips.Children.Add(none);
                }
                var item = new Border { Padding = new Thickness(0, 10, 0, 6), BorderThickness = new Thickness(0, 0, 0, 1), Child = new StackPanel { Children = { name, meta, chips } } };
                item.SetResourceReference(Border.BorderBrushProperty, "CpDivider");
                list.Children.Add(item);
            }
        }
        search.TextChanged += (_, _) => Render();
        w.Loaded += async (_, _) =>
        {
            search.Focus();
            try
            {
                all = await api.ResourcesAsync(tenant, sub, null, CancellationToken.None);
                status.Text = $"{all.Count} 个资源";
            }
            catch (Exception e) { status.Text = e is AppError a ? a.Display : e.Message; }
            Render();
        };
        w.ShowDialog();
        await Task.CompletedTask;
        return result;
    }
}
