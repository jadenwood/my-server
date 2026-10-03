# RealmChronicle: the realm's public record and its flood budget

`plugins/RealmChronicle.cs` (Oxide 2.0.3867, C# 3). It keeps the Chronicle event log and the realm snapshot that the overlay, the public page, the portal, the bot and the Discord herald read. The event types and the files are described in [`docs/realm-systems.md`](../../docs/realm-systems.md) and the header of the `.cs` file; `/chronicle [count]` shows the latest lines in chat. This guide covers the flood budget and the data safety added on 2026-10-02.

Status: **compile-checked against the real 2.0.3867 DLLs and behaviour-tested against mocks** (`tools/exploit-review/run.sh chronicle`, 27 checks; the real RealmEvents and RealmSeasons log into it through an Oxide-like router). It has never run with the flood budget on a live server.

## Why a flood budget

Every plugin writes into the Chronicle, and several lines are triggered by players: contracts, trades, accusations, titles, rumours, house foundings. Each plugin caps its own lines, but together they still allowed about 80 lines an hour. The file keeps only the last `MaxEvents` (500) lines, so a coordinated flood pushed the realm's history (coronations, rebellions, the season's champion) out in about six hours, and every line also went to the overlay, the portal and Discord.

## How it works

- Each type may write `PerTypePerWindow` lines per `WindowMinutes` (a sliding window). `PerType` overrides the number for one type.
- All foldable types together may write `GlobalPerWindow` lines per window.
- A line over either budget is **folded**: it is counted, not written. Once the burst has been quiet for `SummaryQuietSeconds`, one summary line of the same type is written, for example `42 more contract posted entries`, with the time span and the latest title in its detail and no actors. A type gets at most one summary per window; a burst that never goes quiet gets its summary a window after it began. Pending summaries are written on unload, so no count is lost.
- **Never folded:** `coronation`, `abdication`, `claim_declared`, `rebellion_started`, `rebellion_ended`. These are built in and the config cannot change them. `NeverFold` adds more types; by default it holds the lines other plugins count from the file (RealmSeasons: `treaty_signed`, `treaty_broken`, `oath_broken`; RealmRenown: `tournament_champion`, `hunt_kill`, `truce_broken`), `succession`, `blood_claim`, and the season and event start and end lines, which their own plugins already bound. Never-folded lines do not use up the global budget.
- Summary lines carry no actors, so RealmRenown awards no deed for them, and their titles match none of RealmSeasons' texts, so they score nothing.

With the defaults, eight hours of flooding at 100 lines an hour leaves at most about 40 lines an hour in the file (30 plus one summary per flooded type), and the opening coronation is still there. Without the budget it is pushed out (both are checked by the test).

## What `Log` returns

| Value | Meaning | What a caller should do |
|---|---|---|
| > 0 | The new line's id | Nothing |
| 0 | Rejected: unknown type, or empty title | Fall back to another type (for example `decree`) if it wants |
| -1 | Taken but not written as its own line: a recent duplicate (`DuplicateWindowSeconds`), or folded into a summary | Never retry or fall back |

Before this change a duplicate returned 0. RealmTreasury then wrote the same line again under its fallback type, and RealmRenown stopped chronicling titles until it was reloaded. With -1 neither happens. RealmEvents, RealmSeasons and RealmLaws already treated a 0 for a type they had seen accepted as a duplicate; that still works.

## Config (`oxide/config/RealmChronicle.json`, `FloodBudget`)

| Key | Default | Notes |
|---|---|---|
| `Enabled` | true | false turns the whole budget off |
| `WindowMinutes` | 60 | 1 to 1440 |
| `GlobalPerWindow` | 30 | All foldable types together |
| `PerTypePerWindow` | 8 | Any one type without its own entry in `PerType` |
| `PerType` | `decree` 12, `contract_fulfilled` 12, `rumour` 4, `title_earned` 6 | 0 folds every line of that type into the hourly summary |
| `NeverFold` | see above | Added to the five built-in crown and rebellion types |
| `SummaryQuietSeconds` | 120 | How long a burst must be quiet before its summary is written |

The plugin writes the config back on load, so an existing config file gains these keys with their defaults. If the config file holds its own `PerType` or `NeverFold`, those replace the defaults instead of being merged with them.

## A damaged data file

If `oxide/data/RealmChronicle.json` exists but cannot be read (a power cut can leave it empty, `null` or cut short), RealmChronicle used to start a new chronicle and overwrite the file with the next line, wiping the realm's history. It now logs an error and never overwrites that file. New lines are kept in memory and shown by `/chronicle`. Fix the file, or move it away: once it is gone, the next save writes a new chronicle with the lines kept in memory. RealmSeasons and RealmRenown notice the restarted ids and follow the new file.

## What is UNVERIFIED

- Everything above is mock-tested only. The budget is plain C# over timestamps and needs nothing from the game.
- That Oxide's `DataFileSystem.ReadObject` returns null (rather than throwing) for an empty file. Both cases are handled.
