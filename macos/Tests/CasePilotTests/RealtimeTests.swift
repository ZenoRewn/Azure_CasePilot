import XCTest
@testable import CasePilot

final class RealtimeTests: XCTestCase {
    func testResample16kTo24k() {
        let pcm = [Int16(0), 300, 600, 900].withUnsafeBufferPointer { Data(buffer: $0) }
        let out = RealtimeAudio.resample16kTo24k(pcm)
        let vals = out.withUnsafeBytes { Array($0.bindMemory(to: Int16.self)) }
        XCTAssertEqual(vals.count, 6)
        XCTAssertEqual(vals[0], 0)
        XCTAssertEqual(vals[3], 600)
    }

    func testWavHeader() {
        let h = RealtimeAudio.wavHeader()
        XCTAssertEqual(h.count, 44)
        XCTAssertEqual(String(data: h.prefix(4), encoding: .ascii), "RIFF")
    }

    func testAoaiSessionAndEvents() throws {
        var c = AIConfig(); c.sttEndpoint = "https://x.openai.azure.com"
        let p = AoaiRealtime(config: c)
        XCTAssertEqual(p.url.absoluteString, "wss://x.openai.azure.com/openai/v1/realtime?intent=transcription")
        guard case .text(let s) = p.startMessages()[0] else { return XCTFail() }
        XCTAssertTrue(s.contains(#""languages":["zh"]"#) && s.contains("gpt-live-transcribe") && s.contains("24000"))
        c.sttStreamDeployment = "gpt-4o-mini-transcribe"
        guard case .text(let s2) = AoaiRealtime(config: c).startMessages()[0] else { return XCTFail() }
        XCTAssertTrue(s2.contains(#""language":"zh""#))

        var t = Transcript()
        p.handle(#"{"type":"conversation.item.input_audio_transcription.delta","delta":"虚拟"}"#, &t)
        p.handle(#"{"type":"conversation.item.input_audio_transcription.delta","delta":"机"}"#, &t)
        XCTAssertEqual(t.text, "虚拟机")
        p.handle(#"{"type":"conversation.item.input_audio_transcription.completed","transcript":"虚拟机。"}"#, &t)
        XCTAssertEqual(t.text, "虚拟机。"); XCTAssertTrue(t.done)
        p.handle(#"{"type":"error","error":{"message":"bad"}}"#, &t)
        XCTAssertEqual(t.error, "bad")
    }

    func testSpeechRegionAndPostRefinement() throws {
        let claims = Data(#"{"region":"westus3"}"#.utf8).base64EncodedString().replacingOccurrences(of: "=", with: "")
        var c = AIConfig(); c.sttProvider = .azureSpeech
        let p = SpeechRealtime(config: c, stsToken: "h.\(claims).s")
        XCTAssertEqual(p.url.absoluteString, "wss://westus3.stt.speech.microsoft.com/stt/speech/universal/v2")
        c.speechRegion = "eastus"
        XCTAssertTrue(SpeechRealtime(config: c, stsToken: "h.\(claims).s").url.absoluteString.hasPrefix("wss://eastus."))

        let msgs = p.startMessages()
        guard case .text(let ctx) = msgs[1] else { return XCTFail() }
        XCTAssertTrue(ctx.contains("Path: speech.context") && ctx.contains(#""postprocessingoption":"PostRefinement""#) && ctx.contains("zh-CN"))
        guard case .binary(let wav) = msgs[2] else { return XCTFail() }
        let n = Int(wav[0]) << 8 | Int(wav[1])
        XCTAssertTrue(String(decoding: wav[2..<(2 + n)], as: UTF8.self).contains("Path: audio"))
        XCTAssertEqual(String(data: wav[(2 + n)..<(6 + n)], encoding: .ascii), "RIFF")
    }

    func testSpeechHypothesisThenPhrase() {
        var c = AIConfig(); c.speechRegion = "w"
        let p = SpeechRealtime(config: c, stsToken: "t")
        var t = Transcript()
        p.handle("Path:speech.hypothesis\r\nX-RequestId: 1\r\n\r\n{\"Text\":\"虚拟机无法\"}", &t)
        XCTAssertEqual(t.text, "虚拟机无法")
        p.handle("Path:speech.phrase\r\n\r\n{\"RecognitionStatus\":\"Success\",\"DisplayText\":\"虚拟机无法连接。\"}", &t)
        p.handle("Path:speech.hypothesis\r\n\r\n{\"Text\":\"从今天\"}", &t)
        XCTAssertEqual(t.text, "虚拟机无法连接。从今天")
        p.handle("Path: turn.end\r\n\r\n{}", &t)
        XCTAssertTrue(t.done)
    }

    func testOldSettingsDecodeWithStreamDefault() throws {
        let c = try JSONDecoder().decode(AIConfig.self, from: Data(#"{"sttDeployment":"x"}"#.utf8))
        XCTAssertEqual(c.sttStreamDeployment, "gpt-live-transcribe")
        XCTAssertEqual(c.sttDeployment, "x")
    }
}

final class AIAuthTests: XCTestCase {
    func testEntraIgnoresStoredKey() {
        var c = AIConfig(); c.chatAuth = .entra
        XCTAssertTrue(c.authHeader(.chat, key: "k").isEmpty)
        c.chatAuth = .key
        XCTAssertEqual(c.authHeader(.chat, key: "k"), ["api-key": "k"])
        c.sttAuth = .key; c.sttProvider = .azureSpeech
        XCTAssertEqual(c.authHeader(.stt, key: "s"), ["Ocp-Apim-Subscription-Key": "s"])
        c.sttAuth = .entra
        XCTAssertNil(c.key(.stt, stored: "s"))
    }

    func testLegacySettingsInferAuthFromStoredKey() throws {
        let data = Data(#"{"chatDeployment":"x"}"#.utf8)
        let c = try JSONDecoder().decode(AIConfig.self, from: data).migrated(hasChatKey: true, hasSttKey: false)
        XCTAssertEqual(c.chatAuth, .key)
        XCTAssertEqual(c.sttAuth, .entra)
        var explicit = AIConfig(); explicit.chatAuth = .entra
        let d = try JSONDecoder().decode(AIConfig.self, from: try JSONEncoder().encode(explicit)).migrated(hasChatKey: true, hasSttKey: true)
        XCTAssertEqual(d.chatAuth, .entra)
    }
}
