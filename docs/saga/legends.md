# Legends of the Hollow Crown

Ten side-quests for Season 1. Each one uses only commands that already exist, and each is proven by something everyone can check: the Chronicle (`/chronicle`, the overlay, the portal), `/house info`, `/titles` or the season standings. Stewards never set up a legend for a player and never hand out items for one.

**How a legend is claimed.** The player or house posts the proof in the community's `#legends` channel: the Chronicle entries, by number or screenshot, plus whatever the **Proof** line asks for. A Steward checks the proof against the Chronicle and adds the name to the *Roll of Legends*, a pinned Discord post. Recognition is the only reward the Stewards give. Titles and renown come from the plugins by themselves.

**Fair play.** A legend that was arranged (two friends trading a throne, a staged kill, a ransom paid by an alt) does not count. `docs/community/rules.md` applies to every legend, and the Stewards' reading of it is final.

Each legend has:

- **Goal:** what to achieve.
- **Steps:** the commands that do it.
- **Proof:** what to show, and where to read it.
- **Chronicle:** the event types that must appear. The checker makes sure every one of them is registered.
- **Plugin reward:** any title or renown the plugins grant by themselves.

---

### Q01 First of the Hollow Crown

*Place: L01 The Old Throne.*

**Goal:** Be the first player this season to sit the empty Old Throne and be crowned.

**Steps:** While `/crown` says the throne is vacant, take the ancient throne. The Charter's vacant-seat rule lets anyone do this. Hold it long enough for the coronation to be written.

**Proof:** the season's first `coronation` entry names you. `/season hall` lists your reign as the earliest of this season (the Hall shows the newest reign first) [this run: RealmSeasons].

**Chronicle:** `coronation`

**Plugin reward:** renown for every day on the throne (RealmRenown `reign_day`). After seven days in total you earn the title *The Long Reign* [this run: RealmRenown].

### Q02 The Scarred Oak

*Place: L08 Ashgrove Vale.*

**Goal:** Swear an oath in Act I and keep it, with no oath and no treaty broken by your house, until the season ends.

**Steps:** In weeks 1-2, the house leader types `/swear <house>` and the liege accepts with `/swear accept <house>`. Never `/renounce`, never `/treaty break`.

**Proof:** an `oath_sworn` entry for your house from weeks 1-2. At season end `/house info <house>` shows no broken oaths or treaties, and `/season house <house>` shows no treaties broken.

**Chronicle:** `oath_sworn`, `season_ended`

**Plugin reward:** none directly. Kept treaties count for your house in the standings, and RealmDynasties prestige counts days of kept oaths [this run].

### Q03 When the Bell Rings

*Place: L10 Drowned Dunmere.*

**Goal:** Declare a claim and take the throne in the Lawful Hours of a Saturday Crown Night.

**Steps:** The house leader types `/claim declare` at least an hour before a Saturday window. On the night, members take the throne while the window is open.

**Proof:** a `claim_declared` entry for your house, followed by a `rebellion_ended` entry that says your house prevailed, both on the same Saturday window.

**Chronicle:** `claim_declared`, `rebellion_started`, `rebellion_ended`

**Plugin reward:** *Usurper* for the player who wears the crown, and *Kingmaker* for housemates who were online in the Lawful Hours (RealmRenown) [this run]. Crown Night points also go to your house (RealmEvents) [this run].

### Q04 Twice upon the Stone

*Place: L05 The Listing Field.*

**Goal:** Win the Royal Tournament twice in one season.

**Steps:** On a tournament Friday, `/tourney join` and score the most kills among entrants. Housemates do not count. Do it again on a later Friday.

**Proof:** two `tournament_champion` entries with your name.

**Chronicle:** `tournament_champion`

**Plugin reward:** *Champion of the Lists* after the first win (RealmRenown), plus tournament prizes and season points for your house (RealmEvents) [this run].

### Q05 By Steel, Not by Law

*Place: L03 The Crown Market.*

