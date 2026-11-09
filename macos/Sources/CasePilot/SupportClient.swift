import Foundation

/// ARM + Microsoft.Support REST 客户端 (api-version 2024-04-01)
struct SupportClient {
    static let apiVersion = "2024-04-01"
    let auth: AuthProvider

    // MARK: 底层请求

    func request(_ method: String, _ path: String, tenant: String?, body: Any? = nil) async throws -> (Any, HTTPURLResponse) {
        let base = await auth.cloud.arm
        let url = URL(string: path.hasPrefix("http") ? path : base + path)!
        var req = URLRequest(url: url, timeoutInterval: 60)
        req.httpMethod = method
        req.setValue("Bearer " + (try await auth.token(tenant: tenant)), forHTTPHeaderField: "Authorization")
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue(UUID().uuidString, forHTTPHeaderField: "x-ms-client-request-id")
        if let body { req.httpBody = try JSONSerialization.data(withJSONObject: body) }
        let (data, resp) = try await URLSession.shared.data(for: req)
        let http = resp as! HTTPURLResponse
        let json = (try? JSONSerialization.jsonObject(with: data)) ?? [:]
        if http.statusCode >= 400 {
            let err = (json as? [String: Any])?["error"] as? [String: Any] ?? [:]
            throw AppError(message: "\(err["code"] as? String ?? "HTTP \(http.statusCode)"): \(err["message"] as? String ?? "")",
                           requestId: http.value(forHTTPHeaderField: "x-ms-request-id"))
        }
        return (json, http)
    }

    func get(_ path: String, tenant: String?) async throws -> [String: Any] {
        try await request("GET", path, tenant: tenant).0 as? [String: Any] ?? [:]
    }

    func list(_ path: String, tenant: String?) async throws -> [[String: Any]] {
        var out: [[String: Any]] = [], next: String? = path
        while let p = next {
            let page = try await get(p, tenant: tenant)
            out += page["value"] as? [[String: Any]] ?? []
            next = page["nextLink"] as? String
        }
        return out
    }

    static func base(_ sub: String?) -> String {
        if let sub, !sub.isEmpty { return "/subscriptions/\(sub)/providers/Microsoft.Support" }
        return "/providers/Microsoft.Support"
    }
    private var v: String { "api-version=\(Self.apiVersion)" }

    // MARK: 范围

    func tenants() async throws -> [Tenant] {
        try await list("/tenants?api-version=2022-12-01", tenant: nil).map {
            let id = $0["tenantId"] as? String ?? ""
            return Tenant(id: id, name: $0["displayName"] as? String ?? id, domain: $0["defaultDomain"] as? String ?? "")
        }
    }

    func subscriptions(tenant: String) async throws -> [Subscription] {
        try await list("/subscriptions?api-version=2022-12-01", tenant: tenant)
            .filter { ($0["tenantId"] as? String ?? tenant) == tenant }
            .map { Subscription(id: $0["subscriptionId"] as? String ?? "", name: $0["displayName"] as? String ?? "", state: $0["state"] as? String ?? "") }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    /// 订阅 quotaId (用于推断支持计划)
    func quotaId(tenant: String, sub: String) async throws -> String {
        ((try await get("/subscriptions/\(sub)?api-version=2022-12-01", tenant: tenant))["subscriptionPolicies"] as? [String: Any])?["quotaId"] as? String ?? ""
    }

    // MARK: 服务目录

    func services(tenant: String) async throws -> [SupportService] {
        try await list("/providers/Microsoft.Support/services?\(v)", tenant: tenant).map {
            let p = $0["properties"] as? [String: Any] ?? [:]
            return SupportService(id: $0["id"] as? String ?? "", name: $0["name"] as? String ?? "",
                                  displayName: p["displayName"] as? String ?? "", resourceTypes: p["resourceTypes"] as? [String] ?? [])
        }.sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
    }

    func problems(tenant: String, service: SupportService) async throws -> [ProblemClassification] {
        try await list("/providers/Microsoft.Support/services/\(service.name)/problemClassifications?\(v)", tenant: tenant).map {
            ProblemClassification(id: $0["id"] as? String ?? "", displayName: ($0["properties"] as? [String: Any])?["displayName"] as? String ?? "")
        }.sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending }
    }

