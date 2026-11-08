import Foundation

enum AzureCloud: String, CaseIterable, Identifiable, Codable {
    case global = "AzureCloud", china = "AzureChinaCloud", usgov = "AzureUSGovernment"
    var id: String { rawValue }
    var label: String {
        switch self { case .global: "Azure Global"; case .china: "Azure China (世纪互联)"; case .usgov: "Azure US Government" }
    }
    var arm: String {
        switch self {
        case .global: "https://management.azure.com"
        case .china: "https://management.chinacloudapi.cn"
        case .usgov: "https://management.usgovcloudapi.net"
        }
    }
    var login: String {
        switch self {
        case .global: "https://login.microsoftonline.com"
        case .china: "https://login.chinacloudapi.cn"
        case .usgov: "https://login.microsoftonline.us"
        }
    }
    var portal: String {
        switch self { case .global: "https://portal.azure.com"; case .china: "https://portal.azure.cn"; case .usgov: "https://portal.azure.us" }
    }
}

struct Tenant: Identifiable, Hashable { let id: String; let name: String; let domain: String }
struct Subscription: Identifiable, Hashable { let id: String; let name: String; let state: String }

struct SupportService: Identifiable, Hashable {
    let id: String          // 完整资源 ID
    let name: String        // GUID
    let displayName: String
    let resourceTypes: [String]
}
struct ProblemClassification: Identifiable, Hashable { let id: String; let displayName: String }
struct AzureResource: Identifiable, Hashable { let id: String; let name: String; let type: String; let location: String }

enum Severity: String, CaseIterable, Identifiable {
    case minimal, moderate, critical, highestcriticalimpact
    var id: String { rawValue }
    var level: String { ["minimal": "C", "moderate": "B", "critical": "A", "highestcriticalimpact": "A+"][rawValue]! }
    var title: String { ["minimal": "最小影响", "moderate": "中等影响", "critical": "严重影响", "highestcriticalimpact": "业务关键"][rawValue]! }
    var detail: String {
        ["minimal": "功能受损但不影响业务", "moderate": "生产受影响, 有变通方案",
         "critical": "生产中断 · 需 Standard+", "highestcriticalimpact": "全面中断 · 需 ProDirect/Unified"][rawValue]!
    }
    var isHigh: Bool { self == .critical || self == .highestcriticalimpact }
    init?(api: String?) { self.init(rawValue: (api ?? "").lowercased()) }
}

enum CaseKind: String, CaseIterable, Identifiable {
    case technical, quota, billing
    var id: String { rawValue }
    var label: String { ["technical": "技术问题", "quota": "配额申请", "billing": "计费 / 订阅"][rawValue]! }
    /// 非技术类自动匹配的服务名
    func matches(_ s: SupportService) -> Bool {
        let n = s.displayName.lowercased()
        switch self {
        case .technical: return false
        case .quota: return n.contains("service and subscription limits")
        case .billing: return n == "billing"
        }
    }
}

struct Contact: Codable, Equatable {
    var firstName = "", lastName = "", email = "", cc = "", phone = ""
    var preferPhone = false
    var language = "zh-hans", country = "CHN", timeZone = "China Standard Time"

    static let languages = [("zh-hans", "简体中文"), ("en-us", "English"), ("zh-hant", "繁體中文"), ("ja-jp", "日本語"), ("ko-kr", "한국어")]
    static let countries = [("CHN", "中国"), ("HKG", "中国香港"), ("MAC", "中国澳门"), ("TWN", "中国台湾"), ("SGP", "新加坡"),
                            ("JPN", "日本"), ("KOR", "韩国"), ("USA", "美国"), ("GBR", "英国"), ("DEU", "德国"), ("AUS", "澳大利亚")]
    static let timeZones = ["China Standard Time", "Taipei Standard Time", "Tokyo Standard Time", "Korea Standard Time",
                            "Singapore Standard Time", "India Standard Time", "AUS Eastern Standard Time", "GMT Standard Time",
                            "W. Europe Standard Time", "Eastern Standard Time", "Central Standard Time", "Pacific Standard Time", "UTC"]
}

struct QuotaRequest { var region = "", vmFamily = "", newLimit = "" }

struct NewCase {
    var service: SupportService?
    var problem: ProblemClassification?
    var resourceId: String?
    var title = "", description = ""
    var severity: Severity = .moderate
    var require24x7 = false
    var consent = true
    var startTime: Date?
    var quota: QuotaRequest?
    var contact = Contact()
}

struct TicketSummary: Identifiable, Hashable {
    let id: String          // ARM name
    let ticketNumber: String
    let title: String
    let status: String
    let severity: String
    let created: Date?
    let service: String
    let problem: String
    let plan: String?
    var isOpen: Bool { status.lowercased() == "open" }
}

struct Communication: Identifiable, Hashable {
    let id: String
    let sender: String, subject: String, body: String
    let fromMicrosoft: Bool
    let created: Date?
}

struct TicketDetail {
    let summary: TicketSummary
    let props: [String: Any]
    let communications: [Communication]
}

struct RecentProblem: Codable, Hashable, Identifiable {
    let serviceName: String, serviceDisplayName: String, problemId: String, problemDisplayName: String
    var id: String { problemId }
    var label: String { "\(serviceDisplayName) · \(problemDisplayName.split(separator: "/").last.map { $0.trimmingCharacters(in: .whitespaces) } ?? problemDisplayName)" }
}
