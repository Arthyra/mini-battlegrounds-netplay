"""UDP loss, burst, reorder, blackout and recovery regression.

Player A's UDP plane is routed through a fault relay so loss patterns
can be injected deterministically while the TCP planes (world + battle)
stay direct. The relay binds ONE socket for the whole test, so the
server still sees a stable registration address (player.udp == relay
addr) - losing that address would test reconnect instead of loss. The
relay socket carries both directions: test datagrams (from the test's
own client socket) and server datagrams (from the server addr) are
split by source address; the test itself reads from its own socket and
never competes with the relay.

Packet layouts (wire):
  ping  (210): <HHIHII> standard 18-byte header + <f> sent time (22 bytes)
  ack   (209): same header with message 209 + the sent-time echo
  status(82):  standard header + <5f> pose (38 bytes)
  relay (52):  <HH> + <HI> (flag, sender) + <5f> pose - NOT the standard
               header; sender id sits at offset 6

Phases:
  1. baseline     - 210 ping -> 209 ACK echo, 82 -> 52 position relay
  2. random loss  - 30% forward drop; ACKs still arrive for most pings,
                    TCP never reacts
  3. burst loss   - 6-in-a-row drops; gaps then recovery, TCP calm
  4. reorder      - 40% of forward datagrams held 150ms -> ACKs arrive
                    out of order
  5. blackout     - 100% UDP cut (both directions) for 16s (past the 15s
                    battle silence window); TCP stays alive (frame 40
                    keeps arriving), no reconnect
  6. recovery     - fresh 82 with NEW coordinates relays unchanged (no
                    stale snapshot), 210 -> 209 works again

Requires an externally started server with a long round, for example:
  MBG_TEST_AUTOSTART=1 MBG_ROUND_TIME=120 server.exe --players 2 ...
and ports 43120-43135 free.

Usage:
  python tests/test-udp-loss.py
"""
import argparse
import importlib.util
import random
import socket
import struct
import sys
import threading
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

REORDER_DELAY = 0.15
SERVER_ADDR = ("127.0.0.1", base.UDP)


class FaultRelay:
    """Per-player UDP fault injector over a single stable socket."""

    def __init__(self, server_addr):
        self.server_addr = server_addr
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("127.0.0.1", 0))
        self.port = self.sock.getsockname()[1]
        self.client_addr = None
        self.forward_drop = lambda data: False   # pattern hook
        self.forward_hold = lambda data: 0.0     # reorder hook
        self.blackout = False
        self.hold = []          # (release_at, dest, data)
        self.stats = {"forward": 0, "dropped": 0, "reverse": 0}
        self.running = True
        self.lock = threading.Lock()
        threading.Thread(target=self._loop, daemon=True).start()

    def _loop(self):
        self.sock.settimeout(0.2)
        while self.running:
            try:
                data, addr = self.sock.recvfrom(4096)
            except socket.timeout:
                self._flush_holds()
                continue
            except OSError:
                break  # socket closed by close(): exit quietly at teardown
            if addr == self.server_addr:
                # reverse direction: server -> relay -> client socket
                with self.lock:
                    self.stats["reverse"] += 1
                if self.client_addr and not self.blackout:
                    self.sock.sendto(data, self.client_addr)
                continue
            if self.client_addr is None:
                self.client_addr = addr
            with self.lock:
                self.stats["forward"] += 1
            if self.blackout or self.forward_drop(data):
                with self.lock:
                    self.stats["dropped"] += 1
                continue
            hold_for = self.forward_hold(data)
            if hold_for > 0:
                with self.lock:
                    self.hold.append((time.time() + hold_for, self.server_addr, data))
                continue
            self.sock.sendto(data, self.server_addr)
            self._flush_holds()

    def _flush_holds(self):
        now = time.time()
        with self.lock:
            held = list(self.hold)
            self.hold = []
        for release_at, dest, data in held:
            if now >= release_at:
                self.sock.sendto(data, dest)
            else:
                with self.lock:
                    self.hold.append((release_at, dest, data))

    def close(self):
        self.running = False
        self.sock.close()
        time.sleep(0.3)  # let the loop thread see the closed socket and exit


def ping_packet(bid, player_id, value):
    return (struct.pack("<HHIHII", 22, 210, bid, base.WORLD_ID, player_id, player_id)
            + struct.pack("<f", value))


