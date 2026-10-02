# Realm Mods overrides

This folder holds Realm's settings for the game's **built-in server-side Mods system**. They are separate from the Oxide plugins. Nothing here changes game client files. The server sends the values to each player when they join, and the player's own unmodified Steam copy applies them. That is what the code shows, and the official guide (575826710) says the same.

The key names come from the game's own code. Everything below is backed by [docs/mods-keys-from-dll.md](../docs/mods-keys-from-dll.md), which was read from the `Assembly-CSharp.dll` in the Oxide 2.0.3867 zip.

## How the built-in Mods system works

1. The server creates `Mods\` next to the exe. Start the server **twice** before it appears (secondary source: the guide).
2. Each handler `<Name>` has two files [code]:
   - `<Name>.defaults.cfg` lists every key with its default value. The server **deletes and rewrites it every time it starts**, so editing it does nothing.
   - `<Name>.cfg` holds your overrides.
3. The line format is `key = 'value'`. **Keep the space before `=`**: the parser drops the character just before it. Keys are case-sensitive. Do not repeat a key.
4. The server applies a key only if that key is also in `<Name>.defaults.cfg`. When the server starts it rewrites `<Name>.cfg`, and a key it does not recognise should come back as a `# ...` comment. That behaviour is inferred from the code and UNVERIFIED in practice.
5. Stop the server before you edit, then start it again.

## The "grim but readable" preset

Folder: `presets/grim-but-readable/`

| File | What it is |
|---|---|
| `grim-but-readable.cfg` | The override lines. Every key is proven from the DLL. |
| `Apply-Preset.ps1` | Installs and reverts the preset on the **test copy** (`G:\RealmTest\server`). |

What it changes:

| Key | Value | Effect (from the code) |
|---|---|---|
| `Atmosphere.FogDensity` | `1.25` | Fog visibility is divided by 1.25, so sight distance in fog is about 20% shorter. |
| `Atmosphere.FogColor` | `rgba(0.9,0.93,0.98,1)` | Multiplies each biome's fog colour: cool grey-blue, about 5% darker. |
| `Atmosphere.MoonColor` | `rgba(0.84,0.91,1,1)` | Cooler moonlight, about 10% dimmer than white. It is kept bright enough to play at night. |
| `Atmosphere.SunColor` | `rgba(0.95,0.94,0.91,1)` | Slightly greyed sunlight, about 6% dimmer. |
| `Weather.ClearWeight` … `PrecipitateHeavyWeight` | `4 / 5 / 4 / 3 / 2` | Odds at each weather change are about: clear 23%, cloudy 43%, light rain 23%, medium rain 8%, heavy rain 1.4%. |

Not changed on purpose:

- `Clock.DaySpeed`: there is no separate dusk key, and slowing the whole cycle is not wanted.
- `Atmosphere.IslandLatitude` and `IslandLongitude`: these move the sun's path.
- Ambient light: the Mods system has no key for it.

The colour and fog values assume the scene defaults are white and `1`, which are the code defaults. `Apply-Preset.ps1` prints the server's real defaults next to each preset value and flags any difference, so you can adjust the preset.

### Install (on the owner's PC)

1. Make the test copy and start and stop it **twice** (see [docs/smoke-test.md](../docs/smoke-test.md), part A). This creates `G:\RealmTest\server\Mods\`.
2. Make sure the server is **stopped**.
3. In PowerShell:
   ```powershell
   cd <repo>\mods\presets\grim-but-readable
   .\Apply-Preset.ps1 -WhatIf     # dry run: shows which Mods\<Name>.cfg each line goes to, and the defaults
   .\Apply-Preset.ps1
   ```
   The script finds the right file by looking each key up in `Mods\*.defaults.cfg`, because the file name is not in the DLL (UNVERIFIED). A key the server does not list is skipped with a warning and never guessed. Before the first change to each `<Name>.cfg`, the script saves `<Name>.cfg.realm-backup`.
4. Start the server and open the `Mods\<Name>.cfg` files the script wrote. Every preset line should still be there, not turned into a `# ...` comment.
5. Join on `127.0.0.1` and check the look at noon, at dusk, at night and in rain. A player should still be visible 30 to 40 m away at night. If nights are too dark, raise `MoonColor` towards `rgba(0.9,0.95,1,1)` or lower `FogDensity` to `1.15`.

**Manual install, without the script:**

1. Find the file that holds the keys:
   ```powershell
   Select-String -Path 'G:\RealmTest\server\Mods\*.defaults.cfg' -Pattern '^\s*(Atmosphere|Weather)\.'
   ```
2. Copy the matching lines from `grim-but-readable.cfg` into `Mods\<that name>.cfg`, **not** into `.defaults.cfg`.
3. If the keys are spread over more than one file, put each line in its own file.

Do not copy `grim-but-readable.cfg` into `Mods\` as a file of its own. A `grim-but-readable.cfg` handler does not exist, so the game ignores the file.

### Revert

With the server stopped, run:

```powershell
.\Apply-Preset.ps1 -Revert
```

This restores every `<Name>.cfg.realm-backup`. If a `<Name>.cfg` did not exist before the preset, it deletes that file. To revert by hand, delete the preset lines from `Mods\<Name>.cfg`, or delete the whole file, and restart: the defaults apply again.

## Still UNVERIFIED (needs a running server)

- **The Mods file name(s)** that hold the `Atmosphere.`, `Weather.` and `Clock.` keys. The name is set in Unity scene data. It may be something like `Environment.cfg`, but that is not proven, and the script finds it instead.
- **Whether a headless dedicated server lists the `Atmosphere.*` keys at all.** `FogColor` and `FogDensity` exist only if the server scene has the fog controller. A key that is not listed cannot be applied, and the script reports it.
- **The real scene defaults** for colours, weather weights and day speed. The code initializers are white, `1`, `0` and `1`, but the scene can override them.
- **Locale:** colours and numbers are read and written with the server's current culture. On a comma-decimal Windows locale, `rgba(...)` lines and `1.25` can break. Check that the server's own `.defaults.cfg` uses dots.
- **How long each weather lasts.** The weights set the odds, not the duration.
- **Build match:** the keys were read from the Oxide-patched server `Assembly-CSharp.dll` (2.0.3867 zip). The owner's server build may differ. The script only writes keys that the owner's server itself lists, so a mismatch shows up as skipped keys, not as silent breakage.
