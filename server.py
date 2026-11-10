"""Local prototype for the Mini Battlegrounds Steam client (build 2655148)."""
import argparse
import asyncio
from contextlib import nullcontext
from dataclasses import dataclass, field
import ipaddress
import json
import math
import os
import re
from pathlib import Path
from random import choice, uniform
from secrets import randbits, token_hex
import struct
import sys
import time
import unicodedata

BASE_DIR = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parent

HOST = "127.0.0.1"
LOGIN_PORT = 43120
DISCOVERY_PORT = 43122
WORLD_PORT = 43123
BATTLE_PORT = 43134
BATTLE_UDP_PORT = 43135
BATTLE_ID = 1
BATTLE_LOAD_TIMEOUT = 90.0
ROUND_TIME = float(os.environ.get("MBG_ROUND_TIME", "300"))
ROUND_END_DELAY = float(os.environ.get("MBG_ROUND_END_DELAY", "8"))
RECONNECT_GRACE = 30.0
WORLD_ID = 1
LAN_VERSION = "lan-v4"
MAP_ID = 102
MAX_PLAYERS = 8
LOOT_SPAWN = 50
GUN_AMMO = 51
UNIT_DIE = 53
MELEE_HIT_UNIT = 56
FIRE_BULLETS = 57
BULLET_HIT_UNIT = 59
BULLET_HIT_VEHICLE = 60
BULLET_EXPLODE = 61
BULLET_EXPLOSION_HIT_OBSTACLE = 62
BULLET_EXPLOSION_HIT_UNIT = 63
BULLET_EXPLOSION_HIT_VEHICLE = 64
THROWABLE_EXPLODE = 66
THROWABLE_HIT_UNIT = 68
THROWABLE_HIT_VEHICLE = 69
INCENDIARY_HIT_UNIT = 70
INCENDIARY_HIT_VEHICLE = 71
VEHICLES_SPAWN = 72
VEHICLES_STATUS = 74
VEHICLE_HIT_OBSTACLE = 75
VEHICLE_HIT_UNIT = 76
VEHICLE_EXPLODE = 78
VEHICLE_EXPLOSION_HIT_UNIT = 80
LOOT = 85
SWITCH_WEAPON = 89
OPERATE = 90
STOP_OPERATE = 91
RELOAD = 92
STOP_RELOAD = 93
RELOADED = 94
THROW = 95
USE = 96
OPEN_DOOR = 101
CLOSE_DOOR = 102
MOUNT = 103
UNMOUNT = 104
DRIVE_FORWARD = 105
STOP_TURN_RIGHT = 112
BATTLE_CHAT = 114
CHARACTER_SELECT = 121
SKINS_GET = 132
SKIN_APPLIES_GET = 134
SKIN_APPLY = 135
NO_SKIN = 0xFFFFFFFF
SKIN_ATTRIBUTES = bytes(4 * struct.calcsize("<Hf"))
REPORT_HIT = 201
AUTO_REVIVE = 202
MELEE_SWING = 203
REFRESH_LOADOUT = 204
REMOVE_LOOT = 205
SCOPE_STATE = 206
SCOPE_POSE = 207
WHEEL_SWITCH = 208
RESPAWN_MODE = 4
ROOM_STATE = 210
BATTLE_END = 41
BATTLE_CLOSE = 43
ROOM_START = 211
LAST_WEAPON = 212
NICKNAME = 213
SCOPED_BULLET = 1 << 31
PRIMARY_SLOT = 0
MELEE_SLOT = 2
THROWABLE_SLOT = 3
MED_SLOT = 4
FIRST_AID_KIND = 1
FIRST_AID_CAST_TIME = 4.0
FIRST_AID_HEAL = 400.0
MACHETE_KIND = 2
MACHETE_COOLDOWN = 1.2
MACHETE_RANGE = 5.0
MACHETE_DAMAGE = 390.0
MACHETE_CONE = math.cos(math.radians(75))
EQUIP_TIME = 0.5
TICK = 0.05
MAX_HEALTH = 800.0
DOWN_TIME = 3.0
SPAWN_PROTECTION = 1.0
# LAN tuning: the client receives impact damage from the original server.
VEHICLE_LETHAL_SPEED = 8.0
VEHICLE_SEATS = 4
# VehicleData durability/radius and Vehicle_1..3 main collider half-extents (x, z).
VEHICLE_SPECS = {
    1: (1500, 5.0, 1.125, 2.50),
    2: (2500, 6.0, 1.486, 3.84),
    3: (2000, 5.5, 1.490, 3.30),
}
VEHICLE_BLAST_MESSAGES = {
    BULLET_EXPLOSION_HIT_UNIT: BULLET_EXPLOSION_HIT_VEHICLE,
    THROWABLE_HIT_UNIT: THROWABLE_HIT_VEHICLE,
    INCENDIARY_HIT_UNIT: INCENDIARY_HIT_VEHICLE,
}

# DoorwayMarker IDs and x/z coordinates from CS_Scene_102.
DOORS = {
    1: (13.36, 44.96),
    2: (17.40, 29.65),
    3: (-31.54, 29.86),
    4: (-8.29, 29.93),
    5: (-50.19, 33.27),
    6: (-4.23, 45.02),
}


# Closed door leaf bounds from CS_Scene_102, grouped by the DoorwayMarker ID.
DOOR_BOUNDS = {
    1: (13.20, 43.61, 13.52, 46.31),
    2: (14.39, 29.48, 20.41, 29.81),
    3: (-33.95, 29.68, -29.13, 30.04),
    4: (-11.32, 29.75, -5.28, 30.11),
    5: (-51.52, 33.12, -48.86, 33.41),
    6: (-4.38, 43.69, -4.09, 46.35),
}


@dataclass(frozen=True)
class Gun:
    kind: int
    name: str
    ammo_kind: int
    magazine: int
    interval: float
    reload_time: float
    damage: float
    pellets: int = 1
    spread: float = 0.0


# Values from the international APK GunData table.
GUNS = (
    Gun(1, "Desert Eagle", 1, 10, 0.7, 3, 220, 1, 3),
    Gun(2, "P92", 1, 15, 0.2, 2, 100, 1, 1),
    Gun(3, "UZI", 1, 25, 0.075, 2, 65, 1, 3),
    Gun(4, "UMP9", 1, 25, 0.08, 3, 70, 1, 3.1),
    Gun(5, "S1897", 2, 5, 1.7, 4.2, 100, 6, 13),
    Gun(6, "S686", 2, 2, 0.3, 3.5, 100, 6, 13),
    Gun(7, "M16A4", 3, 30, 0.13, 3.2, 95, 1, 3),
    Gun(8, "M416", 3, 30, 0.14, 3.2, 100, 1, 4.5),
    Gun(9, "AKM", 4, 30, 0.19, 3.6, 125, 1, 2),
    Gun(10, "SCAR-H", 4, 30, 0.21, 3.6, 130, 1, 6),
    Gun(11, "RPG", 5, 1, 1, 7, 700, 1, 0.1),
    Gun(12, "98K", 4, 5, 2, 5, 600),
    Gun(13, "M249", 3, 100, 0.09, 7, 105, 1, 5),
    Gun(14, "Magnum", 1, 6, 0.85, 3.5, 300, 1, 2),
    Gun(15, "P90", 1, 50, 0.08, 4, 65, 1, 3),
    Gun(16, "AUG", 3, 42, 0.12, 4.5, 85, 1, 5),
    Gun(17, "SG550", 4, 10, 0.3, 4.5, 350),
    Gun(18, "AWM", 6, 5, 2.3, 5.5, 1000),
)
SIDEARMS = tuple(gun for gun in GUNS if gun.kind in (1, 2, 14))
PRIMARY_GUNS = tuple(gun for gun in GUNS if gun not in SIDEARMS)


def gun_key(name):
    return "".join(ch for ch in unicodedata.normalize("NFKC", name).casefold() if not ch.isspace() and ch != "-")


GUN_COMMANDS = {gun_key(gun.name) if gun.kind != 1 else "dg": gun for gun in GUNS}
GUN_NAMES = GUN_COMMANDS | {name: GUNS[kind - 1] for name, kind in {
    "microuzi": 3, "s1869": 5, "kar98k": 12,
}.items()}


@dataclass(frozen=True)
class SniperTuning:
    minimum: float
    maximum: float
    recovery_per_second: float
    drift_per_shot: float
    scoped_minimum: float
    single_shot: bool


# APK GunData drift fields and GSD_SNIPE_DRIFT_MIN, in degrees.
SNIPERS = {
    12: SniperTuning(2, 5, 10, 10, 4, True),   # 98K
    17: SniperTuning(2, 6, 10, 10, 4, False),  # SG550
    18: SniperTuning(2, 4, 10, 10, 4, True),   # AWM
}


@dataclass
class EquippedGun:
    gun: Gun
    ammo: int = field(init=False)
    next_shot: float = 0.0
    skin: int | None = None
    drift: float = 0.0
    drift_updated_at: float | None = None

    def __post_init__(self):
        self.ammo = self.gun.magazine

    def recover_drift(self, now, minimum, tuning):
        if self.drift_updated_at is not None:
            self.drift -= (now - self.drift_updated_at) * tuning.recovery_per_second
        self.drift = min(tuning.maximum, max(minimum, self.drift))
        self.drift_updated_at = now


