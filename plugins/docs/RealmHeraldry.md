# RealmHeraldry: the realm's colours and its voice

Realm's houses in the game's own guild system, and the realm's votes. Each house flies its colours from `art/palette.json` on the game's guild banner, crest, armour tints and name-tag icons, and its guild bears the house's name. Each season the realm elects council seats from among the heads of houses, and the monarch may put a decree or a law to the whole realm.

**Status.** Compile-checked at C# 3 against the real Oxide 2.0.3867 and patched game DLL metadata (`tools/plugin-compile-check/check.sh`). Mock-tested: 274 checks (`plugins/docs/RealmHeraldry/logic-tests/run.sh`). Exploit suite: 42 checks including a 3000-step fuzz, plus 33 against the real CrownAndConsequences (elected seats, decree mandates) and 14 against the real RealmTreasury (deposits, the colour fee) (`tools/exploit-review/run.sh heraldry`). **Nothing here has been seen working on a real server.** Every game-side assumption is listed under [What is UNVERIFIED](#what-is-unverified), each with its smoke step.

Speakers in chat: **Heraldry** for colours (lang key `Speaker`) and **Council** for votes (`SpeakerCouncil`). Permission: `realmheraldry.admin`. Files: `plugins/RealmHeraldry.cs`, `oxide/config/RealmHeraldry.json`, `oxide/data/RealmHeraldry.json` (and it reads `oxide/data/RealmHouses.json`).

## What players see

