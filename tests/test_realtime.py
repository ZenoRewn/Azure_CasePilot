import base64
import json
import os
import struct
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from casepilot import providers, realtime as rt  # noqa: E402


class FrameTest(unittest.TestCase):
    def test_encode_masks_and_decode_roundtrip(self):
        f = rt.encode_frame(rt.OP_TEXT, "你好".encode())
        self.assertEqual(f[0], 0x81)
        self.assertTrue(f[1] & 0x80)          # 客户端帧必须加掩码
        op, payload, rest = rt.decode_frame(bytes([0x81, 3]) + b"abc" + b"x")
        self.assertEqual((op, payload, rest), (rt.OP_TEXT, b"abc", b"x"))

    def test_decode_extended_length_and_incomplete(self):
        body = b"a" * 300
        frame = bytes([0x82, 126]) + struct.pack(">H", 300) + body
        self.assertEqual(rt.decode_frame(frame)[1], body)
        self.assertIsNone(rt.decode_frame(frame[:100]))


class AudioTest(unittest.TestCase):
    def test_resample_16k_to_24k_length_and_endpoints(self):
        pcm = struct.pack("<4h", 0, 300, 600, 900)
        out = rt.resample_16k_to_24k(pcm)
        vals = struct.unpack(f"<{len(out) // 2}h", out)
        self.assertEqual(len(vals), 6)
        self.assertEqual(vals[0], 0)
        self.assertEqual(vals[3], 600)

    def test_wav_header(self):
        h = rt.wav_header()
        self.assertEqual(len(h), 44)
        self.assertEqual(h[:4], b"RIFF")
        self.assertEqual(struct.unpack("<I", h[24:28])[0], 16000)


class AoaiTest(unittest.TestCase):
    cfg = providers.AIConfig().merged({"stt_endpoint": "https://x.openai.azure.com/"})

    def test_url_and_session(self):
        p = rt.AoaiProtocol(self.cfg)
        self.assertEqual(p.url(), "wss://x.openai.azure.com/openai/v1/realtime?intent=transcription")
        s = json.loads(p.start_messages()[0][1])["session"]
        self.assertEqual(s["audio"]["input"]["transcription"], {"model": "gpt-live-transcribe", "languages": ["zh"]})
        self.assertEqual(s["audio"]["input"]["format"]["rate"], 24000)

    def test_non_live_model_uses_language(self):
        p = rt.AoaiProtocol(self.cfg.merged({"stt_stream_deployment": "gpt-4o-mini-transcribe"}))
        t = json.loads(p.start_messages()[0][1])["session"]["audio"]["input"]["transcription"]
        self.assertEqual(t, {"model": "gpt-4o-mini-transcribe", "language": "zh"})

    def test_audio_is_resampled_and_events_accumulate(self):
        p = rt.AoaiProtocol(self.cfg)
        op, msg = p.audio_message(b"\x00\x00" * 160)
        self.assertEqual(len(base64.b64decode(json.loads(msg)["audio"])), 480)
        st = rt.Transcript()
        p.handle('{"type":"conversation.item.input_audio_transcription.delta","delta":"虚拟"}', st)
        p.handle('{"type":"conversation.item.input_audio_transcription.delta","delta":"机"}', st)
        self.assertEqual(st.text, "虚拟机")
        p.handle('{"type":"conversation.item.input_audio_transcription.completed","transcript":"虚拟机。"}', st)
        self.assertEqual((st.text, st.done), ("虚拟机。", True))
        p.handle('{"type":"error","error":{"message":"bad"}}', st)
        self.assertEqual(st.error, "bad")


class SpeechTest(unittest.TestCase):
    def test_region_from_config_or_token(self):
        cfg = providers.AIConfig().merged({"stt_provider": "azure-speech"})
        claims = base64.urlsafe_b64encode(json.dumps({"region": "westus3"}).encode()).decode().rstrip("=")
        p = rt.SpeechProtocol(cfg, "h." + claims + ".s")
        self.assertEqual(p.url(), "wss://westus3.stt.speech.microsoft.com/stt/speech/universal/v2")
        p2 = rt.SpeechProtocol(cfg.merged({"speech_region": "eastus"}), "h." + claims + ".s")
        self.assertTrue(p2.url().startswith("wss://eastus."))

    def test_start_enables_post_refinement_and_sends_wav_header(self):
        p = rt.SpeechProtocol(providers.AIConfig().merged({"speech_region": "westus3"}), "t")
        msgs = p.start_messages()
        ctx = msgs[1][1]
        self.assertIn("Path: speech.context", ctx)
        body = json.loads(ctx.split("\r\n\r\n", 1)[1])
        self.assertEqual(body["phraseDetection"]["enrichment"]["conversation"]["postprocessingoption"], "PostRefinement")
        self.assertEqual(body["phraseDetection"]["language"], "zh-CN")
        op, wav = msgs[2]
        self.assertEqual(op, rt.OP_BINARY)
        n = struct.unpack(">H", wav[:2])[0]
        self.assertIn(b"Path: audio", wav[2:2 + n])
        self.assertEqual(wav[2 + n:2 + n + 4], b"RIFF")

    def test_hypothesis_then_phrase(self):
        p, st = rt.SpeechProtocol(providers.AIConfig().merged({"speech_region": "w"}), "t"), rt.Transcript()
        p.handle('Path:speech.hypothesis\r\nX-RequestId: 1\r\n\r\n{"Text":"虚拟机无法"}', st)
        self.assertEqual(st.text, "虚拟机无法")
        p.handle('Path:speech.phrase\r\n\r\n{"RecognitionStatus":"Success","DisplayText":"虚拟机无法连接。"}', st)
        p.handle('Path:speech.hypothesis\r\n\r\n{"Text":"从今天"}', st)
        self.assertEqual(st.text, "虚拟机无法连接。从今天")
        p.handle('Path:speech.phrase\r\n\r\n{"RecognitionStatus":"EndOfDictation"}', st)
        p.handle('Path: turn.end\r\n\r\n{}', st)
        self.assertEqual((st.text, st.done), ("虚拟机无法连接。从今天", True))   # 未定稿的部分保留, 不丢字


class SessionsTest(unittest.TestCase):
    def test_unknown_session(self):
        with self.assertRaises(Exception):
            rt.SESSIONS.append("nope", "")


if __name__ == "__main__":
    unittest.main()