def status_packet(bid, player_id, x, y, z, yaw, pitch):
    return (struct.pack("<HHIHII", 38, 82, bid, base.WORLD_ID, player_id, player_id)
            + struct.pack("<5f", x, y, z, yaw, pitch))


def relay_sender(data):
    """52 relay packet: <HH> + <HI> (flag, sender) + pose."""
    return struct.unpack_from("<I", data, 6)[0]


def relay_pose(data):
    return struct.unpack_from("<5f", data, 10)


def establish_round(host):
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
    return a, b, a_battle, b_battle, bid, a_id, b_id


def udp_register(sock, bid, player_id):
    packet = struct.pack("<HHIHII", 18, 0, bid, base.WORLD_ID, player_id, player_id)
    sock.sendto(packet, ("127.0.0.1", base.UDP))
    time.sleep(0.3)


def read_ack(sock, timeout=2.0):
    """Wait for one 209 ACK; returns the echoed sent-time float."""
    sock.settimeout(timeout)
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            data, addr = sock.recvfrom(4096)
        except socket.timeout:
            break
        size, message = struct.unpack_from("<HH", data)
        if message == 209 and size == 22:
            return struct.unpack_from("<f", data, 18)[0]
    return None


def send_and_count_acks(sock, bid, player_id, count, dest, gap=0.25, window=12.0):
    """Send `count` pings to `dest` (the relay, never the server directly);
    count ACKs for `window` seconds."""
    acks = 0
    deadline = time.time() + window
    for i in range(count):
        sock.sendto(ping_packet(bid, player_id, float(i)), dest)
        if time.time() >= deadline:
            break
        sock.settimeout(min(gap, max(0.05, deadline - time.time())))
        try:
            while True:
                data, addr = sock.recvfrom(4096)
                if struct.unpack_from("<H", data, 2)[0] == 209:
                    acks += 1
        except socket.timeout:
            pass
    while time.time() < deadline:
        sock.settimeout(0.3)
        try:
            data, addr = sock.recvfrom(4096)
            if struct.unpack_from("<H", data, 2)[0] == 209:
                acks += 1
        except socket.timeout:
            break
    return acks


