import SwiftUI

struct LoginView: View {
    @Environment(AppStore.self) private var store
    @Environment(\.openURL) private var openURL
    enum Method: String, CaseIterable { case cli = "Azure CLI", device = "浏览器", sp = "App Registration" }
    @State private var method: Method = .cli
    @State private var cloud: AzureCloud = .global
    @State private var busy = false
    @State private var error: String?
    @State private var code: AuthProvider.DeviceCode?
    @State private var autoTried = false
    @State private var spTenant = ""
    @State private var spClient = ""
    @State private var spSecret = ""
    @State private var spCloud: AzureCloud = .global

    var body: some View {
        VStack(spacing: 0) {
            Spacer()
            VStack(alignment: .leading, spacing: 18) {
                Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 64, height: 64)
                VStack(alignment: .leading, spacing: 6) {
                    Text("连接到 Azure").font(.largeTitle.weight(.semibold))
                    Text("凭据仅保存在本机内存中, 不经过任何第三方服务。").foregroundStyle(.secondary)
                }
                Picker("", selection: $method) { ForEach(Method.allCases, id: \.self) { Text($0.rawValue) } }
                    .pickerStyle(.segmented).labelsHidden()

                if method == .cli {
                    Label { Text("复用本机 **az login** 会话, 可切换到 CLI 已登录过的任意租户。") } icon: { Image(systemName: "terminal") }
                        .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
                    Button { Task { await loginCLI() } } label: { primaryLabel("使用 Azure CLI 继续") }
                        .buttonStyle(.borderedProminent).controlSize(.large).disabled(busy)
                } else if method == .sp {
                    Form {
                        Picker("云环境", selection: $spCloud) { ForEach(AzureCloud.allCases) { Text($0.label).tag($0) } }
                        TextField("Tenant ID", text: $spTenant)
                        TextField("Client ID", text: $spClient)
                        SecureField("Client Secret", text: $spSecret)
                    }.formStyle(.grouped).scrollDisabled(true).frame(height: 210).padding(.horizontal, -20)
                    Button { Task { await loginSP() } } label: { primaryLabel("使用 App Registration 登录") }
                        .buttonStyle(.borderedProminent).controlSize(.large).disabled(busy)
                    Text("需在订阅上授予 Support Request Contributor 与 Reader 角色。信息保存在本机, Secret 存入钥匙串。")
                        .font(.caption).foregroundStyle(.secondary)
                } else {
                    Picker("云环境", selection: $cloud) { ForEach(AzureCloud.allCases) { Text($0.label).tag($0) } }
                    if let code {
                        VStack(spacing: 6) {
                            Text("在浏览器中输入验证码 (已复制)").font(.caption).foregroundStyle(.secondary)
                            Text(code.userCode).font(.system(size: 30, weight: .semibold, design: .monospaced)).tracking(4).textSelection(.enabled)
                            Link(code.verificationURL.absoluteString, destination: code.verificationURL).font(.caption)
                        }
                        .frame(maxWidth: .infinity).padding(16)
                        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 14))
                    }
                    Button { Task { await loginDevice() } } label: { primaryLabel(code == nil ? "获取验证码并登录" : "等待浏览器中完成登录…") }
                        .buttonStyle(.borderedProminent).controlSize(.large).disabled(busy)
                    Text("无需安装 Azure CLI, 支持 MFA。").font(.caption).foregroundStyle(.secondary)
                }
                if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout).foregroundStyle(.orange).textSelection(.enabled)
                        .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                        .background(Color.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 12))
                }
            }
            .padding(32)
            .frame(width: 460)
            .background(.background, in: RoundedRectangle(cornerRadius: 24))
            .overlay(RoundedRectangle(cornerRadius: 24).strokeBorder(.separator))
            .shadow(color: .black.opacity(0.06), radius: 16, y: 6)
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
        .task {
            guard !autoTried else { return }
            autoTried = true
            if let sp = SPStore.load() { spTenant = sp.tenant; spClient = sp.clientId; spSecret = sp.secret; spCloud = sp.cloud }
            if AuthProvider.azPath() != nil { await loginCLI(silent: true) } else { method = .device }
        }
    }

    private func primaryLabel(_ t: String) -> some View {
        HStack { if busy { ProgressView().controlSize(.small) }; Text(t) }.frame(maxWidth: .infinity)
    }

    private func loginCLI(silent: Bool = false) async {
        busy = true; defer { busy = false }
        do { try await store.loginCLI(); error = nil }
        catch { self.error = (silent ? "未检测到可用的 Azure CLI 会话。请执行 az login 后重试, 或改用浏览器登录。\n" : "") + error.localizedDescription }
    }

    private func loginSP() async {
        busy = true; defer { busy = false }
        do { try await store.loginSP(tenant: spTenant, clientId: spClient, secret: spSecret, cloud: spCloud); error = nil }
        catch { self.error = error.localizedDescription }
    }

    private func loginDevice() async {
        busy = true; defer { busy = false }
        error = nil
        do {
            let c = try await store.auth.startDevice(cloud: cloud)
            code = c
            NSPasteboard.general.clearContents(); NSPasteboard.general.setString(c.userCode, forType: .string)
            openURL(c.verificationURL)
            try await store.loginDevice(c)
        } catch { self.error = error.localizedDescription; code = nil }
    }
}
