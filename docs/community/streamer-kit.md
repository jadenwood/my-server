# Realm streamer kit

Everything a creator needs to put the Realm on stream: the live overlay, event formats that make good broadcasts, etiquette, and a ready-to-post announcement.

The setting is [Ostreval](lore.md). The rules that matter most for streams are in [rules.md §5](rules.md#5-streams-sniping-and-ghosting).

---

## 1. The overlay

The Chronicle service (`chronicle/`) serves a transparent 1920x1080 overlay. It shows:
- the **crown plate**: the current monarch, their house and how long they have reigned
- up to 6 **house banners**
- a scrolling **ticker** of Chronicle events
- an animated **proclamation card** for each new event: coronations, claims, rebellions, oaths and betrayals, treaties, decrees, ransoms

It shows public names and acts only, never locations or inventories.

### Where it runs (read this first)

The Chronicle service binds to **`127.0.0.1:8787`**, which means it is reachable only on the PC it runs on (see `chronicle/README.md`). So:
- **Today:** the overlay works in an OBS instance **on the server PC**, for example the server owner streaming their own play.
- **Remote creators** can use it only after the operator deliberately publishes the service, for example behind a reverse proxy. That has **not** been set up, and the README warns against binding to other addresses without a reason. Until it is, remote creators run without the overlay or show the `/realm` page through screen capture on the host's stream.

### Add it to OBS

1. Make sure the Chronicle service is running. `http://127.0.0.1:8787/healthz` should respond in a browser.
2. In OBS: **Sources → + → Browser**.
3. URL: `http://127.0.0.1:8787/overlay`
4. Width: `1920`, Height: `1080`.
5. Leave **Custom CSS empty**. The page is already transparent.
6. Put the source **above** your game capture.

The stage scales to any 16:9 source size.

### Overlay options (add to the URL)

| Param | Effect |
|---|---|
| `banners=N` | Show up to N house banners (0–8, default 6). `banners=0` hides them. |
| `ticker=0` | Hide the ticker. |
| `crown=0` | Hide the crown plate. |
| `proclaim=0` | Hide the proclamation cards. |
| `hold=S` | How many seconds each proclamation stays up (4–60, default 12). |
| `poll=S` | How often the overlay checks for new events (2–60 s, default 3). |
| `replay=1` | Shows the latest event when the page loads. Useful for positioning, and remove it afterwards. |
| `bg=1` | A dim backdrop for previewing in a normal browser. **Do not use it in OBS.** |

Suggested scenes:
- **Gameplay:** `http://127.0.0.1:8787/overlay?banners=4`
- **Crown Night:** `http://127.0.0.1:8787/overlay?hold=18` (longer cards for the big moments)
- **Just Chatting / recap:** a separate Browser source with `http://127.0.0.1:8787/realm` (the public Chronicle page) at 1920x1080.

### If something looks wrong

- **"The ravens are late"** on the crown plate means the plugin has stopped refreshing the realm state. Tell an admin. The game may be fine and only the plugin stalled.
- **No cards at all:** open `/healthz`. If it says `missing`, the service is pointed at the wrong data folder (`chronicle/README.md`, "Check the data path").
- **To test without a game server**, run `npm run sample` in `chronicle/` and open `http://127.0.0.1:8787/overlay?replay=1&bg=1`.

### Stream delay

We recommend **at least a 2-minute delay** (OBS: Settings → Advanced → Stream Delay). Sniping is against the rules either way, but a delay protects you and makes enforcement easier.

---

## 2. Event formats

Each format uses mechanics that really exist in the plugins. The times are the **default** rebellion windows in **UTC**; check `/crown` for the next window on the live server.

### Crown Night: a scheduled rebellion
*The big one. Best for a co-stream across several creators.*
- **When:** a rebellion window, by default **Saturday 19:00–20:30 UTC** (90 min) or **Wednesday 19:00–20:00 UTC** (60 min).
- **Setup:** one or more house leaders run `/claim declare` **at least 60 minutes before** the window opens. Earlier is better: declaring during the week builds the story. Each declaration appears in the Chronicle and on the overlay.
- **On air:** count down with `/crown` ("Next rebellion window: …"). When the window opens, the overlay proclaims "House X rises" (`rebellion_started`). Only claimant houses can take an occupied throne until it closes.
- **The finish:** at the end of the window, `rebellion_ended` proclaims the outcome: claimant won, crown held, throne empty, or a third house took it.
- **Tip:** a new monarch's first decree and council appointments make a good closing segment.

### Oath Ceremony
- A house leader runs `/swear <liege>` on stream and the liege's leader runs `/swear accept <house>`. Both must be online.
- The overlay shows "House X swears fealty to House Y". Pair it with in-character speeches.
- The opposite version, **The Breaking**, is `/renounce` then `/renounce confirm`. It is louder, the mark is permanent until an admin pardons it, and the house cannot swear again for 24 hours.

### The Summit (treaty night)
- Several house leaders meet at a neutral spot and negotiate in character. Deals are sealed with `/treaty propose <house> [days]` and `/treaty accept <house>`.
- Viewers watch the overlay for the first `treaty_broken`.

### The Ransom Hour
- A Halloran-style hunt: capture, name a price with `/ransom set <player> <amount>` (500 gold at most), and settle with `/ransom paid` or `/ransom release`.
- The 10-minute legal clock suits short, punchy segments. The captive also deserves a good time, so keep it in character.

### The Herald's Recap
- A short weekly segment. Read the week's Chronicle (`/chronicle 15` in game, or the `/realm` page). Recap claims, coronations and betrayals, then preview the next Crown Night.

---

## 3. Etiquette

- **Show the name tags, not the map.** Avoid long shots of other houses' bases, routes or stashes. Viewers who might ghost should not get help.
- **Ask before you feature someone** in a title, thumbnail or clip about them as a person. Featuring them *as a character* in the Chronicle is fine.
- **Never point your chat at another player.** No raids into their stream to cause trouble, and no "go tell X" requests.
- **Your chat follows the rules too.** Moderate coordinates, base locations and live callouts in your chat.
- **Betrayal on stream is content. Gloating at the real person is not.** Keep it in character.
- **Credit the realm:** link the Realm community in your panels so viewers can join.
- **If something breaks** (a captive stuck past the term, a throne capture that should have been blocked), stop and ping an admin. Do not use it on air.

## 4. Assets

- House names, sigils, words and colours: [`lore.md`](lore.md). The overlay's banner colours match the primary colours listed there.
- Screenshots of the overlay are in `docs/img/` (another team maintains them).
- Use only original Realm and Ostreval names in titles and thumbnails.

---

## 5. Sample event announcement

Copy, edit the bracketed parts, and post it.

> **CROWN NIGHT: The Lawful Hours open Saturday at 19:00 UTC**
>
> The Hearth Charter is plain: no house may take the Old Throne by stealth. So House **[Dunmere]** has raised its claim in the open, against **[Aldric Varrow]** of House **[Varrow]**, who has held the crown for **[nine days]**.
>
> When the window opens on **Saturday at 19:00 UTC**, the throne can be contested for **90 minutes**, but only by houses with a declared claim. Anyone else who wants a part should get their claim in now: `/claim declare` closes 60 minutes before the window.
>
> - Swear your oaths: `/swear <house>`
> - Check the claims: `/claim list`
> - Watch the crown: `/crown`
>
> Streaming live with the Chronicle overlay: **[creator 1]**, **[creator 2]**, **[creator 3]**.
>
> You need your own Steam copy of Reign of Kings. Get the Realm launcher from **[link]**, or direct connect to **[address]**.
>
> *Betrayal is encouraged. Sniping, ghosting and harassment are not. Read the rules: [link].*
