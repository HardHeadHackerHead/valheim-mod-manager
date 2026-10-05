#!/usr/bin/env python3
"""Install the local, prebuilt mods for native Linux Valheim (including Flatpak Steam)."""

import argparse
import configparser
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import sys
import tempfile
from datetime import datetime, timezone
from urllib.request import Request, urlopen
import zipfile


REPO_ROOT = Path(__file__).resolve().parent.parent
PACKAGES = {
    "bepinex.zip": (
        "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/5.4.2351/",
        "bce631497976a93977ceb08e166712e6c31d15244956f89f17df092a9b62e29f",
    ),
    "scriptengine.zip": (
        "https://github.com/BepInEx/BepInEx.Debug/releases/download/r11.1/ScriptEngine_r11.1.zip",
        "7f4a385f329f9290ab8ab00d48c14a46ed61964b61a2922354e09e0dbebb339b",
    ),
}
MAX_ARCHIVE_BYTES = 16 * 1024 * 1024


def find_valheim():
    home = Path.home()
    steam_roots = [
        home / ".steam/steam",
        home / ".steam/root",
        home / ".local/share/Steam",
        home / ".var/app/com.valvesoftware.Steam/.local/share/Steam",
        home / ".var/app/com.valvesoftware.Steam/data/Steam",
    ]
    libraries = set(steam_roots)
    for root in steam_roots:
        vdf = root / "steamapps/libraryfolders.vdf"
        if vdf.is_file():
            for path in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text()):
                libraries.add(Path(path.replace("\\\\", "\\")))
    games = sorted({
        (library / "steamapps/common/Valheim").resolve()
        for library in libraries
        if (library / "steamapps/common/Valheim/valheim.x86_64").is_file()
    })
    if len(games) != 1:
        raise RuntimeError("Use --valheim-dir with the folder containing valheim.x86_64.")
    return games[0]


def check_game_closed():
    for comm in Path("/proc").glob("[0-9]*/comm"):
        try:
            name = comm.read_text().strip()
        except OSError:
            continue
        if name.startswith("valheim"):
            raise RuntimeError("Close Valheim completely before installing mods.")


def package_bytes(filename, cache):
    url, expected_hash = PACKAGES[filename]
    cached = cache / filename if cache else None
    if cached and cached.is_file():
        if cached.stat().st_size > MAX_ARCHIVE_BYTES:
            raise RuntimeError(f"Archive too large: {filename}")
        data = cached.read_bytes()
    else:
        print(f"Downloading {filename} from the official release...", flush=True)
        request = Request(url, headers={"User-Agent": "valheim-mods-linux-installer"})
        with urlopen(request, timeout=30) as response:
            data = response.read(MAX_ARCHIVE_BYTES + 1)
        if len(data) > MAX_ARCHIVE_BYTES:
            raise RuntimeError(f"Archive too large: {filename}")
    if hashlib.sha256(data).hexdigest() != expected_hash:
        raise RuntimeError(f"Checksum mismatch for {filename}; nothing installed.")
    return data


def stage_archive(data, stage, prefix=""):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        if sum(info.file_size for info in archive.infolist()) > MAX_ARCHIVE_BYTES:
            raise RuntimeError("Expanded archive is too large.")
        for info in archive.infolist():
            if info.is_dir() or not info.filename.startswith(prefix):
                continue
            relative = PurePosixPath(info.filename[len(prefix):])
            if relative.is_absolute() or ".." in relative.parts:
                raise RuntimeError("Unsafe path in archive.")
            # Native Linux only: omit the Windows and macOS bootstrap libraries.
            if not (
                relative.parts[0] == "BepInEx"
                or str(relative) in {"start_game_bepinex.sh", "doorstop_libs/libdoorstop_x64.so", ".doorstop_version"}
            ):
                continue
            destination = stage / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(archive.read(info))


def updated_config(path, section, values):
    config = configparser.ConfigParser(interpolation=None, strict=False)
    config.optionxform = str
    if path.is_file():
        config.read_string(path.read_text(encoding="utf-8-sig"))
        if config.has_section(section) and all(config.get(section, key, fallback=None) == value for key, value in values.items()):
            return path.read_bytes()
    if not config.has_section(section):
        config.add_section(section)
    for key, value in values.items():
        config.set(section, key, value)
    output = io.StringIO()
    config.write(output)
    return output.getvalue().encode("utf-8")


