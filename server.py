#!/usr/bin/env python3
"""Azure CasePilot — Web / 服务器 / MCP 入口.

两种运行模式:
  local  (默认): 仅监听 127.0.0.1, Azure CLI / 设备码 / App Registration 登录, 随机会话令牌防 CSRF。
  server       : 监听 0.0.0.0 供容器 / AKS 使用; 使用 Workload Identity 或 App Registration (环境变量),
                 所有 API 与 /mcp 需 Authorization: Bearer <CASEPILOT_API_KEY> (或前置 Entra 认证网关)。
零第三方依赖 (Python 3.9+ 标准库)。
"""
import json
import os
import secrets
import sys
import threading
import time
import urllib.parse
import webbrowser
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# 嵌入式 Python (如 Azure CLI 自带, 含 ._pth) 不会自动把脚本目录加入 sys.path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from casepilot.core import (AUTH, STATIC, ApiError, add_communication, arm_list, check_ids, close_ticket,  # noqa: F401
                            create_ticket, list_all_resources, list_problems, list_resources, list_services,
                            list_tickets, ticket_detail)
from casepilot import mcp, service

SESSION_TOKEN = secrets.token_urlsafe(24)


@dataclass
class Config:
    mode: str = "local"
    host: str = "127.0.0.1"
    port: int = 8765
    api_key: str = ""

    @staticmethod
    def from_env(env):
        mode = env.get("CASEPILOT_MODE", "local")
        raw = env.get("CASEPILOT_PORT", "")   # K8s 会为同名 Service 注入 tcp://ip:port, 需忽略
        c = Config(mode=mode, host="0.0.0.0" if mode == "server" else "127.0.0.1",
                   port=int(raw) if raw.isdigit() else (8080 if mode == "server" else 8765),
                   api_key=env.get("CASEPILOT_API_KEY", ""))
        if mode == "server" and len(c.api_key) < 24 and env.get("CASEPILOT_TRUST_PROXY_AUTH") != "1":
            sys.exit("server 模式需设置 CASEPILOT_API_KEY (>=24 字符), 或在 Entra 认证网关后设置 CASEPILOT_TRUST_PROXY_AUTH=1")
        return c


SERVER_BLOCKED = {"login/cli", "login/device/start", "login/device/poll", "login/sp", "logout", "quit"}

# ---------------------------------------------------------------- HTTP

STATIC_FILES = {**{f"/js/{n}.js": "text/javascript; charset=utf-8" for n in ("core", "auth", "create", "history", "detail", "ai", "main", "pcm-worklet")},
                "/style.css": "text/css; charset=utf-8",
                "/logo.png": "image/png", "/favicon.ico": "image/png"}


