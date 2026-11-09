import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from casepilot import core as server  # noqa: E402  (核心逻辑已移至 casepilot.core)

CONTACT = {"firstName": "三", "lastName": "张", "email": "a@b.com", "timeZone": "China Standard Time",
           "language": "zh-hans", "country": "CHN"}
FORM = {"title": "标题测试", "description": "描述", "serviceId": "/providers/Microsoft.Support/services/x",
        "problemClassificationId": "/p", "severity": "moderate", "contact": CONTACT}


class BuildTicketTest(unittest.TestCase):
    def test_minimal_ticket(self):
        p = server.build_ticket(FORM)["properties"]
        self.assertEqual(p["severity"], "moderate")
        self.assertEqual(p["contactDetails"]["preferredSupportLanguage"], "zh-hans")
        self.assertNotIn("technicalTicketDetails", p)
        self.assertNotIn("quotaTicketDetails", p)

    def test_resource_and_cc(self):
        p = server.build_ticket({**FORM, "resourceId": "/subscriptions/1/r",
                                 "contact": {**CONTACT, "cc": "x@y.com, ,z@y.com"}})["properties"]
        self.assertEqual(p["technicalTicketDetails"]["resourceId"], "/subscriptions/1/r")
        self.assertEqual(p["contactDetails"]["additionalEmailAddresses"], ["x@y.com", "z@y.com"])

    def test_quota_payload_uses_vmfamily(self):
        p = server.build_ticket({**FORM, "quota": {"region": "eastus", "sku": "DSv5 Series", "newLimit": "100"}})
        req = p["properties"]["quotaTicketDetails"]["quotaChangeRequests"][0]
        self.assertEqual(req["region"], "eastus")
        self.assertIn('"VMFamily": "DSv5 Series"', req["payload"])
        self.assertIn('"NewLimit": 100', req["payload"])


class ValidateTest(unittest.TestCase):
    def test_missing_fields(self):
        with self.assertRaises(server.ApiError) as c:
            server.validate({**FORM, "title": " "})
        self.assertIn("title", c.exception.message)

    def test_bad_severity(self):
        with self.assertRaises(server.ApiError):
            server.validate({**FORM, "severity": "urgent"})

    def test_phone_required_when_preferred(self):
        with self.assertRaises(server.ApiError):
            server.validate({**FORM, "contact": {**CONTACT, "method": "phone"}})


class CommunicationTest(unittest.TestCase):
    def test_sender_included_when_given(self):
        from unittest import mock
        sent = {}
        with mock.patch.object(server, "arm", side_effect=lambda m, u, t, b: sent.update(b)):
            server.add_communication("t", "s", "n", "subj", "body", sender="a@b.com")
        self.assertEqual(sent["properties"]["sender"], "a@b.com")

    def test_sender_omitted_by_default(self):
        from unittest import mock
        sent = {}
        with mock.patch.object(server, "arm", side_effect=lambda m, u, t, b: sent.update(b)):
            server.add_communication("t", "s", "n", "subj", "body")
        self.assertNotIn("sender", sent["properties"])


class CheckIdsTest(unittest.TestCase):
    def test_accepts_valid(self):
        server.check_ids({"tenantId": "5b96596a-fc1b-47ce-81c1-7979d58fe273", "name": "casepilot-abc_1.x"})

    def test_rejects_path_injection(self):
        for bad in ({"subscriptionId": "../../x"}, {"name": "a/b"}, {"service": "x?y"}):
            with self.assertRaises(server.ApiError):
                server.check_ids(bad)


if __name__ == "__main__":
    unittest.main()