    func resources(tenant: String, sub: String, types: [String]? = nil) async throws -> [AzureResource] {
        var path = "/subscriptions/\(sub)/resources?api-version=2021-04-01"
        if let types, !types.isEmpty {
            let f = types.prefix(15).map { "resourceType eq '\($0)'" }.joined(separator: " or ")
            path += "&$filter=" + f.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed)!
        }
        return try await list(path, tenant: tenant).map {
            AzureResource(id: $0["id"] as? String ?? "", name: $0["name"] as? String ?? "",
                          type: ($0["type"] as? String ?? "").lowercased(), location: $0["location"] as? String ?? "")
        }.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    // MARK: Case

    static func buildBody(_ c: NewCase) -> [String: Any] {
        var contact: [String: Any] = [
            "firstName": c.contact.firstName, "lastName": c.contact.lastName, "primaryEmailAddress": c.contact.email,
            "preferredContactMethod": c.contact.preferPhone ? "phone" : "email",
            "preferredTimeZone": c.contact.timeZone, "preferredSupportLanguage": c.contact.language, "country": c.contact.country,
        ]
        if !c.contact.phone.isEmpty { contact["phoneNumber"] = c.contact.phone }
        let cc = c.contact.cc.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        if !cc.isEmpty { contact["additionalEmailAddresses"] = cc }
        var p: [String: Any] = [
            "title": c.title.trimmingCharacters(in: .whitespacesAndNewlines),
            "description": c.description.trimmingCharacters(in: .whitespacesAndNewlines),
            "serviceId": c.service?.id ?? "", "problemClassificationId": c.problem?.id ?? "",
            "severity": c.severity.rawValue, "require24X7Response": c.severity.isHigh && c.require24x7,
            "advancedDiagnosticConsent": c.consent ? "Yes" : "No", "contactDetails": contact,
        ]
        if let t = c.startTime { p["problemStartTime"] = ISO8601DateFormatter().string(from: t) }
        if let r = c.resourceId, !r.isEmpty { p["technicalTicketDetails"] = ["resourceId": r] }
        if let q = c.quota {
            let payload = try! JSONSerialization.data(withJSONObject: ["VMFamily": q.vmFamily, "NewLimit": Int(q.newLimit) ?? 0], options: [.sortedKeys])
            p["quotaTicketDetails"] = ["quotaChangeRequestVersion": "1.0",
                                       "quotaChangeRequests": [["region": q.region, "payload": String(data: payload, encoding: .utf8)!]]]
        }
        return ["properties": p]
    }

