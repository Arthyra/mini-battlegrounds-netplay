"""Local prototype for the Mini Battlegrounds Steam client (build 2655148)."""
import argparse
import asyncio
from dataclasses import dataclass, field
import ipaddress
import json
import math
from pathlib import Path
from random import choice, uniform
import struct
import time

HOST = "127.0.0.1"
LOGIN_PORT = 43120
WORLD_PORT = 43123
BATTLE_PORT = 43134
BATTLE_UDP_PORT = 43135
BATTLE_ID = 1
WORLD_ID = 1
MAP_ID = 102
GUN_AMMO = 51
FIRE_BULLETS = 57
BULLET_HIT_UNIT = 59
BULLET_EXPLODE = 61
VEHICLES_SPAWN = 72
VEHICLES_STATUS = 74
SWITCH_WEAPON = 89
OPERATE = 90
STOP_OPERATE = 91
RELOAD = 92
STOP_RELOAD = 93
RELOADED = 94
OPEN_DOOR = 101
CLOSE_DOOR = 102
MOUNT = 103
UNMOUNT = 104
DRIVE_FORWARD = 105
STOP_TURN_RIGHT = 112
SKINS_GET = 132
SKIN_APPLIES_GET = 134
SKIN_APPLY = 135
NO_SKIN = 0xFFFFFFFF
SKIN_ATTRIBUTES = bytes(4 * struct.calcsize("<Hf"))
REPORT_HIT = 201
AUTO_REVIVE = 202
TICK = 0.05
MAX_HEALTH = 800.0
DOWN_TIME = 3.0
SPAWN_PROTECTION = 1.0

# Direct-hit damage per projectile, in original GunData order.
DAMAGE = (220, 100, 65, 70, 100, 100, 95, 100, 125, 130, 700)

# DoorwayMarker IDs and x/z coordinates from CS_Scene_102.
DOORS = {
    1: (13.36, 44.96),
    2: (17.40, 29.65),
    3: (-31.54, 29.86),
    4: (-8.29, 29.93),
    5: (-50.19, 33.27),
    6: (-4.23, 45.02),
}


@dataclass(frozen=True)
class Gun:
    kind: int
    ammo_kind: int
    magazine: int
    interval: float
    reload_time: float
    pellets: int = 1
    spread: float = 0.0


# Values from the original Windows GunData table.
GUNS = (
    Gun(1, 1, 10, .70, 5, spread=3),      # Desert Eagle
    Gun(2, 1, 15, .20, 5, spread=1),      # P92
    Gun(3, 1, 25, .075, 6.5, spread=3),   # UZI
    Gun(4, 1, 25, .08, 6.5, spread=3.1),  # UMP9
    Gun(5, 2, 5, 1.70, 8, 6, 13),         # S1869
    Gun(6, 2, 2, .30, 8, 6, 13),          # S686
    Gun(7, 3, 30, .13, 7, spread=3),      # M16A4
    Gun(8, 3, 30, .14, 7, spread=4.5),    # M416
    Gun(9, 4, 30, .19, 7, spread=2),      # AKM
    Gun(10, 4, 30, .21, 7, spread=6),     # SCAR-H
    Gun(11, 5, 1, 1.0, 7),               # RPG
)


@dataclass
class Vehicle:
    id: int
    kind: int
    x: float
    z: float
    yaw: float = 90.0
    speed: float = 0.0
    driver: int | None = None
    controls: set[int] = field(default_factory=set)

    def status(self):
        half_angle = math.radians(self.yaw) / 2
        return struct.pack(
            "<8f", self.x, 15.0, self.z, math.cos(half_angle),
            0.0, math.sin(half_angle), 0.0, self.speed,
        )

    def step(self, obstacles, other_vehicles):
        throttle = (105 in self.controls) - (107 in self.controls)
        steer = (111 in self.controls) - (109 in self.controls)
        target = throttle * (10 if throttle >= 0 else 5)
        self.speed += max(-16 * TICK, min(16 * TICK, target - self.speed))
        if not self.speed:
            return False
        self.yaw = (self.yaw + steer * 95 * TICK * math.copysign(1, self.speed)) % 360
        angle = math.radians(self.yaw)
        x = max(-69.0, min(79.0, self.x + math.sin(angle) * self.speed * TICK))
        z = max(-11.4, min(56.6, self.z + math.cos(angle) * self.speed * TICK))
        if any(left - .7 < x < right + .7 and bottom - .7 < z < top + .7
               for left, bottom, right, top in obstacles) or any(
            other is not self and math.hypot(x - other.x, z - other.z) < 2.3
            for other in other_vehicles
        ):
            self.speed = 0.0
        else:
            self.x, self.z = x, z
        return True


