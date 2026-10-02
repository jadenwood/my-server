# Realm Mods overrides

This folder holds Realm's settings for the game's **built-in server-side Mods system**. Those settings are separate from the Oxide plugins. Nothing here changes game client files: the server sends the values, and players see them in their own unmodified Steam copy [VERIFIED-SECONDARY, guide 575826710].

## How the built-in Mods system works

These facts come from [docs/server-reference.md](../docs/server-reference.md), section 5. Most of them are **secondary**: they come from search snippets of the official "Server-Side Modding" guide (https://steamcommunity.com/sharedfiles/filedetails/?id=575826710), which could not be opened in full.

1. The server creates a `Mods\` folder next to the exe. **You have to start the server at least twice** before the folder appears.
2. Each category has two files:
   - `<Name>.defaults.cfg` lists every setting at its default value. Treat it as reference only. Do not edit it.
   - `<Name>.cfg` holds your overrides. It starts empty or close to empty.
3. To change a value, copy **the whole line** from the `.defaults.cfg` file into the `.cfg` file, then edit only the value.
4. Restart the server so the change takes effect.

The only file names confirmed in any source are `Players.cfg` and `Players.defaults.cfg`. The guide says other categories exist, including weather and day-night (fog colour and density, sun and moon colour, cycle speed, how often it is cloudy, raining or clear), crafting, armour, blocks and loot. **None of their file names or key names have been verified.** Any name such as `Environment.cfg` is a guess.

## What is in this folder

| File | Status |
|---|---|
| `templates/grim-but-readable.template.cfg` | **A template only.** It describes the "grim but readable" preset: more fog, cooler moonlight, a slightly longer dusk and more storms. Every key is a `<PLACEHOLDER>`, so do not copy it into a server. |

No ready-to-use override file is included. Writing one would mean guessing key names, and the game might ignore a wrong name without any warning.

## Turning the template into real overrides on the owner's PC

1. Run the vanilla smoke test ([docs/smoke-test.md](../docs/smoke-test.md), part A). Start and stop the test server **twice**.
2. In PowerShell, from the repo's `server\` folder, run:
   ```powershell
   .\Export-ModKeys.ps1
   ```
   The script only reads files. It lists every `Mods\*.defaults.cfg` in `G:\RealmTest\server` and copies out the lines that mention fog, moon, sun, day, weather, rain, storm, cloud and similar words. It writes them to `docs\mods-keys.txt`. Commit that file so the real key names are in the repo.
3. For each `<PLACEHOLDER>` in the template, find the matching line in `mods-keys.txt`. Add the whole line to `G:\RealmTest\server\Mods\<Name>.cfg` with the target value. Only edit the **test copy**, never the Steam folder.
4. Restart the test server. Join on `127.0.0.1` and check the preset at noon, at dusk, at night and in a storm. A player should still be visible 30 to 40 m away at night.
5. When the values look right, save the real `<Name>.cfg` files in this folder (for example `mods/grim-but-readable/<Name>.cfg`), and replace the template's placeholders with the verified key names.

## Unverified

- Every atmosphere, weather and day-night key name, and the file names those keys live in.
- Whether override files use the same `key = 'value'` format as `Configuration\*.cfg`. Copying whole lines from `.defaults.cfg` works whatever the format is.
- Whether a separate "dusk length" setting exists. If it does not, leave the overall day-night speed at its default and do not use it as a stand-in.
- Whether the server reloads Mods files while it is running. Assume a restart is needed.
