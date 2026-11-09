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
    @State private var c = AIConfig.load()
    var body: some View {
        Form {
            Section {
                TextField("Endpoint", text: $c.chatEndpoint)
                TextField("部署名", text: $c.chatDeployment)
            } header: { Text("AI 帮写 (Azure OpenAI / Foundry)") } footer: {
                Text("使用当前登录身份调用, 需在该资源上具备 Cognitive Services OpenAI User 角色。默认 westus3 · gpt-6-luna。").foregroundStyle(.secondary)
            }
            Section("语音输入") {
                Picker("转写服务", selection: $c.sttProvider) { ForEach(STTProvider.allCases) { Text($0.label).tag($0) } }
                TextField("Endpoint", text: $c.sttEndpoint)
                if c.sttProvider == .azureOpenAI { TextField("部署名", text: $c.sttDeployment) }
                TextField("语言", text: $c.sttLanguage)
            }
            Section { Button("恢复默认") { c = AIConfig() } }
        }
        .formStyle(.grouped)
        .frame(width: 520, height: 560)
        .onChange(of: c) { _, v in v.save() }
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
