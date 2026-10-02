# Notable Places of Ostreval

Twelve places for *The Hollow Crown*. The names and stories are original to this server. They are tied to the **kind** of terrain, not to any named spot on the game's map, so any Steward team can lay them over the world they run. Choose the real spots before launch, write them into the calendar template, and keep them for the whole season.

Each entry has:

- **Lore:** what the realm says about the place. This is written for players and is safe to quote.
- **Place it at:** the kind of terrain to look for.
- **Mechanics:** which plugin features meet there. Most of this is roleplay: apart from the Old Throne and the two law zones, no code knows where these places are.
- **Story use:** where it appears in the saga.

**Law zones.** RealmLaws knows two places by default: **Crown Market** and **Hearth**. Both are towns, and both start at placeholder coordinates. Before Act II a Steward stands in each place and sets the zone (Act I run-sheet, step S6). Do **not** mark the other places as towns. The law `no_building_towns` (*The Builder's Reserve*) blocks building in **every** town zone except by the crown's house, so a house seat marked as a town would lock its own house out.

---

### L01 The Old Throne

**Lore:** Merewin the Builder raised a throne of black stone on the hill at the centre of the valley, and every road in Ostreval still leads to it. Nine generations of the Builder's line sat it. Since the Hearth Charter it belongs to whoever holds the seat. The stone is worn smooth on the left arm, where generations of hands have gripped it while waiting to be thrown down.

**Place it at:** the ancient throne that the game itself places in the world. It is the only place in this list that the game defines.

**Mechanics:** throne capture and `coronation`. Capture is open to anyone while the throne is vacant. Otherwise it is open only to declared claimants in the Lawful Hours (CrownAndConsequences). Crown Night is fought here, and so are the King's Hunt's hardest kills. `/crown` reports who holds it.

**Story use:** every act. The "hollow crown" of the season's title rests here.

### L02 The Hearth

**Lore:** The great fire below the throne hill, where the houses swore the Charter at the end of the Long Thaw. It has never been allowed to go out. Claims are "raised at the Hearth", and oaths and treaties are said to be sworn over its coals. The Stewards of the Seat keep it, and it is the one place where the Hearth-Herald's voice is said to carry to every corner of the realm.

**Place it at:** open, flat ground within sight of the throne, wide enough for every house to stand under its banner at once. Build a large fire pit there before launch, using only ordinary in-game building.

**Mechanics:** the RealmLaws zone `Hearth` (a town zone), set with `/law zone set Hearth 40 town` while standing at its centre [this run: RealmLaws]. Because it is a town, the laws `no_building_towns` and `no_binding_towns` apply here when proclaimed.

**Story use:** P01, P08 and P29. The Hearth Truce on the last Sunday gathers the realm here.

### L03 The Crown Market

**Lore:** Under the Builders, every trader who came to the throne paid a stall-penny here. The stalls are rebuilt every season on the same foundations. The traders say the cobbles remember every price ever paid on them, and that it is unlucky to haggle on the first stone.

**Place it at:** a crossroads or open square on a main road near the throne. It should be easy to reach from every house seat.

**Mechanics:** the RealmLaws zone `Crown Market` (a town zone), set with `/law zone set Crown Market 60 town` [this run: RealmLaws]. The laws `kings_peace` (blows here are blocked and recorded; UNVERIFIED in game) and `market_curfew` (no lingering between 22:00 and 06:00 UTC) are written for this zone. `/market` trades are made anywhere, but the market belongs here in the story [this run: RealmTreasury].

**Story use:** P13, P16 and the first trial in Act II. Legend Q05 is set here.

### L04 The Tollbridge

**Lore:** The Builders spanned the great river with a single bridge and charged a coin to cross it. The bridge has fallen and been rebuilt a dozen times. Every monarch claims the toll, and every trader swears they paid it last time.

**Place it at:** the bridge or shallow crossing over the largest river nearest the throne. If there is no bridge, build a simple one before launch.

**Mechanics:** the law `bridge_toll` (*The Bridge Toll*). It is a **declared** law: no code sees a toll unpaid, so it is only a crime when the crown or council accuses someone in court. Toll-keepers can be hired through `/contract post merc` during a rebellion. Otherwise it is roleplay.

**Story use:** Act II, where it makes a good first accusation for a monarch who wants to test the court. Legend Q07.

### L05 The Listing Field

**Lore:** Under the Builders the realm's champions fought here for a wreath of oak. Ashgrove still sends the wreath every season, out of habit and a little spite. Whoever wins the lists stands on the old stone at the field's head, which is known only as *the Stone*.

**Place it at:** a wide, flat, open field near the Hearth, with good sightlines for spectators and streamers.

**Mechanics:** the Royal Tournament (RealmEvents, Fridays) [this run]. Kills between entrants count anywhere in the realm, so the field is where the realm *agrees* to fight, not a rule. `tournament_champion` is chronicled, and the champion earns the title *Champion of the Lists* (RealmRenown) [this run].

**Story use:** P18, the Founding Lists in week 1 and the Lists of the Charter in Act II. Legend Q04.

### L06 The Crown Wold

**Lore:** A dark, old forest that once belonged to the Builders' huntsmen, who later became House Halloran. By old custom any monarch may name a quarry here and loose the realm on them. The quarry is said to have the right to run, but not the right to hide.

**Place it at:** the largest stretch of forest within an hour's walk of the throne.

**Mechanics:** the King's Hunt (RealmEvents, Wednesdays) [this run]. The monarch names the quarry with `/hunt name <player>`, and kills of the quarry anywhere count, so the Wold is the traditional ground, not a boundary. `hunt_kill` is chronicled. *Crown's Huntsman* is earned by three quarry taken.

**Story use:** P21. Legend Q06.

### L07 Varrow's Stair

**Lore:** The seat of House Varrow is a stone keep cut into the hill road below the Old Throne, with steps worn by nine generations of masons going up to work. The Varrows say they built the throne's stair and so have the right to climb it. The other houses say the Varrows built a great many stairs.

**Place it at:** the steepest approach road to the throne, on its lower slope.

**Mechanics:** a house seat (RealmHouses). It is not a law zone.

**Story use:** the crown-holders' seat in Act I and Act III. When Varrow holds the throne, the Stair is the crown's last line.

### L08 Ashgrove Vale

**Lore:** The orchard-holds of the eastern vale fed Ostreval through the Long Thaw and were burned for it. The oldest trees are scarred black at the root, and the Ashgroves will not cut them. An Ashgrove oath is sworn with a hand on one of the scarred trunks.

**Place it at:** gentle farmland or meadow on the east side of the map.

**Mechanics:** a house seat. A good place for `/vault` deposits and for delivery contracts (`/contract post delivery`).

**Story use:** P05. Ashgrove's clean oath record is the season's quiet benchmark. Legend Q02.

### L09 Corvane's Reach

**Lore:** A tall tower over the river crossing, home of House Corvane and the realm's oldest rookery. Corvane kept the Builder's letters and claims that some were never opened. Every raven in Ostreval is said to stop at the Reach before it goes where it was sent.

**Place it at:** the highest ground overlooking a river crossing. Build or mark a tower there.

**Mechanics:** a house seat. In the story, the home of ravens, spymasters and rumours (`/raven`, `/raven spymaster <player>`, `/rumour <text>`) [this run: RealmRavens]. No code ties ravens to this place.

**Story use:** P23 and the lost-winter mystery. Legend Q08.

### L10 Drowned Dunmere

**Lore:** House Dunmere held the throne for a single night in the Long Thaw. They were thrown down and their town was flooded. The bell of Dunmere's hall still hangs under the water, and they say it rings at low tide whenever the throne is about to change hands.

**Place it at:** a lakeshore or coast with half-sunken ruins or low ground. If there is nothing like that, a shoreline with a broken wall will do.

**Mechanics:** a house seat. The bell is a story: no code rings it.

**Story use:** P26, the Night of Bells. It is the home of the rebels' claim in every act. Legend Q03.

### L11 The Ember Pass

**Lore:** The forge-camps of the southern pass, where House Halloran's huntsmen became sellswords. The old ransom-pits are cut into the pass's walls. The Charter shut them, but Halloran still keeps them swept, "in case the Charter changes its mind".

**Place it at:** a narrow valley or pass to the south, with rock walls.

**Mechanics:** a house seat. In the story, the home of mercenary contracts (`/contract post merc`) and ransoms (`/ransom`). Ransom limits apply everywhere.

**Story use:** P24 and Act III. Legend Q09.

### L12 Merrin's Ford

**Lore:** A river market at a shallow ford, run by the descendants of ferrymen who carried every side's goods through the Long Thaw and grew rich doing it. Nobody quite trusts Merrin's Ford, and everybody shops there.

**Place it at:** a shallow river crossing downstream of the Tollbridge.

**Mechanics:** a house seat. In the story, the realm's second market: delivery contracts, market listings (`/market sell`) and ransom go-betweens.

**Story use:** P16 and P17. It is neutral ground in Act III. Legend Q10.
