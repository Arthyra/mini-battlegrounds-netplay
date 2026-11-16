"""Latency jitter regression: a full round over frame-delaying TCP proxies.

Both TCP planes (world 43123 and battle 43134) are relayed through proxies
that hold every frame for a random 0.05-2.0s in each direction. With the
client's 2s pings the worst server-side gap is ~4s, well inside the 15s
battle/lobby silence windows, so the round must complete with no disconnect
and the lobby link must survive the round end.

The UDP plane is direct: registration and the 82->52 relay check run
un-jittered (UDP has no silence detector by design).

Requires an externally started server with a short round, for example:
  MBG_TEST_AUTOSTART=1 MBG_ROUND_TIME=20 server.exe --players 2 ...
and ports 43120-43135 free.

Usage:
  python tests/test-jitter-proxy.py
"""
import argparse
import asyncio
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
DELAY_LO, DELAY_HI = 0.05, 2.0

sys.path.insert(0, str(HERE))
saved_argv = sys.argv
sys.argv = ["test-public-battle", "--host", "127.0.0.1"]
spec = importlib.util.spec_from_file_location(
    "test_public_battle", HERE / "test-public-battle.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
sys.argv = saved_argv


def free_port():
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    return port


async def pipe(reader, writer):
    try:
        while True:
            head = await reader.readexactly(4)
            size, message = struct.unpack("<HH", head)
            payload = await reader.readexactly(size - 4)
            await asyncio.sleep(random.uniform(DELAY_LO, DELAY_HI))
            writer.write(head + payload)
            await writer.drain()
    except (asyncio.IncompleteReadError, ConnectionError, OSError):
        pass
    finally:
        try:
            writer.close()
        except OSError:
            pass


async def run_proxy(listen_port, upstream_port):
    async def handle(client_reader, client_writer):
        try:
            upstream_reader, upstream_writer = await asyncio.open_connection(
                "127.0.0.1", upstream_port)
        except OSError:
            return
        await asyncio.gather(pipe(client_reader, upstream_writer),
                             pipe(upstream_reader, client_writer))

    server = await asyncio.start_server(handle, "127.0.0.1", listen_port)
    async with server:
        await server.serve_forever()


def start_jitter_proxies():
    """Two proxies (world, battle planes) on one background event loop."""
    world_port, battle_port = free_port(), free_port()
    loop = asyncio.new_event_loop()
    threading.Thread(target=loop.run_forever, daemon=True).start()
    asyncio.run_coroutine_threadsafe(run_proxy(world_port, base.WORLD), loop)
    asyncio.run_coroutine_threadsafe(run_proxy(battle_port, base.BATTLE), loop)
    return world_port, battle_port


def login_via_proxy(name, world_port):
    """base.login, but the world link is opened against the jitter proxy."""
    sock = socket.create_connection(("127.0.0.1", base.LOGIN), timeout=5)
    encoded = name.encode("utf-16le")
    sock.sendall(base.frame(20, b"\0\0\0\0" + struct.pack("<H", len(encoded)) + encoded))
    msg, data = base.recv_msg(sock)
    assert msg == 20, (msg, data)
    error, player_id, ticket = struct.unpack_from("<HII", data)
    assert error == 0, error
    sock.close()
    world = socket.create_connection(("127.0.0.1", world_port), timeout=10)
    world.settimeout(15)  # every frame may sit up to DELAY_HI in the proxy
    world.sendall(base.frame(23, struct.pack("<II", player_id, ticket)))
    # ping while the handshake drains: the jitter holds each frame up to
    # DELAY_HI, so the ~10s handshake consumes most of the 15s lobby
    # silence timer and the pings must cover it from the start
    base.keepalive(world)
    while True:
        msg, _ = base.recv_msg(world)
        if msg == 119:
            break
    return world, player_id, ticket


def battle_via_proxy(player_id, ticket_assignment_data, battle_port):
    """The 32 assignment points at the real battle port; dial the proxy."""
    host, port, offset = base.parse_endpoint(ticket_assignment_data)
    bid = struct.unpack_from("<I", ticket_assignment_data, offset)[0]
    battle = socket.create_connection(("127.0.0.1", battle_port), timeout=10)
    battle.settimeout(15)
    battle.sendall(base.frame(34, struct.pack("<IHI", bid, base.WORLD_ID, player_id)))
    msg, data = base.recv_msg(battle)
    assert msg == 34, (msg, data)
    error, udp_port, assigned, world_id, map_id = struct.unpack_from("<HIIHI", data)
    assert error == 0 and assigned == player_id and world_id == base.WORLD_ID and map_id == 102
    return battle, bid


if __name__ == "__main__":
    failures = []
    world_port, battle_port = start_jitter_proxies()
    print(f"jitter proxies up: world {world_port}, battle {battle_port} "
          f"(delay {DELAY_LO}-{DELAY_HI}s per frame)")

    a, a_id, _ = login_via_proxy("slot-1" + base.VERSION, world_port)
    b, b_id, _ = login_via_proxy("pc-test" + base.VERSION, world_port)
    base.keepalive(a)
    base.keepalive(b)
    base.ready(a)
    base.ready(b)
    time.sleep(1.0)
    base.start(a)
    a.settimeout(25)
    msg, data = base.recv_msg(a)  # 32 assignment (room-state echoes skipped)
    while msg != 32:
        msg, data = base.recv_msg(a)
    a_battle, bid = battle_via_proxy(a_id, data, battle_port)
    b_battle = None
    # player b: its own 32 is on b's world link
    b.settimeout(25)
    msg, data_b = base.recv_msg(b)
    while msg != 32:
        msg, data_b = base.recv_msg(b)
    b_battle, _ = battle_via_proxy(b_id, data_b, battle_port)
    base.keepalive(a_battle)
    base.keepalive(b_battle)
    base.recv_until(a_battle, {37}, timeout=15)
    base.recv_until(b_battle, {37}, timeout=15)
    a_battle.sendall(base.frame(38, struct.pack("<f", 1.0)))
    b_battle.sendall(base.frame(38, struct.pack("<f", 1.0)))
    base.recv_until(a_battle, {39, 40}, timeout=15)
    base.recv_until(b_battle, {39, 40}, timeout=15)
    print(f"round {bid} established through jitter")

    ua = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ub = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ua.bind(("0.0.0.0", 0))
    ub.bind(("0.0.0.0", 0))
    for sock, player_id in ((ua, a_id), (ub, b_id)):
        sock.sendto(struct.pack("<HHIHII", 18, 0, bid, base.WORLD_ID,
                                player_id, player_id), ("127.0.0.1", base.UDP))
    time.sleep(0.3)
    ua.sendto(struct.pack("<HHIHII", 38, 82, bid, base.WORLD_ID, a_id, a_id)
              + struct.pack("<5f", 1.0, 2.0, 3.0, 4.0, 5.0), ("127.0.0.1", base.UDP))
    try:
        ub.settimeout(3)
        data, _ = ub.recvfrom(128)
        size, message = struct.unpack_from("<HH", data)
        _, sender = struct.unpack_from("<HI", data, 4)
        assert message == 52 and sender == a_id, (size, message, sender)
        print("udp relay ok (52 from player", sender, ")")
    except (socket.timeout, AssertionError) as relay_error:
        print("udp relay failed:", relay_error)
        failures.append("udp relay")
    ua.close()
    ub.close()

    # hold through the round: frames must keep arriving on the jittered
    # battle link (server silence window is 15s; worst jitter gap ~4s)
    a_battle.settimeout(10)
    seen = set()
    deadline = time.time() + 60
    while time.time() < deadline:
        try:
            msg, _ = base.recv_msg(a_battle)
            seen.add(msg)
        except socket.timeout:
            continue
        except ConnectionError:
            break  # finish_round closed the battle conn: the round ended
    if not seen:
        failures.append("no frames through jitter")
    print("frames seen through jitter:", sorted(seen))

    # the lobby link must have survived the whole round: a ready toggle is
    # still echoed with a fresh room state
    try:
        a.sendall(base.frame(27, struct.pack("<H", 4)))
        a.settimeout(10)
        msg, _ = base.recv_msg(a)  # ping echoes interleave; skip them
        while msg == 1:
            msg, _ = base.recv_msg(a)
        # the toggle echoes the directed room state (23) or the
        # broadcast one (210); both prove the lobby link is alive
        assert msg in (23, 210), msg
        print("lobby link alive after the round (room state echoed)")
    except (socket.timeout, AssertionError, ConnectionError) as lobby_error:
        print("lobby link dead after the round:", lobby_error)
        failures.append("lobby link died")

    if failures:
        print("FAILURES:", failures)
        raise SystemExit(1)
    print("JITTER PASS")
