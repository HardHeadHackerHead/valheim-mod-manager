"""File-system regression checks; no Valheim installation or network required."""

from argparse import Namespace
from contextlib import redirect_stdout
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile


SCRIPT = Path(__file__).resolve().parents[1] / "installer/install-linux.py"
SPEC = importlib.util.spec_from_file_location("linux_installer", SCRIPT)
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)


class LinuxInstallTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.manager = self.root / "manager"
        self.mods = self.root / "gameplay mods"
        self.game = self.root / "Valheim with spaces"
        self.game.mkdir()
        (self.game / "valheim.x86_64").write_bytes(b"test executable")
        plugins = self.game / "BepInEx/plugins"
        plugins.mkdir(parents=True)
        (plugins / "ScriptEngine.dll").write_bytes(b"existing engine")
        self.write_repo(self.manager, "ModUpdater")
        self.write_repo(self.mods, "ExampleMod")
        self.args = Namespace(valheim_dir=self.game, mods_repo=[self.mods],
                              mods_only=True, token_file=None, downloads_dir=None)
        self.patch_root = patch.object(installer, "REPO_ROOT", self.manager)
        self.patch_root.start()
        self.addCleanup(self.patch_root.stop)

    def write_repo(self, repo, name, restart=""):
        dist = repo / "dist"
        dist.mkdir(parents=True, exist_ok=True)
        (dist / (name + ".dll")).write_bytes((name + " DLL").encode())
        (dist / (name + ".pdb")).write_bytes((name + " symbols").encode())
        (dist / "manifest.json").write_text(json.dumps({"mods": [{
            "name": name, "restart": restart, "files": [name + ".dll", name + ".pdb"]
        }]}))

    def run_install(self):
        output = io.StringIO()
        with redirect_stdout(output):
            installer.install(self.args)
        return output.getvalue()

    def test_split_repos_sync_without_network_or_loader_changes(self):
        with patch.object(installer, "package_bytes", side_effect=AssertionError("network")):
            output = self.run_install()
        scripts = self.game / "BepInEx/scripts"
        for repo, name in [(self.manager, "ModUpdater"), (self.mods, "ExampleMod")]:
            for suffix in [".dll", ".pdb"]:
                self.assertEqual((scripts / (name + suffix)).read_bytes(),
                                 (repo / "dist" / (name + suffix)).read_bytes())
        self.assertEqual((self.game / "BepInEx/plugins/ScriptEngine.dll").read_bytes(), b"existing engine")
        self.assertIn("Press F6", output)
        self.assertFalse(list(self.game.rglob("*.installing")))

    def test_manager_always_comes_from_manager_repo(self):
        (self.mods / "dist/ModUpdater.dll").write_bytes(b"stale bridge")
        self.run_install()
        self.assertEqual((self.game / "BepInEx/scripts/ModUpdater.dll").read_bytes(), b"ModUpdater DLL")

    def test_credentials_and_comments_are_preserved(self):
        config = self.game / "BepInEx/config/com.dhack.modupdater.cfg"
        config.parent.mkdir()
        original = b"# keep settings\n[Repo]\nToken = private-placeholder\n[General]\nCheckOnStart = true\n"
        config.write_bytes(original)
        self.run_install()
        self.assertEqual(config.read_bytes(), original)
        self.assertEqual(config.stat().st_mode & 0o777, 0o600)

    def test_plugin_migration_backs_up_old_manager_and_requires_restart(self):
        old = self.game / "BepInEx/plugins/ModUpdater/ModUpdater.dll"
        old.parent.mkdir()
        old.write_bytes(b"old manager")
        old.with_suffix(".pdb").write_bytes(b"old symbols")
        output = self.run_install()
        self.assertFalse(old.exists())
        saved = list(self.game.glob(".valheim-mods-backup-*/BepInEx/plugins/ModUpdater/ModUpdater.dll"))
        self.assertEqual(saved[0].read_bytes(), b"old manager")
        self.assertEqual(saved[0].with_suffix(".pdb").read_bytes(), b"old symbols")
        self.assertIn("Restart Valheim", output)

    def test_restart_policy_applies_only_when_files_change(self):
        self.write_repo(self.mods, "ExampleMod", "registers prefabs")
        self.assertIn("Restart Valheim to load: ExampleMod", self.run_install())
        self.assertIn("Press F6", self.run_install())

    def test_missing_symbols_fails_before_game_mutation(self):
        (self.mods / "dist/ExampleMod.pdb").unlink()
        with self.assertRaisesRegex(RuntimeError, "Missing symbols"):
            self.run_install()
        self.assertFalse((self.game / "BepInEx/scripts").exists())

    def test_duplicate_gameplay_filenames_fail_before_mutation(self):
        another = self.root / "another repo"
        self.write_repo(another, "ExampleMod")
        self.args.mods_repo.append(another)
        with self.assertRaisesRegex(RuntimeError, "Duplicate mod filename"):
            self.run_install()
        self.assertFalse((self.game / "BepInEx/scripts").exists())

    def test_full_install_refuses_running_game(self):
        self.args.mods_only = False
        with patch.object(installer, "check_game_closed", side_effect=RuntimeError("Close Valheim")):
            with self.assertRaisesRegex(RuntimeError, "Close Valheim"):
                self.run_install()

    def test_archive_traversal_is_rejected(self):
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w") as archive:
            archive.writestr("BepInExPack_Valheim/../../outside", b"bad")
        with self.assertRaisesRegex(RuntimeError, "Unsafe path"):
            installer.stage_archive(data.getvalue(), self.root / "stage", "BepInExPack_Valheim/")

    def test_download_checksum_is_verified(self):
        cache = self.root / "cache"
        cache.mkdir()
        (cache / "bepinex.zip").write_bytes(b"incorrect package")
        with self.assertRaisesRegex(RuntimeError, "Checksum mismatch"):
            installer.package_bytes("bepinex.zip", cache)

    def test_flatpak_and_custom_steam_library_detection(self):
        fake_home = self.root / "home"
        steam = fake_home / ".var/app/com.valvesoftware.Steam/.local/share/Steam"
        library = self.root / "custom Steam library"
        game = library / "steamapps/common/Valheim"
        game.mkdir(parents=True)
        (game / "valheim.x86_64").write_bytes(b"game")
        (steam / "steamapps").mkdir(parents=True)
        (steam / "steamapps/libraryfolders.vdf").write_text('"path" "' + str(library) + '"')
        with patch.object(Path, "home", return_value=fake_home):
            self.assertEqual(installer.find_valheim(), game.resolve())


if __name__ == "__main__":
    unittest.main()
