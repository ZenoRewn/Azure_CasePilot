using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using CasePilot.App.ViewModels;

namespace CasePilot.App.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is LoginVM old) old.PropertyChanged -= OnVm;
            if (e.NewValue is not LoginVM vm) return;
            vm.PropertyChanged += OnVm;
            Secret.Password = vm.SpSecret;
            Sync(vm);
        };
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && DataContext is LoginVM vm) await vm.InitAsync();
        };
    }

    void OnVm(object? s, PropertyChangedEventArgs e)
    {
        var vm = (LoginVM)s!;
        if (e.PropertyName == nameof(LoginVM.SpSecret) && Secret.Password != vm.SpSecret) Secret.Password = vm.SpSecret;
        Sync(vm);
    }

    void Sync(LoginVM vm)
    {
        CliPanel.Visibility = vm.Method == LoginMethod.Cli ? Visibility.Visible : Visibility.Collapsed;
        DevicePanel.Visibility = vm.Method == LoginMethod.Device ? Visibility.Visible : Visibility.Collapsed;
        SpPanel.Visibility = vm.Method == LoginMethod.Sp ? Visibility.Visible : Visibility.Collapsed;
        // 只在用户输入了内容后才提示格式错误
        TenantErr.Visibility = vm.SpTenant.Length > 0 && !vm.SpTenantValid ? Visibility.Visible : Visibility.Collapsed;
        ClientErr.Visibility = vm.SpClient.Length > 0 && !vm.SpClientValid ? Visibility.Visible : Visibility.Collapsed;
        ErrorBar.ActionText = vm.Method == LoginMethod.Cli ? "重试" : null;
    }

    void Secret_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginVM vm) vm.SpSecret = Secret.Password;
    }

    void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
