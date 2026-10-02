# Realm analytics

`analytics/` is a Node command-line tool with no dependencies. It reads the day files that the [RealmStats](../plugins/docs/RealmStats.md) plugin writes and builds **one self-contained HTML dashboard** from them. The page has inline CSS, inline SVG charts and a few lines of inline JS for tooltips. It makes no network requests, so it opens offline and can be attached to a message.

![Dashboard built from sample data](../docs/img/analytics.png)

*Sample data from `tools/make-sample.js`, not a real server.*

The dashboard shows:

- **Headline numbers:** unique players, new players, average daily active, peak online and when it happened, median session, and day-1 and day-7 retention.
- **Players online over time:** the average and the peak per bucket, with a crosshair tooltip.
- **Daily active players:** returning and new players, as stacked bars.
- **Peak hours:** a heatmap of average players online by weekday and hour, in the time zone you choose.
- **Retention by cohort:** D1 and D7 for every first-seen day.
- **House activity:** player-hours per house, plus players, sessions, PvP kills and deaths. Houses with too few players are grouped.
- **Deaths by cause, session length and joins by hour.**
- **Data notes:** skipped or damaged files, missing days, degraded days, salt changes, and anything the plugin dropped because of a cap.

Every chart has a "Show as a table" view. The page follows the system light or dark theme and has a theme toggle. It works at phone width.

## Use

You need Node 18 or newer. Run the tool on a **copy** of the data folder, or on the live folder (the tool only reads it):

```
node analytics/bin/realm-analytics.js --data <oxide/data/RealmStats> [options]
```

| Option | Default | Meaning |
|---|---|---|
| `--data <folder>` | required | The folder that holds `day-YYYY-MM-DD.json`. |
| `--out <file>` | `realm-analytics.html` | The dashboard to write. The tool **refuses** to write inside the data folder. |
| `--json <file>` | | Also writes the aggregated numbers as JSON. |
| `--days <n>` | 30 | The window length, from 1 to 730 days. |
| `--until <YYYY-MM-DD>` | newest day | The last UTC day of the window. |
| `--tz <offset>` | UTC | The time zone for hours: `+01:00`, `-5` or `-300` (minutes). Days stay UTC days, because the plugin cuts files at UTC midnight. |
| `--min-house <n>` | 3 | Houses with fewer active players than this are grouped as "Smaller houses". |
| `--server <label>` | | Reads only the files whose `Server` matches (`ServerLabel` in the plugin config). |
| `--title <text>` | `Realm analytics` | The page heading. |

Exit codes:
- `0`: the dashboard was written.
- `1`: no readable data, or the folder cannot be read.
- `2`: bad options.

## Definitions

- **Active:** a key seen online that UTC day.
- **New:** a key seen for the first time that UTC day.
- **D1 / D7:** classic day-N retention: of the players new on day *d*, the share active on day *d+1* or *d+7*. A cohort is scored only when:
  - day *d+N* has a file,
  - both days carry the same salt id,
  - and neither day is `Degraded`.

  Otherwise the cell shows `-` and the cohort is left out of the weighted averages.
- **Online / peak:** the players online at each sample, and the most online at once since the previous sample.
- **Several files for one date** (for example `day-2026-09-08.json` and the recovery file `day-2026-09-08-r1.json`) are merged:
  - sets are joined,
  - counters are added,
  - samples at the same time keep the larger value.
- **Damaged files:** a file that cannot be parsed, or has the wrong shape, is skipped and listed under Data notes. It is never changed.

## Privacy

The dashboard shows only totals. Player keys are never printed in the page. They do appear in the `--json` output only as counts, never as lists. House names are escaped, because players choose them. The data folder itself holds pseudonymous keys and should be treated as private server data. See *What it records* in the [plugin doc](../plugins/docs/RealmStats.md).

## Tests

```
cd analytics
npm test               # node --test: loader, aggregator, renderer, CLI, sample generator, plugin-output integration
npm run test:plugin    # compiles plugins/RealmStats.cs with mocks and runs its behaviour checks (Linux x64, .NET from the compile-check cache)
```

The fixtures are:
- `test/fixtures/small/`: a hand-written set whose expected numbers are worked out in the test. It includes a recovery file, a corrupt file and a `state.json` that must be ignored.
- `test/fixtures/sim/`: 14 simulated days **written by the real plugin code**. To regenerate it, run `RS_EXPORT=$PWD/analytics/test/fixtures/sim analytics/plugin-tests/run.sh`. Its keys change on each run, because the salt is random.

## Sample dashboard and screenshot

```
cd analytics
npm run sample         # writes sample-data/ and sample-dashboard.html (both git-ignored)
npm run screenshot     # dev only: needs Playwright + Chromium; writes ../docs/img/analytics.png
```

`tools/screenshot.js` looks for Playwright in `PLAYWRIGHT_MODULE`, then `playwright`, then `/opt/node-tools/node_modules/playwright`, and for Chromium at `CHROMIUM` or `/opt/pw-browsers/chromium`. The screenshot fails if the page makes any non-file request.

## Layout

| Path | What it is |
|---|---|
| `bin/realm-analytics.js` | The CLI. |
| `lib/load.js` | Reads, validates and merges day files (read-only). |
| `lib/aggregate.js` | Pure aggregation: retention, heatmap, series, houses, deaths, sessions and data notes. |
| `lib/charts.js` | Inline SVG chart builders. |
| `lib/render.js` | The HTML page, styles and tooltip script. |
| `tools/make-sample.js` | A deterministic sample data generator. |
| `tools/screenshot.js` | The Playwright screenshot helper (dev only). |
| `plugin-tests/` | Mock-based behaviour tests for `plugins/RealmStats.cs`. |
| `test/` | The Node tests and fixtures. |

Unverified: the dashboard is tested on Linux with Node 22 and rendered in Chromium. It has not been opened on the owner's Windows machine, and it has never been run on data from a live server.
