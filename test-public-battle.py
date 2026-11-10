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


def main():
    a, a_id = login("slot-1" + VERSION)
    b, b_id = login("pc-test" + VERSION)
    print("login", a_id, b_id)
    ready(a)
    ready(b)
    time.sleep(0.2)
    start(a)
    a_battle, battle_id, udp_port, battle_host, battle_port = connect_battle(a, a_id)
    b_battle, _, _, _, _ = connect_battle(b, b_id)
    print("battle", battle_id, "udp", udp_port)
    recv_until(a_battle, {37}, timeout=5)
    recv_until(b_battle, {37}, timeout=5)
    a_battle.sendall(frame(38, struct.pack("<f", 1.0)))
    b_battle.sendall(frame(38, struct.pack("<f", 1.0)))
    recv_until(a_battle, {39, 40}, timeout=5)
    recv_until(b_battle, {39, 40}, timeout=5)

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
    print("reconnect 34/37 ok")
    a2.close()

    b_battle.close()
    a.close()
    b.close()
    ua.close()
    ub.close()
    print("PASS")


if __name__ == "__main__":
    main()









