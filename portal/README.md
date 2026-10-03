# Realm Portal

A static website for the realm, built from the same data files the plugins write. It has no server and no dependencies, and it never uploads anything. You run one command and get a folder of HTML that any static host can serve, or that opens straight from disk with no network.

It wears the Realm art pack (`art/`): the key art of the Old Throne behind the home page, each great house's engraved banner, shield and sigil, an icon for every one of the 42 Chronicle event types, the season medals and the renown title badges.

| Page | What it shows | Built from |
|---|---|---|
| **The Realm** (`index.html`) | The realm's name over the key art, the current monarch under the Old Throne with their house banner and words, the council, players online, the season's medal and its top three houses on a podium, **What comes next** (scheduled realm events, rebellion windows, treaties running out, your own announcements), the latest Chronicle entries, the houses and Heralds' news | `RealmState.json`, `CrownAndConsequences.json`, `RealmSeasons.json`, `RealmEvents.json` (+ its config), `RealmHouses.json`, `RealmChronicle.json`, `docs/community/news-seed.json` |
| **The Chronicle** (`chronicle.html`) | The full history, newest first, filterable by kind, house and text (the filters need JavaScript; without it everything is listed) | `RealmChronicle.json` |
| **Great Houses** (`houses.html`, `houses/<name>.html`) | Season standings, then a card for every house with its banner in the overlay's colour. Each house page shows its words, seat and history from the lore, leader and officers, liege and vassals, treaties (active and lapsed), marks for broken oaths and treaties, its season points and honours, its monarchs, and its Chronicle entries | `RealmHouses.json`, `RealmState.json`, `RealmSeasons.json`, `docs/community/lore.md` |
| **Hall of Kings** (`kings.html`) | Every reign with its house's sigil, its length, how it ended, and decrees and rebellions during it. Records (longest reign, most crowned house, most faithless house), the **Titles of renown** (every RealmRenown title badge, lit with its holder once the Chronicle names one) and past seasons with their medals and champions | `RealmLegends.json` (kept across wipes). Without it, reigns are rebuilt from coronations and abdications in the Chronicle |
| **How to play**, **Rules**, **Lore** | `docs/community/how-to-play.md`, `rules.md`, `lore.md`, rendered with a table of contents | `docs/community/*.md` |
| **Streamers** | Your listed creators, then the streamer kit | `portal.config.json`, `docs/community/streamer-kit.md` |
| **Download** | The player app download (your link), requirements, and how to join by hand (address in one box, port in the other) | `portal.config.json` |
| `status.json` | King, house, online count, last event and the next three events, for bots, widgets or your own page | All of the above |
| `feed.xml` | An Atom feed of the last 50 Chronicle entries (works in feed readers and in Discord/RSS bots) | `RealmChronicle.json` |

Screenshots (sample data plus `sample-data/showcase`): `docs/img/portal-home.png`, `portal-chronicle.png`, `portal-houses.png`, `portal-house.png`, `portal-kings.png`, `portal-play.png`, `portal-download.png`, `portal-mobile.png`, `portal-kings-mobile.png`.

## Build it

You need Node.js 20 or newer. Realm Steward does not need it, but the portal generator does.

```
cd portal
npm run build                      # sample data -> portal/dist (to see what it looks like)
```

For your real server on Windows, point it at the test copy's data folder:

```
node build.mjs --data "G:\RealmTest\server\oxide\data"
```

The generator finds `oxide\config\RealmEvents.json` next to the data folder by itself, for the weekly event schedule. If it is somewhere else, add `--oxide-config <folder>`. The output goes to `portal\dist`; use `--out <folder>` to put it elsewhere.

To keep the site fresh while the server runs, add `--watch`. It checks the data files every 60 seconds (`--watch 30` for 30) and rebuilds only when something changed:

```
node build.mjs --data "G:\RealmTest\server\oxide\data" --watch
```

The pages also re-read `status.json` every minute when they are served over http(s), so the online count and the king refresh without a page reload after each rebuild.

