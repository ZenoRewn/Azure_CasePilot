"""应用服务层: Web / MCP / 容器共用的业务操作 (输入为 dict, 输出可 JSON 序列化)."""
import base64
import json
import os
import time
import urllib.error
import urllib.request

from . import ai, core, plan, providers

MAX_AUDIO_B64 = 14 * 1024 * 1024   # ~10MB 音频
BASE_AI = providers.AIConfig.from_env(os.environ)


def ai_config(r):
    return BASE_AI.merged((r or {}).get("ai"))


def _post(cfg, url, data, ctype):
    headers = {"Content-Type": ctype}
    if cfg.api_key:
        headers["api-key"] = cfg.api_key
    else:
        headers["Authorization"] = "Bearer " + core.AUTH.token(resource=providers.COGNITIVE_SCOPE)
    for attempt in range(3):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, data, headers), timeout=90, context=core.SSL_CTX) as resp:
                return json.loads(resp.read())
        except urllib.error.HTTPError as e:
            # 429: 按 Retry-After 退避重试 (共 3 次), 低配额部署下也能用
            if e.code == 429 and attempt < 2:
                time.sleep(min(int(e.headers.get("Retry-After") or 3), 15))
                continue
            err = core._safe_json(e.read()).get("error", {})
            hint = " (配额不足, 可在设置中换用其他部署/区域)" if e.code == 429 else ""
            raise core.ApiError(502, f"AI 服务错误 {e.code}: {err.get('code', '')} {err.get('message', '')}".strip() + hint)


def chat(cfg, messages, schema):
    url, body = providers.chat_request(cfg, messages, schema)
    return ai.parse_content(_post(cfg, url, json.dumps(body).encode(), "application/json"))


def ai_draft(r):
    text = (r.get("text") or "").strip()
    if not text:
        raise core.ApiError(400, "请先描述问题")
    cfg, tenant = ai_config(r), r.get("tenantId")
    return ai.draft_case(text, r.get("answers") or [], core.list_services(tenant),
                         lambda s: core.list_problems(tenant, s["name"]), lambda m, s: chat(cfg, m, s))


def transcribe(r):
    b64 = r.get("audio") or ""
    if not b64 or len(b64) > MAX_AUDIO_B64:
        raise core.ApiError(400, "音频为空或超过 10MB")
    cfg = ai_config(r)
    fields, part = providers.stt_fields(cfg)
    body, ctype = providers.multipart(fields, (part, r.get("filename") or "audio.webm", base64.b64decode(b64),
                                               r.get("mime") or "application/octet-stream"))
    return {"text": providers.parse_transcript(cfg.stt_provider, _post(cfg, providers.stt_url(cfg), body, ctype)),
            "provider": cfg.stt_provider}


def support_plan(r):
    tenant, sub = r.get("tenantId"), r.get("subscriptionId")
    if not sub:
        return plan.infer([], {})
    tickets = core.list_tickets(tenant, sub, 365)
    policies = core.arm("GET", f"/subscriptions/{sub}?api-version=2022-12-01", tenant).get("subscriptionPolicies", {})
    return plan.infer(tickets, policies)
