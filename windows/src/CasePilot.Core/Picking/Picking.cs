using CasePilot.Core.Models;

namespace CasePilot.Core.Picking;

public static class ResourceRanker
{
    /// <summary>支持该资源类型的服务.</summary>
    public static IEnumerable<SupportService> Candidates(IEnumerable<SupportService> services, AzureResource r) =>
        services.Where(s => s.ResourceTypes.Any(t => t.Equals(r.Type, StringComparison.OrdinalIgnoreCase)));

    /// <summary>同一资源类型对应多个服务时: 名称与资源类型相近、覆盖类型更少 (更专一) 的优先.</summary>
    public static IReadOnlyList<SupportService> Rank(IEnumerable<SupportService> services, AzureResource r)
    {
        var leafFull = r.Type.Split('/')[^1].ToLowerInvariant();
        var leaf = leafFull[..Math.Min(7, leafFull.Length)];
        int Score(SupportService s) =>
            (s.DisplayName.ToLowerInvariant().Replace(" ", "").Contains(leaf) ? -100 : 0) + s.ResourceTypes.Count;
        return services.OrderBy(Score).ToList(); // OrderBy 是稳定排序
    }
}

public static class ServiceSearch
{
    /// <summary>空格分隔的词全部命中 (AND, 不区分大小写); 保持原顺序, 回车选第一项.</summary>
    public static IReadOnlyList<SupportService> Filter(string query, IEnumerable<SupportService> services)
    {
        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return services.Where(s =>
        {
            var n = s.DisplayName.ToLowerInvariant();
            return terms.All(n.Contains);
        }).ToList();
    }
}
