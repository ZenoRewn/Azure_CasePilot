namespace CasePilot.Core.Models;

public enum AzureCloud { Global, China, UsGov }

public static class AzureCloudExtensions
{
    public static string EnvironmentName(this AzureCloud c) => c switch
    {
        AzureCloud.China => "AzureChinaCloud",
        AzureCloud.UsGov => "AzureUSGovernment",
        _ => "AzureCloud",
    };

    public static AzureCloud FromEnvironmentName(string? name) =>
        Enum.GetValues<AzureCloud>().FirstOrDefault(c => c.EnvironmentName() == name, AzureCloud.Global);

    public static string Label(this AzureCloud c) => c switch
    {
        AzureCloud.China => "Azure China (世纪互联)",
        AzureCloud.UsGov => "Azure US Government",
        _ => "Azure Global",
    };

    public static string Arm(this AzureCloud c) => c switch
    {
        AzureCloud.China => "https://management.chinacloudapi.cn",
        AzureCloud.UsGov => "https://management.usgovcloudapi.net",
        _ => "https://management.azure.com",
    };

    public static string Login(this AzureCloud c) => c switch
    {
        AzureCloud.China => "https://login.chinacloudapi.cn",
        AzureCloud.UsGov => "https://login.microsoftonline.us",
        _ => "https://login.microsoftonline.com",
    };

    public static string Portal(this AzureCloud c) => c switch
    {
        AzureCloud.China => "https://portal.azure.cn",
        AzureCloud.UsGov => "https://portal.azure.us",
        _ => "https://portal.azure.com",
    };
}
