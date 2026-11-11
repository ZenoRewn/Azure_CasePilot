using System.Text;
using System.Text.Json.Nodes;
using CasePilot.Core.Models;
using CasePilot.Core.Picking;
using CasePilot.Core.Plan;
using CasePilot.Core.Support;
using CasePilot.Core.Text;

namespace CasePilot.Core.Tests;

[TestClass]
public class SupportBodyTests
{
    internal static NewCase Draft() => new()
    {
        Service = new SupportService("/providers/Microsoft.Support/services/x", "x", "X", []),
        Problem = new ProblemClassification("/p", "P"),
        Title = "标题测试",
        Description = "描述",
        Contact = new Contact { FirstName = "三", LastName = "张", Email = "a@b.com" },
    };

    private static JsonObject Props(NewCase c) => SupportBody.Build(c)["properties"]!.AsObject();

    [TestMethod]
    public void DefaultsChineseChina()
    {
        var c = Props(Draft())["contactDetails"]!;
        Assert.AreEqual("zh-hans", (string?)c["preferredSupportLanguage"]);
        Assert.AreEqual("CHN", (string?)c["country"]);
        Assert.AreEqual("China Standard Time", (string?)c["preferredTimeZone"]);
        Assert.AreEqual("email", (string?)c["preferredContactMethod"]);
    }

    [TestMethod]
    public void QuotaPayloadUsesVMFamily()
    {
        var d = Draft() with { Quota = new QuotaRequest("eastus", "DSv5 Series", "100") };
        var q = Props(d)["quotaTicketDetails"]!;
        Assert.AreEqual("1.0", (string?)q["quotaChangeRequestVersion"]);
        var r = q["quotaChangeRequests"]![0]!;
        Assert.AreEqual("eastus", (string?)r["region"]);
        Assert.AreEqual("""{"NewLimit":100,"VMFamily":"DSv5 Series"}""", (string?)r["payload"]);
    }

    [TestMethod]
    public void QuotaNonNumericLimitIsZero()
    {
        var d = Draft() with { Quota = new QuotaRequest("eastus", "X", "abc") };
        Assert.AreEqual("""{"NewLimit":0,"VMFamily":"X"}""",
            (string?)Props(d)["quotaTicketDetails"]!["quotaChangeRequests"]![0]!["payload"]);
    }

    [TestMethod]
    public void TwentyFourSevenOnlyForHighSeverityWithToggle()
    {
        var d = Draft() with { Require24x7 = true, Severity = Severity.Moderate };
        Assert.IsFalse((bool)Props(d)["require24X7Response"]!);
        Assert.IsTrue((bool)Props(d with { Severity = Severity.Critical })["require24X7Response"]!);
        Assert.IsFalse((bool)Props(d with { Severity = Severity.Critical, Require24x7 = false })["require24X7Response"]!);
    }

    [TestMethod]
    public void CcAndResource()
    {
        var d = Draft() with { Contact = Draft().Contact with { Cc = "x@y.com, ,z@y.com" }, ResourceId = "/subscriptions/1/r" };
        var p = Props(d);
        var cc = p["contactDetails"]!["additionalEmailAddresses"]!.AsArray().Select(n => (string?)n).ToArray();
        CollectionAssert.AreEqual(new[] { "x@y.com", "z@y.com" }, cc);
        Assert.AreEqual("/subscriptions/1/r", (string?)p["technicalTicketDetails"]!["resourceId"]);
    }

    [TestMethod]
    public void NoCcNoResourceNoPhoneAreOmitted()
    {
        var p = Props(Draft());
        Assert.IsFalse(p["contactDetails"]!.AsObject().ContainsKey("additionalEmailAddresses"));
        Assert.IsFalse(p["contactDetails"]!.AsObject().ContainsKey("phoneNumber"));
        Assert.IsFalse(p.ContainsKey("technicalTicketDetails"));
        Assert.IsFalse(p.ContainsKey("problemStartTime"));
        Assert.IsFalse(p.ContainsKey("quotaTicketDetails"));
    }