def local_mods(repositories):
    """The manager comes from this repo; additional repos contain gameplay mods."""
    selected = {}
    restart_files = {}
    for index, repo in enumerate(repositories):
        dist = repo / "dist"
        manifest = json.loads((dist / "manifest.json").read_text(encoding="utf-8-sig"))
        policies = {}
        for mod in manifest["mods"]:
            for name in mod["files"]:
                policies[name] = mod
        for dll in sorted(dist.glob("*.dll")):
            if index and dll.name == "ModUpdater.dll":
                continue  # never replace the manager with a copy from an old mod repo
            if dll.name in selected:
                raise RuntimeError(f"Duplicate mod filename {dll.name} in {repo}.")
            pdb = dll.with_suffix(".pdb")
            if not pdb.is_file():
                raise RuntimeError(f"Missing symbols for {dll.name}; ScriptEngine needs its .pdb file.")
            policy = policies.get(dll.name)
            if policy is None:
                raise RuntimeError(f"{dll.name} is not described in {dist / 'manifest.json'}. Publish the repo first.")
            selected[dll.name] = dll
            if policy.get("restart"):
                restart_files[dll.name] = restart_files[pdb.name] = policy["name"]
    if "ModUpdater.dll" not in selected or selected["ModUpdater.dll"].parent != repositories[0] / "dist":
        raise RuntimeError("Missing dist/ModUpdater.dll in the mod manager repo.")
    return list(selected.values()), restart_files