1. **House colours in the game.** Within two minutes of loading, every house bound to a game guild (`/house link`) flies its colours. House Varrow's guild is named *Varrow* and its banner is plum with an iron-grey emblem; the crest flags, the guild colours on armour and the small banner icon beside members' name tags follow (the game draws all of them from the guild's banner). A player house flies one of the open pairs until its head chooses.
2. **Choosing colours.** `/heraldry colours` lists the 14 pairs, which are yours, which belong to a great house and which another house has chosen. The head of the house picks one: `/heraldry colours blood-gold`. The first choice is free; later changes cost 250 marks (to the crown's treasury) and wait 72 hours. The Herald tells the realm: *House Wolves raises new colours: Blood and Gold.*
3. **A council election.** When a new season starts the Herald calls it: *The council election of Season 2 is called: heads of houses may stand for Keeper of Coin, Marshal with /ballot stand <seat> for the next 2 d 0 h.* A head of a house stands with `/ballot stand marshal` (100 marks are held as a deposit). After two days voting opens, with a window for each player who may vote. `/vote Wren` (or the house: `/vote Wolves`) casts the vote; it can be changed until the close. Three days later the Herald announces the winners and the crown seats them for 28 days.
4. **A referendum.** The monarch asks: `/ballot propose decree roads`. The Herald: *The crown asks the realm: Shall the crown issue the decree Open Roads? Vote with /vote yes or /vote no before 1 d 0 h from now.* After a day the realm answers; a decree carried costs the crown no authority the next time, one refused may not be issued for 72 hours.
5. **Can I vote?** `/ballot me` says yes, or exactly what is missing (time in the realm, play time, time in your house, protection, outlawry, exile).

The `/realm` hub lists `/heraldry` under **Houses and oaths** and `/ballot` and `/vote` under **The crown**.

## Commands

| Command | Who | What it does |
|---|---|---|
| `/heraldry` | Everyone | Your house's colours and whether its game guild is in step |
| `/heraldry house <house>` (or `/heraldry <house>`) | Everyone | Another house's colours |
| `/heraldry colours` | Everyone | The pairs a house may bear, with who bears them |
| `/heraldry colours <pair>` | Head of a house | Choose your house's colours (id or name) |
| `/ballot` | Everyone | What the realm votes on now, with the deadlines |
| `/ballot <n>` | Everyone | One ballot: seats and candidates (or the question), the turnout, your own vote, the results once closed |
| `/ballot results [n]`, `/ballot history` | Everyone | The last results; the last eight ballots |
| `/ballot me` | Everyone | Whether you may vote, and why not |
| `/ballot stand <seat>`, `/ballot withdraw` | Head of a house | Stand for an elected seat (deposit held); withdraw (free before the vote opens, forfeit after) |
| `/ballot propose decree <id>` | The monarch | Ask the realm about a decree from `/decree` (`roads`, `peace`, `relief`, `stores` by default) |
| `/ballot propose law <id>` | The monarch | Ask about a law from `/law catalogue`: proclaim it if it is not in force, repeal it if it is |
| `/vote <candidate or house>` | Voters | Vote in the open council election (exact name, house, or a unique start) |
| `/vote yes` / `/vote no` (`aye`, `nay`) | Voters | Answer the open referendum; `/vote <n> yes` when there are several |

Staff (`realmheraldry.admin`):

| Command | What it does |
|---|---|
| `/heraldry sync` | Bring every bound guild in step now; reports how many were changed, already in step, unbound, waiting |
| `/heraldry preview [house]` | What a sync would change (name and colours, old to new), and why a house has no guild. Changes nothing |
| `/heraldry set <house> <pair>` / `reset <house>` | Give a house a pair (ignores fee, cooldown and reservation) / back to the heralds' choice |
| `/heraldry banner <house> <banner\|keep> <pattern\|keep>` | Set the banner and emblem indices; off until `BannerCount` and `PatternCount` are set from what the game shows |
| `/heraldry status` | Switches, the last sync, `RealmHouses.json`, the colour binding, voters known, ballots open |
| `/ballot admin open` | Call a council election now (it counts as this season's) |
| `/ballot admin advance <n>` | Standing to voting (the vote then runs its full `VotingHours`), or voting to the count |
| `/ballot admin cancel <n>` | Call a ballot off; every deposit is returned |
| `/ballot admin strike <n> <player>` | Strike a vote (an alt found by audit): removed, and that player may not vote in the ballot again. Logged |
| `/ballot admin audit <n>` | Votes by house and the least-played voters, to look for a bloc of alts |
| `/ballot admin voter <player>` | A voter's record: first seen, play, house and time in it, and whether they may vote |

## Heraldry

**The pairs.** Every colour is in `art/palette.json` (the logic tests check it, and that each emblem stands out from its field at 3:1 or more).

| Id | Name | Field (banner) | Charge (emblem) | Reserved for |
|---|---|---|---|---|
| `varrow` | Iron Stag | plum `#4a2347` | iron grey `#9aa0a8` | House Varrow |
| `ashgrove` | White Oak | rust `#7a3a1a` | bone white `#e8dfc8` | House Ashgrove |
| `corvane` | Black Raven | slate `#2c3b42` | silver `#c9ced4` | House Corvane |
| `dunmere` | Drowned Bell | marsh olive `#5a5a22` | tarnished bronze `#e0b56a` | House Dunmere |
| `halloran` | Ember Hound | smoke brown `#3a2a1a` | ember orange `#e27a2c` | House Halloran |
| `merrin` | Silver Eel | deep green `#24472d` | silver `#c9ced4` | House Merrin |
| `blood-gold` | Blood and Gold | `#8b2b22` | `#d6a043` | |
| `moss-parchment` | Moss and Parchment | `#4d6b3a` | `#ecdfbf` | |
| `iron-ember` | Iron and Ember | `#131417` | `#f4c96d` | |
| `verdigris-bone` | Verdigris and Bone | `#245a52` | `#e8dfc8` | |
| `lapis-silver` | Lapis and Silver | `#2c4a7a` | `#c9ced4` | |
| `ink-parchment` | Ink and Parchment | `#2a1c0f` | `#e0cfa4` | |
| `lapis-gold` | Lapis and Gold | `#2c4a7a` | `#d6a043` | |
| `moss-gold` | Moss and Gold | `#4d6b3a` | `#f4c96d` | |

**Which pair a house bears.** Its head's choice, if still allowed; else, for a great house, its own colours; else one of the eight open pairs by a stable hash of the house name (the same name always gives the same pair, so the colours never flicker). A great house's own pair is reserved for it (`ReserveGreatHousePairs`); a chosen pair cannot be chosen by a second house (`UniqueChoices`) while the first house stands. A great house may bear an open pair too. The owner edits the list in the config (`Heraldry.Pairs`); a pair with a bad id or colour is left out with a warning, and with no open pair left the defaults come back.

**Which guild a house flies as.** RealmHouses binds a house to a game guild (`/house link`, or founding a house from inside a guild) and writes the guild id to `oxide/data/RealmHouses.json`; RealmHeraldry reads it (never writes it). A house with no guild there is left alone. Two houses on one guild (a damaged file): the first keeps it. Without a readable file (`GuildFallbackByLeader`), the house leader's guild is used when every member of it belongs to the house.

**Keeping it in step.** Every `SyncSeconds` (120) each bound guild is compared with its house: the guild name (the house name through `NameFormat`, cut to the game's 28 characters, brackets and control characters removed) and the banner's two colours. A difference is put right through the game's own guild update (see Game API). At most `MaxAppliesPerSync` (6) guilds per pass, and a guild changed less than `GuildCooldownSeconds` (60) ago waits, so a player renaming the guild over and over cannot flood the network. A house's own new colours go up at once. With `Enforce` off, a player's own rename or recolour in the game's guild menu stands until the realm changes the arms (a new choice, a renamed house).

**Indices.** The banner shape and emblem (`CurrentBanner`, `CurrentPattern`) are indices into arrays that only the client knows, and an index past their end breaks the client's crest and name-tag drawing. So the plugin keeps each guild's own indices (whatever its founder picked) unless the owner sets `BannerCount` and `PatternCount` from what the game's banner menu shows (smoke step HR6) and gives a house indices with `/heraldry banner`.

## Council elections

- **Seats.** `Council.ElectedSeats` (Keeper of Coin, Marshal), matched against the crown's council (`CrownAndConsequences.GetCouncilSeats`); the Voice of the Crown, the seat that may issue proclamations, stays the monarch's to fill. A seat the crown does not have is ignored; none left, no election.
- **When.** Each new RealmSeasons season (`ElectEachSeason`). The season already under way when the plugin first loads is not elected (it waits for the next one, or `/ballot admin open`). Standing lasts `NominationHours` (48), voting `VotingHours` (72).
- **Candidates.** The head of a house (RealmHouses leader) of at least `CandidateMinHouseMembers` (3), founded `CandidateMinHouseAgeDays` (3) ago, who may vote themselves, and not the monarch (`MonarchMayStand`). One seat per candidate and one candidate per house. A deposit of `CandidateDeposit` (100 marks) is held by RealmTreasury (`HoldMarks`).
- **Votes.** One per seat per voter; may be changed until the close (`AllowVoteChange`). Counts stay sealed until the close; `/ballot <n>` shows only the turnout and your own vote. Each voter's first vote in a ballot is reported to RealmQuests (`ReportQuestEvent(id, "custom", "vote_cast", 1)`).
- **The count.** Most votes wins; a tie goes to the greater renown (RealmRenown `GetRenown`), then to whoever stood first. A lone candidate is elected unopposed. A contested seat with fewer than `MinTurnout` (3) votes stays as it is. The winner is seated through `CrownAndConsequences.SeatElectedCouncillor` for `TermDays` (28): the monarch cannot dismiss them or appoint over them before then (staff can), and the seat outlasts a change of monarch. The winner's house earns `ElectedHousePoints` (5) season points (RealmSeasons `AwardHouse`) and RealmQuests hears `council_elected`.
- **Deposits.** Returned to the winner, to a lone candidate, to everyone when the seat lacked votes, and to anyone with `DepositReturnPercent` (10%) of the seat's votes; otherwise forfeit to the crown's treasury (released from the hold and charged with `ChargeMarks` in the same tick). Withdrawing before voting returns it; withdrawing during the vote forfeits it, and the votes for that candidate are dropped.
- **Results.** The Herald announces each seat and RealmChronicle records one `vote_held` entry: *The realm elects its council*, with the winners as actors.

## Referendums

- Only the monarch (`KingsScheme.IsKing`) or staff may ask. One question at a time; `CooldownHours` (24) after the answer before the monarch may ask again. Voting lasts `VotingHours` (24).
- A question needs `MinTurnout` (5) votes; it is carried when more than `PassPercent` (50) per cent say yes (a tie is not carried).
- **A decree** (`Referendum.Decrees`): carried, the next issue of that decree costs no authority (once); refused, the monarch may not issue it for `MandateHours` (72) (staff can). Both through `CrownAndConsequences.SetDecreeMandate`.
- **A law** (`Referendum.Laws`): RealmLaws has no way for another plugin to enact or repeal a law, so the realm's answer is its word. The Herald tells the crown the command to use. If the realm refused, and within `MandateHours` the crown proclaims the law anyway (or repeals the one the realm kept), the Herald says *The crown defies the realm's vote* and the Chronicle records it, once. The law's name comes from RealmLaws when it is in force; a law not in force is named by its id.
- The answer is announced by the Herald and recorded as `vote_held`: *The realm answers the crown*.

## Who may vote (one Steam account, one vote)

The game's player id is the Steam id, so every vote is held under the account that cast it; a new name changes nothing. Against alts and bought or borrowed accounts, an account must (all switchable in `Voters`):

| Rule | Default | Notes |
|---|---|---|
| Be known to the realm | 72 h (`MinAccountAgeHours`) | First sight by this plugin, or the account's `Joined` date in RealmHouses.json if earlier |
| Have played here | 180 min (`MinPlayMinutes`) | Counted by this plugin minute by minute while online; a stalled timer credits at most two ticks. RealmStats keeps no per-player play time (it is pseudonymous by design), so it cannot be used |
| Belong to a house | on (`RequireHouse`) | RealmHouses `GetHouse` |
| ... for long enough | 48 h (`MinHouseMembershipHours`) | From the RealmHouses.json `Joined` date, else first sight |
| ... since before the ballot opened | on (`JoinedBeforeBallot`) | Moving house during a ballot costs your vote in it |
| Not be under new-player protection | `NewPlayersMayVote` off | RealmWarden `IsNewPlayerProtected` |
| Not be an outlaw | `OutlawsMayVote` off | RealmContracts `IsOutlaw`, RealmLaws `IsCourtOutlaw` |
| Not be exiled | `ExilesMayVote` off | RealmLaws `IsExiled` |
| Have found waystones | 0 (`MinWaystones`) | RealmTravel `GetDiscoveredCount`; only asked when RealmTravel is loaded. Off by default: a realm without waystones would have no voters |

Staff can audit a ballot (votes by house, the least-played voters) and strike a vote.

## Config (`oxide/config/RealmHeraldry.json`)

| Key | Default | Meaning |
|---|---|---|
| `General.TickSeconds` | 60 | The plugin's tick (10 to 300) |
| `General.SaveEverySeconds` | 300 | Voter ledger saves (votes and choices save at once) |
| `General.UsePopups` | true | The "The realm votes" window when voting opens (also follows `/realm popups off`) |
| `General.MaxVotersKept`, `ClosedBallotsKept` | 20000, 30 | Caps; voters in an open ballot are never pruned |
| `Heraldry.Enabled` | true | Switch the guild sync off entirely |
| `Heraldry.SyncGuildNames`, `SyncBannerColours` | true, true | What is kept in step |
| `Heraldry.Enforce` | true | Put back what a player changes in the game's guild menu |
| `Heraldry.NameFormat` | `{0}` | The guild name; e.g. `House {0}` (falls back to the plain name past 28 characters) |
| `Heraldry.SyncSeconds`, `MaxAppliesPerSync`, `GuildCooldownSeconds` | 120, 6, 60 | Pace of the sync |
| `Heraldry.BroadcastBanner` | true | Also send the banner to every player, so others' name tags and armour follow at once |
| `Heraldry.GuildFallbackByLeader` | true | Without RealmHouses.json, use the leader's guild when all its members are the house's |
| `Heraldry.ChangeCooldownHours`, `ChangeFeeMarks`, `FirstChoiceFree` | 72, 250, true | Changing colours |
| `Heraldry.UniqueChoices`, `ReserveGreatHousePairs`, `AnnounceNewColours` | true, true, true | |
| `Heraldry.BannerCount`, `PatternCount` | 0, 0 | The client's counts; 0 = indices never set |
| `Heraldry.Pairs` | the 14 above | Id, Name, Field, Charge, FieldName, ChargeName, ReservedFor |
| `Council.Enabled`, `ElectEachSeason` | true, true | |
| `Council.ElectedSeats` | Keeper of Coin, Marshal | Must match the crown's `CouncilSeats` |
| `Council.NominationHours`, `VotingHours`, `TermDays` | 48, 72, 28 | |
| `Council.MinTurnout`, `CandidateDeposit`, `DepositReturnPercent` | 3, 100, 10 | |
| `Council.CandidateMinHouseMembers`, `CandidateMinHouseAgeDays`, `MonarchMayStand` | 3, 3, false | |
| `Council.ElectedHousePoints`, `AllowVoteChange` | 5, true | |
| `Referendum.Enabled`, `Decrees`, `Laws` | true, true, true | |
| `Referendum.VotingHours`, `CooldownHours`, `MinTurnout`, `PassPercent`, `MandateHours` | 24, 24, 5, 50, 72 | |
| `Voters.*` | see the table above | `SeedFromHousesFile` (true) reads `Joined` dates from RealmHouses.json |

## Game API used (research notes)

Confirmed in the 2.0.3867 `Assembly-CSharp.dll` metadata (the plugin compiles against it) and read in the decompiled code (type.member names only; no game code is copied):

- `SocialAPI.Get<GuildScheme>()`: `GuildScheme.TryGetGuild(ulong)`, `TryGetGuildByMember(ulong)`. `Guild.BaseID`, `Guild.Name`, the public field `Guild.Banner` (`BannerData`), `Guild.Members()` → `Members.Has`, `MemberCount`, `EachMember()` → `Member.PlayerId`.
- `BannerData.CurrentBanner`, `CurrentPattern` (int), `CurrentColor`, `CurrentPaternColor` (`UnityEngine.Color`; the game's own spelling). The colours are set by reflection because the compile check has no `UnityEngine.Color`.
- The update path is the game's own: `GuildScheme.SetGuildName` builds `new Guild(guild)` with a new `Name` and raises `GuildUpdateEvent` through `EventManager.CallEvent`; the server's `ServerSupplier.OnBannerSend` raises the same event for a banner. On the server, `ServerSupplier.OnGuildUpdate` calls `Guild.UpdateGuild` (name and banner), sends `GuildBannerUpdateEvent` to the guild's members, `BannerUpdateEvent`, and `GuildNameUpdateEvent` when the name changed. RealmHeraldry builds the same copy with a new `BannerData` and raises `GuildUpdateEvent`; with `BroadcastBanner` it also raises `GuildBannerUpdateEvent(banner, guildId)` (on a dedicated server a network event goes to every player by default).
- What the clients draw from it: `GuildBannerUpdate` (the banner), `CrestSupplier` → `CrestColorsUpdate` (crest flags), `BannerIconSupplier` → `BannerSetByPlayerIDEvent` → `NameTagBannerIcon` (the name-tag icon) and `GuildColoredArmor` (`CurrentPaternColor` is the guild colour on armour, `CurrentColor` the background).
- The game's rename window allows 28 characters (`SocialMenu.ChangeName`, `ValueMaximum`).
- A guild of one player may change its banner in the game's menu (`ServerSupplier.OnBannerSend` checks the member count); larger guilds cannot, which suits a house's colours being set by its head here.
- `SocialAPI.Get<KingsScheme>()`: `HasKing()`, `IsKing(Player)`, `GetKingID()`.
- There is no Oxide hook for a guild rename or banner change (`docs/oxide-rok-api.md` 2.8), hence the polling.
- **Not available, designed around:** a per-player playtime from RealmStats (it is pseudonymous; this plugin counts its own minutes), an API to enact a law in RealmLaws (a law referendum is advisory, with defiance called out), the client's banner and pattern counts (indices are kept unless the owner sets the counts).

## Integration

Calls out (all non-public, through `[PluginReference]` and `Call`; `null` means "not loaded"):

| Plugin | Methods |
|---|---|
| RealmHouses | `GetHouse`, `GetHouseLeader`, `GetMembers`, `GetHouseFounded`, `GetHouseSummaries`; reads `RealmHouses.json` (`Houses[].Name`, `GuildId`, `Members[].Id`, `Joined`) |
| CrownAndConsequences | `GetCouncilSeats`, `SeatElectedCouncillor`, `GetDecreeList`, `SetDecreeMandate` (new, small, non-public; see below) |
| RealmChronicle | `Log` (type `vote_held`, new) |
| RealmTreasury | `ChargeMarks` (colour fee, forfeit deposits), `HoldMarks`, `ReleaseHold` (deposits) |
| RealmSeasons | `GetSeasonNumber`, `GetSeasonName`, `AwardHouse` |
| RealmRenown | `GetRenown` (tie-break) |
| RealmHerald | `PopupsWanted` |
| RealmWarden, RealmContracts, RealmLaws, RealmTravel | `IsNewPlayerProtected`; `IsOutlaw`; `IsCourtOutlaw`, `IsExiled`, `GetActiveLaws`; `GetDiscoveredCount` |
| RealmQuests | `ReportQuestEvent` (`custom`: `vote_cast`, `council_elected`) |

RealmArena, RealmDominion and RealmTravel's waystones and kits were looked at; nothing in an election or a banner needs them beyond the optional waystone rule. Quest content can now use the two custom deeds (for example a weekly "Have your say" task on `vote_cast`).

Calls in (non-public API for other plugins):

| Method | Returns |
|---|---|
| `GetHouseArms(string house)` | `"pairId\|#field\|#charge\|pair name"`, or null for an unknown house (for a portal page or a painted sign) |
| `IsEligibleVoter(string playerId)` | Whether the player may vote now (the general rules) |
| `GetBallotSummary()` | One plain line on what the realm votes on now, or null (for a board) |

**CrownAndConsequences additions** (non-public, about 110 lines): `GetCouncilSeats()`, `GetCouncil()` (`seat\|id\|name\|electedUntil` per seat), `SeatElectedCouncillor(seat, playerId, name, untilIso)` (refuses an unknown seat, a bad id, a term in the past or past 400 days), `GetDecreeList()` (`id\|name`), `SetDecreeMandate(decreeId, passed, hours)` (1 to 720 hours). `/council appoint` and `remove` refuse an elected seat in its term unless staff; a change of monarch keeps elected seats; `/decree` refuses a decree the realm refused (staff excepted) and waives the authority of one it carried, once. Two new data tables, `ElectedUntil` and `Mandates`, load as empty from older files.

## Data

`oxide/data/RealmHeraldry.json`: `Arms` (per house: chosen pair, who and when, indices, the bound guild, what was last applied, drift counts), `Voters` (per Steam id: name, first and last seen, play seconds, house and since when), `Ballots` (open and the last `ClosedBallotsKept` closed: kind, status, times, seats, candidates with their deposit holds, votes per voter, struck voters, the question, results), `LawWatches`, `NextBallotId`, the season last elected. A file that exists but cannot be read, is empty or truncated, or comes from a newer version pauses the plugin, which then never writes it; fix or move it and reload.

## Tests

```
bash plugins/docs/RealmHeraldry/logic-tests/run.sh      # 274 checks
bash tools/exploit-review/run.sh heraldry                # 42 + 33 (real crown) + 14 (real treasury)
```

The logic tests compile `plugins/RealmHeraldry.cs` unchanged with mocks of the guild and banner types (the server's `GuildUpdateEvent` handling modelled on `ServerSupplier.OnGuildUpdate`) and fakes of every plugin it calls. They cover the sync (names, colours, indices kept, drift put back, Enforce off, every option, the pacing, a game that refuses or throws), the guild binding (file, fallback, duplicates, missing guilds), colour choices (reservation, uniqueness, cooldown, fee, a fallen house), the voter ledger and every voter rule, elections (calling, standing, deposits, voting, the count, ties, quorum, a seat the crown refuses, withdrawals, reloads, house hopping), staff tools, referendums (decrees, laws, defiance), data safety, the API, config clamps and the chat style. The exploit suite is in `tools/exploit-review/heraldry/README.md`.

## Smoke test on a real server

Two players, A (staff, `realmheraldry.admin`) and B. Both in game. RealmHouses, CrownAndConsequences, RealmTreasury, RealmSeasons and RealmHerald loaded.

| Step | Do | Expect |
|---|---|---|
| HR1 | Load the plugin; read the log. A: `/heraldry status` | No warning about banner colours; `Colour binding: ok`; RealmHouses.json read |
| HR2 | A founds a house from inside a game guild (or `/house link`) with B in it: House Varrow. Wait 2 min or `/heraldry sync` | The game's guild menu shows the guild named **Varrow**. Note whether the name updates for B without relogging |
| HR3 | Look at the guild's banner in the game's guild menu, a placed crest's flags, both players' armour and the icon by B's name tag (from A) | Plum field, iron-grey emblem on each. Note which ones update at once, which after a relog, which never |
| HR4 | B (not head) renames the guild in the game's menu (if the game lets him). Wait 2 min | The name returns to Varrow |
| HR5 | A: `/heraldry preview`, then `/heraldry colours moss-gold` (Varrow may bear an open pair) | Preview lists the change; the banner turns moss green with bright gold at once |
| HR6 | Open the game's banner menu for a one-player guild and step through banners and emblems; count them | Write the counts into `Heraldry.BannerCount` / `PatternCount`, reload, then `/heraldry banner Varrow 1 1`: the banner shape and emblem change for both players without errors in either client's log |
| HR7 | A: `/ballot admin open`; A: `/ballot stand marshal` (A must meet the voter rules: lower `Voters.*` for the test); `/ballot admin advance 1` | The Herald lines; B sees the "The realm votes" window (note how it looks and whether `\n` breaks lines) |
| HR8 | B: `/vote A's name`; A: `/ballot admin advance 1` | *A is elected Marshal unopposed*; `/council` shows A as Marshal; the deposit is back in A's purse; the Chronicle overlay shows *The Realm Votes* with the ballot icon |
| HR9 | A takes the throne (or the test monarch tries): `/council remove Marshal` as a monarch without staff rights | *The realm elected the Marshal...* |
| HR10 | Monarch: `/ballot propose decree roads`; both `/vote no`; with `Referendum.MinTurnout` 2, `/ballot admin advance <n>`; monarch: `/decree roads` | *The realm answers no*; `/decree roads` is refused for 72 hours |
| HR11 | Restart the server mid-vote | Votes, candidates and deposits survive; the ballot closes on time |

## What is UNVERIFIED

Everything at run time. In particular:

- That a `GuildUpdateEvent` raised by a plugin on the dedicated server renames the guild and changes its banner for clients, as the game's own `SetGuildName` and `OnBannerSend` paths suggest (HR2, HR3).
- Which of the banner, crest flags, armour tints and name-tag icons follow at once, after a relog, or not at all (HR3). `BroadcastBanner` is the plugin's attempt to make other players' clients follow at once.
- That the server's own `Player.Local` passes the `rok.guild.update` permission check for a server-raised event (the game's own server code raises the same event, so it should; HR2).
- The colours as the game renders them (gamma, the emblem's texture) versus the hex in the palette (HR3).
- The client's banner and pattern counts, and that an index within them is safe (HR6).
- Whether a guild's own members can still change its banner or name in the game's menu, and how often players do (HR4); the drift counts in `/heraldry status` show it.
- The "The realm votes" window (HR7), as for every Realm popup.
- That the play time counted here matches what players feel is fair; tune `Voters.*` after the friends alpha.

## Merge notes (shared files this branch touches)

- `plugins/CrownAndConsequences.cs`: the additions above, all additive except three guarded lines in `/council` and `/decree`, and the succession clear that now keeps elected seats.
- `plugins/RealmHerald.cs`: three catalogue entries (`heraldry` under houses; `ballot`, `vote` under crown) and their `Cmd.*` lines.
- Chronicle type `vote_held` in every list (as `holding_taken` was): `plugins/RealmChronicle.cs`, `chronicle/server.js`, `chronicle/public/assets/common.js`, `portal/lib/model.mjs`, `launcher/lib/discord.js`, `launcher/renderer/heraldry.js`, `bot/src/art.js`, `chronicle/public/assets/realm-art.js` (copied to streamkit), `art/icons/event-map.json` and the new `art/icons/ballot.svg` (sprite, PNGs, gallery, painter bundle and the art copies rebuilt); the type counts in `bot/test/art.test.js`, `portal/test/art.test.mjs`, `streamkit/test/art.test.js` go from 43 to 44. If another branch adds a type at the same time, the counts add up and the art must be rebuilt once after merging.
- `tools/exploit-review/run.sh`: the `heraldry` case. `docs/community/ops/staff-roles-and-permissions.md`: the `realmheraldry.admin` row and grant line. `docs/saga/COMMANDS.md`: regenerated.
