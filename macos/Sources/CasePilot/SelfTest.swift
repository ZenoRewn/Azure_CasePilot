#if DEBUG
import SwiftUI

/// 调试自检: CASEPILOT_SELFTEST=1 走真实 Azure 流程 (创建→补充→关闭); CASEPILOT_SNAPSHOT=<dir> 渲染界面截图
enum SelfTest {
    @MainActor static func runIfRequested() {
        let env = ProcessInfo.processInfo.environment
        if let dir = env["CASEPILOT_SNAPSHOT"] { Task { await snapshot(dir: dir, live: env["CASEPILOT_SELFTEST"] == "1"); exit(0) } }
        else if env["CASEPILOT_SELFTEST"] == "1" { Task { await e2e(); exit(0) } }
        else if let f = env["CASEPILOT_SP_TEST"] { Task { await spTest(f); exit(0) } }
    }

    @MainActor static func e2e() async {
        let store = AppStore()
        func log(_ s: String) { print("[selftest] " + s); fflush(stdout) }
        do {
            try await store.loginCLI(); log("login \(store.identity?.user ?? "")")
            while store.services.isEmpty || store.subscriptionId.isEmpty { try await Task.sleep(for: .milliseconds(300)) }
            let t = store.tenantId!, sub = store.sub
            log("tenant=\(t) sub=\(sub ?? "-") services=\(store.services.count) plan=\(store.supportPlan ?? "-") tickets=\(store.tickets.count)")
            let svc = store.services.first { $0.displayName == "Virtual Machine running Linux" }!
            let pcs = try await store.client.problems(tenant: t, service: svc)
            let res = try await store.client.resources(tenant: t, sub: sub!, types: svc.resourceTypes)
            log("problems=\(pcs.count) resources=\(res.map(\.name))")
            var d = NewCase()
            d.service = svc; d.problem = pcs.first { $0.displayName.contains("RDP or SSH") } ?? pcs[0]
            d.resourceId = res.first?.id; d.severity = .minimal
            d.title = "[CasePilot 工具测试, 请直接关闭] 原生 macOS App 验证"
            d.description = "Azure CasePilot 原生 SwiftUI 版本发布前验证 Case, 无需处理, 将由工具自动关闭。Release validation, no action needed."
            d.contact = Contact(firstName: "Zhengyu", lastName: "Ren", email: "zhengyuren@microsoft.com")
            let created = try await store.client.create(d, tenant: t, sub: sub)
            log("created \(created.ticketNumber) status=\(created.status) sev=\(created.severity)")
            try await store.client.reply(created.id, text: "CasePilot 原生 App 验证: 补充信息功能, 无需回复。", tenant: t, sub: sub)
            try await Task.sleep(for: .seconds(8))
            let det = try await store.client.detail(created.id, tenant: t, sub: sub)
            log("comms=\(det.communications.map { ($0.fromMicrosoft ? "MS:" : "ME:") + $0.subject.prefix(30) })")
            try await store.client.close(created.id, tenant: t, sub: sub)
            let after = try await store.client.detail(created.id, tenant: t, sub: sub)
            log("closed status=\(after.summary.status)")
        } catch { log("ERROR \(error.localizedDescription)") }
    }

    /// App Registration: 登录 → 订阅/服务/问题/历史 → 创建 → 关闭; 并验证本地保存可读回
    @MainActor static func spTest(_ file: String) async {
        let l = (try? String(contentsOfFile: file, encoding: .utf8))?.split(separator: "\n").map(String.init) ?? []
        let store = AppStore()
        func log(_ s: String) { print("[sptest] " + s); fflush(stdout) }
        do {
            try await store.loginSP(tenant: l[0], clientId: l[1], secret: l[2], cloud: .global)
            log("login \(store.identity?.method ?? "") \(store.identity?.user ?? "")")
            let saved = SPStore.load()
            log("saved tenant=\(saved?.tenant == l[0]) client=\(saved?.clientId == l[1]) secret=\(saved?.secret == l[2])")
            while store.services.isEmpty || store.subscriptionId.isEmpty { try await Task.sleep(for: .milliseconds(300)) }
            let t = store.tenantId!, sub = store.sub
            log("subs=\(store.subscriptions.count) services=\(store.services.count) tickets=\(store.tickets.count)")
            let svc = store.services.first { $0.displayName == "Virtual Machine running Linux" }!
            let pcs = try await store.client.problems(tenant: t, service: svc)
            var d = NewCase()
            d.service = svc; d.problem = pcs.first { $0.displayName.contains("RDP or SSH") } ?? pcs[0]; d.severity = .minimal
            d.title = "[CasePilot 工具测试, 请直接关闭] App Registration 验证"
            d.description = "Azure CasePilot App Registration 登录方式验证 Case, 无需处理, 将由工具自动关闭。"
            d.contact = Contact(firstName: "Zhengyu", lastName: "Ren", email: "zhengyuren@microsoft.com")
            let c = try await store.client.create(d, tenant: t, sub: sub)
            log("created \(c.ticketNumber)")
            try await store.client.close(c.id, tenant: t, sub: sub)
            log("closed \(try await store.client.detail(c.id, tenant: t, sub: sub).summary.status)")
        } catch { log("ERROR \(error.localizedDescription)") }
    }

    /// 真实窗口截图 (cacheDisplay, 无需屏幕录制权限)
    @MainActor static func snapshot(dir: String, live: Bool) async {
        func grab(_ name: String) {
            guard let v = NSApp.windows.first(where: { $0.isVisible && $0.contentView != nil })?.contentView,
                  let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) else { return }
            v.cacheDisplay(in: v.bounds, to: rep)
            try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: dir).appendingPathComponent(name + ".png"))
        }
        try? await Task.sleep(for: .seconds(14))
        NotificationCenter.default.post(name: .showCreate, object: nil)
        NSApp.windows.first?.setContentSize(NSSize(width: 1280, height: 820))
        try? await Task.sleep(for: .seconds(1))
        grab("create")
        NotificationCenter.default.post(name: .showHistory, object: nil)
        try? await Task.sleep(for: .seconds(4))
        grab("history")
        NotificationCenter.default.post(name: .selectFirstTicket, object: nil)
        try? await Task.sleep(for: .seconds(6))
        grab("detail")
        print("[snapshot] done")
    }
}
#endif
