import SwiftUI

/// ⌘, 设置: 联系人默认值 (每个 Case 复用)
struct ContactSettingsView: View {
    @Environment(AppStore.self) private var store

    var body: some View {
        @Bindable var store = store
        Form {
            Section {
                TextField("姓", text: $store.contact.lastName)
                TextField("名", text: $store.contact.firstName)
                TextField("邮箱", text: $store.contact.email)
                TextField("抄送", text: $store.contact.cc, prompt: Text("可选, 逗号分隔"))
            } header: { Text("联系人") } footer: { Text("Microsoft 支持将通过此邮箱联系你。仅保存在本机。").foregroundStyle(.secondary) }
            Section("联系方式") {
                Picker("首选方式", selection: $store.contact.preferPhone) { Text("邮件").tag(false); Text("电话").tag(true) }
                TextField("电话", text: $store.contact.phone, prompt: Text(store.contact.preferPhone ? "必填" : "可选"))
            }
            Section("区域") {
                Picker("支持语言", selection: $store.contact.language) { ForEach(Contact.languages, id: \.0) { Text($0.1).tag($0.0) } }
                Picker("国家 / 地区", selection: $store.contact.country) { ForEach(Contact.countries, id: \.0) { Text($0.1).tag($0.0) } }
                Picker("时区", selection: $store.contact.timeZone) { ForEach(Contact.timeZones, id: \.self) { Text($0).tag($0) } }
            }
        }
        .formStyle(.grouped)
        .frame(width: 520, height: 560)
    }
}

struct AISettingsView: View {
    @Environment(AppStore.self) private var store
    @State private var c = AIConfig.load()
    @State private var aoaiKey = SecretStore.get(.aoai) ?? ""
    @State private var sttKey = SecretStore.get(.stt) ?? ""

    var body: some View {
        Form {
            Section {
                AuthPicker(auth: $c.chatAuth)
                TextField("Endpoint", text: $c.chatEndpoint, prompt: Text("https://<资源>.openai.azure.com"))
                TextField("部署名", text: $c.chatDeployment)
                if c.chatAuth == .key { SecureField("API Key", text: $aoaiKey, prompt: Text("仅保存在本机钥匙串")) }
                ConnectionTest { try await store.ai.testChat() }
            } header: { Text("AI 帮写 · Azure OpenAI / Foundry") } footer: {
                AuthFooter(auth: c.chatAuth, user: store.identity?.user, role: "Cognitive Services OpenAI User")
            }
            Section {
                Picker("转写服务", selection: $c.sttProvider) { ForEach(STTProvider.allCases) { Text($0.label).tag($0) } }
                AuthPicker(auth: $c.sttAuth)
                if c.sttProvider == .azureSpeech {
                    if c.sttAuth == .entra {
                        TextField("Endpoint", text: $c.sttEndpoint, prompt: Text("https://<资源>.cognitiveservices.azure.com"))
                    }
                    TextField("区域", text: $c.speechRegion, prompt: Text(c.sttAuth == .key ? "必填, 如 westus3" : "可选, 默认从令牌识别"))
                } else {
                    TextField("Endpoint", text: $c.sttEndpoint)
                    TextField("流式部署名", text: $c.sttStreamDeployment)
                    TextField("整段转写部署名", text: $c.sttDeployment, prompt: Text("流式失败时使用"))
                }
                if c.sttAuth == .key {
                    SecureField(c.sttProvider == .azureSpeech ? "Speech Key" : "API Key", text: $sttKey, prompt: Text("仅保存在本机钥匙串"))
                }
                TextField("语言", text: $c.sttLanguage)
                ConnectionTest { try await store.ai.testStream() }
            } header: { Text("语音输入") } footer: {
                AuthFooter(auth: c.sttAuth, user: store.identity?.user,
                           role: c.sttProvider == .azureSpeech ? "Cognitive Services User" : "Cognitive Services OpenAI User")
            }
            Section {
                Button("恢复默认", role: .destructive) { c = AIConfig(); aoaiKey = ""; sttKey = "" }
            }
        }
        .formStyle(.grouped)
        .frame(width: 540, height: 680)
        .onChange(of: c) { _, v in v.save() }
        .onChange(of: aoaiKey) { _, v in SecretStore.set(.aoai, v.trimmingCharacters(in: .whitespaces)) }
        .onChange(of: sttKey) { _, v in SecretStore.set(.stt, v.trimmingCharacters(in: .whitespaces)) }
    }
}

private struct AuthPicker: View {
    @Binding var auth: AIAuth
    var body: some View {
        Picker("认证方式", selection: $auth) {
            Text("登录身份 (Entra ID)").tag(AIAuth.entra)
            Text("API Key").tag(AIAuth.key)
        }
        .pickerStyle(.segmented)
    }
}

private struct AuthFooter: View {
    let auth: AIAuth, user: String?, role: String
    var body: some View {
        Group {
            if auth == .entra {
                Text("以 \(user ?? "当前登录身份") 调用, 无需 Key。需要在资源上具备 \(role) 角色。")
            } else {
                Text("Key 仅保存在本机钥匙串。切回登录身份不会删除已保存的 Key。")
            }
        }.foregroundStyle(.secondary)
    }
}

/// 「测试连接」: 结果就地显示, 401/403 时给出角色或 Key 提示
private struct ConnectionTest: View {
    let run: () async throws -> Void
    @State private var state: (ok: Bool, text: String)?
    @State private var testing = false
    var body: some View {
        HStack {
            if let s = state {
                Label(s.text, systemImage: s.ok ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                    .foregroundStyle(s.ok ? .green : .orange).font(.callout).lineLimit(3).textSelection(.enabled)
            }
            Spacer()
            Button {
                Task {
                    testing = true; defer { testing = false }
                    do { try await run(); state = (true, "连接成功") } catch { state = (false, error.localizedDescription) }
                }
            } label: { if testing { ProgressView().controlSize(.small) } else { Text("测试连接") } }
            .disabled(testing)
        }
    }
}

/// 为 Claude Desktop / VS Code 等 MCP 客户端提供配置片段 (本机 stdio, 复用 Azure CLI 登录)
struct MCPSettingsView: View {
    @Environment(AppStore.self) private var store
    private var snippet: String {
        let sub = store.subscriptionId
        return """
        {
          "mcpServers": {
            "casepilot": {
              "command": "python3",
              "args": ["-m", "casepilot.mcp"],
              "cwd": "<Azure_CasePilot 仓库路径>",
              "env": { "CASEPILOT_SUBSCRIPTION_ID": "\(sub)" }
            }
          }
        }
        """
    }
    var body: some View {
        Form {
            Section {
                Text(snippet).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                Button("复制配置") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(snippet, forType: .string) }
            } header: { Text("本机 MCP (stdio)") } footer: {
                Text("复用 Azure CLI 登录。写操作 (创建 / 补充 / 关闭) 需客户端在用户确认后传 confirm=true。").foregroundStyle(.secondary)
            }
            Section("服务器 MCP (AKS)") {
                Text("POST https://<你的域名>/mcp\nAuthorization: Bearer <CASEPILOT_API_KEY>").font(.system(.caption, design: .monospaced)).textSelection(.enabled)
            }
        }
        .formStyle(.grouped)
        .frame(width: 520, height: 560)
    }
}
