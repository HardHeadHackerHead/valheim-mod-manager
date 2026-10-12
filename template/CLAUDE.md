# My Valheim mods

<!-- From Claude Tools' project template. Fill in the two paths below, then keep this file short: what an assistant needs to know to
     work on these mods that it can't see in the code. -->

- **The game:** `VALHEIM_DIR` = `<the Valheim folder>`. The BepInEx folder the game uses: `<...\BepInEx>` (with r2modman, the profile's).
  Instead of environment variables you can write `mods/Directory.Build.local.props` (git-ignored, read first by the build) with the path
  Claude Tools reports: `<Project><PropertyGroup><ValheimDir>...</ValheimDir></PropertyGroup></Project>` (and `<BepInExDir>` for a profile).
- **Names:** a new mod gets its own GUID, name, folder, csproj and namespace (not `com.yourname.examplemod` / `ExampleMod` / `YourName`).
  `publish.ps1` and `modcheck` refuse the template's names.
- **Skills:** the Valheim skills come from the `valheim-modding` plugin, switched on for this project in `.claude/settings.json`. Start
  with **valheim-mod-start**; read **valheim-pitfalls** before code that registers pieces or items, changes inventories, saves data or
  takes from chests.
- **Seeing the game:** Claude Tools (with `AllowRequests = true`) carries out requests written to `<BepInEx>/claude/requests`; its guide
  `<BepInEx>/claude/CLAUDE.md` lists every command. Ask before acting in the player's game.
- **Build and load:** `dotnet build mods/<Mod> -c Release` builds and copies the DLL into `BepInEx/scripts` (`-p:DeployToGame=false` to
  only build). Then in a request: `reload <Mod>`, `waitfor <Mod> <version>`, `errors`. Raise `Version` with every change.
- **Look up, don't guess:** `game find <text>` / `game <Type.Method>` for the game's code; `who <Type.Method>` and `clashes` for other mods.
- **Before a release:** `modcheck <Mod>`; `python tools/check_translations.py` for a mod with `translations/`; a real restart; then
  `publish.ps1` (writes `dist/` for the mod manager) and, for Thunderstore, the **valheim-thunderstore** skill.
- Shared code goes in `mods/Shared` (see its README); each mod gets its own copy.
