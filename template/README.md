# Your own Valheim mods, shared through the mod manager

> This starter kit comes with Claude Tools (`BepInEx/claude/templates/project`) and is `template/` in the
> [mod manager repo](https://github.com/HardHeadHackerHead/valheim-mod-manager): the same files.

This folder is a starter kit. Copy it into a new GitHub repo (public is easiest) and your mods can be installed and updated
by anyone running the mod manager (F7), including people who also use someone else's mods.

## What the manager reads: a "feed"
A feed is just a folder in a GitHub repo (default `dist`) that contains:

| File | What |
| --- | --- |
| `manifest.json` | list of mods: guid, name, version, description, notes, files, optional `restart`, optional `was` (the GUIDs a mod had before) |
| `YourMod.dll`, `YourMod.pdb` | the built mod (the .pdb must sit next to the .dll) |

You never write `manifest.json` by hand. `publish.ps1` builds every mod in `mods/` and writes the whole `dist` folder.

## Layout
```
your-repo/
  CLAUDE.md                   what an assistant needs to know about your mods (fill in the two paths)
  .claude/settings.json       switches on the Valheim skills (the valheim-modding plugin) for this project only
  publish.ps1
  mods/
    Directory.Build.props       game paths + references (set VALHEIM_DIR, or make Directory.Build.local.props)
    Shared/                     code more than one mod needs, compiled into each (see Shared/README.md)
      All/                      ...into every mod (AiDisclosure.cs: says AI helped, as Thunderstore asks)
    YourMod/
      YourMod.csproj
      Plugin.cs                 needs const Guid, Name, Version
      DESCRIPTION.txt           shown in the manager (optional)
      CHANGELOG.txt             top paragraph = "what's new" (optional)
      RESTART_REQUIRED.txt      optional: the mod can't be hot-reloaded; text = why
      translations/             optional: English.json and one file per language (see Shared/Translations.cs)
  tools/
    check_translations.py     every language has the same keys and {0} placeholders as English
  dist/                         generated, commit it
```

## Publishing
1. Install BepInEx for Valheim and Claude Tools (its dev loader loads and reloads mods in `BepInEx/scripts`; ScriptEngine works too). Tell
   the build where the game is: set the environment variable `VALHEIM_DIR` to the game folder (and `BEPINEX_DIR` too if a mod manager keeps
   BepInEx in a profile folder), or write `mods/Directory.Build.local.props` instead (see below).
2. Make a mod: copy `mods/ExampleMod`, rename the folder, csproj, namespace, and change `Guid`, `Name`, `Version`.
   `publish.ps1` refuses a mod that still has the template's names (`yourname`, `examplemod`); `publish.ps1 -Check` only checks them.
3. Run `powershell -File publish.ps1` (or `publish.ps1 -Mods MyMod` to build only that mod: the others stay exactly as they
   are in `dist/`, so players see no update for a mod that didn't change).
4. `git add dist; git commit; git push`.
5. Raise `Version` for every change people should receive. The manager compares versions, and also notices rebuilt files.

No environment variable? Make `mods/Directory.Build.local.props` with your game folder in it; the build reads it first, and git ignores it:

```xml
<Project><PropertyGroup><ValheimDir>D:/Games/Valheim</ValheimDir></PropertyGroup></Project>
```

On native Linux (including Flatpak Steam), set `VALHEIM_DIR` to the folder
containing `valheim.x86_64`; common Steam locations are detected automatically.
Use `dotnet build mods/ExampleMod -c Release` to build and deploy, then `claude reload ExampleMod` in the game's console.
Run `pwsh -NoProfile -File ./publish.ps1` with PowerShell 7 to publish. Publishing
only writes this repo's build output and `dist/`; it does not deploy to your game.
For validation builds, pass `-p:DeployToGame=false`.

## Sharing code between your mods
Put it in `mods/Shared` (see its README): compiled into each mod, so players install one mod and nothing else, and two of your mods
never clash over it. That is what most popular Valheim mods do (blaxxun-boop's ServerSync and PieceManager are merged into each mod's
DLL); a separate library mod that players must install too (like Jotunn) is for big frameworks many mods build on.

## Telling others
Anyone adds your feed in the mod manager (F7), "Mod sources": type `your-github-name/your-repo`
(`owner/repo@branch:folder` if you use a different branch or folder). They click Install themselves; nothing from an extra source is installed automatically.

## Rules that keep things working
- `Guid` must be unique and never change. If you must change it, keep the old one as `public const string OldGuid = "..."`: the manager then treats the old copy as an older version of the same mod (and asks for a restart), and move the settings file over in your mod (BepInEx names it after the GUID). Two feeds with the same Guid: the first feed in the player's list wins.
- File names must be unique across mods; the manager skips a mod whose files clash with another.
- Only `.dll` and `.pdb` files are installed, straight into `BepInEx/scripts`.
- Add `RESTART_REQUIRED.txt` if reloading in-game breaks the mod (for example it registers prefabs at startup). The manager then says "Restart the game" instead of reloading it.
- Undo your Harmony patches in `OnDestroy` so reloads stay clean.

## Getting watched by default
Open a pull request to the mod manager repo that adds your repo to `sources.json` (see `CONTRIBUTING.md` there). Everyone running the manager will then see your mods in Browse.

## Cover image
Put a `cover.png` (or `cover.jpg`) in a mod's folder, about 640x360 and under 1 MB. `publish.ps1` copies it into `dist/` and the manager shows it on the mod's card. Without one, the card shows a coloured tile with the mod's first letter.
