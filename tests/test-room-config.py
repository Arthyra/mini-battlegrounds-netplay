"""ROOM_CONFIG (212) regression: owner-only cap/round-time customization.

v0.13.0 custom rooms, server-authoritative:
  - the owner may set the player cap (2..slots the server started with)
    and the round length (30..600s) from the lobby
  - every accepted config broadcasts a fresh 210 room state; rejected
    configs broadcast nothing, which the test uses as the rejection signal
  - the next room start uses the configured cap and round length

Requires an externally started server with MBG_TEST_AUTOSTART=1,
MBG_ROUND_TIME=120 and --players 4 (so a cap change to 3 is legal)
and ports 43120-43135 free.

Usage:
  python tests/test-room-config.py
"""
import argparse
import importlib.util
import socket
import struct
import sys
import time
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
args = parser.parse_args()

HERE = Path(__file__).resolve().parent

sys.path.insert(0, str(HERE))
saved_argv = sys.argv
sys.argv = ["test-public-battle", "--host", "127.0.0.1"]
spec = importlib.util.spec_from_file_location(
    "test_public_battle", HERE / "test-public-battle.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
sys.argv = saved_argv


def config_packet(cap, seconds):
    return base.frame(212, struct.pack("<HH", cap, seconds))


def room_state_broadcast(sock, timeout=2.5):
    """Wait for one 210 room state; returns the cap byte, or None."""
    sock.settimeout(timeout)
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            msg, data = base.recv_msg(sock)
        except socket.timeout:
            return None
        if msg == 210:
            return struct.unpack_from("<B", data, 2)[0]
    return None


def drain(sock, seconds=0.5):
    sock.settimeout(0.2)
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            sock.recvfrom(4096)
        except socket.timeout:
            continue


if __name__ == "__main__":
    failures = []
    a, a_id = base.login("slot-1" + base.VERSION)   # first login -> owner
    b, b_id = base.login("pc-test" + base.VERSION)
    base.keepalive(a)
    base.keepalive(b)
    drain(a, 1.0)  # the login-time room state
    drain(b, 1.0)

    # 1. owner narrows the cap 4 -> 3 and shortens the round; the fresh
    #    210 must carry the new cap
    a.sendall(config_packet(3, 30))
    cap = room_state_broadcast(a)
    if cap == 3:
        print("owner config accepted: cap", cap)
    else:
        print("owner config failed:", cap)
        failures.append("owner config")

    # 2. non-owner config: no 210 may follow (rejected silently)
    b.sendall(config_packet(1, 30))
    cap = room_state_broadcast(a)
    if cap is None:
        print("non-owner config rejected (no broadcast)")
    else:
        print("non-owner config APPLIED (bug): cap", cap)
        failures.append("non-owner accepted")

    # 3. out-of-range caps and round lengths: no 210 may follow
    a.sendall(config_packet(9, 30))   # above the 4 slots the server started with
    cap = room_state_broadcast(a)
    if cap is None:
        print("over-slot cap rejected")
    else:
        print("over-slot cap APPLIED (bug): cap", cap)
        failures.append("over-slot accepted")
    a.sendall(config_packet(2, 5))    # below the 30s floor
    cap = room_state_broadcast(a)
    if cap is None:
        print("sub-30s round rejected")
    else:
        print("sub-30s round APPLIED (bug): cap", cap)
        failures.append("floor not enforced")

    # 4. the configured 30s round must take effect: start, load, and expect
    #    BATTLE_CLOSE ~38s after the round starts (30s + 8s result delay),
    #    far sooner than the server default 120s; the cap stays 3
    a.sendall(config_packet(3, 30))
    cap = room_state_broadcast(a)
    if cap != 3:
        failures.append("final config failed")
    base.ready(a)
    base.ready(b)
    time.sleep(0.5)
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
    print(f"round {bid} started with cap {cap}, configured 30s round")

    start = time.time()
    closed_at = None
    a_battle.settimeout(60)
    try:
        while time.time() - start < 60:
            msg, _ = base.recv_msg(a_battle)
            if msg == 43:
                closed_at = time.time() - start
                break
    except (socket.timeout, ConnectionError):
        pass
    if closed_at is None:
        print("round did not close within 60s - 30s config NOT applied")
        failures.append("config round length not applied")
    elif closed_at < 20:
        print(f"round closed after {closed_at:.1f}s - too early (config mangled)")
        failures.append("round closed too early")
    else:
        print(f"round closed after {closed_at:.1f}s - configured 30s round applied")

    if failures:
        print("FAILURES:", failures)
        raise SystemExit(1)
    print("ROOM-CONFIG PASS")