@dataclass
class Vehicle:
    id: int
    kind: int
    x: float
    z: float
    yaw: float = 90.0
    speed: float = 0.0
    occupants: dict[int, int] = field(default_factory=dict)
    health: float = field(init=False)
    controls: set[int] = field(default_factory=set)
    contacts: set[int] = field(default_factory=set)

    def __post_init__(self):
        self.health = self.spec[0]

    @property
    def spec(self):
        return VEHICLE_SPECS[(self.kind - 1) % 3 + 1]

    @property
    def driver(self):
        return self.occupants.get(0)

    def heading(self):
        angle = math.radians(self.yaw)
        return math.sin(angle), math.cos(angle)

    def ray_hit(self, x, z, dx, dz, distance):
        """First intersection with the oriented main collider in the ground plane."""
        forward_x, forward_z = self.heading()
        x, z = x - self.x, z - self.z
        _, _, half_width, half_length = self.spec
        near, far = 0.0, distance
        for start, step, extent in (
            (x * forward_z - z * forward_x, dx * forward_z - dz * forward_x, half_width),
            (x * forward_x + z * forward_z, dx * forward_x + dz * forward_z, half_length),
        ):
            if abs(step) < 1e-6:
                if abs(start) > extent:
                    return None
                continue
            entry, exit = sorted(((-extent - start) / step, (extent - start) / step))
            near, far = max(near, entry), min(far, exit)
            if near > far:
                return None
        return near

    def status(self):
        half_angle = math.radians(self.yaw) / 2
        return struct.pack(
            "<8f", self.x, 15.0, self.z, math.cos(half_angle),
            0.0, math.sin(half_angle), 0.0, self.speed,
        )

    def footprint(self, x, z, yaw):
        angle = math.radians(yaw)
        _, _, width, length = self.spec
        return x, z, math.sin(angle), math.cos(angle), width, length

    @staticmethod
    def overlaps(a, b):
        # Separating axes are the width and length axes of each rectangle.
        ax, az, afx, afz, aw, al = a
        bx, bz, bfx, bfz, bw, bl = b
        dx, dz = bx - ax, bz - az
        parallel = abs(afx * bfx + afz * bfz)
        perpendicular = abs(afx * bfz - afz * bfx)
        return (
            abs(dx * afz - dz * afx) < aw + bw * parallel + bl * perpendicular
            and abs(dx * afx + dz * afz) < al + bl * parallel + bw * perpendicular
            and abs(dx * bfz - dz * bfx) < bw + aw * parallel + al * perpendicular
            and abs(dx * bfx + dz * bfz) < bl + al * parallel + aw * perpendicular
        )

    @staticmethod
    def overlaps_wall(footprint, wall):
        left, bottom, right, top = wall
        box = ((left + right) / 2, (bottom + top) / 2, 0, 1, (right - left) / 2, (top - bottom) / 2)
        return Vehicle.overlaps(footprint, box)

    def blocked(self, x, z, yaw, walls, other_vehicles):
        footprint = self.footprint(x, z, yaw)
        return any(self.overlaps_wall(footprint, wall) for wall in walls) or any(
            other is not self and self.overlaps(footprint, other.footprint(other.x, other.z, other.yaw))
            for other in other_vehicles
        )

    def step(self, obstacles, other_vehicles):
        if not self.health:
            return False
        throttle = (105 in self.controls) - (107 in self.controls)
        steer = (111 in self.controls) - (109 in self.controls)
        target = throttle * (10 if throttle >= 0 else 5)
        self.speed += max(-16 * TICK, min(16 * TICK, target - self.speed))
        if not self.speed:
            return False
        if steer:
            yaw = (self.yaw + steer * 95 * TICK * math.copysign(1, self.speed)) % 360
            if not self.blocked(self.x, self.z, yaw, obstacles, other_vehicles):
                self.yaw = yaw
        dx, dz = self.heading()
        x = max(-69.0, min(79.0, self.x + dx * self.speed * TICK))
        z = max(-11.4, min(56.6, self.z + dz * self.speed * TICK))
        # At 20 Hz the car moves at most .5 m. Check the midpoint as well
        # to catch a grazing corner while the body moves along a wall.
        if (self.blocked((self.x + x) / 2, (self.z + z) / 2, self.yaw, obstacles, other_vehicles)
                or self.blocked(x, z, self.yaw, obstacles, other_vehicles)):
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
    primary: Gun
    world: asyncio.StreamWriter | None = None
    world_ticket: int = 0
    room_ready: bool = False
    battle: asyncio.StreamWriter | None = None
    udp: tuple | None = None
    disconnected_at: float = 0.0
    ready: bool = False
    status: tuple[float, ...] = field(init=False)
    guns: list[EquippedGun] = field(init=False)
    reserve: dict[int, int] = field(init=False)
    slot: int = PRIMARY_SLOT
    previous_slot: int = PRIMARY_SLOT
    firing: bool = False
    fired_during_operation: bool = False
    scoped: bool = False
    equip_ready_at: float = 0.0
    next_swing: float = 0.0
    reload_at: float = 0.0
    vehicle: Vehicle | None = None
    health: float = MAX_HEALTH
    revive_at: float = 0.0
    protected_until: float = 0.0
    throwables: set[int] = field(default_factory=set)
    throwable: int = 0
    throw_started: float = 0.0
    throw_at: float = 0.0
    throw_release_at: float = 0.0
    medkits: int = 0
    med_at: float = 0.0
    med_casting: bool = False
    skins: dict[tuple[int, int, int], int] = field(default_factory=dict)

    profile_name: str = ""
    nickname: str = ""
    character: int = 1

    def __post_init__(self):
        self.reset_for_round(self.primary)

    def reset_for_round(self, primary):
        self.status = (2.5 * self.id, 15.2, 3, 0, 0)
        self.udp = None
        self.ready = self.firing = self.scoped = False
        self.vehicle = None
        self.health = MAX_HEALTH
        self.revive_at = self.protected_until = self.reload_at = 0.0
        self.reset_weapons(primary)

    def reset_weapons(self, primary):
        self.primary = primary
        self.guns = [EquippedGun(primary), EquippedGun(choice(SIDEARMS))]
        self.throwables = {1, 2}
        self.medkits = 1
        self.throwable = 0
        self.throw_started = 0.0
        self.throw_at = self.throw_release_at = self.med_at = 0.0
        self.med_casting = False
        self.reserve = {}
        for weapon in self.guns:
            ammo_kind = weapon.gun.ammo_kind
            self.reserve[ammo_kind] = self.reserve.get(ammo_kind, 0) + 4 * weapon.gun.magazine
        self.slot = PRIMARY_SLOT
        self.previous_slot = PRIMARY_SLOT
        self.fired_during_operation = False
        self.equip_ready_at = 0.0
        self.next_swing = 0.0

    def name(self):
        display = (self.nickname or f"LAN Test {self.id}")[:16]
        return display.encode("utf-16le").ljust(34, b"\0")

    def profile(self, now):
        money_and_stats = struct.pack("<6q7I", now, 0, 0, 0, 0, 0, self.character, 0, 0, 0, 0, 0, 0)
        purchase_history = bytes(struct.calcsize("<qHqH"))
        platform_rewards = bytes(struct.calcsize("<?3q?qI"))
        leaderboard = bytes(struct.calcsize("<q6Iq6I8?17H"))
        sign_in = struct.pack("<HqH", 0, now, 1)
        return self.name() + money_and_stats + purchase_history + platform_rewards + leaderboard + sign_in

    def weapon_id(self, slot):
        return self.id * 10 + slot + 1

    def throwable_id(self, kind):
        return self.id * 10 + 5 + kind

    def med_id(self):
        return self.id * 10 + 9

    def selected_id(self):
        if self.slot == THROWABLE_SLOT:
            return self.throwable_id(self.throwable)
        if self.slot == MED_SLOT:
            return self.med_id()
        return self.weapon_id(self.slot)

    def applied_skin(self, category, item):
        """Item skins are shared by both characters; clothing stays per character."""
        for character in (self.character, 1, 2):
            skin = self.skins.get((character, category, item))
            if skin is not None:
                return skin
        return None

    def gun_skin(self, weapon):
        return weapon.skin if weapon.skin is not None else self.applied_skin(1, weapon.gun.kind)

    def loadout(self):
        storage = []
        for kind, count in self.reserve.items():
            if count:
                storage.append(struct.pack("<IHII", self.id * 100 + kind, 7, kind, count))
        for kind in sorted(self.throwables):
            storage.append(self.item(self.throwable_id(kind), 4, kind, self.applied_skin(3, kind)))
        if self.medkits:
            storage.append(self.item(self.med_id(), 5, FIRST_AID_KIND, self.applied_skin(4, FIRST_AID_KIND)))

        weapons = bytearray(struct.pack("<H", 3))
        for slot, weapon in enumerate(self.guns):
            weapons.extend(struct.pack("<H", slot))
            weapons.extend(self.item(self.weapon_id(slot), 2, weapon.gun.kind, self.gun_skin(weapon)))
            weapons.extend(struct.pack("<IH", weapon.ammo, 0))
        weapons.extend(struct.pack("<H", MELEE_SLOT))
        weapons.extend(self.item(self.weapon_id(MELEE_SLOT), 1, MACHETE_KIND, self.applied_skin(0, MACHETE_KIND)))
        outfit = self.outfit()
        return (struct.pack("<H", len(storage)) +
                b"".join(struct.pack("<H", index) + item for index, item in enumerate(storage)) +
                weapons + struct.pack("<H", len(outfit)) + b"".join(
                    struct.pack("<H", kind - 98) +
                    self.item(self.id * 100 + 10 + kind - 101, 3, kind, skin) +
                    struct.pack("<H", self.character - 1)
                    for kind, skin in outfit
                ))

    @staticmethod
    def item(item_id, item_type, kind, skin):
        item = struct.pack("<IHII?", item_id, item_type, kind, 1, skin is not None)
        return item if skin is None else item + skin_stats(skin)

    def outfit(self):
        # Only clothing is added to the unit. Armor remains disabled in this mode.
        return [
            (item, kind) for (character, category, item), kind in self.skins.items()
            if character == self.character and category == 2 and 101 <= item <= 105
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
    def __init__(self, host, size, stock_vehicles=False, owner_profile=None):
        self.host = host
        self.size = size
        self.owner_profile = owner_profile.casefold() if owner_profile else None
        self.players = {}
        self.owner = None
        self.battle_id = BATTLE_ID
        self.vehicle_skin_offset = 0 if stock_vehicles else 3
        root = BASE_DIR
        self.obstacles = json.loads((root / "map102-vehicle-obstacles.json").read_text(encoding="utf-8"))
        props_file = root / "map102-vehicle-props.json"
        if not props_file.is_file():
            raise SystemExit("Map 102 prop data missing. Run python export-local-data.py first.")
        props = json.loads(props_file.read_text(encoding="utf-8"))
        if isinstance(props, list):
            raise SystemExit("Map 102 prop data is outdated. Run python export-local-data.py again.")
        self.initial_movable_props = {prop_id: (health, bounds) for prop_id, health, bounds in props["movable"]}
        self.initial_blast_doors = {prop_id: (health, bounds) for prop_id, health, bounds in props["doors"]}
        self.blast_doorway = {
            prop_id: min(DOOR_BOUNDS, key=lambda door: math.dist(
                ((bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2), DOORS[door]
            )) for prop_id, (_, bounds) in self.initial_blast_doors.items()
        }
        self.reset_round_state()
        self.profile_path = root / "lan-profiles.json"
        self.saved_profiles = json.loads(self.profile_path.read_text(encoding="utf-8")) if self.profile_path.exists() else {}
        self.skin_catalog = {
            kind: (category, item)
            for kind, category, item in json.loads((root / "skin-catalog.json").read_text(encoding="utf-8"))
        }
        self.skin_inventory = struct.pack("<I", len(self.skin_catalog)) + b"".join(
            struct.pack("<I", kind) + skin_stats(kind) for kind in self.skin_catalog
        )

    def reset_round_state(self):
        self.queued = []
        self.joined = []
        self.started = False
        self.loading_deadline = 0.0
        self.round_started = 0.0
        self.round_ending = False
        self.forwarded = set()
        self.open_doors = set()
        skin_offset = self.vehicle_skin_offset
        self.vehicles = {
            vehicle.id: vehicle for vehicle in (
                Vehicle(101, 1 + skin_offset, 12.0, -12.0),
                Vehicle(102, 2 + skin_offset, -62.0, -8.0),
                Vehicle(103, 3 + skin_offset, 79.0, -3.0),
            )
        }
        self.bullet_id = 0
        self.rockets = []
        self.grenades = []
        self.loot = {}
        self.next_loot = 1000
        self.live_bullets = {}
        self.movable_props = self.initial_movable_props.copy()
        self.blast_doors = self.initial_blast_doors.copy()

    def finish_round(self, reason):
        print(f"round {self.battle_id} ended: {reason}", flush=True)
        for player in self.queued:
            if player.battle is not None:
                player.battle.close()
                player.battle = None
            player.udp = None
        members = self.lobby_members()
        self.players = {player.id: player for player in members}
        for player in members:
            player.room_ready = False
        if self.owner not in members:
            if self.owner_profile:
                self.owner = next((p for p in members if p.profile_name == self.owner_profile), None)
            else:
                self.owner = members[0] if members else None
        self.battle_id += 1
        self.reset_round_state()
        self.send_room_state()

    def player_left(self, player):
        # Let the remaining clients remove the unit instead of leaving a frozen body.
        if player in self.joined:
            self.joined.remove(player)
        if player in self.queued:
            self.queued.remove(player)
        packet = frame(214, struct.pack("<I", player.id))
        for member in self.joined:
            if member.battle is not None:
                member.battle.write(packet)
        print(f"battle left: player {player.id}", flush=True)

    def admin_status(self):
        return {
            "started": self.started,
            "battle_id": self.battle_id,
            "size": self.size,
            "owner": self.owner.id if self.owner else None,
            "players": [
                {
                    "id": player.id,
                    "name": player.nickname or f"LAN Test {player.id}",
                    "owner": player is self.owner,
                    "ready": player.room_ready,
                    "battle": player.battle is not None,
                    "disconnected": bool(player.disconnected_at),
                }
                for player in self.lobby_members()
            ],
        }

    def admin_reset(self):
        self.finish_round("admin reset")
        return True

    def admin_kick(self, player_id):
        player = self.players.get(player_id)
        if player is None:
            return False
        if player.battle is not None:
            player.battle.close()
        if player.world is not None:
            player.world.close()
        if player in self.joined:
            if player.vehicle:
                self.unmount(player)
            self.player_left(player)
        if self.players.get(player.id) is player and not self.queued:
            self.release_lobby_slot(player)
        self.send_room_state()
        return True

    def admin_logs(self):
        path = BASE_DIR / "server.log"
        try:
            lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        except OSError as error:
            return f"cannot read log: {error}"
        return "\n".join(lines[-200:])

    @staticmethod
    def skin_applies(player):
        return struct.pack("<I", len(player.skins)) + b"".join(
            struct.pack("<II", character, skin) for (character, _, _), skin in player.skins.items()
        )

    def save_profile(self, player):
        self.saved_profiles[player.profile_name] = {
            "character": player.character,
            "nickname": player.nickname,
            "skins": [[*key, value] for key, value in sorted(player.skins.items())],
        }
        text = json.dumps(self.saved_profiles, indent=2) + "\n"
        temporary = self.profile_path.with_suffix(".tmp")
        temporary.write_text(text, encoding="utf-8")
        try:
            self.profile_path.with_suffix(".bak").write_text(text, encoding="utf-8")
        except OSError:
            pass
        temporary.replace(self.profile_path)

    async def login(self, reader, writer):
        message, payload = await receive(reader)
        if message != 20 or len(payload) < 4:
            raise ValueError("Unexpected first login message")
        first_slot = 1
        slot = next((i for i in range(first_slot, self.size + 1) if i not in self.players), None)
        if self.queued or slot is None:
            raise ValueError("The LAN room is full or already in a match.")
        player = Player(slot, choice(PRIMARY_GUNS))
        player.world_ticket = randbits(32) or 1
        player.profile_name = f"slot-{player.id}"
        if len(payload) >= 6:
            size, = struct.unpack_from("<H", payload, 4)
            name = payload[6:6 + size].decode("utf-16le", errors="replace")
            profile, separator, version = name.partition("|")
            if not separator or version != LAN_VERSION:
                raise ValueError("Client version mismatch; update the LAN client.")
            if profile != "1" and re.fullmatch(r"[a-zA-Z0-9_-]{1,32}", profile):
                player.profile_name = profile.casefold()
        if any(p.profile_name == player.profile_name for p in self.players.values()):
            raise ValueError("Choose a different -lan-profile for each client.")
        saved = self.saved_profiles.get(player.profile_name, {})
        player.character = saved.get("character", 1)
        player.nickname = saved.get("nickname", "")
        player.skins = {
            (character, category, kind): skin
            for character, category, kind, skin in saved.get("skins", ())
            if self.skin_catalog.get(skin) == (category, kind)
        }
        self.players[player.id] = player
        print(f"login <- 20; assigned test player {player.id}", flush=True)
        asyncio.get_running_loop().call_later(20, self.expire_login, player)
        await send(writer, 20, struct.pack("<HII", 0, player.id, player.world_ticket) + endpoint(self.host, WORLD_PORT))
        await reader.read()

    def expire_login(self, player):
        if self.players.get(player.id) is player and player.world is None:
            del self.players[player.id]

    def lobby_members(self):
        return [p for p in self.players.values() if p.world is not None and not p.world.is_closing()]

    def send_room_state(self):
        members = self.lobby_members()
        ready = sum(p.room_ready for p in members)
        member_data = struct.pack("<H", len(members))
        for player in members:
            name = player.name()
            member_data += struct.pack("<IBBH", player.id, player.room_ready, player is self.owner, len(name)) + name
        for member in members:
            payload = struct.pack("<BBB???", ready, len(members), self.size,
                                  member.room_ready, member is self.owner, bool(self.queued)) + member_data
            member.world.write(frame(ROOM_STATE, payload))

    def is_local_peer(self, writer):
        address = writer.get_extra_info("peername")[0]
        return address == self.host or (address == HOST and ipaddress.IPv4Address(self.host).is_loopback)

    def release_lobby_slot(self, player):
        del self.players[player.id]
        if self.owner is player:
            if self.owner_profile and player.profile_name == self.owner_profile:
                self.owner = None
            else:
                self.owner = next(iter(self.lobby_members()), None)
        self.send_room_state()

    def start_room(self, owner):
        if owner is not self.owner or not owner.room_ready or self.queued:
            return
        self.queued = [p for p in self.lobby_members() if p.room_ready]
        for player in self.queued:
            player.reset_for_round(choice(PRIMARY_GUNS))
        self.loading_deadline = time.monotonic() + BATTLE_LOAD_TIMEOUT
        self.send_room_state()
        print(f"room started by player {owner.id} with {len(self.queued)} ready player(s)", flush=True)
        assignment = frame(32, endpoint(self.host, BATTLE_PORT) + struct.pack("<I", self.battle_id))
        for member in self.queued:
            member.world.write(assignment)

    async def world(self, reader, writer):
        message, payload = await receive(reader)
        if message != 23 or len(payload) != 8:
            raise ValueError("Unexpected first world message")
        player_id, ticket = struct.unpack("<II", payload)
        player = self.players.get(player_id)
        if self.queued or player is None or player.world_ticket != ticket or player.world is not None:
            raise ValueError("Unknown or duplicate test player, or room already started")
        player.world = writer
        if self.owner is None and (self.owner_profile is None or player.profile_name == self.owner_profile):
            self.owner = player
        try:
            await self.run_lobby(player, reader, writer)
        finally:
            player.world = None
            if not self.queued and self.players.get(player.id) is player:
                self.release_lobby_slot(player)

    async def run_lobby(self, player, reader, writer):
        now = int(time.time())
        print(f"world <- 23; player {player.id}", flush=True)
        await send(writer, 23, struct.pack("<HHqiqq", 0, WORLD_ID, now, 0, now + 86400, now + 86400 * 30))
        await send(writer, 117, struct.pack("<H", 0) + player.profile(now))
        await send(writer, SKINS_GET, self.skin_inventory)
        await send(writer, SKIN_APPLIES_GET, self.skin_applies(player))
        await send(writer, 119)
        self.send_room_state()
        while True:
            message, payload = await receive(reader)
            if message == 1:
                await send(writer, message, payload)
            elif message == SKINS_GET and not payload:
                await send(writer, SKINS_GET, self.skin_inventory)
            elif message == SKIN_APPLIES_GET and not payload:
                await send(writer, SKIN_APPLIES_GET, self.skin_applies(player))
            elif message == CHARACTER_SELECT and len(payload) == 4:
                character, = struct.unpack("<I", payload)
                if character in (1, 2):
                    player.character = character
                    self.save_profile(player)
                    await send(writer, CHARACTER_SELECT, payload)
            elif message == SKIN_APPLY and len(payload) == 14:
                character, category, item, skin = struct.unpack("<IHII", payload)
                key = (character, category, item)
                if character in (1, 2) and (skin == NO_SKIN or self.skin_catalog.get(skin) == (category, item)):
                    if skin == NO_SKIN:
                        player.skins.pop(key, None)
                    else:
                        player.skins[key] = skin
                    self.save_profile(player)
                    await send(writer, SKIN_APPLY, payload)
            elif message == NICKNAME and len(payload) >= 2:
                size, = struct.unpack_from("<H", payload, 0)
                if 2 <= size <= 32 and len(payload) >= 2 + size:
                    name = payload[2:2 + size].decode("utf-16le", errors="replace").strip()
                    if name and not any(ord(ch) < 32 for ch in name):
                        player.nickname = name[:16]
                        self.save_profile(player)
                        await send(writer, 117, struct.pack("<H", 0) + player.profile(int(time.time())))
                        print(f"nickname: player {player.id} -> {player.nickname}", flush=True)
            elif message == 27 and payload == struct.pack("<H", RESPAWN_MODE) and not self.queued:
                player.room_ready = not player.room_ready
                self.send_room_state()
            elif message == ROOM_START and not payload:
                self.start_room(player)

    def battle_snapshot(self):
        count = len(self.joined)
        header = struct.pack("<fHH", 60.0, 0, count)
        units = bytearray()
        for player in self.joined:
            units.extend(struct.pack("<III", player.id, player.character, 0))
            units.extend(struct.pack("<H3f5f", player.id, player.health, MAX_HEALTH, 6.5, *player.status))
            units.extend(player.loadout())
        players = b"".join(
            struct.pack("<HI", WORLD_ID, p.id) + p.name() + struct.pack("<IIHI", p.character, 0, 0, p.id)
            for p in self.joined
        )
        return header + units + struct.pack("<H", count) + players

    async def battle(self, reader, writer):
        message, payload = await receive(reader)
        if message != 34 or len(payload) != 10:
            raise ValueError("Unexpected first battle message")
        battle_id, world_id, player_id = struct.unpack("<IHI", payload)
        player = self.players.get(player_id)
        if (battle_id, world_id) != (self.battle_id, WORLD_ID) or player not in self.queued or player.battle is not None:
            raise ValueError("Unknown or duplicate battle player")
        reconnect = player in self.joined
        if reconnect and not player.disconnected_at:
            raise ValueError("Duplicate battle player")
        if not reconnect:
            self.joined.append(player)
        player.battle = writer
        player.disconnected_at = 0.0
        if reconnect:
            print(f"battle reconnect <- 34; player {player.id}", flush=True)
        try:
            await self.run_battle(player, reader, writer, battle_id)
        finally:
            if player.battle is writer:
                player.battle = player.udp = None
                if not self.started:
                    self.finish_round("a player disconnected while loading")
                else:
                    player.disconnected_at = time.monotonic()
                    player.firing = False
                    player.reload_at = 0.0
                    print(f"battle disconnected; waiting for player {player.id}", flush=True)

    async def run_battle(self, player, reader, writer, battle_id):
        print(f"battle <- 34; player {player.id}", flush=True)
        writer.write(frame(34, struct.pack("<HIIHI", 0, BATTLE_UDP_PORT, player.id, 1, MAP_ID)))
        if len(self.joined) == len(self.queued):
            await asyncio.sleep(0.5)
            if self.battle_id != battle_id:
                return
            snapshot = frame(37, self.battle_snapshot())
            for member in self.joined:
                member.battle.write(snapshot)
        while self.battle_id == battle_id:
            message, payload = await receive(reader)
            if self.battle_id != battle_id:
                return
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
                    initial_state = frame(39, struct.pack("<H", 0)) + frame(40) + frame(VEHICLES_SPAWN, vehicles)
                    initial_state += b"".join(
                        frame(SWITCH_WEAPON, struct.pack("<II", equipped.id, equipped.weapon_id(PRIMARY_SLOT)))
                        for equipped in self.joined
                    )
                    self.started = True
                    self.round_started = time.monotonic()
                    for member in self.joined:
                        member.battle.write(initial_state)
            elif self.started:
                self.battle_input(player, message, payload)

    def broadcast(self, message, payload):
        packet = frame(message, payload)
        for player in self.joined:
            if player.battle and not player.battle.is_closing():
                player.battle.write(packet)

    def battle_input(self, player, message, payload):
        if message == BATTLE_CHAT:
            self.battle_chat(player, payload)
            return
        if message == REPORT_HIT and len(payload) == 8:
            self.report_hit(player, *struct.unpack("<II", payload), time.monotonic())
            return
        if message == SCOPE_STATE and payload in (b"\x00", b"\x01"):
            scoped = (payload == b"\x01" and not player.revive_at and player.vehicle is None
                      and player.slot < MELEE_SLOT and player.guns[player.slot].gun.kind in SNIPERS)
            self.set_scope(player, scoped)
            return
        if player.revive_at:
            return
        if message == LAST_WEAPON and not payload and player.vehicle is None:
            if player.previous_slot <= MELEE_SLOT and player.previous_slot != player.slot:
                self.switch(player, player.previous_slot, time.monotonic())
            return
        if message == WHEEL_SWITCH and payload in (b"\x00", b"\x01") and player.vehicle is None:
            forward = payload == b"\x01"
            current = player.slot if player.slot <= MELEE_SLOT else (-1 if forward else 0)
            self.switch(player, (current + (1 if forward else -1)) % (MELEE_SLOT + 1), time.monotonic())
        elif message == SWITCH_WEAPON and len(payload) == 4 and player.vehicle is None:
            item_id, = struct.unpack("<I", payload)
            now = time.monotonic()
            slot = item_id - player.weapon_id(PRIMARY_SLOT)
            if PRIMARY_SLOT <= slot <= MELEE_SLOT and slot != player.slot:
                self.switch(player, slot, now)
            elif item_id == player.med_id() and player.medkits:
                if player.slot != MED_SLOT:
                    self.switch(player, MED_SLOT, now)
                self.start_first_aid(player, now)
            else:
                for kind in player.throwables:
                    if item_id == player.throwable_id(kind) and item_id != player.selected_id():
                        player.throwable = kind
                        self.switch(player, THROWABLE_SLOT, now)
                        break
        elif message in (OPERATE, STOP_OPERATE) and len(payload) == 20 and player.vehicle is None:
            status = struct.unpack("<5f", payload)
            if all(map(math.isfinite, status)):
                if player.scoped:
                    # Sniping is stationary: keep the last ground position but
                    # still allow the player to turn and aim.
                    status = (player.status[0], player.status[1], player.status[2], status[3], status[4])
                player.status = status
                now = time.monotonic()
                if player.slot == THROWABLE_SLOT:
                    if message == OPERATE and not (player.throw_started or player.throw_at) and now >= player.equip_ready_at:
                        player.throw_started = now
                        self.broadcast(OPERATE, struct.pack("<I?", player.id, False))
                    elif message == STOP_OPERATE and player.throw_started:
                        player.throw_release_at = max(now, player.throw_started + .35)
                        player.throw_at = player.throw_release_at + .55
                        player.throw_started = 0.0
                elif player.slot == MED_SLOT:
                    if message == OPERATE:
                        self.start_first_aid(player, now)
                else:
                    player.firing = message == OPERATE
                    if player.firing:
                        self.fire(player, now)
                    else:
                        player.fired_during_operation = False
        elif message == LOOT and len(payload) == 12 and player.vehicle is None:
            loot_id, source_slot, _, _, _ = struct.unpack("<I4H", payload)
            if source_slot == 0:
                self.pickup(player, loot_id, time.monotonic())
        elif message == RELOAD and not payload and player.vehicle is None:
            self.start_reload(player, time.monotonic())
        elif message in (OPEN_DOOR, CLOSE_DOOR) and len(payload) == 4:
            door, = struct.unpack("<I", payload)
            if door in DOORS and math.dist(DOORS[door], (player.status[0], player.status[2])) <= 5:
                if message == OPEN_DOOR:
                    self.open_doors.add(door)
                elif any(vehicle.overlaps_wall(vehicle.footprint(vehicle.x, vehicle.z, vehicle.yaw), DOOR_BOUNDS[door])
                         for vehicle in self.vehicles.values()):
                    return
                else:
                    self.open_doors.discard(door)
                self.broadcast(message, struct.pack("<II", player.id, door))
                print(f"door {door}: {'opened' if message == OPEN_DOOR else 'closed'} by player {player.id}", flush=True)
        elif message == MOUNT and len(payload) == 6 and player.vehicle is None:
            vehicle_id, seat = struct.unpack("<IH", payload)
            vehicle = self.vehicles.get(vehicle_id)
            if (vehicle and vehicle.health and seat < VEHICLE_SEATS and seat not in vehicle.occupants
                    and math.dist((vehicle.x, vehicle.z), (player.status[0], player.status[2])) <= 6):
                vehicle.occupants[seat] = player.id
                player.vehicle = vehicle
                self.set_scope(player, False)
                player.firing = False
                player.fired_during_operation = False
                self.cancel_reload(player)
                self.cancel_item(player)
                self.broadcast(MOUNT, struct.pack("<IIH", player.id, vehicle.id, seat))
                print(f"vehicle {vehicle.id}: player {player.id} mounted seat {seat}", flush=True)
        elif message == UNMOUNT and not payload and player.vehicle:
            self.unmount(player)
        elif (DRIVE_FORWARD <= message <= STOP_TURN_RIGHT and not payload
              and player.vehicle and player.vehicle.driver == player.id):
            vehicle = player.vehicle
            if (message - DRIVE_FORWARD) % 2:
                vehicle.controls.discard(message - 1)
            else:
                vehicle.controls.add(message)
                print(f"vehicle {vehicle.id}: drive input {message}", flush=True)
            self.broadcast(message, struct.pack("<I", vehicle.id))

    def set_scope(self, player, scoped):
        if player.scoped != scoped:
            if player.slot < MELEE_SLOT:
                weapon = player.guns[player.slot]
                if tuning := SNIPERS.get(weapon.gun.kind):
                    old_minimum = tuning.scoped_minimum if player.scoped else tuning.minimum
                    weapon.recover_drift(time.monotonic(), old_minimum, tuning)
                    if scoped:
                        weapon.drift = max(weapon.drift, tuning.scoped_minimum)
            player.scoped = scoped
            self.broadcast(SCOPE_POSE, struct.pack("<I?", player.id, scoped))

    def start_first_aid(self, player, now):
        if player.medkits and player.health < MAX_HEALTH and not player.med_at:
            player.med_at = max(now, player.equip_ready_at)

    def switch(self, player, slot, now):
        if slot < MELEE_SLOT and player.guns[slot].gun.kind in SNIPERS:
            weapon = player.guns[slot]
            weapon.drift = 0.0
            weapon.drift_updated_at = None
            if player.slot == MELEE_SLOT:
                weapon.next_shot = 0.0
        else:
            self.set_scope(player, False)
        self.cancel_reload(player)
        self.cancel_item(player)
        if slot != player.slot and player.slot <= MELEE_SLOT:
            player.previous_slot = player.slot
        player.slot = slot
        player.firing = False
        player.fired_during_operation = False
        player.equip_ready_at = now + EQUIP_TIME
        self.broadcast(SWITCH_WEAPON, struct.pack("<II", player.id, player.selected_id()))

    def cancel_item(self, player):
        if player.throw_started or player.throw_at or player.med_casting:
            self.broadcast(STOP_OPERATE, struct.pack("<I?", player.id, False))
        player.med_casting = False
        player.throw_started = player.throw_at = player.throw_release_at = player.med_at = 0.0

    def update_item(self, player, now):
        if player.throw_release_at and now >= player.throw_release_at:
            self.broadcast(STOP_OPERATE, struct.pack("<I?", player.id, False))
            player.throw_release_at = 0.0
        if player.throw_at and now >= player.throw_at:
            player.throw_at = 0.0
            self.throw_grenade(player, now)
            self.switch(player, PRIMARY_SLOT, now)
        if player.med_at and now >= player.med_at:
            operation = struct.pack("<I?", player.id, False)
            if not player.med_casting:
                player.med_casting = True
                player.med_at = now + FIRST_AID_CAST_TIME
                self.broadcast(OPERATE, operation)
            else:
                player.med_casting = False
                player.med_at = 0.0
                self.broadcast(STOP_OPERATE, operation)
                self.broadcast(USE, struct.pack("<III", player.id, player.med_id(), 1))
                player.medkits -= 1
                player.health = min(MAX_HEALTH, player.health + FIRST_AID_HEAL)
                self.switch(player, PRIMARY_SLOT, now)
                print(f"medkit: player {player.id} healed", flush=True)

    def throw_grenade(self, player, now):
        kind = player.throwable
        player.throwables.remove(kind)
        self.bullet_id += 1
        projectile = self.bullet_id
        x, y, z, yaw, aim = player.status
        angle = math.radians(yaw)
        dx, dz = math.sin(angle), math.cos(angle)
        distance = self.clear_distance(x, z, dx, dz, max(2.0, min(25.0, aim)), self.obstacles)
        impact = (x + dx * distance, y + .2, z + dz * distance)
        self.broadcast(THROW, struct.pack("<II13f", player.id, projectile, x, y + 1.5, z,
                                          dx * distance, 4, dz * distance, 1, 0, 0, 0, 2, 2, 0))
        self.grenades.append((now + 1.5, now + 11.5, False, player, projectile, kind, impact))
        print(f"throw: player {player.id}, kind {kind}", flush=True)

    def update_grenades(self, now):
        pending = []
        for when, end, exploded, owner, projectile, kind, impact in self.grenades:
            if when > now:
                pending.append((when, end, exploded, owner, projectile, kind, impact))
                continue
            if not exploded:
                self.broadcast(THROWABLE_EXPLODE, struct.pack("<I3f", projectile, *impact))
                if kind == 1:
                    self.blast(owner, projectile, impact, 7, 700, THROWABLE_HIT_UNIT, now)
            if kind == 2 and now < end:
                self.blast(owner, projectile, impact, 9, 100, INCENDIARY_HIT_UNIT, now)
                pending.append((when + .7, end, True, owner, projectile, kind, impact))
        self.grenades = pending

    def blast(self, owner, projectile, impact, radius, damage, message, now):
        x, _, z = impact
        for target in self.joined:
            if target is owner or not self.can_damage(target, now):
                continue
            tx, ty, tz = target.status[:3]
            if target.vehicle is not None:
                tx, ty, tz = target.vehicle.x, 15.0, target.vehicle.z
            if math.dist(impact, (tx, ty + 1, tz)) > radius or self.melee_blocked(x, z, tx - x, tz - z):
                continue
            self.broadcast(message, struct.pack("<IIff", projectile, target.id, target.health, damage))
            self.apply_damage(owner, target, damage, now)

        for vehicle in self.vehicles.values():
            if (math.dist(impact, (vehicle.x, 16.0, vehicle.z)) <= radius
                    and not self.melee_blocked(x, z, vehicle.x - x, vehicle.z - z)):
                packet = struct.pack("<IIf", projectile, vehicle.id, damage)
                self.damage_vehicle(owner, vehicle, damage, VEHICLE_BLAST_MESSAGES[message], packet, now)

    def rocket_hit_props(self, projectile, impact, radius, damage):
        x, _, z = impact
        for props in (self.movable_props, self.blast_doors):
            for prop_id, (health, bounds) in list(props.items()):
                left, bottom, right, top = bounds
                px = max(left, min(right, x))
                pz = max(bottom, min(top, z))
                if math.hypot(px - x, pz - z) > radius or self.melee_blocked(x, z, px - x, pz - z):
                    continue
                self.broadcast(BULLET_EXPLOSION_HIT_OBSTACLE,
                               struct.pack("<II5f", projectile, prop_id, *impact, radius, damage))
                if damage >= health:
                    del props[prop_id]
                else:
                    props[prop_id] = (health - damage, bounds)

    def damage_vehicle(self, shooter, vehicle, damage, message, packet, now):
        if not vehicle.health:
            return
        vehicle.health = max(0.0, vehicle.health - damage)
        occupants = set(vehicle.occupants.values())
        if not vehicle.health:
            vehicle.speed = 0.0
            for occupant in occupants:
                self.unmount(self.players[occupant])
        self.broadcast(message, packet)
        print(f"vehicle {vehicle.id}: health {vehicle.health:g}", flush=True)
        if vehicle.health:
            return

        self.broadcast(VEHICLE_EXPLODE, struct.pack("<I", vehicle.id))
        _, radius, _, _ = vehicle.spec
        for target in self.joined:
            if target is shooter or not self.can_damage(target, now):
                continue
            tx, ty, tz = target.status[:3]
            if target.vehicle is not None:
                tx, ty, tz = target.vehicle.x, 15.0, target.vehicle.z
            if target.id not in occupants and math.dist((tx, ty, tz), (vehicle.x, 15.0, vehicle.z)) > radius:
                continue
            self.broadcast(VEHICLE_EXPLOSION_HIT_UNIT,
                           struct.pack("<IIff", vehicle.id, target.id, target.health, MAX_HEALTH))
            self.apply_damage(shooter, target, MAX_HEALTH, now)

    def vehicle_impacts(self, vehicle, previous_x, previous_z, now):
        driver = self.players.get(vehicle.driver)
        if driver is None:
            vehicle.contacts.clear()
            return
        dx, dz = vehicle.x - previous_x, vehicle.z - previous_z
        moved = math.hypot(dx, dz)
        footprint = vehicle.footprint(vehicle.x, vehicle.z, vehicle.yaw)
        if moved:
            # Hard walls already stopped the car. Open or knock down the softer
            # objects swept by the car on the accepted movement.
            midpoint = vehicle.footprint((vehicle.x + previous_x) / 2, (vehicle.z + previous_z) / 2, vehicle.yaw)
            for door, bounds in DOOR_BOUNDS.items():
                if door not in self.open_doors and any(vehicle.overlaps_wall(pose, bounds) for pose in (midpoint, footprint)):
                    # A car should knock the door leaf away, not play the normal
                    # open animation. Reuse the door's destroyable group when it
                    # has one, and only fall back to opening when it does not.
                    door_center = ((bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2)
                    blast = min(
                        self.blast_doors.items(),
                        key=lambda entry: math.dist(
                            ((entry[1][1][0] + entry[1][1][2]) / 2, (entry[1][1][1] + entry[1][1][3]) / 2),
                            door_center,
                        ),
                        default=None,
                    )
                    if blast is not None and math.dist(
                        ((blast[1][1][0] + blast[1][1][2]) / 2, (blast[1][1][1] + blast[1][1][3]) / 2),
                        door_center,
                    ) < 3:
                        prop_id, (health, _) = blast
                        del self.blast_doors[prop_id]
                        self.broadcast(VEHICLE_HIT_OBSTACLE, struct.pack(
                            "<II4f", vehicle.id, prop_id, previous_x, 15.0, previous_z, health,
                        ))
                    else:
                        self.broadcast(OPEN_DOOR, struct.pack("<II", driver.id, door))
                    self.open_doors.add(door)
            for prop_id, (health, bounds) in list(self.movable_props.items()):
                if any(vehicle.overlaps_wall(pose, bounds) for pose in (midpoint, footprint)):
                    del self.movable_props[prop_id]
                    self.broadcast(VEHICLE_HIT_OBSTACLE, struct.pack(
                        "<II4f", vehicle.id, prop_id, previous_x, 15.0, previous_z, health,
                    ))

        contacts = set()
        for target in self.joined:
            if target.vehicle is not None or not self.can_damage(target, now):
                continue
            x, y, z = target.status[:3]
            if abs(y - 15.0) > 2 or not vehicle.overlaps_wall(footprint, (x - .25, z - .25, x + .25, z + .25)):
                continue
            if target.id in vehicle.contacts:
                contacts.add(target.id)
                continue
            if not moved:
                continue
            if self.melee_blocked(previous_x, previous_z, x - previous_x, z - previous_z):
                continue
            contacts.add(target.id)
            speed = abs(vehicle.speed)
            damage = MAX_HEALTH if speed >= VEHICLE_LETHAL_SPEED else speed * 4
            self.broadcast(VEHICLE_HIT_UNIT, struct.pack(
                "<II9f", vehicle.id, target.id, target.health, x, y + 1, z,
                damage, dx / moved * speed, 0, dz / moved * speed, .25,
            ))
            self.apply_damage(driver, target, damage, now)
        vehicle.contacts = contacts

    def clear_distance(self, x, z, dx, dz, distance, obstacles):
        for left, bottom, right, top in obstacles:
            near, far = .15, distance
            for origin, direction, low, high in ((x, dx, left, right), (z, dz, bottom, top)):
                if abs(direction) < 1e-6:
                    if not low < origin < high:
                        break
                    continue
                entry, exit = sorted(((low - origin) / direction, (high - origin) / direction))
                near, far = max(near, entry), min(far, exit)
                if near >= far:
                    break
            else:
                distance = max(.15, near - .12)
        return distance

    def drop_guns(self, player):
        x, y, z = player.status[:3]
        for slot, weapon in enumerate(player.guns):
            self.next_loot += 1
            loot_id = self.next_loot
            position = (x + (-.45 if slot == PRIMARY_SLOT else .45), y + .1, z)
            skin = player.gun_skin(weapon)
            self.loot[loot_id] = (player.id, slot, position, weapon.gun, weapon.ammo, skin)
            payload = struct.pack("<I7fH", loot_id, *position, 1, 0, 0, 0, 1)
            payload += Player.item(loot_id, 2, weapon.gun.kind, skin) + struct.pack("<I", weapon.ammo)
            self.broadcast(LOOT_SPAWN, payload)

    def pickup(self, player, loot_id, now):
        loot = self.loot.get(loot_id)
        if loot is None:
            return
        owner, slot, position, gun, ammo, skin = loot
        if owner == player.id or math.dist(position, player.status[:3]) > 3:
            return
        del self.loot[loot_id]
        weapon = EquippedGun(gun, skin=skin)
        weapon.ammo = ammo
        self.broadcast(REMOVE_LOOT, struct.pack("<I", loot_id))
        self.equip_gun(player, slot, weapon, gun.magazine, now)
        print(f"loot: player {player.id} picked up gun {gun.kind}", flush=True)

    def equip_gun(self, player, slot, weapon, reserve, now):
        if weapon.gun.kind not in SNIPERS:
            self.set_scope(player, False)
        self.cancel_reload(player)
        self.cancel_item(player)
        player.guns[slot] = weapon
        if slot == PRIMARY_SLOT:
            player.primary = weapon.gun
        ammo_kind = weapon.gun.ammo_kind
        if not player.reserve.get(ammo_kind):
            player.reserve[ammo_kind] = reserve
        active_ammo = {w.gun.ammo_kind for w in player.guns}
        player.reserve = {kind: count for kind, count in player.reserve.items() if kind in active_ammo}
        player.slot = slot
        player.firing = False
        player.fired_during_operation = False
        player.equip_ready_at = now + EQUIP_TIME
        self.broadcast(REFRESH_LOADOUT, struct.pack("<II", player.id, player.selected_id()) + player.loadout())

    def battle_chat(self, player, payload):
        if not 4 < len(payload) <= 516:
            return
        channel, size = struct.unpack_from("<HH", payload)
        if channel != 0 or size != len(payload) - 4:
            return
        try:
            text = payload[4:].decode("utf-16-le").strip()
        except UnicodeDecodeError:
            return
        if not text:
            return

        if text.startswith("/"):
            command = text.lower()
            if command == "/help":
                reply = "/98k 或 /dg 换枪  /guns 枪械列表  /pos 当前坐标  /down 测试倒地"
            elif command == "/guns":
                reply = "  ".join("/" + name for name in GUN_COMMANDS)
            elif command == "/pos":
                x, y, z = player.status[:3]
                if player.vehicle is not None:
                    x, z = player.vehicle.x, player.vehicle.z
                reply = f"坐标 X {x:.1f} / Y {y:.1f} / Z {z:.1f}"
            elif command == "/down":
                if player.revive_at:
                    reply = "已经倒地。"
                else:
                    self.broadcast(UNIT_DIE, struct.pack("<I", player.id))
                    self.knock_down(player, time.monotonic())
                    reply = "测试倒地，3 秒后原地复活。"
            elif gun := GUN_NAMES.get(gun_key(command[1:])):
                if player.revive_at or player.vehicle is not None:
                    reply = "站立且下车后才能换枪。"
                else:
                    slot = 1 if gun in SIDEARMS else PRIMARY_SLOT
                    now = time.monotonic()
                    if player.guns[slot].gun != gun:
                        self.equip_gun(player, slot, EquippedGun(gun), 4 * gun.magazine, now)
                    elif player.slot != slot:
                        self.switch(player, slot, now)
                    reply = f"已装备 {gun.name}（{slot + 1} 键）。"
            else:
                reply = "未知指令，输入 /help 查看。"
            data = f"[本地] {reply}".encode("utf-16-le")
            player.battle.write(frame(BATTLE_CHAT, struct.pack("<HIHH", WORLD_ID, player.id, 0, len(data)) + data))
            return

        data = text.replace("<", "＜").replace(">", "＞").encode("utf-16-le")
        self.broadcast(BATTLE_CHAT, struct.pack("<HIHH", WORLD_ID, player.id, 0, len(data)) + data)

    def start_reload(self, player, now):
        if player.slot >= MELEE_SLOT or player.reload_at or now < player.equip_ready_at:
            return
        weapon = player.guns[player.slot]
        if weapon.ammo < weapon.gun.magazine:
            player.reload_at = now + weapon.gun.reload_time
            self.broadcast(RELOAD, struct.pack("<I", player.id))

    def cancel_reload(self, player):
        if player.reload_at:
            player.reload_at = 0.0
            self.broadcast(STOP_RELOAD, struct.pack("<I", player.id))

    def unmount(self, player):
        vehicle = player.vehicle
        if vehicle.driver == player.id:
            vehicle.controls.clear()
            vehicle.contacts.clear()
        seat = next(seat for seat, occupant in vehicle.occupants.items() if occupant == player.id)
        del vehicle.occupants[seat]
        player.vehicle = None
        player.status = (vehicle.x, 15.0, vehicle.z, vehicle.yaw, 0.0)
        self.broadcast(UNMOUNT, struct.pack("<I", player.id))
        print(f"vehicle {vehicle.id}: player {player.id} unmounted at {vehicle.x:.2f}, {vehicle.z:.2f}", flush=True)

    def report_hit(self, shooter, bullet, target_id, now):
        record = self.live_bullets.get(bullet)
        if record is None or record[0] != shooter.id or target_id == shooter.id:
            return
        _, gun, expires = self.live_bullets.pop(bullet)
        if now >= expires:
            return
        damage = GUNS[gun - 1].damage
        vehicle = self.vehicles.get(target_id)
        if vehicle is not None:
            packet = struct.pack("<IIIf", shooter.id, bullet, vehicle.id, damage)
            self.damage_vehicle(shooter, vehicle, damage, BULLET_HIT_VEHICLE, packet, now)
            return
        target = self.players.get(target_id)
        if not self.can_damage(target, now):
            return
        self.broadcast(BULLET_HIT_UNIT, struct.pack(
            "<5I4fI2f", shooter.id, bullet, gun, 0, target.id, target.health,
            0.0, 1.5, 0.0, 0, damage, 0.0,
        ))
        self.apply_damage(shooter, target, damage, now)

    @staticmethod
    def can_damage(target, now):
        return (target is not None and target.battle is not None and not target.battle.is_closing()
                and not target.revive_at and now >= target.protected_until)

    def apply_damage(self, shooter, target, damage, now):
        if shooter is target or damage <= 0:
            return
        target.health = max(0.0, target.health - damage)
        print(f"hit: player {shooter.id} -> {target.id}, health {target.health:g}", flush=True)
        if not target.health:
            self.knock_down(target, now)

    def knock_down(self, player, now):
        self.set_scope(player, False)
        player.health = 0.0
        player.revive_at = now + DOWN_TIME
        player.firing = False
        self.cancel_reload(player)
        self.cancel_item(player)
        if player.vehicle is not None:
            self.unmount(player)
        self.drop_guns(player)
        print(f"down: player {player.id}", flush=True)

    def revive(self, player, now):
        player.health = MAX_HEALTH
        player.revive_at = 0.0
        player.protected_until = now + SPAWN_PROTECTION
        player.reset_weapons(choice(tuple(gun for gun in PRIMARY_GUNS if gun != player.primary)))
        self.broadcast(AUTO_REVIVE, struct.pack("<If", player.id, player.health) + player.loadout())
        print(f"revive: player {player.id}, gun {player.primary.kind}", flush=True)

    def fire(self, player, now):
        if now < player.equip_ready_at:
            return
        if player.slot == MELEE_SLOT:
            self.swing(player, now)
            return
        if player.slot > MELEE_SLOT:
            return
        weapon = player.guns[player.slot]
        gun = weapon.gun
        tuning = SNIPERS.get(gun.kind)
        if tuning and tuning.single_shot and player.fired_during_operation:
            return
        if player.reload_at or now < weapon.next_shot:
            return
        if not weapon.ammo:
            self.start_reload(player, now)
            return
        x, y, z, yaw, aim = player.status
        scoped = player.scoped and tuning is not None
        if tuning:
            minimum = tuning.scoped_minimum if scoped else tuning.minimum
            weapon.recover_drift(now, minimum, tuning)
            # APK TryToFire rolls a cone limit between minimum and current drift.
            cone = uniform(minimum, weapon.drift)
            weapon.drift = min(tuning.maximum, weapon.drift + tuning.drift_per_shot)
            player.fired_during_operation = True
        weapon.ammo -= 1
        weapon.next_shot = now + gun.interval
        origin = (x, y + 2.0, z)
        bullets = bytearray(struct.pack("<IH", player.id, gun.pellets))
        for _ in range(gun.pellets):
            self.bullet_id += 1
            # Every sniper bullet uses the white sniper tracer, scoped or not.
            bullet_id = self.bullet_id | SCOPED_BULLET if tuning else self.bullet_id
            if tuning:
                # APK Unit.FireBullet rotates the cone offset around the forward axis.
                offset = math.tan(math.radians(uniform(0, cone)))
                roll = math.radians(uniform(0, 360))
                lateral = -math.sin(roll) * offset
                vertical = math.cos(roll) * offset
                angle = math.radians(yaw)
                length = math.hypot(1, offset)
                dx = (math.sin(angle) + math.cos(angle) * lateral) / length
                dy = vertical / length
                dz = (math.cos(angle) - math.sin(angle) * lateral) / length
            else:
                angle = math.radians(yaw + uniform(-gun.spread, gun.spread))
                dx, dy, dz = math.sin(angle), 0.0, math.cos(angle)
            bullets.extend(struct.pack("<I6f", bullet_id, *origin, dx, dy, dz))
            if gun.kind == 11:
                distance = max(1.0, min(40.0, aim))
                # Stop at the first unit or obstacle along the flight path.
                for target in self.joined:
                    if target is player or target.revive_at:
                        continue
                    tx, ty, tz = target.status[:3]
                    along = (tx - x) * dx + (tz - z) * dz
                    if 0 < along < distance and abs(ty - y) < 2 and abs((tx - x) * dz - (tz - z) * dx) < 1.1:
                        distance = along
                for vehicle in self.vehicles.values():
                    collision = vehicle.ray_hit(x, z, dx, dz, distance)
                    if collision is not None:
                        distance = collision
                rocket_obstacles = self.obstacles + [bounds for _, bounds in self.movable_props.values()]
                rocket_obstacles += [bounds for prop_id, (_, bounds) in self.blast_doors.items()
                                     if self.blast_doorway[prop_id] not in self.open_doors]
                distance = self.clear_distance(x, z, dx, dz, distance, rocket_obstacles)
                impact = (x + dx * distance, y + 2.0, z + dz * distance)
                self.rockets.append((now + distance / 26.0, player, self.bullet_id, impact))
            else:
                self.live_bullets[bullet_id] = (player.id, gun.kind, now + 3.0)
        self.broadcast(FIRE_BULLETS, bullets)
        self.broadcast(GUN_AMMO, struct.pack("<II", player.weapon_id(player.slot), weapon.ammo))
        print(f"fire: player {player.id}, gun {gun.kind}, ammo {weapon.ammo}", flush=True)
        if not weapon.ammo:
            self.start_reload(player, now)

    def swing(self, player, now):
        if now < player.next_swing:
            return
        player.next_swing = now + MACHETE_COOLDOWN
        self.broadcast(MELEE_SWING, struct.pack("<I", player.id))

        x, y, z, yaw, _ = player.status
        angle = math.radians(yaw)
        forward_x, forward_z = math.sin(angle), math.cos(angle)
        for target in self.joined:
            if target is player or target.vehicle is not None or not self.can_damage(target, now):
                continue
            tx, ty, tz = target.status[:3]
            dx, dz = tx - x, tz - z
            distance = math.hypot(dx, dz)
            if abs(ty - y) > 2.0 or distance > MACHETE_RANGE:
                continue
            if dx * forward_x + dz * forward_z < distance * MACHETE_CONE:
                continue
            if self.melee_blocked(x, z, dx, dz):
                continue
            skin = player.skins.get((player.character, 0, MACHETE_KIND), 1)
            self.broadcast(MELEE_HIT_UNIT, struct.pack(
                "<5I2f", player.id, player.weapon_id(MELEE_SLOT), MACHETE_KIND,
                skin, target.id, target.health, MACHETE_DAMAGE,
            ))
            self.apply_damage(player, target, MACHETE_DAMAGE, now)

        for vehicle in self.vehicles.values():
            distance = vehicle.ray_hit(x, z, forward_x, forward_z, MACHETE_RANGE)
            if (distance is not None and abs(y - 15.0) <= 2
                    and not self.melee_blocked(x, z, forward_x * distance, forward_z * distance)):
                packet = struct.pack("<IIIf", player.id, 0, vehicle.id, MACHETE_DAMAGE)
                self.damage_vehicle(player, vehicle, MACHETE_DAMAGE, BULLET_HIT_VEHICLE, packet, now)
                break

    def melee_blocked(self, x, z, dx, dz):
        # The server has the map's static obstacle bounds. Ignore near-endpoint
        # intersections so touching the edge of a prop does not swallow a swing.
        for left, bottom, right, top in self.obstacles:
            near, far = 0.05, 0.95
            for origin, step, low, high in ((x, dx, left, right), (z, dz, bottom, top)):
                if not step:
                    if not low < origin < high:
                        break
                    continue
                entry, exit = sorted(((low - origin) / step, (high - origin) / step))
                near, far = max(near, entry), min(far, exit)
                if near >= far:
                    break
            else:
                return True
        return False

    async def update(self):
        while True:
            await asyncio.sleep(TICK)
            now = time.monotonic()
            if not self.started:
                if self.queued and now >= self.loading_deadline:
                    self.finish_round("loading timed out")
                continue
            if self.round_started and now - self.round_started >= ROUND_TIME:
                if not self.round_ending:
                    self.round_ending = True
                    self.broadcast(BATTLE_END, b"")
                    print(f"round {self.battle_id} time limit; result shown", flush=True)
                if now - self.round_started >= ROUND_TIME + ROUND_END_DELAY:
                    self.broadcast(BATTLE_CLOSE, b"")
                    self.finish_round("5 minute time limit")
                    continue
            for player in list(self.joined):
                if player.disconnected_at and now - player.disconnected_at >= RECONNECT_GRACE:
                    if player.vehicle:
                        self.unmount(player)
                    self.player_left(player)
            if not self.joined:
                self.finish_round("all players left")
                continue
            for player in self.joined:
                if player.battle is None or player.battle.is_closing():
                    continue
                if player.revive_at and now >= player.revive_at:
                    self.revive(player, now)
                if player.reload_at and now >= player.reload_at:
                    weapon = player.guns[player.slot]
                    gun = weapon.gun
                    amount = gun.magazine - weapon.ammo
                    weapon.ammo = gun.magazine
                    player.reload_at = 0.0
                    self.broadcast(RELOADED, struct.pack("<III", player.id, amount, weapon.ammo))
                    print(f"reload: player {player.id}, ammo {weapon.ammo}", flush=True)
                if player.firing:
                    self.fire(player, now)
                self.update_item(player, now)

            while self.live_bullets:
                oldest = next(iter(self.live_bullets))
                if self.live_bullets[oldest][2] > now:
                    break
                self.live_bullets.pop(oldest)

            pending = []
            for when, owner, projectile, impact in self.rockets:
                if when <= now:
                    self.broadcast(BULLET_EXPLODE, struct.pack("<I3f", projectile, *impact))
                    self.blast(owner, projectile, impact, 7.2, GUNS[10].damage, BULLET_EXPLOSION_HIT_UNIT, now)
                    self.rocket_hit_props(projectile, impact, 7.2, GUNS[10].damage)
                else:
                    pending.append((when, owner, projectile, impact))
            self.rockets = pending
            self.update_grenades(now)

            updates = []
            for vehicle in self.vehicles.values():
                previous_x, previous_z, previous_speed = vehicle.x, vehicle.z, vehicle.speed
                if vehicle.step(self.obstacles, self.vehicles.values()) or previous_speed:
                    updates.append(struct.pack("<I", vehicle.id) + vehicle.status())
                self.vehicle_impacts(vehicle, previous_x, previous_z, now)
            if updates:
                self.broadcast(VEHICLES_STATUS, struct.pack("<H", len(updates)) + b"".join(updates))

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        if len(data) not in (18, 38):
            return
        size, message, battle_id, world_id, player_id, ticket = struct.unpack_from("<HHIHII", data)
        player = self.players.get(player_id)
        if (size != len(data) or (battle_id, world_id) != (self.battle_id, WORLD_ID)
                or player is None or player.battle is None or ticket != player_id):
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


class LanDiscovery(asyncio.DatagramProtocol):
    def __init__(self, host):
        self.host = host

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        if data == b"MBG-LAN-DISCOVER-1":
            self.transport.sendto(b"MBG-LAN-ROOM-1 " + self.host.encode("ascii"), addr)


async def main(host, bind, size, stock_vehicles=False, share=False, owner_profile=None,
               admin_port=8080, admin_token=None):
    root = BASE_DIR
    match = Match(host, size, stock_vehicles, owner_profile)
    async with (
        await asyncio.start_server(handler(match.login), bind, LOGIN_PORT),
        await asyncio.start_server(handler(match.world), bind, WORLD_PORT),
        await asyncio.start_server(handler(match.battle), bind, BATTLE_PORT),
    ):
        udp, _ = await asyncio.get_running_loop().create_datagram_endpoint(
            lambda: match, local_addr=(bind, BATTLE_UDP_PORT)
        )
        admin = None
        admin_server = None
        if admin_port:
            token = admin_token or os.environ.get("MBG_ADMIN_TOKEN")
            token_file = root / "admin-token"
            if not token:
                try:
                    token = token_file.read_text(encoding="ascii").strip()
                except OSError:
                    token = ""
            if not token:
                token = token_hex(16)
                token_file.write_text(token + "\n", encoding="ascii")
                try:
                    token_file.chmod(0o600)
                except OSError:
                    pass
            from admin_server import AdminServer
            admin = AdminServer(match, admin_port, token, root)
            try:
                admin_server = await admin.start()
                print(f"Admin/update UI on 0.0.0.0:{admin_port}", flush=True)
            except OSError as error:
                print(f"Admin UI unavailable on {admin_port}: {error}", flush=True)
        discovery = None
        try:
            if bind != "0.0.0.0" and not ipaddress.IPv4Address(bind).is_loopback:
                try:
                    discovery, _ = await asyncio.get_running_loop().create_datagram_endpoint(
                        lambda: LanDiscovery(host), local_addr=("0.0.0.0", DISCOVERY_PORT)
                    )
                except OSError as error:
                    print(f"LAN discovery unavailable; players can enter {host} manually: {error}", flush=True)
            print(f"Listeners ready on {bind} -> {host} for {size} test player(s)", flush=True)
            if share:
                from lan_share import share_client
            with share_client(host) if share else nullcontext():
                await match.update()
        finally:
            if discovery is not None:
                discovery.close()
            if admin_server is not None:
                admin_server.close()
                await admin_server.wait_closed()
            udp.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--players", choices=range(1, MAX_PLAYERS + 1), default=MAX_PLAYERS, type=int, help="room capacity")
    parser.add_argument("--host", type=ipaddress.IPv4Address, help="IPv4 address advertised to players (LAN or public)")
    parser.add_argument("--bind", type=ipaddress.IPv4Address, help="local IPv4 to bind (default: --host, or 0.0.0.0 for a public host)")
    parser.add_argument("--share-client", action="store_true", help="prepare a LAN download and open the host client")
    parser.add_argument("--stock-vehicles", action="store_true", help="Use the three stock vehicle appearances")
    parser.add_argument("--owner-profile", help="profile name that always owns the room (for example slot-1)")
    parser.add_argument("--admin-port", type=int, default=8080, help="token-protected admin/update UI port (0 disables)")
    parser.add_argument("--admin-token", help="admin token; defaults to MBG_ADMIN_TOKEN or admin-token file")
    args = parser.parse_args()
    if args.host is None:
        if args.share_client:
            from lan_share import default_lan_host
            try:
                args.host = ipaddress.IPv4Address(default_lan_host())
            except OSError:
                parser.error("could not select a LAN address automatically; specify --host")
        else:
            args.host = ipaddress.IPv4Address(HOST)
    if args.host.is_unspecified or args.host.is_link_local:
        parser.error("--host must be a usable IPv4 address")
    if args.bind is None:
        args.bind = ipaddress.IPv4Address("0.0.0.0") if not (args.host.is_private or args.host.is_loopback) else args.host
    if args.bind.is_multicast or args.bind.is_link_local:
        parser.error("--bind must be a local IPv4 address or 0.0.0.0")
    if args.share_client and not (args.host.is_private or args.host.is_loopback):
        parser.error("--share-client is for LAN hosts; use a public client prepared separately")
    try:
        asyncio.run(main(str(args.host), str(args.bind), args.players, args.stock_vehicles, args.share_client,
                          args.owner_profile, args.admin_port, args.admin_token))
    except KeyboardInterrupt:
        pass





