"""Run every Python unit test in this directory with one command.

The test files use hyphenated names on purpose (they mirror release
names), which `python -m unittest discover` cannot import (0 tests,
silently). Load each file by path instead and report a single verdict:

    python tests/run-all.py            # unit tests only
    python tests/run-all.py --remote   # also run test-public-battle.py
                                       # against the live ECS (needs the
                                       # room to be idle)

C# helpers under tests/ and tools/ are compiled tools with
their own arguments; see the header of each file.
"""
import importlib.util
import os
import subprocess
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent

UNIT_FILES = [
    "test-connection-lifecycle.py",
    "test-room-and-explosion.py",
    "test-gameplay-timing.py",
]

REMOTE_FILE = "test-public-battle.py"


def load_suite(name):
    path = HERE / name
    spec = importlib.util.spec_from_file_location(path.stem.replace("-", "_"), path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return unittest.defaultTestLoader.loadTestsFromModule(module)


def main():
    run_remote = "--remote" in sys.argv[1:]
    argv = [arg for arg in sys.argv[1:] if not arg.startswith("-")]
    sys.argv = [sys.argv[0]] + argv
    suite = unittest.TestSuite()
    for name in UNIT_FILES:
        suite.addTests(load_suite(name))
    runner = unittest.TextTestRunner(verbosity=1)
    result = runner.run(suite)
    ok = result.wasSuccessful()
    if run_remote:
        # test-public-battle.py is a script against a live server, not a
        # unittest case; run it as a child process and propagate the result.
        # The target must be given explicitly (no baked-in server address).
        host = os.environ.get("MBG_TEST_HOST")
        if not host:
            print("remote: FAIL (set MBG_TEST_HOST to the server address)")
            sys.exit(1)
        remote = subprocess.run([sys.executable, str(HERE / REMOTE_FILE), "--host", host])
        print("remote:", "PASS" if remote.returncode == 0 else "FAIL")
        ok = ok and remote.returncode == 0
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
