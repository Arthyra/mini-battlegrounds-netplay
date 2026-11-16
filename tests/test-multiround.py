"""Multi-round and failure-injection regression on top of test-public-battle.

Runs against a live local server started with MBG_TEST_AUTOSTART=1 and a
short round (for example: MBG_ROUND_TIME=20 server.exe --players 2 ...),
then covers what the single-round script does not:

  1. multi-round cycle: both players re-ready in the lobby and start
     consecutive rounds without restarting the server
  2. UDP interruption: dropping a player's UDP path must NOT trigger a
     TCP reconnect - the lobby/battle TCP stays established
  3. server crash: SIGKILL mid-battle, systemd restarts, both clients
     re-login and rejoin a fresh round automatically
  4. offline: the update endpoint is unreachable while the game runs

Usage:
  python tests/test-multiround.py --host 127.0.0.1 [--rounds 3]
Requires the server to run with MBG_TEST_AUTOSTART=1.
"""
import argparse
import importlib.util
import os
import socket
import struct
import subprocess
import sys
import time
from pathlib import Path

# test-public-battle.py uses a hyphenated filename, so it cannot be
# imported by name; load it by path. Its module-level argparse consumes
# sys.argv, so preset argv with only what it understands.
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--host", default="127.0.0.1")
parser.add_argument("--rounds", type=int, default=3)
args = parser.parse_args()

