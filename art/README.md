# Realm art pack

This folder holds original heraldry and brand art for the Realm of Ostreval: six house sigils with banner and shield versions, the Realm logo, Chronicle event icons, Discord art and a social card template. Usage rules are in [`docs/brand.md`](../docs/brand.md). To see everything, open [`index.html`](index.html) in a browser. It works straight from disk; with no network the gallery falls back to system serif fonts.

## What's here

| Path | What | How it is made |
|---|---|---|
| `sigils/<house>.svg` | House roundels, 256 × 256 | **Hand-written** |
| `banners/<house>.svg`, `shields/<house>.svg` | Banner (200 × 340) and shield (240 × 280) for each house | Generated from the sigil's `<g id="charge">` |
| `logo/realm-emblem.svg` | The emblem: crown in the Old Throne | **Hand-written** |
| `logo/realm-logo-{horizontal,stacked}-{dark,light}.svg`, `logo/realm-wordmark-{dark,light}.svg`, `logo/realm-emblem-mono.svg`, `logo/realm-favicon.svg` | Lockups and small marks | Generated from the emblem and `src/wordmark.json` |
| `src/wordmark.json` | REALM / OSTREVAL as SVG path data | Outlined once from Cinzel (OFL) by `tools/wordmark.mjs` |
| `src/cinzel-bold-widths.json` | Cinzel Bold advance widths, used to wrap social-card titles | Written by `tools/wordmark.mjs` |
| `icons/*.svg` | Event icons on a 24px grid | **Hand-written** |
| `icons/event-map.json` | Suggested icon for every Chronicle event type | **Hand-written** |
| `sprite/icons.svg` | All icons as `<symbol>`s | Generated |
| `discord/*.svg` | Server icon 512, banner 960 × 540, invite splash 1920 × 1080 | Generated |
| `social/card-template.svg`, `social/card-example-*.svg` | 1200 × 630 link-preview card | Generated |
| `palette.json` | Every colour the pack may use | **Hand-written**, enforced by the checker |
| `png/` | PNG exports of everything above, plus `icons/contact-sheet.png` | Rendered with headless Chromium |
| `index.html` | Gallery | Generated |
| `tools/` | Build, lint, export and test scripts | **Hand-written** |

Generated files say so in their `<desc>`. Edit the hand-written source, never a generated file. The next build overwrites generated files.

## Building

You need Node 18 or newer. The tools need `svgo` (optimising), `playwright` (PNG export) and `opentype.js` (only to re-outline the wordmark). None of these is committed.

```
cd art/tools && npm install          # once; or point ART_NODE_MODULES at a node_modules folder that has them
node art/tools/build.mjs all         # generate + optimise + PNGs + gallery + check
node --test art/tools/test/*.test.mjs
```

The steps can also be run one at a time: `generate`, `optimize`, `png`, `gallery` and `check`. `check --skip-png` runs the lint without a browser.

PNG export uses Playwright's Chromium. Pass `--chromium <path>` (or set `PLAYWRIGHT_CHROMIUM`) to use a browser you already have. Only the social cards contain live text. For those, the exporter loads Cinzel and EB Garamond from Google Fonts. You can instead pass `--fonts <dir>` with the `.ttf` files. Either way, the export **fails** if the fonts did not load, so a card never ships in a fallback font.

To re-outline the wordmark (only if its lettering changes), download Cinzel 600 and 700 from Google Fonts, then run:

```
node art/tools/wordmark.mjs --bold Cinzel-Bold.ttf --semibold Cinzel-SemiBold.ttf
```

## What `check` enforces

- Every SVG is well-formed. The `png` step also parses each one strictly in Chromium.
- Each SVG has the SVG namespace, a `viewBox` and a `<title>`.
- No scripts, event handlers, `javascript:` URLs, embedded rasters, `foreignObject`, or external `href`/`url()`. Every file is self-contained and safe to inline.
- No live `<text>` outside `social/`. Logos must render without fonts.
- Ids are unique and every `url(#…)` / `href="#…"` resolves. Sigil ids are prefixed with the house name, so sigils can share a page.
- Every hex colour in every SVG is listed in `palette.json`. Icons use `currentColor` only, on a 24 × 24 grid with a 1.75 stroke.
- House field colours equal the dye the Chronicle overlay computes for that house name. The overlay's hash in `chronicle/public/assets/common.js` is re-run to check this.
- Contrast: the documented text pairs, metal on field (3:1 or better), and Discord role colours (4.5:1 or better on Discord dark).
- `docs/brand.md` documents every brand and house colour and mentions no colour outside the palette.
- Every icon named in `icons/event-map.json` exists. Event types in `chronicle/server.js` with no mapping are reported as warnings.
- No well-known third-party franchise names appear in the pack or the brand guide.
- File size budgets: icons under 1.5 KB, sigils under 8 KB, everything else under 60 KB.
- Every expected PNG exists at the right pixel size, and `index.html` exists.
- When PNGs are exported, social-card text is measured in the browser with the real fonts. The export fails if any line runs into the sigil column.

## Adding a house sigil

1. Add the house to `palette.json`. Its `field` is the overlay dye for its name; `check` tells you if you got it wrong.
2. Copy a sigil file and draw the charge inside `<g id="charge">` on a 200 × 200 grid. Prefix any ids with the house key.
3. Run `node art/tools/build.mjs all`. The banner, shield, PNGs and gallery entry are created for you. The Discord art shows the six great houses only.

## Licence

All art here is original work for this project. The wordmark letters are outlined from Cinzel, © The Cinzel Project Authors, SIL Open Font License 1.1; no font files are included. The repository has no licence file yet. See the Originality section of `docs/brand.md`.