    [TestMethod]
    public void ConsentTrimStartTimeSeverityPhone()
    {
        var d = Draft() with
        {
            Title = "  t  \n", Description = "\n d ", Consent = false, Severity = Severity.HighestCriticalImpact,
            StartTime = new DateTimeOffset(2026, 11, 11, 16, 5, 9, TimeSpan.FromHours(8)),
            Contact = Draft().Contact with { Phone = "123", PreferPhone = true },
        };
        var p = Props(d);
        Assert.AreEqual("t", (string?)p["title"]);
        Assert.AreEqual("d", (string?)p["description"]);
        Assert.AreEqual("No", (string?)p["advancedDiagnosticConsent"]);
        Assert.AreEqual("Yes", (string?)Props(Draft())["advancedDiagnosticConsent"]);
        Assert.AreEqual("highestcriticalimpact", (string?)p["severity"]);
        Assert.AreEqual("2026-11-11T08:05:09Z", (string?)p["problemStartTime"]);
        Assert.AreEqual("123", (string?)p["contactDetails"]!["phoneNumber"]);
        Assert.AreEqual("phone", (string?)p["contactDetails"]!["preferredContactMethod"]);
        Assert.AreEqual("/providers/Microsoft.Support/services/x", (string?)p["serviceId"]);
        Assert.AreEqual("/p", (string?)p["problemClassificationId"]);
    }
}

[TestClass]
public class TextTests
{
    [TestMethod]
    public void HtmlPlain_StripsTagsAndScripts() =>
        Assert.AreEqual("你好 Zheng\n\nA & B",
            HtmlText.Plain("<style>x{}</style><p>你好&nbsp;Zheng</p><script>alert(1)</script><br>A &amp; B"));

    [TestMethod]
    public void HtmlPlain_DecodesAmpLast() => Assert.AreEqual("&lt;", HtmlText.Plain("&amp;lt;"));

    [TestMethod]
    public void ReplyBody_IsPlainText_NotHtmlEscaped() =>
        // Azure 把回复包进 <pre> 并剥离 ; 与 <>: HTML 转义会显示为 "&ltb&gt" 乱码 (2026-11-11 实测)
        Assert.AreEqual("a <b> & c\n第二行", ReplyText.Body("  a <b> & c\r\n第二行 \n"));

    [TestMethod]
    public void JwtClaims_UnpaddedBase64Url()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"tid":"t1","upn":"u@x","n":"??>"}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var c = Jwt.Claims($"h.{payload}.s");
        Assert.AreEqual("t1", c["tid"].GetString());
        Assert.AreEqual("u@x", c["upn"].GetString());
    }

    [TestMethod]
    public void JwtClaims_GarbageIsEmpty()
    {
        Assert.AreEqual(0, Jwt.Claims("").Count);
        Assert.AreEqual(0, Jwt.Claims("a.!!!.c").Count);
    }

    [TestMethod]
    public void FormEncoding_PlusAndSpace() =>
        Assert.AreEqual("client_secret=a%2Bb%20c%3D&scope=https%3A%2F%2Fx%2F.default",
            FormEncoding.Encode([new("client_secret", "a+b c="), new("scope", "https://x/.default")]));
}

[TestClass]
public class ArmPathsTests
{
    [TestMethod]
    public void SupportBase()
    {
        Assert.AreEqual("/subscriptions/s1/providers/Microsoft.Support", ArmPaths.SupportBase("s1"));
        Assert.AreEqual("/providers/Microsoft.Support", ArmPaths.SupportBase(null));
        Assert.AreEqual("/providers/Microsoft.Support", ArmPaths.SupportBase(""));
    }

    [TestMethod]
    public void ResourcesFilter_AtMost15()
    {
        var types = Enumerable.Range(0, 20).Select(i => $"t{i}").ToList();
        var f = ArmPaths.ResourcesFilter(types);
        Assert.AreEqual(15, f.Split(" or ").Length);
        Assert.IsTrue(f.StartsWith("resourceType eq 't0' or resourceType eq 't1'"));
        Assert.IsTrue(f.EndsWith("resourceType eq 't14'"));
    }

    [TestMethod]
    public void TicketsFilter_DayBoundaryUtc()
    {
        var now = new DateTimeOffset(2026, 11, 11, 3, 0, 0, TimeSpan.Zero);
        Assert.AreEqual("createdDate ge 2026-10-12T00:00:00Z", ArmPaths.TicketsFilter(30, now));
        Assert.AreEqual("createdDate ge 2026-11-10T00:00:00Z", ArmPaths.TicketsFilter(0, now));
        Assert.AreEqual("createdDate ge 2025-11-11T00:00:00Z", ArmPaths.TicketsFilter(9999, now));
    }
}

[TestClass]
public class PortalLinkTests
{
    [TestMethod]
    public void PerCloud()
    {
        Assert.AreEqual(
            "https://portal.azure.cn/#view/Microsoft_Azure_Support/SupportRequestDetails.ReactView/id/%2Fsubscriptions%2Fs1%2Fproviders%2FMicrosoft%2ESupport%2FsupportTickets%2Fcasepilot%2D1",
            PortalLink.For(AzureCloud.China, "s1", "casepilot-1"));
        Assert.IsTrue(PortalLink.For(AzureCloud.Global, null, "n").StartsWith("https://portal.azure.com/#view/"));
        Assert.IsTrue(PortalLink.For(AzureCloud.UsGov, null, "n").EndsWith("/id/%2Fproviders%2FMicrosoft%2ESupport%2FsupportTickets%2Fn"));
        Assert.IsTrue(PortalLink.For(AzureCloud.UsGov, null, "n").StartsWith("https://portal.azure.us/"));
    }

