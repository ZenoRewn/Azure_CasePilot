"""流式语音转写: Azure OpenAI Realtime (gpt-live-transcribe) 与 Azure Speech (实时识别 + Post-Stream Refinement).

浏览器 WebSocket 无法携带 Authorization 头, 因此由服务端持有到 Azure 的 WebSocket,
前端通过 ai/stream/start → push (PCM16 16kHz 块, 返回当前文本) → stop 驱动。
WebSocket 客户端为标准库手写 (RFC 6455), 保持零依赖。
"""
import base64
import datetime
import json
import os
import socket
import ssl
import struct
import threading
import time
import urllib.parse
import uuid

OP_TEXT, OP_BINARY, OP_CLOSE, OP_PING, OP_PONG = 1, 2, 8, 9, 10
FINAL_TIMEOUT = 8


# ---------------------------------------------------------------- WebSocket 帧
def encode_frame(op, payload):
    n, mask = len(payload), os.urandom(4)
    head = bytes([0x80 | op])
    if n < 126:
        head += bytes([0x80 | n])
    elif n < 65536:
        head += bytes([0x80 | 126]) + struct.pack(">H", n)
    else:
        head += bytes([0x80 | 127]) + struct.pack(">Q", n)
    return head + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload))


def decode_frame(buf):
    """返回 (op, payload, 剩余字节); 数据不完整返回 None。服务端帧不带掩码。"""
    if len(buf) < 2:
        return None
    op, n, i = buf[0] & 0x0F, buf[1] & 0x7F, 2
    if n == 126:
        if len(buf) < 4:
            return None
        n, i = struct.unpack(">H", buf[2:4])[0], 4
    elif n == 127:
        if len(buf) < 10:
            return None
        n, i = struct.unpack(">Q", buf[2:10])[0], 10
    if len(buf) < i + n:
        return None
    return op, buf[i:i + n], buf[i + n:]


class WebSocket:
    def __init__(self, url, headers, timeout=15):
        u = urllib.parse.urlparse(url)
        from .core import SSL_CTX
        raw = socket.create_connection((u.hostname, u.port or 443), timeout=timeout)
        self.sock = (SSL_CTX or ssl.create_default_context()).wrap_socket(raw, server_hostname=u.hostname)
        key = base64.b64encode(os.urandom(16)).decode()
        path = u.path + ("?" + u.query if u.query else "")
        req = (f"GET {path} HTTP/1.1\r\nHost: {u.hostname}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
               f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n"
               + "".join(f"{k}: {v}\r\n" for k, v in headers.items()) + "\r\n")
        self.sock.sendall(req.encode())
        resp = b""
        while b"\r\n\r\n" not in resp:
            chunk = self.sock.recv(4096)
            if not chunk:
                break
            resp += chunk
        head, _, self.buf = resp.partition(b"\r\n\r\n")
        status = head.split(b"\r\n", 1)[0].decode(errors="replace")
        if " 101 " not in status + " ":
            raise ConnectionError(f"实时转写连接失败: {status}")
        self.sock.settimeout(None)
        self.lock = threading.Lock()

    def send(self, op, payload):
        if isinstance(payload, str):
            payload = payload.encode()
        with self.lock:
            self.sock.sendall(encode_frame(op, payload))

    def recv(self):
        """返回 (op, payload); 连接关闭返回 (OP_CLOSE, b"")。"""
        while True:
            f = decode_frame(self.buf)
            if f is None:
                chunk = self.sock.recv(65536)
                if not chunk:
                    return OP_CLOSE, b""
                self.buf += chunk
                continue
            op, payload, self.buf = f
            if op == OP_PING:
                self.send(OP_PONG, payload)
                continue
            if op == OP_CLOSE:
                return OP_CLOSE, payload
            return op, payload   # Azure 实时转写的消息均为单帧, 不处理分片

    def close(self):
        try:
            self.send(OP_CLOSE, b"")
        except OSError:
            pass
        try:
            self.sock.close()
        except OSError:
            pass


# ---------------------------------------------------------------- 音频
def resample_16k_to_24k(pcm):
    """PCM16LE 单声道线性插值 16k → 24k (AOAI Realtime 仅接受 24kHz)。"""
    n = len(pcm) // 2
    if not n:
        return b""
    src = struct.unpack(f"<{n}h", pcm[:n * 2])
    m = n * 3 // 2
    out = []
    for j in range(m):
        x = j * 2 / 3
        i = int(x)
        a = src[i]
        b = src[i + 1] if i + 1 < n else a
        out.append(int(round(a + (b - a) * (x - i))))
    return struct.pack(f"<{m}h", *out)


def wav_header(rate=16000):
    return (b"RIFF" + struct.pack("<I", 0) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 1, rate, rate * 2, 2, 16)
            + b"data" + struct.pack("<I", 0))


class Transcript:
    def __init__(self):
        self.finals, self.partial, self.done, self.error = "", "", False, None

    @property
    def text(self):
        return self.finals + self.partial


