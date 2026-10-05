# Valheim Mod Manager

An in-game mod manager for Valheim (BepInEx). Press **F7** to see, install, update, enable and disable mods, and to see what the other players in your world have.

- **My mods**: what you have installed, updates first.
- **Browse**: everything the sources you watch offer, grouped by source. Install what you want.
- **Players**: who has which mods, and the mods other players have that you do not (one click to install, or to add the source they use).
- **Sources**: where mods come from. Built in: this repo (the manager updates itself) and [valheim-mods](https://github.com/HardHeadHackerHead/valheim-mods) (the main mods). Community sources are suggested here through pull requests. You can add your own.

Works with public repos without a login. Mods can be reloaded in-game with no restart.

## Install (Windows, Steam)
Follow [installer/INSTALL.md](installer/INSTALL.md) (written so an AI assistant can do it for you), or run `installer/install.ps1`.
It installs BepInEx, ScriptEngine, this manager and the main mods. Afterwards press F7 in game.

## Share your own mods
Copy the [template/](template/) folder into a new public repo. It makes a "feed": a `dist/` folder with `manifest.json` and your DLLs. See [template/README.md](template/README.md).
To have your repo watched by everyone by default, open a pull request that adds it to [sources.json](sources.json). See [CONTRIBUTING.md](CONTRIBUTING.md).

## Build
Needs the .NET SDK and a Valheim install with BepInEx. Set the `VALHEIM_DIR` environment variable to the game folder (or edit `mods/Directory.Build.props`).
`dotnet build mods/ModUpdater -c Release` builds and copies it to `BepInEx/scripts`. `powershell -File publish.ps1` makes the `dist/` folder.

## How it works
A feed is a folder in a GitHub repo with `manifest.json` (mods, versions, files) and the `.dll`/`.pdb` files. The manager compares versions and file hashes through GitHub's API, downloads only what changed, and reloads just those mods using ScriptEngine.
Mods from the built-in sources can update automatically; mods from any other source are only ever installed when you click Install. Mods run inside your game with full access to your PC, so only add sources you trust.
