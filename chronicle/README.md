# Realm Chronicle service

A small local web service that turns the data files written by the `RealmChronicle` Oxide plugin into:

- `GET /api/state`: the crown, the houses, and the online count
- `GET /api/events`: the chronicle feed
- `/overlay`: a transparent 1920x1080 OBS browser source showing the crown, house banners, a ticker and animated proclamations
- `/realm`: a public chronicle page with the crown, the great houses and a filterable timeline

It has no dependencies; it uses only Node 20+ built-ins (tested on Node 22). It only ever **reads** the data directory and never writes to it.

![Overlay preview](../docs/img/overlay-preview.png)

## Running it on the server PC (Windows)

```powershell
cd <repo>\chronicle
# default data dir: G:\RealmTest\server\oxide\data  (the test copy made by server\New-TestServer.ps1)
node server.js
# or point it elsewhere, e.g. if Oxide put its folder under Saves\:
$env:REALM_DATA_DIR = "G:\RealmTest\server\Saves\oxide\data"
node server.js
```

Then open `http://127.0.0.1:8787/realm`.

**Check the data path.** Oxide creates `oxide\` under the server's working directory, or under the path given to `-oxide.directory` (docs/oxide-rok-api.md, section 6.4). One community note says it goes under `Saves\oxide\` instead (docs/server-reference.md, section 6). Point `REALM_DATA_DIR` at whichever folder actually contains `RealmChronicle.json` once the plugin has run. `GET /healthz` reports `missing` while the service cannot find the files.

| Option | Env var | Flag | Default |
|---|---|---|---|
| Data dir | `REALM_DATA_DIR` | `--data <dir>` | `G:\RealmTest\server\oxide\data` (the test copy) |
| Bind address | `HOST` | `--host` | `127.0.0.1` (local only) |
| Port | `PORT` | `--port` | `8787` |
| Stale after (s) | `REALM_STALE_SECONDS` | `--stale` | `180` (`0` turns the check off) |

The service binds to loopback by default, so no firewall or router changes are needed. Binding to anything else prints a warning. Do that only on purpose, for example behind a reverse proxy, once the public page is wanted.

### Try it with the sample data

```bash
npm run sample      # node server.js --data sample-data --stale 0
npm test            # node:test smoke tests against sample-data
```

## OBS setup

1. Add a **Browser** source with URL `http://127.0.0.1:8787/overlay`, width `1920` and height `1080`.
2. Leave the custom CSS empty. The page background is already transparent.
3. The stage scales to fit any source size with a 16:9 aspect.

Overlay query options:

| Param | Effect |
|---|---|
| `banners=N` | Show up to N house banners (default 6, max 8); `0` hides them |
| `ticker=0` / `crown=0` / `proclaim=0` | Hide the ticker, the crown plate, or the proclamation cards |
| `hold=S` | How long each proclamation stays up, in seconds (default 12) |
| `poll=S` | Event poll interval, in seconds (default 3) |
| `replay=1` | Proclaim the latest event on load (useful for positioning) |
| `bg=1` | Dim backdrop for previewing in a normal browser. Do not use it in OBS. |

New events appear as an unrolling parchment proclamation with a wax seal and embers, and are added to the scrolling ticker. A change of king makes the crown plate flare. If the plugin stops refreshing `RealmState.json`, the crown plate shows "The ravens are late".

## API

`GET /api/state` returns the `RealmState.json` contract, with only whitelisted fields, plus `stale`:

```json
{ "king": "Aldric Varrow", "house": "Varrow", "since": "2026-09-30T19:42:10Z",
  "houses": [{ "name": "Varrow", "sigil": "Iron Stag", "liege": null, "members": 9 }],
  "online": 23, "maxPlayers": 40, "updated": "2026-10-01T21:05:00Z", "stale": false }
```

`GET /api/events` returns an array of `{id, ts, type, title, detail, actors[]}`:

- With no `since`, it returns the latest `limit` events (default 50, max 500), oldest first.
- `?since=<id>` returns events with `id > since`, oldest first, up to `limit`. Poll with the last id you saw.
- A non-integer `since` returns 400.
- Unknown event types and extra fields are dropped. Timestamps without a zone are read as UTC.

If a file is half-written or unreadable, the service keeps serving the last good copy and adds an `X-Realm-Data-Warning` response header. A missing file gives empty but valid responses.

## Files

```
server.js               HTTP server + API (node: built-ins only)
public/overlay.html     OBS overlay       public/assets/overlay.{css,js}
public/realm.html       public page       public/assets/realm.{css,js}
public/assets/theme.css shared tokens (iron, parchment, ember gold) and banner styles
public/assets/common.js shared helpers: event-type labels/icons, banner dye, DOM builder
sample-data/            fixtures in the plugin's file format
test/server.test.js     smoke tests
```

The fonts are Cinzel, Cinzel Decorative and EB Garamond from Google Fonts. Offline, the pages fall back to Trajan, Palatino or Georgia. The Content-Security-Policy allows only this origin plus Google Fonts.

## The plugin side

`plugins/RealmChronicle.cs` writes both files through `Interface.Oxide.DataFileSystem`:

- `oxide/data/RealmChronicle.json`
- `oxide/data/RealmState.json`

The state is refreshed every `StateRefreshSeconds` (30 by default), and also on joins, leaves, plugin loads and throne changes. The event log is capped at `MaxEvents` (500 by default). Players can read recent entries in game with `/chronicle [n]`. Other plugins log events with `RealmChronicle.Call("Log", type, title, detail, actors)`.
