# Realm trailer: 60-second shot list

A one-minute trailer the server owner can cut from **their own in-game footage** plus screen recordings of the Realm apps and the Chronicle overlay. No stock footage, no music or art you do not have the rights to, and no names from other franchises (see [lore.md](lore.md), "Writing guidelines").

**Working title:** *The Crown Belongs to the Seat*
**Format:** 1920x1080, 30 or 60 fps, 60 s (a 30 s cut is at the end).
**Tone:** grim but readable. Betrayal, debt and pride. No gore for its own sake.

---

## Before you film

| What | How |
|---|---|
| A busy server | Film during a **Crown Night** (Saturday 19:00 UTC by default, see [how-to-play.md §12](how-to-play.md#12-realm-events)) or stage one with your Phase 1 group ([launch-plan.md](launch-plan.md)). Ask everyone who will be on screen first ([streamer-kit.md §3](streamer-kit.md#3-etiquette)). |
| Game capture | OBS **Game Capture** of Reign of Kings at 1920x1080. Hide your own HUD and chat if the game allows it. *UNVERIFIED: whether the game has a free camera or HUD toggle for cinematic shots; if not, film from a second player character who only walks and watches.* |
| The overlay | OBS **Browser** source `http://127.0.0.1:8787/overlay?hold=18` above the game capture, on the server PC ([streamer-kit.md §1](streamer-kit.md#1-the-overlay)). For clean takes of single proclamations, use `?replay=1&bg=1` in a normal browser and record that window. |
| The apps | Record the **Realm** player app (first run, Swear your allegiance, Join) and **Realm Steward** Home with OBS **Window Capture**. Reference stills: `docs/img/player-firstrun.png`, `player-allegiance.png`, `player-home.png`, `player-join.png`, `steward-home.png`. |
| Sound | Ambient game sound only, or music you own or have licensed. Keep voice lines short; the cards carry the story. |
| Privacy | Do not show base locations, routes or stashes of other houses, and no real-world names. Player names in the Chronicle are fine, because they are public in-game names. |

---

## Shot list

Times are cumulative. "VO" is an optional voice-over line; "CARD" is on-screen text (Cinzel or another serif, gold on black, as in the apps).

| # | Time | Shot | Source you capture | Text / VO |
|---|---|---|---|---|
| 1 | 0:00–0:04 | Black. A single ember drifts up. The Realm sigil draws itself in a gold line, then fills with blood red. | Player app first run (the sigil reveal), window capture, cropped to the sigil | — |
| 2 | 0:04–0:08 | Dusk over the valley. Slow pan across the hills to the Old Throne on its hill. | In-game: stand on a ridge at dusk and turn slowly (or walk forward) | VO: *"The Builder's line is gone."* |
| 3 | 0:08–0:12 | Close on the empty throne. A player walks into frame and sits. | In-game, a second player sits on the throne | CARD: **THE CROWN BELONGS TO THE SEAT** |
| 4 | 0:12–0:15 | Overlay proclamation (`coronation` card, e.g. "**Aldric Varrow takes the throne**") sweeps in over the throne room. | Overlay `coronation` card over the same scene | — |
| 5 | 0:15–0:20 | Quick cuts: six house banners and sigils, one per half-second (Iron Stag, White Oak, Black Raven, Drowned Bell, Ember Hound, Silver Eel). | Player app "Swear your allegiance" screen; or in-game banners/flags if your houses have built them | CARD: **SIX GREAT HOUSES** |
| 6 | 0:20–0:24 | Two house leaders face each other. One kneels. Overlay: "House Ashgrove swears fealty to House Varrow". | In-game `/swear` and `/swear accept` on camera, overlay `oath_sworn` | VO: *"Swear an oath..."* |
| 7 | 0:24–0:28 | Same two leaders, later. One turns away. Overlay in red: "House Dunmere renounces its oath". | In-game `/renounce` + `/renounce confirm`, overlay `oath_broken` | VO: *"...and everyone will know if you break it."* |
| 8 | 0:28–0:31 | Chat close-up: `/claim declare`. Overlay: "House Corvane raises a claim at the Hearth". | In-game chat + overlay `claim_declared` | CARD: **NO CROWN IS TAKEN BY STEALTH** |
| 9 | 0:31–0:34 | Realm feed countdown ticking: "Next: Crown Night · 0d 00h 00m 05s". | Player app Home, Realm feed panel (needs a Chronicle that announces the next event; see "Notes") | — |
| 10 | 0:34–0:44 | **Crown Night.** Fast cuts: a gate breached, archers on a wall, a charge up the throne hill, the claimant reaching the throne. Overlay: the `rebellion_started` card, then the `rebellion_ended` card with the result. | In-game during the rebellion window, several players filming (with permission); overlay `rebellion_started` / `rebellion_ended` | VO (one line, low): *"The Lawful Hours are open."* |
| 11 | 0:44–0:48 | A captive led away. Overlay: the `ransom_set` card (300 gold; the cap is 500). Cut to them walking free. | In-game `/ransom set`, `/ransom paid`; overlay `ransom_set` / `released` | CARD: **BETRAYAL IS THE GAME. HARASSMENT IS NOT.** |
| 12 | 0:48–0:52 | The Chronicle page scrolling: coronations, oaths, betrayals. | `http://127.0.0.1:8787/realm`, browser capture | CARD: **EVERY ACT IS WRITTEN DOWN** |
| 13 | 0:52–0:56 | Player app: press **Join**, the steps tick through "Checking server → Launching Steam → Game starting → Joining". | Player app window capture (any server) | CARD: **ONE CLICK. YOUR OWN STEAM COPY.** |
| 14 | 0:56–1:00 | Sigil over black. Tagline. Where to join. | Player app first run end frame, or the sigil on black | CARD: **THE REALM** · *Swear an oath. Claim a crown. Answer for it.* · **[Discord link] · Reign of Kings on Steam** |

### 30-second cut
Shots 1, 3, 4, 5 (shortened to 2 s), 7, 8, 10 (shortened to 6 s), 13, 14.

### Vertical (9:16) clips
Shots 4, 7 and 10 work as 10–15 s vertical clips: crop to the overlay card and the action under it.

---

## Notes and honesty checks

- **Exact card wording comes from the plugins** (each event's title); the quotes above are examples from the sample data. Film the real cards.
- **Everything shown must be real.** Each overlay card above is an actual Chronicle event type (`chronicle/server.js`, `EVENT_TYPES`). Do not fake an overlay card for an event the plugins did not record.
- **Shot 9 (countdown):** the player app shows the countdown only when a server's Chronicle announces the next event (`next` in `/api/state`, see `launcher/lib/shared/feed.js`). *UNVERIFIED: no plugin writes that field today, and the Chronicle service currently drops it.* Until it does, replace shot 9 with the in-game `/events` or `/crown` output that shows the next window.
- **Shot 13 (join):** the panel's last two steps are timed guidance. Realm cannot see inside the game. Do not caption it "joins in one second".
- **Required on the end card:** the game is **Reign of Kings, bought on Steam**. Realm never changes game files and is not affiliated with the game's developers.
- **Rights:** use only footage you or consenting players recorded, and music you have the rights to. Do not use the game's logo as your own branding.
