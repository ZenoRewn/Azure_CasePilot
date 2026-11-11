using System.Windows.Controls;

namespace CasePilot.App.Views;

public partial class McpView : UserControl
{
    public McpView() => InitializeComponent();

    void Copied(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this)?.DataContext is ViewModels.MainVM m) _ = m.ShowToastAsync("已复制 MCP 配置");
    }
}
