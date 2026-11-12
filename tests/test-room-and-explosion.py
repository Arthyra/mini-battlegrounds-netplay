"""Focused regressions for empty ECS rounds and owner explosion damage."""
import asyncio
from pathlib import Path
import struct
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import server


class Writer:
    def __init__(self, closing=False):
        self.closing = closing
        self.packets = []

    def is_closing(self):
        return self.closing

    def close(self):
        self.closing = True

    def write(self, packet):
        self.packets.append(packet)


def fixture():
    match = server.Match.__new__(server.Match)
    match.host, match.size = "127.0.0.1", 2
    match.battle_id, match.owner_profile, match.owner = 1, None, None
    match.vehicle_skin_offset = 0
    match.initial_movable_props, match.initial_blast_doors = {}, {}
    match.obstacles = []
    match.players = {}
    match.empty_since = 0
    match.reset_round_state()
    match.events = []
    match.broadcast = lambda message, payload: match.events.append((message, payload))
    player = server.Player(1, server.PRIMARY_GUNS[0])
    player.ready = True
    player.battle = Writer()
    player.protected_until = 0
    player.status = (0, 15, 0, 0, 0)
    match.players = {1: player}
    match.joined = match.queued = [player]
    return match, player


class ExplosionTests(unittest.TestCase):
    def test_vehicle_destroyer_is_hurt_without_self_kill_credit(self):
        match, player = fixture()
        vehicle = server.Vehicle(101, 1, 0, 0, 0)
        match.vehicles = {101: vehicle}
        match.damage_vehicle(player, vehicle, vehicle.health, server.BULLET_HIT_VEHICLE, b"", 100)
        self.assertEqual(player.health, 0)
        self.assertEqual(player.deaths, 1)
        self.assertEqual(player.kills, 0)
        self.assertTrue(player.revive_at)
        self.assertIn(server.VEHICLE_EXPLOSION_HIT_UNIT, [message for message, _ in match.events])

    def test_throwable_rocket_and_fire_can_hurt_owner(self):
        for message in (server.THROWABLE_HIT_UNIT, server.BULLET_EXPLOSION_HIT_UNIT, server.INCENDIARY_HIT_UNIT):
            with self.subTest(message=message):
                match, player = fixture()
                match.vehicles = {}
                match.blast(player, 17, (0, 16, 0), 7, 100, message, 100)
                self.assertEqual(player.health, server.MAX_HEALTH - 100)
                self.assertIn(message, [code for code, _ in match.events])

    def test_explosion_still_respects_spawn_protection_and_radius(self):
        for protected, x in ((True, 0), (False, 50)):
            match, player = fixture()
            match.vehicles = {}
            player.protected_until = 101 if protected else 0
            player.status = (x, 15, 0, 0, 0)
            match.blast(player, 17, (0, 16, 0), 7, 100, server.THROWABLE_HIT_UNIT, 100)
            self.assertEqual(player.health, server.MAX_HEALTH)

    def test_direct_bullet_self_hit_remains_rejected(self):
        match, player = fixture()
        match.live_bullets[17] = (player.id, 1, 101)
        match.report_hit(player, 17, player.id, 100)
        self.assertEqual(player.health, server.MAX_HEALTH)
        self.assertIn(17, match.live_bullets)


class TickDone(Exception):
    pass


class EmptyRoundTests(unittest.IsolatedAsyncioTestCase):
    async def tick(self, match):
        calls = 0

        async def sleep(_):
            nonlocal calls
            calls += 1
            if calls > 1:
                raise TickDone

        with patch.object(server.asyncio, "sleep", sleep), patch.object(server.time, "monotonic", return_value=100):
            with self.assertRaises(TickDone):
                await match.update()

    async def test_empty_loading_and_started_rounds_are_reclaimed_immediately(self):
        for started in (False, True):
            with self.subTest(started=started):
                match, player = fixture()
                match.started = started
                match.loading_deadline = 1000
                match.round_started = 99
                player.world = Writer(closing=True)
                player.battle = None
                player.disconnected_at = 99  # Still inside reconnect grace.
                match.rockets = [(1000, player, 1, (0, 16, 0))]
                await self.tick(match)
                self.assertEqual(match.battle_id, 2)
                self.assertFalse(match.queued)
                self.assertFalse(match.joined)
                self.assertFalse(match.players)
                self.assertFalse(match.rockets)
                self.assertIsNone(match.owner)

    async def test_live_lobby_connection_preserves_battle_reconnect_window(self):
        match, player = fixture()
        match.loading_deadline = 1000
        player.world = Writer()
        player.battle = None
        await self.tick(match)
        self.assertEqual(match.battle_id, 1)
        self.assertEqual(match.queued, [player])

    async def test_live_battle_connection_is_not_an_empty_room(self):
        match, player = fixture()
        match.loading_deadline = 1000
        player.world = None
        player.battle = Writer()
        await self.tick(match)
        self.assertEqual(match.battle_id, 1)


if __name__ == "__main__":
    unittest.main()
