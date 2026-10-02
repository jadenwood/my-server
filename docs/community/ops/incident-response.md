# Incident response: exploits, duplication and leaks

What staff do when the game or the realm's data is being broken on purpose, or when something private gets out. Server crashes, a dead disk, DDoS and leaked **secrets** (webhook URLs, bucket keys, passwords) are covered by the ops runbook [`ops/disaster-recovery.md`](../../../ops/disaster-recovery.md); this page links to it instead of repeating it.

> Not legal advice. Where personal data of players is exposed, data-protection law may require specific steps and deadlines. The Owner must check what applies to them (section 6).

---

## 1. Severity levels

| Level | Examples | Who leads | First response |
|---|---|---|---|
| **1 Critical** | Item duplication or currency creation being used right now; a working way to take the crown, bypass captivity limits or break escrow; player personal data (Steam IDs with names, IPs, private letters, moderation evidence) posted publicly; a staff account or the server PC compromised | Owner (or deputy) | Within 1 hour, any time |
| **2 High** | A reproducible exploit found but not known to be abused; a treasury audit mismatch; a staff member abusing a permission; a leaked secret (`disaster-recovery.md` G) | Admin on duty, Owner told | Within 4 hours |
| **3 Normal** | A bug that gives a small edge; a single player's data shared with the wrong person by mistake; a cheat report without evidence yet | Any admin | Within 24 hours |

When in doubt, pick the higher level.

## 2. Exploits and duplication

### 2.1 Contain (first hour)

Pick the smallest step that stops the damage:

1. **Stop the economy if it is the economy:** `/treasury freeze` (market orders; cancels and expiry still work). For contracts, set `"ItemEscrow": false` in `oxide/config/RealmContracts.json` and `oxide.reload RealmContracts` (honour mode; open escrow records are kept).
2. **Unload the plugin involved** if the exploit lives in one: `oxide.unload <Plugin>` (an Oxide core command [SRC Oxide.ReignOfKings 2.0.3867 `ReignOfKingsCore.cs:91`]). The plugin stops; its data file stays on disk. Do **not** delete data files: some refuse to load a damaged file precisely to protect escrow (`docs/smoke-test.md` C14).
3. **Back up now, before anything else changes**, so the evidence is kept: Steward's backup, or `ops/scripts/Backup-RealmOffsite.ps1 -LocalOnly -Label incident` (`ops/disaster-recovery.md`).
4. **Remove the people abusing it:** Court kick, and a temporary ban until the investigation ends (this is a containment step, not the final penalty; tell them so).
5. **If it cannot be contained:** close the server to new joins (whitelist on, Court) or stop it with a save. Post a short notice: "The realm is closed for repairs. No details yet." Never describe the exploit publicly.

### 2.2 Investigate

- Who, when and how much: `/treasury audit` and the treasury ledger (`/treasury ledger`), the Chronicle, Warden evidence, the server log, `/contract` history, RealmEvents prize records, RealmStats day files (sessions only, no names).
- Reproduce it **only on a test copy** (`server/New-TestServer.ps1`, never the live world).
- Write the method in the private incident note only. Share it with the plugin's maintainers privately, not in a public issue.

### 2.3 Fix and recover

| Option | When |
|---|---|
| **Targeted fix**: take back duplicated items or marks, refund victims (`/contract admin refund`, `/treasury cancel`), restore game state with admin commands | The amount and the people are known |
| **Rollback** to the last good backup (`ops/scripts/Restore-RealmBackup.ps1`, `server/Restore-Saves.ps1`) | Duplicated items spread too widely to trace. Announce it: everyone loses the progress since that backup, so do it only with the two-person rule and an Owner decision |
| **Patch** the plugin or config, compile-check (`tools/plugin-compile-check/check.sh`, 0 errors), test on the test copy, deploy | Always, before reopening |

### 2.4 Penalties and credit

- Deliberate abuse for gain: level 6, permanent (`moderation-handbook.md`).
- Used it once, then reported it straight away: no penalty; the gain is removed.
- **Reported it without using it: credit.** Thank them publicly by name (with their consent) once the fix is live. `rules.md` §3 promises this.

## 3. Cheating (third-party tools, EAC tampering)

- Realm cannot see inside a player's PC and never asks players to install anything to check. Evidence is clips, impossible movement or damage in the server log, and Warden flags.
- Ban (level 6) on strong evidence. Easy Anti-Cheat bans are run by the anti-cheat provider, not by Realm; Realm does not report players to it and cannot lift its bans.

## 4. Leaks

### 4.1 Player personal data or private messages

Examples: a Warden evidence log, the Court log, `RealmHouses.json` with Steam IDs, a RealmStats salt, raven letters, or the staff case sheet posted or sent outside staff.

1. **Contain:** ask the place it was posted to remove it (Discord message delete, a hosting takedown). Revoke access of whoever leaked it if staff (`staff-roles-and-permissions.md` §5).
2. **Assess:** what data, how many players, how sensitive (IP addresses and private messages are more serious than in-game names), how long it was out.
3. **If the RealmStats salt leaked:** the pseudonymous keys can now be matched to Steam IDs. Change `Salt` in `oxide/config/RealmStats.json` (retention history restarts, `plugins/docs/RealmStats.md`), and treat the kept day files as identifiable until they age out.
4. **Tell affected players** directly where you can, in plain words: what got out, what you did, what they can do. Do this within 72 hours of knowing.
5. **Legal duties:** see section 6.

### 4.2 Staff-only material (evidence, cases, discussions)

Same as 4.1, plus: if a moderation case leaked, tell the players named in it. The leaker is handled as staff misconduct.

### 4.3 Secrets (tokens, keys, passwords)

Follow `ops/disaster-recovery.md` G: revoke at the source first, then replace. For the **server-list signing key**: there is no revoke switch in shipped player apps, which trust only the key they were built with (`docs/going-public.md` §3). If it leaks, build and ship a new player app with a new key, publish a list with a higher version from the new key, and tell players to update and ignore any list the old app shows that points somewhere unexpected. **UNVERIFIED:** this recovery path has not been rehearsed.

### 4.4 Game or unreleased material

Realm never holds or shares game files. If someone posts game files, decompiled game code or the rights holder's assets in Realm spaces, delete them and remind them that Realm does not redistribute game content (`docs/legal/eula-compliance-checklist.md`).

## 5. Communication

| When | Where | What |
|---|---|---|
| During containment | Discord announcements, `/notice` in game | "The realm is closed for repairs" or "The market is frozen while we investigate". No method, no names |
| After the fix | News feed and Discord | What was affected in general terms, what was done (rollback or targeted), what players need to do, credit for the reporter if they agree |
| Never | Anywhere public | The exploit method; the names of accused players; evidence |

## 6. Personal data breaches and the law

If a leak involves personal data (for example SteamID64s with names, IP addresses in logs, private letters, moderation evidence), data-protection laws in some places (for example the GDPR in the EU and UK) can require notifying a regulator and the people affected within a set time, often 72 hours. Whether this applies depends on where the Owner and players are. **UNVERIFIED and not legal advice:** the Owner should check the rules where they live before going public, and keep the incident note as a record either way.

## 7. After every level 1 or 2 incident

Write a short note in the staff channel (same format as `ops/disaster-recovery.md` "After the incident"):
- what happened, when it was detected and by whom;
- what was done, and how long each step took;
- the lasting fix (a plugin patch, a config change, a new check in the smoke test or a plugin's logic tests);
- what players were told.

Review the note at the next staff meeting. If a rule or this page was wrong, fix it.
