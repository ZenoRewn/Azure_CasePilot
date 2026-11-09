import SwiftUI

/// 麦克风按钮: 点击开始录音, 再次点击结束并转写, 结果追加到 text
struct MicButton: View {
    @Environment(AppStore.self) private var store
    @Binding var text: String
    @State private var recorder = VoiceRecorder()
    @State private var busy = false
    @State private var error: String?

    var body: some View {
        Button {
            Task { await toggle() }
        } label: {
            if busy { ProgressView().controlSize(.small) }
            else { Image(systemName: recorder.recording ? "stop.circle.fill" : "mic").symbolEffect(.pulse, isActive: recorder.recording) }
        }
        .buttonStyle(.borderless)
        .foregroundStyle(recorder.recording ? Color.red : Color.secondary)
        .help(recorder.recording ? "结束并转写" : "语音输入")
        .disabled(busy)
        .popover(isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Text(error ?? "").padding().frame(width: 300)
        }
    }

    private func toggle() async {
        if recorder.recording {
            guard let audio = recorder.stop() else { return }
            busy = true; defer { busy = false }
            do {
                let t = try await store.ai.transcribe(audio)
                text = text.isEmpty ? t : text + " " + t
            } catch { self.error = error.localizedDescription }
        } else {
            do { try await recorder.start() } catch { self.error = error.localizedDescription }
        }
    }
}

/// 新建页顶部: 一句话描述 → AI 草稿 → (可选) 追问一轮
struct AIComposer: View {
    @Environment(AppStore.self) private var store
    var apply: (AIDraft) async -> Void
    @State private var text = ""
    @State private var questions: [String] = []
    @State private var answers: [String] = []
    @State private var busy = false
    @State private var status: (ok: Bool, text: String)?

    var body: some View {
        Section {
            HStack(alignment: .top, spacing: 8) {
                // Form 中 TextField 的标题会被当作左侧标签 (黑色), 改用 prompt 作灰色占位并隐藏标签, 输入区占满整行
                TextField("问题描述", text: $text, prompt: Text("一两句话描述问题, 如: 东南亚 gpt-6-luna 下午开始一直 429, 配额没用满"), axis: .vertical)
                    .labelsHidden().lineLimit(2...5).textFieldStyle(.plain)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .onSubmit { Task { await run(withAnswers: false) } }
                MicButton(text: $text)
            }
            if !questions.isEmpty {
                ForEach(questions.indices, id: \.self) { i in
                    VStack(alignment: .leading, spacing: 4) {
                        Text(questions[i]).font(.callout.weight(.medium))
                        HStack {
                            TextField("回答", text: $answers[i], prompt: Text("回答或留空")).labelsHidden().textFieldStyle(.roundedBorder)
                            MicButton(text: $answers[i])
                        }
                    }
                }
            }
            HStack {
                if let status {
                    Label(status.text, systemImage: status.ok ? "checkmark.circle.fill" : "info.circle")
                        .font(.caption).foregroundStyle(status.ok ? .green : .secondary).lineLimit(3)
                    if !status.ok && status.text.contains("AI 服务错误") { AISettingsButton(compact: true) }
                }
                Spacer()
                if !questions.isEmpty {
                    Button("跳过") { questions = []; status = (true, "已使用当前草稿, 请检查后提交") }
                }
                Button { Task { await run(withAnswers: !questions.isEmpty) } } label: {
                    if busy { ProgressView().controlSize(.small) }
                    else { Label(questions.isEmpty ? "生成草稿" : "补充并更新", systemImage: "sparkles") }
                }
                .buttonStyle(.borderedProminent).disabled(busy || text.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        } header: {
            HStack {
                Label("AI 帮写", systemImage: "sparkles").foregroundStyle(.tint)
                Spacer()
                AISettingsButton()
            }
        }
    }

    private func run(withAnswers: Bool) async {
        guard let t = store.tenantId, !text.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        busy = true; defer { busy = false }
        let qa = withAnswers ? zip(questions, answers).map { QA(q: $0, a: $1.isEmpty ? "未知" : $1) } : []
        do {
            let ai = store.ai, client = store.client
            let d = try await AIDrafter.draft(text: text, answers: qa, services: store.services,
                                              problems: { try await client.problems(tenant: t, service: $0) },
                                              chat: { try await ai.chat($0, schema: $1) })
            await apply(d)
            questions = d.questions; answers = Array(repeating: "", count: d.questions.count)
            status = d.ready ? (true, "草稿已填好, 请检查后提交")
                : d.service == nil ? (false, "未能确定服务, 请手动选择")
                : !d.questions.isEmpty ? (false, "已生成初稿, 回答下面的问题可让描述更完整") : (false, "请确认问题类型")
        } catch { status = (false, error.localizedDescription) }
    }
}

/// 显眼的 AI 设置入口: 显示当前模型与认证方式, 点击直达 设置 → AI 与语音
struct AISettingsButton: View {
    var compact = false
    @Environment(\.openSettings) private var openSettings
    var body: some View {
        let c = AIConfig.load()
        let auth = SecretStore.get(.aoai) == nil ? "登录身份" : "API Key"
        Button {
            UserDefaults.standard.set(SettingsTab.ai.rawValue, forKey: "settingsTab")
            NotificationCenter.default.post(name: .selectSettingsTab, object: SettingsTab.ai)
            openSettings()
        } label: {
            Label(compact ? "配置 AI" : "\(c.chatDeployment) · \(auth)", systemImage: "gearshape")
                .font(.caption)
        }
        .buttonStyle(.bordered).controlSize(.small)
        .help("配置 AI 帮写与语音 (Endpoint / Key / 区域)")
    }
}
