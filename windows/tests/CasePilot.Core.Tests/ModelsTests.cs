using CasePilot.Core.Models;

namespace CasePilot.Core.Tests;

[TestClass]
public class SeverityTests
{
    [DataTestMethod]
    [DataRow("minimal", Severity.Minimal)]
    [DataRow("Moderate", Severity.Moderate)]
    [DataRow("CRITICAL", Severity.Critical)]
    [DataRow("HighestCriticalImpact", Severity.HighestCriticalImpact)]
    public void Parse_IsCaseInsensitive(string api, Severity expected) =>
        Assert.AreEqual(expected, SeverityExtensions.Parse(api));

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("urgent")]
    public void Parse_UnknownReturnsNull(string? api) => Assert.IsNull(SeverityExtensions.Parse(api));

    [TestMethod]
    public void Levels_Titles_Details()
    {
        Assert.AreEqual("C", Severity.Minimal.Level());
        Assert.AreEqual("B", Severity.Moderate.Level());
        Assert.AreEqual("A", Severity.Critical.Level());
        Assert.AreEqual("A+", Severity.HighestCriticalImpact.Level());
        Assert.AreEqual("最小影响", Severity.Minimal.Title());
        Assert.AreEqual("业务关键", Severity.HighestCriticalImpact.Title());
        Assert.AreEqual("生产中断 · 需 Standard+", Severity.Critical.Detail());
    }

    [TestMethod]
    public void ApiValue_IsLowercase() => Assert.AreEqual("highestcriticalimpact", Severity.HighestCriticalImpact.ApiValue());

    [TestMethod]
    public void IsHigh_OnlyAandAPlus()
    {
        Assert.IsFalse(Severity.Minimal.IsHigh());
        Assert.IsFalse(Severity.Moderate.IsHigh());
        Assert.IsTrue(Severity.Critical.IsHigh());
        Assert.IsTrue(Severity.HighestCriticalImpact.IsHigh());
    }
}

[TestClass]
public class CaseKindTests
{
    static SupportService Svc(string name) => new("/providers/Microsoft.Support/services/x", "x", name, []);

    [TestMethod]
    public void From_DetectsQuotaBillingTechnical()
    {
        Assert.AreEqual(CaseKind.Quota, CaseKindExtensions.From(Svc("Service and subscription limits (quotas)")));
        Assert.AreEqual(CaseKind.Billing, CaseKindExtensions.From(Svc("Billing")));
        Assert.AreEqual(CaseKind.Technical, CaseKindExtensions.From(Svc("Billing and more")));
        Assert.AreEqual(CaseKind.Technical, CaseKindExtensions.From(Svc("Virtual Machine running Linux")));
        Assert.AreEqual(CaseKind.Technical, CaseKindExtensions.From(null));
    }

    [TestMethod]
    public void Labels() => Assert.AreEqual("配额申请", CaseKind.Quota.Label());
}

[TestClass]
public class ContactTests
{
    static Contact Valid() => new() { FirstName = "三", LastName = "张", Email = "a@b.com" };

    [TestMethod]
    public void Defaults_ChineseChina()
    {
        var c = new Contact();
        Assert.AreEqual("zh-hans", c.Language);
        Assert.AreEqual("CHN", c.Country);
        Assert.AreEqual("China Standard Time", c.TimeZone);
        Assert.IsFalse(c.PreferPhone);
        Assert.AreEqual("", c.Email);
    }

    [TestMethod]
    public void IsValid_RequiresNamesAndEmail()
    {
        Assert.IsTrue(Valid().IsValid);
        Assert.IsFalse((Valid() with { FirstName = "" }).IsValid);
        Assert.IsFalse((Valid() with { LastName = "" }).IsValid);
        Assert.IsFalse((Valid() with { Email = "a@b" }).IsValid);
        Assert.IsFalse((Valid() with { Email = "a b@c.com" }).IsValid);
    }

    [TestMethod]
    public void IsValid_PhoneRequiredWhenPreferred()
    {
        Assert.IsFalse((Valid() with { PreferPhone = true }).IsValid);
        Assert.IsTrue((Valid() with { PreferPhone = true, Phone = "123" }).IsValid);
    }

    [TestMethod]
    public void CcList_SplitsTrimsDropsEmpty() =>
        CollectionAssert.AreEqual(new[] { "x@y.com", "z@y.com" }, (Valid() with { Cc = "x@y.com, ,z@y.com" }).CcList.ToArray());

    [TestMethod]
    public void EnumTables()
    {
        Assert.AreEqual(5, Contact.Languages.Count);
        Assert.AreEqual(11, Contact.Countries.Count);
        Assert.AreEqual(13, Contact.TimeZones.Count);
    }
}
