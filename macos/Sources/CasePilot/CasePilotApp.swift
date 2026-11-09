import SwiftUI

@main
struct CasePilotApp: App {
    @State private var store = AppStore()
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        WindowGroup("CasePilot") {
            RootView()
                .environment(store)
                .frame(minWidth: 1000, minHeight: 680)
                .preferredColorScheme(.light)
        }
        .windowToolbarStyle(.unified(showsTitle: true))
        .defaultSize(width: 1280, height: 820)
        .commands {
            CommandGroup(replacing: .newItem) {}
            CommandMenu("Case") {
                Button("新建 Case") { NotificationCenter.default.post(name: .showCreate, object: nil) }.keyboardShortcut("n")
                Button("刷新") { Task { await store.loadTickets() } }.keyboardShortcut("r")
            }
        }
        Settings {
            SettingsRoot()
            .environment(store).preferredColorScheme(.light)
        }
    }
}

/// 关闭最后一个窗口即退出 App (无常驻后台服务)
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        NSApp.appearance = NSAppearance(named: .aqua)
        NSApp.activate(ignoringOtherApps: true)
        #if DEBUG
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { SelfTest.runIfRequested() }
        #endif
    }
}

extension Notification.Name {
    static let showCreate = Notification.Name("showCreate")
    static let showHistory = Notification.Name("showHistory")
    static let selectFirstTicket = Notification.Name("selectFirstTicket")
}

enum AppSection: String, Hashable { case create, history }

struct RootView: View {
    @Environment(AppStore.self) private var store
    @State private var section: AppSection = .create

    var body: some View {
        Group {
            if store.identity == nil {
                LoginView()
            } else {
                NavigationSplitView {
                    Sidebar(section: $section).navigationSplitViewColumnWidth(min: 210, ideal: 230, max: 280)
                } detail: {
                    switch section {
                    case .create: CreateCaseView(onCreated: { section = .history })
                    case .history: HistoryView()
                    }
                }
                .toolbar { ScopeToolbar() }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .showCreate)) { _ in section = .create }
        .onReceive(NotificationCenter.default.publisher(for: .showHistory)) { _ in section = .history }
        .alert("提示", isPresented: Binding(get: { store.banner != nil }, set: { if !$0 { store.banner = nil } })) {
            Button("好") {}
        } message: { Text(store.banner ?? "") }
    }
}

struct Sidebar: View {
    @Environment(AppStore.self) private var store
    @Binding var section: AppSection

    var body: some View {
        VStack(spacing: 0) {
            List(selection: Binding(get: { section }, set: { if let s = $0 { section = s } })) {
                Section("工作台") {
                    Label("新建 Case", systemImage: "plus.bubble").tag(AppSection.create)
                    Label("我的 Case", systemImage: "tray.full")
                        .badge(store.openCount)
                        .tag(AppSection.history)
                }
                Section("设置") {
                    SidebarSettingsLink(tab: .ai, title: "AI 与语音", symbol: "sparkles")
                    SidebarSettingsLink(tab: .contact, title: "联系人", symbol: "person.crop.circle")
                    SidebarSettingsLink(tab: .mcp, title: "MCP", symbol: "point.3.connected.trianglepath.dotted")
                }
            }
            .listStyle(.sidebar)
            if let id = store.identity {
                IdentityMenu {
                    HStack(spacing: 10) {
                        Circle().fill(Color.accentColor.opacity(0.15)).frame(width: 30, height: 30)
                            .overlay(Text(String(id.user.prefix(1)).uppercased()).font(.callout.weight(.semibold)).foregroundStyle(Color.accentColor))
                        VStack(alignment: .leading, spacing: 1) {
                            Text(id.user).font(.callout.weight(.medium)).lineLimit(1).truncationMode(.middle)
                            Text(id.method + (store.plan.name.map { " · \($0)" } ?? "")).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                .help(store.plan.name.map { "支持计划: \($0) (\(store.plan.sourceLabel))" } ?? "支持计划未知")
                        }
                        Spacer(minLength: 0)
                        Image(systemName: "chevron.up.chevron.down").font(.caption).foregroundStyle(.tertiary)
                    }
                    .padding(12)
                    .background(.fill.quaternary, in: RoundedRectangle(cornerRadius: 10))
                    .contentShape(RoundedRectangle(cornerRadius: 12))
                }
                .padding(10)
            }
        }
    }
}