**Safety.** The generator only reads the data folder. It writes only to its output folder, and it refuses to replace a non-empty folder that it did not create (it marks its own with a `.realm-portal` file). A build is written to a staging folder first and swapped in, so a host serving the folder never sees a half-written site.

## Configure it

Copy `portal.config.example.json` to `portal.config.json` (next to `build.mjs`; it is picked up automatically, or pass `--config <file>`) and fill in:

| Key | Meaning |
|---|---|
| `realmName`, `kicker`, `tagline` | The site's name and the lines under it |
| `download.url` | The link to the player installer (`Realm-Setup-<version>.exe`) wherever you host it, for example a GitHub release. **Placeholder until you set it:** example.* links and anything that is not https are ignored, and the page says "Download coming soon" |
| `download.version`, `download.sha256` | Shown next to the button so players can check the file (`Get-FileHash Realm-Setup-0.3.0.exe` in PowerShell gives the SHA-256) |
| `server.address`, `server.port` | The public address to type in the game's direct-connect box. **Address only:** `host:port` is rejected, because typing that into the address box is exactly what makes the game say "Unable to resolve host name" |
| `discordInvite` | Your Discord invite link (https only) |
| `siteUrl` | The site's public https address. Used for absolute links in `feed.xml`, the canonical link of each page and the link-preview images. **Set it before you share links**: Discord and other sites only show a preview image from an absolute address |
| `streamers` | `[{ "name", "url", "platform", "house", "note" }]`, https links only |
| `events` | Your own announcements for What comes next: `[{ "title", "at", "end", "detail" }]` with ISO-8601 UTC times |
| `staleAfterMinutes` | When the "players online" figure counts as old (default 10) |

`portal.config.json` holds no secrets, but it is your server's public address. Commit it only if you want it in the repo.

## Host it (you do this; nothing here deploys)

The output is plain files with relative links, so it works from any folder or sub-path.

- **GitHub Pages.** Create a repository (or use a `gh-pages` branch) and put the **contents** of `dist/` at its root. In the repository's **Settings > Pages**, choose that branch. `dist/.nojekyll` is included so GitHub serves the files as they are. To update, rebuild and push the new contents.
- **Netlify, Cloudflare Pages and similar.** Drag the `dist` folder onto the dashboard's manual deploy, or connect a repository that contains it. There is no build command; the publish directory is the folder itself.
- **Any web server or object storage** (nginx, Caddy, IIS, S3 or R2 with website hosting). Copy the folder. `404.html` is there for hosts that use it.
- **Just looking.** Open `dist/index.html` in a browser. Everything works except the once-a-minute `status.json` refresh, which browsers block on `file://`.

To keep a hosted copy fresh, run the generator with `--watch` on the server PC and sync `dist/` to your host with that host's own tool (for example a scheduled `git push` of the folder, or `rclone sync`). That sync step is yours to choose and is not included here.

## Art and link previews

- **Where the art comes from.** `assets/art` and `assets/fonts` are copies of `art/` and of the launcher's OFL fonts, made by `node scripts/sync-art.mjs` (the same script fills `chronicle/public/assets` and `streamkit/public/assets`). Edit the art in `art/`, never the copies, then run the script. A test fails when a copy has drifted (`node scripts/sync-art.mjs --check` says which).
- **Great houses and player houses.** The six great houses of the lore (Varrow, Ashgrove, Corvane, Dunmere, Halloran, Merrin) show their drawn banner, shield and sigil. A house a player founds keeps its dyed banner with its initial, so it never borrows a great house's charge.
- **Icons.** Each page carries the event icons it uses as inline `<symbol>`s, so they work from `file://` and under the CSP.
- **Badges.** Season events show the season medal (I to IV, then again from I). A `title_earned` entry whose title is one of RealmRenown's default titles shows that title's badge.
- **Link previews.** Every page has Open Graph and Twitter card tags: the 1200 x 630 key art card (`assets/art/og/realm-card.png`) for most pages, and the house's sigil for a great house's page. With no `siteUrl` the image address is relative, which browsers accept but Discord ignores. **UNVERIFIED:** how Discord, Slack and other unfurlers show the cards; paste a page link into a Discord channel once the site is hosted with `siteUrl` set.
- **Home screen.** `manifest.webmanifest` lets a phone add the site with the Realm emblem.