def frame(message, payload=b""):
    return struct.pack("<HH", 4 + len(payload), message) + payload


def skin_stats(kind):
    return struct.pack("<I", kind) + SKIN_ATTRIBUTES


def endpoint(host, port):
    address = host.encode("ascii")
    return struct.pack("<H", len(address)) + address + struct.pack("<I", port)


@dataclass
class Player:
    id: int
    gun: Gun
    world: asyncio.StreamWriter | None = None
    battle: asyncio.StreamWriter | None = None
    udp: tuple | None = None
    ready: bool = False
    status: tuple[float, ...] = field(init=False)
    ammo: int = field(init=False)
    reserve: int = field(init=False)
    firing: bool = False
    next_shot: float = 0.0
    reload_at: float = 0.0
    vehicle: Vehicle | None = None
    health: float = MAX_HEALTH
    revive_at: float = 0.0
    protected_until: float = 0.0
    skins: dict[tuple[int, int, int], int] = field(default_factory=dict)

    def __post_init__(self):
        self.status = (2.5 * self.id, 15.2, 3, 0, 0)
        self.ammo = self.gun.magazine
        self.reserve = self.gun.magazine * 4

    def name(self):
        return f"LAN Test {self.id}".encode("utf-16le").ljust(34, b"\0")

    def profile(self, now):
        money_and_stats = struct.pack("<6q7I", now, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0)
        purchase_history = bytes(struct.calcsize("<qHqH"))
        platform_rewards = bytes(struct.calcsize("<?3q?qI"))
        leaderboard = bytes(struct.calcsize("<q6Iq6I8?17H"))
        sign_in = struct.pack("<HqH", 0, now, 1)
        return self.name() + money_and_stats + purchase_history + platform_rewards + leaderboard + sign_in

    @property
    def gun_id(self):
        return self.id * 10 + 1

    def loadout(self):
        storage = struct.pack("<HHIHII", 1, 0, self.gun_id + 3, 7, self.gun.ammo_kind, self.reserve)
        skin = self.skins.get((1, 1, self.gun.kind))
        weapon = struct.pack("<HHIHII?", 1, 0, self.gun_id, 2, self.gun.kind, 1, skin is not None)
        if skin is not None:
            weapon += skin_stats(skin)
        return storage + weapon + struct.pack("<IHH", self.ammo, 0, 0)

    def outfit(self):
        # Only clothing is added to the unit. Armor remains disabled in this mode.
        return [
            kind for (character, category, item), kind in self.skins.items()
            if character == 1 and category == 2 and item >= 101
        ]


async def receive(reader):
    size, message = struct.unpack("<HH", await reader.readexactly(4))
    if size < 4:
        raise ValueError(f"Invalid frame size {size}")
    return message, await reader.readexactly(size - 4)


async def send(writer, message, payload=b""):
    writer.write(frame(message, payload))
    await writer.drain()
    if message != 1:
        print(f"-> {message} ({len(payload)} payload bytes)", flush=True)


