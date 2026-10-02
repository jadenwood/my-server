# The Realm of Ostreval

Ostreval is the original setting of the Realm server. It is not taken from any book or TV series. Do not bring in names from other franchises.

Each in-game system on this server has a matching piece of lore: houses, oaths, treaties, the crown, decrees, the council, claims, rebellion windows, ransom and the Chronicle. The lore explains why the rules are what they are. **The plugins are the final word.** If this page and `/help` text disagree, the plugin wins and this page should be fixed.

---

## The short history

**The Builder's Throne.** Long ago, Merewin the Builder raised a throne of black stone on the hill at the centre of the valley. Every road in Ostreval still leads to it. Players know it as the Old Throne, the ancient throne in the game. The Builder's line ruled from it for nine generations.

**The Long Thaw.** Queen Isolde the Quiet was the last of the Builder's blood. She died in the spring of the Long Thaw. She had no heir and named no successor. For a season the great houses fought over the empty seat. Fields burned, the ransom-pits filled, and the roads closed.

**The Hearth Charter.** The houses had bled themselves dry. They met at the Hearth, the great fire below the throne hill, and swore a charter. The Charter is the law of the realm today. It says:

1. **The crown belongs to the seat, not the blood.** Whoever holds the Old Throne is the monarch. No bloodline has a right to it.
2. **The crown's power is lent, not owned.** A monarch spends *authority* (the realm's patience) to issue decrees. Authority recovers slowly, and every decree must wait its turn.
3. **The crown rules with a council.** There are three seats: the **Voice of the Crown**, the **Keeper of Coin** and the **Marshal**. They are empty again whenever the crown changes hands.
4. **No one may take the crown by stealth.** A house that wants the throne must first *declare its claim* in public. It may fight for the throne only in the **Lawful Hours**, the rebellion windows named by the Charter. An empty throne is the exception: anyone may sit on it.
5. **No captive is held forever.** A ransom has a limit. The time anyone may be held is short and fixed. Once a captive is freed, they may not be taken again for a while.
6. **The crown's tax has a ceiling.** No monarch may take more than the Charter allows.
7. **All of this is written down.** The Chronicle records every coronation, oath, treaty, betrayal, decree, claim, rebellion and ransom, so the realm remembers.

**Why the crown is still contested.** The Charter put an end to open war, not to ambition. Any house can sit on the throne, so every house is tempted to try. Each monarch knows that a declared claim may already be on the Chronicle, and that its rebellion window is days or hours away. Oaths bind houses together, but nothing in the Charter forbids breaking them. It only makes sure everyone *knows* you did.

---

## The six great houses

These six houses are the starting families of Ostreval. They are also the houses in the Chronicle sample data (`chronicle/sample-data/`). Players can **found or claim** them, and anyone may also found a house of their own.

**How claiming works.** The plugin does not reserve these names: the first player to run `/house found <Name> <sigil>` owns that name. So that a house goes to an actual group and not to whoever types fastest, the community runs a claim sign-up before each launch phase (see `launch-plan.md`). Admins can disband a house that squats a name against the sign-up (see `rules.md`).

**Colours.** The overlay picks each banner's colour from a hash of the house name (`chronicle/public/assets/common.js`, `dyeFor`). A house's on-stream colour is therefore set by its name, not by choice. The *primary* colour listed below is the exact dye the overlay gives that name, so the lore matches what viewers see. The *secondary* colour is lore only, for player-made banners, Discord roles and thumbnails.

To found one of them in game (sigils fit the plugin limits: 32 characters at most, using only letters, digits, spaces, `'` and `-`):

```
/house found Varrow Iron Stag
/house found Ashgrove White Oak
/house found Corvane Black Raven
/house found Dunmere Drowned Bell
/house found Halloran Ember Hound
/house found Merrin Silver Eel
```

### House Varrow: the Iron Stag
- **Sigil:** an iron-grey stag, rearing, on a plum field.
- **Colours:** plum (`#4a2347`, overlay dye) and iron grey.
- **Words:** *"We Stand Our Ground."*
- **Seat:** a stone keep on the hill road below the Old Throne.
- **History:** The Varrows were the Builder's masons and later the throne's wardens. They believe the Charter was written to keep *them* from the crown, and they have spent three generations proving it can be won lawfully.
- **Play-style hook: the crown-holders.** For groups who want to *take and keep* the throne: run the council, time decrees, and defend against declared rebellions in the Lawful Hours. Expect every other house to declare against you.

### House Ashgrove: the White Oak
- **Sigil:** a white oak, roots and branches spread, on a rust-red field.
- **Colours:** rust red (`#7a3a1a`, overlay dye) and bone white.
- **Words:** *"Deep Roots, Long Memory."*
- **Seat:** the orchard-holds of the eastern vale.
- **History:** The Ashgroves fed the realm through the Long Thaw and lost the most because of it. They keep oaths and never forget who broke one.
- **Play-style hook: the steady liege or loyal vassal.** For builders, farmers and players who like diplomacy. Swear to a strong house, or gather vassals of your own. Ashgrove's reputation is its clean record in `/house info`, so an Ashgrove that breaks an oath is news.

