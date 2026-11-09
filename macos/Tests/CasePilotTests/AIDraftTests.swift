import XCTest
@testable import CasePilot

final class AIDraftTests: XCTestCase {
    let services = [SupportService(id: "/s/vm", name: "svc-vm", displayName: "Virtual Machine running Linux", resourceTypes: []),
                    SupportService(id: "/s/aoai", name: "svc-aoai", displayName: "Azure OpenAI", resourceTypes: [])]
    let problems = [ProblemClassification(id: "/p/ssh", displayName: "Cannot connect / SSH"),
                    ProblemClassification(id: "/p/429", displayName: "API Errors / HTTP 429")]

    private func run(_ first: [String: Any], _ second: [String: Any] = ["problemId": "/p/429"], answers: [QA] = []) async throws -> AIDraft {
        var replies = [first, second]
        return try await AIDrafter.draft(text: "x", answers: answers, services: services, problems: { _ in self.problems }) { _, _ in
            replies.removeFirst()
        }
    }

    func testMapsCatalogByNameOrDisplayName() async throws {
        let d = try await run(["service": "Azure OpenAI", "severity": "moderate", "title": "t", "description": "d", "questions": []])
        XCTAssertEqual(d.service?.name, "svc-aoai")
        XCTAssertEqual(d.problem?.id, "/p/429")
        XCTAssertTrue(d.ready)
    }

    func testRejectsHallucination() async throws {
        let d = try await run(["service": "nope", "severity": "moderate", "title": "t", "description": "d", "questions": []])
        XCTAssertNil(d.service)
        XCTAssertFalse(d.ready)
    }

    func testQuestionsCappedAndOnlyOneRound() async throws {
        let qs = ["a", "b", "c", "d"]
        let d1 = try await run(["service": "svc-aoai", "severity": "x", "title": "t", "description": "d", "questions": qs])
        XCTAssertEqual(d1.questions.count, 3)
        XCTAssertEqual(d1.severity, .moderate)
        let d2 = try await run(["service": "svc-aoai", "severity": "minimal", "title": "t", "description": "d", "questions": qs],
                               answers: [QA(q: "a", a: "1")])
        XCTAssertTrue(d2.questions.isEmpty)
        XCTAssertTrue(d2.ready)
    }

    func testShortlistKeepsHitsAndPopular() {
        let many = (0..<300).map { SupportService(id: "\($0)", name: "s\($0)", displayName: "Service \($0)", resourceTypes: []) } + services
        let s = AIDrafter.shortlist("gpt 部署 429", many)
        XCTAssertTrue(s.contains { $0.name == "svc-aoai" })
        XCTAssertLessThanOrEqual(s.count, 60)
    }

    func testProblemShortlistCapsAndRanks() {
        let many = (0..<200).map { ProblemClassification(id: "/p/\($0)", displayName: "Category \($0) / Item") }
            + [ProblemClassification(id: "/p/ssh", displayName: "Cannot connect to my VM / RDP or SSH port")]
        let s = AIDrafter.problemShortlist("linux vm ssh 连接失败", many)
        XCTAssertLessThanOrEqual(s.count, 40)
        XCTAssertEqual(s.first?.id, "/p/ssh")
    }

    func testAIConfigDefaults() {
        let c = AIConfig()
        XCTAssertEqual(c.chatDeployment, "gpt-6-luna")
        XCTAssertTrue(c.chatEndpoint.contains("westus3"))
        XCTAssertEqual(c.sttProvider, .azureOpenAI)
    }

    func testRequestRoutingAndKeyHeaders() {
        var c = AIConfig()
        XCTAssertTrue(c.authHeader(.chat, key: nil).isEmpty)
        XCTAssertEqual(c.authHeader(.chat, key: "k"), ["api-key": "k"])
        XCTAssertEqual(c.authHeader(.stt, key: "t"), ["api-key": "t"])
        c.sttProvider = .azureSpeech; c.speechRegion = "southeastasia"
        XCTAssertEqual(c.authHeader(.stt, key: "s"), ["Ocp-Apim-Subscription-Key": "s"])
        XCTAssertEqual(c.sttURL, "https://southeastasia.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2024-11-15")
        c.speechRegion = ""
        XCTAssertTrue(c.sttURL.hasPrefix(c.sttEndpoint + "/speechtotext"))
    }

    func testConfigDecodesOldSavedSettings() throws {
        // v2.0.0 保存的设置无 speechRegion 字段, 升级后仍可读取
        let old = #"{"chatEndpoint":"https://x","chatDeployment":"d","sttProvider":"azure-openai","sttEndpoint":"https://y","sttDeployment":"t","sttLanguage":"zh-CN"}"#
        let c = try JSONDecoder().decode(AIConfig.self, from: Data(old.utf8))
        XCTAssertEqual(c.chatDeployment, "d")
        XCTAssertEqual(c.speechRegion, "")
    }

    func testPlanTier() {
        XCTAssertEqual(SupportPlan.tier("Unified Enterprise"), "unified")
        XCTAssertEqual(SupportPlan.tier("Azure Support Plan - Internal"), "internal")
        XCTAssertEqual(SupportPlan.infer(tickets: [], quotaId: "MSDN_2014-09-01").allowsCritical, false)
    }
}
