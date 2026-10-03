# The Realm painter (art/tools/painter)

Renders Realm's original art to sign-sized PNGs for the game's painted signs, plus the pieces the RealmPainter plugin composes live boards from, and packs them into the one data file the plugin reads. Guide to the plugin: [`plugins/docs/RealmPainter.md`](../../../plugins/docs/RealmPainter.md).

```
node art/tools/painter/paint.mjs build    render with headless Chromium, write art/paintings/**, then check
node art/tools/painter/paint.mjs check    self-check without a browser: files, sizes, hashes, bundle, fonts, palette
node art/tools/painter/paint.mjs list     every painting id with its size and title
node --test art/tools/painter/test/*.test.mjs
```

`build` needs Playwright: from `art/tools/node_modules`, `$ART_NODE_MODULES` or a global install, and Chromium from `/opt/pw-browsers` or `--chromium <path>`. `check` and the tests need nothing but Node.

## What it makes

All generated; never edit by hand. `build` is deterministic: the same sources give the same bytes.

| Output | What |
|---|---|
| `art/paintings/<group>/<id>.png` | Finished paintings: house sigils, banners and notices (sigil, name, words), event posters, the welcome poster, the static Ironbreaker poster, the Realm emblem. 160-320 px a side. |
| `art/paintings/sprites/*.png` | Board pieces: parchment and iron texture tiles, 96 and 32 px sigils, the emblem, the Ironbreaker's hammer, every event icon as a 24 px mask and twelve as 64 px masks. |
| `art/paintings/fonts/<face>.png` | Glyph atlases (grey masks) for seven faces: `display` (Cinzel 700, 44 px, capitals only), `title` (Cinzel 700, 30), `head` (Cinzel 700, 20), `label` (Cinzel 600, 13), `body` and `italic` (EB Garamond 500, 17), `small` (EB Garamond 500, 14). ASCII, Latin-1 and common typographic punctuation. With `OFL-*.txt`, the font licences. |
| `art/paintings/manifest.json` | Every file with its size and sha256. |
| `art/paintings/RealmPainterArt.json` | The bundle the plugin reads: every painting and sprite as base64 PNG, the atlases with their glyph records `[codepoint, x, y, w, h, left, top, advance*64]`, the Chronicle type-to-icon map from `art/icons/event-map.json`, the board palette from `art/palette.json`, and the licence lines. Copy it to `<server>/oxide/data/`. |

`designs.mjs` holds the designs (HTML and SVG built from the art pack; colours only from `art/palette.json`), the size caps and the faces. `png.mjs` is a small PNG encoder and decoder with no dependencies; the decoder is also the independent check of the plugin's C# encoder (`plugins/docs/RealmPainter/logic-tests/decode-check.mjs`).

## Licences

Paintings and sprites are original Realm art from `art/`. The glyph atlases are rendered from Cinzel (Copyright 2020 The Cinzel Project Authors) and EB Garamond (Copyright 2017 The EB Garamond Project Authors), both SIL Open Font License 1.1; the licence texts sit next to the atlases and the bundle's `Licenses` credits them.

## The self-check

`check` fails when: a file changed since the build (sha256), a painting is blank or missing, a design is not built or a built item is no longer designed, a picture is over its size cap (512 px a side; 160 KB a painting, 48 KB a sprite, 64 KB an atlas, 3 MB the bundle), the bundle and the files differ, a Chronicle type has no icon sprite, a face lacks a character of its charset or has a glyph outside its atlas, the palette differs from `art/palette.json`, or a font licence is missing.
