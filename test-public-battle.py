import socket
import struct
import time

HOST = "198.51.100.10"
LOGIN = 43120
WORLD = 43123
BATTLE = 43134
UDP = 43135
WORLD_ID = 1
VERSION = "|lan-v4"


def frame(message, payload=b""):
    return struct.pack("<HH", 4 + len(payload), message) + payload


def drain(sock):
    sock.settimeout(0.1)
    try:
        while True:
            sock.recvfrom(2048)
    except socket.timeout:
        pass


def recv_msg(sock):
    head = sock.recv(4)
    while len(head) < 4:
        chunk = sock.recv(4 - len(head))
        if not chunk:
            raise ConnectionError("closed")
        head += chunk
    size, message = struct.unpack("<HH", head)
    data = b""
    while len(data) < size - 4:
        chunk = sock.recv(size - 4 - len(data))
        if not chunk:
            raise ConnectionError("closed")
        data += chunk
    return message, data


def parse_endpoint(data, offset=0):
    length = struct.unpack_from("<H", data, offset)[0]
    offset += 2
    host = data[offset:offset + length].decode("ascii")
    offset += length
    port = struct.unpack_from("<I", data, offset)[0]
    return host, port, offset + 4


def login(name):
    sock = socket.create_connection((HOST, LOGIN), timeout=5)
    encoded = name.encode("utf-16le")
    sock.sendall(frame(20, b"\0\0\0\0" + struct.pack("<H", len(encoded)) + encoded))
    msg, data = recv_msg(sock)
    assert msg == 20, (msg, data)
    error, player_id, ticket = struct.unpack_from("<HII", data)
    assert error == 0, error
    host, port, _ = parse_endpoint(data, 10)
    sock.close()
    world = socket.create_connection((host, port), timeout=5)
    world.sendall(frame(23, struct.pack("<II", player_id, ticket)))
    while True:
        msg, data = recv_msg(world)
        if msg == 119:
            break
    return world, player_id


def ready(world):
    world.sendall(frame(27, struct.pack("<H", 4)))


def start(world):
    world.sendall(frame(211))


def connect_battle(world, player_id):
    while True:
        msg, data = recv_msg(world)
        if msg == 32:
            host, port, offset = parse_endpoint(data)
            battle_id = struct.unpack_from("<I", data, offset)[0]
            break
    battle = socket.create_connection((host, port), timeout=5)
    battle.sendall(frame(34, struct.pack("<IHI", battle_id, WORLD_ID, player_id)))
    msg, data = recv_msg(battle)
    assert msg == 34, (msg, data)
    error, udp_port, assigned, world_id, map_id = struct.unpack_from("<HIIHI", data)
    assert error == 0 and assigned == player_id and world_id == WORLD_ID and map_id == 102
    return battle, battle_id, udp_port, host, port


def recv_until(sock, wanted, timeout=10):
    deadline = time.time() + timeout
    while time.time() < deadline:
        msg, data = recv_msg(sock)
        if msg in wanted:
            return msg, data
        if msg == 37:
            continue
    raise TimeoutError(wanted)


def keepalive(sock, interval=2.0):
    # Mirror the Unity client's 2s battle ping so the server's silence
    # detector never fires during a healthy test.
    import threading

    def loop():
        try:
            while True:
                time.sleep(interval)
                sock.sendall(frame(1, struct.pack("<f", time.time())))
        except Exception:
            pass

    thread = threading.Thread(target=loop, daemon=True)
    thread.start()
    return thread


def read_room_state(sock):
    while True:
        msg, data = recv_msg(sock)
        if msg == 210:
            break
    count = data[1]
    offset = 8  # BBB??? then the inner <H member count
    ready = {}
    for _ in range(count):
        pid, room_ready, is_owner, nlen = struct.unpack_from("<IBBH", data, offset)
        offset += 8 + 34  # name is fixed-width utf-16le padded to 34
        ready[pid] = bool(room_ready)
    return ready


def ensure_both_ready(a, a_id, b, b_id, attempts=8):
    # Read one room-wide snapshot from a's socket; toggle whichever side is
    # missing. Never start until both are confirmed ready, otherwise the
    # round starts with a subset and the room refuses further toggles.
    for _ in range(attempts):
        a.sendall(frame(27, struct.pack("<H", 4)))
        ready = read_room_state(a)
        a_ready = ready.get(a_id, False)
        b_ready = ready.get(b_id, False)
        if a_ready and b_ready:
            return True
        # undo the a toggle when b is not ready yet, then toggle b
        if a_ready:
            a.sendall(frame(27, struct.pack("<H", 4)))
            read_room_state(a)
        b.sendall(frame(27, struct.pack("<H", 4)))
        read_room_state(b)
        time.sleep(0.2)
    return False


