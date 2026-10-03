# Custom art inside the game: what the client can and cannot load

Researched 2026-10-02 by decompiling the `Assembly-CSharp.dll` that ships in the Oxide 2.0.3867 zip (the same file `tools/plugin-compile-check/check.sh` downloads). No decompiled code is kept in this repo. Only type and member names are quoted here.

The question: can the server send new assets (textures, models, UI) to players, so the game looks better without anyone modifying their install?

## Short answer

The game has **no** way to download textures, models or UI from a server. It **does** have one feature that carries real images from the server to every player: **painted signs**. Each painting is a PNG of up to 1024 × 1024 that travels over the network and is saved with the world. That is the one legitimate way to put Realm's own art into the 3D world.

## What the client code contains

| Code | What it does | Usable by a server? |
|---|---|---|
| `CodeHatch.Thrones.Painting.PaintArea` | A painting's pixels. `PaintData` is the image as **PNG bytes** (`EncodeToPNG` / `LoadImage`). Up to `MAX_TEXTURE_SIZE` = 1024 px per side, `TEXEL_WORLD_SIZE` = 0.008 world units per pixel. `Serialize` writes `Bounds` and `PaintData`. | **Yes, probably.** See "Painted signs" below. |
| `PaintableObject`, `SignPainting`, `InteractablePainting` | An entity that can be painted: a colour area and a depth/normal area (relief), optional double-sided. `ApplyPainting(true)` raises the network event. | Target for a plugin. |
| `CodeHatch.Networking.Events.Entities.PaintObjectUpdateEvent` | Network event that carries the `PaintableObject` (both areas, PNG bytes included) to clients. Permission string `rok.painting.update`. | Target for a plugin. |
| `BundleManager` (`DownloadAssetBundle`, `AssetBundleFullDownloadUrl`) | Downloads Unity asset bundles. The URL comes from a field set inside the game's own scenes. Nothing in the code assigns it from the network or a config file. | **No.** |
| `CodeHatch.ModTools.Textures.TextureDatabase` | A texture-pack loader that reads `<game>/ROK_Data/Texture Packs/<pack>/*.cfg` plus image files. Nothing in the code ever calls `BeginLoadingTexture` or `SwapTexturePack`. It is unfinished developer tooling. | **No** (dead code). |
| `DownloadTexture` | Example script from the NGUI library with a placeholder URL (`yourwebsite.com/logo.png`). | No. |
| `LoadingQuips` | Loading-screen text fetched from a hard-coded Code Hatch URL. That service is offline. | No. |
| `BannerData` | Guild banners: a banner index, a pattern index into the game's built-in patterns, and two colours. No custom images. | Colours and built-in patterns only. |
| Built-in Mods system (`Mods\*.cfg`) | Numbers and colours the server sends on join: atmosphere, fog, sun, moon, weather. See `mods/README.md`. | Yes, already used. |

## Painted signs: the one real path

**How it works, from the code.** A painted sign is a `PaintableObject` entity:

1. Its image lives in `ColorArea.PaintData` as PNG bytes.
2. When it changes, the game sends `PaintObjectUpdateEvent` with the full image to clients.
3. It is part of the entity's saved data, so it should survive restarts and reach players who join later.

**What a Realm plugin could do.** It could write Realm's original art into signs that admins place in the world. Ideas:

- **House sigils** on the house seats and in the capital, from `art/sigils`.
- **Wanted posters** for outlaws, with bounties from RealmContracts.
- **A Chronicle notice board** in the capital, redrawn as events happen.
- **Season standings**, the Hall of Kings, and the current monarch's proclamation.
- **Event posters** for Crown Night and the Royal Tournament.
- **Maps and direction signs** for the capital.

**Why this stays within the rules.** No game file is modified or shared. The images are our own art, sent through a feature every player already has. Players can already paint signs themselves; the plugin would only fill them in.

**UNVERIFIED. Nothing here has been tested on a real server.**

1. Whether a server-side plugin can set `PaintData` and raise `PaintObjectUpdateEvent` so that clients accept and show the image. The dedicated server runs Unity in batch mode; `Texture2D.LoadImage` normally works there.
2. Which placeable items are `PaintableObject`s (signs certainly; banners and others unknown) and their paintable size in pixels.
3. Whether the server limits who may send `rok.painting.update`, and whether a server-sent update needs a player as sender.
4. Network cost: each update carries the whole PNG. Keep images small and redraw rarely.
5. How the depth/normal area should be filled. An empty one is the default for a flat painting.

**First test on the owner's server:**

1. Place one sign.
2. Have a small test plugin write a 256 × 256 house sigil PNG into it and raise the update event.
3. Check that the sign shows the sigil, that it is still there after a reconnect, and that it survives a server restart.

## What is not possible

- New models, textures for the world or characters, new HUD or menus, fonts, or better graphics quality. None of these can be sent by a server.
- Changing them means modifying every player's install. That breaks the EULA, Easy Anti-Cheat removes modified clients, and handing out modified game files is copyright infringement. Realm does not do this.
- The path to fully new visuals is a standalone Realm game in a modern engine. It would carry over Realm's design, lore, art, apps and plugin rules.
