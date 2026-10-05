# Valheim mods: setup instructions (written for an AI agent)

You are helping a Valheim player install a small set of friend-made mods on **Windows** (Steam). They have
never installed mods before. Do the install for them and explain what you're doing in plain language.

## What you need from the player
- Nothing, if the repos `HardHeadHackerHead/valheim-mod-manager` and `HardHeadHackerHead/valheim-mods` are public. If it is private, a **read-only GitHub token** (the repo owner
  sends it separately): ask the player to paste it and pass it as `-Token` below. Treat it as a secret: don't echo it back, don't commit it,
  don't put it in logs or screenshots.

## Steps
1. **Get the installer.** (For a private repo add the header `Authorization = "Bearer $token"` to the request.)
   ```powershell
   $token = ""   # only for a private repo: put the read-only token here
   Invoke-WebRequest "https://api.github.com/repos/HardHeadHackerHead/valheim-mod-manager/contents/installer/install.ps1?ref=main" `
     -Headers @{ Accept = "application/vnd.github.raw+json"; "User-Agent" = "installer" } `   # private repo: also add Authorization = "Bearer $token"
     -OutFile "$env:TEMP\install-valheim-mods.ps1"
   ```
   (In Windows PowerShell 5.1, run `[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12` first.)

2. **Have the player close Valheim completely** (the installer refuses to run while the game is open).

3. **Read the script before running it.** It's short. Confirm it only touches the Valheim folder and your
   temp folder, then run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File "$env:TEMP\install-valheim-mods.ps1" -Token $token
   ```
   If it can't find the game, re-run with `-ValheimDir "<folder containing valheim.exe>"`
   (Steam > right-click Valheim > Manage > Browse local files).

4. **Verify.** The installer prints each step. Afterwards check that these exist in the Valheim folder:
   - `BepInEx\core\BepInEx.dll`
   - `BepInEx\plugins\ScriptEngine.dll`
   - `BepInEx\scripts\ModUpdater.dll` (and its `.pdb`)
   - at least one `BepInEx\scripts\*.dll`

5. **Have the player launch Valheim from Steam and load into the world.** The first launch with mods is slower.
   Success looks like a chat line: `[Mod]: CraftFromChests v... loaded`.

## What got installed (explain this to the player)
| Piece | What it is |
|---|---|
| BepInEx | The standard Valheim mod loader. Mods can't run without it. |
| ScriptEngine | Lets mods reload while the game is running (F6), so no restarts. |
| ModUpdater | The mod manager. Press **F7** in-game to see installed vs. latest versions (and what other players have), and to download/reload updates. It updates itself too. |
| CraftFromChests | The first mod: crafting stations use materials from nearby chests. |

## Using it
- At a workbench you'll see **Chest lines** and **Range** buttons by the tabs. Lines draws a line to each chest in use; Range cycles 5/10/15/20/30 m.
- To get new versions of the mods later: press **F7** in-game.

## If something goes wrong
- Read `<Valheim folder>\BepInEx\LogOutput.log`. It lists each mod that loaded and any errors.
- "Can't read ... with that token": the token is wrong or expired. Ask the repo owner for a new one.
- Mods not loading at all: make sure the game was launched through Steam, and that `winhttp.dll` exists in the Valheim folder.
- A game update can break mods until they're updated: ask the repo owner, then press F7.
- To undo everything: delete `BepInEx`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and `doorstop_libs` from the Valheim folder (or "Verify integrity of game files" in Steam).

## Notes for the agent
- Windows only. Don't use this on Linux/Steam Deck.
- Don't modify the game's own files. Everything is added alongside them.
- Don't skip step 3: reading a script before running it is good practice, and the player should see you do it.
