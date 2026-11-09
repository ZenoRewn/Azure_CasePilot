import json
import os
import sys
import threading
import unittest
import urllib.error
import urllib.request
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import server  # noqa: E402


class Base(unittest.TestCase):
    MODE = "local"

    @classmethod
    def setUpClass(cls):
        cls.cfg = server.Config(mode=cls.MODE, host="127.0.0.1", port=0, api_key="k" * 32 if cls.MODE == "server" else "")
        cls.srv = server.make_server(cls.cfg)
        cls.port = cls.srv.server_address[1]
        cls.cfg.port = cls.port
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def req(self, path, body=None, headers=None, host=None):
        h = {"Host": host or f"127.0.0.1:{self.port}", **(headers or {})}
        r = urllib.request.Request(f"http://127.0.0.1:{self.port}{path}", json.dumps(body).encode() if body is not None else None, h)
        try:
            with urllib.request.urlopen(r) as resp:
                return resp.status, resp.read()
        except urllib.error.HTTPError as e:
            return e.code, e.read()


class LocalModeTest(Base):
    def test_index_and_assets_served(self):
        for p in ("/", "/js/ai.js", "/style.css", "/logo.png"):
            self.assertEqual(self.req(p)[0], 200, p)

    def test_health(self):
        self.assertEqual(self.req("/healthz")[0], 200)

    def test_api_requires_session_token(self):
        self.assertEqual(self.req("/api/status", {})[0], 403)
        self.assertEqual(self.req("/api/status", {}, {"X-CasePilot-Token": server.SESSION_TOKEN})[0], 200)

    def test_rejects_foreign_host(self):
        self.assertEqual(self.req("/", host="evil.com")[0], 403)

    def test_new_routes_registered(self):
        for r in ("ai/draft", "ai/transcribe", "ai/config", "plan"):
            self.assertIn(r, server.ROUTES)


class ServerModeTest(Base):
    MODE = "server"

    def test_any_host_and_health(self):
        self.assertEqual(self.req("/healthz", host="casepilot.contoso.com")[0], 200)

    def test_api_requires_bearer(self):
        self.assertEqual(self.req("/api/status", {}, host="casepilot.contoso.com")[0], 401)
        ok = self.req("/api/status", {}, {"Authorization": "Bearer " + "k" * 32}, host="casepilot.contoso.com")
        self.assertEqual(ok[0], 200)

    def test_mcp_http(self):
        h = {"Authorization": "Bearer " + "k" * 32, "Content-Type": "application/json"}
        code, body = self.req("/mcp", {"jsonrpc": "2.0", "id": 1, "method": "tools/list"}, h)
        self.assertEqual(code, 200)
        self.assertGreaterEqual(len(json.loads(body)["result"]["tools"]), 9)

    def test_server_mode_disables_interactive_login_and_quit(self):
        h = {"Authorization": "Bearer " + "k" * 32}
        for r in ("login/cli", "login/device/start", "quit"):
            self.assertEqual(self.req("/api/" + r, {}, h)[0], 403, r)


class ConfigTest(unittest.TestCase):
    def test_server_mode_requires_api_key(self):
        with self.assertRaises(SystemExit):
            server.Config.from_env({"CASEPILOT_MODE": "server"})

    def test_ignores_k8s_service_link_env(self):
        # K8s 为名为 casepilot 的 Service 注入 CASEPILOT_PORT=tcp://10.0.0.1:80
        c = server.Config.from_env({"CASEPILOT_MODE": "server", "CASEPILOT_API_KEY": "x" * 32, "CASEPILOT_PORT": "tcp://10.0.0.1:80"})
        self.assertEqual(c.port, 8080)

    def test_env(self):
        c = server.Config.from_env({"CASEPILOT_MODE": "server", "CASEPILOT_API_KEY": "x" * 32, "CASEPILOT_PORT": "9000"})
        self.assertEqual((c.mode, c.host, c.port), ("server", "0.0.0.0", 9000))


if __name__ == "__main__":
    unittest.main()