### House Corvane: the Black Raven
- **Sigil:** a black raven with a silver key in its beak, on a slate field.
- **Colours:** slate (`#2c3b42`, overlay dye) and silver.
- **Words:** *"Every Secret Has a Price."*
- **Seat:** the tower of Corvane's Reach, overlooking the river crossing.
- **History:** The Corvanes kept the Builder's letters, and they say they still have some that were never opened. They drafted most of the Hearth Charter's wording, loopholes included.
- **Play-style hook: intrigue.** For players who enjoy treaties, double dealing and timing. Sign many treaties (`/treaty propose`), break the right one at the right moment, and use the Chronicle to read the realm. If you can win the crown by persuasion, do.

### House Dunmere: the Drowned Bell
- **Sigil:** a bronze bell sunk beneath three wavy lines, on an olive field.
- **Colours:** olive (`#5a5a22`, overlay dye) and tarnished bronze.
- **Words:** *"The Tide Returns."*
- **Seat:** the flooded town of Dunmere, whose bell still rings at low tide.
- **History:** Dunmere held the throne for a single night during the Long Thaw. They were thrown down and their town was flooded. They have never accepted the verdict.
- **Play-style hook: the rebels.** For groups who want the *drama of the claim*: declare against whoever reigns, rally vassals, and fight in the Lawful Hours. Dunmere is built around `/claim declare` and the rebellion windows. Losing often is part of the role.

### House Halloran: the Ember Hound
- **Sigil:** a hound wreathed in embers, on a dark umber field.
- **Colours:** dark umber (`#3a2a1a`, overlay dye) and ember orange.
- **Words:** *"Loyal Until the Last Coal."*
- **Seat:** the forge-camps of the southern pass.
- **History:** The Hallorans were the Builder's huntsmen and became sellswords when the line ended. Their loyalty is real but has a contract term.
- **Play-style hook: hired swords and ransomers.** For fighting groups. Sell your blades through timed treaties. Take captives and name fair ransoms. Be the house both sides hire before a Crown Night. Ransom has a hard limit in time and amount (see `how-to-play.md`), so Halloran plays it fast and fair.

### House Merrin: the Silver Eel
- **Sigil:** a silver eel coiled through a broken net, on a deep green field.
- **Colours:** deep green (`#24472d`, overlay dye) and silver.
- **Words:** *"Slip the Net."*
- **Seat:** the river market at Merrin's Ford.
- **History:** Merrin was a family of ferrymen who became merchants. They grew rich in the Long Thaw by carrying goods for every side. Nobody quite trusts them, and everybody needs them.
- **Play-style hook: traders and go-betweens.** For players who want to stay neutral and still matter. Run the roads (Open Roads is Merrin's favourite decree), broker treaties, and carry ransoms between houses. Getting away from captors and from obligations is Merrin's way.

---

## Lore glossary (in-game term ↔ lore term)

| In game (plugin) | In Ostreval |
|---|---|
| The throne / king | The Old Throne; the monarch is called *the Crown* in proclamations |
| Crown authority (`/crown`) | The realm's patience |
| Decree (`/decree`) | A proclamation read by the heralds |
| Council seats (`/council`) | Voice of the Crown, Keeper of Coin, Marshal |
| Claim (`/claim declare`) | Raising a claim at the Hearth |
| Rebellion window | The Lawful Hours |
| Oath (`/swear`) | Bending the knee |
| Renounce (`/renounce`) | Breaking the oath (the house gets the oathbreaker mark) |
| Treaty (`/treaty`) | A sealed truce for a set number of days |
| Ransom (`/ransom`) | The ransom-glass, the Charter's time limit on holding a captive |
| Chronicle (`/chronicle`, overlay, `/realm` page) | The Chronicle of Ostreval |
| Chronicle event types | `coronation`, `abdication`, `claim_declared`, `rebellion_started`, `rebellion_ended`, `house_founded`, `oath_sworn`, `oath_broken`, `treaty_signed`, `treaty_broken`, `decree`, `ransom_set`, `ransom_paid`, `released` (from `plugins/RealmChronicle.cs`, `KnownTypes`) |

## Writing guidelines for community content
- Use only original names. Do not borrow from any franchise.
- Ostreval is grim but readable: betrayal, debt and pride. No gore for its own sake.
- Lore never promises a mechanic that the plugins do not have. For example, **the King's Peace and Open Roads are proclamations only**: no code stops combat while they are in force. If keeping or breaking them matters, it matters because players make it matter.
