# Install on native Linux

Use the native Linux version of Valheim, including with Flatpak Steam. Leave
forced Proton compatibility disabled in Steam's Valheim properties. The Windows
PowerShell installer is for the Windows game; it is not needed here.

## First install

Close Valheim and run this from a complete checkout of the **manager repo**:

```bash
python3 installer/install-linux.py
```

The installer finds standard and Flatpak Steam libraries, verifies and installs
pinned official BepInExPack Valheim and ScriptEngine releases, and copies this
repo's `dist/ModUpdater.dll` and `.pdb` into `BepInEx/scripts`. Python 3.9 or newer
and an internet connection are needed; there are no Python package dependencies.

In Steam, set **Valheim > Properties > General > Launch Options** to:

```text
./start_game_bepinex.sh %command%
```

Launch normally and press **F7**. The built-in sources include this manager repo
and `HardHeadHackerHead/valheim-mods`. Use Browse to install gameplay mods. Public
sources need no GitHub token. **F6** reloads local mods that support hot reload.
ScriptEngine is configured to load installed mods automatically on launch.

For another Steam library, pass `--valheim-dir "/path/to/steamapps/common/Valheim"`.
The folder must contain `valheim.x86_64`.

## Both repos checked out locally

The manager and gameplay mods live in separate repos. To install both local
copies, pass the path to the gameplay repo (repeat for other mod repos):

```bash
python3 installer/install-linux.py --mods-repo ../valheim-mods
```

After pulling either repo, sync their published DLLs and symbols without
reinstalling the loader or downloading anything:

```bash
python3 installer/install-linux.py --mods-only --mods-repo ../valheim-mods
```

You can leave Valheim running during `--mods-only`. Once it finishes, press F6,
**unless the installer asks for a restart**. It respects each mod's `restart`
policy in `dist/manifest.json` (for example Recycler registers prefabs at startup)
and asks for one restart when migrating an old manager from `BepInEx/plugins`.
It always takes the manager from this repo, even if an older gameplay repo also
contains `ModUpdater.dll`.

## Private sources

Only private repos need a fine-grained GitHub token with **Contents: Read-only**
access to the relevant repo. You can enter one in the F7 manager, or store it in a
private file and supply its path:

```bash
chmod 600 /path/to/github-token
python3 installer/install-linux.py --mods-only --token-file /path/to/github-token
```

The installer stores it in `BepInEx/config/com.dhack.modupdater.cfg` with
owner-only permissions. Existing settings and tokens are preserved unless you
supply a new token. New installs disable automatic updates at startup; enable
those in F7's Settings if wanted. The GitHub CLI login is never enabled by this
installer. In Flatpak Steam, host commands such as `gh` may be unavailable; use a
read-only token for private sources instead.

## Troubleshooting and undo

- Read `BepInEx/LogOutput.log` in the game folder after a normal Steam launch.
  Expect Script Engine, ModUpdater and your installed mods without load errors.
  Headless client startup is not a substitute for an in-game check.
- No log: check the launch options, executable launcher and native Linux selection.
- Mods absent: check `[General]` / `LoadOnStart = true` in
  `BepInEx/config/com.bepis.bepinex.scriptengine.cfg`.
- All mod DLLs and PDBs go in `BepInEx/scripts`; ScriptEngine goes in `plugins`.
  Duplicate mod DLLs across those folders cause GUID conflicts.
- Replaced files and migrated plugin copies are backed up under
  `.valheim-mods-backup-<timestamp>` in the game folder. Existing loader settings
  and manager credentials are preserved. Original game files and worlds are not
  modified. Do not press F6 until the sync command finishes.
- To launch without mods, remove `./start_game_bepinex.sh` from Steam's launch
  options. To revert a replaced mod, restore its matching DLL/PDB pair from the
  backup with Valheim closed.

The pinned loader is BepInExPack Valheim **5.4.2351** and ScriptEngine is **r11.1**.
Maintainer instructions:
[BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
and [ScriptEngine](https://github.com/BepInEx/BepInEx.Debug#scriptengine).
