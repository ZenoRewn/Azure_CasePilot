using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CasePilot.App.ViewModels;
using CasePilot.Core.Models;

namespace CasePilot.App.Views;

public partial class HistoryView : UserControl
{
    private readonly MainVM _main;

    public HistoryView(MainVM main)
    {
        _main = main;
        InitializeComponent();
        DataContext = main;
    }

    public void FocusSearch() => Dispatcher.BeginInvoke(() => { Search.Focus(); Search.SelectAll(); });

    void GridSized(object sender, SizeChangedEventArgs e)
    {
        var vis = HistoryLayout.ShowCreated(e.NewSize.Width) ? Visibility.Visible : Visibility.Collapsed;
        if (CreatedCol.Visibility == vis) return;
        // 在布局过程中切换列可见性会使 DataGrid 的星号列宽计算失真, 推迟到本轮布局之后再切换并重算列宽.
        Dispatcher.BeginInvoke(() =>
        {
            CreatedCol.Visibility = vis;
            foreach (var c in Grid.Columns) { var w = c.Width; c.Width = 0; c.Width = w; }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    TicketSummary? Selected => Grid.SelectedItem as TicketSummary;

    async void DaysChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Days.SelectedValue is int d && IsLoaded) await _main.History.SetDaysAsync(d);
    }

    async void OpenRow(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { } t) await _main.OpenDetailAsync(t);
    }

    async void GridKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Selected is not { } t) return;
        e.Handled = true;
        await _main.OpenDetailAsync(t);
    }

    void CopyNumber(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } t) return;
        _main.Deps.Os.Copy(t.TicketNumber);
        _ = _main.ShowToastAsync("已复制 Case 号");
    }

    void OpenPortal(object sender, RoutedEventArgs e)
    {
        if (Selected is { } t) _main.Deps.Os.OpenUrl(_main.Shell.PortalUrl(t.Id));
    }

    async void CloseCase(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } t) return;
        await _main.OpenDetailAsync(t);
        if (_main.Detail is { } d) await d.CloseAsync();
    }
}
