using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CasePilot.App.Controls;

public enum InfoBarSeverity { Informational, Success, Warning, Error }

/// <summary>InfoBar: 图标 + 文字 (Request ID 等宽显示) + 可选操作按钮 + 关闭. Message 为空时折叠.</summary>
public sealed class InfoBar : Border
{
    public static readonly DependencyProperty MessageProperty = Dp(nameof(Message), typeof(string), null);
    public static readonly DependencyProperty SeverityProperty = Dp(nameof(Severity), typeof(InfoBarSeverity), InfoBarSeverity.Informational);
    public static readonly DependencyProperty ActionTextProperty = Dp(nameof(ActionText), typeof(string), null);
    public static readonly DependencyProperty ActionCommandProperty = Dp(nameof(ActionCommand), typeof(ICommand), null);
    public static readonly DependencyProperty SecondaryTextProperty = Dp(nameof(SecondaryText), typeof(string), null);
    public static readonly DependencyProperty SecondaryCommandProperty = Dp(nameof(SecondaryCommand), typeof(ICommand), null);
    public static readonly DependencyProperty CloseCommandProperty = Dp(nameof(CloseCommand), typeof(ICommand), null);

    static DependencyProperty Dp(string name, Type t, object? def) =>
        DependencyProperty.Register(name, t, typeof(InfoBar), new PropertyMetadata(def, (d, _) => ((InfoBar)d).Update()));

    private readonly TextBlock _icon = new() { FontSize = 16, Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _text = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _rid = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _action = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _secondary = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close = new() { Margin = new Thickness(4, 0, -8, 0), VerticalAlignment = VerticalAlignment.Center, Content = G(0xE711) };

    static string G(int code) => char.ConvertFromUtf32(code);

    public InfoBar()
    {
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14, 10, 14, 10);
        Margin = new Thickness(0, 0, 0, 12);
        _icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _rid.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _rid.SetResourceReference(TextBlock.ForegroundProperty, "CpTextSecondary");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "CpTextPrimary");
        foreach (var b in new[] { _action, _secondary, _close })
            b.SetResourceReference(StyleProperty, "SubtleButton");
        _action.SetResourceReference(BackgroundProperty, "CpSubtleFill");
        _secondary.SetResourceReference(BackgroundProperty, "CpSubtleFill");
        _close.SetResourceReference(Control.FontFamilyProperty, "IconFont");
        _close.FontSize = 12;
        AutomationProperties.SetName(_close, "关闭提示");
        _text.SetValue(TextBlock.IsHitTestVisibleProperty, true);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(_text);
        stack.Children.Add(_rid);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(stack, 1);
        Grid.SetColumn(_action, 2);
        Grid.SetColumn(_secondary, 3);
        Grid.SetColumn(_close, 4);
        foreach (UIElement e in new UIElement[] { _icon, stack, _action, _secondary, _close }) g.Children.Add(e);
        Child = g;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
        Update();
    }

    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public InfoBarSeverity Severity { get => (InfoBarSeverity)GetValue(SeverityProperty); set => SetValue(SeverityProperty, value); }
    public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
    public string? SecondaryText { get => (string?)GetValue(SecondaryTextProperty); set => SetValue(SecondaryTextProperty, value); }
    public ICommand? SecondaryCommand { get => (ICommand?)GetValue(SecondaryCommandProperty); set => SetValue(SecondaryCommandProperty, value); }
    public ICommand? CloseCommand { get => (ICommand?)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    void Update()
    {
        var msg = Message ?? "";
        Visibility = msg.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        const string marker = "Request ID: ";
        var i = msg.IndexOf(marker, StringComparison.Ordinal);
        _text.Text = (i < 0 ? msg : msg[..i]).TrimEnd();
        _rid.Text = i < 0 ? "" : msg[i..];
        _rid.Visibility = i < 0 ? Visibility.Collapsed : Visibility.Visible;
        var (glyph, fg, bg) = Severity switch
        {
            InfoBarSeverity.Success => (G(0xE930), "CpSuccess", "CpSuccessBg"),
            InfoBarSeverity.Warning => (G(0xE7BA), "CpCaution", "CpCautionBg"),
            InfoBarSeverity.Error => (G(0xEA39), "CpCritical", "CpCriticalBg"),
            _ => (G(0xE946), "CpAccent", "CpInfoBg"),
        };
        _icon.Text = glyph;
        _icon.SetResourceReference(TextBlock.ForegroundProperty, fg);
        SetResourceReference(BackgroundProperty, bg);
        SetResourceReference(BorderBrushProperty, "CpCardStroke");
        _action.Content = ActionText;
        _action.Command = ActionCommand;
        _action.Visibility = string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
        _secondary.Content = SecondaryText;
        _secondary.Command = SecondaryCommand;
        _secondary.Visibility = string.IsNullOrEmpty(SecondaryText) ? Visibility.Collapsed : Visibility.Visible;
        _close.Command = CloseCommand;
        _close.Visibility = CloseCommand is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(this, msg);
        if (msg.Length > 0 && UIElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
