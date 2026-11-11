import io
import os
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
from casepilot import core  # noqa: E402


def _free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def _fake_popen(calls, stdout):
    class P:
        pid, returncode = 1, 0

        def __init__(self, *a, **kw):
            calls.update(kw)

        def communicate(self, timeout=None):
            return stdout, ""
    return P


class EmbeddedPythonTest(unittest.TestCase):
    def test_server_imports_without_script_dir_on_path(self):
        # az CLI 自带的嵌入式 Python (._pth) 不会把脚本目录加入 sys.path, 等价于 -I
        code = f"import runpy; runpy.run_path({os.path.join(ROOT, 'server.py')!r}, run_name='probe')"
        p = subprocess.run([sys.executable, "-I", "-c", code], capture_output=True, text=True,
                           cwd=tempfile.gettempdir(), timeout=60)
        self.assertEqual(p.returncode, 0, p.stderr)


class ConsoleEncodingTest(unittest.TestCase):
    def test_startup_banner_survives_non_utf8_console(self):
        # 重定向到文件/管道时 Windows 默认 cp1252/cp936, 中文横幅不能导致崩溃
        env = {**os.environ, "PYTHONIOENCODING": "cp1252", "CASEPILOT_NO_BROWSER": "1",
               "CASEPILOT_PORT": str(_free_port()), "CASEPILOT_MODE": "local"}
        p = subprocess.Popen([sys.executable, os.path.join(ROOT, "server.py")], env=env,
                             stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        try:
            line = p.stdout.readline().decode("utf-8", "replace")
        finally:
            p.kill()
            p.wait()
            p.stdout.close()
        self.assertIn("http://127.0.0.1:", line)


class RunAzEncodingTest(unittest.TestCase):
    def test_run_az_decodes_utf8_regardless_of_locale(self):
        calls = {}

        with mock.patch.object(core.subprocess, "Popen", _fake_popen(calls, '{"n": "中文"}')):
            out = core._run_az(["account", "show"])
        self.assertEqual(calls.get("encoding"), "utf-8")
        self.assertEqual(out, {"n": "中文"})
        self.assertEqual(calls["env"]["PYTHONIOENCODING"], "utf-8")


    def test_run_az_hides_console_window_on_windows(self):
        calls = {}

        with mock.patch.object(core.subprocess, "Popen", _fake_popen(calls, "{}")):
            core._run_az(["account", "show"])
        self.assertEqual(calls.get("creationflags", 0), getattr(subprocess, "CREATE_NO_WINDOW", 0))

    def test_run_az_timeout_kills_process_and_raises_api_error(self):
        killed = []

        class FakeProc:
            pid = 4242
            returncode = None

            def communicate(self, timeout=None):
                if not killed:
                    raise subprocess.TimeoutExpired("az", timeout)
                return "", ""

        with mock.patch.object(core.subprocess, "Popen", lambda *a, **kw: FakeProc()),                 mock.patch.object(core, "_kill_tree", lambda p: killed.append(p.pid)):
            with self.assertRaises(core.ApiError) as cm:
                core._run_az(["account", "show"])
        self.assertEqual(killed, [4242])
        self.assertIn("超时", cm.exception.message)


class KillTreeTest(unittest.TestCase):
    def test_kill_tree_uses_taskkill_tree_on_windows(self):
        runs = []
        p = mock.Mock(pid=42)
        with mock.patch.object(core.subprocess, "run", lambda argv, **kw: runs.append((argv, kw))),                 mock.patch.object(core.os, "name", "nt"):
            core._kill_tree(p)
        self.assertEqual(runs[0][0], ["taskkill", "/F", "/T", "/PID", "42"])
        self.assertEqual(runs[0][1].get("creationflags", 0), getattr(subprocess, "CREATE_NO_WINDOW", 0))
        p.kill.assert_called_once()

    def test_timeout_does_not_hang_if_pipes_stay_open(self):
        seen = []

        class FakeProc:
            pid, returncode = 7, None

            def communicate(self, timeout=None):
                seen.append(timeout)
                raise subprocess.TimeoutExpired("az", timeout)

        with mock.patch.object(core.subprocess, "Popen", lambda *a, **kw: FakeProc()),                 mock.patch.object(core, "_kill_tree", lambda p: None):
            with self.assertRaises(core.ApiError):
                core._run_az(["account", "show"])
        self.assertEqual(len(seen), 2)
        self.assertIsNotNone(seen[1])


class ServerBindTest(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "Windows SO_REUSEADDR semantics")
    def test_second_bind_same_port_fails(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location("cp_server", os.path.join(ROOT, "server.py"))
        server = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(server)
        cfg = mock.Mock(host="127.0.0.1", port=_free_port())
        a = server.make_server(cfg)
        try:
            with self.assertRaises(OSError):
                server.make_server(cfg).server_close()
        finally:
            a.server_close()


class McpStdioEncodingTest(unittest.TestCase):
    def test_stdio_streams_reconfigured_to_utf8(self):
        from casepilot import mcp
        out = io.TextIOWrapper(io.BytesIO(), encoding="cp1252")
        inp = io.TextIOWrapper(io.BytesIO(), encoding="cp1252")
        mcp._utf8_stdio(inp, out)
        self.assertEqual(out.encoding.lower().replace("-", ""), "utf8")
        self.assertEqual(inp.encoding.lower().replace("-", ""), "utf8")


class CssFontTest(unittest.TestCase):
    def test_mono_has_windows_fallbacks(self):
        with open(os.path.join(ROOT, "static", "style.css"), encoding="utf-8") as f:
            css = f.read()
        rule = next(l for l in css.splitlines() if l.startswith(".mono "))
        self.assertIn('"Cascadia Mono", Consolas', rule)


if __name__ == "__main__":
    unittest.main()
