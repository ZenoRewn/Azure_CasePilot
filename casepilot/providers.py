"""Azure OpenAI / Foundry 对话与语音转写 (Azure OpenAI 或 Azure Speech), 使用 Entra ID 令牌.

默认: westus3 资源上的 gpt-6-luna (reasoning_effort=none, 低延迟) 与 gpt-4o-mini-transcribe。
均可通过环境变量或 API 参数覆盖。
"""
import json
import uuid
from dataclasses import dataclass, replace

from . import ai

COGNITIVE_SCOPE = "https://cognitiveservices.azure.com"


@dataclass(frozen=True)
class AIConfig:
    chat_endpoint: str = "https://westus3-azureai-service-507154.openai.azure.com"
    chat_deployment: str = "gpt-6-luna"
    reasoning_effort: str = "none"
    stt_provider: str = "azure-openai"            # azure-openai | azure-speech
    stt_endpoint: str = "https://westus3-azureai-service-507154.openai.azure.com"
    stt_deployment: str = "gpt-4o-mini-transcribe"
    stt_language: str = "zh-CN"
    api_key: str = ""                             # AOAI / Foundry key; 为空时使用 Entra 令牌
    stt_key: str = ""                             # 转写 key (AOAI key 或 Speech key); 为空时使用 Entra 令牌
    speech_region: str = ""                       # 设置后 Speech 使用区域端点 https://<region>.api.cognitive.microsoft.com

    @staticmethod
    def from_env(env):
        c = AIConfig()
        m = {"CASEPILOT_AOAI_ENDPOINT": "chat_endpoint", "CASEPILOT_AOAI_DEPLOYMENT": "chat_deployment",
             "CASEPILOT_REASONING": "reasoning_effort", "CASEPILOT_STT_PROVIDER": "stt_provider",
             "CASEPILOT_STT_ENDPOINT": "stt_endpoint", "CASEPILOT_STT_DEPLOYMENT": "stt_deployment",
             "CASEPILOT_STT_LANGUAGE": "stt_language", "CASEPILOT_AOAI_KEY": "api_key",
             "CASEPILOT_STT_KEY": "stt_key", "CASEPILOT_SPEECH_REGION": "speech_region"}
        return c.merged({f: env[k] for k, f in m.items() if env.get(k)})

    def merged(self, overrides):
        allowed = {k: v for k, v in (overrides or {}).items() if k in self.__dataclass_fields__ and isinstance(v, str) and v}
        c = replace(self, **allowed)
        return replace(c, chat_endpoint=c.chat_endpoint.rstrip("/"), stt_endpoint=c.stt_endpoint.rstrip("/"))

    def public(self):
        d = dict(self.__dict__)
        d["api_key"], d["stt_key"] = bool(self.api_key), bool(self.stt_key)
        return d

    @property
    def speech_endpoint(self):
        return f"https://{self.speech_region}.api.cognitive.microsoft.com" if self.speech_region else self.stt_endpoint


def chat_request(c, messages, schema):
    return (f"{c.chat_endpoint}/openai/v1/chat/completions",
            {"model": c.chat_deployment, "messages": messages, "reasoning_effort": c.reasoning_effort,
             "response_format": ai.response_format(schema)})


def stt_url(c):
    if c.stt_provider == "azure-speech":
        return f"{c.speech_endpoint}/speechtotext/transcriptions:transcribe?api-version=2024-11-15"
    return f"{c.stt_endpoint}/openai/deployments/{c.stt_deployment}/audio/transcriptions?api-version=2025-04-01-preview"


def key_header(c, kind):
    """kind: chat | stt. 返回 key 认证头; 空 dict 表示使用 Entra 令牌。"""
    if kind == "chat":
        return {"api-key": c.api_key} if c.api_key else {}
    if not c.stt_key:
        return {}
    return {"Ocp-Apim-Subscription-Key": c.stt_key} if c.stt_provider == "azure-speech" else {"api-key": c.stt_key}


def stt_fields(c):
    if c.stt_provider == "azure-speech":
        return {"definition": json.dumps({"locales": [c.stt_language]})}, "audio"
    return {"language": c.stt_language.split("-")[0]}, "file"


def multipart(fields, file):
    boundary = "cp" + uuid.uuid4().hex
    out = b""
    for k, v in fields.items():
        out += f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n'.encode()
    name, filename, data, ctype = file
    out += (f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'
            f"Content-Type: {ctype}\r\n\r\n").encode() + data + f"\r\n--{boundary}--\r\n".encode()
    return out, f"multipart/form-data; boundary={boundary}"


def parse_transcript(provider, payload):
    if provider == "azure-speech":
        return "".join(p.get("text", "") for p in payload.get("combinedPhrases", []))
    return payload.get("text", "")
