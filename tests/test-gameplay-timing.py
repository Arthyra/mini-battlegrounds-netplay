import importlib.util
from pathlib import Path
import struct
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('fixtures', Path(__file__).with_name('test-room-and-explosion.py'))
fixtures = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixtures)
s = fixtures.server

class GameplayTimingTests(unittest.TestCase):
    def test_all_vehicle_variants_reach_increased_limits(self):
        for kind in range(1, 7):
            old = {1: (16, 7.5), 2: (9, 4), 3: (11, 5)}[(kind - 1) % 3 + 1]
            for control, expected in ((105, old[0] * 1.3), (107, -old[1] * 1.3)):
                with self.subTest(kind=kind, control=control):
                    car = s.Vehicle(101, kind, 0, 0, 0)
                    car.controls.add(control)
                    for _ in range(60):
                        car.x = car.z = 0
                        car.step([], [])
                    self.assertAlmostEqual(car.speed, expected)

    def test_quick_click_and_held_throw(self):
        for release, expected in ((100.02, 100.25), (101, 101.15)):
            with self.subTest(release=release):
                match, player = fixtures.fixture()
                player.slot = s.THROWABLE_SLOT
                payload = struct.pack('<5f', *player.status)
                with patch.object(s.time, 'monotonic', return_value=100):
                    match.battle_input(player, s.OPERATE, payload)
                with patch.object(s.time, 'monotonic', return_value=release):
                    match.battle_input(player, s.STOP_OPERATE, payload)
                self.assertAlmostEqual(player.throw_at, expected)
                thrown = []
                match.throw_grenade = lambda p, now: thrown.append(now)
                match.switch = lambda *args: None
                match.update_item(player, expected - .001)
                self.assertEqual(thrown, [])
                match.update_item(player, expected + .001)
                match.update_item(player, expected + .1)
                self.assertEqual(len(thrown), 1)
                self.assertEqual(player.throw_release_at, 0)

if __name__ == '__main__':
    unittest.main()
