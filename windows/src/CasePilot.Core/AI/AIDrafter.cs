using System.Text.Json;
using System.Text.Json.Nodes;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Models;

namespace CasePilot.Core.AI;

public sealed record QA(string Q, string A);

public sealed record AIDraft(SupportService? Service, ProblemClassification? Problem, Severity Severity,
    string Title, string Description, IReadOnlyList<string> Questions)
{
    public bool Ready => Service is not null && Problem is not null && Questions.Count == 0;
}

/// <summary>控制 token: 完整服务目录约 350 项, 问题类型可达 200 项.</summary>
public static class Shortlist
{
    public static readonly IReadOnlySet<string> Popular = new HashSet<string>
    {
        "Azure OpenAI", "Microsoft Foundry", "Foundry Tools", "Virtual Machine running Linux",
        "Virtual Machine running Windows", "Kubernetes Service (AKS)", "App Service", "Storage Account Management",
        "Azure SQL Database", "Service and subscription limits (quotas)", "Billing", "Subscription management",
    };

    public static readonly IReadOnlyList<(string Key, string Value)> Synonyms =
    [
        ("aoai", "openai"), ("gpt", "openai"), ("模型", "openai"), ("foundry", "foundry"), ("虚拟机", "virtual machine"),
        ("vm", "virtual machine"), ("ssh", "virtual machine"), ("rdp", "virtual machine"), ("k8s", "kubernetes"), ("aks", "kubernetes"),
        ("存储", "storage"), ("配额", "quota"), ("quota", "quota"), ("账单", "billing"), ("发票", "billing"), ("数据库", "sql"), ("网站", "app service"),
    ];

    /// <summary>关键词命中在前, 其后为常用服务; 去重, 最多 limit 项.</summary>
    public static IReadOnlyList<SupportService> Services(string text, IReadOnlyList<SupportService> services, int limit = 60)
    {
        var t = text.ToLowerInvariant();
        var keys = t.Split([' ', '/'], StringSplitOptions.RemoveEmptyEntries).Where(k => k.Length > 2).ToHashSet();
        foreach (var (k, v) in Synonyms) if (t.Contains(k)) keys.Add(v);
        var hits = services.Where(s => keys.Any(k => s.DisplayName.ToLowerInvariant().Contains(k)));
        var seen = new HashSet<string>();
        return hits.Concat(services.Where(s => Popular.Contains(s.DisplayName)))
            .Where(s => seen.Add(s.Name)).Take(limit).ToList();
    }

    /// <summary>按与描述的词重叠排序 (稳定) 后截断.</summary>
    public static IReadOnlyList<ProblemClassification> Problems(string text, IReadOnlyList<ProblemClassification> problems, int limit = 40)
    {
        var t = text.ToLowerInvariant();
        var keys = new HashSet<string>();
        var cur = new System.Text.StringBuilder();
        foreach (var ch in t + " ")
        {
            if (char.IsLetterOrDigit(ch) || ch == '.') { cur.Append(ch); continue; }
            var w = cur.ToString(); cur.Clear();
            if (w.Length > 1 && w.All(char.IsAscii)) keys.Add(w);
        }
        foreach (var (k, v) in Synonyms) if (t.Contains(k)) { keys.Add(v); keys.Add(k); }
        int Score(ProblemClassification p) { var d = p.DisplayName.ToLowerInvariant(); return keys.Count(d.Contains); }
        return problems.OrderByDescending(Score).Take(limit).ToList();
    }
}

/// <summary>起草 Case (与 macOS AIDrafter / casepilot/ai.py 同一规则).</summary>
public static class AIDrafter
{
    public const int MaxQuestions = 3;

