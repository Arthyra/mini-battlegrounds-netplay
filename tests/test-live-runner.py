import importlib.util
import os
from pathlib import Path
from subprocess import CompletedProcess
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "run_all", Path(__file__).with_name("run-all.py"))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class LiveRunnerTests(unittest.TestCase):
    def test_missing_server_fails_explicit_live_run(self):
        with tempfile.TemporaryDirectory() as temp, patch("builtins.print"):
            self.assertFalse(runner.run_live(Path(temp) / "tests"))

    def test_both_server_launch_paths_use_a_fresh_profile(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            exe = root / "dist" / "server" / "server.exe"
            exe.parent.mkdir(parents=True)
            exe.touch()
            previous_profile = root / "existing-profile.json"
            previous_profile.write_text('{"existing": true}', encoding="utf-8")
            plan = [("room-config.py", 120, 4), ("crash-restart.py", None, None)]
            profiles = []

            def profile_from(env):
                profile = Path(env["MBG_PROFILE_PATH"])
                self.assertNotEqual(profile, previous_profile)
                self.assertEqual(profile.read_text(encoding="utf-8"), "{}")
                profiles.append(profile)

            def launch_server(*args, env, **kwargs):
                profile_from(env)

            def launch_script(*args, env):
                profile_from(env)
                return CompletedProcess(args, 0)

            with (patch.dict(os.environ, MBG_PROFILE_PATH=str(previous_profile)),
                  patch.object(runner, "LIVE_PLAN", plan),
                  patch.object(runner.socket, "socket") as socket,
                  patch.object(runner.subprocess, "Popen", side_effect=launch_server),
                  patch.object(runner.subprocess, "run", side_effect=launch_script),
                  patch("builtins.print")):
                socket.return_value.connect_ex.side_effect = [1, 0]
                self.assertTrue(runner.run_live(root / "tests"))

            self.assertEqual(len(profiles), 3)
            self.assertEqual(len(set(profiles)), 1)
            self.assertFalse(profiles[0].exists())
            self.assertEqual(previous_profile.read_text(encoding="utf-8"), '{"existing": true}')


if __name__ == "__main__":
    unittest.main()
