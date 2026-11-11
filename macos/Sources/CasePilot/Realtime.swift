import AVFoundation
import Foundation

// MARK: - 流式语音转写 (与服务端 casepilot/realtime.py 同一协议)
// Azure OpenAI Realtime (gpt-live-transcribe) 或 Azure Speech 实时识别 + Post-Stream Refinement (PSR)

enum WSMessage: Equatable { case text(String), binary(Data) }

struct Transcript {
    var finals = "", partial = "", done = false, error: String?
    var text: String { finals + partial }
}

protocol RealtimeProtocol {
    var url: URL { get }
    func startMessages() -> [WSMessage]
    func audioMessage(_ pcm16k: Data) -> WSMessage
    func endMessages() -> [WSMessage]
    func handle(_ message: String, _ t: inout Transcript)
}

enum RealtimeAudio {
    /// PCM16LE 单声道线性插值 16k → 24k (AOAI Realtime 仅接受 24kHz)
    static func resample16kTo24k(_ pcm: Data) -> Data {
        let src = pcm.withUnsafeBytes { Array($0.bindMemory(to: Int16.self)) }
        guard !src.isEmpty else { return Data() }
        let out = (0..<(src.count * 3 / 2)).map { j -> Int16 in
            let x = Double(j) * 2 / 3, i = Int(x), a = Double(src[i]), b = Double(i + 1 < src.count ? src[i + 1] : src[i])
            return Int16((a + (b - a) * (x - Double(i))).rounded())
        }
        return out.withUnsafeBufferPointer { Data(buffer: $0) }
    }

    static func wavHeader(rate: UInt32 = 16000, dataSize: UInt32 = 0) -> Data {
        var d = Data("RIFF".utf8)
        func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        func u16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        u32(dataSize == 0 ? 0 : 36 + dataSize); d.append(Data("WAVEfmt ".utf8))
        u32(16); u16(1); u16(1); u32(rate); u32(rate * 2); u16(2); u16(16)
        d.append(Data("data".utf8)); u32(dataSize)
        return d
    }

    static func wav(_ pcm: Data) -> Data { wavHeader(dataSize: UInt32(pcm.count)) + pcm }
}

private func json(_ o: Any) -> String { String(decoding: (try? JSONSerialization.data(withJSONObject: o)) ?? Data(), as: UTF8.self) }
private func parse(_ s: String) -> [String: Any] { (try? JSONSerialization.jsonObject(with: Data(s.utf8))) as? [String: Any] ?? [:] }

struct AoaiRealtime: RealtimeProtocol {
    let config: AIConfig
    var url: URL { URL(string: config.sttEndpoint.replacingOccurrences(of: "https://", with: "wss://") + "/openai/v1/realtime?intent=transcription")! }