def install(args):
    if sys.platform != "linux":
        raise RuntimeError("This installer is for native Linux Valheim. Use install.ps1 on Windows.")
    if not args.mods_only:
        check_game_closed()
    game = args.valheim_dir.expanduser().resolve() if args.valheim_dir else find_valheim()
    if not (game / "valheim.x86_64").is_file():
        raise RuntimeError(f"No native Linux Valheim executable in {game}. Disable forced Proton in Steam.")
    repositories = [REPO_ROOT] + [path.expanduser().resolve() for path in args.mods_repo]
    mods, restart_files = local_mods(repositories)
    if args.mods_only and not (game / "BepInEx/plugins/ScriptEngine.dll").is_file():
        raise RuntimeError("Run the full installer first to install the mod loader and ScriptEngine.")
    for mod in mods:
        if mod.name != "ModUpdater.dll" and list((game / "BepInEx/plugins").rglob(mod.name)):
            raise RuntimeError(f"{mod.name} is already in plugins. Move it out before using scripts to avoid loading it twice.")
    legacy_updaters = list((game / "BepInEx/plugins").rglob("ModUpdater.dll"))
    for name, destination in [("ScriptEngine.dll", "BepInEx/plugins/ScriptEngine.dll")]:
        if any(path != game / destination for path in (game / "BepInEx/plugins").rglob(name)):
            raise RuntimeError(f"An extra {name} is already installed in plugins; remove the duplicate first.")
    token = args.token_file.expanduser().read_text().strip() if args.token_file else None
    if token is not None and (not token or "\n" in token or "\r" in token):
        raise RuntimeError("The token file must contain a single nonempty token.")

    # Complete downloads and config preparation before touching the game directory.
    with tempfile.TemporaryDirectory(prefix="valheim-mods-") as temporary:
        stage = Path(temporary)
        if not args.mods_only:
            stage_archive(package_bytes("bepinex.zip", args.downloads_dir), stage, "BepInExPack_Valheim/")
            stage_archive(package_bytes("scriptengine.zip", args.downloads_dir), stage)
        scripts = stage / "BepInEx/scripts"
        scripts.mkdir(parents=True, exist_ok=True)
        for mod in mods:
            shutil.copy2(mod, scripts / mod.name)
            shutil.copy2(mod.with_suffix(".pdb"), scripts / mod.with_suffix(".pdb").name)
        configs = stage / "BepInEx/config"
        configs.mkdir(parents=True, exist_ok=True)
        script_config = Path("BepInEx/config/com.bepis.bepinex.scriptengine.cfg")
        (stage / script_config).write_bytes(updated_config(game / script_config, "General", {"LoadOnStart": "true"}))
        updater_config = Path("BepInEx/config/com.dhack.modupdater.cfg")
        existing_updater = game / updater_config
        if token is not None or not existing_updater.exists():
            content = updated_config(existing_updater, "Repo", {"Token": token or ""})
            (configs / updater_config.name).write_bytes(content)
            (configs / updater_config.name).write_bytes(updated_config(configs / updater_config.name, "General", {"CheckOnStart": "false"}))

        required = ["BepInEx/core/BepInEx.dll", "BepInEx/plugins/ScriptEngine.dll",
                    "start_game_bepinex.sh", "doorstop_libs/libdoorstop_x64.so"]
        if not args.mods_only and any(not (stage / name).is_file() for name in required):
            raise RuntimeError("Unexpected package layout; nothing installed.")
        if not args.mods_only:
            check_game_closed()
        backup = game / (".valheim-mods-backup-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ"))
        # The current manager hot-reloads from scripts. Keep old plugin copies outside
        # BepInEx so the same GUID is not loaded twice on the next launch.
        for old in legacy_updaters:
            for source in [old, old.with_suffix(".pdb")]:
                if source.is_file():
                    saved = backup / source.relative_to(game)
                    saved.parent.mkdir(parents=True, exist_ok=True)
                    shutil.move(str(source), str(saved))
        installed = 0
        restart_needed = set()
        for source in sorted(stage.rglob("*")):
            if not source.is_file():
                continue
            relative = source.relative_to(stage)
            destination = game / relative
            # Keep the player's loader settings and configured updater on repeat installs.
            if destination.exists() and str(relative) == "BepInEx/config/BepInEx.cfg":
                continue
            if destination.exists() and source.read_bytes() == destination.read_bytes():
                continue
            if destination.exists():
                saved = backup / relative
                saved.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(destination, saved)
            destination.parent.mkdir(parents=True, exist_ok=True)
            # Replace whole files so a concurrent reload never reads half a DLL.
            temporary_target = destination.with_name(destination.name + ".installing")
            try:
                shutil.copy2(source, temporary_target)
                os.replace(temporary_target, destination)
            finally:
                temporary_target.unlink(missing_ok=True)
            installed += 1
            if relative.parent == Path("BepInEx/scripts") and source.name in restart_files:
                restart_needed.add(restart_files[source.name])
        launcher = game / "start_game_bepinex.sh"
        if launcher.exists():
            launcher.chmod(launcher.stat().st_mode | 0o100)
        (game / updater_config).chmod(0o600)
        print(f"Installed {installed} files in: {game}")
        if backup.exists():
            print(f"Previous versions backed up in: {backup}")
        if args.mods_only:
            if legacy_updaters:
                restart_needed.add("the migrated mod manager")
            if restart_needed:
                print("Restart Valheim to load: " + ", ".join(sorted(restart_needed)) + ".")
            else:
                print("Press F6 in-game to reload the updated local mods.")
        else:
            print("Steam > Valheim > Properties > General > Launch Options:")
            print("./start_game_bepinex.sh %command%")
            print("Mods load automatically; F6 reloads local mods.")
        print("F7 opens the mod manager. Public sources work without a token.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--valheim-dir", type=Path, help="folder containing valheim.x86_64; otherwise discover Steam libraries")
    parser.add_argument("--downloads-dir", type=Path, help="use previously downloaded bepinex.zip and scriptengine.zip (checksums verified)")
    parser.add_argument("--mods-only", action="store_true", help="copy local mods after git pull without downloading or reinstalling the loader; game may remain open")
    parser.add_argument("--mods-repo", type=Path, action="append", default=[], help="also install dist/ from a separate local mod repo; may be repeated")
    parser.add_argument("--token-file", type=Path, help="optional private file containing a read-only GitHub token for F7 updates")
    args = parser.parse_args()
    try:
        install(args)
    except (OSError, RuntimeError, ValueError, KeyError, TypeError, configparser.Error, zipfile.BadZipFile) as error:
        parser.exit(1, f"Error: {error}\n")


if __name__ == "__main__":
    main()