    func create(_ c: NewCase, tenant: String, sub: String?) async throws -> TicketSummary {
        let name = "casepilot-" + UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: "").prefix(12)
        let path = "\(Self.base(sub))/supportTickets/\(name)?\(v)"
        let (_, resp) = try await request("PUT", path, tenant: tenant, body: Self.buildBody(c))
        if let op = resp.value(forHTTPHeaderField: "Azure-AsyncOperation") {
            for _ in 0..<40 {
                let st = (try await get(op, tenant: tenant)["status"] as? String ?? "").lowercased()
                if st == "succeeded" { break }
                if st == "failed" || st == "canceled" { throw AppError(message: "创建失败 (\(st))") }
                try await Task.sleep(for: .seconds(3))
            }
        }
        return Self.summary(try await get(path, tenant: tenant))
    }

    func tickets(tenant: String, sub: String?, days: Int) async throws -> [TicketSummary] {
        let since = ISO8601DateFormatter().string(from: Date().addingTimeInterval(-Double(days) * 86400))
        let filter = "createdDate ge \(since)".addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed)!
        return try await list("\(Self.base(sub))/supportTickets?\(v)&$top=100&$filter=\(filter)", tenant: tenant)
            .map(Self.summary).sorted { ($0.created ?? .distantPast) > ($1.created ?? .distantPast) }
    }

    func detail(_ name: String, tenant: String, sub: String?) async throws -> TicketDetail {
        let base = "\(Self.base(sub))/supportTickets/\(name)"
        let t = try await get("\(base)?\(v)", tenant: tenant)
        let comms = (try? await list("\(base)/communications?\(v)", tenant: tenant)) ?? []
        return TicketDetail(summary: Self.summary(t), props: t["properties"] as? [String: Any] ?? [:],
                            communications: comms.map { c in
                                let p = c["properties"] as? [String: Any] ?? [:]
                                return Communication(id: c["name"] as? String ?? UUID().uuidString, sender: p["sender"] as? String ?? "",
                                                     subject: p["subject"] as? String ?? "", body: HTMLText.plain(p["body"] as? String ?? ""),
                                                     fromMicrosoft: (p["communicationDirection"] as? String) == "Outbound",
                                                     created: Self.date(p["createdDate"]))
                            }.sorted { ($0.created ?? .distantPast) > ($1.created ?? .distantPast) })
    }

    func reply(_ name: String, text: String, tenant: String, sub: String?, sender: String? = nil) async throws {
        let html = text.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\n", with: "<br>")
        let cname = "cp-" + UUID().uuidString.lowercased().prefix(12)
        _ = try await request("PUT", "\(Self.base(sub))/supportTickets/\(name)/communications/\(cname)?\(v)", tenant: tenant,
                              body: ["properties": ["subject": "客户补充信息 / Update from customer", "body": html].merging(sender.map { ["sender": $0] } ?? [:]) { a, _ in a }])
    }

    /// 关闭后回读确认: PATCH 成功不代表状态已生效
    func close(_ name: String, tenant: String, sub: String?) async throws {
        let path = "\(Self.base(sub))/supportTickets/\(name)?\(v)"
        var status = ""
        for _ in 0..<4 {
            do { _ = try await request("PATCH", path, tenant: tenant, body: ["status": "closed"]) }
            catch let e as AppError where e.message.contains("redundant action") || e.message.contains("currently Closed") {}
            try await Task.sleep(for: .seconds(3))
            status = (try await get(path, tenant: tenant)["properties"] as? [String: Any])?["status"] as? String ?? ""
            if status.lowercased() == "closed" { return }
            try await Task.sleep(for: .seconds(5))
        }
        throw AppError(message: "关闭请求已发送, 但 Case 当前状态仍为 \(status), 请稍后重试")
    }

    // MARK: 解析

    static func summary(_ t: [String: Any]) -> TicketSummary {
        let p = t["properties"] as? [String: Any] ?? [:]
        return TicketSummary(id: t["name"] as? String ?? "", ticketNumber: p["supportTicketId"] as? String ?? "",
                             title: p["title"] as? String ?? "", status: p["status"] as? String ?? "",
                             severity: p["severity"] as? String ?? "", created: date(p["createdDate"]),
                             service: p["serviceDisplayName"] as? String ?? "", problem: p["problemClassificationDisplayName"] as? String ?? "",
                             plan: p["supportPlanType"] as? String)
    }

    static func date(_ v: Any?) -> Date? {
        guard let s = v as? String else { return nil }
        let f = ISO8601DateFormatter()
        if let d = f.date(from: s) { return d }
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f.date(from: s)
    }
}

enum HTMLText {
    /// 邮件 HTML → 纯文本 (从不渲染外部 HTML)
    static func plain(_ html: String) -> String {
        var s = html
        for (pat, rep) in [("(?is)<(style|script|head)[^>]*>.*?</\\1>", ""), ("(?i)<br\\s*/?>", "\n"),
                           ("(?i)</(p|div|li|tr|h[1-6])>", "\n"), ("<[^>]+>", "")] {
            s = s.replacingOccurrences(of: pat, with: rep, options: .regularExpression)
        }
        for (e, c) in [("&nbsp;", " "), ("&lt;", "<"), ("&gt;", ">"), ("&quot;", "\""), ("&#39;", "'"), ("&amp;", "&")] {
            s = s.replacingOccurrences(of: e, with: c)
        }
        s = s.replacingOccurrences(of: "[ \\t\\u00a0]+\\n", with: "\n", options: .regularExpression)
        s = s.replacingOccurrences(of: "\\n\\s*\\n+", with: "\n\n", options: .regularExpression)
        return s.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