class Match(asyncio.DatagramProtocol):
    def __init__(self, host, size):
        self.host = host
        self.size = size
        self.players = {}
        self.queued = []
        self.joined = []
        self.started = False
        self.forwarded = set()
        self.vehicles = {
            vehicle.id: vehicle for vehicle in (
                Vehicle(101, 1, 1.5, -1.0),
                Vehicle(102, 2, 28.0, -3.0),
                Vehicle(103, 3, -20.0, -3.0),
            )
        }
        self.bullet_id = 0
        self.rockets = []
        self.live_bullets = {}
        root = Path(__file__).resolve().parent
        self.obstacles = json.loads((root / "map102-vehicle-obstacles.json").read_text(encoding="utf-8"))
        self.skin_catalog = {
            kind: (category, item)
            for kind, category, item in json.loads((root / "skin-catalog.json").read_text(encoding="utf-8"))
        }
        self.skin_inventory = struct.pack("<I", len(self.skin_catalog)) + b"".join(
            struct.pack("<I", kind) + skin_stats(kind) for kind in self.skin_catalog
        )

    async def login(self, reader, writer):
        message, payload = await receive(reader)
        if message != 20 or len(payload) < 4:
            raise ValueError("Unexpected first login message")
        if len(self.players) >= self.size:
            raise ValueError("All test slots are assigned. Restart the server for a new match.")
        player = Player(len(self.players) + 1, choice(GUNS))
        self.players[player.id] = player
        print(f"login <- 20; assigned test player {player.id}", flush=True)
        await send(writer, 20, struct.pack("<HII", 0, player.id, player.id) + endpoint(self.host, WORLD_PORT))
        await reader.read()

    async def world(self, reader, writer):
        message, payload = await receive(reader)
        if message != 23 or len(payload) != 8:
            raise ValueError("Unexpected first world message")
        player_id, ticket = struct.unpack("<II", payload)
        player = self.players.get(player_id)
        if player is None or player_id != ticket or player.world is not None:
            raise ValueError("Unknown or duplicate test player")
        player.world = writer
        now = int(time.time())
        print(f"world <- 23; player {player.id}", flush=True)
        await send(writer, 23, struct.pack("<HHqiqq", 0, WORLD_ID, now, 0, now + 86400, now + 86400 * 30))
        await send(writer, 117, struct.pack("<H", 0) + player.profile(now))
        await send(writer, SKINS_GET, self.skin_inventory)
        await send(writer, SKIN_APPLIES_GET, struct.pack("<I", 0))
        await send(writer, 119)
        while True:
            message, payload = await receive(reader)
            if message == 1:
                await send(writer, message, payload)
            elif message == SKINS_GET and not payload:
                await send(writer, SKINS_GET, self.skin_inventory)
            elif message == SKIN_APPLIES_GET and not payload:
                applies = b"".join(
                    struct.pack("<II", character, skin)
                    for (character, _, _), skin in player.skins.items()
                )
                await send(writer, SKIN_APPLIES_GET, struct.pack("<I", len(player.skins)) + applies)
            elif message == SKIN_APPLY and len(payload) == 14:
                character, category, item, skin = struct.unpack("<IHII", payload)
                key = (character, category, item)
                if character in (1, 2) and (skin == NO_SKIN or self.skin_catalog.get(skin) == (category, item)):
                    if skin == NO_SKIN:
                        player.skins.pop(key, None)
                    else:
                        player.skins[key] = skin
                    await send(writer, SKIN_APPLY, payload)
            elif message == 27 and len(payload) == 2 and player not in self.queued:
                self.queued.append(player)
                print(f"world <- 27; queued player {player.id} ({len(self.queued)}/{self.size})", flush=True)
                if len(self.queued) == self.size:
                    for member in self.queued:
                        await send(member.world, 32, endpoint(self.host, BATTLE_PORT) + struct.pack("<I", BATTLE_ID))

    def battle_snapshot(self):
        header = struct.pack("<fHH", 60.0, 0, self.size)
        units = bytearray()
        for player in self.joined:
            outfit = player.outfit()
            units.extend(struct.pack("<III", player.id, 1, len(outfit)))
            units.extend(b"".join(skin_stats(kind) for kind in outfit))
            units.extend(struct.pack("<H3f5f", player.id, player.health, MAX_HEALTH, 6.5, *player.status))
            units.extend(player.loadout())
        players = b"".join(
            struct.pack("<HI", WORLD_ID, p.id) + p.name() + struct.pack("<IIHI", 1, 0, 0, p.id)
            for p in self.joined
        )
        return header + units + struct.pack("<H", self.size) + players

    async def battle(self, reader, writer):
        message, payload = await receive(reader)
        if message != 34 or len(payload) != 10:
            raise ValueError("Unexpected first battle message")
        battle_id, world_id, player_id = struct.unpack("<IHI", payload)
        player = self.players.get(player_id)
        if (battle_id, world_id) != (BATTLE_ID, WORLD_ID) or player not in self.queued or player.battle:
            raise ValueError("Unknown or duplicate battle player")
        player.battle = writer
        print(f"battle <- 34; player {player.id}", flush=True)
        await send(writer, 34, struct.pack("<HIIHI", 0, BATTLE_UDP_PORT, player.id, 1, MAP_ID))
        self.joined.append(player)
        if len(self.joined) == self.size:
            await asyncio.sleep(0.5)
            for member in self.joined:
                await send(member.battle, 37, self.battle_snapshot())
        while True:
            message, payload = await receive(reader)
            if message == 1:
                await send(writer, message, payload)
            elif message == 38 and payload == struct.pack("<f", 1.0) and not player.ready:
                player.ready = True
                print(f"battle <- 38; player {player.id} loaded", flush=True)
                if all(member.ready for member in self.joined):
                    vehicles = struct.pack("<H", len(self.vehicles)) + b"".join(
                        struct.pack("<II", vehicle.id, vehicle.kind) + vehicle.status()
                        for vehicle in self.vehicles.values()
                    )
                    for member in self.joined:
                        await send(member.battle, 39, struct.pack("<H", 0))
                        await send(member.battle, 40)
                        await send(member.battle, VEHICLES_SPAWN, vehicles)
                        for equipped in self.joined:
                            await send(member.battle, SWITCH_WEAPON, struct.pack("<II", equipped.id, equipped.gun_id))
                    self.started = True
            elif self.started:
                self.battle_input(player, message, payload)

    def broadcast(self, message, payload):
        packet = frame(message, payload)
        for player in self.joined:
            if player.battle and not player.battle.is_closing():
                player.battle.write(packet)

    def battle_input(self, player, message, payload):
        if message == REPORT_HIT and len(payload) == 8:
            self.report_hit(player, *struct.unpack("<II", payload), time.monotonic())
            return
        if player.revive_at:
            return
        if message in (OPERATE, STOP_OPERATE) and len(payload) == 20 and player.vehicle is None:
            status = struct.unpack("<5f", payload)
            if all(map(math.isfinite, status)):
                player.status = status
                player.firing = message == OPERATE
                if player.firing:
                    self.fire(player, time.monotonic())
        elif message == RELOAD and not payload and player.vehicle is None:
            if not player.reload_at and player.ammo < player.gun.magazine and player.reserve:
                player.reload_at = time.monotonic() + player.gun.reload_time
                self.broadcast(RELOAD, struct.pack("<I", player.id))
        elif message in (OPEN_DOOR, CLOSE_DOOR) and len(payload) == 4:
            door, = struct.unpack("<I", payload)
            if door in DOORS and math.dist(DOORS[door], (player.status[0], player.status[2])) <= 5:
                self.broadcast(message, struct.pack("<II", player.id, door))
                print(f"door {door}: {'opened' if message == OPEN_DOOR else 'closed'} by player {player.id}", flush=True)
        elif message == MOUNT and len(payload) == 6 and player.vehicle is None:
            vehicle_id, _seat = struct.unpack("<IH", payload)
            vehicle = self.vehicles.get(vehicle_id)
            if vehicle and vehicle.driver is None and math.dist((vehicle.x, vehicle.z), (player.status[0], player.status[2])) <= 6:
                vehicle.driver = player.id
                player.vehicle = vehicle
                player.firing = False
                if player.reload_at:
                    player.reload_at = 0.0
                    self.broadcast(STOP_RELOAD, struct.pack("<I", player.id))
                self.broadcast(MOUNT, struct.pack("<IIH", player.id, vehicle.id, 0))
                print(f"vehicle {vehicle.id}: player {player.id} mounted", flush=True)
        elif message == UNMOUNT and not payload and player.vehicle:
            self.unmount(player)
        elif DRIVE_FORWARD <= message <= STOP_TURN_RIGHT and not payload and player.vehicle:
            vehicle = player.vehicle
            if (message - DRIVE_FORWARD) % 2:
                vehicle.controls.discard(message - 1)
            else:
                vehicle.controls.add(message)
                print(f"vehicle {vehicle.id}: drive input {message}", flush=True)
            self.broadcast(message, struct.pack("<I", vehicle.id))

    def unmount(self, player):
        vehicle = player.vehicle
        vehicle.driver = None
        vehicle.controls.clear()
        player.vehicle = None
        player.status = (vehicle.x, 15.0, vehicle.z, vehicle.yaw, 0.0)
        self.broadcast(UNMOUNT, struct.pack("<I", player.id))
        print(f"vehicle {vehicle.id}: player {player.id} unmounted at {vehicle.x:.2f}, {vehicle.z:.2f}", flush=True)

    def report_hit(self, shooter, bullet, target_id, now):
        record = self.live_bullets.get(bullet)
        if record is None or record[0] != shooter.id or target_id == shooter.id:
            return
        _, gun, expires = self.live_bullets.pop(bullet)
        target = self.players.get(target_id)
        if (now >= expires or target is None or target.battle is None or target.battle.is_closing()
                or target.revive_at or now < target.protected_until):
            return
        damage = DAMAGE[gun - 1]
        x, y, z = target.status[:3] if target.vehicle is None else (target.vehicle.x, 15.0, target.vehicle.z)
        self.broadcast(BULLET_HIT_UNIT, struct.pack(
            "<5I4fI2f", shooter.id, bullet, gun, 0, target.id, target.health,
            x, y + 1.5, z, 0, damage, 0.0,
        ))
        target.health = max(0.0, target.health - damage)
        print(f"hit: player {shooter.id} -> {target.id}, health {target.health:g}", flush=True)
        if not target.health:
            target.revive_at = now + DOWN_TIME
            target.firing = False
            target.reload_at = 0.0
            if target.vehicle is not None:
                self.unmount(target)
            print(f"down: player {target.id}", flush=True)

    def revive(self, player, now):
        player.health = MAX_HEALTH
        player.revive_at = 0.0
        player.protected_until = now + SPAWN_PROTECTION
        player.gun = choice(tuple(gun for gun in GUNS if gun != player.gun) or GUNS)
        player.ammo = player.gun.magazine
        player.reserve = player.gun.magazine * 4
        self.broadcast(AUTO_REVIVE, struct.pack("<If", player.id, player.health) + player.loadout())
        print(f"revive: player {player.id}, gun {player.gun.kind}", flush=True)

    def fire(self, player, now):
        if player.reload_at or not player.ammo or now < player.next_shot:
            return
        x, y, z, yaw, aim = player.status
        gun = player.gun
        player.ammo -= 1
        player.next_shot = now + gun.interval
        origin = (x, y + 2.0, z)
        bullets = bytearray(struct.pack("<IH", player.id, gun.pellets))
        for _ in range(gun.pellets):
            self.bullet_id += 1
            angle = math.radians(yaw + uniform(-gun.spread, gun.spread))
            dx, dz = math.sin(angle), math.cos(angle)
            bullets.extend(struct.pack("<I6f", self.bullet_id, *origin, dx, 0.0, dz))
            if gun.kind == 11:
                distance = max(5.0, min(40.0, aim))
                impact = struct.pack("<I3f", self.bullet_id, x + dx * distance, y + 2.0, z + dz * distance)
                self.rockets.append((now + distance / 26.0, impact))
            else:
                self.live_bullets[self.bullet_id] = (player.id, gun.kind, now + 3.0)
        self.broadcast(FIRE_BULLETS, bullets)
        self.broadcast(GUN_AMMO, struct.pack("<II", player.gun_id, player.ammo))
        print(f"fire: player {player.id}, gun {gun.kind}, ammo {player.ammo}", flush=True)

    async def update(self):
        while True:
            await asyncio.sleep(TICK)
            if not self.started:
                continue
            now = time.monotonic()
            for player in self.joined:
                if player.battle.is_closing():
                    if player.vehicle:
                        self.unmount(player)
                    continue
                if player.revive_at and now >= player.revive_at:
                    self.revive(player, now)
                if player.reload_at and now >= player.reload_at:
                    amount = min(player.gun.magazine - player.ammo, player.reserve)
                    player.ammo += amount
                    player.reserve -= amount
                    player.reload_at = 0.0
                    self.broadcast(RELOADED, struct.pack("<III", player.id, amount, player.ammo))
                    print(f"reload: player {player.id}, ammo {player.ammo}", flush=True)
                if player.firing:
                    self.fire(player, now)

            while self.live_bullets:
                oldest = next(iter(self.live_bullets))
                if self.live_bullets[oldest][2] > now:
                    break
                self.live_bullets.pop(oldest)

            pending = []
            for when, impact in self.rockets:
                if when <= now:
                    self.broadcast(BULLET_EXPLODE, impact)
                    print("rocket exploded", flush=True)
                else:
                    pending.append((when, impact))
            self.rockets = pending

            updates = []
            for vehicle in self.vehicles.values():
                previous_speed = vehicle.speed
                if vehicle.step(self.obstacles, self.vehicles.values()) or previous_speed:
                    updates.append(struct.pack("<I", vehicle.id) + vehicle.status())
            if updates:
                self.broadcast(VEHICLES_STATUS, struct.pack("<H", len(updates)) + b"".join(updates))

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        if len(data) not in (18, 38):
            return
        size, message, battle_id, world_id, player_id, ticket = struct.unpack_from("<HHIHII", data)
        player = self.players.get(player_id)
        if size != len(data) or (battle_id, world_id) != (BATTLE_ID, WORLD_ID) or player is None or ticket != player_id:
            return
        if message == 0 and size == 18 and player.udp is None:
            player.udp = addr
            print(f"battle UDP <- 0; player {player.id} registered", flush=True)
        elif self.started and message == 82 and size == 38 and player.udp == addr and player.vehicle is None:
            status = struct.unpack_from("<5f", data, 18)
            if not all(map(math.isfinite, status)):
                return
            player.status = status
            packet = frame(52, struct.pack("<HI", 1, player.id) + data[18:])
            for member in self.joined:
                if member is not player and member.udp is not None:
                    self.transport.sendto(packet, member.udp)
                    pair = (player.id, member.id)
                    if pair not in self.forwarded:
                        self.forwarded.add(pair)
                        print(f"battle UDP 82 -> 52; player {player.id} to {member.id}", flush=True)