# ---------------------------------------------------------------- 协议
class AoaiProtocol:
    def __init__(self, cfg):
        self.cfg = cfg

    def url(self):
        return self.cfg.stt_endpoint.replace("https://", "wss://", 1) + "/openai/v1/realtime?intent=transcription"

    def start_messages(self):
        dep, lang = self.cfg.stt_stream_deployment, self.cfg.stt_language.split("-")[0]
        tr = {"model": dep, "languages": [lang]} if "live" in dep else {"model": dep, "language": lang}
        return [(OP_TEXT, json.dumps({"type": "session.update", "session": {"type": "transcription", "audio": {"input": {
            "format": {"type": "audio/pcm", "rate": 24000}, "transcription": tr, "turn_detection": None}}}}))]

    def audio_message(self, pcm16k):
        return OP_TEXT, json.dumps({"type": "input_audio_buffer.append",
                                    "audio": base64.b64encode(resample_16k_to_24k(pcm16k)).decode()})

    def end_messages(self):
        return [(OP_TEXT, json.dumps({"type": "input_audio_buffer.commit"}))]

    def handle(self, msg, st):
        e = json.loads(msg)
        t = e.get("type", "")
        if t == "conversation.item.input_audio_transcription.delta":
            st.partial += e.get("delta", "")
        elif t == "conversation.item.input_audio_transcription.completed":
            st.finals += e.get("transcript", "")
            st.partial, st.done = "", True
        elif t == "error":
            st.error = (e.get("error") or {}).get("message") or "实时转写错误"


class SpeechProtocol:
    def __init__(self, cfg, sts_token):
        self.cfg, self.token, self.rid = cfg, sts_token, uuid.uuid4().hex
        self.region = cfg.speech_region
        if not self.region:
            from .core import _jwt_claims
            try:
                self.region = _jwt_claims(sts_token).get("region", "")
            except (ValueError, IndexError):
                self.region = ""

    def url(self):
        return f"wss://{self.region}.stt.speech.microsoft.com/stt/speech/universal/v2"

    def headers(self):
        return {"Authorization": "Bearer " + self.token, "X-ConnectionId": uuid.uuid4().hex}

    def _head(self, path, ctype):
        ts = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3]
        return f"Path: {path}\r\nX-RequestId: {self.rid}\r\nX-Timestamp: {ts}Z\r\nContent-Type: {ctype}\r\n"

    def _text(self, path, body):
        return OP_TEXT, self._head(path, "application/json") + "\r\n" + json.dumps(body)

    def _audio(self, payload):
        h = self._head("audio", "audio/x-wav").encode()
        return OP_BINARY, struct.pack(">H", len(h)) + h + payload

    def start_messages(self):
        return [self._text("speech.config", {"context": {"system": {"name": "CasePilot", "version": "2"}}}),
                self._text("speech.context", {"phraseDetection": {"mode": "CONVERSATION", "language": self.cfg.stt_language,
                           "enrichment": {"conversation": {"postprocessingoption": "PostRefinement"}}},
                           "phraseOutput": {"format": "Detailed"}}),
                self._audio(wav_header())]

    def audio_message(self, pcm16k):
        return self._audio(pcm16k)

    def end_messages(self):
        return [self._audio(b"")]

    def handle(self, msg, st):
        head, _, body = msg.partition("\r\n\r\n")
        path = next((ln.split(":", 1)[1].strip().lower() for ln in head.split("\r\n") if ln.lower().startswith("path:")), "")
        if path == "speech.hypothesis":
            st.partial = json.loads(body).get("Text", "")
        elif path == "speech.phrase":
            j = json.loads(body)
            if j.get("RecognitionStatus") == "Success":
                st.finals += j.get("DisplayText", "")
                st.partial = ""
        elif path == "turn.end":
            st.done = True


# ---------------------------------------------------------------- 会话
class Session:
    def __init__(self, proto, headers):
        self.proto, self.st = proto, Transcript()
        self.ws = WebSocket(proto.url(), headers)
        self.ended = threading.Event()
        for op, m in proto.start_messages():
            self.ws.send(op, m)
        self.touched = time.time()
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self):
        try:
            while True:
                op, payload = self.ws.recv()
                if op == OP_CLOSE:
                    break
                if op == OP_TEXT:
                    self.proto.handle(payload.decode("utf-8", "replace"), self.st)
                    if self.st.done or self.st.error:
                        break
        except (OSError, ValueError) as e:
            self.st.error = self.st.error or f"实时转写中断: {e}"
        finally:
            self.ended.set()

    def push(self, pcm):
        self.touched = time.time()
        if pcm and not self.ended.is_set():
            op, m = self.proto.audio_message(pcm)
            self.ws.send(op, m)
        return self.st.text

    def finish(self):
        if not self.ended.is_set():
            for op, m in self.proto.end_messages():
                self.ws.send(op, m)
            self.ended.wait(FINAL_TIMEOUT)
        self.ws.close()
        return self.st.text


class Sessions:
    TTL = 300

    def __init__(self):
        self.items, self.lock = {}, threading.Lock()

    def add(self, s):
        sid = uuid.uuid4().hex
        with self.lock:
            for k in [k for k, v in self.items.items() if time.time() - v.touched > self.TTL]:
                self.items.pop(k).ws.close()
            self.items[sid] = s
        return sid

    def _get(self, sid, pop=False):
        with self.lock:
            s = self.items.pop(sid, None) if pop else self.items.get(sid)
        if not s:
            raise KeyError("实时转写会话不存在或已过期")
        return s

    def append(self, sid, b64):
        s = self._get(sid)
        if s.st.error:
            raise RuntimeError(s.st.error)
        return s.push(base64.b64decode(b64) if b64 else b"")

    def finish(self, sid):
        s = self._get(sid, pop=True)
        text = s.finish()
        if s.st.error and not text:
            raise RuntimeError(s.st.error)
        return text


SESSIONS = Sessions()
