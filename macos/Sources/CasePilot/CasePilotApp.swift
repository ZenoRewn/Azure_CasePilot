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
        .windowToolbarStyle(.unified)
        .defaultSize(width: 1280, height: 820)
        .commands {
            CommandGroup(replacing: .newItem) {}
            CommandMenu("Case") {
                Button("新建 Case") { NotificationCenter.default.post(name: .showCreate, object: nil) }.keyboardShortcut("n")
                Button("刷新") { Task { await store.loadTickets() } }.keyboardShortcut("r")
            }
        }
        Settings {
            ContactSettingsView().environment(store).preferredColorScheme(.light)
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
        Task { @MainActor in SelfTest.runIfRequested() }
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
    @State private var confirmLogout = false

    var body: some View {
        VStack(spacing: 0) {
            List(selection: Binding(get: { section }, set: { if let s = $0 { section = s } })) {
                Section("工作台") {
                    Label("新建 Case", systemImage: "plus.bubble").tag(AppSection.create)
                    Label("我的 Case", systemImage: "tray.full")
                        .badge(store.openCount)
                        .tag(AppSection.history)
                }
            }
            .listStyle(.sidebar)
            if let id = store.identity {
                HStack(spacing: 10) {
                    Circle().fill(Color.accentColor.opacity(0.15)).frame(width: 30, height: 30)
                        .overlay(Text(String(id.user.prefix(1)).uppercased()).font(.callout.weight(.semibold)).foregroundStyle(Color.accentColor))
                    VStack(alignment: .leading, spacing: 1) {
                        Text(id.user).font(.callout.weight(.medium)).lineLimit(1).truncationMode(.middle)
                        Text("\(id.method) · \(id.cloud.rawValue)").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                    Button { confirmLogout = true } label: { Image(systemName: "rectangle.portrait.and.arrow.right") }
                        .buttonStyle(.borderless).help("切换身份")
                }
                .padding(12)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
                .padding(10)
            }
        }
        .confirmationDialog("切换身份?", isPresented: $confirmLogout) {
            Button("切换", role: .destructive) { Task { await store.logout() } }
        }
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
