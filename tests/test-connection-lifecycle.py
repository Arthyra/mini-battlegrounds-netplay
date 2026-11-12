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

    def get_extra_info(self, name):
        return ("127.0.0.1", 1)


class ConnectionLifecycleTests(unittest.IsolatedAsyncioTestCase):
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
