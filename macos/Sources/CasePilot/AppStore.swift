import Foundation
import Observation

@MainActor @Observable
final class AppStore {
    let auth = AuthProvider()
    var client: SupportClient { SupportClient(auth: auth) }

    // 身份与范围
    var identity: AuthProvider.Identity?
    var tenants: [Tenant] = []
    var subscriptions: [Subscription] = []
    var tenantId: String? { didSet { if tenantId != oldValue { Task { await onTenant() } } } }
    var subscriptionId: String = "" { didSet { if subscriptionId != oldValue { Task { await onSubscription() } } } }
    var services: [SupportService] = []
    var supportPlan: String?

    // 历史
    var tickets: [TicketSummary] = []
    var historyDays = 30
    var loadingTickets = false

    var banner: String?
    private let defaults = UserDefaults.standard
    var sub: String? { subscriptionId.isEmpty ? nil : subscriptionId }

    // MARK: 登录

    func loginCLI() async throws {
        identity = try await auth.useCLI()
        await afterLogin()
    }

    func loginDevice(_ code: AuthProvider.DeviceCode) async throws {
        identity = try await auth.pollDevice(code)
        await afterLogin()
    }

    func logout() async {
        await auth.logout()
        identity = nil; tenants = []; subscriptions = []; tickets = []; services = []
    }

    private func afterLogin() async {
        tenants = (try? await client.tenants()) ?? []
        if tenants.isEmpty, let t = identity?.tenant { tenants = [Tenant(id: t, name: t, domain: "")] }
        let saved = defaults.string(forKey: "tenant")
        tenantId = tenants.first { $0.id == saved }?.id ?? tenants.first { $0.id == identity?.tenant }?.id ?? tenants.first?.id
    }

    private func onTenant() async {
        guard let t = tenantId else { return }
        defaults.set(t, forKey: "tenant")
        subscriptions = []; services = []
        do { subscriptions = try await client.subscriptions(tenant: t) } catch { banner = "加载订阅失败: \(error.localizedDescription)" }
        let saved = defaults.string(forKey: "sub.\(t)")
        let pick = saved.flatMap { s in s.isEmpty || subscriptions.contains { $0.id == s } ? s : nil } ?? subscriptions.first?.id ?? ""
        if pick == subscriptionId { await onSubscription() } else { subscriptionId = pick }
        do { services = try await client.services(tenant: t) } catch { banner = "加载服务失败: \(error.localizedDescription)" }
    }

    private func onSubscription() async {
        guard let t = tenantId else { return }
        defaults.set(subscriptionId, forKey: "sub.\(t)")
        await loadTickets()
    }

    func loadTickets() async {
        guard let t = tenantId else { return }
        loadingTickets = true
        defer { loadingTickets = false }
        do {
            tickets = try await client.tickets(tenant: t, sub: sub, days: historyDays)
            // 无直接查询支持计划的 API: 取最近 Case 的 supportPlanType 推断
            if let p = tickets.compactMap(\.plan).first { supportPlan = p }
            else if historyDays < 365, let p = try? await client.tickets(tenant: t, sub: sub, days: 365).compactMap(\.plan).first { supportPlan = p }
            else { supportPlan = nil }
        } catch { banner = "加载 Case 失败: \(error.localizedDescription)" }
    }

    var openCount: Int { tickets.filter(\.isOpen).count }

    // MARK: 联系人 (默认中文 / 中国, 本地记忆)

    var contact: Contact {
        get { (defaults.data(forKey: "contact")).flatMap { try? JSONDecoder().decode(Contact.self, from: $0) } ?? Contact() }
        set { defaults.set(try? JSONEncoder().encode(newValue), forKey: "contact") }
    }

    var recentServices: [String] {
        get { defaults.stringArray(forKey: "recentSvc") ?? [] }
        set { defaults.set(Array(newValue.prefix(4)), forKey: "recentSvc") }
    }

    func portalURL(for name: String) -> URL? {
        let rid = "\(sub.map { "/subscriptions/\($0)" } ?? "")/providers/Microsoft.Support/supportTickets/\(name)"
        let enc = rid.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? rid
        return URL(string: "\(identity?.cloud.portal ?? AzureCloud.global.portal)/#view/Microsoft_Azure_Support/SupportRequestDetails.ReactView/id/\(enc)")
    }
}
