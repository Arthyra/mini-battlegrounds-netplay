#!/usr/bin/env python3
"""Build the Windows player ZIP, write a manifest, and optionally publish it."""
import importlib.util
import argparse
import hashlib
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
_spec = importlib.util.spec_from_file_location("build_friend_zip", ROOT / "build-friend-zip.py")
_module = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_module)
build = _module.build

DEFAULT_CLIENT = ROOT / "_local-archive" / "public-client-ecs"
DEFAULT_OUTPUT = ROOT / "dist"
DEFAULT_HOST = "198.51.100.10"
DEFAULT_REPO = "Ash-Rise/mini-battlegrounds-lan"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def git_commit():
    try:
        return subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    except (OSError, subprocess.CalledProcessError):
        return ""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="release version, for example 0.4")
    parser.add_argument("--client", type=Path, default=DEFAULT_CLIENT, help="prepared public client directory")
    parser.add_argument("--host", default=DEFAULT_HOST, help="address written to server.txt")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--notes", type=Path, help="release notes Markdown file")
    parser.add_argument("--publish", action="store_true", help="create the GitHub release with gh")
    parser.add_argument("--repo", default=DEFAULT_REPO)
    args = parser.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)
    archive = args.output / "Mini-Battlegrounds-LAN-Windows.zip"
    if archive.exists():
        archive.unlink()
    build(archive, args.client, args.host, args.version)

    manifest = {
        "version": args.version,
        "tag": f"lan-player-v{args.version}",
        "host": args.host,
        "client": str(args.client),
        "archive": archive.name,
        "bytes": archive.stat().st_size,
        "sha256": sha256(archive),
        "commit": git_commit(),
        "built_at": datetime.now(timezone.utc).isoformat(),
    }
    manifest_path = args.output / f"release-v{args.version}.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    update_dir = args.output / "update"
    update_dir.mkdir(parents=True, exist_ok=True)
    update_archive = update_dir / archive.name
    if update_archive.exists():
        update_archive.unlink()
    update_archive.write_bytes(archive.read_bytes())
    latest = {
        "version": args.version,
        "tag": manifest["tag"],
        "url": f"http://{args.host}:8080/update/{archive.name}",
        "bytes": manifest["bytes"],
        "sha256": manifest["sha256"],
    }
    (update_dir / "latest.json").write_text(json.dumps(latest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    print(json.dumps(latest, indent=2))

    if args.publish:
        notes = args.notes or ROOT / f"release-notes-v{args.version}.md"
        if not notes.is_file():
            parser.error(f"release notes not found: {notes}")
        subprocess.run([
            "gh", "release", "create", manifest["tag"], str(archive),
            "--repo", args.repo,
            "--title", f"Windows 玩家包 · 102 复活模式 v{args.version}",
            "--notes-file", str(notes),
            "--latest",
        ], cwd=ROOT, check=True)


if __name__ == "__main__":
    main()
