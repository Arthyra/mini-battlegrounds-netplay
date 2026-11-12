#!/usr/bin/env python3
"""Build the Windows player ZIP, write a manifest, and optionally publish it."""
import importlib.util
import argparse
import base64
import os
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
    parser.add_argument("--package-version", help="Numeric executable/update version when promoting a newer numbered candidate")
    parser.add_argument("--client", type=Path, default=DEFAULT_CLIENT, help="prepared public client directory")
    parser.add_argument("--host", default=DEFAULT_HOST, help="address written to server.txt")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--notes", type=Path, help="release notes Markdown file")
    parser.add_argument("--title", help="Release title, defaults to the version number")
    parser.add_argument("--publish", action="store_true", help="create the GitHub release with gh")
    parser.add_argument("--repo", default=DEFAULT_REPO)
    parser.add_argument("--signing-key", type=Path,
                        default=Path(os.environ["MBG_UPDATE_SIGNING_KEY"]) if os.environ.get("MBG_UPDATE_SIGNING_KEY") else None,
                        help="Pinned RSA private PEM for signing latest.json (never commit)")
    parser.add_argument("--update-base-url", help="Public update directory URL, preferably HTTPS")
    args = parser.parse_args()
    if args.signing_key is None or not args.signing_key.is_file():
        parser.error("Set --signing-key or MBG_UPDATE_SIGNING_KEY to the matching RSA private PEM.")

    args.output.mkdir(parents=True, exist_ok=True)
    archive = args.output / "Mini-Battlegrounds-LAN-Windows.zip"
    if archive.exists():
        archive.unlink()
    package_version = args.package_version or args.version
    build(archive, args.client, args.host, package_version)

    manifest = {
        "version": args.version,
        "package_version": package_version,
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
    base_url = (args.update_base_url or f"https://{args.host}/mbg-update").rstrip("/")
    if not base_url.lower().startswith("https://"):
        parser.error("New update download URLs must use HTTPS.")
    latest = {
        "version": package_version,
        "tag": manifest["tag"],
        "url": f"{base_url}/{archive.name}",
        "bytes": manifest["bytes"],
        "sha256": manifest["sha256"],
    }
    # Keep this exact canonical form in sync with lan-launcher.cs.
    canonical = ("MBG-LAN-UPDATE-1\n" + latest["version"] + "\n"
                 + latest["url"] + "\n" + latest["sha256"].lower() + "\n").encode("utf-8")
    signature = subprocess.run(
        ["openssl", "dgst", "-sha256", "-sign", str(args.signing_key)],
        input=canonical, stdout=subprocess.PIPE, check=True,
    ).stdout
    latest["signature"] = base64.b64encode(signature).decode("ascii")
    (update_dir / "latest.json").write_text(json.dumps(latest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    print(json.dumps(latest, indent=2))

    if args.publish:
        notes = args.notes or ROOT / "docs" / "releases" / f"v{args.version}" / "release-notes.md"
        if not notes.is_file():
            parser.error(f"release notes not found: {notes}")
        subprocess.run([
            "gh", "release", "create", manifest["tag"], str(archive),
            "--repo", args.repo,
            "--title", args.title or f"v{args.version}",
            "--notes-file", str(notes),
            "--latest",
        ], cwd=ROOT, check=True)


if __name__ == "__main__":
    main()
