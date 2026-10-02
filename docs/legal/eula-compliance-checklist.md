# EULA compliance checklist

A checklist for keeping Realm inside the terms that govern Reign of Kings, its dedicated server, Steam and the tools Realm builds on. Work through it before each launch phase (`docs/community/launch-plan.md`) and before anything involving money ([`monetisation-guardrails.md`](monetisation-guardrails.md)).

> **Not legal advice.** This is a volunteer project's own checklist. It is not a legal opinion, and nobody who wrote it is a lawyer. If real money, a business, or a dispute with the rights holder is involved, get advice from a qualified lawyer where you live.

---

## 0. Status: the EULA text has not been read

Be clear about what is and is not known:

- The repo **refers to** the Reign of Kings EULA in two places, but **quotes none of it**:
  - `README.md` "Legal boundary": *"Clients are never touched. This respects the EULA and Easy Anti-Cheat."* This is a **claim, not a verified fact**: no one has checked it against the EULA text.
  - `docs/community/launch-plan.md` §0: *"UNVERIFIED: nobody on this project has reviewed the text of the Reign of Kings EULA, Code Hatch's server-hosting terms, or Steam's terms on monetisation."*
- While writing this checklist (2026-10-02) the published EULA could not be fetched: the build environment blocks `store.steampowered.com`. So **every clause reference below is blank and every EULA item is UNVERIFIED**.
- The game's store page lists the developer as **Code Hatch** (as `README.md` says). Who holds the rights **today** (the developer, a publisher, a buyer, or nobody active) is **UNVERIFIED**. See the outreach letter's "Before you send" section.

**First task for the Owner:** open the Reign of Kings store page on Steam, follow the "End User License Agreement" link (Steam shows third-party EULAs on the store page and before install), and save a dated copy (PDF or text) in a **private** folder outside this repo. Do the same for the dedicated server (app 381690) if it shows its own, and for the Steam Subscriber Agreement. Then fill in the "Clause" column below. Do not commit the EULA text to the repo: it is the rights holder's copyrighted text.

## 1. What Realm does (verified from this repo)

These are facts about Realm's design, each checkable in the repo. They are what the EULA will be compared against.

