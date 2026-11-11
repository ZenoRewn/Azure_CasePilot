using System.Windows;
using System.Windows.Controls;
using CasePilot.App.ViewModels;

namespace CasePilot.App.Views;

public partial class AISettingsView : UserControl
{
    public AISettingsView() => InitializeComponent();

    AISettingsVM Vm => (AISettingsVM)DataContext;

    void ChatKeyChanged(object sender, RoutedEventArgs e) => Vm.ChatKeyInput = ChatKey.Password;
    void SttKeyChanged(object sender, RoutedEventArgs e) => Vm.SttKeyInput = SttKey.Password;

    void SaveChatKey(object sender, RoutedEventArgs e) { Vm.SaveChatKey(); ChatKey.Clear(); }
    void SaveSttKey(object sender, RoutedEventArgs e) { Vm.SaveSttKey(); SttKey.Clear(); }

    void Reset(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(Window.GetWindow(this), "恢复默认设置?", "将清除已保存的 Key, 并恢复默认 Endpoint 与 Deployment。", "恢复默认")) return;
        Vm.Reset();
        ChatKey.Clear();
        SttKey.Clear();
    }
}
