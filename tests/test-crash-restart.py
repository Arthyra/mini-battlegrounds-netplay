"""Server crash and restart regression (self-contained).

Spawns dist/server/server.exe itself, runs one full round, hard-kills the
process mid-battle (the crash scenario), restarts it, and verifies both
players can log in again and complete a fresh round on the new process:

  1. healthy round: login -> ready -> start -> battle handshake -> UDP
  2. crash: TerminateProcess on the server
  3. restart: fresh process, fresh state
  4. rejoin: re-login both players, re-ready, new round completes

The server ports (43120/43123/43134/43135) must be free: stop any other
test server first. Skips cleanly when server.exe has not been built.

Usage:
  python tests/test-crash-restart.py
"""
import argparse
import importlib.util
import os
import socket
import struct
import subprocess
import sys
import threading
import time
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
args = parser.parse_args()

HERE = Path(__file__).resolve().parent
SERVER_EXE = HERE.parent / "dist" / "server" / "server.exe"

sys.path.insert(0, str(HERE))
saved_argv = sys.argv
sys.argv = ["test-public-battle", "--host", "127.0.0.1"]
spec = importlib.util.spec_from_file_location(
    "test_public_battle", HERE / "test-public-battle.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
sys.argv = saved_argv

SERVER_ENV = dict(os.environ,
                  MBG_TEST_AUTOSTART="1",
                  MBG_ROUND_TIME="20",
                  MBG_NO_EMPTY_SHUTDOWN="1")
SERVER_ARGS = ["--players", "2", "--host", "127.0.0.1", "--bind", "127.0.0.1",
               "--admin-port", "0"]
BUSY_PORTS = (base.LOGIN, base.WORLD, base.BATTLE, base.UDP)


def port_busy(port):
    probe = socket.socket()
    probe.settimeout(1.0)
    busy = probe.connect_ex(("127.0.0.1", port)) == 0
    probe.close()
    return busy


def kill_tree(proc):
    # server.exe is a PyInstaller onefile build: proc.pid is the
    # bootloader; the real server is its child and keeps the ports
    # alive unless the whole tree dies (proc.kill() is not enough).
    subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def spawn_server():
    proc = subprocess.Popen([str(SERVER_EXE), *SERVER_ARGS], env=SERVER_ENV,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace")
    ready = threading.Event()
    lines = []

    def pump():
        for line in proc.stdout:
            lines.append(line)
            if "Listeners ready" in line:
                ready.set()

    threading.Thread(target=pump, daemon=True).start()
    if not ready.wait(20):
        kill_tree(proc)
        raise SystemExit("FAIL: server never reported 'Listeners ready'; output:"
                         + "".join(lines[-10:]))
    return proc


def establish_round():
    """login both players, start a round, complete the battle handshake."""
    a, a_id = base.login("slot-1" + base.VERSION)
    b, b_id = base.login("pc-test" + base.VERSION)
    base.keepalive(a)
    base.keepalive(b)
    base.ready(a)
    base.ready(b)
    time.sleep(0.3)
    base.start(a)
    a_battle, bid, _, _, _ = base.connect_battle(a, a_id)
    b_battle, _, _, _, _ = base.connect_battle(b, b_id)
    base.keepalive(a_battle)
    base.keepalive(b_battle)
    base.recv_until(a_battle, {37}, timeout=8)
    base.recv_until(b_battle, {37}, timeout=8)
    a_battle.sendall(base.frame(38, struct.pack("<f", 1.0)))
    b_battle.sendall(base.frame(38, struct.pack("<f", 1.0)))
    base.recv_until(a_battle, {39, 40}, timeout=8)
    base.recv_until(b_battle, {39, 40}, timeout=8)
    return a, b, a_battle, b_battle, bid


if __name__ == "__main__":
    if not SERVER_EXE.is_file():
        print("SKIP: dist/server/server.exe is not built; run the PyInstaller build first")
        raise SystemExit(0)
    busy = [p for p in BUSY_PORTS if port_busy(p)]
    if busy:
        print(f"FAIL: ports {busy} are busy; stop other test servers first")
        raise SystemExit(1)

    procs = []
    try:
        proc = spawn_server()
        procs.append(proc)
        a, b, a_battle, b_battle, bid = establish_round()
        print(f"round {bid} established on the first process")
        a_battle.close()
        b_battle.close()

        # the crash: no graceful shutdown, no goodbye frames
        kill_tree(proc)
        proc.wait(10)
        print("server killed mid-session")
        time.sleep(1.0)

        proc = spawn_server()
        procs.append(proc)
        a2, b2, a2_battle, b2_battle, bid2 = establish_round()
        print(f"round {bid2} established on the restarted process")
        a2_battle.close()
        b2_battle.close()
        print("CRASH-RESTART PASS")
    finally:
        for proc in procs:
            if proc.poll() is None:
                kill_tree(proc)
