import AVFoundation
import Foundation

/// Azure OpenAI / Foundry 对话 + 语音转写 (Azure OpenAI 或 Azure Speech), 使用当前登录身份的 Entra 令牌
struct AIClient {
    let auth: AuthProvider
    var config: AIConfig { AIConfig.load().trimmed }

    private func post(_ url: String, body: Data, contentType: String) async throws -> [String: Any] {
        var req = URLRequest(url: URL(string: url)!, timeoutInterval: 90)
        req.httpMethod = "POST"
        req.setValue(contentType, forHTTPHeaderField: "Content-Type")
        req.setValue("Bearer " + (try await auth.token(tenant: nil, resource: AuthProvider.cognitive)), forHTTPHeaderField: "Authorization")
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
            let hint = http.statusCode == 401 || http.statusCode == 403 ? "\n请确认当前身份在该 AI 资源上具备 Cognitive Services OpenAI User 角色。"
                : http.statusCode == 429 ? "\n配额不足, 可在 设置 → AI 与语音 中换用其他部署或区域。" : ""
            throw AppError(message: "AI 服务错误 \(http.statusCode): \(e["code"] as? String ?? "") \(e["message"] as? String ?? "")" + hint)
        }
        return j
    }

    func chat(_ messages: [[String: String]], schema: [String: Any]) async throws -> [String: Any] {
        let c = config
        let body: [String: Any] = ["model": c.chatDeployment, "messages": messages, "reasoning_effort": "none",
                                   "response_format": ["type": "json_schema", "json_schema": ["name": "casepilot", "strict": true, "schema": schema]]]
        let j = try await post("\(c.chatEndpoint)/openai/v1/chat/completions", body: try JSONSerialization.data(withJSONObject: body), contentType: "application/json")
        let content = (((j["choices"] as? [[String: Any]])?.first?["message"] as? [String: Any])?["content"] as? String) ?? "{}"
        return (try? JSONSerialization.jsonObject(with: Data(content.utf8))) as? [String: Any] ?? [:]
    }

    func transcribe(_ audio: Data, filename: String = "voice.m4a", mime: String = "audio/m4a") async throws -> String {
        let c = config
        let boundary = "cp" + UUID().uuidString
        var body = Data()
        func field(_ k: String, _ v: String) { body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(k)\"\r\n\r\n\(v)\r\n".data(using: .utf8)!) }
        let url: String, part: String
        if c.sttProvider == .azureSpeech {
            url = "\(c.sttEndpoint)/speechtotext/transcriptions:transcribe?api-version=2024-11-15"; part = "audio"
            field("definition", #"{"locales":["\#(c.sttLanguage)"]}"#)
        } else {
            url = "\(c.sttEndpoint)/openai/deployments/\(c.sttDeployment)/audio/transcriptions?api-version=2025-04-01-preview"; part = "file"
            field("language", String(c.sttLanguage.prefix(2)))
        }
        body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(part)\"; filename=\"\(filename)\"\r\nContent-Type: \(mime)\r\n\r\n".data(using: .utf8)!)
        body.append(audio); body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)
        let j = try await post(url, body: body, contentType: "multipart/form-data; boundary=\(boundary)")
        if c.sttProvider == .azureSpeech { return (j["combinedPhrases"] as? [[String: Any]] ?? []).compactMap { $0["text"] as? String }.joined() }
        return j["text"] as? String ?? ""
    }
}

/// 麦克风录音 (m4a), 点击开始 / 再次点击结束
@MainActor @Observable
final class VoiceRecorder {
    private var recorder: AVAudioRecorder?
    private var url: URL { FileManager.default.temporaryDirectory.appendingPathComponent("casepilot-voice.m4a") }
    var recording = false

    func start() async throws {
        guard await AVCaptureDevice.requestAccess(for: .audio) else {
            throw AppError(message: "未获得麦克风权限。请在 系统设置 → 隐私与安全性 → 麦克风 中允许 CasePilot。")
        }
        let r = try AVAudioRecorder(url: url, settings: [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 16000, AVNumberOfChannelsKey: 1])
        r.record(); recorder = r; recording = true
    }

    func stop() -> Data? {
        recorder?.stop(); recorder = nil; recording = false
        return try? Data(contentsOf: url)
    }
}
