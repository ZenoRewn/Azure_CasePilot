import os
import sys
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from casepilot import service  # noqa: E402

SVC = [{"id": "/providers/Microsoft.Support/services/svc-vm", "name": "svc-vm", "displayName": "VM Linux", "resourceTypes": []}]
PB = [{"id": "/p/ssh", "displayName": "SSH"}]


class ServiceTest(unittest.TestCase):
    def test_ai_draft_wires_catalog_and_chat(self):
        replies = iter([{"service": "svc-vm", "severity": "minimal", "title": "t", "description": "d", "questions": []},
                        {"problemId": "/p/ssh"}])
        with mock.patch.object(service.core, "list_services", return_value=SVC), \
             mock.patch.object(service.core, "list_problems", return_value=PB), \
             mock.patch.object(service, "chat", side_effect=lambda cfg, m, s: next(replies)):
            d = service.ai_draft({"tenantId": "t", "text": "ssh 不通"})
        self.assertTrue(d["ready"])
        self.assertEqual(d["problem"]["id"], "/p/ssh")

    def test_ai_draft_requires_text(self):
        with self.assertRaises(service.core.ApiError):
            service.ai_draft({"tenantId": "t", "text": "  "})

    def test_ai_config_override_ignores_unknown_keys(self):
        c = service.ai_config({"ai": {"chat_deployment": "gpt-6-sol", "evil": "x"}})
        self.assertEqual(c.chat_deployment, "gpt-6-sol")
        self.assertFalse(hasattr(c, "evil"))

    def test_transcribe_rejects_large_audio(self):
        with self.assertRaises(service.core.ApiError):
            service.transcribe({"audio": "A" * (service.MAX_AUDIO_B64 + 1)})

    def test_support_plan(self):
        with mock.patch.object(service.core, "list_tickets", return_value=[{"supportPlanType": "Standard", "createdDate": "x"}]), \
             mock.patch.object(service.core, "arm", return_value={"subscriptionPolicies": {"quotaId": "x"}}):
            p = service.support_plan({"tenantId": "t", "subscriptionId": "s"})
        self.assertEqual(p["tier"], "standard")


if __name__ == "__main__":
    unittest.main()