    func startMessages() -> [WSMessage] {
        let dep = config.sttStreamDeployment, lang = String(config.sttLanguage.prefix(2))
        let tr: [String: Any] = dep.contains("live") ? ["model": dep, "languages": [lang]] : ["model": dep, "language": lang]
        return [.text(json(["type": "session.update", "session": ["type": "transcription", "audio": ["input": [
            "format": ["type": "audio/pcm", "rate": 24000], "transcription": tr, "turn_detection": NSNull()]]]]))]
    }
    func audioMessage(_ pcm16k: Data) -> WSMessage {
        .text(json(["type": "input_audio_buffer.append", "audio": RealtimeAudio.resample16kTo24k(pcm16k).base64EncodedString()]))
    }
    func endMessages() -> [WSMessage] { [.text(#"{"type":"input_audio_buffer.commit"}"#)] }

    func handle(_ message: String, _ t: inout Transcript) {
        let e = parse(message)
        switch e["type"] as? String {
        case "conversation.item.input_audio_transcription.delta": t.partial += e["delta"] as? String ?? ""
        case "conversation.item.input_audio_transcription.completed":
            t.finals += e["transcript"] as? String ?? ""; t.partial = ""; t.done = true
        case "error": t.error = (e["error"] as? [String: Any])?["message"] as? String ?? "实时转写错误"
        default: break
        }
    }
}

struct SpeechRealtime: RealtimeProtocol {
    let config: AIConfig
    let stsToken: String
    let requestId = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()

    var region: String { config.speechRegion.isEmpty ? AuthProvider.jwtClaims(stsToken)["region"] as? String ?? "" : config.speechRegion }
    var url: URL { URL(string: "wss://\(region).stt.speech.microsoft.com/stt/speech/universal/v2")! }

    private func head(_ path: String, _ ctype: String) -> String {
        let ts = ISO8601DateFormatter.string(from: Date(), timeZone: .gmt, formatOptions: [.withInternetDateTime, .withFractionalSeconds])
        return "Path: \(path)\r\nX-RequestId: \(requestId)\r\nX-Timestamp: \(ts)\r\nContent-Type: \(ctype)\r\n"
    }
    private func text(_ path: String, _ body: [String: Any]) -> WSMessage { .text(head(path, "application/json") + "\r\n" + json(body)) }
    private func audio(_ payload: Data) -> WSMessage {
        let h = Data(head("audio", "audio/x-wav").utf8)
        return .binary(Data([UInt8(h.count >> 8), UInt8(h.count & 0xFF)]) + h + payload)
    }

    func startMessages() -> [WSMessage] {
        [text("speech.config", ["context": ["system": ["name": "CasePilot", "version": "2"]]]),
         text("speech.context", ["phraseDetection": ["mode": "CONVERSATION", "language": config.sttLanguage,
                                                     "enrichment": ["conversation": ["postprocessingoption": "PostRefinement"]]],
                                 "phraseOutput": ["format": "Detailed"]]),
         audio(RealtimeAudio.wavHeader())]
    }
    func audioMessage(_ pcm16k: Data) -> WSMessage { audio(pcm16k) }
    func endMessages() -> [WSMessage] { [audio(Data())] }

    func handle(_ message: String, _ t: inout Transcript) {
        let parts = message.components(separatedBy: "\r\n\r\n")
        let path = parts[0].components(separatedBy: "\r\n").first { $0.lowercased().hasPrefix("path:") }
            .map { $0.dropFirst(5).trimmingCharacters(in: .whitespaces).lowercased() } ?? ""
        let body = parse(parts.dropFirst().joined(separator: "\r\n\r\n"))
        switch path {
        case "speech.hypothesis": t.partial = body["Text"] as? String ?? ""
        case "speech.phrase" where body["RecognitionStatus"] as? String == "Success":
            t.finals += body["DisplayText"] as? String ?? ""; t.partial = ""
        case "turn.end": t.done = true
        default: break
        }
    }
}

// MARK: - 会话

/// 一次流式转写: 连接 → 推音频 → 结束等定稿 (最多 8 秒)
actor RealtimeSession {
    private let proto: RealtimeProtocol
    private let task: URLSessionWebSocketTask
    private var t = Transcript()
    private var ended = false
    private let onUpdate: @Sendable (String) -> Void

    init(proto: RealtimeProtocol, headers: [String: String], onUpdate: @escaping @Sendable (String) -> Void) {
        var req = URLRequest(url: proto.url)
        headers.forEach { req.setValue($1, forHTTPHeaderField: $0) }
        self.proto = proto; self.onUpdate = onUpdate
        task = URLSession.shared.webSocketTask(with: req)
    }

    func start() async throws {
        task.resume()
        for m in proto.startMessages() { try await send(m) }
        Task { await receiveLoop() }
    }

    private func send(_ m: WSMessage) async throws {
        switch m {
        case .text(let s): try await task.send(.string(s))
        case .binary(let d): try await task.send(.data(d))
        }
    }

    private func receiveLoop() async {
        while !ended {
            do {
                guard case .string(let s) = try await task.receive() else { continue }
                proto.handle(s, &t)
                onUpdate(t.text)
                if t.done || t.error != nil { ended = true }
            } catch {
                if t.error == nil { t.error = "实时转写中断: \(error.localizedDescription)" }
                ended = true
            }
        }
    }

    var failed: Bool { t.error != nil }

    func push(_ pcm: Data) async {
        guard !ended, !pcm.isEmpty else { return }
        do { try await send(proto.audioMessage(pcm)) } catch { t.error = error.localizedDescription; ended = true }
    }

    func finish() async -> String {
        if !ended {
            for m in proto.endMessages() { try? await send(m) }
            for _ in 0..<80 where !ended { try? await Task.sleep(for: .milliseconds(100)) }
        }
        ended = true
        task.cancel(with: .normalClosure, reason: nil)
        return t.text
    }
}

extension AIClient {
    /// Speech STS 令牌: 有 Speech Key 时走区域端点, 否则用 Entra 令牌在资源端点上换取
    func speechToken(_ c: AIConfig) async throws -> String {
        var req: URLRequest
        if let key = SecretStore.get(.stt), !key.isEmpty {
            guard !c.speechRegion.isEmpty else { throw AppError(message: "使用 Speech Key 时请在 设置 → AI 与语音 中填写区域") }
            req = URLRequest(url: URL(string: "https://\(c.speechRegion).api.cognitive.microsoft.com/sts/v1.0/issueToken")!)
            req.setValue(key, forHTTPHeaderField: "Ocp-Apim-Subscription-Key")
        } else {
            guard let u = URL(string: "\(c.sttEndpoint)/sts/v1.0/issueToken") else { throw AppError(message: "Endpoint 无效") }
            req = URLRequest(url: u)
            req.setValue("Bearer " + (try await auth.token(tenant: nil, resource: AuthProvider.cognitive)), forHTTPHeaderField: "Authorization")
        }
        req.httpMethod = "POST"; req.httpBody = Data()
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard (resp as? HTTPURLResponse)?.statusCode == 200 else {
            throw AppError(message: "Speech 令牌获取失败 \((resp as? HTTPURLResponse)?.statusCode ?? 0), 请检查 Endpoint / 区域 / Key 与角色")
        }
        return String(decoding: data, as: UTF8.self)
    }

    func startStream(onUpdate: @escaping @Sendable (String) -> Void) async throws -> RealtimeSession {
        let c = config
        let proto: RealtimeProtocol, headers: [String: String]
        if c.sttProvider == .azureSpeech {
            let p = SpeechRealtime(config: c, stsToken: try await speechToken(c))
            proto = p; headers = ["Authorization": "Bearer " + p.stsToken, "X-ConnectionId": p.requestId]
        } else {
            proto = AoaiRealtime(config: c)
            let key = c.authHeader(.stt, key: SecretStore.get(.stt))
            headers = key.isEmpty ? ["Authorization": "Bearer " + (try await auth.token(tenant: nil, resource: AuthProvider.cognitive))] : key
        }
        let s = RealtimeSession(proto: proto, headers: headers, onUpdate: onUpdate)
        try await s.start()
        return s
    }
}
