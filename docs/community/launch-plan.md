# Realm community launch plan

The launch is phased: closed test with friends, then a creator test night, then open weekends. Each phase has a **checkpoint** that must pass before the next one starts. The plan is **server-first**: everything players experience comes from the server (Oxide plugins, `Mods\*.cfg`, the Chronicle service). Nothing is ever shipped to a player's PC except the optional launcher, which only opens Steam.

---

## 0. Ground rules for every phase

**Legal and EULA**
- Every player uses **their own Steam copy** of Reign of Kings (app 344760). We never redistribute, repack or patch the game client, and we do not host game files for download. The dedicated server (app 381690) is installed by the operator through Steam or SteamCMD.
- Oxide (MIT, 2.0.3867) replaces `Assembly-CSharp.dll` **in the server's test copy only**. Clients and Easy Anti-Cheat are untouched.
- The launcher opens `steam://rungameid/344760` and shows the address. It contains no game binaries.
- The lore is original (`lore.md`). Do not use franchise names in houses, news or event titles.
- **UNVERIFIED:** nobody on this project has reviewed the text of the Reign of Kings EULA, Code Hatch's server-hosting terms, or Steam's terms on monetisation. **Before any money is involved** (donations, supporter perks, sponsored events), someone must read them. Until then there are **no paid perks of any kind**, and certainly nothing that gives an in-game advantage.

**Technical prerequisites (already in the repo)**
- The smoke test (`docs/smoke-test.md`) passes stages A–C on the owner's PC.
- The plugins compile against the shipped Oxide and game DLLs with `tools/plugin-compile-check/check.sh`, and they load in a real server without errors.
- `chronicle/` passes `npm test`.

**What is still unverified and blocks going public** (from `README.md` and `docs/server-reference.md`): which exe to run, the Oxide data path, and **whether `bindIP=127.0.0.1` still lets clients authenticate**. Opening the server to anyone outside the owner's PC also means changing `bindIP` / `isPrivate` and the router or firewall. The repo's scripts **deliberately do not** do this. It is a separate decision for the owner (see Phase 1).

---

## Phase 1: Closed test with friends

**Who:** 5–10 trusted friends.
**Length:** 2–3 sessions over 1–2 weeks.
**Goal:** prove that people *outside* the owner's PC can join, play the house and crown loop, and come back.

**Before you start**
- [ ] The owner decides how friends will reach the server (port forward or a hosted box) and makes that change by hand, with a backup (`server/Backup-Saves.ps1`). Write down exactly what was changed.
- [ ] Turn on the whitelist (`Whitelist.cfg`, `enabled = 'True'`) and/or set a `password`. *UNVERIFIED: the line format of whitelist entries (`docs/server-reference.md` §4.3).*
- [ ] Set `rConPassword` if RCON is enabled at all.
- [ ] Grant admin permissions to 1–2 admins only. Post the admin rules (`rules.md` §6).
- [ ] Set the rebellion windows (`oxide/config/CrownAndConsequences.json`, `RebellionWindows` / `UtcOffsetHours`) to a time when friends are online.
- [ ] Run the community claim sign-up for the six great houses (`lore.md`).

**What to try in the sessions:** every command in `how-to-play.md`, at least once:
- found, invite and join a house
- swear and renounce an oath
- sign and break a treaty
- crown capture on an empty throne
- each decree, including Royal Stores (check that items arrive)
- council appoint and remove
- `/claim declare`, then a real rebellion window
- capture, then `/ransom set`, `/ransom paid` and `/ransom release`
- `/ransom free` after expiry (a disposable character, because death may cost items)

Watch the Chronicle and the overlay throughout.

**Checkpoint 1 (go / no-go)**
| Check | Target |
|---|---|
| Friends who tried to join and got in | 100%, with every failure understood |
| Server crashes or hangs | 0 in the last session |
| Plugin errors in the console / `oxide/logs` | 0 unexplained |
| Captives stuck past their term | 0, or each one freed by `/ransom free` or an admin within 5 minutes |
| Throne gate | A non-claimant was blocked during an occupied, non-window time (confirms the UNVERIFIED cancel path) |
| Chronicle | Every event type above appeared, and the overlay never showed "The ravens are late" during play |
| Friends who came back for session 2 | at least 60% |

---

## Phase 2: Creator test night

**Who:** the Phase 1 group plus 2–4 small creators who agree to the rules and the streamer kit.
**Length:** one evening built around a **Crown Night** (`streamer-kit.md` §2).
**Goal:** check that the game is fun to *watch*, and that the server holds up with a stream audience trying to join.