**Goal:** As the accused in a court case, demand trial by combat and be acquitted.

**Steps:** When you are accused (`/court cases` shows the case), type `/court combat <case>`. The accuser names a champion with `/court champion <case> <player>`. Win the fight inside the combat window [this run: RealmLaws].

**Proof:** an `accusation` entry naming you, a `trial_by_combat` entry, and a `verdict` entry saying you were acquitted, all for the same case.

**Chronicle:** `accusation`, `trial_by_combat`, `verdict`

**Plugin reward:** after an acquittal you cannot be accused again for a while (RealmLaws), and the crown loses some of its accusation quota.

### Q06 The Quarry Who Lived

*Place: L06 The Crown Wold.*

**Goal:** Be named quarry of the King's Hunt and survive until it ends.

**Steps:** The monarch names you with `/hunt name <player>`, and `/hunt` shows you as still at large. Do not die to a hunter from outside your house before the hunt ends.

**Proof:** the `event_ended` entry for the King's Hunt lists you among the survivors.

**Chronicle:** `event_ended`

**Plugin reward:** survive points for your house (RealmEvents) [this run].

### Q07 Keeper of the Toll

*Place: L04 The Tollbridge.*

**Goal:** As monarch, proclaim the Bridge Toll and keep it in force for seven days in a row.

**Steps:** The monarch types `/law proclaim bridge_toll`. Keep the crown and do not repeal the law. A change of monarch makes the old laws lapse, so the crown has to hold all week [this run: RealmLaws].

**Proof:** a `law_proclaimed` entry for *The Bridge Toll* and no `law_repealed` entry for it in the next seven days. `/law list` still shows it on day seven.

**Chronicle:** `law_proclaimed`, `law_repealed`

**Plugin reward:** none directly. The reign itself earns renown each day.

### Q08 Every Secret Has a Price

*Place: L09 Corvane's Reach.*

**Goal:** Have treaties with three different houses at the same time, and break none of them before their terms end.

**Steps:** The house leader types `/treaty propose <house> [days]` to three houses, and each accepts with `/treaty accept <house>`. `/treaty list` shows all three at once.

**Proof:** three `treaty_signed` entries for your house with three different houses, a screenshot of `/treaty list` showing all three in force together, and no `treaty_broken` entry for your house until they end.

**Chronicle:** `treaty_signed`, `treaty_broken`

**Plugin reward:** each treaty kept to term counts for your house in the standings (RealmSeasons) [this run].

### Q09 The Fair Captor

*Place: L11 The Ember Pass.*

**Goal:** Take three captives over the season, set a ransom for each, and have every one paid before the law frees them.

**Steps:** Bind a captive, then type `/ransom set <player> <amount>`. When the ransom is paid, record it with `/ransom paid <player>` and free them with `/ransom release <player>`. Never let a term run out.

**Proof:** three `ransom_set` entries with you as captor, each followed by a `ransom_paid` entry for the same captive. No `released` entry for these captives may read *"The term of captivity has ended; by the realm's law ... is free"*: that wording means the term ran out.

**Chronicle:** `ransom_set`, `ransom_paid`, `released`

**Plugin reward:** none. In Ostreval a fair captor is paid in reputation.

### Q10 Warden of the Roads

*Place: L12 Merrin's Ford.*

**Goal:** Fill five delivery orders for the realm's traders.

**Steps:** `/contract list delivery`, then `/contract info <id>` to see what is wanted. Gather the goods into your inventory and type `/contract deliver <id>`. (`/contract accept` is only for mercenary contracts.) Do this five times, for at least two different posters.

**Proof:** five `contract_fulfilled` entries with your name, for at least two posters. `/titles all` shows *Warden of Roads* as earned.

**Chronicle:** `contract_fulfilled`, `title_earned`

**Plugin reward:** the title *Warden of Roads* (RealmRenown) [this run], renown for each delivery, and contract points for your house (RealmSeasons) [this run].
