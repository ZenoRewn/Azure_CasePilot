using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CasePilot.App.Controls;
using CasePilot.App.ViewModels;
using CasePilot.Core.Models;

using Page = CasePilot.App.ViewModels.Page;

namespace CasePilot.App.Views;

public partial class CreateView : UserControl
{
    private readonly MainVM _main;
    private CreateCaseVM Vm => _main.Create;

    public CreateView(MainVM main)
    {
        _main = main;
        InitializeComponent();
        DataContext = main;
        SvcSearch.Picked += async s => await Vm.PickAsync(s);
        Vm.PropertyChanged += OnVm;
        Sync();
    }

    void OnVm(object? s, PropertyChangedEventArgs e) => Sync();

    void Sync()
    {
        var vm = Vm;
        AiInfo.Severity = vm.AiStatusOk ? InfoBarSeverity.Success : vm.AiShowSettings ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        AiInfo.ActionText = vm.AiShowSettings ? "打开 AI 设置" : null;
        AiInfo.ActionCommand = _main.OpenAISettingsCommand;
        ResultBar.Severity = vm.ResultOk ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ResultBar.ActionText = vm.ResultOk ? "复制" : null;
        ResultBar.ActionCommand = vm.CopyCreatedCommand;
        ResultBar.SecondaryText = vm.ResultOk ? "在门户中打开" : null;
        ResultBar.SecondaryCommand = vm.OpenCreatedCommand;
        ContactCard.SetResourceReference(Border.BorderBrushProperty, _main.Shell.ContactValid ? "CpCardStroke" : "CpCritical");
        CheckIcon.Text = vm.Missing.Count == 0 ? "\uE73E" : "\uEA39";
        // 与 Mac 一致: 缺信息红色感叹号, 可提交蓝色对勾; 悬停显示完整缺失清单
        var color = vm.Missing.Count == 0 ? "CpAccent" : "CpCritical";
        CheckIcon.SetResourceReference(TextBlock.ForegroundProperty, color);
        MissingLabel.SetResourceReference(TextBlock.ForegroundProperty, color);
        StatusPanel.ToolTip = vm.Missing.Count == 0 ? "信息已完整, 可以提交" : "还需填写:\n" + string.Join("\n", vm.Missing.Select(m => "· " + m));
        if (!vm.UseStart && vm.StartTime is null && StartDate.SelectedDate is not null) StartDate.SelectedDate = null;
    }

    async void QuickPick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is QuickService q) await Vm.PickAsync(q.Service);
    }

    async void ReusePick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RecentProblem r) await Vm.ReuseAsync(r);
    }

    void OpenAISettings(object sender, RoutedEventArgs e) => _main.OpenAISettingsCommand.Execute(null);

    void EditContact(object sender, RoutedEventArgs e) => _main.Shell.CurrentPage = Page.Contacts;

    void DigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    void StartChanged(object? sender, EventArgs e)
    {
        if (StartDate.SelectedDate is not { } d) { Vm.StartTime = null; return; }
        var ok = TimeSpan.TryParseExact(StartTime.Text.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var t);
        StartTime.SetResourceReference(Control.BorderBrushProperty, ok ? "CpCardStroke" : "CpCritical");
        if (!ok) return;
        var local = d.Date + t;
        Vm.StartTime = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    void Preview(object sender, RoutedEventArgs e) =>
        Dialogs.ShowText(Window.GetWindow(this), "请求体预览", Vm.PreviewJson(), _main.Deps.Os);

    async void PickFromResource(object sender, RoutedEventArgs e)
    {
        if (_main.Shell.TenantId is not { } t || _main.Shell.Sub is not { } sub)
        {
            await _main.ShowToastAsync("请先选择订阅");
            return;
        }
        var pick = await ResourcePicker.ShowAsync(Window.GetWindow(this), _main.Deps.Api, t, sub, _main.Shell.Services);
        if (pick is { } p) await Vm.PickAsync(p.Service, p.ResourceId);
    }
}
