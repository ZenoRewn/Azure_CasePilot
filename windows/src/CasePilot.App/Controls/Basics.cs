using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CasePilot.App.ViewModels;

namespace CasePilot.App.Controls;

/// <summary>分段控件: 横排的单选 ListBox, 样式见 Themes/Styles.xaml.</summary>
public sealed class SegmentedControl : ListBox
{
    static SegmentedControl() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SegmentedControl), new FrameworkPropertyMetadata(typeof(SegmentedControl)));

    public SegmentedControl() => SelectionMode = SelectionMode.Single;
}

/// <summary>状态 / 严重性胶囊: 22 px 高, 始终带文字.</summary>
public sealed class Pill : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Pill), new PropertyMetadata("", (d, _) => ((Pill)d).Update()));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(PillKind), typeof(Pill), new PropertyMetadata(PillKind.Neutral, (d, _) => ((Pill)d).Update()));
    /// <summary>读屏前缀 (如 "严重性"), 使列表单元格读作 "严重性 A" 而不只是 "A".</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Pill), new PropertyMetadata(null, (d, _) => ((Pill)d).Update()));

    private readonly TextBlock _text = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    public Pill()
    {
        Height = 22;
        MinWidth = 22;
        CornerRadius = new CornerRadius(11);
        Padding = new Thickness(8, 0, 8, 0);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Child = _text;
        Update();
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public PillKind Kind { get => (PillKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public static string AccessibleName(string? label, string text) => string.IsNullOrEmpty(label) ? text : $"{label} {text}";

    void Update()
    {
        _text.Text = Text;
        System.Windows.Automation.AutomationProperties.SetName(this, AccessibleName(Label, Text));
        var (fg, bg) = Kind switch
        {
            PillKind.Success => ("CpSuccess", "CpSuccessBg"),
            PillKind.Warning => ("CpCaution", "CpCautionBg"),
            PillKind.Critical => ("CpCritical", "CpCriticalBg"),
            PillKind.Accent => ("CpAccent", "CpAccentSubtle"),
            _ => ("CpNeutral", "CpNeutralBg"),
        };
        _text.SetResourceReference(TextBlock.ForegroundProperty, fg);
        SetResourceReference(BackgroundProperty, bg);
    }
}

/// <summary>TextBox / PasswordBox 灰色占位提示 (附加属性, 以 Adorner 绘制).</summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);

    static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(Hint), new PropertyMetadata(false));

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control c) return;
        if (!(bool)c.GetValue(HookedProperty))
        {
            // 只挂一次事件; 加载 / 可见性 / 尺寸 / 内容变化时都重新确保 Adorner 存在并重绘.
            c.SetValue(HookedProperty, true);
            c.Loaded += (_, _) => Ensure(c);
            c.IsVisibleChanged += (_, _) => Ensure(c);
            c.SizeChanged += (_, _) => Ensure(c);
            if (c is TextBox tb) tb.TextChanged += (_, _) => Ensure(c);
            if (c is PasswordBox pb) pb.PasswordChanged += (_, _) => Ensure(c);
            if (c is ComboBox cb) cb.SelectionChanged += (_, _) => Ensure(c);
        }
        Ensure(c);
    }

    /// <summary>Adorner 层在首次加载时可能不可用 (折叠 / 尚未入可视树), 因此每次检查并按需补挂.</summary>
    internal static bool Ensure(Control c)
    {
        if (AdornerLayer.GetAdornerLayer(c) is not { } layer) return false;
        var mine = layer.GetAdorners(c)?.OfType<HintAdorner>().ToList();
        if (mine is null || mine.Count == 0) { layer.Add(new HintAdorner(c)); return true; }
        foreach (var h in mine) h.InvalidateVisual();
        return true;
    }

    internal static bool IsEmpty(Control c) => c switch
    {
        TextBox t => t.Text.Length == 0,
        PasswordBox p => p.Password.Length == 0,
        ComboBox cb => cb.SelectedItem is null,
        _ => false,
    };

    sealed class HintAdorner(Control c) : Adorner(c)
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (!c.IsVisible || !IsEmpty(c) || GetText(c) is not { Length: > 0 } text) return;
            var brush = (Brush?)c.TryFindResource("CpTextTertiary") ?? Brushes.Gray;
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(c.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), c.FontSize, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, c.ActualWidth - 24),
                MaxLineCount = c is TextBox { AcceptsReturn: true } ? 4 : 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var top = c is TextBox { AcceptsReturn: true } ? 7 : Math.Max(0, (c.ActualHeight - ft.Height) / 2);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, c.ActualWidth, c.ActualHeight)));
            dc.DrawText(ft, new Point(12, top));
            dc.Pop();
        }

        protected override System.Windows.Media.HitTestResult? HitTestCore(PointHitTestParameters p) => null;
    }
}
