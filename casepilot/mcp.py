"""CasePilot MCP Server (Model Context Protocol, stdio 与 Streamable HTTP 共用处理逻辑).

本地: `python3 -m casepilot.mcp` (默认 Azure CLI 认证); 服务器: 由 server.py 在 POST /mcp 暴露。
写操作 (create_case / add_case_message / close_case) 必须显式传 confirm=true。
"""
import json
import os
import sys

from . import core, service

PROTOCOL = "2025-06-18"
DEFAULT_CONTACT = {"language": "zh-hans", "country": "CHN", "timeZone": "China Standard Time", "method": "email"}

S = lambda **p: {"type": "object", "properties": p, "additionalProperties": False}  # noqa: E731
STR, BOOL, INT = {"type": "string"}, {"type": "boolean"}, {"type": "integer"}
SCOPE = {"tenantId": {**STR, "description": "默认使用当前上下文"}, "subscriptionId": {**STR, "description": "空字符串表示租户级"}}
CONFIRM = {**BOOL, "description": "写操作需用户确认后传 true"}

TOOLS = [
    ("list_services", "列出 Azure 支持服务 (可按关键字过滤)", S(query=STR, **SCOPE), {"readOnlyHint": True}),
    ("list_problem_types", "列出某服务的问题类型", {**S(service=STR, **SCOPE), "required": ["service"]}, {"readOnlyHint": True}),
    ("draft_case", "用 AI 将自然语言描述起草为结构化 Case; questions 非空时应先向用户追问, 再带 answers 重新调用",
     {**S(text=STR, answers={"type": "array", "items": S(q=STR, a=STR)}, **SCOPE), "required": ["text"]}, {"readOnlyHint": True}),
    ("create_case", "创建 Support Case (需 confirm=true)。联系人默认中文/中国",
     {**S(title=STR, description=STR, serviceId=STR, problemClassificationId=STR,
          severity={"type": "string", "enum": ["minimal", "moderate", "critical", "highestcriticalimpact"]},
          resourceId=STR, require24X7Response=BOOL,
          contact=S(firstName=STR, lastName=STR, email=STR, phone=STR, language=STR, country=STR, timeZone=STR),
          confirm=CONFIRM, **SCOPE),
      "required": ["title", "description", "serviceId", "problemClassificationId", "severity", "contact", "confirm"]},
     {"destructiveHint": False, "idempotentHint": False}),
    ("list_cases", "列出最近的 Case", S(days=INT, **SCOPE), {"readOnlyHint": True}),
    ("get_case", "查看 Case 详情与沟通记录", {**S(name=STR, **SCOPE), "required": ["name"]}, {"readOnlyHint": True}),
    ("add_case_message", "向 Case 发送补充信息 (需 confirm=true)",
     {**S(name=STR, message=STR, confirm=CONFIRM, **SCOPE), "required": ["name", "message", "confirm"]}, {"destructiveHint": False}),
    ("close_case", "关闭 Case (需 confirm=true)", {**S(name=STR, confirm=CONFIRM, **SCOPE), "required": ["name", "confirm"]},
     {"destructiveHint": True, "idempotentHint": True}),
    ("get_support_plan", "查看订阅的支持计划 (Standard / ProDirect / Unified 等, 标注推断来源)", S(**SCOPE), {"readOnlyHint": True}),
]


class ToolError(Exception):
    pass


