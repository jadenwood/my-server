# Moderation handbook

How Realm staff handle reports, gather evidence and decide on penalties. The player-facing rules are [`docs/community/rules.md`](../rules.md); this handbook is how staff apply them. Who may use which tool is in [`staff-roles-and-permissions.md`](staff-roles-and-permissions.md).

> Not legal advice. Community operating rules for a volunteer-run game server.

**The one test** (from `rules.md`): would this still be a problem if everyone involved were only a character? In-game betrayal is the game and is never punished. Harm to a real person, or breaking the game itself, is.

---

## 1. Where reports come from

| Source | What staff see | Tool |
|---|---|---|
| In game: `/warden report <player> <reason>` | Alert queue with reporter, target, reason; evidence line | `/warden alerts`, `/warden evidence <player>` |
| Automatic Warden flags: combat log, chat flood mutes, offensive names, raid-hour hits | Alert queue, evidence log, `oxide/logs/RealmWarden/*.txt` | `/warden alerts`, `/warden player <player>` |
| Reported ravens (`/raven report`) | Letter text and true sender, kept 7 days at most | `/raven admin reports`, `/raven admin letter <#>` |
| Stuck captive past the ransom term | `[Crown]` admin alert in chat | Free them first (`rules.md` §6), then investigate |
| Discord: the reports channel or a DM to staff | Message, screenshots, clips | Copy into a case (section 3) |
| Treasury audit mismatch, contract or prize dispute | Server log line; `/treasury audit` | Treat as a possible exploit: [`incident-response.md`](incident-response.md) |

Players are told: **report, don't retaliate**. A report made in good faith never counts against the reporter, even if no rule was broken.

## 2. The escalation ladder

Penalties grow with each repeat. Staff may skip steps for serious cases (the "Starts at" column). Every step is logged.

| Level | Penalty | Who may give it | Typical use |
|---|---|---|---|
| 0 | **Note**: no penalty, recorded in the case log | Any staff | A first, borderline or accidental issue; a player who self-reported |
| 1 | **Private warning** in game or Discord DM, naming the rule | Any staff | First minor breach: mild spam, a heated insult, an inappropriate house name |
| 2 | **Mute**: `/warden mute <player> <minutes>` (1-1440), or the Court's game mute; Discord timeout | Any staff | Chat abuse after a warning, flooding |
| 3 | **Kick** (Court) | Moderator asks, Admin does | Ignoring a mute or warning in the moment; to stop harm now |
| 4 | **Temporary ban, 1-7 days** (Court: `/ban "<name>" <days> <reason>`) | Admin | Repeat harassment, griefing, ghosting or sniping with good evidence, combat logging as a pattern |
| 5 | **Temporary ban, 8-30 days** | Admin, with a second admin's approval | A second level-4 offence within 90 days |
| 6 | **Permanent ban** (`days` 0) | Admin, with a second admin's approval (two-person rule) | See "Starts at 6" below |

**Starts at 6 (permanent, straight away):** cheats or injectors or tampering with Easy Anti-Cheat; deliberate item duplication or other exploit abuse for gain; doxxing; real-money trading; ban evasion with an alternate account; credible threats of real-world harm; sexual content involving minors (also report it to the platform and, where the law requires, to the authorities: the Owner decides, and that is outside this handbook).

**Starts at 4:** stream sniping or ghosting with clear evidence; slurs or bigotry aimed at a real person; using an alternate account to get round a game limit (claim cooldown, ransom immunity).

**Reset:** a player with no penalty for 90 days returns to the bottom of the ladder for minor offences (levels 1-3). Bans at 4 or above stay on record.

**Game-state fixes are separate from penalties.** Returning duplicated items, `/contract admin refund`, `/house pardon`, freeing a captive or cancelling a market order fixes the realm. It is done whether or not anyone is punished, and it is logged the same way.

**Staff misconduct** (using a permission for your own side, reading letters without a case, sharing evidence outside staff) is handled by the Owner: removal from the staff group the same day, then the same ladder as any player.