class Handler(BaseHTTPRequestHandler):
    server_version = "CasePilot/1.0"

    def log_message(self, fmt, *args):
        sys.stderr.write("[%s] %s\n" % (time.strftime("%H:%M:%S"), fmt % args))

    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        data = body if isinstance(body, bytes) else json.dumps(body, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; media-src 'self' blob:")
        self.end_headers()
        self.wfile.write(data)

    cfg = Config()

    @property
    def server_mode(self):
        return self.cfg.mode == "server"

    def _host_ok(self):
        return self.server_mode or self.headers.get("Host", "") in (f"127.0.0.1:{self.cfg.port}", f"localhost:{self.cfg.port}")

    def _authorized(self):
        if self.server_mode:
            if os.environ.get("CASEPILOT_TRUST_PROXY_AUTH") == "1" and self.headers.get("X-MS-CLIENT-PRINCIPAL-NAME"):
                return True
            got = self.headers.get("Authorization", "").removeprefix("Bearer ").strip()
            return bool(self.cfg.api_key) and secrets.compare_digest(got, self.cfg.api_key)
        return secrets.compare_digest(self.headers.get("X-CasePilot-Token", ""), SESSION_TOKEN)

    def do_GET(self):
        path = urllib.parse.urlparse(self.path).path
        if path in ("/healthz", "/readyz"):
            return self._send(200, {"ok": True, "mode": self.cfg.mode, "auth": AUTH.mode})
        if not self._host_ok():
            return self._send(403, {"error": "forbidden host"})
        if path == "/":
            html = (STATIC / "index.html").read_text(encoding="utf-8").replace("__SESSION_TOKEN__", SESSION_TOKEN)
            return self._send(200, html.encode(), "text/html; charset=utf-8")
        ctype = STATIC_FILES.get(path)
        if ctype:
            return self._send(200, (STATIC / ("logo.png" if path == "/favicon.ico" else path[1:])).read_bytes(), ctype)
        self._send(404, {"error": "not found"})

    def do_POST(self):
        if not self._host_ok():
            return self._send(403, {"error": "forbidden host"})
        if not self._authorized():
            return self._send(401 if self.server_mode else 403, {"error": "unauthorized"})
        path = urllib.parse.urlparse(self.path).path
        try:
            n = int(self.headers.get("Content-Length") or 0)
            if n > 16 * 1024 * 1024:
                return self._send(413, {"error": "payload too large"})
            req = json.loads(self.rfile.read(n) or b"{}")
            if path == "/mcp":
                resp = MCP.handle(req)
                return self._send(202 if resp is None else 200, resp or b"")
            action = path.removeprefix("/api/")
            if self.server_mode and action in SERVER_BLOCKED:
                return self._send(403, {"ok": False, "error": "服务器模式下认证由部署配置决定"})
            fn = ROUTES.get(action)
            check_ids(req)
            if not fn:
                return self._send(404, {"error": "unknown api"})
            self._send(200, {"ok": True, "data": fn(req)})
        except ApiError as e:
            self._send(e.status if 400 <= e.status < 600 else 500,
                       {"ok": False, "error": e.message, "detail": e.detail})
        except Exception as e:  # noqa: BLE001
            self._send(500, {"ok": False, "error": f"{type(e).__name__}: {e}"})


ROUTES = {
    "status": lambda r: AUTH.status(),
    "login/cli": lambda r: AUTH.use_cli(),
    "login/sp": lambda r: AUTH.use_sp(r.get("tenantId", ""), r.get("clientId", ""), r.get("clientSecret", ""),
                                      r.get("cloud", "AzureCloud")),
    "logout": lambda r: AUTH.logout(),
    "quit": lambda r: threading.Timer(0.3, lambda: os._exit(0)).start(),
    "login/device/start": lambda r: AUTH.device_start(r.get("cloud", "AzureCloud")),
    "login/device/poll": lambda r: AUTH.device_poll(),
    "tenants": lambda r: [{"tenantId": t["tenantId"], "displayName": t.get("displayName") or t["tenantId"],
                           "domain": (t.get("defaultDomain") or "")}
                          for t in arm_list("/tenants?api-version=2022-12-01",
                                            AUTH.sp["tenant"] if AUTH.mode == "sp" else None)],
    "subscriptions": lambda r: sorted(({"subscriptionId": s["subscriptionId"], "displayName": s["displayName"],
                                        "state": s["state"]}
                                       for s in arm_list("/subscriptions?api-version=2022-12-01", r["tenantId"])
                                       if s.get("tenantId", r["tenantId"]) == r["tenantId"]),
                                      key=lambda s: s["displayName"].lower()),
    "services": lambda r: list_services(r["tenantId"]),
    "problems": lambda r: list_problems(r["tenantId"], r["service"]),
    "allResources": lambda r: list_all_resources(r["tenantId"], r["subscriptionId"]),
    "resources": lambda r: list_resources(r["tenantId"], r["subscriptionId"], r.get("resourceTypes", [])),
    "create": create_ticket,
    "tickets": lambda r: list_tickets(r["tenantId"], r.get("subscriptionId"), r.get("days", 30)),
    "ticket": lambda r: ticket_detail(r["tenantId"], r.get("subscriptionId"), r.get("name")),
    "close": lambda r: close_ticket(r["tenantId"], r.get("subscriptionId"), r.get("name")),
    "ai/draft": service.ai_draft,
    "ai/transcribe": service.transcribe,
    "ai/stream/start": service.stream_start,
    "ai/stream/push": service.stream_push,
    "ai/stream/stop": service.stream_stop,
    "ai/config": lambda r: service.ai_config(r).public(),
    "plan": service.support_plan,
    "reply": lambda r: add_communication(r["tenantId"], r.get("subscriptionId"), r.get("name"),
                                         r.get("subject"), r.get("body"),
                                         sender=r.get("sender") or (_case_sender(r) if AUTH.mode in ("sp", "workload") else None)),
}


def _case_sender(r):
    return ticket_detail(r["tenantId"], r.get("subscriptionId"), r.get("name")).get("properties", {}) \
        .get("contactDetails", {}).get("primaryEmailAddress")


MCP = mcp.Server(context=lambda: {"tenantId": os.environ.get("CASEPILOT_TENANT_ID") or os.environ.get("AZURE_TENANT_ID"),
                                  "subscriptionId": os.environ.get("CASEPILOT_SUBSCRIPTION_ID")})


class _Server(ThreadingHTTPServer):
    # Windows 的 SO_REUSEADDR 允许其他进程绑定同一已占用端口 (端口劫持/静默双开)
    allow_reuse_address = os.name != "nt"

    def server_bind(self):
        if os.name == "nt":
            import socket
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def make_server(cfg):
    Handler.cfg = cfg
    return _Server((cfg.host, cfg.port), Handler)


def _safe_console():
    """Windows 控制台/重定向常为 cp1252/cp936: 无法编码的字符替换, 避免中文日志导致启动崩溃。"""
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            # 重定向到文件/管道时输出 UTF-8 日志; 真实控制台 (WriteConsoleW) 不受编码影响
            stream.reconfigure(encoding="utf-8" if not stream.isatty() else None, errors="replace")


def main():
    _safe_console()
    cfg = Config.from_env(os.environ)
    if cfg.mode == "server":
        if os.environ.get("AZURE_FEDERATED_TOKEN_FILE"):
            AUTH.use_workload()
        elif os.environ.get("AZURE_CLIENT_SECRET"):
            AUTH.use_sp(os.environ["AZURE_TENANT_ID"], os.environ["AZURE_CLIENT_ID"], os.environ["AZURE_CLIENT_SECRET"], "AzureCloud")
        srv = make_server(cfg)
        print(f"CasePilot server 模式: 0.0.0.0:{cfg.port} auth={AUTH.mode}", flush=True)
    else:
        for port in range(cfg.port, cfg.port + 20):
            try:
                cfg.port = port
                srv = make_server(cfg)
                break
            except OSError:
                continue
        else:
            sys.exit("端口均被占用, 请设置 CASEPILOT_PORT")
        url = f"http://127.0.0.1:{cfg.port}/"
        print(f"Azure CasePilot 已启动: {url}  (Ctrl+C 退出)", flush=True)
        if os.environ.get("CASEPILOT_NO_BROWSER") != "1":
            threading.Timer(0.6, lambda: webbrowser.open(url)).start()
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
