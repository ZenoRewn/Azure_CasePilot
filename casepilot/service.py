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


SECRET_FIELDS = ("api_key", "stt_key")


def ai_config(r):
    """本机模式: 前端可带 key (存于用户本机); 服务器模式: key 只来自环境变量 / K8s Secret, 忽略请求中的 key。"""
    o = dict((r or {}).get("ai") or {})
    if os.environ.get("CASEPILOT_MODE") == "server":
        for k in SECRET_FIELDS:
            o.pop(k, None)
    return BASE_AI.merged(o)


def _post(cfg, url, data, ctype, kind="chat"):
    headers = {"Content-Type": ctype, **providers.key_header(cfg, kind)}
    if len(headers) == 1:
        headers["Authorization"] = "Bearer " + core.AUTH.token(resource=providers.COGNITIVE_SCOPE)
    for attempt in range(3):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, data, headers), timeout=90, context=core.SSL_CTX) as resp:
                return json.loads(resp.read())
        except urllib.error.HTTPError as e:
            with e:  # HTTPError 持有响应流, 需显式关闭
                body = e.read()
            # 429: 按 Retry-After 退避重试 (共 3 次), 低配额部署下也能用
            if e.code == 429 and attempt < 2:
                time.sleep(min(int(e.headers.get("Retry-After") or 3), 15))
                continue
            err = core._safe_json(body).get("error", {})
            hint = " (配额不足, 可在设置中换用其他部署/区域)" if e.code == 429 else ""
            raise core.ApiError(502, f"AI 服务错误 {e.code}: {err.get('code', '')} {err.get('message', '')}".strip() + hint)


def chat(cfg, messages, schema):
    url, body = providers.chat_request(cfg, messages, schema)
    try:
        resp = _post(cfg, url, json.dumps(body).encode(), "application/json")
    except core.ApiError as e:
        # 非推理模型 (如 gpt-4.1-mini) 不认识 reasoning_effort: 去掉后重试一次
        if "reasoning_effort" not in e.message or "reasoning_effort" not in body:
            raise
        body.pop("reasoning_effort")
        resp = _post(cfg, url, json.dumps(body).encode(), "application/json")
    return ai.parse_content(resp)


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
    return {"text": providers.parse_transcript(cfg.stt_provider, _post(cfg, providers.stt_url(cfg), body, ctype, "stt")),
            "provider": cfg.stt_provider}


def support_plan(r):
    tenant, sub = r.get("tenantId"), r.get("subscriptionId")
    if not sub:
        return plan.infer([], {})
    tickets = core.list_tickets(tenant, sub, 365)
    policies = core.arm("GET", f"/subscriptions/{sub}?api-version=2022-12-01", tenant).get("subscriptionPolicies", {})
    return plan.infer(tickets, policies)
