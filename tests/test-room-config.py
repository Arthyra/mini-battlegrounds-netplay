"""ROOM_CONFIG (212/215) regression: owner-only cap and round length.

v1.0.0 custom rooms, server-authoritative:
  - the owner may retarget the player cap (2..slots, never below the
    players currently in the lobby) and the round length (30..600s)
  - a running round locks the config: 212 is rejected while queued
  - every accepted config re-broadcasts the 210 room state AND a fresh
    215 ROOM_CONFIG_STATE (cap, round); rejected configs send neither,
    which the test uses as the rejection signal
  - the login flow carries 215, so a client learns the live values
  - the next room start uses the configured cap and round length

Requires an externally started server with MBG_TEST_AUTOSTART=1,
MBG_ROUND_TIME=120 and --players 4 (so a cap change to 3 is legal),
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


def read_config_state(sock, timeout=2.5):
    """Wait for one 215 ROOM_CONFIG_STATE; returns (cap, round) or None."""
    sock.settimeout(timeout)
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            msg, data = base.recv_msg(sock)
        except socket.timeout:
            return None
        if msg == 215:
            cap, rnd = struct.unpack_from("<HH", data)
            return cap, rnd
    return None


def read_room_cap(sock, timeout=2.5):
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


def drain(sock, seconds=0.6):
    sock.settimeout(0.2)
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            base.recv_msg(sock)
        except socket.timeout:
            continue
        except ConnectionError:
            break


def establish_round(a, b, a_id, b_id):
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
    return a_battle, b_battle, bid


if __name__ == "__main__":
    failures = []
    a, a_id = base.login("slot-1" + base.VERSION)   # first login -> owner
    b, b_id = base.login("pc-test" + base.VERSION)
    base.keepalive(a)
    base.keepalive(b)

    # 1. login-time 215 carries the live cap (4 slots, default 120s round)
    #    read it BEFORE any draining, it is part of the login sequence
    state = read_config_state(a)
    if state == (4, 120):
        print("login 215: cap 4, round 120s as started")
    else:
        print("login 215 wrong:", state)
        failures.append("login 215")
    drain(a, 1.0)
    drain(b, 1.0)

    # 2. owner narrows the cap 4 -> 3; both 210 and 215 must reflect it
    a.sendall(config_packet(3, 30))
    cap = read_room_cap(a)
    state = read_config_state(a)
    if cap == 3 and state == (3, 30):
        print("owner config accepted: cap 3, round 30s (210 + 215)")
    else:
        print("owner config failed:", cap, state)
        failures.append("owner config")

    # 3. a NON-OWNER with a fully VALID config must be rejected; drain the
    #    peer queues first - step 2's broadcast reached both clients and
    #    stale frames would fake an acceptance
    drain(a, 1.0)
    drain(b, 1.0)
    b.sendall(config_packet(4, 60))
    if read_config_state(b) is None and read_room_cap(a) is None:
        print("non-owner valid config rejected (no broadcast)")
    else:
        print("non-owner valid config ACCEPTED (bug)")
        failures.append("non-owner accepted")

    # 4. range enforcement on both axes
    a.sendall(config_packet(9, 60))    # above the 4 slots
    if read_config_state(a) is None:
        print("over-slot cap rejected")
    else:
        print("over-slot cap ACCEPTED (bug)")
        failures.append("over-slot accepted")
    a.sendall(config_packet(3, 10))    # below the 30s floor
    if read_config_state(a) is None:
        print("sub-30s round rejected")
    else:
        print("sub-30s round ACCEPTED (bug)")
        failures.append("floor not enforced")

    # 5. shrink-then-restore round trip: 3 -> 2 -> 3 must both succeed
    a.sendall(config_packet(2, 60))
    state = read_config_state(a)
    a.sendall(config_packet(3, 60))
    state2 = read_config_state(a)
    if state == (2, 60) and state2 == (3, 60):
        print("cap round trip 3 -> 2 -> 3 ok")
    else:
        print("cap round trip failed:", state, state2)
        failures.append("cap round trip")

    # 6. a running round locks the config: start with the 30s setting and
    #    try to change it mid-match - no 215 may arrive while queued
    a.sendall(config_packet(3, 30))
    read_config_state(a)
    drain(a, 1.0)
    drain(b, 1.0)
    a_battle, b_battle, bid = establish_round(a, b, a_id, b_id)
    print(f"round {bid} started (configured 30s)")
    a.sendall(config_packet(3, 60))    # mid-match change attempt
    if read_config_state(a) is None:
        print("mid-match config rejected (no 215)")
    else:
        print("mid-match config APPLIED (bug)")
        failures.append("mid-match accepted")

    # 7. the round must close at 30s + 8s result delay (~38s), proving the
    #    pre-round config was what the timer used
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
    if closed_at is None or closed_at < 20 or closed_at > 50:
        print(f"round closed at {closed_at} - timer did not use the config")
        failures.append("configured round length not applied")
    else:
        print(f"round closed after {closed_at:.1f}s - configured 30s applied")

    # 8. after the round the owner must be able to restore a longer round
    time.sleep(1.0)
    drain(a, 1.0)
    drain(b, 1.0)
    a.sendall(config_packet(3, 300))
    state = read_config_state(a)
    if state == (3, 300):
        print("post-round restore to 300s accepted")
    else:
        print("post-round restore failed:", state)
        failures.append("post-round restore")

    if failures:
        print("FAILURES:", failures)
        raise SystemExit(1)
    print("ROOM-CONFIG PASS")
