using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CasePilot.App.ViewModels;
using CasePilot.Core.Models;

namespace CasePilot.App.Controls;

/// <summary>麦克风按钮: 空闲 E720 → 录音 (红色 + 计时) → 转写中. 结果插入到 Target 光标处.</summary>
public sealed class MicButton : Button
{
    /// <summary>组合根设置: 由插入回调创建 MicVM. 未设置 (设计 / 快照) 时按钮禁用.</summary>
    public static Func<Action<string>, MicVM>? Factory { get; set; }

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(TextBox), typeof(MicButton));

    public TextBox? Target { get => (TextBox?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    private readonly TextBlock _glyph = new() { Text = char.ConvertFromUtf32(0xE720), FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { FontSize = 12, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Popup _popup = new() { StaysOpen = false, Placement = PlacementMode.Bottom, AllowsTransparency = true };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private MicVM? _vm;

    public MicButton()
    {
        SetResourceReference(StyleProperty, "SubtleButton");
        _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "CpTextSecondary");
        _time.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _time.SetResourceReference(TextBlock.ForegroundProperty, "CpCritical");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(_glyph);
        sp.Children.Add(_time);
        Content = sp;
        VerticalAlignment = VerticalAlignment.Top;
        Focusable = true;
        AutomationProperties.SetName(this, "语音输入");
        ToolTip = "语音输入";
        _popup.PlacementTarget = this;
        _timer.Tick += (_, _) => _vm?.Tick(DateTimeOffset.Now);
        Loaded += (_, _) =>
        {
            if (_vm is not null) return;
            if (Factory is null) { IsEnabled = false; return; }
            _vm = Factory(Insert);
            _vm.PropertyChanged += OnVm;
            Command = _vm.ToggleCommand;
        };
    }

    void Insert(string text)
    {
        if (Target is not { } tb) return;
        var (t, caret) = MicVM.InsertAt(tb.Text, tb.CaretIndex, text);
        tb.Text = t;
        tb.CaretIndex = caret;
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        tb.Focus();
    }

    void OnVm(object? s, PropertyChangedEventArgs e)
    {
        var vm = _vm!;
        switch (e.PropertyName)
        {
            case nameof(MicVM.State):
                var rec = vm.State == MicState.Recording;
                _glyph.Text = vm.State switch { MicState.Recording => char.ConvertFromUtf32(0xE71A), MicState.Transcribing => char.ConvertFromUtf32(0xE895), _ => char.ConvertFromUtf32(0xE720) };
                _glyph.SetResourceReference(TextBlock.ForegroundProperty, rec ? "CpCritical" : "CpTextSecondary");
                _time.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
                ToolTip = vm.Tooltip;
                AutomationProperties.SetName(this, vm.Tooltip);
                if (rec) _timer.Start(); else _timer.Stop();
                break;
            case nameof(MicVM.Elapsed):
                _time.Text = vm.Elapsed;
                break;
            case nameof(MicVM.Error):
                if (vm.Error is { } err) ShowError(err, vm);
                else _popup.IsOpen = false;
                break;
        }
    }

    void ShowError(string err, MicVM vm)
    {
        var bar = new InfoBar
        {
            Message = err,
            Severity = InfoBarSeverity.Warning,
            Width = 340,
            Margin = new Thickness(0),
            ActionText = vm.PermissionHint ? "打开麦克风隐私设置" : null,
            ActionCommand = vm.OpenPrivacyCommand,
            CloseCommand = vm.DismissCommand,
        };
        bar.SetResourceReference(BackgroundProperty, "CpCautionBg");
        _popup.Child = new Border { Child = bar, Background = Brushes.Transparent, Padding = new Thickness(0, 4, 0, 0) };
        _popup.IsOpen = true;
    }
}

/// <summary>服务搜索框: TextBox + Popup + ListBox, 支持 ↑/↓/Enter/Esc.</summary>
public sealed class ServiceSearchBox : Grid
{
    public static readonly DependencyProperty ServicesProperty = DependencyProperty.Register(
        nameof(Services), typeof(IReadOnlyList<SupportService>), typeof(ServiceSearchBox));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(SupportService), typeof(ServiceSearchBox),
        new PropertyMetadata(null, (d, e) => ((ServiceSearchBox)d).ShowSelected()));

    public IReadOnlyList<SupportService>? Services { get => (IReadOnlyList<SupportService>?)GetValue(ServicesProperty); set => SetValue(ServicesProperty, value); }
    public SupportService? Selected { get => (SupportService?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    public event Action<SupportService>? Picked;

    private readonly TextBox _box = new() { FontSize = 14 };
    private readonly ListBox _list = new() { MaxHeight = 320, BorderThickness = new Thickness(0), FontSize = 14 };
    private readonly Popup _popup = new() { StaysOpen = false, Placement = PlacementMode.Bottom, AllowsTransparency = true };
    private bool _suppress;

    public ServiceSearchBox()
    {
        Hint.SetText(_box, "搜索服务, 如 OpenAI、虚拟机、AKS");
        AutomationProperties.SetName(_box, "搜索服务");
        _list.DisplayMemberPath = nameof(SupportService.DisplayName);
        var border = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(4), Child = _list, Margin = new Thickness(0, 4, 8, 8) };
        border.SetResourceReference(Border.BackgroundProperty, "CpCardBg");
        border.SetResourceReference(Border.BorderBrushProperty, "CpCardStroke");
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.14, Direction = 270 };
        _popup.Child = border;
        _popup.PlacementTarget = _box;
        Children.Add(_box);
        Children.Add(_popup);
        _box.TextChanged += (_, _) => { if (!_suppress) Search(); };
        _box.PreviewKeyDown += OnKey;
        _box.GotKeyboardFocus += (_, _) => _box.SelectAll();
        _list.PreviewMouseLeftButtonUp += (_, _) => Commit();
        SizeChanged += (_, _) => border.Width = ActualWidth;
    }

    void Search()
    {
        var hits = _box.Text.Trim().Length == 0 ? [] : CasePilot.Core.Picking.ServiceSearch.Filter(_box.Text, Services ?? []).Take(12).ToList();
        _list.ItemsSource = hits;
        _list.SelectedIndex = hits.Count > 0 ? 0 : -1;
        _popup.IsOpen = hits.Count > 0 && _box.IsKeyboardFocusWithin;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen) { if (e.Key == Key.Down) { Search(); e.Handled = true; } return; }
        var n = _list.Items.Count;
        switch (e.Key)
        {
            case Key.Down: _list.SelectedIndex = (_list.SelectedIndex + 1) % n; break;
            case Key.Up: _list.SelectedIndex = (_list.SelectedIndex - 1 + n) % n; break;
            case Key.Enter: Commit(); break;
            case Key.Escape: _popup.IsOpen = false; break;
            default: return;
        }
        _list.ScrollIntoView(_list.SelectedItem);
        e.Handled = true;
    }

    void Commit()
    {
        if (_list.SelectedItem is not SupportService s) return;
        _popup.IsOpen = false;
        Picked?.Invoke(s);
    }

    void ShowSelected()
    {
        _suppress = true;
        _box.Text = Selected?.DisplayName ?? "";
        _suppress = false;
    }
}