    public const string System = """
        你是 Azure 技术支持工单助手。根据用户描述起草一张 Azure Support Case。
        规则:
        - service 必须是给定服务目录中的 name, 选最贴切的一个。
        - severity: minimal=不影响业务, moderate=生产受影响但有变通, critical=生产中断, highestcriticalimpact=关键业务全面中断。拿不准时用 moderate。
        - title: 一句话, 包含服务、区域/资源 (若已知) 与核心现象, 中文, 不超过 80 字。
        - description 使用以下结构, 只写用户提供或可直接推断的事实, 未知项写"待补充":
          【现象】【业务影响】【首次发生时间 (含时区)】【错误信息 / Request ID】【已尝试的排查】
        - questions: 仅当缺少会显著影响排查的关键信息时提问 (最多 3 个, 简短中文, 合并相关项); 信息足够或已有补充问答时返回空数组。
        - 不要编造 Request ID、资源名或时间。
        """;

    public const string ProblemSystem = "从问题类型目录中选出最匹配的一项, 返回其 id。";

    public static readonly string ServiceSchema = new JsonObject
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("service", "severity", "title", "description", "questions"),
        ["properties"] = new JsonObject
        {
            ["service"] = new JsonObject { ["type"] = "string" },
            ["severity"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Enum.GetValues<Severity>().Select(s => (JsonNode?)s.ApiValue()).ToArray()),
            },
            ["title"] = new JsonObject { ["type"] = "string" },
            ["description"] = new JsonObject { ["type"] = "string" },
            ["questions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
    }.ToJsonString();

    public static readonly string ProblemSchema = new JsonObject
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("problemId"),
        ["properties"] = new JsonObject { ["problemId"] = new JsonObject { ["type"] = "string" } },
    }.ToJsonString();

    public static async Task<AIDraft> DraftAsync(string text, IReadOnlyList<QA> answers, IReadOnlyList<SupportService> services,
        Func<SupportService, CancellationToken, Task<IReadOnlyList<ProblemClassification>>> problems, IChat chat, CancellationToken ct)
    {
        var qa = string.Join("\n", answers.Select(a => $"问: {a.Q}\n答: {a.A}"));
        var catalog = string.Join("\n", Shortlist.Services(text + " " + qa, services).Select(s => $"{s.Name}\t{s.DisplayName}"));
        var user = $"用户描述:\n{text}\n\n" + (qa.Length == 0 ? "" : $"补充问答:\n{qa}\n\n") + $"服务目录 (name\\tdisplayName):\n{catalog}";
        var r = ParseObject(await chat.ChatAsync([new("system", System), new("user", user)], ServiceSchema, ct));

        var title = Str(r, "title");
        var description = Str(r, "description");
        var pick = Str(r, "service").Trim().ToLowerInvariant();
        var svc = services.FirstOrDefault(s => s.Name.ToLowerInvariant() == pick || s.DisplayName.ToLowerInvariant() == pick);
        ProblemClassification? problem = null;
        if (svc is not null)
        {
            var ps = Shortlist.Problems($"{text} {qa} {title} {description}", await problems(svc, ct));
            var p = ParseObject(await chat.ChatAsync(
                [new("system", ProblemSystem),
                 new("user", $"工单: {title}\n{description}\n\n问题类型目录 (id\\tdisplayName):\n"
                     + string.Join("\n", ps.Select(x => $"{x.Id}\t{x.DisplayName}")))], ProblemSchema, ct));
            var pid = Str(p, "problemId").Trim().ToLowerInvariant();
            problem = ps.FirstOrDefault(x => x.Id.ToLowerInvariant() == pid || x.DisplayName.ToLowerInvariant() == pid);
        }
        // 只追问一轮: 回答后剩余未知项在描述中标注"待补充"
        IReadOnlyList<string> qs = answers.Count > 0 || r["questions"] is not JsonArray arr ? [] :
            arr.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "")
               .Where(s => s.Length > 0).Take(MaxQuestions).ToList();
        return new AIDraft(svc, problem, SeverityExtensions.Parse(Str(r, "severity")) ?? Severity.Moderate,
            title.Length > 200 ? title[..200] : title, description, qs);
    }

    private static JsonObject ParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? []; }
        catch (JsonException) { return []; }
    }

    private static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