    [TestMethod]
    public void CloudUrls()
    {
        Assert.AreEqual("https://management.chinacloudapi.cn", AzureCloud.China.Arm());
        Assert.AreEqual("https://login.microsoftonline.us", AzureCloud.UsGov.Login());
        Assert.AreEqual("Azure China (世纪互联)", AzureCloud.China.Label());
        Assert.AreEqual(AzureCloud.China, AzureCloudExtensions.FromEnvironmentName("AzureChinaCloud"));
        Assert.AreEqual(AzureCloud.Global, AzureCloudExtensions.FromEnvironmentName("whatever"));
    }
}

[TestClass]
public class PickingTests
{
    [TestMethod]
    public void Rank_PrefersSpecificVMService()
    {
        var r = new AzureResource("/r", "vm", "microsoft.compute/virtualmachines", "x");
        var broad = new SupportService("a", "a", "Azure Update Manager", Enumerable.Repeat("t", 5).ToList());
        var vm = new SupportService("b", "b", "Virtual Machine running Linux", ["t"]);
        Assert.AreEqual("Virtual Machine running Linux", ResourceRanker.Rank([broad, vm], r)[0].DisplayName);
    }

    [TestMethod]
    public void Candidates_MatchTypeCaseInsensitive()
    {
        var r = new AzureResource("/r", "vm", "microsoft.compute/virtualmachines", "x");
        var a = new SupportService("a", "a", "A", ["Microsoft.Compute/virtualMachines"]);
        var b = new SupportService("b", "b", "B", ["Microsoft.Web/sites"]);
        CollectionAssert.AreEqual(new[] { a }, ResourceRanker.Candidates([a, b], r).ToArray());
    }

    [TestMethod]
    public void ServiceSearch_AndsTerms_KeepsOrder()
    {
        static SupportService S(string n) => new(n, n, n, []);
        var list = new[] { S("Azure Kubernetes Service"), S("Virtual Machine running Linux"), S("Virtual Machine running Windows") };
        var f = ServiceSearch.Filter("machine LINUX", list);
        Assert.AreEqual(1, f.Count);
        Assert.AreEqual("Virtual Machine running Linux", f[0].DisplayName);
        Assert.AreEqual("Virtual Machine running Linux", ServiceSearch.Filter("virtual", list)[0].DisplayName);
        Assert.AreEqual(3, ServiceSearch.Filter("  ", list).Count);
    }
}

[TestClass]
public class SupportPlanTests
{
    private static TicketSummary T(string? plan, int year) =>
        new("n", "1", "t", "Open", "minimal", new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero), "s", "p", plan);

    [TestMethod]
    public void FromRecentTicket()
    {
        var p = SupportPlan.Infer([T("Unified Enterprise", 2026)], "EnterpriseAgreement_2014-09-01");
        Assert.AreEqual(new SupportPlan("Unified Enterprise", "unified", "ticket", true), p);
        Assert.AreEqual("根据该订阅最近的 Case 识别", p.SourceLabel);
    }

    [TestMethod]
    public void NewestTicketWins() =>
        Assert.AreEqual("prodirect", SupportPlan.Infer([T("Basic", 2025), T("ProDirect", 2026), T(null, 2027)], "").Tier);

    [TestMethod]
    public void OfferHintWithoutTickets() =>
        Assert.AreEqual(new SupportPlan("Developer / Basic (按订阅类型推断)", "developer-or-basic", "offer", false),
            SupportPlan.Infer([], "MSDN_2014-09-01"));

    [TestMethod]
    public void Unknown() =>
        Assert.AreEqual(new SupportPlan(null, "unknown", "none", null), SupportPlan.Infer([], "PayAsYouGo_2014-09-01"));

    [TestMethod]
    public void UnknownTierFromTicketHasNullAllowsCritical() =>
        Assert.IsNull(SupportPlan.Infer([T("Mystery", 2026)], "").AllowsCritical);

    [DataTestMethod]
    [DataRow("Azure Support Plan - Internal", "internal")]
    [DataRow("Standard", "standard")]
    [DataRow("Premier", "premier")]
    [DataRow("Developer", "developer-or-basic")]
    [DataRow("Professional Direct", "prodirect")]
    public void Tiers(string name, string tier) => Assert.AreEqual(tier, SupportPlan.TierOf(name));
}