class Server:
    def __init__(self, context):
        self.context = context   # () -> {"tenantId", "subscriptionId"}

    def _scope(self, a):
        ctx = self.context()
        a = dict(a)
        a.setdefault("tenantId", ctx.get("tenantId"))
        a.setdefault("subscriptionId", ctx.get("subscriptionId"))
        a["subscriptionId"] = a["subscriptionId"] or None
        core.check_ids({k: a.get(k) for k in ("tenantId", "subscriptionId", "service", "name")})
        return a

    @staticmethod
    def _confirm(a):
        if a.get("confirm") is not True:
            raise ToolError("写操作需要用户确认: 请向用户展示将执行的内容, 确认后以 confirm=true 重新调用")

    def call(self, name, a):
        a = self._scope(a or {})
        t, sub = a["tenantId"], a["subscriptionId"]
        if name == "list_services":
            q = (a.get("query") or "").lower()
            return [{"id": s["id"], "name": s["name"], "displayName": s["displayName"]}
                    for s in core.list_services(t) if q in s["displayName"].lower()]
        if name == "list_problem_types":
            return core.list_problems(t, a["service"])
        if name == "draft_case":
            return service.ai_draft(a)
        if name == "create_case":
            self._confirm(a)
            contact = {**DEFAULT_CONTACT, **(a.get("contact") or {})}
            return core.create_ticket({**a, "contact": contact})
        if name == "list_cases":
            return core.list_tickets(t, sub, a.get("days") or 30)
        if name == "get_case":
            return core.ticket_detail(t, sub, a["name"])
        if name == "add_case_message":
            self._confirm(a)
            body = a["message"].replace("&", "&amp;").replace("<", "&lt;").replace("\n", "<br>")
            sender = core.ticket_detail(t, sub, a["name"]).get("properties", {}).get("contactDetails", {}).get("primaryEmailAddress")
            return core.add_communication(t, sub, a["name"], "客户补充信息 / Update from customer", body, sender=sender)
        if name == "close_case":
            self._confirm(a)
            return core.close_ticket(t, sub, a["name"])
        if name == "get_support_plan":
            return service.support_plan(a)
        raise ToolError(f"未知工具: {name}")

    def handle(self, msg):
        mid, method, params = msg.get("id"), msg.get("method"), msg.get("params") or {}
        if mid is None:
            return None   # notification
        try:
            if method == "initialize":
                result = {"protocolVersion": params.get("protocolVersion") or PROTOCOL,
                          "capabilities": {"tools": {"listChanged": False}},
                          "serverInfo": {"name": "casepilot", "title": "Azure CasePilot", "version": "2.0.0"},
                          "instructions": "创建 Case 前先用 draft_case 起草并向用户确认; 写操作需 confirm=true。"}
            elif method == "ping":
                result = {}
            elif method == "tools/list":
                result = {"tools": [{"name": n, "description": d, "inputSchema": s, "annotations": an} for n, d, s, an in TOOLS]}
            elif method == "tools/call":
                try:
                    data = self.call(params.get("name"), params.get("arguments"))
                    result = {"content": [{"type": "text", "text": json.dumps(data, ensure_ascii=False, default=str)}],
                              "structuredContent": data if isinstance(data, dict) else {"items": data}}
                except (ToolError, core.ApiError) as e:
                    text = getattr(e, "message", None) or str(e)
                    result = {"content": [{"type": "text", "text": text}], "isError": True}
            else:
                return {"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": f"Method not found: {method}"}}
            return {"jsonrpc": "2.0", "id": mid, "result": result}
        except Exception as e:  # noqa: BLE001
            return {"jsonrpc": "2.0", "id": mid, "error": {"code": -32603, "message": f"{type(e).__name__}: {e}"}}

    def serve(self, inp=sys.stdin, out=sys.stdout):
        for line in inp:
            if not line.strip():
                continue
            try:
                resp = self.handle(json.loads(line))
            except json.JSONDecodeError:
                resp = {"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Parse error"}}
            if resp is not None:
                out.write(json.dumps(resp, ensure_ascii=False) + "\n")
                out.flush()


def _local_context():
    """stdio 模式: 环境变量优先, 否则取 Azure CLI 当前账号。"""
    cache = {}

    def ctx():
        if not cache:
            info = core.AUTH.use_cli() if not core.AUTH.mode else {}
            cache["tenantId"] = os.environ.get("CASEPILOT_TENANT_ID") or info.get("tenant")
            cache["subscriptionId"] = os.environ.get("CASEPILOT_SUBSCRIPTION_ID") or \
                core._run_az(["account", "show"]).get("id")
        return cache
    return ctx


def main():
    if os.environ.get("AZURE_FEDERATED_TOKEN_FILE"):
        core.AUTH.use_workload()
    Server(_local_context()).serve()


if __name__ == "__main__":
    main()
