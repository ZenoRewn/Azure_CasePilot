import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from casepilot import plan  # noqa: E402


class PlanTest(unittest.TestCase):
    def test_from_recent_ticket(self):
        p = plan.infer([{"supportPlanType": "Unified Enterprise", "createdDate": "2026-11-01"}], {"quotaId": "EnterpriseAgreement_2014-09-01"})
        self.assertEqual(p["plan"], "Unified Enterprise")
        self.assertEqual(p["tier"], "unified")
        self.assertEqual(p["source"], "ticket")
        self.assertTrue(p["allowsCritical"])

    def test_newest_ticket_wins(self):
        p = plan.infer([{"supportPlanType": "Basic", "createdDate": "2025-01-01"},
                        {"supportPlanType": "ProDirect", "createdDate": "2026-01-01"}], {})
        self.assertEqual(p["tier"], "prodirect")

    def test_offer_hint_without_tickets(self):
        p = plan.infer([], {"quotaId": "MSDN_2014-09-01"})
        self.assertEqual(p["source"], "offer")
        self.assertEqual(p["tier"], "developer-or-basic")
        self.assertFalse(p["allowsCritical"])

    def test_unknown(self):
        p = plan.infer([], {"quotaId": "PayAsYouGo_2014-09-01"})
        self.assertEqual(p["tier"], "unknown")
        self.assertIsNone(p["allowsCritical"])

    def test_tiers(self):
        for name, tier in [("Azure Support Plan - Internal", "internal"), ("Standard", "standard"),
                           ("Premier", "premier"), ("Developer", "developer-or-basic")]:
            self.assertEqual(plan.tier_of(name), tier, name)


if __name__ == "__main__":
    unittest.main()
