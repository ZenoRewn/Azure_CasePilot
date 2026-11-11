import Foundation

// MARK: - 配置 (保存在本机 UserDefaults)

enum STTProvider: String, Codable, CaseIterable, Identifiable {
    case azureOpenAI = "azure-openai", azureSpeech = "azure-speech"
    var id: String { rawValue }
    var label: String { self == .azureOpenAI ? "Azure OpenAI (gpt-live-transcribe 流式)" : "Azure Speech (实时流式 + PSR)" }
}

enum AIAuth: String, Codable, CaseIterable, Identifiable {
    case entra, key
    var id: String { rawValue }
}

struct AIConfig: Codable, Equatable {
    var chatEndpoint = "https://westus3-azureai-service-507154.openai.azure.com"
    var chatDeployment = "gpt-6-luna"
    var sttProvider: STTProvider = .azureOpenAI
    var sttEndpoint = "https://westus3-azureai-service-507154.openai.azure.com"
    var sttDeployment = "gpt-4o-mini-transcribe"       // 整段转写 (流式失败时兜底)
    var sttStreamDeployment = "gpt-live-transcribe"    // 流式转写
    var sttLanguage = "zh-CN"
    var speechRegion = ""      // 设置后 Speech 使用区域端点
    /// 认证方式; nil 表示旧版设置, 由 migrated() 按钥匙串中是否有 Key 推断
    var chatAuthSaved: AIAuth?
    var sttAuthSaved: AIAuth?
    var chatAuth: AIAuth { get { chatAuthSaved ?? .entra } set { chatAuthSaved = newValue } }
    var sttAuth: AIAuth { get { sttAuthSaved ?? .entra } set { sttAuthSaved = newValue } }

    enum CodingKeys: String, CodingKey {
        case chatEndpoint, chatDeployment, sttProvider, sttEndpoint, sttDeployment, sttStreamDeployment, sttLanguage, speechRegion
        case chatAuthSaved = "chatAuth", sttAuthSaved = "sttAuth"
    }

    enum Kind { case chat, stt }

    init() {}
    init(from d: Decoder) throws {   // 兼容旧版本保存的设置 (缺字段时取默认)
        let c = try d.container(keyedBy: CodingKeys.self), z = AIConfig()
        chatEndpoint = try c.decodeIfPresent(String.self, forKey: .chatEndpoint) ?? z.chatEndpoint
        chatDeployment = try c.decodeIfPresent(String.self, forKey: .chatDeployment) ?? z.chatDeployment
        sttProvider = try c.decodeIfPresent(STTProvider.self, forKey: .sttProvider) ?? z.sttProvider
        sttEndpoint = try c.decodeIfPresent(String.self, forKey: .sttEndpoint) ?? z.sttEndpoint
        sttDeployment = try c.decodeIfPresent(String.self, forKey: .sttDeployment) ?? z.sttDeployment
        sttStreamDeployment = try c.decodeIfPresent(String.self, forKey: .sttStreamDeployment) ?? z.sttStreamDeployment
        sttLanguage = try c.decodeIfPresent(String.self, forKey: .sttLanguage) ?? z.sttLanguage
        speechRegion = try c.decodeIfPresent(String.self, forKey: .speechRegion) ?? ""
        chatAuthSaved = try c.decodeIfPresent(AIAuth.self, forKey: .chatAuthSaved)
        sttAuthSaved = try c.decodeIfPresent(AIAuth.self, forKey: .sttAuthSaved)
    }

    /// 旧版没有认证方式字段: 钥匙串里已有 Key 则视为 Key 认证, 保持升级前行为
    func migrated(hasChatKey: Bool, hasSttKey: Bool) -> AIConfig {
        var c = self
        if c.chatAuthSaved == nil { c.chatAuthSaved = hasChatKey ? .key : .entra }
        if c.sttAuthSaved == nil { c.sttAuthSaved = hasSttKey ? .key : .entra }
        return c
    }

    /// 选择 Key 认证且 Key 非空时返回 Key; 登录身份时忽略已存的 Key (不删除, 切回可恢复)
    func key(_ kind: Kind, stored: String?) -> String? {
        guard (kind == .chat ? chatAuth : sttAuth) == .key, let stored, !stored.isEmpty else { return nil }
        return stored
    }

    /// key 为空 → 使用 Entra 令牌
    func authHeader(_ kind: Kind, key stored: String?) -> [String: String] {
        guard let key = key(kind, stored: stored) else { return [:] }
        return kind == .stt && sttProvider == .azureSpeech ? ["Ocp-Apim-Subscription-Key": key] : ["api-key": key]
    }

