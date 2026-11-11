using System.Globalization;
using CasePilot.Core.Models;

namespace CasePilot.App.ViewModels;

public enum PillKind { Neutral, Success, Warning, Critical, Accent }

/// <summary>列表与详情的显示格式 (纯函数, 便于测试).</summary>
public static class Display
{
    public static string Relative(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } w) return "";
        var d = now - w;
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} 分钟前";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} 小时前";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} 天前";
        if (d.TotalDays < 365) return $"{(int)(d.TotalDays / 30)} 个月前";
        return $"{(int)(d.TotalDays / 365)} 年前";
    }

    public static string Full(DateTimeOffset? when) =>
        when?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

    public static string StatusLabel(string status) => status.ToLowerInvariant() switch
    {
        "open" => "处理中",
        "closed" => "已关闭",
        _ => status,
    };

    public static PillKind StatusKind(string status) =>
        status.Equals("open", StringComparison.OrdinalIgnoreCase) ? PillKind.Success : PillKind.Neutral;

    public static string SeverityLabel(string api) => SeverityExtensions.Parse(api)?.Level() ?? "-";

    public static PillKind SeverityKind(string api) => SeverityExtensions.Parse(api) switch
    {
        Severity.Moderate => PillKind.Warning,
        Severity.Critical or Severity.HighestCriticalImpact => PillKind.Critical,
        _ => PillKind.Neutral,
    };

    /// <summary>Narrator 读出的列表行名称: Case 号, 标题, 严重性, 状态, 创建时间.</summary>
    public static string RowName(TicketSummary t, DateTimeOffset now) =>
        $"{t.TicketNumber}, {t.Title}, 严重性 {SeverityLabel(t.Severity)}, {StatusLabel(t.Status)}, {Relative(t.Created, now)}";

    public static string Subtitle(TicketSummary t)
    {
        var p = t.Problem.Split('/')[^1].Trim();
        return p.Length == 0 ? t.Service : $"{t.Service} · {p}";
    }

    public static string Initials(string user) => user.Length == 0 ? "?" : user[..1].ToUpperInvariant();
}

/// <summary>"我的 Case" 列表的响应式列布局: 宽度不足 (如详情面板打开) 时隐藏 "创建时间", 把空间让给 Case 号与标题.</summary>
public static class HistoryLayout
{
    /// <summary>Case # (148) + 标题最小 (160) + 严重性 (72) + 状态 (88) + 创建时间 (100) + 边距.</summary>
    public const double CreatedMinGridWidth = 600;

    public static bool ShowCreated(double gridWidth) => gridWidth <= 0 || gridWidth >= CreatedMinGridWidth;
}
