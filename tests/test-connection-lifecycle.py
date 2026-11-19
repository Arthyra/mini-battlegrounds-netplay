"""Deterministic battle-writer takeover regression using the real entry point."""
import asyncio
import struct
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import sys
from pathlib import Path

# Tests live under tests/ while the application entry point stays at the root.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import server


class Writer:
    def __init__(self):
        self.closed = False

    def close(self):
        self.closed = True

    def is_closing(self):
        return self.closed

    def write(self, payload):
        self.sent = getattr(self, "sent", []) + [payload]

    async def drain(self):
        return None

    def get_extra_info(self, name):
        return ("127.0.0.1", 1)


class ConnectionLifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_login_counts_members_above_a_reduced_room_cap(self):
        match = server.Match.__new__(server.Match)
        match.host = "127.0.0.1"
        match.slots = 4
        match.size = 2
        match.queued = []
        match.joined = []
        match.saved_profiles = {}
        match.skin_catalog = {}
        match.players = {
            slot: SimpleNamespace(id=slot, profile_name=f"slot-{slot}", world=Writer())
            for slot in (1, 4)
        }

        async def login(writer):
            reader = asyncio.StreamReader()
            name = f"new-player|{server.LAN_VERSION}".encode("utf-16le")
            reader.feed_data(server.frame(20, struct.pack("<IH", 0, len(name)) + name))
            reader.feed_eof()
            await match.login(reader, writer)

        rejected = Writer()
        with self.assertRaisesRegex(ValueError, "room is full"):
            await login(rejected)
        self.assertEqual(set(match.players), {1, 4})

        del match.players[4]
        accepted = Writer()
        await login(accepted)
        self.assertEqual(match.players[2].profile_name, "new-player")
        _, message, error, player_id = struct.unpack_from("<HHHI", accepted.sent[0])
        self.assertEqual((message, error, player_id), (20, 0, 2))

    async def test_stale_writer_cannot_cancel_new_udp_warning(self):
        match = server.Match.__new__(server.Match)
        player = SimpleNamespace(id=1, battle=None, udp=None, disconnected_at=0,
                                 ready=True, udp_warning=None)
        match.players = {1: player}
        match.queued = [player]
        match.joined = [player]
        match.battle_id = 7
        match.started = True
        old_writer, new_writer = Writer(), Writer()
        old_entered = asyncio.Event()
        new_entered = asyncio.Event()
        release_old = asyncio.Event()
        release_new = asyncio.Event()

        async def receive(reader):
            return 34, struct.pack("<IHI", 7, server.WORLD_ID, 1)

        async def run(player, reader, writer, battle_id, reconnect):
            if writer is old_writer:
                old_entered.set()
                await release_old.wait()
            else:
                player.udp_warning = asyncio.create_task(asyncio.Event().wait())
                new_entered.set()
                await release_new.wait()

        match.run_battle = run
        with patch.object(server, "receive", receive):
            old = asyncio.create_task(match.battle(None, old_writer))
            await old_entered.wait()
            new = asyncio.create_task(match.battle(None, new_writer))
            await new_entered.wait()
            warning = player.udp_warning
            try:
                release_old.set()
                await old
                await asyncio.sleep(0)
                self.assertIs(player.battle, new_writer)
                self.assertIs(player.udp_warning, warning)
                self.assertFalse(warning.cancelled())
            finally:
                warning.cancel()
                new.cancel()
                await asyncio.gather(warning, new, return_exceptions=True)

    async def test_silent_lobby_times_out_and_owner_transfers(self):
        # A silently-dead lobby client (no FIN/RST) used to pin its room slot
        # forever, and if it was the owner every future room start was
        # blocked. Drive the real Match.world entry: the first receive
        # returns the world-login frame, then the connection goes silent, so
        # the lobby loop must time out (LOBBY_SILENCE_TIMEOUT) and ownership
        # must move to the surviving member.
        match = server.Match.__new__(server.Match)
        owner = SimpleNamespace(id=1, world=None, battle=None, udp=None,
                                room_ready=True, nickname="", character=1,
                                skins={}, profile_name="slot-1")
        member = SimpleNamespace(id=2, world=None, battle=None, udp=None,
                                 room_ready=False, nickname="", character=1,
                                 skins={}, profile_name="slot-2")
        match.players = {1: owner, 2: member}
        match.queued = []
        match.joined = []
        match.battle_id = 1
        match.started = False
        match.owner = owner
        match.owner_profile = None
        match.skin_inventory = b""
        match.skin_applies = lambda player: b""
        match.size = 8
        match.room_state_sent = []
        match.send_room_state = lambda: match.room_state_sent.append(1)
        match.save_profile = lambda player: None

        owner.world_ticket = 77
        owner.profile = lambda now: b""
        member.world = Writer()
        dead_writer = Writer()

        async def receive(reader):
            # first call: the world-login frame; afterwards the link dies
            # silently (never yields again)
            if not receive.logged_in:
                receive.logged_in = True
                return 23, struct.pack("<II", owner.id, 77)
            await asyncio.Event().wait()
        receive.logged_in = False

        real_timeout = server.LOBBY_SILENCE_TIMEOUT
        server.LOBBY_SILENCE_TIMEOUT = 0.05
        try:
            with patch.object(server, "receive", receive):
                await asyncio.wait_for(match.world(None, dead_writer), timeout=2)
        finally:
            server.LOBBY_SILENCE_TIMEOUT = real_timeout
        await asyncio.sleep(0)
        # release_lobby_slot ran on the timed-out connection: the dead
        # owner is removed and ownership moved to the surviving member.
        self.assertIs(match.owner, member)
        self.assertNotIn(owner.id, match.players)
        self.assertIsNone(owner.world)
        self.assertTrue(match.room_state_sent)

    async def test_lively_lobby_is_not_disconnected(self):
        # The client pings (message 1) every 2s on the lobby link; the
        # silence timeout must never fire while pings keep arriving.
        match = server.Match.__new__(server.Match)
        owner = SimpleNamespace(id=1, world=None, battle=None, udp=None,
                                room_ready=True, nickname="", character=1,
                                skins={}, profile_name="slot-1")
        match.players = {1: owner}
        match.queued = []
        match.joined = []
        match.battle_id = 1
        match.started = False
        match.owner = owner
        match.owner_profile = None
        match.skin_inventory = b""
        match.skin_applies = lambda player: b""
        match.size = 8
        match.room_state_sent = []
        match.send_room_state = lambda: match.room_state_sent.append(1)
        match.release_lobby_slot = lambda player: None
        match.save_profile = lambda player: None

        owner.world_ticket = 77
        owner.profile = lambda now: b""
        writer = Writer()

        async def receive(reader):
            if not receive.logged_in:
                receive.logged_in = True
                return 23, struct.pack("<II", owner.id, 77)
            await asyncio.sleep(0.4)  # pings well inside the timeout
            return 1, struct.pack("<f", 0.0)
        receive.logged_in = False

        real_timeout = server.LOBBY_SILENCE_TIMEOUT
        server.LOBBY_SILENCE_TIMEOUT = 2.0
        try:
            with patch.object(server, "receive", receive):
                task = asyncio.create_task(match.world(None, writer))
                try:
                    await asyncio.sleep(8.0)  # 4x the timeout, pings keep coming
                    self.assertFalse(task.done())
                    self.assertIs(match.owner, owner)
                finally:
                    writer.close()
                    task.cancel()
                    await asyncio.gather(task, return_exceptions=True)
        finally:
            server.LOBBY_SILENCE_TIMEOUT = real_timeout
    async def test_current_writer_cleans_its_udp_warning(self):
        match = server.Match.__new__(server.Match)
        warning = asyncio.create_task(asyncio.Event().wait())
        player = SimpleNamespace(id=1, battle=None, udp=None, disconnected_at=0,
                                 ready=True, udp_warning=warning, vehicle=None)
        match.players = {1: player}
        match.queued = [player]
        match.joined = [player]
        match.battle_id = 7
        match.started = True

        async def receive(reader):
            return 34, struct.pack("<IHI", 7, server.WORLD_ID, 1)

        async def run(*args):
            return

        match.run_battle = run
        with patch.object(server, "receive", receive):
            await match.battle(None, Writer())
        await asyncio.gather(warning, return_exceptions=True)
        self.assertIsNone(player.battle)
        self.assertIsNone(player.udp_warning)
        self.assertTrue(warning.cancelled())
        self.assertGreater(player.disconnected_at, 0)


if __name__ == "__main__":
    unittest.main()