/// 点击身份卡: 切换租户 / 订阅 / 认证方式
struct IdentityMenu<Label: View>: View {
    @Environment(AppStore.self) private var store
    @ViewBuilder var label: Label
    var body: some View {
        Menu {
            Section("租户") {
                ForEach(store.tenants) { t in
                    Toggle(t.domain.isEmpty ? t.name : "\(t.name) · \(t.domain)", isOn: Binding(get: { store.tenantId == t.id }, set: { if $0 { store.tenantId = t.id } }))
                }
            }
            Section("订阅") {
                Toggle("无 · 租户级", isOn: Binding(get: { store.subscriptionId.isEmpty }, set: { if $0 { store.subscriptionId = "" } }))
                ForEach(store.subscriptions) { s in
                    Toggle(s.name, isOn: Binding(get: { store.subscriptionId == s.id }, set: { if $0 { store.subscriptionId = s.id } }))
                }
            }
            Section("切换认证方式") {
                Button("Azure CLI", systemImage: "terminal") { Task { await store.logout(); try? await store.loginCLI() } }
                Button("浏览器登录…", systemImage: "safari") { Task { await store.logout(switchTo: "浏览器") } }
                if let sp = SPStore.load(), !sp.secret.isEmpty {
                    Button("App Registration (\(sp.clientId.prefix(8))…)", systemImage: "key") {
                        Task { await store.logout(); try? await store.loginSP(tenant: sp.tenant, clientId: sp.clientId, secret: sp.secret, cloud: .global) }
                    }
                }
                Button("App Registration…", systemImage: "key.horizontal") { Task { await store.logout(switchTo: "App Registration") } }
            }
            Divider()
            Button("退出登录", role: .destructive) { Task { await store.logout() } }
        } label: { label }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden)
        .help("切换租户 / 订阅 / 认证方式")
    }
}

struct ScopeToolbar: ToolbarContent {
    @Environment(AppStore.self) private var store

    var body: some ToolbarContent {
        @Bindable var store = store
        ToolbarItem(placement: .navigation) {
            Picker("租户", selection: Binding(get: { store.tenantId ?? "" }, set: { store.tenantId = $0 })) {
                ForEach(store.tenants) { t in Text(t.domain.isEmpty ? t.name : "\(t.name) · \(t.domain)").tag(t.id) }
            }
            .frame(width: 260).help("租户")
        }
        ToolbarItem(placement: .navigation) {
            Picker("订阅", selection: $store.subscriptionId) {
                Text("无 · 租户级 Case").tag("")
                ForEach(store.subscriptions) { s in Text("\(s.name) · \(s.id.prefix(8))").tag(s.id) }
            }
            .frame(width: 300).help("订阅")
        }
    }
}

struct SidebarSettingsLink: View {
    let tab: SettingsTab, title: String, symbol: String
    @Environment(\.openSettings) private var openSettings
    var body: some View {
        Button {
            UserDefaults.standard.set(tab.rawValue, forKey: "settingsTab")
            NotificationCenter.default.post(name: .selectSettingsTab, object: tab)
            openSettings()
        } label: { Label(title, systemImage: symbol) }
        .buttonStyle(.plain)
    }
}

struct SettingsRoot: View {
    @State private var tab = SettingsTab(rawValue: UserDefaults.standard.string(forKey: "settingsTab") ?? "") ?? .ai
    var body: some View {
        TabView(selection: $tab) {
            AISettingsView().tabItem { Label("AI 与语音", systemImage: "sparkles") }.tag(SettingsTab.ai)
            ContactSettingsView().tabItem { Label("联系人", systemImage: "person.crop.circle") }.tag(SettingsTab.contact)
            MCPSettingsView().tabItem { Label("MCP", systemImage: "point.3.connected.trianglepath.dotted") }.tag(SettingsTab.mcp)
        }
        .onReceive(NotificationCenter.default.publisher(for: .selectSettingsTab)) { if let t = $0.object as? SettingsTab { tab = t } }
    }
}