def main():
    a = b = None
    login_error = None
    for attempt in range(30):
        try:
            a, a_id = login("slot-1" + VERSION)
            b, b_id = login("pc-test" + VERSION)
            break
        except (ConnectionError, AssertionError, OSError) as login_error:
            if a:
                try: a.close()
                except Exception: pass
            if b:
                try: b.close()
                except Exception: pass
            print("login retry", attempt, login_error)
            time.sleep(4)
    else:
        raise SystemExit("login kept failing: " + str(login_error))
    print("login", a_id, b_id)
    # A late finish_round from the previous round resets room_ready for
    # everyone; retry the ready+start cycle until the assignment arrives.
    a_battle = b_battle = None
    battle_id = udp_port = battle_host = battle_port = None
    for attempt in range(4):
        assert ensure_both_ready(a, a_id, b, b_id), "both players never became ready"
        time.sleep(0.3)
        start(a)
        try:
            a_battle, battle_id, udp_port, battle_host, battle_port = connect_battle(a, a_id)
            b_battle, _, _, _, _ = connect_battle(b, b_id)
            keepalive(a_battle)
            keepalive(b_battle)
            break
        except (TimeoutError, socket.timeout, AssertionError, ConnectionError) as start_error:
            print("start retry", attempt, start_error)
            time.sleep(6)
    if a_battle is None:
        raise TimeoutError("room never started")
    print("battle", battle_id, "udp", udp_port)
    recv_until(a_battle, {37}, timeout=5)
    recv_until(b_battle, {37}, timeout=5)
    a_battle.sendall(frame(38, struct.pack("<f", 1.0)))
    b_battle.sendall(frame(38, struct.pack("<f", 1.0)))
    recv_until(a_battle, {39, 40}, timeout=5)
    recv_until(b_battle, {39, 40}, timeout=5)
    vehicles = []
    try:
        while True:
            msg, data = recv_msg(a_battle)
            if msg == 72:
                count = struct.unpack_from("<H", data, 0)[0]
                offset = 2
                for _ in range(count):
                    vehicle_id, kind = struct.unpack_from("<II", data, offset)
                    x, y, z = struct.unpack_from("<3f", data, offset + 8)
                    vehicles.append((vehicle_id, x, z))
                    offset += 8 + 32
                break
    except (AssertionError, socket.timeout):
        pass
    assert vehicles, "no vehicle spawn broadcast received"

    ua = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ub = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ua.bind(("0.0.0.0", 0))
    ub.bind(("0.0.0.0", 0))
    for sock, player_id in ((ua, a_id), (ub, b_id)):
        sock.sendto(struct.pack("<HHIHII", 18, 0, battle_id, WORLD_ID, player_id, player_id), (HOST, UDP))
    time.sleep(0.2)
    time.sleep(0.5)
    status = struct.pack("<5f", 10.0, 15.0, 20.0, 30.0, 0.0)
    ua.sendto(struct.pack("<HHIHII", 38, 82, battle_id, WORLD_ID, a_id, a_id) + status, (HOST, UDP))
    ub.settimeout(5)
    data, _ = ub.recvfrom(128)
    size, message = struct.unpack_from("<HH", data)
    _, sender = struct.unpack_from("<HI", data, 4)
    assert message == 52 and sender == a_id and size == 30, (size, message, sender)
    print("udp 82 -> 52 ok")

    a_battle.close()
    time.sleep(1)
    a2 = socket.create_connection((battle_host, battle_port), timeout=5)
    a2.sendall(frame(34, struct.pack("<IHI", battle_id, WORLD_ID, a_id)))
    msg, data = recv_msg(a2)
    assert msg == 34, (msg, data)
    recv_until(a2, {37}, timeout=5)
    keepalive(a2)
    print("reconnect 34/37 ok")

    # --- regression: a reconnected player re-registers UDP and relays again.
    a2.sendall(frame(38, struct.pack("<f", 1.0)))
    time.sleep(0.3)
    ua.sendto(struct.pack("<HHIHII", 18, 0, battle_id, WORLD_ID, a_id, a_id), (HOST, UDP))
    time.sleep(0.3)
    drain(ub)
    status = struct.pack("<5f", 11.0, 15.0, 21.0, 0.0, 0.0)
    ua.sendto(struct.pack("<HHIHII", 38, 82, battle_id, WORLD_ID, a_id, a_id) + status, (HOST, UDP))
    ub.settimeout(5)
    data, _ = ub.recvfrom(128)
    size, message = struct.unpack_from("<HH", data)
    _, sender = struct.unpack_from("<HI", data, 4)
    fields = struct.unpack_from("<5f", data, 10) if size >= 30 else ()
    assert message == 52 and sender == a_id and fields and fields[0] == 11.0,         ("post-reconnect relay got", size, message, sender, fields)
    print("reconnect udp re-register ok")

    # --- regression: mounted player status relay uses the vehicle position
    # (the server used to crash on vehicle.y and freeze mounted players).
    status = struct.pack("<5f", 11.0, 15.0, 21.0, 0.0, 0.0)
    ua.sendto(struct.pack("<HHIHII", 38, 82, battle_id, WORLD_ID, a_id, a_id) + status, (HOST, UDP))
    time.sleep(0.3)
    mounted = False
    for vehicle_id, vx, vz in vehicles:
        # move the player next to the vehicle first (mount needs <= 9m)
        drain(ub)
        status = struct.pack("<5f", float(vx), 15.0, float(vz), 0.0, 0.0)
        ua.sendto(struct.pack("<HHIHII", 38, 82, battle_id, WORLD_ID, a_id, a_id) + status, (HOST, UDP))
        time.sleep(0.3)
        for seat in range(2):
            a2.sendall(frame(103, struct.pack("<IH", vehicle_id, seat)))
            time.sleep(0.2)
            data = b""
            try:
                while True:
                    msg, data = recv_msg(a2)
                    if msg == 103:
                        mounted = True
                        break
            except (AssertionError, ConnectionError, socket.timeout):
                continue
            if mounted:
                break
        if mounted:
            break
    assert mounted, "mount failed (no vehicle within reach?)"
    drain(ub)
    status = struct.pack("<5f", 40.0, 15.0, 40.0, 90.0, 0.0)
    ua.sendto(struct.pack("<HHIHII", 38, 82, battle_id, WORLD_ID, a_id, a_id) + status, (HOST, UDP))
    ub.settimeout(5)
    data, _ = ub.recvfrom(128)
    size, message = struct.unpack_from("<HH", data)
    _, sender = struct.unpack_from("<HI", data, 4)
    x, y, z = struct.unpack_from("<3f", data, 10)
    assert message == 52 and sender == a_id, (message, sender)
    assert y == 15.0, "mounted status y is not the fixed ground height"
    print("mounted status relay ok (vehicle position, y=15)")

    # --- regression: disconnecting while mounted and scoped resets both, so
    # the reconnected player is not position-pinned by stale server state.
    a2.sendall(frame(206, b""))
    time.sleep(0.2)
    a2.close()
    time.sleep(1)
    a3 = socket.create_connection((battle_host, battle_port), timeout=5)
    a3.sendall(frame(34, struct.pack("<IHI", battle_id, WORLD_ID, a_id)))
    msg, data = recv_msg(a3)
    assert msg == 34, (msg, data)
    recv_until(a3, {37}, timeout=5)
    keepalive(a3)
    ua.sendto(struct.pack("<HHIHII", 18, 0, battle_id, WORLD_ID, a_id, a_id), (HOST, UDP))
    time.sleep(0.3)
    drain(ub)
    status = struct.pack("<5f", 12.0, 15.0, 22.0, 0.0, 0.0)
    ua.sendto(struct.pack("<HHIHII", 38, 82, battle_id, WORLD_ID, a_id, a_id) + status, (HOST, UDP))
    ub.settimeout(5)
    data, _ = ub.recvfrom(128)
    _, sender = struct.unpack_from("<HI", data, 4)
    x, y, z = struct.unpack_from("<3f", data, 10)
    assert (x, z) == (12.0, 22.0), ("stale scoped/mounted state pinned the reconnected player", x, z)
    print("scoped/mounted state reset on disconnect ok")

    a3.close()
    b_battle.close()
    a.close()
    b.close()
    ua.close()
    ub.close()
    print("PASS")


if __name__ == "__main__":
    main()









