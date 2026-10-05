# Your own Valheim mods, shared through the mod manager

This folder is a starter kit. Copy it into a new GitHub repo (public is easiest) and your mods can be installed and updated
by anyone running the mod manager (F7), including people who also use someone else's mods.

## What the manager reads: a "feed"
A feed is just a folder in a GitHub repo (default `dist`) that contains:

| File | What |
| --- | --- |
| `manifest.json` | list of mods: guid, name, version, description, notes, files, optional `restart` |
| `YourMod.dll`, `YourMod.pdb` | the built mod (the .pdb must sit next to the .dll) |

You never write `manifest.json` by hand. `publish.ps1` builds every mod in `mods/` and writes the whole `dist` folder.

## Layout
```
your-repo/
  publish.ps1
  mods/
    Directory.Build.props       game paths + references (set VALHEIM_DIR or edit the path)
    YourMod/
      YourMod.csproj
      Plugin.cs                 needs const Guid, Name, Version
      DESCRIPTION.txt           shown in the manager (optional)
      CHANGELOG.txt             top paragraph = "what's new" (optional)
      RESTART_REQUIRED.txt      optional: the mod can't be hot-reloaded; text = why
  dist/                         generated, commit it
```

## Publishing
1. Install BepInEx (+ ScriptEngine) for Valheim; set the environment variable `VALHEIM_DIR` to the game folder (or edit `Directory.Build.props`).
2. Make a mod: copy `mods/ExampleMod`, rename the folder, csproj, namespace, and change `Guid`, `Name`, `Version`.
3. Run `powershell -File publish.ps1`.
4. `git add dist; git commit; git push`.
5. Raise `Version` for every change people should receive. The manager compares versions, and also notices rebuilt files.

## Telling others
Anyone adds your feed in the mod manager (F7), "Mod sources": type `your-github-name/your-repo`
(`owner/repo@branch:folder` if you use a different branch or folder). They click Install themselves; nothing from an extra source is installed automatically.

## Rules that keep things working
- `Guid` must be unique and never change. Two feeds with the same Guid: the first feed in the player's list wins.
- File names must be unique across mods; the manager skips a mod whose files clash with another.
- Only `.dll` and `.pdb` files are installed, straight into `BepInEx/scripts`.
- Add `RESTART_REQUIRED.txt` if reloading in-game breaks the mod (for example it registers prefabs at startup). The manager then says "Restart the game" instead of reloading it.
- Undo your Harmony patches in `OnDestroy` so reloads stay clean.

## Getting watched by default
Open a pull request to the mod manager repo that adds your repo to `sources.json` (see `CONTRIBUTING.md` there). Everyone running the manager will then see your mods in Browse.
