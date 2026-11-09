import os
import sys
import time
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from casepilot import core  # noqa: E402


class TokenResourceTest(unittest.TestCase):
    def setUp(self):
        self.a = core.Auth()
        self.a.mode = "cli"

    def test_cli_requests_given_resource_and_caches_per_resource(self):
        seen = []

        def fake_az(args, hint_tenant=None):
            seen.append(args[args.index("--resource") + 1])
            return {"accessToken": "t" + str(len(seen)), "expires_on": time.time() + 3600}
        with mock.patch.object(core, "_run_az", fake_az):
            arm = self.a.token("t1")
            cog = self.a.token("t1", resource="https://cognitiveservices.azure.com")
            again = self.a.token("t1", resource="https://cognitiveservices.azure.com")
        self.assertEqual(seen, ["https://management.azure.com/", "https://cognitiveservices.azure.com/"])
        self.assertNotEqual(arm, cog)
        self.assertEqual(cog, again)

    def test_sp_uses_resource_default_scope(self):
        self.a.mode = "sp"
        self.a.sp = {"tenant": "t1", "client_id": "c", "secret": "s"}
        scopes = []

        def fake_post(url, fields):
            scopes.append(fields["scope"])
            return {"access_token": "x", "expires_in": 3600}
        with mock.patch.object(core, "_post_form", fake_post):
            self.a.token(resource="https://cognitiveservices.azure.com")
        self.assertEqual(scopes, ["https://cognitiveservices.azure.com/.default"])


class WorkloadIdentityTest(unittest.TestCase):
    def test_exchanges_federated_token(self):
        import tempfile
        f = tempfile.NamedTemporaryFile("w", delete=False); f.write("fed-jwt"); f.close()
        env = {"AZURE_FEDERATED_TOKEN_FILE": f.name, "AZURE_CLIENT_ID": "cid", "AZURE_TENANT_ID": "tid",
               "AZURE_AUTHORITY_HOST": "https://login.microsoftonline.com/"}
        sent = {}
        with mock.patch.dict(os.environ, env), mock.patch.object(core, "_post_form",
                side_effect=lambda u, fl: sent.update(url=u, **fl) or {"access_token": "at", "expires_in": 3600}):
            a = core.Auth()
            a.use_workload()
            tok = a.token(resource="https://cognitiveservices.azure.com")
        self.assertEqual(tok, "at")
        self.assertEqual(sent["url"], "https://login.microsoftonline.com/tid/oauth2/v2.0/token")
        self.assertEqual(sent["client_assertion"], "fed-jwt")
        self.assertEqual(sent["scope"], "https://cognitiveservices.azure.com/.default")

    def test_missing_env_is_clear_error(self):
        with mock.patch.dict(os.environ, {}, clear=True), self.assertRaises(core.ApiError):
            core.Auth().use_workload()


if __name__ == "__main__":
    unittest.main()
