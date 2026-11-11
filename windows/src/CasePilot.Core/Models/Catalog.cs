namespace CasePilot.Core.Models;

/// <param name="Id">完整资源 ID</param>
/// <param name="Name">GUID</param>
/// <param name="DisplayName">显示名</param>
/// <param name="ResourceTypes">关联的 ARM 资源类型</param>
public sealed record SupportService(string Id, string Name, string DisplayName, IReadOnlyList<string> ResourceTypes);

public sealed record ProblemClassification(string Id, string DisplayName);

public sealed record AzureResource(string Id, string Name, string Type, string Location);
