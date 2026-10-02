# Renderer art (copies of `art/`)

Both apps load their pages from disk (`file://`) and must work offline, so the art they show is
copied here from the art pack. These files are **copies**: edit the source in `art/`, run the art
build, then copy again. `node scripts/design-screens.mjs` fails if a copy here differs from its
source.

| Here | Source | Used for |
|---|---|---|
| `logo/realm-emblem.svg` | `art/logo/realm-emblem.svg` | Home crest, setup wizard, first-run reveal |
| `logo/realm-favicon.svg` | `art/logo/realm-favicon.svg` | Title bar mark (below 48px), window icon |
| `logo/realm-logo-horizontal-dark.svg`, `logo/realm-wordmark-dark.svg` | `art/logo/` | Reserved for splash and about screens |
| `keyart/old-throne-1920.svg` | `art/keyart/old-throne-1920.svg` | Backdrop of every screen, player Home hero, setup wizard band |
| `icons.svg` | `art/sprite/icons.svg` | One icon per Chronicle event type (`renderer/heraldry.js`), guide and tour cards |
| `sigils/`, `shields/`, `banners/` `<house>.svg` | `art/sigils/`, `art/shields/`, `art/banners/` | Allegiance picker (shields), sworn banner on player Home (banners), Steward Realm view (shields) |
| `badges/seasons/season-1..4.svg` | `art/badges/seasons/` | Season events in the Chronicle feeds |
| `badges/titles/<title>.svg` | `art/badges/titles/` | `title_earned` / `title_bestowed` events with a known renown title |
| `textures/iron.svg`, `textures/parchment.svg` | `art/textures/` | Reserved |

To refresh after an art change (from the repository root):

```
cp art/logo/realm-emblem.svg art/logo/realm-favicon.svg art/logo/realm-logo-horizontal-dark.svg art/logo/realm-wordmark-dark.svg launcher/renderer/assets/logo/
cp art/keyart/old-throne-1920.svg launcher/renderer/assets/keyart/
cp art/sprite/icons.svg launcher/renderer/assets/icons.svg
cp art/sigils/*.svg launcher/renderer/assets/sigils/ && cp art/shields/*.svg launcher/renderer/assets/shields/ && cp art/banners/*.svg launcher/renderer/assets/banners/
cp art/badges/seasons/*.svg launcher/renderer/assets/badges/seasons/ && cp art/badges/titles/*.svg launcher/renderer/assets/badges/titles/
cp art/textures/*.svg launcher/renderer/assets/textures/
```

A missing file never breaks a screen: images hide themselves (`heraldry.js`), and without
`heraldry.js` the player app falls back to plain glyphs.
