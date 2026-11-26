"""Public installer archive must only contain files from an explicit allowlist."""
import importlib.util
import hashlib
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("mod_installer_builder", ROOT / "steam-install/build-installer.py")
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class InstallerArchiveTests(unittest.TestCase):
    def test_zip_never_contains_unity_dll_or_extracted_game_json(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            client = work / "client"
            managed = client / "CrazyShooter_Data/Managed"
            managed.mkdir(parents=True)
            (managed / "LanRoomUI.dll").write_bytes(b"self-built room UI")
            # Deliberately place forbidden game assets in the input directory:
            # the builder must never mirror the input directory.
            (managed / "UnityEngine.dll").write_bytes(b"original runtime")
            (managed / "Assembly-CSharp.dll").write_bytes(b"original assembly")
            server = work / "server"
            server.mkdir()
            (server / "server.exe").write_bytes(b"self-built server")
            (server / "map102-vehicle-obstacles.json").write_text("[]")
            tools = work / "tools"
            tools.mkdir()
            for name in ("PatchLanCombat.exe", "Mono.Cecil.dll"):
                (tools / name).write_bytes(b"patch program")
            launcher = work / "launcher"
            launcher.mkdir()
            for name in ("Start Game.exe", "Updater.exe"):
                (launcher / name).write_bytes(b"launcher")
            extractor = work / "ExtractLocalData.exe"
            extractor.write_bytes(b"offline local extractor")
            notices = work / "EXTRACTOR_NOTICES.txt"
            notices.write_text("dependency licenses reviewed by package creator")
            pristine = work / "Assembly-CSharp.dll"
            pristine.write_bytes(b"known original")
            csc = work / "csc.exe"
            csc.write_bytes(b"compiler placeholder")

            def compile_stub(command, check):
                self.assertTrue(check)
                output = next(arg for arg in command if arg.startswith("/out:"))
                Path(output[len("/out:"):]).write_bytes(b"built ModSetup")

            args = [
                "build-installer.py", "--client", str(client),
                "--server-dist", str(server), "--launcher-dir", str(launcher),
                "--patch-tools", str(tools), "--extractor", str(extractor),
                "--extractor-notices", str(notices),
                "--supported-assembly", str(pristine),
                "--output", str(work / "output"), "--version", "1.0-test"
            ]
            with patch.object(sys, "argv", args), patch.object(builder, "CSC", csc), \
                 patch.object(builder.subprocess, "run", side_effect=compile_stub):
                builder.main()
            result = work / "output/mbg-lan-mod-setup-1.0-test.zip"
            with zipfile.ZipFile(result) as z:
                names = set(z.namelist())
                self.assertIn("ModSetup.exe", names)
                self.assertEqual(z.read("LICENSE"), (ROOT / "LICENSE").read_bytes())
                self.assertIn("payload/ExtractLocalData.exe", names)
                self.assertIn("payload/MONO_CECIL_LICENSE.txt", names)
                self.assertIn("payload/EXTRACTOR_NOTICES.txt", names)
                self.assertIn("payload/supported-assemblies.sha256", names)
                self.assertNotIn("payload/UnityEngine.dll", names)
                self.assertNotIn("payload/UnityEngine.UI.dll", names)
                self.assertFalse(any(name.endswith(".json") for name in names))
                self.assertFalse(any("Assembly-CSharp.dll" in name for name in names))
                expected = hashlib.sha256(pristine.read_bytes()).hexdigest()
                self.assertIn(expected, z.read("payload/supported-assemblies.sha256").decode())


if __name__ == "__main__":
    unittest.main()
