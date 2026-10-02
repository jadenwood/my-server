# Ban appeal process

How a player asks for a warning, mute, kick or ban to be reviewed, and how staff handle it. The short version for players is `docs/community/rules.md` §8; this page is the full process and does not change any of its promises (one appeal, within 14 days, reviewed by a different admin, answered within 7 days).

> Not legal advice.

---

## 1. For players

**What you can appeal:** any warning, mute, kick or ban from Realm staff, or an automatic Warden mute or name kick.

**What you cannot appeal:** in-game betrayal (an oath broken, a treaty torn up, a lost crown, a ransom), and Chronicle entries about public acts. That is the game.

**How:**
1. Within **14 days** of the action, post in the appeals channel on Discord or use the appeals form linked there. If you are banned from Discord too, email the appeals address in the Discord server's public info panel (placeholder: `appeals@<your-domain>`; the Owner fills this in).
2. Use this template:

```
In-game name:
SteamID64 (the 17-digit number in your Steam profile link; staff never ask for your password or login):
Discord name (if any):
What action was taken, and when (UTC if you know it):
Case number (if you were given one):
What happened, in your words:
Why it should be changed (wrong person, wrong facts, too harsh, something new):
Evidence (clip links, screenshots with timestamps):
```

3. Be honest. Appeals that admit what happened and explain it are often reduced. Appeals that turn out to contain fabricated evidence are closed and that is recorded.
4. One appeal per action. Arguing about it in public channels does not help.

**What happens next:** you get a reply within **7 days**. The outcome is one of: **upheld** (no change), **reduced** (shorter or lower), or **overturned** (removed, and taken off the public log if it was there). If a house's marks were involved, an admin can use `/house pardon`.

## 2. For staff

### Rules of review

- The reviewer is **an admin who did not take the action** and has no conflict (own house, friend, rival, or on stream with the player). If every admin is conflicted, the Owner reviews.
- Permanent bans are reviewed by **two** admins, neither of whom took the action.
- The reviewer reads the whole case: the original evidence, the decision note, and the new appeal. They may ask the player and the original staff member questions.
- The player may see the evidence about themselves, with other players' private details removed (names of reporters, letters they were not part of, Steam IDs other than their own).
- The original staff member is not punished because an appeal succeeds. Appeals exist because people make mistakes.

### Tests the reviewer applies

1. **Right person?** Was the account identified by SteamID64, and could it have been someone else (shared PC, name imitation)?
2. **Right facts?** Does the evidence meet the standard in the moderation handbook §3 for that level?
3. **Right rule?** Is it really a breach of `rules.md`, or was it in-game betrayal?
4. **Right size?** Does the penalty fit the ladder, given the player's history?
5. **Anything new?** New evidence, or a change in behaviour (for a long ban appealed late, see below).

### Doing the outcome

| Outcome | Action |
|---|---|
| Upheld | Reply with the reason, in one or two sentences. Close the case |
| Reduced | Court: `/unban <name, ban number or SteamID64>`, then a new `/ban "<name>" <days> <reason incl. case number>` with the shorter time; or shorten a Warden mute with `/warden unmute` and a new `/warden mute`. Log both |
| Overturned | `/unban ...` or `/warden unmute` / `/warden clear <player>`. Remove the entry from any public log. Restore game state where the penalty took something (for example `/house pardon`). Note it in the case |

Unban syntax is from the game's `CoreCommandHandler.Unban` as documented in `docs/admin-console.md` [DEC]. **UNVERIFIED at run time** on the real server, like the rest of the Court.

### Late appeals and second chances

- After 14 days, an appeal is only accepted for a **permanent ban**, and only once every **6 months**. It is a "second chance" review: the question is whether the player can return without repeating the harm, not whether the original ban was right.
- Second chances are never given for: cheating tools, deliberate duplication for profit, doxxing, threats of real-world harm, or abuse involving minors.

### Records

Each appeal is added to the original case: date received, reviewer(s), outcome, one-line reason, date answered. Keep it with the case and delete it on the same schedule (moderation handbook §3).

### Targets

| Measure | Target |
|---|---|
| First reply (acknowledged, reviewer named) | 48 hours |
| Decision | 7 days |
| Appeals open longer than 7 days | 0 (also a launch-plan checkpoint) |
