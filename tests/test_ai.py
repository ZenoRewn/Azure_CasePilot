import json
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from casepilot import ai  # noqa: E402

SERVICES = [{"name": "svc-vm", "displayName": "Virtual Machine running Linux"},
            {"name": "svc-aoai", "displayName": "Azure OpenAI"}]
PROBLEMS = [{"id": "/p/ssh", "displayName": "Cannot connect to my VM / SSH"},
            {"id": "/p/perf", "displayName": "VM Performance / CPU high"}]


def fake_chat(*replies):
    calls = []

    def chat(messages, schema):
        calls.append({"messages": messages, "schema": schema})
        return replies[len(calls) - 1]
    chat.calls = calls
    return chat


class DraftTest(unittest.TestCase):
    def test_picks_service_and_problem_from_catalog(self):
        chat = fake_chat({"service": "svc-vm", "severity": "moderate", "title": "VM SSH 超时",
                          "description": "【现象】SSH 超时", "questions": []},
                         {"problemId": "/p/ssh"})
        d = ai.draft_case("我的 linux 虚拟机 ssh 连不上", [], SERVICES, lambda s: PROBLEMS, chat)
        self.assertEqual(d["service"]["name"], "svc-vm")
        self.assertEqual(d["problem"]["id"], "/p/ssh")
        self.assertEqual(d["severity"], "moderate")
        self.assertEqual(d["questions"], [])
        self.assertTrue(d["ready"])

    def test_accepts_display_name_as_service(self):
        chat = fake_chat({"service": "Azure OpenAI", "severity": "moderate", "title": "t", "description": "d", "questions": []},
                         {"problemId": "/p/ssh"})
        self.assertEqual(ai.draft_case("x", [], SERVICES, lambda s: PROBLEMS, chat)["service"]["name"], "svc-aoai")

    def test_accepts_problem_display_name(self):
        chat = fake_chat({"service": "svc-vm", "severity": "moderate", "title": "t", "description": "d", "questions": []},
                         {"problemId": "VM Performance / CPU high"})
        self.assertEqual(ai.draft_case("x", [], SERVICES, lambda s: PROBLEMS, chat)["problem"]["id"], "/p/perf")

    def test_no_more_questions_after_answer_round(self):
        chat = fake_chat({"service": "svc-vm", "severity": "moderate", "title": "t", "description": "d", "questions": ["还有吗?"]},
                         {"problemId": "/p/ssh"})
        d = ai.draft_case("x", [{"q": "a", "a": "b"}], SERVICES, lambda s: PROBLEMS, chat)
        self.assertEqual(d["questions"], [])
        self.assertTrue(d["ready"])

    def test_rejects_hallucinated_service(self):
        chat = fake_chat({"service": "made-up", "severity": "moderate", "title": "t", "description": "d", "questions": []})
        d = ai.draft_case("x", [], SERVICES, lambda s: PROBLEMS, chat)
        self.assertIsNone(d["service"])
        self.assertFalse(d["ready"])

    def test_rejects_hallucinated_problem(self):
        chat = fake_chat({"service": "svc-vm", "severity": "minimal", "title": "t", "description": "d", "questions": []},
                         {"problemId": "/p/nope"})
        d = ai.draft_case("x", [], SERVICES, lambda s: PROBLEMS, chat)
        self.assertIsNone(d["problem"])
        self.assertFalse(d["ready"])

    def test_invalid_severity_falls_back_to_moderate(self):
        chat = fake_chat({"service": "svc-vm", "severity": "urgent!!", "title": "t", "description": "d", "questions": []},
                         {"problemId": "/p/ssh"})
        self.assertEqual(ai.draft_case("x", [], SERVICES, lambda s: PROBLEMS, chat)["severity"], "moderate")

    def test_questions_make_draft_not_ready_and_capped(self):
        qs = [f"问题{i}?" for i in range(6)]
        chat = fake_chat({"service": "svc-vm", "severity": "moderate", "title": "t", "description": "d", "questions": qs},
                         {"problemId": "/p/ssh"})
        d = ai.draft_case("x", [], SERVICES, lambda s: PROBLEMS, chat)
        self.assertEqual(len(d["questions"]), 3)
        self.assertFalse(d["ready"])

    def test_answers_are_sent_to_model(self):
        chat = fake_chat({"service": "svc-vm", "severity": "moderate", "title": "t", "description": "d", "questions": []},
                         {"problemId": "/p/ssh"})
        ai.draft_case("ssh 不通", [{"q": "何时开始?", "a": "今天 10 点"}], SERVICES, lambda s: PROBLEMS, chat)
        sent = json.dumps(chat.calls[0]["messages"], ensure_ascii=False)
        self.assertIn("今天 10 点", sent)
        self.assertIn("Virtual Machine running Linux", sent)

    def test_title_truncated_to_200(self):
        chat = fake_chat({"service": "svc-vm", "severity": "moderate", "title": "长" * 300, "description": "d", "questions": []},
                         {"problemId": "/p/ssh"})
        self.assertEqual(len(ai.draft_case("x", [], SERVICES, lambda s: PROBLEMS, chat)["title"]), 200)


class ShortlistTest(unittest.TestCase):
    def test_keyword_hits_and_popular_kept_and_capped(self):
        many = [{"name": f"s{i}", "displayName": f"Service {i}"} for i in range(300)]
        many += [{"name": "aoai", "displayName": "Azure OpenAI"}, {"name": "vm", "displayName": "Virtual Machine running Linux"}]
        short = ai.shortlist("openai 部署 429", many, limit=40)
        names = [s["name"] for s in short]
        self.assertLessEqual(len(short), 40)
        self.assertIn("aoai", names)
        self.assertIn("vm", names)  # 常用服务始终保留

    def test_draft_sends_only_shortlist(self):
        many = [{"name": f"s{i}", "displayName": f"Service {i}"} for i in range(300)] + SERVICES
        chat = fake_chat({"service": "svc-vm", "severity": "moderate", "title": "t", "description": "d", "questions": []},
                         {"problemId": "/p/ssh"})
        ai.draft_case("linux 虚拟机 ssh", [], many, lambda s: PROBLEMS, chat)
        self.assertLess(len(chat.calls[0]["messages"][1]["content"]), 4000)


class SchemaTest(unittest.TestCase):
    def test_service_schema_is_strict(self):
        s = ai.SERVICE_SCHEMA
        self.assertFalse(s["additionalProperties"])
        self.assertEqual(set(s["required"]), set(s["properties"]))


if __name__ == "__main__":
    unittest.main()