**Before you start**
- [ ] Brief the creators: `streamer-kit.md`, `rules.md` §5 (sniping and ghosting), and the recommended stream delay.
- [ ] Decide on the overlay. It binds to `127.0.0.1`, so **only an OBS on the server PC** can use it unless the operator deliberately publishes the Chronicle service behind a reverse proxy. If remote creators need it, set that up and test it **before** this phase. Otherwise only the host streams the overlay.
- [ ] Get one or two claims declared at least a day ahead, so the story builds in the Chronicle.
- [ ] Have an admin on duty who is **not** playing competitively (`rules.md` §6).
- [ ] Back up just before the event.
- [ ] Post the news item (`news-seed.json`, "Crown Night") and the announcement (`streamer-kit.md` §5).

**Checkpoint 2 (go / no-go)**
| Check | Target |
|---|---|
| Join failures reported during the event | < 10% of attempts, with causes logged |
| Crashes during the rebellion window | 0 |
| `rebellion_started` / `rebellion_ended` fired on time | yes, within one plugin tick (15 s) of the window edge |
| Sniping or ghosting reports | each one handled under `rules.md`, with a written outcome |
| Creator feedback | at least half would stream it again |
| Viewers who joined afterwards | counted (see Measurement). No fixed target yet; this is the baseline |

---

## Phase 3: Open weekends

**Who:** the public, on Friday to Sunday weekends. The server is listed or advertised, and the whitelist is off.
**Length:** 3–4 weekends, each with a Crown Night on Saturday.
**Goal:** grow a returning population and find out whether permanent opening is worth it.

**Each weekend**
- [ ] News post on Wednesday (news feed, Discord). Creators get the announcement template.
- [ ] Backup before opening and after closing.
- [ ] An admin on duty during the Crown Night. The appeals channel is open (`rules.md` §8).
- [ ] Review after the weekend, using the numbers below.

**Checkpoint 3 (decide whether to stay open permanently)**
| Check | Target |
|---|---|
| Weekend-to-weekend return rate | at least 30% of unique players come back the following weekend |
| Crashes per weekend | at most 1, and it recovers by itself (restart loop) |
| Join failure rate | < 5% |
| Open moderation cases older than 7 days | 0 |
| Houses with 3 or more members active | at least 4 |

The targets are **starting proposals**, not benchmarks. Adjust them after Phase 1 with real numbers.

---

## Measurement: what to count and where it comes from

| Metric | Source | How |
|---|---|---|
| **Unique players / return players** | Server console log. A join prints `Authentication verified for <name> (<steamid>).` and a leave prints `<name> has disconnected.` (VERIFIED-PRIMARY, `docs/server-reference.md`) | Collect the Steam IDs per session or weekend. *Returning* means the ID was also seen in an earlier session. *UNVERIFIED: where the log file is written. Capture the console output if there is no file.* |
| **Join failures** | Players who said they tried to join, compared with authentication lines in the log | Keep a short "couldn't join?" form or channel. A failure is an attempt with no matching `Authentication verified` line. Record the cause (version, password, firewall, full server). |
| **Crashes** | Restarts of the server process, Unity crash output, Oxide `oxide/logs` | Count unplanned restarts per session. Log the time and the last console lines. |
| **Plugin health** | Oxide console: `PrintError` / `PrintWarning` lines such as "Captive tick failed", "Royal Stores tick failed", "Could not read oxide/data/…", and in-game `[Crown]` admin alerts | Each one gets a ticket. Zero should be unexplained. |
| **Chronicle freshness** | `GET /healthz`, `stale` in `/api/state`, "The ravens are late" on the overlay | Check before and during events. |
| **Engagement** | `oxide/data/RealmChronicle.json` (keeps the last 500 events by default) | Count events by `type` per weekend: houses founded, oaths sworn or broken, treaties, claims, coronations, ransoms. Copy the file after each weekend, because old events roll off. |
| **Concurrency** | `online` / `maxPlayers` in `/api/state` (refreshed every 30 s) | Note the peak during the Crown Night. |
| **Moderation load** | Admin log and appeals channel | Count reports, actions and appeals, and the time to resolve each. |

Data rules: keep Steam IDs only for counting returns, in a private sheet, and delete them after the test period. The public Chronicle already holds names only.

---

## Rollback

If a phase fails its checkpoint: close the server (whitelist on, or the old `bindIP` / `isPrivate` values back), restore from backup if the world is damaged (`server/Restore-Saves.ps1`), fix the problem, and **repeat the same phase**. Do not skip ahead. If Oxide itself is the problem, `server/Install-Oxide.ps1 -Rollback` returns to vanilla.
