import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from casepilot import providers  # noqa: E402


class AIConfigTest(unittest.TestCase):
    def test_defaults(self):
        c = providers.AIConfig.from_env({})
        self.assertEqual(c.chat_deployment, "gpt-6-luna")
        self.assertEqual(c.chat_endpoint, "https://westus3-azureai-service-507154.openai.azure.com")
        self.assertEqual(c.stt_provider, "azure-openai")

    def test_env_override_and_trailing_slash(self):
        c = providers.AIConfig.from_env({"CASEPILOT_AOAI_ENDPOINT": "https://x.openai.azure.com/", "CASEPILOT_AOAI_DEPLOYMENT": "m"})
        self.assertEqual(c.chat_endpoint, "https://x.openai.azure.com")
        self.assertEqual(c.chat_deployment, "m")

    def test_chat_request_shape(self):
        c = providers.AIConfig.from_env({})
        url, body = providers.chat_request(c, [{"role": "user", "content": "hi"}], {"type": "object"})
        self.assertEqual(url, c.chat_endpoint + "/openai/v1/chat/completions")
        self.assertEqual(body["model"], "gpt-6-luna")
        self.assertEqual(body["reasoning_effort"], "none")
        self.assertEqual(body["response_format"]["type"], "json_schema")

    def test_stt_urls(self):
        c = providers.AIConfig.from_env({})
        self.assertIn("/openai/deployments/gpt-4o-mini-transcribe/audio/transcriptions", providers.stt_url(c))
        s = providers.AIConfig.from_env({"CASEPILOT_STT_PROVIDER": "azure-speech"})
        self.assertIn("/speechtotext/transcriptions:transcribe", providers.stt_url(s))

    def test_multipart(self):
        body, ctype = providers.multipart({"language": "zh"}, ("file", "a.wav", b"RIFF", "audio/wav"))
        self.assertIn(b'name="language"', body)
        self.assertIn(b'filename="a.wav"', body)
        self.assertTrue(ctype.startswith("multipart/form-data; boundary="))

    def test_parse_transcripts(self):
        self.assertEqual(providers.parse_transcript("azure-openai", {"text": "你好"}), "你好")
        self.assertEqual(providers.parse_transcript("azure-speech", {"combinedPhrases": [{"text": "a"}, {"text": "b"}]}), "ab")


if __name__ == "__main__":
    unittest.main()