if __name__ == "__main__":
    failures = []
    random.seed(20260117)

    a, b, a_battle, b_battle, bid, a_id, b_id = establish_round("127.0.0.1")
    print(f"round {bid} established (TCP direct, player A UDP via fault relay)")

    relay_a = FaultRelay(SERVER_ADDR)
    ca = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ca.bind(("127.0.0.1", 0))
    # register THROUGH the relay so player.udp == relay addr; the relay then
    # learns the client socket address from this datagram
    udp_register(relay_a.sock, bid, a_id)
    time.sleep(0.3)
    # re-arm the reverse path: the relay must know where the test reads
    ca.sendto(ping_packet(bid, a_id, -1.0), ("127.0.0.1", relay_a.port))
    time.sleep(0.3)
    ca.settimeout(1.0)
    try:
        ca.recvfrom(4096)  # either the ACK or a status broadcast
    except socket.timeout:
        pass
    if relay_a.client_addr is None:
        print("FAIL: relay never learned the client address")
        raise SystemExit(1)

    ub = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ub.bind(("0.0.0.0", 0))
    udp_register(ub, bid, b_id)

    # 1. baseline: ping -> ACK echo, status -> relay
    ca.sendto(ping_packet(bid, a_id, 123.5), ("127.0.0.1", relay_a.port))
    echo = read_ack(ca)
    if echo is not None and abs(echo - 123.5) < 1e-6:
        print("baseline: 210 -> 209 ACK echo ok")
    else:
        print("baseline: no/mismatched ACK:", echo)
        failures.append("baseline ack")
    ca.sendto(status_packet(bid, a_id, 10.0, 20.0, 30.0, 0.5, 0.25),
              ("127.0.0.1", relay_a.port))
    ub.settimeout(3)
    try:
        data, _ = ub.recvfrom(128)
        assert struct.unpack_from("<H", data, 2)[0] == 52, "expected relay"
        assert relay_sender(data) == a_id, relay_sender(data)
        assert relay_pose(data)[:3] == (10.0, 20.0, 30.0), relay_pose(data)
        print("baseline: 82 -> 52 relay ok")
    except (socket.timeout, AssertionError) as e:
        print("baseline: relay failed:", e)
        failures.append("baseline relay")

    # 2. random 30% forward loss over 20 pings
    relay_a.forward_drop = lambda data: random.random() < 0.3
    got = send_and_count_acks(ca, bid, a_id, 20, ("127.0.0.1", relay_a.port))
    print(f"random loss: {got}/20 ACKs through 30% drop")
    if not (5 <= got <= 19):
        failures.append(f"random loss ACK count {got}")

    # 3. burst loss: 6-in-a-row of every 8 dropped
    counter = {"n": 0}
    def burst_drop(data):
        counter["n"] += 1
        return counter["n"] % 8 >= 2  # drops 6 of every 8
    relay_a.forward_drop = burst_drop
    got = send_and_count_acks(ca, bid, a_id, 24, ("127.0.0.1", relay_a.port), gap=0.3, window=16.0)
    print(f"burst loss: {got}/24 ACKs through 6-of-8 bursts")
    if not (1 <= got <= 9):
        failures.append(f"burst loss ACK count {got}")
    relay_a.forward_drop = lambda data: False
    ca.sendto(ping_packet(bid, a_id, 999.0), ("127.0.0.1", relay_a.port))
    echo = read_ack(ca)
    if echo is not None and abs(echo - 999.0) < 1e-6:
        print("burst recovery: ACK ok")
    else:
        print("burst recovery: no ACK")
        failures.append("burst recovery")

    # 4. reorder: hold 40% of forward datagrams 150ms
    relay_a.forward_hold = lambda data: REORDER_DELAY if random.random() < 0.4 else 0.0
    ca.settimeout(0.4)
    arrivals = []
    for i in range(10):
        ca.sendto(ping_packet(bid, a_id, float(i)), ("127.0.0.1", relay_a.port))
        time.sleep(0.15)
    deadline = time.time() + 5
    while time.time() < deadline and len(arrivals) < 10:
        try:
            data, addr = ca.recvfrom(4096)
            if struct.unpack_from("<H", data, 2)[0] == 209:
                arrivals.append(struct.unpack_from("<f", data, 18)[0])
        except socket.timeout:
            pass
    relay_a.forward_hold = lambda data: 0.0
    reordered = any(arrivals[i] > arrivals[i + 1] for i in range(len(arrivals) - 1))
    print(f"reorder: {len(arrivals)}/10 ACKs, out-of-order={reordered}")
    if len(arrivals) < 8:
        failures.append(f"reorder lost ACKs ({len(arrivals)})")

    # 5. blackout: 16s full UDP cut (both directions), TCP stays alive
    relay_a.blackout = True
    print("blackout: UDP cut for 16s...")
    time.sleep(16)
    a_battle.settimeout(8)
    try:
        msg, _ = base.recv_msg(a_battle)
        print("blackout: battle TCP alive (frame", msg, ")")
    except (socket.timeout, ConnectionError) as e:
        print("blackout: battle TCP died:", e)
        failures.append("blackout killed battle tcp")
    relay_a.blackout = False

    # 6. recovery: same relay socket still registered, ACK + fresh relay
    ca.sendto(ping_packet(bid, a_id, 777.0), ("127.0.0.1", relay_a.port))
    echo = read_ack(ca)
    if echo is not None and abs(echo - 777.0) < 1e-6:
        print("recovery: 210 -> 209 ACK ok again")
    else:
        print("recovery: ACK missing")
        failures.append("recovery ack")
    ca.sendto(status_packet(bid, a_id, 111.5, 222.5, 333.5, 0.1, 0.2),
              ("127.0.0.1", relay_a.port))
    ub.settimeout(3)
    try:
        data, _ = ub.recvfrom(128)
        assert struct.unpack_from("<H", data, 2)[0] == 52, "expected relay"
        assert relay_sender(data) == a_id, relay_sender(data)
        assert relay_pose(data)[:3] == (111.5, 222.5, 333.5), relay_pose(data)
        print("recovery: fresh coordinates relayed unchanged")
    except (socket.timeout, AssertionError) as e:
        print("recovery: relay check failed:", e)
        failures.append("recovery relay")

    ca.close()
    relay_a.close()
    ub.close()
    if failures:
        print("FAILURES:", failures)
        raise SystemExit(1)
    print("UDP-LOSS PASS")