    var sttURL: String {
        if sttProvider == .azureSpeech {
            let base = speechRegion.isEmpty ? sttEndpoint : "https://\(speechRegion).api.cognitive.microsoft.com"
            return "\(base)/speechtotext/transcriptions:transcribe?api-version=2024-11-15"
        }
        return "\(sttEndpoint)/openai/deployments/\(sttDeployment)/audio/transcriptions?api-version=2025-04-01-preview"
    }

    static func load() -> AIConfig {
        (UserDefaults.standard.data(forKey: "aiConfig").flatMap { try? JSONDecoder().decode(AIConfig.self, from: $0) } ?? AIConfig())
            .migrated(hasChatKey: !(SecretStore.get(.aoai) ?? "").isEmpty, hasSttKey: !(SecretStore.get(.stt) ?? "").isEmpty)
    }
    func save() { UserDefaults.standard.set(try? JSONEncoder().encode(self), forKey: "aiConfig") }
    var trimmed: AIConfig {
        var c = self
        c.chatEndpoint = c.chatEndpoint.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        c.sttEndpoint = c.sttEndpoint.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return c
    }
}

// MARK: - 起草 (与服务端 casepilot/ai.py 同一规则)

struct QA: Hashable { var q: String; var a: String }

struct AIDraft {
    var service: SupportService?
    var problem: ProblemClassification?
    var severity: Severity
    var title: String
    var description: String
    var questions: [String]
    var ready: Bool { service != nil && problem != nil && questions.isEmpty }
}

enum AIDrafter {
    typealias Chat = (_ messages: [[String: String]], _ schema: [String: Any]) async throws -> [String: Any]
    static let maxQuestions = 3

    static let system = """
    你是 Azure 技术支持工单助手。根据用户描述起草一张 Azure Support Case。
    规则:
    - service 必须是给定服务目录中的 name, 选最贴切的一个。
    - severity: minimal=不影响业务, moderate=生产受影响但有变通, critical=生产中断, highestcriticalimpact=关键业务全面中断。拿不准时用 moderate。
    - title: 一句话, 包含服务、区域/资源 (若已知) 与核心现象, 中文, 不超过 80 字。
    - description 使用以下结构, 只写用户提供或可直接推断的事实, 未知项写"待补充":
      【现象】【业务影响】【首次发生时间 (含时区)】【错误信息 / Request ID】【已尝试的排查】
    - questions: 仅当缺少会显著影响排查的关键信息时提问 (最多 3 个, 简短中文, 合并相关项); 信息足够或已有补充问答时返回空数组。
    - 不要编造 Request ID、资源名或时间。
    """

    static let serviceSchema: [String: Any] = [
        "type": "object", "additionalProperties": false,
        "required": ["service", "severity", "title", "description", "questions"],
        "properties": ["service": ["type": "string"], "severity": ["type": "string", "enum": Severity.allCases.map(\.rawValue)],
                       "title": ["type": "string"], "description": ["type": "string"],
                       "questions": ["type": "array", "items": ["type": "string"]]],
    ]
    static let problemSchema: [String: Any] = [
        "type": "object", "additionalProperties": false, "required": ["problemId"], "properties": ["problemId": ["type": "string"]],
    ]

    static let popular: Set<String> = ["Azure OpenAI", "Microsoft Foundry", "Foundry Tools", "Virtual Machine running Linux",
        "Virtual Machine running Windows", "Kubernetes Service (AKS)", "App Service", "Storage Account Management",
        "Azure SQL Database", "Service and subscription limits (quotas)", "Billing", "Subscription management"]
    static let synonyms = ["aoai": "openai", "gpt": "openai", "模型": "openai", "foundry": "foundry", "虚拟机": "virtual machine",
        "vm": "virtual machine", "ssh": "virtual machine", "rdp": "virtual machine", "k8s": "kubernetes", "aks": "kubernetes",
        "存储": "storage", "配额": "quota", "quota": "quota", "账单": "billing", "发票": "billing", "数据库": "sql", "网站": "app service"]

    /// 控制 token: 只发送关键词命中与常用服务 (完整目录 ~350 项会触发 TPM 限流)
    static func shortlist(_ text: String, _ services: [SupportService], limit: Int = 60) -> [SupportService] {
        let t = text.lowercased()
        var keys = Set(t.split(whereSeparator: { $0 == " " || $0 == "/" }).map(String.init).filter { $0.count > 2 })
        synonyms.filter { t.contains($0.key) }.forEach { keys.insert($0.value) }
        let hits = services.filter { s in keys.contains { s.displayName.lowercased().contains($0) } }
        var seen = Set<String>()
        return Array((hits + services.filter { popular.contains($0.displayName) }).filter { seen.insert($0.name).inserted }.prefix(limit))
    }

