import AVFoundation
import Foundation

/// Azure OpenAI / Foundry 对话 + 语音转写 (Azure OpenAI 或 Azure Speech), 使用当前登录身份的 Entra 令牌
struct AIClient {
    let auth: AuthProvider
    var config: AIConfig { AIConfig.load().trimmed }

    private func post(_ url: String, body: Data, contentType: String, kind: AIConfig.Kind) async throws -> [String: Any] {
        guard let u = URL(string: url), u.scheme == "https" else { throw AppError(message: "Endpoint 无效: \(url)") }
        var req = URLRequest(url: u, timeoutInterval: 90)
        req.httpMethod = "POST"
        req.setValue(contentType, forHTTPHeaderField: "Content-Type")
        let keyHeaders = config.authHeader(kind, key: SecretStore.get(kind == .chat ? .aoai : .stt))
        if keyHeaders.isEmpty {
            req.setValue("Bearer " + (try await auth.token(tenant: nil, resource: AuthProvider.cognitive)), forHTTPHeaderField: "Authorization")
        } else { keyHeaders.forEach { req.setValue($1, forHTTPHeaderField: $0) } }
        req.httpBody = body
        var (data, resp) = try await URLSession.shared.data(for: req)
        // 429: 按 Retry-After 退避, 最多重试 2 次
        for _ in 0..<2 {
            guard let http = resp as? HTTPURLResponse, http.statusCode == 429 else { break }
            try await Task.sleep(for: .seconds(min(Int(http.value(forHTTPHeaderField: "Retry-After") ?? "") ?? 3, 15)))
            (data, resp) = try await URLSession.shared.data(for: req)
        }
        let j = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        if let http = resp as? HTTPURLResponse, http.statusCode >= 400 {
            let e = j["error"] as? [String: Any] ?? [:]
            let hint = http.statusCode == 401 || http.statusCode == 403 ? (keyHeaders.isEmpty ? "\n请确认当前身份在该 AI 资源上具备 Cognitive Services OpenAI User 角色, 或在设置中填写 Key。" : "\n请检查 设置 → AI 与语音 中的 Key 与 Endpoint / 区域。")
                : http.statusCode == 429 ? "\n配额不足, 可在 设置 → AI 与语音 中换用其他部署或区域。" : ""
            throw AppError(message: "AI 服务错误 \(http.statusCode): \(e["code"] as? String ?? "") \(e["message"] as? String ?? "")" + hint)
        }
        return j
    }

    /// 非推理模型 (如 gpt-4.1-mini) 会以 400 拒绝 reasoning_effort, 此时去掉后重试一次 (2026-11-11 实测)
    static func shouldRetryWithoutReasoning(_ error: Error) -> Bool {
        (error as? AppError)?.message.contains("reasoning_effort") == true
    }

    func chat(_ messages: [[String: String]], schema: [String: Any]) async throws -> [String: Any] {
        let c = config
        var body: [String: Any] = ["model": c.chatDeployment, "messages": messages, "reasoning_effort": "none",
                                   "response_format": ["type": "json_schema", "json_schema": ["name": "casepilot", "strict": true, "schema": schema]]]
        let url = "\(c.chatEndpoint)/openai/v1/chat/completions"
        let j: [String: Any]
        do {
            j = try await post(url, body: try JSONSerialization.data(withJSONObject: body), contentType: "application/json", kind: .chat)
        } catch where Self.shouldRetryWithoutReasoning(error) {
            body.removeValue(forKey: "reasoning_effort")
            j = try await post(url, body: try JSONSerialization.data(withJSONObject: body), contentType: "application/json", kind: .chat)
        }
        let content = (((j["choices"] as? [[String: Any]])?.first?["message"] as? [String: Any])?["content"] as? String) ?? "{}"
        return (try? JSONSerialization.jsonObject(with: Data(content.utf8))) as? [String: Any] ?? [:]
    }

    func transcribe(_ audio: Data, filename: String = "voice.m4a", mime: String = "audio/m4a") async throws -> String {
        let c = config
        let boundary = "cp" + UUID().uuidString
        var body = Data()
        func field(_ k: String, _ v: String) { body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(k)\"\r\n\r\n\(v)\r\n".data(using: .utf8)!) }
        let url = c.sttURL, part: String
        if c.sttProvider == .azureSpeech {
            part = "audio"; field("definition", #"{"locales":["\#(c.sttLanguage)"]}"#)
        } else {
            part = "file"; field("language", String(c.sttLanguage.prefix(2)))
        }
        body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(part)\"; filename=\"\(filename)\"\r\nContent-Type: \(mime)\r\n\r\n".data(using: .utf8)!)
        body.append(audio); body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)
        let j = try await post(url, body: body, contentType: "multipart/form-data; boundary=\(boundary)", kind: .stt)
        if c.sttProvider == .azureSpeech { return (j["combinedPhrases"] as? [[String: Any]] ?? []).compactMap { $0["text"] as? String }.joined() }
        return j["text"] as? String ?? ""
    }
}

/// 麦克风录音: 实时输出 16kHz 单声道 PCM16 块 (供流式转写), 结束时返回全部 PCM (供整段转写兜底)
@MainActor @Observable
final class VoiceRecorder {
    private let engine = AVAudioEngine()
    private var pcm = Data()
    var recording = false

    func start(onChunk: @escaping @Sendable (Data) -> Void) async throws {
        guard await AVCaptureDevice.requestAccess(for: .audio) else {
            throw AppError(message: "未获得麦克风权限。请在 系统设置 → 隐私与安全性 → 麦克风 中允许 CasePilot。")
        }
        let input = engine.inputNode, inFormat = input.outputFormat(forBus: 0)
        guard let outFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16000, channels: 1, interleaved: true),
              let converter = AVAudioConverter(from: inFormat, to: outFormat) else { throw AppError(message: "无法初始化录音格式") }
        pcm = Data()
        input.installTap(onBus: 0, bufferSize: 4096, format: inFormat) { [weak self] buf, _ in
            let cap = AVAudioFrameCount(Double(buf.frameLength) * 16000 / inFormat.sampleRate) + 16
            guard let out = AVAudioPCMBuffer(pcmFormat: outFormat, frameCapacity: cap) else { return }
            var fed = false
            converter.convert(to: out, error: nil) { _, status in
                if fed { status.pointee = .noDataNow; return nil }
                fed = true; status.pointee = .haveData; return buf
            }
            guard out.frameLength > 0, let p = out.int16ChannelData else { return }
            let chunk = Data(bytes: p[0], count: Int(out.frameLength) * 2)
            onChunk(chunk)
            Task { @MainActor in self?.pcm.append(chunk) }
        }
        engine.prepare()
        try engine.start()
        recording = true
    }

    /// 停止并返回 16kHz PCM16
    func stop() -> Data {
        engine.inputNode.removeTap(onBus: 0); engine.stop(); recording = false
        return pcm
    }
}
