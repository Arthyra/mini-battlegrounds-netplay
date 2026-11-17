"""Run every Python unit test in this directory with one command.

The test files use hyphenated names on purpose (they mirror release
names), which `python -m unittest discover` cannot import (0 tests,
silently). Load each file by path instead and report a single verdict:

    python tests/run-all.py            # unit tests only (CI-safe)
    python tests/run-all.py --remote   # also run test-public-battle.py
                                       # against the live ECS (needs the
                                       # room to be idle; set MBG_TEST_HOST)
    python tests/run-all.py --live     # also run the integration fault
                                       # scripts against a LOCAL server this
                                       # command starts itself (needs
                                       # dist/server/server.exe; binds
                                       # 127.0.0.1 only, never touches ECS)

--live sequencing (ports 43120-43135 must be free):
  multiround + jitter need a 20s-round server; udp-loss needs a long
  round; crash-restart manages its own server. The runner restarts the
  server between them so each script always gets the round length its
  docstring promises.

C# helpers under tests/ and tools/ are compiled tools with
their own arguments; see the header of each file.
"""
import importlib.util
import os
import socket
import subprocess
import sys
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent

UNIT_FILES = [
    "test-connection-lifecycle.py",
    "test-room-and-explosion.py",
    "test-gameplay-timing.py",
]

REMOTE_FILE = "test-public-battle.py"

LIVE_PLAN = [
    # (script, server round seconds; None = script manages its own server)
    ("test-multiround.py", 20),
    ("test-jitter-proxy.py", 20),
    ("test-udp-loss.py", 120),
    ("test-crash-restart.py", None),
]


def load_suite(name):
    path = HERE / name
    spec = importlib.util.spec_from_file_location(path.stem.replace("-", "_"), path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return unittest.defaultTestLoader.loadTestsFromModule(module)


def start_local_server(exe, round_time):
    env = dict(os.environ,
               MBG_TEST_AUTOSTART="1",
               MBG_ROUND_TIME=str(round_time),
               MBG_NO_EMPTY_SHUTDOWN="1")
    proc = subprocess.Popen([str(exe), "--players", "2", "--host", "127.0.0.1",
                             "--bind", "127.0.0.1", "--admin-port", "0"],
                            env=env, stdout=subprocess.DEVNULL,
                            stderr=subprocess.DEVNULL)
    deadline = time.time() + 20
    while time.time() < deadline:
        probe = socket.socket()
        ready = probe.connect_ex(("127.0.0.1", 43120)) == 0
        probe.close()
        if ready:
            return proc
        time.sleep(0.5)
    subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    raise SystemExit("live: local server never became ready")


def stop_local_server(proc):
    if proc is None or proc.poll() is not None:
        return
    subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    proc.wait(10)
    time.sleep(1.0)


def run_live(here):
    exe = here.parent / "dist" / "server" / "server.exe"
    if not exe.is_file():
        print("live: SKIP (dist/server/server.exe is not built)")
        return True
    ok = True
    server = None
    current_round = None
    try:
        for name, round_time in LIVE_PLAN:
            if round_time is not None:
                if server is None or current_round != round_time:
                    stop_local_server(server)
                    server = start_local_server(exe, round_time)
                    current_round = round_time
            else:
                stop_local_server(server)
                server = None
                current_round = None
            print(f"live: {name}")
            # every live script targets 127.0.0.1 by design; none of them
            # takes --host
            result = subprocess.run([sys.executable, str(here / name)])
            print(f"live: {name}", "PASS" if result.returncode == 0 else "FAIL")
            ok = ok and result.returncode == 0
    finally:
        stop_local_server(server)
    return ok


def main():
    run_remote = "--remote" in sys.argv[1:]
    run_live_flag = "--live" in sys.argv[1:]
    argv = [arg for arg in sys.argv[1:] if not arg.startswith("-")]
    sys.argv = [sys.argv[0]] + argv
    suite = unittest.TestSuite()
    for name in UNIT_FILES:
        suite.addTests(load_suite(name))
    runner = unittest.TextTestRunner(verbosity=1)
    result = runner.run(suite)
    ok = result.wasSuccessful()
    if run_live_flag:
        ok = run_live(HERE) and ok
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
