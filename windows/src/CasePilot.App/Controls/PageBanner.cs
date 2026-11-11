using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CasePilot.App.Controls;

/// <summary>
/// 页面级错误 / 成功提示. 放在各页面标题之下 (而非 Shell 顶部), 使标题位置在有无错误时保持一致.
/// 绑定到窗口的 MainVM (Shell.Banner / Toast), 与页面自身 DataContext 无关.
/// </summary>
public sealed class PageBanner : StackPanel
{
    public PageBanner()
    {
        var error = new InfoBar { Severity = InfoBarSeverity.Error, ActionText = "重试", SecondaryText = "复制" };
        Bind(error, InfoBar.MessageProperty, "Shell.Banner");
        Bind(error, InfoBar.ActionCommandProperty, "RetryCommand");
        Bind(error, InfoBar.SecondaryCommandProperty, "CopyBannerCommand");
        Bind(error, InfoBar.CloseCommandProperty, "DismissBannerCommand");
        var toast = new InfoBar { Severity = InfoBarSeverity.Success };
        Bind(toast, InfoBar.MessageProperty, "Toast");
        Children.Add(error);
        Children.Add(toast);
    }

    static void Bind(FrameworkElement e, DependencyProperty p, string path) =>
        e.SetBinding(p, new Binding("DataContext." + path) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1) });
}
