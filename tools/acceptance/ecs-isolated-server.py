"""Start an acceptance instance on 432xx, without production ports or profiles."""
import argparse
import asyncio
import os
import sys
from pathlib import Path


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--players", type=int, choices=range(2, 9), default=2)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(root))
    os.environ["MBG_PROFILE_PATH"] = str(root / "acceptance-profiles.json")
    import server

    server.LOGIN_PORT = 43220
    server.WORLD_PORT = 43223
    server.BATTLE_PORT = 43234
    server.BATTLE_UDP_PORT = 43235
    server.DISCOVERY_PORT = 43222
    asyncio.run(server.main(args.host, "0.0.0.0", args.players, admin_port=0))
