# RealmHerald: welcome, first steps and the /realm hub

`plugins/RealmHerald.cs` (Oxide 2.0.3867, C# 3) is the realm's voice for newcomers and the help hub for everyone. It welcomes a player the first time they join, walks them through three first steps, lists every Realm command by subject under `/realm`, sends rotating tips, and shows a message of the day. The hub and the welcome also open as the game's own popup windows.

Status: **compiles with 0 errors against the real 2.0.3867 DLLs, and is behaviour-tested against mocks (102 checks pass). It has never run on a live server.** See [What is UNVERIFIED](#what-is-unverified).

RealmHerald writes nothing to the Chronicle (a welcome is not realm history), so **no new Chronicle event types are needed**.

---

## What a newcomer sees

About 20 seconds after their first join (`WelcomeDelaySeconds`, so it lands after the game's own join lines and RealmWarden's protection notice):

1. **A welcome window** (`WelcomePopup`) titled "Welcome to Ostreval": the greeting, the message of the day and the three first steps.
2. **The same welcome in chat**, four lines, so a player who never sees the window misses nothing:
   ```
   Realm: Hail, Ada, and well met. Six great houses contend for the Old Throne, and the realm remembers what you do.
     Hail, Ada. 3 of the realm are here, and the Old Throne is held by Aldric of House Varrow. /realm lists every command.
     Your first steps: swear to a house, see who holds the crown, take a contract. /realm path shows the way.
     Every command, by subject: /realm
   ```
3. **One Herald line to the realm**: "Herald: Ada arrives in Ostreval for the first time." At most `NewcomerHeraldsPerHour` (6) an hour, so a wave of alts cannot flood chat.

A returning player gets only the message of the day, 8 seconds after joining (`MotdDelaySeconds`).

## The first steps

| Step | Done when |
|---|---|
| 1. Swear to a house | `RealmHouses.GetHouse(id)` names a house. |
| 2. See who holds the crown | The player types `/realm crown`, or `/crown` is seen through `OnPlayerCommand` (UNVERIFIED that Oxide chat commands reach that hook). |
| 3. Take a first contract | `RealmContracts.HasContractHistory(id)` is true, or `/contract accept` or `/contract post` is seen through `OnPlayerCommand` (same caveat). |

The next step is shown a minute after joining (`FirstReminderSeconds`) and then every 15 minutes (`PathReminderMinutes`), at most 3 times a session (`PathRemindersPerSession`). Each step is announced once when it is done, and the whole path is congratulated once. A step whose plugin is not loaded says so and can be set aside with `/realm skip`; it never blocks the path.

## Commands

| Command | What it does |
|---|---|
| `/realm` | Opens the hub as a popup window: one line per subject with its commands. One chat line goes with it: `/realm list` shows the same in chat. With popups off, prints the hub in chat. |
| `/realm list` (or `help`) | The hub in chat: a header and one line per subject, every command in the command colour. |
| `/realm <subject>` | Each command of a subject with its one-line description. Subjects: `houses`, `crown`, `law`, `blood`, `coin`, `events`, `letters`, `fair`. |
| `/realm <command>` | One command's description, for example `/realm contract`. |
| `/realm path` | Your first steps: done, next and later. `/realm path off` stops the reminders, `/realm path on` starts them again. |
| `/realm skip` | Sets the current step aside. |
| `/realm crown` | Who holds the Old Throne. Counts as step 2. |
| `/realm tips on\|off` | Rotating tips for you. |
| `/realm popups on\|off` | Realm popup windows for you. Off means chat only, from RealmHerald and from RealmHouses. |
| `/realm motd` | The message of the day. |

Commands of plugins that are not loaded are left out of the hub. `tools/realm-integration/check.mjs` fails if the catalogue in the plugin and the `[ChatCommand]`s in `plugins/*.cs` ever differ, so a new command cannot be forgotten.

Admin commands (`realmherald.admin`, granted with `oxide.grant user <name> realmherald.admin`):

| Command | What it does |
|---|---|
| `/realm admin motd add <text>` | Adds a line to the message of the day (saved to the config). At most `MaxMotdLines` lines of `MaxLineLength` characters. |
| `/realm admin motd clear` / `list` | Clears or lists it. |
| `/realm admin tip` | Sends the next tip now. |
| `/realm admin reset <player>` | Starts a player's first steps again. |
| `/realm admin status` | Players known, paths walked, newcomers today, tips, which plugins answer, and the data file's health. |

## Message of the day and tips

The MOTD and the tips are plain text in `oxide/config/RealmHerald.json`. Placeholders in the MOTD: `{player}`, `{online}`, `{max}`, `{monarch}`, `{season}`. They are filled by plain replacement, never by `string.Format`, so a stray brace in an admin's line is harmless. Every `/command` in admin-written text is drawn in the command colour when it is sent.

One tip goes out every `TipIntervalMinutes` (20) to the players online who have tips on, in the quiet "Tip" voice. A tip that names a command of a plugin that is not loaded is skipped.

## Popups

The hub and the welcome use the game's popup window (`Player.ShowPopup`, see [docs/realm-commands.md](../../docs/realm-commands.md#popups)). Every window has its chat version, which is always sent too. A window is plain text: chat colour tags are stripped.

- `UsePopups` (true) switches every RealmHerald window off for the server.
- `WelcomePopup` (true) switches off only the welcome window.
- `/realm popups off` switches them off for one player. RealmHouses asks `PopupsWanted(playerId)` before each of its own windows, so the choice covers both plugins.
- If the game throws when a window is opened, the plugin logs a warning and prints the chat version.

## Config

`oxide/config/RealmHerald.json`. Defaults:

| Key | Default | Meaning |
|---|---|---|
| `WelcomeNewPlayers` | `true` | Welcome a Steam id the first time it joins. |
| `WelcomeDelaySeconds` | `20` | Delay after the join. |
| `HeraldNewcomers` / `NewcomerHeraldsPerHour` | `true` / `6` | The Herald line to the realm, and its cap (0 = never). |
| `FirstStepsPath` | `true` | The three first steps and their reminders. |
| `FirstReminderSeconds` / `PathReminderMinutes` / `PathRemindersPerSession` | `60` / `15` / `3` | Reminder timing. |
| `ShowMotdOnJoin` / `MotdDelaySeconds` / `Motd` | `true` / `8` / one line | The message of the day. |
| `TipsEnabled` / `TipIntervalMinutes` / `TipMinPlayers` / `Tips` | `true` / `20` / `1` / twelve tips | Rotating tips. |
| `UsePopups` / `WelcomePopup` | `true` / `true` | Popup windows (see above). |
| `TickSeconds` | `30` | How often reminders and tips are checked. |
| `MaxPlayersKept` | `20000` | Records beyond this are dropped, least recently seen first; online players never are. |
| `MaxMotdLines` / `MaxLineLength` | `6` / `200` | Limits for admin-written MOTD lines. |

## Data

`oxide/data/RealmHerald.json`: per Steam id, the name, first and last seen, the three steps, and the path, tips and popups choices; the tip rotation; newcomers today. If the file exists but cannot be parsed, the plugin reports it, keeps answering `/realm` from memory and **never writes the file** until it is fixed or moved away and the plugin is reloaded.

## Cross-plugin calls

All optional. A missing plugin only switches off the part that needs it.

| Call | Used for |
|---|---|
| `RealmHouses.GetHouse(string playerId)` | Step 1. |
| `CrownAndConsequences.GetKingName()`, `GetKingHouse()` | `/realm crown`, `{monarch}`. |
| `RealmContracts.HasContractHistory(string playerId)` | Step 3. |
| `RealmSeasons.GetSeasonName()` | `{season}`. |
| Offered: `PopupsWanted(string playerId)` | RealmHouses asks it before each window. |

## Tests

```
bash plugins/docs/RealmHerald/logic-tests/run.sh
```

Compiles the unchanged plugin with mocks of the game and Oxide and runs 102 checks: the welcome and its window, the newcomer cap, the first-steps path and reminders, the hub window and chat hub against the real `[ChatCommand]`s, `/realm popups` and `PopupsWanted`, a game that throws on a window, tips, the MOTD, admin commands, the chat style, reload, a damaged data file and pruning.

## Smoke test on a real server

1. Deploy, then join with a Steam account the server has never seen. Within about 20 seconds: a "Welcome to Ostreval" window and four welcome lines in chat. Another player sees one Herald line.
2. Type `/realm`. A window lists every command by subject, and one chat line points to `/realm list`. Close the window. Type `/realm list`: nine lines in chat.
3. Type `/realm popups off`, then `/realm`: the hub comes in chat, no window. `/realm popups on` brings windows back.
4. Wait a minute: "Your next step: Swear to a house". Join a house: "Step 1 of 3 done". Type `/crown`: if step 2 is not marked, `OnPlayerCommand` does not see Oxide commands; `/realm crown` still marks it.
5. Note how the windows look: line breaks, length, whether the text fits. Report anything cut off.

## What is UNVERIFIED

- Everything at run time: nothing here has run on a real server.
- That `ShowPopup` windows reach a client from a dedicated server, how they look, whether `\n` breaks lines in them, and how much text fits.
- That Oxide's own chat commands reach `OnPlayerCommand` (steps 2 and 3 have other ways to be done).
- How chat colours look in the game's chat.
