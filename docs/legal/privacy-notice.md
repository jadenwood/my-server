# Realm privacy notice

**Applies to:** the Realm game servers and their plugins, the **Realm** player app, **Realm Steward** (the owner's app), the **Realm Portal** website, the **Realm Discord bot** and Discord herald, and **Realm analytics**.

**Last updated:** [date the Owner publishes it]. **Who is responsible:** [Owner's name or community name], contact: [privacy contact email]. Realm is a volunteer community project. It is not a company and is not affiliated with the makers of Reign of Kings, with Valve (Steam) or with Discord.

> **Not legal advice.** This notice was written from what the Realm code actually does (each claim points to the code or doc it comes from). Whether it meets the data-protection law where the Owner and players live (for example the GDPR in the EU or UK) has **not** been checked by a lawyer. The Owner fills in the brackets and should get it reviewed before going public.

---

## The short version

- Realm **does not sell or share your data**, shows **no ads**, and has **no tracking scripts** or third-party analytics.
- The game server necessarily sees your **Steam ID, in-game name and IP address** when you play. Realm's plugins store your Steam ID and name to run houses, the crown, laws, letters and moderation.
- What you do in public in the game (founding a house, taking the crown, breaking an oath) is written to the public **Chronicle** under your **in-game name**. That is part of the game. It never shows your location, inventory or Steam ID.
- Play statistics are kept under a **pseudonymous key**, not your Steam ID, for 120 days. Type `/stats optout` to stop that.
- Private letters (`/raven`) are deleted after 7 days. Staff can only read one if it is reported.
- You can ask what Realm holds about you, or ask for it to be deleted: see [`data-deletion-process.md`](data-deletion-process.md).

---

## 1. What is collected, where, and for how long

### 1.1 The game server and Realm plugins

The Reign of Kings server itself receives your Steam ID, name and IP address when you connect, and writes some of this to its own log files (for example `Authentication verified for <name> (<steamid>).`, `docs/server-reference.md`). Realm cannot change what the game logs.

| Data | Where it is stored | Why | Kept for |
|---|---|---|---|
| Steam ID, name, house membership, oaths, treaties, marks | `oxide/data/RealmHouses.json` | Houses and oaths | While the house exists; reset on a world wipe |
| Monarch, council, claims, captives, ransoms | `oxide/data/CrownAndConsequences.json` | The crown and ransom rules | Current state; history goes to the Chronicle |
| Contracts, escrowed items, poster and fulfiller | `oxide/data/RealmContracts.json` | Contract escrow | Until settled, then as history until a wipe |
| Laws, crime records, cases, sentences | `oxide/data/RealmLaws.json` | Laws and trials | Until a wipe |
| Dynasties, renown, titles, treasury vaults and market orders, season standings, event results | Each plugin's file in `oxide/data/` | The game systems | Until a wipe (the Hall of Kings is kept across wipes: `RealmLegends.json`) |
| **Private letters** (`/raven`): text, sender, recipients | `oxide/data/RealmRavens.json` | Letters between players | Inbox: **7 days** by default (at most 30). Players unseen for 60 days with nothing stored are removed (`plugins/docs/RealmRavens.md`) |
| **Letter audit**: who wrote to whom, letter text, reports, blocks | same file | Stops harassment through anonymous letters | **At most 7 days**, enforced by the plugin |
| **Moderation records**: Steam ID, name, first and last seen, playtime, flags (combat log, chat flood, offensive name, raid-hour hits), mutes, reports, and **evidence lines that include your in-game position** and up to 80 characters of a blocked chat message | `oxide/data/RealmWarden.json` (capped) and `oxide/logs/RealmWarden/*.txt` | Fair play and moderation | Data file: capped (oldest dropped). Log files: **12 months**, deleted by the Owner monthly (policy; not automatic) |
| **Play statistics**: a pseudonymous key (a keyed hash of your Steam ID), sessions (start and length, with **no** key on them), joins and leaves per hour, deaths by cause, house activity | `oxide/data/RealmStats/` | Understanding how many people play and when | **120 days** (`RetentionDays`), deleted automatically. Never names, Steam IDs, IPs, chat or positions (`plugins/docs/RealmStats.md`) |
| Game server log files (written by the game, not Realm): names, Steam IDs, connection messages | `<server>\Logs\Log[...].txt` | Diagnosing joins and crashes; evidence | **90 days**, deleted by the Owner (policy; not automatic) |
| **The Chronicle**: public acts, with **in-game names** of the people involved | `oxide/data/RealmChronicle.json` | The public history of the realm, shown on the overlay, portal and Discord | The newest **500** events (`MaxEvents`); older ones roll off. Copies may exist in Discord posts and portal builds |

### 1.2 The Realm player app

- **Stored on your PC only:** your settings (`%APPDATA%\Realm Player\realm-player.json`: whether you finished onboarding, join method, notifications, background mode, and a cached copy of the server list), and the house you chose under "Swear your allegiance" (in the app's local storage). None of this is sent anywhere.
- **Network requests it makes:** it downloads the signed server list from the address the Owner publishes (for example GitHub Pages); it asks each listed game server for its status (a Steam query on the server's port); and for servers that publish a Chronicle address it reads that server's public Chronicle. Each of those servers sees your IP address, as any website or game server does.
- It asks **Steam** to start the game. It contains **no** analytics, crash reporting, accounts or tracking (`launcher/player/main.js`).

### 1.3 Realm Steward (used only by the server Owner)

Runs on the Owner's PC. It keeps server settings, backups, a **Court log** of moderation actions (`%APPDATA%\Realm\court\court-log.jsonl`: time, server, action, target name, reason, result; rotated at 2 MB, the previous file kept), and app logs that can include server console lines with player names and Steam IDs. It sends nothing about players to anyone, except the Discord herald below if the Owner turns it on. Its **Copy report** feature removes public IPs, Steam IDs, the Steam auth ticket, passwords and the Windows user name (`README.md`).

### 1.4 Realm Portal

A static website built from the server's data. It shows public information only (the Chronicle, houses, the Hall of Kings). **It never shows Steam IDs**, locations, inventories or IP addresses; the build drops them and tests check this (`portal/README.md`). It loads no third-party scripts, fonts or trackers. The company hosting the website (chosen by the Owner, for example [GitHub Pages]) receives visitors' IP addresses in its server logs under its own privacy policy.

### 1.5 Discord bot and Discord herald

- The **herald** posts public Chronicle events to a Discord channel. It sends nothing that is not already public in the Chronicle.
- The **bot** answers `/realm` commands with public realm information. It does **not** read messages (it uses only the `Guilds` gateway intent). If the Owner turns on house roles, `/realm swear` stores your **Discord user ID and the time** of your last oath (in `bot/state/bot-state.json`) to apply the cooldown, and gives you a "Sworn to <House>" role. That role is **self-declared**: it is not linked to your Steam account. Use `/realm forswear` to remove it.
- Discord itself processes everything you do in Discord under **Discord's** privacy policy.

### 1.6 Analytics dashboard

The Owner can build an offline HTML dashboard from the play statistics (`analytics/`). It shows **totals only**: no keys, no names. Houses with fewer than 3 active players are grouped so that one person's play times are not revealed.

### 1.7 Backups and operations

Backups of the world and the plugin data (which include the data above) are kept **14** copies locally and **30 days** off-site by default (`ops/config/realm-ops.example.json`). Uptime-monitor logs: 30 days. Deleted data can therefore survive in a backup until that backup expires. Backups are encrypted at rest only if the Owner's storage provides it: **UNVERIFIED** per deployment.

### 1.8 Moderation case notes and appeals

Staff keep case notes (who, what rule, evidence, decision) privately for **12 months** after the last penalty. For a permanent ban, the Steam ID, date, rule and a one-line summary are kept while the ban stands (`docs/community/ops/moderation-handbook.md` §3). The game's own ban list keeps the name, Steam ID and possibly the IP address of banned players (`/banlist` shows `(ip)`, `docs/admin-console.md`).

## 2. What Realm never does

- Sell, rent or trade personal data, or use it for advertising.
- Put Steam IDs, IP addresses, locations or inventories on any public page, overlay or Discord post.
- Read private letters without a report or a logged harassment case (`docs/community/ops/staff-roles-and-permissions.md` §4).
- Install anything on your PC or change your game files.
- Ask for your Steam or Discord password.

## 3. Why (purposes and, where it applies, legal basis)

| Purpose | Data | Basis (where data-protection law applies; **UNVERIFIED, not legal advice**) |
|---|---|---|
| Running the game and its systems | Steam ID, name, game state | Needed to provide the service you chose to join |
| Fair play, moderation and safety | Moderation records, letter audit, ban list, case notes | Legitimate interest in a safe community |
| Public Chronicle and streams | In-game names and public acts | Legitimate interest; it is the core of the game, and you choose your in-game name |
| Statistics | Pseudonymous key, sessions | Legitimate interest; you can opt out at any time |
| Discord house roles | Discord user ID, oath time | Your choice to use `/realm swear` |

## 4. Your choices and opt-outs

| You want to... | Do this |
|---|---|
| Stop being counted in statistics | `/stats optout` in game. Your key is removed from every kept day; you then count only in anonymous totals. `/stats optin` undoes it. `/stats privacy` and `/stats me` show what is kept |
| Not appear by your real name anywhere | Choose an in-game name that is not your real name. The Chronicle uses only your in-game name |
| Stop the player app's notifications or background checks | Turn them off in the app's settings. Uninstalling the app removes it; delete `%APPDATA%\Realm Player` to remove its settings |
| Remove your Discord house role and stop the bot storing an oath time | `/realm forswear`, and stop using `/realm swear`. Ask for the stored time to be deleted (section 5) |
| Block someone's letters | `/raven block` (see `plugins/docs/RealmRavens.md`) |
| See or delete what Realm holds about you | [`data-deletion-process.md`](data-deletion-process.md) |

You cannot opt out of the game server seeing your Steam ID and IP address, or of moderation records, while you play: the game and fair play depend on them.

## 5. Your rights

You can ask to **see** the personal data Realm holds about you, to **correct** it, to have it **deleted**, or to **object** to a use of it. Follow [`data-deletion-process.md`](data-deletion-process.md), or write to [privacy contact email]. We answer within 30 days. Some data may be kept after a deletion request where needed for safety (for example a permanent ban record), and we will tell you if so.

If you are in a country with a data-protection regulator, you can also complain to it.

## 6. Children

Realm is not directed at children under 13, and Discord and Steam require users to be at least 13 (higher in some countries). If you believe a child under 13 has given Realm personal data, contact us and we will delete it.

## 7. Where data is stored

On the Owner's server PC or rented server (in [country/region]), in the off-site backup bucket (in [region]), on the website host, and in Discord. Data may cross borders when these are in different countries.

## 8. Changes

Changes to this notice are announced in the news feed and Discord before they take effect. The "Last updated" date changes with each version.
