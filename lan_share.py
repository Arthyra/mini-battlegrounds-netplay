"""Serve one prepared game copy to friends on the same LAN."""
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import shutil
import socket
import subprocess
from pathlib import Path
from secrets import token_urlsafe
from tempfile import TemporaryDirectory
from threading import Thread

from prepare_lan_client import prepare


def default_lan_host():
    # A UDP connect selects the outgoing interface without sending traffic.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as route:
        route.connect(("192.0.2.1", 9))
        return route.getsockname()[0]


@contextmanager
def share_client(host):
    with TemporaryDirectory(prefix="mini-battlegrounds-lan-", ignore_cleanup_errors=True) as directory:
        root = Path(directory)
        client = root / "client"
        print("Preparing the LAN client download...", flush=True)
        prepare(client, host)
        archive = Path(shutil.make_archive(str(root / "mini-battlegrounds-lan"), "zip", root, "client"))
        size = archive.stat().st_size
        invite = "/" + token_urlsafe(16)

        class Download(BaseHTTPRequestHandler):
            def setup(self):
                super().setup()
                self.connection.settimeout(15)

            def do_GET(self):
                if self.path != invite:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header("Content-Type", "application/zip")
                self.send_header("Content-Disposition", 'attachment; filename="mini-battlegrounds-lan.zip"')
                self.send_header("Content-Length", str(size))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                try:
                    with archive.open("rb") as download:
                        shutil.copyfileobj(download, self.wfile)
                except (ConnectionError, TimeoutError):
                    pass

        with ThreadingHTTPServer((host, 43121), Download) as http:
            game = subprocess.Popen([str(client / "CrazyShooter.exe"), "-screen-fullscreen", "0", "-lan-profile=slot-1"],
                                    cwd=client)
            thread = Thread(target=http.serve_forever, daemon=True)
            thread.start()
            try:
                print(f"Invite link (same LAN): http://{host}:{http.server_port}{invite}", flush=True)
                yield
            finally:
                http.shutdown()
                if game.poll() is None:
                    game.terminate()
                    game.wait()
