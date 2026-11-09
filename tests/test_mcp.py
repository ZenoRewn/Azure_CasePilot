import io
import json
import os
import sys
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from casepilot import mcp  # noqa: E402


T, SUB = "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"


def rpc(server, method, params=None, id=1):
    return server.handle({"jsonrpc": "2.0", "id": id, "method": method, "params": params or {}})


class MCPTest(unittest.TestCase):
    def setUp(self):
        self.s = mcp.Server(context=lambda: {"tenantId": T, "subscriptionId": SUB})

    def test_initialize(self):
        r = rpc(self.s, "initialize", {"protocolVersion": "2025-06-18"})["result"]
        self.assertEqual(r["serverInfo"]["name"], "casepilot")
        self.assertIn("tools", r["capabilities"])

    def test_notification_has_no_response(self):
        self.assertIsNone(self.s.handle({"jsonrpc": "2.0", "method": "notifications/initialized"}))

    def test_tools_list_has_core_tools_with_schemas(self):
        tools = {t["name"]: t for t in rpc(self.s, "tools/list")["result"]["tools"]}
        for n in ("list_services", "list_problem_types", "draft_case", "create_case", "list_cases", "get_case",
                  "add_case_message", "close_case", "get_support_plan"):
            self.assertIn(n, tools)
            self.assertEqual(tools[n]["inputSchema"]["type"], "object")
        self.assertTrue(tools["create_case"]["annotations"]["destructiveHint"] is False)
        self.assertTrue(tools["close_case"]["annotations"]["destructiveHint"])

    def test_create_requires_confirm(self):
        r = rpc(self.s, "tools/call", {"name": "create_case", "arguments": {"title": "t"}})["result"]
        self.assertTrue(r["isError"])
        self.assertIn("confirm", r["content"][0]["text"])

    def test_create_with_confirm_calls_core_with_context(self):
        captured = {}
        with mock.patch.object(mcp.core, "create_ticket", side_effect=lambda f: captured.update(f) or {"supportTicketId": "123"}):
            r = rpc(self.s, "tools/call", {"name": "create_case", "arguments": {
                "confirm": True, "title": "t", "description": "d", "serviceId": "/x", "problemClassificationId": "/p",
                "severity": "minimal", "contact": {"firstName": "a", "lastName": "b", "email": "a@b.com"}}})["result"]
        self.assertFalse(r.get("isError"))
        self.assertEqual(json.loads(r["content"][0]["text"])["supportTicketId"], "123")
        self.assertEqual(captured["tenantId"], T)
        self.assertEqual(captured["contact"]["language"], "zh-hans")   # 默认中文 / 中国
        self.assertEqual(captured["contact"]["country"], "CHN")

    def test_errors_become_tool_errors(self):
        with mock.patch.object(mcp.core, "list_tickets", side_effect=mcp.core.ApiError(403, "AuthorizationFailed")):
            r = rpc(self.s, "tools/call", {"name": "list_cases", "arguments": {}})["result"]
        self.assertTrue(r["isError"])
        self.assertIn("AuthorizationFailed", r["content"][0]["text"])

    def test_rejects_bad_ids(self):
        r = rpc(self.s, "tools/call", {"name": "get_case", "arguments": {"name": "../x"}})["result"]
        self.assertTrue(r["isError"])

    def test_unknown_method(self):
        self.assertEqual(rpc(self.s, "nope")["error"]["code"], -32601)

    def test_stdio_loop(self):
        inp = io.StringIO(json.dumps({"jsonrpc": "2.0", "id": 7, "method": "ping"}) + "\n")
        out = io.StringIO()
        self.s.serve(inp, out)
        self.assertEqual(json.loads(out.getvalue())["id"], 7)


if __name__ == "__main__":
    unittest.main()