## 3. Evidence standards

A penalty at level 2 or above needs evidence that would convince a staff member who was not there.

| Standard | Needed for | Meaning |
|---|---|---|
| **Logged** | Levels 0-1 | Staff saw it, or one credible report. Write down what was seen |
| **Clear** | Levels 2-4 | At least one record staff did not create from memory: a Warden evidence line or alert, a game log line with a timestamp, a Court log entry, a screenshot or clip with the in-game clock or a timestamp visible, an audit entry |
| **Strong** | Levels 5-6 and every sniping, ghosting, cheating or exploit case | Two independent records that agree (for example a clip **and** the Warden evidence line, or a clip **and** the server log), and the accused was asked for their side first unless that would let them destroy evidence or keep exploiting |

What counts and what does not:

- **Good:** `oxide/logs/RealmWarden/realmwarden_evidence-<date>.txt` lines; the server's own `Logs\Log[...].txt` (joins: `Authentication verified for <name> (<steamid>).`); `%APPDATA%\Realm\court\court-log.jsonl`; `/raven admin audit`; the Chronicle; uncut clips with game sound; Discord message links (not just screenshots, which are easy to fake).
- **Weak on its own:** a single cropped screenshot; "everyone knows"; a report from someone in an active feud with the accused; a stream VOD that does not show the moment.
- **Never:** information got by breaking the rules (hacking someone's account, a leaked DM from a third party who was not in the conversation), or anything gathered by following a player off-platform.
- **Stream sniping and ghosting** (`rules.md` §5): match timestamps from the stream (with its declared delay) to in-game moves that no in-game knowledge explains. Being suspected is never punished on its own.
- **Identify by SteamID64**, not by name. Names change and can imitate others. `/warden player <player>` and the Court's player list (from `/realm.players`) show the ID.

### Keeping evidence

- Open one **case** per incident in the private staff channel (or a staff-only sheet): case number, date (UTC), reporter, accused (name and SteamID64), rule, evidence links, the decision, who decided, who approved.
- Copy the relevant lines out of logs into the case. `RealmRavens` deletes its audit after 7 days at most, and the Warden's in-file list is capped, so do it on the day.
- Keep cases for **12 months** after the last penalty, then delete them, except permanent bans: keep the SteamID64, date, rule and a one-line summary for as long as the ban stands. This matches the privacy notice ([`../../legal/privacy-notice.md`](../../legal/privacy-notice.md)).
- Evidence stays inside staff. Never post it publicly, never name and shame (`ops/disaster-recovery.md` F says the same for attacks). The accused may see the evidence about themselves during an appeal, with other players' private information removed.

## 4. Handling a report, step by step

1. **Stop ongoing harm first.** Free a stuck captive, mute a flood, kick someone who is actively griefing. Note what you did.
2. **Check for conflicts.** If it involves your house, friends or stream, hand it to someone else now.
3. **Open the case** and **save the evidence** (section 3).
4. **Hear both sides** where possible, in private. Ask open questions. Do not reveal who reported.
5. **Decide** with the ladder. Write the reason in one or two plain sentences that name the rule.
6. **Get approval** if it is level 5-6 or a two-person action.
7. **Act and tell the player** in private: the rule, the penalty, its length, and how to appeal ([`ban-appeals.md`](ban-appeals.md)). Ban reasons in the Court should be short and factual, for example `harassment rules 2 case 41`, because the reason is shown to the player and stored in the game's ban list.
8. **Close the loop** with the reporter: "we acted on it" or "we found no breach", never the details of the penalty.
9. **Fix the realm** if needed (section 2, game-state fixes).

## 5. What staff never do

- Reveal locations, base layouts, inventories or plans learned through admin tools (the Chronicle never shows these; staff must not either).
- Use a permission for their own house's benefit, or join a fight with a permission's bypass active.
- Ban for in-game betrayal: broken oaths, torn treaties, lies in character, spies. That is the game.
- Argue a case in public channels. Point people to the appeal process.
- Copy evidence outside the staff space, or keep it past its retention.