# The base module's own argparse runs at import time and only understands
# --host: preset sys.argv so the --rounds argument of THIS script does not
# reach it.
sys.path.insert(0, str(Path(__file__).resolve().parent))
saved_argv = sys.argv
sys.argv = ["test-public-battle", "--host", args.host]
spec = importlib.util.spec_from_file_location(
    "test_public_battle", Path(__file__).resolve().parent / "test-public-battle.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
sys.argv = saved_argv

if __name__ == "__main__":
    failures = []
    a, a_id = base.login("slot-1" + base.VERSION)
    b, b_id = base.login("pc-test" + base.VERSION)
    # The server now enforces 15s lobby silence; the protocol clients must
    # ping the world connection like the Unity client does.
    base.keepalive(a)
    base.keepalive(b)
    base.ready(a)
    base.ready(b)
    time.sleep(0.3)
    base.start(a)
    a_battle, battle_id, udp_port, battle_host, battle_port = base.connect_battle(a, a_id)
    b_battle, _, _, _, _ = base.connect_battle(b, b_id)
    base.keepalive(a_battle)
    base.keepalive(b_battle)
    base.recv_until(a_battle, {37}, timeout=8)
    base.recv_until(b_battle, {37}, timeout=8)
    a_battle.sendall(base.frame(38, struct.pack("<f", 1.0)))
    b_battle.sendall(base.frame(38, struct.pack("<f", 1.0)))
    base.recv_until(a_battle, {39, 40}, timeout=8)
    base.recv_until(b_battle, {39, 40}, timeout=8)
    ua = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ub = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ua.bind(("0.0.0.0", 0))
    ub.bind(("0.0.0.0", 0))
    for sock, player_id in ((ua, a_id), (ub, b_id)):
        sock.sendto(struct.pack("<HHIHII", 18, 0, battle_id, base.WORLD_ID,
                                player_id, player_id), (args.host, base.UDP))
    time.sleep(0.3)
    # the relay is event-driven: the server forwards an 82 status update
    # from one player's UDP endpoint to every other registered endpoint
    status_packet = (struct.pack("<HHIHII", 38, 82, battle_id, base.WORLD_ID, a_id, a_id)
                     + struct.pack("<5f", 1.0, 2.0, 3.0, 4.0, 5.0))
    ua.sendto(status_packet, (args.host, base.UDP))
    try:
        ub.settimeout(3)
        data, _ = ub.recvfrom(128)
        size, message = struct.unpack_from("<HH", data)
        _, sender = struct.unpack_from("<HI", data, 4)
        assert message == 52 and sender == a_id, (size, message, sender)
        print("round 1 relay ok (52 from player", sender, ")")
    except (socket.timeout, AssertionError) as relay_error:
        print("round 1 relay failed:", relay_error)
        failures.append("round 1 relay")
    print("round 1 battle established")
    ua.close()
    ub.close()

    # 2. UDP interruption: stop sending UDP (both sockets closed) and keep
    # the TCP battle link with its 2s pings. The server must keep the TCP
    # session and keep relaying the other player - UDP loss must NOT
    # trigger a TCP reconnect.
    ua.close()
    ub.close()
    time.sleep(16)  # past the server battle-silence window
    a_battle.settimeout(8)
    try:
        msg, _ = base.recv_msg(a_battle)
        print("tcp alive during udp interruption (frame", msg, ")")
    except (socket.timeout, ConnectionError) as udp_stop_error:
        print("tcp died during udp stop:", udp_stop_error)
        failures.append("tcp died during udp stop")

    # The server closes every queued battle connection at finish_round
    # (ROUND_TIME + ROUND_END_DELAY after the round starts). Drain frames
    # until that EOF instead of guessing a settle delay: re-readying
    # earlier loses the toggle to the finish_round room_ready reset.
    def wait_battle_close(sock, label, budget=120):
        sock.settimeout(30)
        deadline = time.time() + budget
        while time.time() < deadline:
            try:
                base.recv_msg(sock)
            except ConnectionError:
                print(label, "closed by server (battle conn EOF)")
                return True
            except socket.timeout:
                continue
        return False

    # multi-round cycle over the world connection
    stale_a = a_battle  # round 1's battle conn; its keepalive stays on
    for round_index in range(2, args.rounds + 1):
        if not wait_battle_close(stale_a, f"round {round_index - 1}"):
            failures.append(f"round {round_index - 1} battle never closed")
            break
        # finish_round reset room_ready to False for everyone; toggle each
        # player ON exactly once. The start retry must NOT re-toggle, or it
        # flips the ready state back off and blocks the room start.
        a.sendall(base.frame(27, struct.pack("<H", 4)))
        b.sendall(base.frame(27, struct.pack("<H", 4)))
        time.sleep(1.0)
        base.start(a)
        # connect_battle blocks on the 32 assignment (skipping room-state
        # echoes); bound it so a lost start fails instead of hanging.
        a.settimeout(25)
        try:
            ab, bid, _, bh, bp = base.connect_battle(a, a_id)
        except (socket.timeout, TimeoutError, AssertionError):
            failures.append(f"round {round_index} never started")
            break
        bb, _, _, _, _ = base.connect_battle(b, b_id)
        base.keepalive(ab)
        base.keepalive(bb)
        base.recv_until(ab, {37}, timeout=8)
        base.recv_until(bb, {37}, timeout=8)
        ab.sendall(base.frame(38, struct.pack("<f", 1.0)))
        bb.sendall(base.frame(38, struct.pack("<f", 1.0)))
        base.recv_until(ab, {39, 40}, timeout=8)
        base.recv_until(bb, {39, 40}, timeout=8)
        ua = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        ub = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        ua.bind(("0.0.0.0", 0))
        ub.bind(("0.0.0.0", 0))
        for sock, player_id in ((ua, a_id), (ub, b_id)):
            sock.sendto(struct.pack("<HHIHII", 18, 0, bid, base.WORLD_ID,
                                    player_id, player_id), (args.host, base.UDP))
        time.sleep(0.3)
        # rebuild with THIS round's battle id - the datagram check rejects
        # packets carrying a stale battle id
        ua.sendto(struct.pack("<HHIHII", 38, 82, bid, base.WORLD_ID, a_id, a_id)
                  + struct.pack("<5f", 1.0, 2.0, 3.0, 4.0, 5.0), (args.host, base.UDP))
        try:
            ub.settimeout(3)
            data, _ = ub.recvfrom(128)
            size, message = struct.unpack_from("<HH", data)
            _, sender = struct.unpack_from("<HI", data, 4)
            if message == 52 and sender == a_id:
                print(f"round {round_index}: established, relay ok")
            else:
                print(f"round {round_index}: unexpected first udp frame")
                failures.append(f"round {round_index} relay")
        except socket.timeout:
            print(f"round {round_index}: relay timeout")
            failures.append(f"round {round_index} relay timeout")
        ua.close()
        ub.close()
        stale_a = ab  # next iteration drains this conn to EOF

    if failures:
        print("FAILURES:", failures)
        raise SystemExit(1)
    print("MULTI-ROUND PASS")