def handler(fn):
    async def run(reader, writer):
        try:
            await fn(reader, writer)
        except (asyncio.IncompleteReadError, ConnectionError):
            pass
        except ValueError as exc:
            print(f"{fn.__name__}: {exc}", flush=True)
        finally:
            writer.close()
            try:
                await writer.wait_closed()
            except ConnectionError:
                pass
    return run


async def main(host, size):
    match = Match(host, size)
    async with (
        await asyncio.start_server(handler(match.login), host, LOGIN_PORT),
        await asyncio.start_server(handler(match.world), host, WORLD_PORT),
        await asyncio.start_server(handler(match.battle), host, BATTLE_PORT),
    ):
        udp, _ = await asyncio.get_running_loop().create_datagram_endpoint(
            lambda: match, local_addr=(host, BATTLE_UDP_PORT)
        )
        try:
            print(f"Listeners ready on {host} for {size} test player(s)", flush=True)
            await match.update()
        finally:
            udp.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--players", choices=range(1, 5), default=1, type=int)
    parser.add_argument("--host", type=ipaddress.IPv4Address, default=HOST)
    args = parser.parse_args()
    if not args.host.is_private or args.host.is_unspecified or args.host.is_link_local:
        parser.error("--host must be a loopback or private LAN IPv4 address")
    try:
        asyncio.run(main(str(args.host), args.players))
    except KeyboardInterrupt:
        pass
