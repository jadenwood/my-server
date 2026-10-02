# Realm brand guide

This page sets out how the Realm looks: colours, type, the logo, the six house sigils, the event icons, and the Discord and social images. The art itself is in [`art/`](../art/). Open [`art/index.html`](../art/index.html) in a browser to see every piece with its PNG exports.

All art in this pack is original and was drawn for the Realm of Ostreval. <!-- trademark-guard-ok -->It uses no artwork, logos, fonts or trademarks from Reign of Kings, Game of Thrones or any other franchise.<!-- /trademark-guard-ok --> Keep it that way: see [Originality](#originality).

`art/palette.json` is the source of truth for colours. `node art/tools/build.mjs check` fails if this page, an SVG and that file disagree.

---

## Colour

### Brand palette

The brand colours are the same tokens the Chronicle overlay uses (`chronicle/public/assets/theme.css`). That way the website, stream overlay, Discord and printed material all match.

| Swatch | Name | Hex | CSS token | Use |
|---|---|---|---|---|
| Iron | Iron 950 | `#0c0d0f` | `--iron-950` | Deepest shadow, the emblem's outer rim, raven black |
| Iron | Iron 900 | `#131417` | `--iron-900` | **Default dark background**, sigil rims |
| Iron | Iron 800 | `#1b1c20` | `--iron-800` | Panels and cards on dark |
| Iron | Iron 700 | `#26282d` | `--iron-700` | Raised panels, borders on dark |
| Iron | Iron 600 | `#34363c` | `--iron-600` | Hairlines, emblem highlight |
| Iron | Iron 200 | `#a3a6ad` | `--iron-200` | Muted text on dark |
| Parchment | Parchment | `#ecdfbf` | `--parchment` | **Default light background**, body text on dark |
| Parchment | Parchment 2 | `#e0cfa4` | `--parchment-2` | Second stop of parchment gradients, secondary text on dark |
| Parchment | Parchment edge | `#b99a62` | `--parchment-edge` | Rules and borders on parchment, banner cords |
| Ink | Ink | `#2a1c0f` | `--ink` | Body text on parchment, light-mode wordmark, icons on light |
| Ink | Ink soft | `#5b4630` | `--ink-soft` | Secondary text on parchment |
| Gold | Ember | `#d6a043` | `--ember` | **Brand gold**: rings, rules, the wordmark on dark, icons on dark |
| Gold | Ember hot | `#f4c96d` | `--ember-hot` | Gold highlights, sparks, links on dark house panels |
| Gold | Ember pale | `#fbe3a2` | (none) | Top stop of the gold gradient only |
| Gold | Ember deep | `#9c6a1e` | `--ember-deep` | Bottom stop of the gold gradient. Gold on parchment, **large sizes only** |
| Accent | Blood | `#8b2b22` | `--blood` | Warnings, broken oaths, rebellion, claims |
| Accent | Moss | `#4d6b3a` | `--moss` | Success, treaties signed, captives released |

**The gold gradient** used for the wordmark and emblem runs top to bottom: `#fbe3a2` 0%, `#f4c96d` 35%, `#d6a043` 60%, `#9c6a1e` 100%. It is the same gradient the overlay uses for `.gold-text`.

**Event tones.** The overlay gives every Chronicle event a tone. Use the matching brand colour wherever an event appears: *gold* `#d6a043`, *blood* `#8b2b22`, *moss* `#4d6b3a`, *iron* `#a3a6ad`.

**Contrast.** These pairs are checked by the build (WCAG 2 ratios):

| Text | On | Ratio needed | Use |
|---|---|---|---|
| `#ecdfbf` parchment | `#131417` iron | 7:1 (AAA) | Body text on dark |
| `#a3a6ad` iron 200 | `#131417` iron | 4.5:1 (AA) | Muted text on dark |
| `#d6a043` ember | `#131417` iron | 4.5:1 (AA) | Gold text and icons on dark |
| `#2a1c0f` ink | `#ecdfbf` parchment | 7:1 (AAA) | Body text on parchment |
| `#5b4630` ink soft | `#ecdfbf` parchment | 4.5:1 (AA) | Secondary text on parchment |
| `#8b2b22` blood | `#ecdfbf` parchment | 4.5:1 (AA) | Warnings on parchment |
| `#9c6a1e` ember deep | `#ecdfbf` parchment | 3:1 | **Large text (24px+) and icons only** |

Ember `#d6a043` on parchment is only about 1.8:1. **Never** set gold text on parchment. Use ember deep for large text, or ink.

### House colours

Each great house has a **field** colour and a **metal** colour.

- The **field** is the exact dye the Chronicle overlay gives that house name. The overlay hashes the name in `chronicle/public/assets/common.js` (`dyeFor`), so the colour comes from the name and cannot be chosen. The build recomputes the hash and fails if `palette.json` drifts from it.
- The **metal** is the colour of the charge (the beast or object on the field). It matches the house description in [`community/lore.md`](community/lore.md).
- The **Discord role** colour is a lighter version of the house hue. It is readable at 4.5:1 or better on Discord's dark theme (`#313338`). On the light theme (`#ffffff`) these colours are only about 2.1 to 2.6:1. That is a Discord limit, so do not try to fix it by darkening the colours.

| House | Sigil | Field (overlay dye) | Light / dark shade | Metal | Discord role |
|---|---|---|---|---|---|
| Varrow | Iron Stag | `#4a2347` plum | `#674664` / `#2e162c` | `#9aa0a8` iron grey | `#c58fc0` |
| Ashgrove | White Oak | `#7a3a1a` rust red | `#8f5a3f` / `#4c2410` | `#e8dfc8` bone white | `#e08a5c` |
| Corvane | Black Raven | `#2c3b42` slate | `#4e5a60` / `#1b2529` | `#c9ced4` silver | `#8fb0bf` |
| Dunmere | Drowned Bell | `#5a5a22` olive | `#747445` / `#383815` | `#e0b56a` tarnished bronze | `#b8b85a` |
| Halloran | Ember Hound | `#3a2a1a` dark umber | `#5a4c3f` / `#241a10` | `#e27a2c` ember orange | `#ec8a3c` |
| Merrin | Silver Eel | `#24472d` deep green | `#47644f` / `#162c1c` | `#c9ced4` silver | `#6fbf85` |

The light and dark shades are the overlay's `--dye-light` and `--dye-dark` values. Sigils, banners and shields use them for the field gradient. A few charges also use detail colours. These are listed under `extra` in `palette.json`: Dunmere's bronze shadow `#6e4c22`, highlight `#e2c38a` and sea-foam waves `#c9d1b0`; Halloran's ember shadow `#b8561b` and flame `#f4a547`; and Merrin's net `#9fb3a4`.

---

## Typography

Both families are free under the **SIL Open Font License 1.1** (OFL). Load them from Google Fonts, or bundle the font files together with their OFL licence text.

| Role | Family | Weights | Notes |
|---|---|---|---|
| Display: headings, labels, buttons, kickers | **Cinzel** | 600, 700 | Capitals only (Cinzel has no lowercase). Track small labels out by +0.08 to +0.2 em. |
| Decorative: one big word, at most once per screen | Cinzel Decorative | 700 | The overlay uses it for the throne card. Do not use it for body text or anything below 32px. |
| Body: prose, descriptions, footers | **EB Garamond** | 500, *500 italic* | Use italic for house words, e.g. *"The Tide Returns."* Use 18px or larger on screen. |

Fallback stacks, matching `theme.css`:

```css
--font-display: 'Cinzel', 'Trajan Pro', 'Cormorant SC', 'Palatino Linotype', 'Book Antiqua', Georgia, serif;
--font-body: 'EB Garamond', 'Garamond', 'Palatino Linotype', 'Book Antiqua', Georgia, serif;
```

The wordmark is **outlined**: the letters are plain SVG paths taken from Cinzel Bold (REALM) and Cinzel SemiBold (OSTREVAL) by `art/tools/wordmark.mjs`, so they render the same with or without the font installed. The OFL allows artwork made with the font. The outlines are not font software, and no font files are committed to the repo. Never retype the logo in a live font. Use the files.

---

## Logo

The logo has two parts:

- **The emblem**: the Old Throne of black stone with the crown resting in its arch. *The crown belongs to the seat, not the blood* (the first article of the Hearth Charter).
- **The wordmark**: REALM, with OSTREVAL set small between two gold rules.

| File | Use on |
|---|---|
| `art/logo/realm-logo-horizontal-dark.svg` | **Main logo.** Website headers, overlays, anything wide and dark |
| `art/logo/realm-logo-horizontal-light.svg` | The same, on parchment or white |
| `art/logo/realm-logo-stacked-dark.svg` / `-light.svg` | Square or tall spaces: posters, splash screens, avatars with room |
| `art/logo/realm-wordmark-dark.svg` / `-light.svg` | Where the emblem is already shown nearby |
| `art/logo/realm-emblem.svg` | Avatars, app icons, watermarks, the Discord icon |
| `art/logo/realm-emblem-mono.svg` | One colour (`currentColor`): embossing, stamps, laser-cut, one-ink print |
| `art/logo/realm-favicon.svg` | Browser tabs and anything below 48px |

The *dark* versions are for dark backgrounds and have a gold wordmark. The *light* versions are for light backgrounds and have an ink wordmark. The emblem is a self-contained badge with its own iron disc, so it works on either.

### Clear space

Let **x** be the height of the capital letters of REALM. Keep **½ x** clear on every side of a lockup or wordmark. Keep clear space equal to **¼ of the diameter** around the emblem used alone. No other text, edges or images may enter this space.

In the 736 × 200 horizontal lockup, x is 89 units, so the clear margin is about 44 units: roughly a fifth of the lockup's height.

### Minimum sizes

| Version | Smallest on screen | Smallest in print |
|---|---|---|
| Horizontal lockup | 280px wide | 50 mm wide |
| Stacked lockup, wordmark | 200px wide | 35 mm wide |
| Emblem | 48px | 12 mm |
| Below 48px | Use `realm-favicon.svg` (the event crown on iron) | n/a |

Below these sizes the OSTREVAL line drops under 8px and stops being legible.

### Do

- Use the supplied files, scaled proportionally.
- Use the dark versions on iron, dark photos or video. Use the light versions on parchment or white.
- Give the logo its clear space. Centre it, or align it to a strong edge.
- Use the one-colour emblem when only one ink is available.

### Don't

- Don't retype, re-space or recolour the wordmark, or set it in another font.
- Don't stretch, skew, rotate, outline or add drop shadows or glows to the logo. The emblem already has its own rim.
- Don't put the gold wordmark on parchment or the ink wordmark on dark. Both fail contrast.
- Don't put the logo on busy screenshots without a dark scrim behind it (iron 900 `#131417` at 70% or more).
- Don't swap the crown, throne or rings for anything else, or combine the emblem with a house sigil into a new mark.
- Don't use the emblem as a house sigil. It belongs to the realm, not to a house.

---

## House sigils, banners and shields

Every house has three files. Each is drawn on its overlay dye with an iron rim and a gold ring:

| Variant | File | Size | For |
|---|---|---|---|
| Sigil (roundel) | `art/sigils/<house>.svg` | 256 × 256 | Avatars, Discord emoji, Chronicle cards, thumbnails |
| Banner | `art/banners/<house>.svg` | 200 × 340 | Website headers, stream lower-thirds, posters |
| Shield | `art/shields/<house>.svg` | 240 × 280 | House pages, tournament brackets, merch mock-ups |

The charges follow [`community/lore.md`](community/lore.md):

- **Varrow**: an iron-grey stag, rearing, on plum.
- **Ashgrove**: a white oak, roots and branches spread, on rust red.
- **Corvane**: a black raven with a silver key in its beak, on slate.
- **Dunmere**: a bronze bell sunk beneath three wavy lines, on olive.
- **Halloran**: a running hound wreathed in embers, on dark umber.
- **Merrin**: a silver eel coiled through a broken net, on deep green.

The sigil files are hand-drawn. The banners and shields are built from the sigil's `<g id="charge">` by `art/tools/build.mjs`, so if a charge changes, edit only the sigil.

**Player-founded houses.** These six sigils belong to whichever group holds that house name (see the claim sign-up in `community/lore.md`). Don't hand them to other houses. A new house can use the banner and shield shapes with its own overlay dye and its own charge. Add it to `palette.json`, draw `art/sigils/<name>.svg` with a `<g id="charge">` on a 200 × 200 grid, and run the build.

---

## Event icons

Icons for Chronicle events live in `art/icons/` (one SVG each) and `art/sprite/icons.svg` (all of them as `<symbol>`s).

| Icon | Events |
|---|---|
| `crown` | coronation, heir named, succession |
| `abdication` | abdication |
| `claim` | claim declared, accusation, blood claim |
| `rebellion` | rebellion started and ended, trial by combat, the king's hunt |
| `house` | house founded, dynasty founded, blood restored |
| `oath` / `oath-broken` | oath sworn / oath broken |
| `treaty` | treaty signed and broken, truce broken, verdict |
| `contract` | contract posted and ended |
| `coins` | ransom paid, contract paid, treasury and trade events |
| `ransom` | ransom set (the ransom-glass), season started |
| `released` | captive released, pardon |
| `decree` | decree, laws, realm events, rumours |
| `trophy` | season ended, tournament champion, titles |

The full mapping for every current event type is in `art/icons/event-map.json`. It is advice for anyone building UI and adds no event types. The overlay keeps its own inline icon set.

Rules for drawing a new icon:

- **24 × 24 grid**, with 2px of padding (art inside 2 to 22).
- **1.75 stroke**, round caps, round joins, `fill="none"`. Fill only small solid details.
- **`currentColor` only**, so an icon takes the colour of the text around it (ember on dark, ink on light, or the event's tone).
- Draw at 24px. Use it at 16, 24, 32 or 48px. Check it at 24px on both iron and parchment (`art/png/icons/contact-sheet.png`).
- Add a `<title>`, an entry in `event-map.json`, and run the build.

```html
<svg class="icon" aria-hidden="true"><use href="art/sprite/icons.svg#realm-icon-crown"/></svg>
```

---

## Discord

Upload the **PNGs** from `art/png/discord/`, not the SVGs.

| Asset | File | Size | Notes |
|---|---|---|---|
| Server icon | `server-icon.png` | 512 × 512 | Shown as a circle. The emblem is round, so nothing is cut off. |
| Server banner | `server-banner.png` | 960 × 540 | Shown small above the channel list. The lockup is large for that reason. |
| Invite splash | `invite-splash.png` | 1920 × 1080 | Background of invite pages. Keep the important art in the centre. |
| Emoji | `art/png/sigils/<house>-128.png` | 128 × 128 | One per house, e.g. `:varrow:` |
| Role colours | see [House colours](#house-colours) | n/a | Use the *Discord role* column, not the field dye. |

UNVERIFIED: the sizes and safe areas follow Discord's published recommendations for server banners and invite splashes. They have not been tested on a live server, and those features need a server boost level that the community may not have.

---

## Social cards

`art/social/card-template.svg` is a 1200 × 630 card for link previews (Open Graph and Twitter-style cards), Discord announcement images and stream thumbnails. It has slots for a kicker, a title, a subtitle, a footer, and a house sigil (or the emblem). To make a card:

```
node art/tools/build.mjs card --house dunmere --kicker "Claim declared" \
  --title "Dunmere raises a claim at the Hearth" \
  --subtitle "The Lawful Hours open on Saturday at 20:00." \
  --footer "The Chronicle of Ostreval" --out card.svg --png card.png
```

The card text uses live Cinzel and EB Garamond, so always post the PNG. The `--png` step loads the fonts and fails if they cannot be loaded, so a card never goes out set in a fallback font. Titles are wrapped by measured Cinzel width. They use two lines at 66px, or drop to 54px and three lines when they need more room, and end with an ellipsis if even that is not enough. Subtitles wrap to two lines. The export fails if any text would run into the sigil column, for example a single very long word. Keep titles under about 50 characters.

Examples: `art/png/social/card-example-coronation.png` and `card-example-treaty-broken.png`. Both are marked "example card, not a real event".

---

## Voice (short version)

Follow [`community/lore.md`](community/lore.md). In short: grim but readable. Use the realm's own terms (*the Old Throne*, *the Hearth*, *the Lawful Hours*, *the Chronicle*). Don't promise mechanics the plugins don't have. Use only original names.

## Originality

- All art here was drawn for this project. Do not import or trace other games' or shows' artwork, logos, icons or fonts.
- <!-- trademark-guard-ok -->Don't use the names, logos or key art of Reign of Kings, Game of Thrones or their publishers in Realm branding. The game may be named in plain text where players need to know what to install, and nowhere in the logo.<!-- /trademark-guard-ok -->
- `node art/tools/build.mjs check` scans `art/` and this page for well-known franchise names and fails if it finds one.
- Licence: the repository has no licence file yet. Until the maintainers choose one, this art is offered for the Realm community's own use. Decide on a licence (for example CC BY-SA 4.0 for the art) before anyone outside the project reuses it.
