import XCTest
@testable import CasePilot

final class BuildBodyTests: XCTestCase {
    private func draft() -> NewCase {
        var d = NewCase()
        d.service = SupportService(id: "/providers/Microsoft.Support/services/x", name: "x", displayName: "X", resourceTypes: [])
        d.problem = ProblemClassification(id: "/p", displayName: "P")
        d.title = "标题测试"; d.description = "描述"
        d.contact = Contact(firstName: "三", lastName: "张", email: "a@b.com")
        return d
    }

    func testDefaultsChineseChina() {
        let c = (SupportClient.buildBody(draft())["properties"] as! [String: Any])["contactDetails"] as! [String: Any]
        XCTAssertEqual(c["preferredSupportLanguage"] as? String, "zh-hans")
        XCTAssertEqual(c["country"] as? String, "CHN")
        XCTAssertEqual(c["preferredTimeZone"] as? String, "China Standard Time")
    }

    func testQuotaPayloadUsesVMFamily() {
        var d = draft(); d.quota = QuotaRequest(region: "eastus", vmFamily: "DSv5 Series", newLimit: "100")
        let q = (SupportClient.buildBody(d)["properties"] as! [String: Any])["quotaTicketDetails"] as! [String: Any]
        let payload = (q["quotaChangeRequests"] as! [[String: Any]])[0]["payload"] as! String
        XCTAssertEqual(payload, #"{"NewLimit":100,"VMFamily":"DSv5 Series"}"#)
    }

    func test24x7OnlyForHighSeverity() {
        var d = draft(); d.require24x7 = true; d.severity = .moderate
        XCTAssertEqual((SupportClient.buildBody(d)["properties"] as! [String: Any])["require24X7Response"] as? Bool, false)
        d.severity = .critical
        XCTAssertEqual((SupportClient.buildBody(d)["properties"] as! [String: Any])["require24X7Response"] as? Bool, true)
    }

    func testCCAndResource() {
        var d = draft(); d.contact.cc = "x@y.com, ,z@y.com"; d.resourceId = "/subscriptions/1/r"
        let p = SupportClient.buildBody(d)["properties"] as! [String: Any]
        XCTAssertEqual((p["contactDetails"] as! [String: Any])["additionalEmailAddresses"] as? [String], ["x@y.com", "z@y.com"])
        XCTAssertEqual((p["technicalTicketDetails"] as! [String: Any])["resourceId"] as? String, "/subscriptions/1/r")
    }
}

final class HTMLTextTests: XCTestCase {
    func testStripsTagsAndScripts() {
        let s = HTMLText.plain("<style>x{}</style><p>你好&nbsp;Zheng</p><script>alert(1)</script><br>A &amp; B")
        XCTAssertEqual(s, "你好 Zheng\n\nA & B")
    }
}

final class ResourceRankTests: XCTestCase {
    func testPrefersSpecificVMService() {
        let r = AzureResource(id: "/r", name: "vm", type: "microsoft.compute/virtualmachines", location: "x")
        let broad = SupportService(id: "a", name: "a", displayName: "Azure Update Manager", resourceTypes: Array(repeating: "t", count: 5))
        let vm = SupportService(id: "b", name: "b", displayName: "Virtual Machine running Linux", resourceTypes: ["t"])
        XCTAssertEqual(ResourcePicker.rank([broad, vm], r).first?.displayName, "Virtual Machine running Linux")
    }
}

final class JWTTests: XCTestCase {
    func testClaims() {
        let payload = Data(#"{"tid":"t1","preferred_username":"u@x"}"#.utf8).base64EncodedString()
            .replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
        let c = AuthProvider.jwtClaims("h.\(payload).s")
        XCTAssertEqual(c["tid"] as? String, "t1")
    }
}
