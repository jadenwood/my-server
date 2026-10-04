# RealmQuests: tasks, the season's tale, deeds and house goals

`plugins/RealmQuests.cs` (Oxide 2.0.3867, C# 3) gives every player of Ostreval something to do each day and something to chase all season:

- **Daily tasks** (three a day) and **weekly tasks** (two a week), drawn for each player from a pool of 31 and 16;
- **The Hollow Crown**, the Season 1 story in four acts and 17 steps, following [`docs/saga/season-1-the-hollow-crown.md`](../../docs/saga/season-1-the-hollow-crown.md);
- **68 deeds** (achievements) in five kinds (survival, war, politics, economy, exploration), most with three or four tiers;
- **House goals**: one shared goal a week for every house, paid in house season points.

Everything players do is read from real hooks and from what the other Realm plugins already keep. Rewards are marks (RealmTreasury), renown and titles (RealmRenown), house season points (RealmSeasons) and a few goods. All content is data: JSON in the realm's own voice in [`RealmQuests/content/`](RealmQuests/content/).

Status: **compiles with 0 errors against the real 2.0.3867 DLLs, behaviour-tested against mocks (293 checks) and exploit-tested (45 checks, plus 13 for the treasury method that pays its marks). It has never run on a live server.** See [What is UNVERIFIED](#what-is-unverified-and-how-to-test-it-in-game).

Tags: **[CODE]** = read in the decompiled 2.0.3867 `Assembly-CSharp.dll` (type and member names only; no game code is kept in this repo). **[ASM]** = confirmed by compiling against that DLL's metadata. **[OPJ]** = the Oxide hook manifest ([`docs/oxide-rok-api.md`](../../docs/oxide-rok-api.md)). **UNVERIFIED** = not seen working in game.

---

## What players see

```
/quest
Quests: Your journal. The day turns in 16h 0m, the week in 6d 16h.
  Daily 1: Wolves at the Fold - Slay wolves 1/3 (15 marks)
  Daily 2: Timber for the Fold - Hand in wood 0/150 (12 marks)
  Daily 3: Raise the Walls - done
  Weekly 1: The Huntsman's Week - Slay creatures of the wild 12/40; Slay wolves 4/10 (70 marks)
  Weekly 2: Every Bell Answered - Take part in different kinds of realm event 1/2 (80 marks)
  Story: Act 1: The Empty Seat - A Roof Before Winter: Lay building blocks 22/60
  House Varrow: Walls of the House - Blocks laid by the house 640/1500
  /quest log the full text, /quest give to hand in goods, /achievements your deeds.
```

The same journal opens in the game's own window (with `/realm popups off` a player keeps chat only). Progress is told as it happens (`Quests: Wolves at the Fold: Slay wolves 2/3`, at most every 15 s per task), and a finished task, story step or deed opens a window with its closing line and reward:

```
Quests: Task done: Wolves at the Fold. The folds sleep easier tonight, and so do the shepherds.
  You receive 15 marks, renown.
Quests: Deed recorded: Wolfsbane (Bronze). Wolves slain. The shepherds keep count even when you do not.
Quests: The treasury pays you 25 marks for your deeds.
```

A newcomer is pointed to the board once; an online player is told when the day turns (`A new day on the quest-board: 3 new tasks.`) and when a new act of the tale opens (with its intro in a window). Gold tiers, the end of the tale and a house's met goal are heralded to the realm (at most 6 an hour), and the end of the tale is written in the Chronicle (`title_earned`), so it reaches the overlay, the portal and Discord.

## Commands

| Command | What it does |
|---|---|
| `/quest` | The journal (chat and window) |
| `/quest log` | The full text of every open task and the story step, with objectives and rewards |
| `/quest story` | The season's tale: the prologue, the act and its intro, the current step (window too) |
| `/quest house` | Your house's goal this week, the top helpers and what it pays |
| `/quest give` / `/quest give all` | Lists the goods your tasks, story step and house goal want, then hands them in |
| `/quest collect` | Reward goods that did not fit in your packs |
| `/quest abandon d1\|w2 [confirm]` | Gives up a task; the slot stays empty until the reset. The story cannot be abandoned |
| `/quest reroll d1` | Draws another daily for that slot (one a day) |
| `/achievements` | Your deeds by kind, tiers, and the three closest to their next tier (window too) |
| `/achievements <kind>` | One kind: survival, war, politics, economy, exploration |
| `/achievements <name>` | One deed and its tiers |
| `/achievements top` | The realm's most accomplished |

Stewards with `realmquests.admin` (see [`staff-roles-and-permissions.md`](../../docs/community/ops/staff-roles-and-permissions.md)):

| Command | What it does |
|---|---|
| `/quest admin status` | Content counts, each content file's state, problems, the feeds, owed marks and goods, the season day |
| `/quest admin reload` | Reads the content files again (after an edit) |
| `/quest admin places` | The named places and whether each is marked |
| `/quest admin place set <id> [radius]` / `clear <id>` | Marks a named place where the steward stands (radius 5-500 m) |
| `/quest admin reset <player> [daily\|weekly\|story\|all]` | Resets a player's board or story |
| `/quest admin complete <player> <quest>` | Finishes an open task or story step **with its reward** (a testing aid; logged) |
| `/quest admin creatures` | The last creature names seen dying (to tune `slay_creature` targets) |
| `/quest admin items <word>` | Item names the server knows (to fix content) |

## How each objective is seen

| Objective type | What counts | Source |
|---|---|---|
| `craft` | Each product stack the server makes, credited to the player who asked the crafter to start | `EventManager.Subscribe` to `ItemCrafterCraftEvent` (Sender, Crafter, Product) and `ItemCrafterItemEvent` (Crafter, Stack) [ASM]. The server runs `ItemCrafter.Update` -> `CraftItem` -> `ItemCrafterItemEvent` [CODE]. `OnItemCrafted(ItemCrafterFinishEvent)` [OPJ L1088] cannot name the product (on the server `ItemCrafter.CraftFinish` clears `Product` before raising it [CODE]), so it only ends the claim on a station. Hand crafting: the crafter's entity is the player |
| `deliver` | Goods handed in with `/quest give all`: counted with `ItemCollection.AutoCount`, taken with `AutoSplit`, measured again [ASM]. Taken goods are spent (a sink) | **There is no server hook for gathering** ([`oxide-rok-api.md`](../../docs/oxide-rok-api.md) 3.9: every gather path runs on the client). `OnGadgetCollect` is picking a placed object back up [CODE `ItemPrefabCollector.Collect`], not gathering. So "gather" tasks are deliveries |
| `slay_creature` | A creature killed by a player | `OnEntityDeath(EntityDeathEvent)` [OPJ L188]; killer `KillingDamage.DamageSource.Owner` [ASM]. A creature has a `MonsterMotor` or `MonsterEntity` (the test the game's own `SalvageSupplier` uses [CODE]). Its kind comes from `Entity.ToString()` ("Wolf(Clone) (…Entity)" -> "wolf"); targets match part of the name |
| `slay_player` | A fair kill (see [Anti-abuse](#anti-abuse)) | `OnEntityDeath` [OPJ L188] |
| `build` | A block placed, once per cell per day | `OnCubePlacement(CubePlaceEvent)` [OPJ L266]: `Sender`, `GridID`, `Position`, `Material`, `CausedByDestruction` [ASM]; counted on the next tick so a placement another plugin cancelled is not. Materials map to roles with RealmSculptor's table (`MaterialIds`) |
| `visit` | Standing within a marked place's radius (once per place per day) | `Player.Entity.Position` [ASM], checked every `VisitSeconds` (5 s), so a rider passing through is seen |
| `playtime` | Active minutes: moved at least `ActiveMoveMeters` since the last tick, or did something counted in the last `ActionKeepsActiveSeconds` | the same sample |
| `survive` | Active minutes in one life (a death starts a new one) | the sample, and `OnEntityDeath` |
| `event` | Present and active for `EventAttendMinutes` while a realm event runs, once per kind per day. Whoever the Chronicle names as breaking the Truce (`truce_broken`) is not credited with keeping it that day | `RealmEvents.GetActiveEvents()` |
| `oath` | Your house swore (`sworn`) or accepted (`accepted`) an oath while you were in it | `RealmHouses.GetHouseSummaries()` polled: a liege that changes |
| `treaty` | Your house sealed a treaty | `oxide/data/RealmChronicle.json` read-only: `treaty_signed`, house names in the title |
| `contract` | A contract you finished (`bounty`, `delivery`, `merc`) | `oxide/data/RealmContracts.json` read-only (Status, FulfillerId, PosterId, Type), as RealmRenown reads it |
| `chronicle` | A Chronicle entry of a type whose doer (`actors[0]`) is you: `coronation`, `claim_declared`, `law_proclaimed`, `dynasty_founded`, `tournament_champion`, `hunt_kill`, `house_founded`, ... | `RealmChronicle.json` read-only by id cursor; the name is matched to a player the board has seen (two players with one name: nobody) |
| `house` | Belonging to (`member`) or leading (`leader`) a house | `RealmHouses.GetHouse`, `GetHouseLeader` |
| `renown`, `title`, `marks` | Renown, titles held, marks in the purse (levels; progress never goes back) | `RealmRenown.GetRenown`, `GetTitles`; `RealmTreasury.GetPurse` |
| `quests`, `achievements`, `house_goal` | Finished tasks (`daily`, `weekly`, `story`), deed tiers, met house goals | the plugin itself |
| `custom` | Whatever another plugin reports through `ReportQuestEvent` | the API |

## Content

The content lives in [`RealmQuests/content/`](RealmQuests/content/) and is read on the server from `oxide/data/RealmQuests/`:

| File | What | Shipped |
|---|---|---|
| `Places.json` | The twelve notable places of [`docs/saga/locations.md`](../../docs/saga/locations.md), with a default radius | 12 |
| `Dailies.json` | The daily pool | 31 |
| `Weeklies.json` | The weekly pool | 16 |
| `Story.json` | The Season 1 tale: prologue, four acts (`UnlockDay` 0, 14, 28, 42: weeks 1, 3, 5, 7), 17 steps, epilogue, rewards | 1 |
| `Achievements.json` | The deeds | 68 |
| `HouseGoals.json` | The weekly house goal pool | 12 |

**Deploying.** Copy the folder to the server: `plugins\docs\RealmQuests\content\*.json` -> `<server>\oxide\data\RealmQuests\`. Steward's deploy does not copy it yet (ROADMAP STW-1, with the sculptures and sign art). Without it the plugin runs but `/quest` says the board is not posted. After editing a file on the server, `/quest admin reload`.

**Marking places.** No code knows where the realm's places are on the map you run. Before launch a steward stands at each and types `/quest admin place set <id> [radius]` (`/quest admin places` lists them). Until a place is marked: daily and weekly tasks that need it are not drawn, and a story step that needs it is waived (the tale never stalls on a steward's to-do list).

A task, written as data:

```json
{
  "Id": "d_wolves", "Title": "Wolves at the Fold", "Weight": 10,
  "Text": "Halloran's shepherds count fewer lambs each dawn. Thin the wolves that prowl the edge of the Crown Wold.",
  "Done": "The folds sleep easier tonight, and so do the shepherds.",
  "Objectives": [ { "Type": "slay_creature", "Targets": ["wolf"], "Count": 3, "Text": "Slay wolves" } ],
  "Reward": { "Marks": 15, "Items": [ { "Item": "CookedMeat", "Count": 2 } ], "Renown": "" }
}
```

- `Weight`: how often it is drawn. `NeedsHouse`: only for players in a house. `MinActiveMinutes`: only for players who have played that long. `AnyOne`: done when any one objective is done (else all).
- `Targets`: item names (the display name, any case, or the game's `ResourceType` name such as `IronOre`, `WolfPelt`; `~word` means "the name contains word"), creature words, material roles (`stone`, `cobblestone`, `wood`, ...), place ids, event kinds (`crown_night`, `tournament`, `kings_hunt`, `truce`), Chronicle types, contract types. Empty = anything of that type. `Distinct`: count each victim, place or kind once.
- `Reward.Renown`: a RealmRenown deed kind; empty uses the default (`quest_daily`, `quest_weekly`, `quest_story`, `achievement`, `achievement_gold`, `story_complete`, `house_goal`); `none` gives no renown.

The loader refuses (and lists in `/quest admin status`) any entry with: a bad or duplicate id; a title, text or name that is empty, too long (titles 60, texts 200, objective texts 80) or holds `[ ] { }`; an unknown objective type or place; a visit asking for more places than it names; a delivery of any item a reward can give (no reward loops); a task counting deeds; tiers that do not rise; a category outside the five; absurd rewards. Item names unknown to the server are reported once the world has loaded. A content file that is missing or does not parse switches off only its own part and is never written.

**Writing new content.** Ostreval's voice: grim but readable, original names only (see [`docs/community/lore.md`](../../docs/community/lore.md)). Name what players do, never promise a mechanic the plugins lack. Keep every line under 200 characters.

## Rewards

| Reward | How |
|---|---|
| Marks | Owed first, then asked of `RealmTreasury.RewardMarks(playerId, name, amount, source)`. The treasury strikes them new under caps of its own (`RewardMintPerDay` 3000 a day for all players, `RewardMaxPerCall` 500, and never within `RewardCrownReserve` 20000 of `MintSupplyCap`, so the crown can still mint); they count in `MarksMinted`, so the treasury's zero-sum audit holds. What it does not pay stays owed and is paid on a later tick. A new account is paid only after `MinActiveMinutesForMarks` (30) of active play |
| Renown and titles | `RealmRenown.AddDeed(id, name, kind, note, "quests:<key>")`, one dedupe key per task and day, step or tier. RealmRenown's defaults now include the seven deed kinds below; the six titles they lead to are added in RealmRenown's config (below) |
| House season points | `RealmSeasons.AwardHouse(house, points, honour)` when a house goal is met (capped by RealmSeasons' `MaxEventAwardPerCall`) |
| Goods | A persisted owed ledger: the entry is removed and saved **before** the stack is given, the gain is measured with `AutoCount`, only the shortfall goes back. A crash can lose a reward, never duplicate one. RealmSentinel is told each gift is explained (`SentinelItemSource`). Shipped rewards use `Bread`, `CookedMeat`, `SteelIngot` and `Charcoal` |

RealmRenown deeds added for quests (in `plugins/RealmRenown.cs` defaults, so existing servers get them on the next load), and the titles written for them. **The titles are not RealmRenown defaults yet**: every default title needs a badge in the art pack (`art/src/titles.json`, checked by `art/tools/test`), and those six badges are a follow-up for the art team. Until then a server turns them on by pasting the entries of [`RealmQuests/renown-titles.json`](RealmQuests/renown-titles.json) into the `Titles` list of `oxide/config/RealmRenown.json` and reloading RealmRenown (`/titles all` then lists them; titles from the config have no badge on the portal):

| Deed | Renown | Title it leads to (config) |
|---|---|---|
| `quest_daily` | 5 | the Diligent (30) |
| `quest_weekly` | 15 | the Steadfast (10) |
| `quest_story` | 20 | |
| `story_complete` | 100 | Witness of the Crown (1) |
| `achievement` (bronze, silver tiers) | 10 | the Accomplished (25) |
| `achievement_gold` (gold and higher tiers) | 30 | Paragon of Ostreval (10) |
| `house_goal` | 15 | Pillar of the House (6) |

## Anti-abuse

| Abuse | Defence |
|---|---|
| Rerolling by relogging or a restart | The board is drawn from a seed of the player, the day and a per-server salt: the same board every time. One free `/quest reroll` a day; it never redraws a task already on the board or yesterday's. An abandoned slot stays empty until the reset; a finished task cannot be rerolled |
| Alt farming | Marks are held until an account has `MinActiveMinutesForMarks` of active (moving) play, and at most `MaxRewardedAccountsPerAddress` (3) accounts from one address are paid marks a day. The address is kept only as a salted hash, in memory, never written |
| Kill-trading | A kill counts only if the victim is online, has `PvpMinVictimActiveMinutes` (30) of play, is not under RealmWarden's new-player protection, did not die in the last `PvpMinVictimLifeSeconds` (120), is not of the killer's house or an allied house (liege, vassal, treaty), and the two have not had a counted kill either way round in `PvpPairCooldownHours` (24). `PvpCreditsPerDay` (10) caps the rest |
| Crafting loops | Only products of a craft the player started count; `CraftIgnore` (torches, bandages, sticks, firewood, wood shields) never counts; `CraftCreditsPerDay` (300) |
| Building loops | A cell counts once a day (place, break, place); blocks the game places on a collapse do not count; `BuildCreditsPerDay` (400) |
| Hunting, contracts, oaths, treaties | `CreatureCreditsPerDay` (150); contracts never with yourself or a housemate, the same pair once in `ContractPairCooldownHours`, `ContractCreditsPerDay` (3); oaths and treaties once a day per player, never for a one-account house, never for a member of less than `HouseMemberMinHours` (12) |
| House goals | A house needs `HouseGoalMinMembers` (2); a member's share is capped at `HouseGoalMemberCapPercent` (50%) of the goal; the goal needs `HouseGoalMinContributors` (2); recruits of less than `HouseMemberMinHours` cannot help (leaving and rejoining restarts the wait); only helpers who gave at least 5% are paid |
| Reward loops | No delivery or delivery deed may ask for an item any reward gives (checked at load) |
| AFK farming | Playtime, survival and event attendance count only active ticks |
| Forged reports | `ReportQuestEvent` accepts only `contract`, `event` and `custom`, capped at 200 a day per player and type |
| Name spoofing in the Chronicle | A doer's name two seen players share credits nobody |

`tools/exploit-review/run.sh quests` replays each of these (Q1 to Q11).

## Config (`oxide/config/RealmQuests.json`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled`, `DailiesEnabled`, `WeekliesEnabled`, `StoryEnabled`, `AchievementsEnabled`, `HouseGoalsEnabled` | true | Switch the whole board, or one part, off |
| `DailyCount`, `WeeklyCount`, `DailyRerolls` | 3, 2, 1 | Board size (0-6, 0-4) and free rerolls a day |
| `ResetHourUtc` | 4 | The quest day turns at this hour (UTC); the week turns on Monday at it |
| `StoryRequiresSeason`, `StoryTimeGates` | true | The tale runs only while RealmSeasons runs the season the tale is written for (`Season` in `Story.json`); acts wait for their `UnlockDay`. A new season's `Story.json` starts every player's tale afresh |
| `UsePopups`, `PopupOnComplete` | true | The game's windows (chat is always sent too) |
| `MarksRewards`, `ItemRewards`, `RenownRewards`, `SeasonPointRewards` | true | Each kind of reward |
| `RewardScale` | 1 | Multiplies every marks reward (0-10) |
| `MarksPayPerTick` | 500 | Marks asked of the treasury per player per tick |
| `MinActiveMinutesForMarks`, `MaxRewardedAccountsPerAddress` | 30, 3 | Alt limits (0 = off) |
| `Pvp*`, `CreatureCreditsPerDay`, `CraftCreditsPerDay`, `CraftIgnore`, `BuildCreditsPerDay`, `Contract*` | see above | Anti-abuse limits |
| `EventAttendMinutes` | 10 | Active minutes at an event that count as taking part |
| `HouseGoal*`, `HouseMemberMinHours` | 1, 2, 2, 50, 12 | House goals |
| `TickSeconds`, `FeedPollSeconds`, `VisitSeconds` | 30, 60, 5 | Presence sampling; polling other plugins and their files; checking marked places |
| `ActiveMoveMeters`, `ActionKeepsActiveSeconds` | 1.5, 120 | What counts as active |
| `ProgressNoticeSeconds` | 15 | Progress lines per task at most this often |
| `AnnounceAchievements`, `MaxAnnouncementsPerHour`, `ChronicleStory`, `MaxChroniclePerHour` | true, 6, true, 4 | Heralds and the Chronicle |
| `MaterialIds` | RealmSculptor's table | Block materials -> roles (UNVERIFIED ids; `/sculpt materials` writes the real table) |
| `TierNames` | Bronze, Silver, Gold, Ostreval | Tier names |

RealmTreasury's own keys for the marks it pays: `RewardsEnabled`, `RewardMintPerDay`, `RewardMaxPerCall`, `RewardCrownReserve` (in `oxide/config/RealmTreasury.json`).

## Data

`oxide/data/RealmQuests.json`: each player's board, story, deeds, owed marks and goods; each house's goals; marked places; feed cursors. If it exists but does not parse (a truncated file after a power cut, or `null`), the plugin tracks nothing, answers "the quest-board is closed", and **never writes the file**. A copy of the last file that loaded is kept as `RealmQuests_lastgood.json`: move the damaged file away, rename the copy, reload. Other plugins' files (`RealmChronicle.json`, `RealmContracts.json`, `RealmSeasons.json`) are only read, after `ExistsDatafile`, so they are never created or changed.

## API (non-public, `Plugin.Call`)

| Method | Returns |
|---|---|
| `ReportQuestEvent(string playerId, string type, string subject, int amount)` | `bool`. Lets another plugin report a `contract`, `event` or `custom` deed |
| `GetAchievementCount(string playerId)` | `int`, deed tiers held |
| `GetStoryProgress(string playerId)` | `string` ("Act 2: The Charter Tested - Ink on the Board", "complete") or null |
| `GetHouseGoalText(string house)` | `string`, one plain line for a painted board, or null |

Calls it makes: RealmTreasury `RewardMarks`, `GetPurse`; RealmRenown `AddDeed`, `GetRenown`, `GetTitles`; RealmHouses `GetHouse`, `GetHouseLeader`, `GetLiege`, `GetMembers`, `GetHouseSummaries`, `HasTreaty`; RealmSeasons `AwardHouse`, `GetSeasonNumber`; RealmChronicle `Log` (`title_earned`); RealmHerald `PopupsWanted`; RealmEvents `GetActiveEvents`; RealmWarden `IsNewPlayerProtected`; RealmSentinel `SentinelItemSource`. Every one is optional: a missing plugin only switches off what needs it.

## What is UNVERIFIED, and how to test it in game

Nothing here has been seen on a real server. Run these on the test server with two players (A and B) and a steward (S), with the content copied to `oxide\data\RealmQuests\` and Season 1 started. Write each result back here.

| # | Check | How |
|---|---|---|
| QS1 | The plugin loads and reads its content | Console: `Content: 31 daily, 16 weekly, 4 story chapters, 68 achievements, 12 house goals, 12 places`. S: `/quest admin status` shows every file `ok` and no `Problem` line |
| QS2 | Item names in content exist on this server | `/quest admin status` lists no "item ... is not known". If it does, fix the name with `/quest admin items <word>` (ResourceType names such as `IronOre` should resolve) |
| QS3 | The journal and its window | A: `/quest`. The chat lines appear in the Quests voice; a window opens with the journal; line breaks show. Then `/realm popups off` and `/quest`: chat only |
| QS4 | Crafting is credited | A takes a daily with a craft objective (S: `/quest admin reset A daily` until one shows, or edit a test task) and crafts at a bench and by hand. Progress lines appear per product. If nothing moves, the server does not raise `ItemCrafterItemEvent` for plugins: report it |
| QS5 | Creature names | A kills a wolf, a deer and a chicken. S: `/quest admin creatures` lists their names. If they are not `wolf`, `deer`, `chicken` (or contain those words), change the `slay_creature` targets in the content to the names shown |
| QS6 | Building | A places blocks of stone and wood; `Raise the Walls` moves by one per new cell; breaking and replacing a block does not move it again. S: `/sculpt materials` confirms the material ids |
| QS7 | Places | S stands at the Hearth: `/quest admin place set the_hearth 40`. A walks into the circle: "You reach the Hearth." (only when it moved a task or deed) |
| QS8 | Deliveries | A carries 150 wood with `Timber for the Fold`: `/quest give` lists it, `/quest give all` takes exactly what is wanted and the packs show it gone at once |
| QS9 | Player kills | A kills B (different houses, both played 30 min): `First Blood` bronze. B kills A at once: nothing. A kills B again within 2 minutes of B's respawn: nothing |
| QS10 | Event attendance | During Crown Night, A moves about for 10 minutes: Night of Crowns bronze. Standing still: nothing |
| QS11 | Marks | A finishes a task after 30 active minutes: "The treasury pays you N marks"; `/purse` shows them; `/treasury audit` (S) balances |
| QS12 | Goods | A finishes `Venison for the Hall` with full packs: "still wait"; after making room, `/quest collect` gives 2 cooked meat once. RealmSentinel raises no item alert for it |
| QS13 | Renown and titles | A finishes a daily: RealmRenown says `+5 renown` (deed `quest_daily`). After pasting `renown-titles.json` into RealmRenown's config, `/titles all` lists the six quest titles; S: `/renown admin title give A the Diligent` shows one in chat |
| QS14 | The story | A: `/quest story` shows the prologue and Act 1 with a window. On season day 15 Act 2 opens (on a test server, move `StartedAt` back in `oxide\data\RealmSeasons.json` while it is stopped). S's `/quest admin complete A <step>` walks it to the end: the herald line and the Chronicle `title_earned` entry appear |
| QS15 | House goals | Two members of a house of three (in it 12 h or more) build together; `/quest house` shows the shared count, capped at half per member; when met, the herald line, `/season house <name>` shows the points, and each helper is paid |
| QS16 | Oaths and treaties | Two houses of two: one swears to the other (`/swear`, `/swear accept`): members get Oathsworn / Liege Lord within a minute. A treaty: Seal and Wax |
| QS17 | A damaged data file | Stop the server, cut `oxide\data\RealmQuests.json` in half, start: the console says it cannot be read; `/quest` says the board is closed; the file is unchanged after a save and a stop |
| QS18 | The day and the week | At 04:00 UTC the dailies change; on Monday at 04:00 the weeklies change; a relog in between shows the same board |

## For reviewers: edits outside this plugin

- `plugins/RealmTreasury.cs`: the non-public `RewardMarks` method and four config keys (`RewardsEnabled`, `RewardMintPerDay`, `RewardMaxPerCall`, `RewardCrownReserve`). RealmTreasury had no method another plugin could pay marks through; `tools/exploit-review/quests/TreasuryReward.cs` tests it.
- `plugins/RealmRenown.cs`: seven deed kinds in the defaults (no new titles there: see Rewards).
- `plugins/RealmHerald.cs`: `/quest` and `/achievements` in the `/realm` catalogue (its logic test now asks for 36 or more commands).
- `docs/community/ops/staff-roles-and-permissions.md`: `realmquests.admin`.
- `tools/exploit-review/run.sh`: the `quests` suite.