## What gets published, and what never does

- **Published:** the public names already in the Chronicle and in `/house info`: house names and sigils, leaders and officers, monarchs, member *counts*, treaties, oaths and their breaking, season points, and your docs.
- **Never:** Steam ids. `RealmHouses.json` member ids, `CrownAndConsequences.json` king and council ids and every other id are dropped by the readers in `lib/data.mjs`, and a member "name" that is really an id (the plugin falls back to the id when it has no name) is dropped too. No player locations, inventories or IP addresses are in the data files, and the portal reads nothing else. The tests check that no Steam-id-shaped number reaches any page.
- **Hardening:** every page carries a strict Content-Security-Policy (no inline script or style, no third-party requests; fonts are the OFL Cinzel and EB Garamond files shipped with the launcher, copied into `assets/fonts`; the art is SVG and PNG in `assets/art`). All data and Markdown text is escaped. Links in Markdown go only to https or to the portal's own pages; repo paths are shown as text.

## How sure the details are

- The data shapes come from the plugin source in `plugins/` (`RealmChronicle.cs`, `RealmHouses.cs`, `CrownAndConsequences.cs`, `RealmSeasons.cs`, `RealmEvents.cs`). The readers are tolerant: a missing file is a warning, a half-written file is skipped, and unknown fields are ignored.
- **Season points** are computed with RealmSeasons' **default** weights (`ScoreWeights`). If you change the weights in `oxide/config/RealmSeasons.json`, the portal's numbers will differ from `/season standings` until the generator reads that config. UNVERIFIED on a live server.
- **Event schedule** times are computed from `oxide/config/RealmEvents.json` in UTC, the way the plugin documents it. Whether a slot actually runs on the night (clashes are skipped by the plugin, and a server that is down misses it) is not known to the portal.
- **Reigns** come from `RealmLegends.json` when RealmSeasons runs. Otherwise they are rebuilt from the Chronicle, so reigns older than its retention (500 events by default) are missing.
- Nothing here has been served from a real public host from this environment; the pages were checked as files in headless Chromium at desktop and phone width (`npm run screens`: no errors, every image and icon loads, no sideways scrolling). UNVERIFIED on real phones: open the hosted home page on a phone and check the hero, the monarch's plate and the podium.

## Develop

```
npm test                            # generator tests (node:test, no network)
npm run screens                     # regenerate docs/img/portal-*.png in headless Chromium (Playwright) and check every page
node scripts/sync-art.mjs           # copy art/ and the fonts into portal, chronicle and streamkit (--check to compare only)
```

| File | Role |
|---|---|
| `build.mjs` | Command line: options, one build or `--watch` |
| `lib/data.mjs` | Tolerant, privacy-filtering readers for every data file |
| `lib/model.mjs` | Houses, reigns, records, upcoming events, event labels (the same as the overlay) |
| `lib/markdown.mjs` | A small Markdown renderer that escapes everything |
| `lib/pages.mjs` | Page templates, `status.json`, `feed.xml`, `manifest.webmanifest`, house colours |
| `lib/art.mjs` | Which art file belongs to an event, a house, a season or a title; the per-page icon sprite |
| `lib/generate.mjs` | Reads, renders, stages and swaps the output folder |
| `assets/` | `portal.css`, `portal.js` (times, filters, status refresh), `art/` and `fonts/` (copies, see above) |
| `scripts/` | `sync-art.mjs` (art and font copies), `screens.mjs` (screenshots and page checks) |
| `sample-data/` | Sample `RealmHouses`, `CrownAndConsequences`, `RealmSeasons`, `RealmLegends`, `RealmEvents` files that match `chronicle/sample-data`. `showcase/` adds six Chronicle entries (two renown titles, a law, a rumour, coin, a contract) for the screenshots |

House colours use the same palette and hash as the overlay (`chronicle/public/assets/common.js`) and the Discord herald (`launcher/lib/discord.js`), and a test keeps the three in step.
