using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CasePilot.Core.Models;

namespace CasePilot.Core.Support;

public static class ArmPaths
{
    public const string SupportApi = "2024-04-01";

    public static string SupportBase(string? sub) =>
        string.IsNullOrEmpty(sub) ? "/providers/Microsoft.Support" : $"/subscriptions/{sub}/providers/Microsoft.Support";

    /// <summary>资源类型过滤 (ARM 限制, 最多 15 个类型).</summary>
    public static string ResourcesFilter(IEnumerable<string> types) =>
        string.Join(" or ", types.Take(15).Select(t => $"resourceType eq '{t}'"));

    /// <summary>与 Python 一致: days 限制在 1..365, 截断到 UTC 当日 0 点.</summary>
    public static string TicketsFilter(int days, DateTimeOffset now)
    {
        days = Math.Clamp(days, 1, 365);
        var since = now.UtcDateTime.AddDays(-days);
        return "createdDate ge " + since.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);
    }
}

public static class PortalLink
{
    public static string For(AzureCloud cloud, string? sub, string name)
    {
        var rid = $"{(string.IsNullOrEmpty(sub) ? "" : $"/subscriptions/{sub}")}/providers/Microsoft.Support/supportTickets/{name}";
        return $"{cloud.Portal()}/#view/Microsoft_Azure_Support/SupportRequestDetails.ReactView/id/{EncodeAlnum(rid)}";
    }

    /// <summary>与 Swift addingPercentEncoding(.alphanumerics) 一致: 仅保留字母数字.</summary>
    private static string EncodeAlnum(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if (char.IsAsciiLetterOrDigit((char)b)) sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

public static class SupportBody
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject Build(NewCase c)
    {
        var ct = c.Contact;
        var contact = new JsonObject
        {
            ["firstName"] = ct.FirstName,
            ["lastName"] = ct.LastName,
            ["primaryEmailAddress"] = ct.Email,
            ["preferredContactMethod"] = ct.PreferPhone ? "phone" : "email",
            ["preferredTimeZone"] = ct.TimeZone,
            ["preferredSupportLanguage"] = ct.Language,
            ["country"] = ct.Country,
        };
        if (ct.Phone.Length > 0) contact["phoneNumber"] = ct.Phone;
        if (ct.CcList.Count > 0) contact["additionalEmailAddresses"] = new JsonArray(ct.CcList.Select(x => (JsonNode?)x).ToArray());

        var p = new JsonObject
        {
            ["title"] = c.Title.Trim(),
            ["description"] = c.Description.Trim(),
            ["serviceId"] = c.Service?.Id ?? "",
            ["problemClassificationId"] = c.Problem?.Id ?? "",
            ["severity"] = c.Severity.ApiValue(),
            ["require24X7Response"] = c.Severity.IsHigh() && c.Require24x7,
            ["advancedDiagnosticConsent"] = c.Consent ? "Yes" : "No",
            ["contactDetails"] = contact,
        };
        if (c.StartTime is { } t)
            p["problemStartTime"] = t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(c.ResourceId)) p["technicalTicketDetails"] = new JsonObject { ["resourceId"] = c.ResourceId };
        if (c.Quota is { } q)
        {
            // 键按字母序, 与 Swift/Python 的 sortedKeys 输出一致
            var payload = new JsonObject
            {
                ["NewLimit"] = int.TryParse(q.NewLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
                ["VMFamily"] = q.VmFamily,
            }.ToJsonString(Compact);
            p["quotaTicketDetails"] = new JsonObject
            {
                ["quotaChangeRequestVersion"] = "1.0",
                ["quotaChangeRequests"] = new JsonArray(new JsonObject { ["region"] = q.Region, ["payload"] = payload }),
            };
        }
        return new JsonObject { ["properties"] = p };
    }
}