| # | Fact | Where to check |
|---|---|---|
| F1 | Players use **their own Steam copy** of Reign of Kings (app 344760). Realm never distributes the game client | `README.md` Legal boundary; `launcher/` only opens allow-listed `steam://` URLs |
| F2 | The repo contains **no game binaries or assets**, and the apps contain none | `README.md`; `launcher` test proves the player app has no server-control code |
| F3 | The dedicated server (app 381690) is installed by the Owner **through Steam or SteamCMD**, then **copied** to a test folder | `server/New-TestServer.ps1`, `ops/scripts/Install-RealmServer.ps1` |
| F4 | **Oxide replaces `Assembly-CSharp.dll` in the server copy** (a modification of a server game file on the Owner's machine). The client is never modified | `server/Install-Oxide.ps1`, `README.md` |
| F5 | Plugins run inside the server and use the game's own APIs; the project has **decompiled the shipped DLLs** to document those APIs (`docs/oxide-rok-api.md`, tags [DEC] and [IL]) | `docs/oxide-rok-api.md` |
| F6 | The game's own `Mods\*.cfg` override files are used for atmosphere presets | `mods/` |
| F7 | Realm adds **original** lore, names and art; no franchise names | `docs/community/lore.md`, `docs/brand.md`, `art/README.md` |
| F8 | A design idea has the player app write the client's `Configuration\Lobby Mirrors.cfg` (opt-in). It is **not built**: no code under `launcher/` mentions `Lobby Mirrors`, `CustomMirrors` or `UseDefaultMirrors` (grep, 2026-10-02). If it is ever built, the client is no longer "never touched" | `docs/seamless-design.md` §"Player side (opt-in)" |
| F9 | Realm has **no paid features** and takes no money today | `docs/community/launch-plan.md` §0 |
| F10 | Realm streams and records gameplay (overlay, creator nights, trailer) | `docs/community/streamer-kit.md`, `trailer-script.md` |

## 2. Reign of Kings EULA: questions to answer

Fill in each row from the saved EULA copy. "Clause" is the section number and a short quote. "Result" is one of **OK**, **Needs permission**, **Not allowed**, **Unclear**.

| # | Question | Realm facts it touches | Clause | Result |
|---|---|---|---|---|
| E1 | Does the licence allow **running a dedicated server** for other people? Is there a separate server-hosting licence or policy? | F3 | UNVERIFIED | |
| E2 | Does it limit **who may host** (individuals, licensed hosts only) or the **number of servers**? | F3, `docs/going-public.md` (up to four servers) | UNVERIFIED | |
| E3 | Does it allow **modifying server files** (Oxide replacing `Assembly-CSharp.dll`)? Is it silent, permissive for "mods", or forbidding "modify, adapt, translate"? | F4 | UNVERIFIED | |
| E4 | Does it forbid **decompiling, disassembling or reverse engineering**? If so, is there an interoperability exception where you live (for example in EU law)? Note: Realm's API docs come from reading the shipped DLLs | F5 | UNVERIFIED | |
| E5 | Does it say anything about **mods, user-generated content or `Mods\*.cfg`** files, and who owns them? | F6, F7 | UNVERIFIED | |
| E6 | Is use limited to **personal, non-commercial** use? What does it count as commercial (donations, ads, sponsorships, paid hosting)? | F9, `monetisation-guardrails.md` | UNVERIFIED | |
| E7 | Does it restrict **selling or trading virtual items** for real money? | `rules.md` §3 already bans real-money trading | UNVERIFIED | |
| E8 | Does it cover **videos, streams and screenshots** (a video policy, or silence)? Can creators monetise them? | F10 | UNVERIFIED | |
| E9 | Does it restrict use of the **game's name, logo or trademarks** by third parties? Can Realm say "a community server for Reign of Kings"? | F7, branding | UNVERIFIED | |
| E10 | Does it require players or hosts to keep **anti-cheat** (Easy Anti-Cheat) intact, or forbid circumventing it? | F1, F4 (server only) | UNVERIFIED | |
| E11 | **Termination:** what happens if terms are broken, and can the rights holder revoke permission for community servers? | all | UNVERIFIED | |
| E12 | **Governing law and changes:** which country's law applies, and can the terms change without notice? Record the EULA version or date you saved | all | UNVERIFIED | |

## 3. Other terms that apply

| # | Terms | What to check | Status |
|---|---|---|---|
| S1 | **Steam Subscriber Agreement** (Valve) | Rules on running game servers, commercial use of Steam content, and selling items or accounts. Steam IDs are shown and stored by Realm (see the privacy notice) | UNVERIFIED: not read for this project |
| S2 | **Steamworks / Steam Web API terms** | Only relevant if Realm ever calls the Steam Web API. Today it does not: it uses A2S queries and `steam://` links only | Not applicable today |
| S3 | **Easy Anti-Cheat** terms for players | Realm never touches the client or EAC (F1). Check that nothing Realm asks players to do (launch options `-ip`/`-port`, `docs/going-public.md` T1) conflicts | UNVERIFIED |
| S4 | **Oxide** licence | MIT licence, `github.com/OxideMod/Oxide.ReignOfKings` (`docs/server-reference.md`, VERIFIED-PRIMARY). MIT allows use and modification; keep its notice if Realm ever redistributes Oxide (it does not: the Owner downloads it) | OK |
| S5 | **Fonts** | Cinzel and EB Garamond under the SIL Open Font License 1.1 (`docs/brand.md`). Bundle the OFL text with any font files | OK if the OFL text ships with them |
| S6 | **Discord** Developer Terms and Policy | The bot (`bot/`) and herald use Discord's API. Check data-use and privacy-policy requirements for bots | UNVERIFIED |
| S7 | **Hosting provider** acceptable-use policy | Game servers, DDoS, and Windows licensing (`ops/hosting-guide.md`) | Per provider |
| S8 | **Realm's own repo licence** | There is **no licence file** yet (`art/README.md`, `docs/brand.md`). Until one is chosen, others have no clear right to reuse Realm's code or art | Open: maintainers' decision |

## 4. Checklist before each phase

Tick each line. Anything unticked blocks the phase.

**Before Phase 1 (closed test with friends)**
- [ ] EULA and Steam Subscriber Agreement saved privately with the date (section 0).
- [ ] Rows E1, E3, E4, E10 filled in. If any is **Not allowed**, stop and write to the rights holder first ([`rights-holder-outreach-letter.md`](rights-holder-outreach-letter.md)).
- [ ] No game files in the repo, the apps, Discord or any download page (F1, F2). Search the repo for `.dll`, `.assets`, `.resS` and `Data/` folders before any release.
- [ ] F8 still true: the player app writes no client file. Re-run the grep in F8. If that changed, confirm it is allowed (E3, E5) or remove it.
- [ ] Privacy notice published ([`privacy-notice.md`](privacy-notice.md)).

**Before Phase 3 (open weekends, public listing)**
- [ ] All of E1-E12 filled in.
- [ ] Every place Realm describes itself says it is unofficial and not affiliated with the game's developer or publisher (portal, Discord, player app About, store-like pages, the trailer end card).
- [ ] No use of the game's logo as Realm's logo (E9).

**Before any money is involved**
- [ ] [`monetisation-guardrails.md`](monetisation-guardrails.md) followed, including written permission where it says so.

## 5. Keeping this current

- Re-read the EULA every 6 months and whenever Steam shows a changed agreement. Note the date in the private copy's file name.
- If the Owner gets written permission, record **what** was allowed, **by whom** (name, role, company), **when**, and keep the message privately. Update the "Result" column and `monetisation-guardrails.md`.
