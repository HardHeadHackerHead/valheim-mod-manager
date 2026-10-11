# Shared code

Code more than one of your mods needs. It is **compiled into each mod's own DLL**: players install only your mod, nothing extra, and
each mod gets its own private copy, so two of your mods never clash over it. One file to fix; a fix reaches players when each mod
using it is rebuilt and released (raise its `Version`).

- **`Shared/All/`**: compiled into every mod (see `Directory.Build.props`). Starts with `AiDisclosure.cs`.
- **`Shared/Translations.cs`**: compiled into every mod that has a `translations/English.json`, with its `translations/*.json`
  built in: the mod's words in the player's language (`T("yourmod_key")`; see the file's header and the valheim-mod-start skill).
- **Anything else in `Shared/`**: linked by the mods that want it, in their csproj:
  `<ItemGroup><Compile Include="../Shared/ContainerAccess.cs" Link="Shared/ContainerAccess.cs" /></ItemGroup>`

Rules that keep it working:
- Make every class `internal`, in a namespace of yours (`YourName.Shared`): each mod's copy stays private to its DLL.
- At the top of each file: why it exists, which mods use it, how it gets in.
- Small and tested in a real mod before a second one uses it.
- Ready-made files to start from: Claude Tools' `BepInEx/claude/templates` (settings the server decides, taking from chests safely,
  windows in the game's look, build pieces that reload while you play). Copy one here and change its namespace.
