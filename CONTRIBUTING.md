# Suggest your mod repo

The mod manager shows mods from **sources**. Anyone can suggest one so it is watched by everybody by default.
Watching only lists the mods in the **Browse** tab: nothing is installed until a player clicks Install, and every player can stop watching any suggested source.

## How
1. Make your own mod repo (copy `template/`, see `template/README.md`). It must be public and have a `dist/` folder with `manifest.json`.
2. Edit `sources.json` in this repo and add an entry:
   ```json
   {
     "sources": [
       { "repo": "your-name/your-mod-repo", "name": "Your Name's mods", "about": "Short line about what your mods do." }
     ]
   }
   ```
   Optional per entry: `"branch": "main"` and `"folder": "dist"` if yours differ.
3. Open a pull request. The checklist in the template is what gets reviewed.

## What gets accepted
- Public repos with source code for every mod in them.
- Mods that do what their description says, and nothing that reads or sends personal data.
- Unique mod GUIDs. If two sources ship the same GUID, the one earlier in the list wins.

Removed on request, or if a source turns out to be harmful.
