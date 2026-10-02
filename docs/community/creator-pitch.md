# Creator pitch: outreach message and event pitch

For inviting streamers and video creators to the Realm. The technical details for creators (overlay, event formats, etiquette) are in [streamer-kit.md](streamer-kit.md). This page is what you **send** them.

Before you send anything:
- Pick creators who already play survival, medieval or roleplay-adjacent games and who have **small to mid-sized** audiences. They are the ones the launch plan asks for in Phase 2 ([launch-plan.md](launch-plan.md#phase-2-creator-test-night)).
- Fill in every `[bracket]`. Do not send a message with an empty link or date.
- Promise only what exists. Every mechanic below is in the plugins ([how-to-play.md](how-to-play.md)). If you change a default (a window time, a ransom cap), change it here too.
- One message per creator, written to them. Mention a video or stream of theirs you actually watched.

---

## 1. First message (DM or email, about 150 words)

> **Subject:** A throne war you can stream: Crown Night on [date]
>
> Hi [name],
>
> I run **[Realm]**, a community server for Reign of Kings built around one rule: *the crown belongs to whoever holds the seat.* Six great houses, sworn oaths you can break (everyone sees it), declared claims, and a scheduled rebellion window where the throne is fought for.
>
> I liked [specific video/stream] and I think your chat would love **Crown Night** on **[Saturday, date] at [19:00 UTC]**: 90 minutes, only houses that declared a claim can take the throne, and every coronation and betrayal pops up as a proclamation on a stream overlay.
>
> You'd need your own Steam copy of Reign of Kings. Our launcher joins in one click and never touches game files. I can give your community its own house, a briefing call, and an admin on duty all night.
>
> Want the one-page brief? No pressure either way.
>
> [your name], [Discord handle], [link]

**Follow-up (once, after 5–7 days, only if no reply):**
> Hi [name], just bumping this once: Crown Night is on [date]. If the timing doesn't suit you, I'm happy to invite you to a later one. Thanks either way!

---

## 2. The one-page event brief (send after a "yes" or "tell me more")

**Crown Night on [Realm]: [date], [19:00–20:30 UTC]**

**The pitch in one line:** a 90-minute siege for a throne that anyone can hold, with every oath, betrayal and coronation proclaimed live on your overlay.

**What your viewers see**
- A live **Chronicle overlay** on top of your game: crown plate (who reigns and for how long), house banners, a ticker, and animated proclamation cards for coronations, claims, rebellions, oaths, betrayals, treaties, decrees and ransoms.
- A story that builds all week: claims have to be **declared at least 60 minutes before** the window, so the rivalry shows up in the Chronicle days ahead.
- A clean ending: when the window closes the overlay proclaims the result: claimant won, crown held, throne empty, or a third house took it.

**What you get**
- **Your own house** if you want one: one of the six great houses of Ostreval (Varrow, Ashgrove, Corvane, Dunmere, Halloran, Merrin) or a new one, claimed for you before launch. House words, colours and sigils are in [lore.md](lore.md), free to use in thumbnails.
- **A 20-minute briefing call** before the night: rules, overlay setup, and the story so far.
- **An admin on duty** for the whole window who is not playing competitively ([rules.md §6](rules.md#6-admin-conduct)).
- **Shout-outs** in the realm's news feed and Discord, plus your clips featured in the weekly Herald's Recap if you agree.

**Run of show (suggested)**
| Time (UTC) | Segment |
|---|---|
| 18:00 | Go live. Recap the week with `/chronicle` and the claims with `/claim list`. Last call for claims (they close at 18:00 for a 19:00 window). |
| 18:30 | Oath ceremonies on stream (`/swear`), last treaties (`/treaty propose`). |
| 19:00 | **The Lawful Hours open.** The overlay proclaims the rebellion. |
| 20:30 | Window closes, result proclaimed. The new monarch's first decree and council make a strong closing segment. |

**Other formats we can run with you:** Royal Tournament (Friday, PvP ranking), The King's Hunt (Wednesday, the monarch names up to 3 quarry), Oath Ceremony / The Breaking, The Summit (treaty night), The Ransom Hour (10-minute legal clock, 500 gold cap). Details: [streamer-kit.md §2](streamer-kit.md#2-event-formats) and [how-to-play.md §12](how-to-play.md#12-realm-events).

**What we ask**
- Read the rules, especially **§5 Streams: sniping and ghosting** ([rules.md](rules.md)). We recommend a **stream delay of at least 2 minutes**.
- Show name tags, not maps: no long shots of other houses' bases or routes.
- Keep betrayal in character. Never point your chat at another player.
- Link the realm in your panel so viewers can join.

**Practical**
- Players need **their own Steam copy of Reign of Kings**. Realm never modifies or redistributes game files.
- Join with the Realm app (one click, through Steam), or Direct Connect: address **[address]** in the address box, port **[port]** in the port box. Do not type them together as "address:port".
- **Overlay caveat:** the overlay is served on the server PC (`127.0.0.1:8787`). Remote creators can use it only if we publish it for you ahead of time: **[yes, URL will be sent by (date) / no, overlay is on the host's stream only]**. Do not promise it until it has been tested ([launch-plan.md Phase 2](launch-plan.md#phase-2-creator-test-night)).

---

## 3. Short versions

**Discord / X post (under 280 characters):**
> Crown Night on [Realm], [Sat date] [19:00 UTC]: 90 minutes, one throne, only declared claimants may take it. Every oath and betrayal is proclaimed live. Bring your own Reign of Kings (Steam). Creators wanted: [link]

**Creator call in the realm's own news feed** (fits `launcher/news.json`):
> **Wanted: heralds for Crown Night.** Streaming Reign of Kings? Take a house, get the overlay, and tell the story of the siege live. Ask [contact] by [date].

---

## 4. After the event

- Thank each creator within 24 hours, with a link to their best Chronicle moment.
- Ask the launch-plan questions: would they stream it again, what broke, what was confusing ([launch-plan.md Checkpoint 2](launch-plan.md#phase-2-creator-test-night)).
- Credit their clips in the Herald's Recap only with permission.
- If anything went wrong (a stuck captive, a throne taken outside the rules), say so plainly and say what changed.
