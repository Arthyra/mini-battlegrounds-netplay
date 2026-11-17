"""Reference semantics for the client's UDP probe statistics (InstallUdpStats).

The real implementation is hand-written IL inside the patched client, which
no out-of-process test can execute. This file pins the DESIGN the IL
implements as an executable reference, one method per IL block:

  on_round_loaded   -> HandleBattleLoaded insert
  on_ack(data)      -> LanUdpAck
  on_tick(now)      -> LanUdpTick (per frame)
  on_ping_sent()    -> LanUdpPing tail

Every test below mirrors one IL property; the IL was additionally verified
structurally ( Cecil probe: UpdatePing injects the probe, LanUdpTick never
sends, ack carries the lastEcho watermark, HandleBattleLoaded resets it)
and the HUD readout is checked against injected loss during the Unity GUI
acceptance. If this reference and the IL ever diverge, fix the IL.

Semantics summary:
- sent/recv are per-round totals; loss% = clamp(dSent - dRecv, 0) * 100 /
  max(dSent, 1) over a 10s tumbling window defined by baseline snapshots
  (never mid-flight zeroing), recomputed EVERY tick so a total blackout
  reaches 100% within ~one window tick without any ACK.
- ACK watermark: an echo not strictly newer than the last processed echo is
  a duplicate, a reordered-old echo, or a pre-round leftover - ignored
  entirely (dups can never raise recv).
- RTT only moves for echoes 0..30s old; NaN echoes compare unordered and
  are ignored.
"""
import struct
import unittest


class UdpStatsReference:
    """Python mirror of the patched client's static fields/logic."""

    WINDOW = 10.0
    RTT_MAX = 30.0

    def __init__(self, now=0.0):
        self.on_round_loaded(now)

    def on_round_loaded(self, now):
        self.ping_enabled = True
        self.sent = self.recv = self.loss = 0
        self.rtt = 0.0
        self.win_start = now
        self.win_sent_base = self.win_recv_base = 0
        self.last_echo = now  # pre-round echoes are never strictly newer

    def on_ping_sent(self):
        self.sent += 1

    def on_tick(self, now):
        if not self.ping_enabled:
            return
        if now - self.win_start >= self.WINDOW:
            self.win_start = now
            self.win_sent_base = self.sent
            self.win_recv_base = self.recv
        d_sent = self.sent - self.win_sent_base
        if d_sent == 0:
            return  # no probes this window: hold the last known loss
        d_recv = self.recv - self.win_recv_base
        diff = max(d_sent - d_recv, 0)
        self.loss = diff * 100 // d_sent

    def on_ack(self, data, now):
        message = struct.unpack_from("<H", data, 2)[0]
        if message != 209:
            return
        echo = struct.unpack_from("<f", data, 18)[0]
        if not (echo > self.last_echo):  # <= or NaN -> ignore
            return
        self.last_echo = echo
        rtt_val = now - echo
        if 0.0 <= rtt_val <= self.RTT_MAX:
            self.rtt = rtt_val
        self.recv += 1


def ping_echo(sent_at, message=209):
    # the wire payload the client's ack reads: message at 2, echo at 18
    return struct.pack("<HHIHII", 22, message, 1, 1, 1, 1) + struct.pack("<f", sent_at)


