#!/usr/bin/env python3
"""Azure CasePilot — 本地 Web 工具, 快速创建 Azure Support Case.

零第三方依赖 (仅 Python 3.9+ 标准库)。认证两种方式:
  1. Azure CLI: 复用本机 `az login` 会话 (az account get-access-token)
  2. App Registration: client_credentials (Client ID + Secret)

仅监听 127.0.0.1, 每次启动生成随机会话令牌防 CSRF / DNS rebinding。
"""
import json
import re
import os
import secrets
import shutil
import ssl
import tempfile
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from casepilot.core import *  # noqa: F401,F403  (Tidy First: 核心逻辑移至 casepilot/core.py)
from casepilot import core

# ---------------------------------------------------------------- HTTP

STATIC_FILES = {**{f"/js/{n}.js": "text/javascript; charset=utf-8" for n in ("core", "auth", "create", "history", "detail", "main")},
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
        self.send_header("Content-Security-Policy", "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:")
        self.end_headers()
        self.wfile.write(data)

    def _host_ok(self):
        return self.headers.get("Host", "") in (f"127.0.0.1:{PORT}", f"localhost:{PORT}")

    def do_GET(self):
        if not self._host_ok():
            return self._send(403, {"error": "forbidden host"})
        path = urllib.parse.urlparse(self.path).path
        if path == "/":
            html = (STATIC / "index.html").read_text(encoding="utf-8").replace("__SESSION_TOKEN__", SESSION_TOKEN)
            return self._send(200, html.encode(), "text/html; charset=utf-8")
        ctype = STATIC_FILES.get(path)
        if ctype:
            return self._send(200, (STATIC / ("logo.png" if path == "/favicon.ico" else path[1:])).read_bytes(), ctype)
        self._send(404, {"error": "not found"})

    def do_POST(self):
        if not self._host_ok() or not secrets.compare_digest(self.headers.get("X-CasePilot-Token", ""), SESSION_TOKEN):
            return self._send(403, {"error": "invalid session token"})
        try:
            n = int(self.headers.get("Content-Length") or 0)
            req = json.loads(self.rfile.read(n) or b"{}")
            action = urllib.parse.urlparse(self.path).path.removeprefix("/api/")
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
    "reply": lambda r: add_communication(r["tenantId"], r.get("subscriptionId"), r.get("name"),
                                         r.get("subject"), r.get("body")),
}


def main():
    global PORT
    for port in range(PORT, PORT + 20):
        try:
            srv = ThreadingHTTPServer((HOST, port), Handler)
            break
        except OSError:
            continue
    else:
        sys.exit(f"端口 {PORT}-{PORT + 19} 均被占用, 请设置 CASEPILOT_PORT")
    PORT = port
    url = f"http://127.0.0.1:{PORT}/"
    print(f"Azure CasePilot 已启动: {url}  (Ctrl+C 退出)")
    if os.environ.get("CASEPILOT_NO_BROWSER") != "1":
        threading.Timer(0.6, lambda: webbrowser.open(url)).start()
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
