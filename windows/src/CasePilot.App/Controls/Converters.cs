using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CasePilot.App.ViewModels;
using CasePilot.Core.AI;
using CasePilot.Core.Models;

namespace CasePilot.App.Controls;

/// <summary>bool / null / 空字符串 / 非零 → Visible; Parameter="!" 取反.</summary>
public sealed class VisConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var on = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            System.Collections.ICollection col => col.Count > 0,
            _ => true,
        };
        if (p as string == "!") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

/// <summary>枚举 ↔ 序号 (分段控件 SelectedIndex).</summary>
public sealed class EnumIndexConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is Enum e ? System.Convert.ToInt32(e, c) : -1;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is int i && i >= 0 ? Enum.ToObject(Nullable.GetUnderlyingType(t) ?? t, i) : Binding.DoNothing;
}

/// <summary>枚举 == 参数 (导航 RadioButton IsChecked).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is true && p is string s ? Enum.Parse(t, s) : Binding.DoNothing;
}

/// <summary>显示文本: Mode = status | statusKind | sev | sevKind | rel | full | sub | level | sevTitle | cloud | stt.</summary>
public sealed class DisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c) => (p as string, value) switch
    {
        ("status", string s) => Display.StatusLabel(s),
        ("statusKind", string s) => Display.StatusKind(s),
        ("sev", string s) => Display.SeverityLabel(s),
        ("sevKind", string s) => Display.SeverityKind(s),
        ("rel", DateTimeOffset d) => Display.Relative(d, DateTimeOffset.Now),
        ("full", DateTimeOffset d) => Display.Full(d),
        ("sub", TicketSummary s) => Display.Subtitle(s),
        ("a11y", TicketSummary s) => Display.RowName(s, DateTimeOffset.Now),
        ("level", Severity s) => s.Level(),
        ("sevTitle", Severity s) => s.Title(),
        ("cloud", AzureCloud a) => a.Label(),
        ("stt", SttProvider s) => s.Label(),
        _ => value is null ? "" : value,
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
