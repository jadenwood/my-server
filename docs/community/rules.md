# Realm server rules

Realm is a game about **betrayal**. The rules protect the betrayal and keep out everything that only ruins the game for others.

The one test: **would this still be a problem if everyone involved were only a character?**
- If it is a problem only *inside* the story (you lied, broke an oath, stabbed an ally), it is **allowed**. The Chronicle will remember it, and that is the punishment.
- If it reaches the *real person* (their feelings, their stream, their PC, their time) or breaks the *game itself* (cheats, exploits), it is **not allowed**.

---

## 1. Allowed, and encouraged: in-game betrayal

All of these are legitimate play:

- **Breaking oaths** (`/renounce`) and **breaking treaties** (`/treaty break`). Your house gets the mark. That is the cost.
- **Attacking a treaty partner or your own liege.** Treaties and oaths are promises. The game does not enforce them and neither do admins.
- **Ignoring a proclamation.** The King's Peace and Open Roads are proclamations. Fighting during them is defiance, not a rule break.
- **Lying in character**: false promises, fake alliances, spies who join a house to report back to another one.
- **Declaring a claim** and fighting for the throne in the rebellion window, including against your former liege.
- **Capturing players and asking a ransom** within the Charter's limits (section 4).
- **Taking an empty throne** at any hour. The Charter allows it.
- **Reading the Chronicle**, the `/realm` page and the overlay. They are public on purpose and never show locations.

## 2. Not allowed: harassment

- Insults, slurs or threats aimed at the **real person**: their identity, appearance, nationality, religion, gender, sexuality or disability. In-character insults between rival houses are fine. Bigotry is never "in character".
- Following someone outside the game to keep a conflict going: DMs they did not want, other Discord servers, their stream chat, their social media.
- Doxxing, or sharing anyone's personal information. This leads to an immediate permanent ban.
- Sexual content aimed at other players.
- Spamming chat, mass-pinging, or flooding commands to clutter the Chronicle (the plugins already throttle some of this, so do not test it).
- Impersonating admins or other players, including names chosen to look like theirs.

## 3. Not allowed: griefing and cheating

**Griefing** is anything whose main purpose is to ruin someone's time, with no real in-game goal. Examples:
- Killing fresh-spawned players over and over when you gain nothing from it.
- Blocking the throne room, spawn points or main roads with structures or exploits so that others *cannot play*. Fortifying your own keep is fine.
- Using any trick to hold a captive past the Charter's term (see section 4).
- Founding a house under one of the six great house names against the community claim sign-up (see `lore.md`), or squatting names to block others.

**Cheating.** All of these are bannable:
- Any third-party cheat, injector, ESP or aim assist, or anything that tampers with Easy Anti-Cheat.
- Exploiting bugs: duplicating items, glitching through walls or into bases, falling under the map, or abusing a broken mechanic. If you find a bug, **report it** to admins in private. Reporting it is good and gets credit; using it is not allowed.
- **Alternate accounts** used to get around a game limit: a second house leader to dodge the 72-hour claim cooldown, a spare captive or captor to get around ransom immunity, or bypassing a ban.
- Trading in-game items, houses, titles or the crown for **real money** or anything outside the game.

## 4. Captivity and ransom limits

The plugin enforces these. Do not try to get around them.

- **10 minutes** is the longest hold from the first capture. Re-binding does not reset it.
- **500 gold** is the most you can ask. Once set, a ransom can only go down, and it can be changed at most 3 times.
- **15 minutes** of protection from being captured again after release.
- A captive whose term has ended can type `/ransom free`.

Rules on top of the plugin:
- Do **not** kill a captive to get around the clock, pass a captive to a friend, or hold someone in a way the plugin does not see (for example while the plugin is reloading). If the plugin did not record the capture, the 10-minute limit still applies.
- Ransom is **in-game gold only**. Never ask for real money, Steam items, follows, subs or anything else outside the game.
- Holding a captive is a game action. Abusing the captive in chat as a real person is harassment (section 2).
- Taking the gold and then refusing to type `/ransom paid` is dishonourable, but it is in-game betrayal and **allowed**: the law frees the captive when the term ends anyway. Tell the realm and let it judge.

## 5. Streams: sniping and ghosting

Creators are welcome. These rules protect both them and the players they meet.

- **Stream sniping is not allowed.** It means watching someone's live stream (or VOD, if it is still current) to find out where they are, what their base looks like, what they carry or what they plan, and then acting on it in game.
- **Ghosting is not allowed.** It means passing on information from someone who should not have it: a stream, a dead or spectating player, someone who is not in the game, or a person in voice chat who is not playing their character.
- **Spies are fine. Ghosts are not.** A player who joins a house and secretly reports to another one *in character* is playing the game. Someone who watches a stream and messages coordinates is ghosting.
- **The Chronicle is public.** Acting on what the Chronicle, the `/realm` page, the overlay or `/claim list` say is never sniping. They show names and public acts only.
- **Creators:** we recommend a stream delay of at least 2 minutes (see `streamer-kit.md`). A delay does not make sniping allowed.
- **Proving it:** the admins decide from evidence, such as timestamps lining up with a stream and movement no in-game knowledge could explain. Being *suspected* is not punished on its own.

## 6. Admin conduct

- Admins hold `realmhouses.admin` and/or `crownandconsequences.admin`. **That permission bypasses the throne-capture gate**, and it lets them disband houses, cancel claims, pardon marks and change the council.
- **An admin who holds the permission does not compete.** That means no contesting the throne, no council seat, no taking captives, no leading a house in a claim. An admin who wants to play competitively for a session first has the permission revoked (`oxide.revoke user <name> crownandconsequences.admin` and `oxide.revoke user <name> realmhouses.admin`). The revocation is logged in the admin log channel, and the permission is granted back afterwards. *UNVERIFIED: that `oxide.revoke` works this way in this Oxide build. The smoke test uses only `oxide.grant`.*
- **Every admin action that changes the realm is logged** in a public admin log with the reason: disband, pardon, claim cancel, council change, manual release, kick or ban. `/claim cancel` also appears in the Chronicle as "set aside by the realm's stewards".
- Admins do not take sides in the story. Never use admin tools to reveal locations, bases or plans.
- An admin never acts on a case involving their own house, friends or stream. They hand it to another admin.
- Admins investigate before they punish, wherever possible: ask both sides and check logs and the Chronicle.
- When a player is stuck past their ransom term, the plugin alerts admins in game. Admins should free that player first and investigate afterwards.

## 7. Enforcement

These are guidelines. Admins can skip steps for serious cases.

1. A warning (in private).
2. A kick.
3. A temporary ban (1–7 days).
4. A permanent ban.

Cheating, doxxing, real-money trading and ban evasion go straight to a permanent ban. Every ban is logged with its reason.

## 8. Appeals

- You can appeal **any** warning, kick or ban once, within **14 days**, in the appeals channel or on the appeals form linked in Discord.
- Include your in-game name, your Steam profile, what happened, and any clips or screenshots.
- The appeal is reviewed by **a different admin** from the one who acted. You get an answer within **7 days**.
- Possible outcomes: upheld, reduced, or overturned. If a ban is overturned, it is removed from the public log. If the appeal concerned a house's marks, an admin can use `/house pardon`.
- Arguing about a decision in public channels while the appeal is open does not help it.
- Arguments over *in-game* betrayal (an oath broken, a treaty torn up) are not appealable. That is the game.

---

*These rules apply on the Realm server and in its community spaces. They will change as the community grows. Changes are announced in the news feed before they take effect.*
