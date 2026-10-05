# The Saga Pack: Season 1, "The Hollow Crown"

This folder holds the story that runs alongside a season. Players do not need it to play. Stewards (the admins) use it to frame what players do anyway with the plugins: declare claims, swear oaths, break treaties, proclaim laws, send ravens and post contracts. The plugins keep the rules, and the saga gives each week a reason and an ending.

Everything here follows `docs/community/lore.md`. Ostreval, its houses and its places are original to this server. **The plugins are the final word:** if a line here promises something no plugin does, the line is wrong. Report it, or fix it and run the checker.

## What is in the pack

| File | What it is | Who uses it |
|---|---|---|
| [`season-1-the-hollow-crown.md`](season-1-the-hollow-crown.md) | The storyline: premise, the four acts, the mechanic behind each beat, and the branches that depend on what players do | Stewards, writers, streamers |
| [`runsheets/act-1-the-empty-seat.md`](runsheets/act-1-the-empty-seat.md) | Act I (weeks 1-2): pre-flight, season start, the first sitting of the throne | Stewards on shift |
| [`runsheets/act-2-the-charter-tested.md`](runsheets/act-2-the-charter-tested.md) | Act II (weeks 3-4): laws, the court, the treasury, the first tournament | Stewards on shift |
| [`runsheets/act-3-the-lawful-hours.md`](runsheets/act-3-the-lawful-hours.md) | Act III (weeks 5-6): rebellions, the King's Hunt, outlaws, spies, blood claims | Stewards on shift |
| [`runsheets/act-4-the-reckoning.md`](runsheets/act-4-the-reckoning.md) | Act IV (weeks 7-8): the last Crown Night, the Hearth Truce, the season-end ceremony | Stewards on shift |
| [`proclamations.md`](proclamations.md) | 30 ready-to-post herald lines (P01-P30), each with an in-game line and a Discord version | Stewards, Discord moderators |
| [`locations.md`](locations.md) | 13 notable places (L01-L13), described generically so any group can map them onto the world | Stewards, builders, roleplayers |
| [`legends.md`](legends.md) | 10 legend side-quests (Q01-Q10) that players complete with existing commands, each with how it is proven | Players, Stewards |
| [`calendar-8-weeks.md`](calendar-8-weeks.md) | A template for the 8-week calendar, with the weekly rhythm read from the plugins' defaults | Stewards, community managers |
| [`COMMANDS.md`](COMMANDS.md) | **Generated.** Every command the pack uses, which plugin defines it and how settled that plugin is | Stewards (before each act) |
| [`tools/check-saga.mjs`](tools/check-saga.mjs) | The checker that keeps the pack honest (see below) | Whoever edits the pack |

## How to run a season with it

1. **A week before launch**, read the storyline and the Act I run-sheet. Fill in the calendar template with real dates. Run the checker (below).
2. **On launch day**, follow the Act I pre-flight step by step. It sets the season length and name, grants the steward permissions and marks the places in the world.
3. **Each act**, open its run-sheet at the start of every shift. Each one lists, for every day and time (UTC), the exact command to type, the proclamation to post and what to check afterwards. Where the story depends on what players did, the run-sheet branches. Read the state with the listed commands, then follow the matching branch.
4. **After each big moment**, post the matching proclamation from `proclamations.md`. Fill in every `{placeholder}` before posting.

## Where proclamations go

| Channel | How | Notes |
|---|---|---|
| In-game chat as the server | Realm Steward → Court → **Say** (types the line into the admin console without `/`) | `[DEC]` `Console.Submit`, from `docs/admin-console.md`. UNVERIFIED at run time. |
| Server notice (a chat line) | Court → **Notice**, which sends `/notice <text>` | `[DEC]` `Server.Notice` calls `CoreServer.Notice`, which is `BroadcastMessage` with a `[Server]` prefix: a line in every player's chat, not an on-screen banner. Use it for the big moments only. UNVERIFIED at run time. |
| Popup | Court → **Popup**, which sends `/popup <text>` | `[DEC]` A window every player must close. Use it at most once per act. UNVERIFIED at run time. |
| In-game chat as you | Type the line in game chat | Always works. It shows your name, not the server's. |
| Discord | Post the ```` ```discord ```` version by hand in the announcements channel | The Steward's Discord herald (`docs/discord-herald.md`) already relays Chronicle events by itself, so do not repost those. |

Every in-game herald line in this pack is checked so that it can be pasted unchanged into all three console channels. Each line is plain ASCII, at most 300 characters with placeholders filled (the Steward's limit), and has no quotes or apostrophes, because Steward turns those into backticks in `/notice` and `/popup`. That is why the heralds write "the Peace of the Crown" and not "the King's Peace".

## Status marks used in this pack

- **[this run]**: the command belongs to a plugin another team is still building in this run (RealmDynasties, RealmLaws, RealmRavens, RealmTreasury, RealmRenown, RealmWarden, RealmSeasons, RealmEvents). The command exists in the current source, but its syntax may still change. `COMMANDS.md` shows the status of every command. Run the checker before each act.
- **UNVERIFIED**: the behaviour has been read in code but not seen on a live server. Run-sheets give a "check" step after each one, so the steward on shift finds out at once.
- **[DEC]**: read in decompiled game code (see `docs/admin-console.md`).

## The checker

```
node docs/saga/tools/check-saga.mjs           # check; exit code 1 on any error
node docs/saga/tools/check-saga.mjs --write   # also regenerate COMMANDS.md
node --test docs/saga/tools/check-saga.test.mjs   # the checker's own tests (14 tests)
```

It reads `plugins/*.cs`, `docs/admin-console.md` and `docs/oxide-rok-api.md`. It never writes outside `docs/saga/COMMANDS.md`. It fails when:

- a slash command in a code span, code block or herald line has no `[ChatCommand]` in `plugins/*.cs` and is not one of the game's console commands listed in `docs/admin-console.md`;
- a subcommand (`/claim declare`, `/court trial`, ...) is not a string literal in the plugin that owns the command;
- a decree id, law id, event kind or title name is not in the plugin's catalogue (`/decree peace`, `/law proclaim kings_peace`, `/event start tournament`, `/titles set Kingslayer`);
- `/season start` days or `/event start` minutes fall outside the range the plugin accepts;
- a chronicle type named on a `**Chronicle:**` line is not in `RealmChronicle.cs` `KnownTypes` or `docs/saga/EVENTS.json`;
- a herald line breaks the in-game rules above, or a Discord text is over 2000 characters or pings (`@everyone`, `@here`, mentions);
- the pack is the wrong shape: not exactly 30 proclamations, 13 locations, 10 legends, 8 calendar weeks and 4 act run-sheets;
- the weekly rhythm table in the calendar no longer matches the defaults in CrownAndConsequences, RealmEvents and RealmWarden;
- `COMMANDS.md` is stale.

What it cannot prove: that a command *behaves* as the run-sheets describe. That is what the "check" steps on the run-sheets and `docs/smoke-test.md` are for.

## New chronicle event types

None. The saga only uses event types that `RealmChronicle.cs` already registers, so there is no `EVENTS.json` here. The checker would read one if a later version of the pack needed new types.