class UdpStatsSemanticsTests(unittest.TestCase):
    def setUp(self):
        self.s = UdpStatsReference(now=100.0)
        self.s.on_tick(100.0)  # baseline snapshot at t0

    def test_loss_climbs_without_acks(self):
        # five probes over 10s, all lost: no ACK ever arrives, yet the HUD
        # loss reaches 100% on the very first tick after the first lost
        # probe and HOLDS at 100% after the probes stop (no flicker to 0)
        for i in range(5):
            self.s.on_ping_sent()
            self.s.on_tick(100.0 + 2.0 * (i + 1))
        self.assertEqual(self.s.loss, 100)
        self.s.on_tick(112.0)  # window rolled at 110; no new probes since
        self.assertEqual(self.s.loss, 100)  # hold: last known state

    def test_all_acked_keeps_loss_zero(self):
        t = 100.0
        for i in range(5):
            t += 2.0
            self.s.on_ping_sent()
            self.s.on_ack(ping_echo(t - 0.05), now=t)
            self.s.on_tick(t)
        self.assertEqual(self.s.loss, 0)
        self.assertEqual(self.s.recv, 5)
        self.assertGreater(self.s.rtt, 0.0)

    def test_duplicate_ack_never_raises_recv(self):
        self.s.on_ping_sent()
        self.s.on_ack(ping_echo(100.0), now=100.5)
        recv_after_first = self.s.recv
        self.s.on_ack(ping_echo(100.0), now=100.6)  # duplicate echo
        self.s.on_ack(ping_echo(99.0), now=100.7)   # reordered-old echo
        self.assertEqual(self.s.recv, recv_after_first)

    def test_reordered_ack_does_not_count_but_later_one_does(self):
        self.s.on_ping_sent()  # probe A at t=100
        self.s.on_ping_sent()  # probe B at t=101
        self.s.on_ack(ping_echo(101.0), now=101.4)  # B confirmed first
        self.assertEqual(self.s.recv, 1)
        self.s.on_ack(ping_echo(100.0), now=101.5)  # A's echo arrives late
        self.assertEqual(self.s.recv, 1)            # stale watermark: ignored
        self.s.on_ping_sent()  # probe C
        self.s.on_ack(ping_echo(102.0), now=102.3)
        self.assertEqual(self.s.recv, 2)

    def test_nan_echo_ignored(self):
        nan = struct.unpack("<f", struct.pack("<I", 0x7FC00000))[0]
        self.s.on_ping_sent()
        self.s.on_ack(ping_echo(nan), now=101.0)
        self.assertEqual(self.s.recv, 0)
        self.assertEqual(self.s.rtt, 0.0)

    def test_rtt_gate_30s(self):
        self.s.on_ping_sent()  # probe sent 0.2s after round load
        self.s.on_ack(ping_echo(100.2), now=131.0)  # ~30.8s old -> stale
        self.assertEqual(self.s.rtt, 0.0)
        self.assertEqual(self.s.recv, 1)            # delivery still counts
        self.s.on_ping_sent()
        self.s.on_ack(ping_echo(131.5), now=131.9)  # fresh echo
        self.assertAlmostEqual(self.s.rtt, 0.4)

    def test_non_209_frames_ignored(self):
        self.s.on_ping_sent()
        self.s.on_ack(ping_echo(100.0, message=52), now=100.5)
        self.assertEqual(self.s.recv, 0)

    def test_window_rollover_is_baseline_snapshot(self):
        # probes sent and confirmed before the rollover must not leak into
        # the next window's loss
        t = 100.0
        for _ in range(4):
            t += 2.0
            self.s.on_ping_sent()
            self.s.on_ack(ping_echo(t - 0.05), now=t)
            self.s.on_tick(t)
        self.s.on_tick(110.5)  # rollover happened at ~110: clean baselines
        self.assertEqual(self.s.loss, 0)

    def test_late_ack_lands_in_current_window(self):
        # a probe sent at t=109, confirmed at t=111 (after the 110 rollover):
        # window 0 closed with it unconfirmed (loss held at 100), the late
        # delivery counts in window 1, and the next CONFIRMED probe makes
        # the readout recover
        self.s.on_ping_sent()
        self.s.on_tick(109.0)
        self.assertEqual(self.s.loss, 100)
        self.s.on_tick(111.0)  # rollover: window 1 baseline includes it
        self.s.on_ack(ping_echo(109.0), now=111.2)
        self.s.on_tick(111.3)
        self.assertEqual(self.s.loss, 100)  # held: no new probes in window 1
        self.s.on_ping_sent()
        self.s.on_tick(111.5)
        self.s.on_ack(ping_echo(111.4), now=111.6)
        self.s.on_tick(111.7)
        self.assertEqual(self.s.loss, 0)    # confirmed probe: clean readout

    def test_round_reset_blocks_previous_round_acks(self):
        self.s.on_ping_sent()
        t = 100.0
        self.s.on_round_loaded(now=120.0)  # new round pins lastEcho to 120
        self.s.on_tick(120.0)
        self.s.on_ack(ping_echo(100.0), now=120.5)  # previous-round echo
        self.assertEqual(self.s.recv, 0)
        self.assertEqual(self.s.loss, 0)
        self.s.on_ping_sent()
        self.s.on_ack(ping_echo(120.5), now=120.9)
        self.assertEqual(self.s.recv, 1)

    def test_division_guard_zero_sent(self):
        # no probes this window: loss must read 0 without touching divide
        self.s.on_tick(120.0)
        self.assertEqual(self.s.loss, 0)


if __name__ == "__main__":
    unittest.main()