    /// 问题类型可达 200 项 (约 10k token): 按与描述的词重叠排序后截断
    static func problemShortlist(_ text: String, _ problems: [ProblemClassification], limit: Int = 40) -> [ProblemClassification] {
        let t = text.lowercased()
        var keys = Set(t.split(whereSeparator: { !$0.isLetter && !$0.isNumber && $0 != "." }).map(String.init).filter { $0.count > 1 && $0.allSatisfy(\.isASCII) })
        synonyms.filter { t.contains($0.key) }.forEach { keys.insert($0.value); keys.insert($0.key) }
        func score(_ p: ProblemClassification) -> Int { keys.filter { p.displayName.lowercased().contains($0) }.count }
        return Array(problems.enumerated().sorted { (score($0.element), -$0.offset) > (score($1.element), -$1.offset) }.map(\.element).prefix(limit))
    }

    static func draft(text: String, answers: [QA], services: [SupportService],
                      problems: (SupportService) async throws -> [ProblemClassification], chat: Chat) async throws -> AIDraft {
        let qa = answers.map { "问: \($0.q)\n答: \($0.a)" }.joined(separator: "\n")
        let catalog = shortlist(text + " " + qa, services).map { "\($0.name)\t\($0.displayName)" }.joined(separator: "\n")
        let user = "用户描述:\n\(text)\n\n" + (qa.isEmpty ? "" : "补充问答:\n\(qa)\n\n") + "服务目录 (name\\tdisplayName):\n\(catalog)"
        let r = try await chat([["role": "system", "content": system], ["role": "user", "content": user]], serviceSchema)

        let pick = (r["service"] as? String ?? "").trimmingCharacters(in: .whitespaces).lowercased()
        let svc = services.first { $0.name.lowercased() == pick || $0.displayName.lowercased() == pick }
        var problem: ProblemClassification?
        if let svc {
            let ps = problemShortlist("\(text) \(qa) \(r["title"] as? String ?? "") \(r["description"] as? String ?? "")", try await problems(svc))
            let p = try await chat([["role": "system", "content": "从问题类型目录中选出最匹配的一项, 返回其 id。"],
                                    ["role": "user", "content": "工单: \(r["title"] as? String ?? "")\n\(r["description"] as? String ?? "")\n\n问题类型目录 (id\\tdisplayName):\n"
                                     + ps.map { "\($0.id)\t\($0.displayName)" }.joined(separator: "\n")]], problemSchema)
            let pid = (p["problemId"] as? String ?? "").trimmingCharacters(in: .whitespaces).lowercased()
            problem = ps.first { $0.id.lowercased() == pid || $0.displayName.lowercased() == pid }
        }
        // 只追问一轮: 回答后剩余未知项在描述中标注"待补充"
        let qs = answers.isEmpty ? Array((r["questions"] as? [String] ?? []).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }.prefix(maxQuestions)) : []
        return AIDraft(service: svc, problem: problem, severity: Severity(api: r["severity"] as? String) ?? .moderate,
                       title: String((r["title"] as? String ?? "").prefix(200)), description: r["description"] as? String ?? "", questions: qs)
    }
}

// MARK: - 支持计划 (与服务端 casepilot/plan.py 同一规则)

struct SupportPlan: Equatable {
    var name: String?
    var tier: String
    var source: String          // ticket | offer | none
    var allowsCritical: Bool?

    static let tiers = [("unified", "unified"), ("premier", "premier"), ("prodirect", "prodirect"), ("professional direct", "prodirect"),
                        ("internal", "internal"), ("standard", "standard"), ("developer", "developer-or-basic"), ("basic", "developer-or-basic")]
    static let high: Set<String> = ["unified", "premier", "prodirect", "internal", "standard"]
    static let noPaid = ["MSDN", "FreeTrial", "AzurePass", "Sponsored", "Students", "DreamSpark", "AzureForStudents"]

    static func tier(_ name: String?) -> String {
        let n = (name ?? "").lowercased()
        return tiers.first { n.contains($0.0) }?.1 ?? "unknown"
    }

    static func infer(tickets: [TicketSummary], quotaId: String) -> SupportPlan {
        if let p = tickets.sorted(by: { ($0.created ?? .distantPast) > ($1.created ?? .distantPast) }).compactMap(\.plan).first {
            let t = tier(p)
            return SupportPlan(name: p, tier: t, source: "ticket", allowsCritical: t == "unknown" ? nil : high.contains(t))
        }
        if noPaid.contains(where: quotaId.hasPrefix) {
            return SupportPlan(name: "Developer / Basic (按订阅类型推断)", tier: "developer-or-basic", source: "offer", allowsCritical: false)
        }
        return SupportPlan(name: nil, tier: "unknown", source: "none", allowsCritical: nil)
    }

    var sourceLabel: String { ["ticket": "根据该订阅最近的 Case 识别", "offer": "根据订阅类型推断"][source] ?? "" }
}
