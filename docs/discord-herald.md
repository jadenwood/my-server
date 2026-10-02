# Discord herald

Realm Steward can post new Chronicle events (coronations, claims, rebellions, betrayals, treaties, season ends, tournament champions and more) to a Discord channel as they happen. It is **off** until you turn it on, and it never posts history: it starts after the newest event that exists when you switch it on.

Screenshot: `docs/img/steward-discord.png`.

## Set it up

1. In Discord, open the channel you want, then **Edit Channel > Integrations > Webhooks > New Webhook**. Copy the webhook URL.
2. In Realm Steward, open **Settings > Discord herald**. Paste the URL.
3. Tick **Post new Chronicle events to Discord** and choose which kinds of event to post. The defaults are the big moments: the crown, claims and rebellions, new houses, broken oaths, treaties, ransoms paid and seasons.
4. Optionally set the realm name used in posts, and your Realm Portal's https address so each post links to that event on the website (`portal/README.md`).
5. **Save Discord settings**, then **Send test**. A test message "A raven from the Steward" should appear in the channel.

## How it works

- **Source.** Every 15 seconds the Steward reads `oxide\data\RealmChronicle.json` in the main server copy (the same folder the overlay reads). It only reads it. Events newer than the last one relayed are posted. If the Chronicle is wiped and ids start again, the herald starts after the new newest event and does not replay anything.
- **Looks.** Each post is an embed: the kind of event, its title and text, the house and the people named, the time and the Chronicle number. The embed's colour is the **house colour**: the same palette and name hash as the stream overlay and the portal, so House Varrow is plum everywhere. Events without a house get a colour for their kind (gold, blood, moss, iron).
- **Rate limits.** Messages go through a queue: one request every 2 seconds at most, 20 a minute at most, and up to 10 events in one message (within Discord's 6000-character limit). The queue obeys Discord's `retry_after` on HTTP 429 and its `X-RateLimit-Remaining` / `X-RateLimit-Reset-After` headers. Server errors and network failures are retried with growing waits (2, 4, 8, 16 s). The queue holds at most 200 events; if Discord is unreachable for a long time, the oldest are dropped and counted.
- **When the webhook is gone.** If Discord answers 404 or 401 (webhook deleted or token reset), the herald turns itself off, says so on the card, and writes it to the app log (**Settings > Open logs**).
- **Safety.** Mentions never ping anyone (`allowed_mentions` is empty and `@everyone`, `@here` and `<@id>` are defused), and player-written text is Markdown-escaped, so a house name cannot add links or formatting. Webhook usernames never contain "discord", which Discord rejects.

## Where the webhook URL is kept

The URL is a secret: anyone who has it can post in that channel.

- It is stored only in `%APPDATA%\Realm\realm-discord.json` on the owner's PC, never in the repo, the server folder or the logs.
- It is encrypted with Windows (Electron `safeStorage`, which uses DPAPI) when that is available. The file then cannot be read on another Windows account or another PC; the card says so and asks you to paste the URL again.
- After saving, the app only ever shows a redacted hint (`.../webhooks/<id>/•••••• (token hidden)`). **Forget** deletes it.
- Only `https://discord.com/api/webhooks/<id>/<token>` and `https://discordapp.com/api/webhooks/<id>/<token>` are accepted. Other hosts, http, ports, user names, query strings and fragments are refused.

## What is verified

- Unit tests (`launcher/test/discord.test.js`, no network): URL validation and redaction, embed building and Discord's limits, mention and Markdown escaping, batching, the 2-second spacing and the 20-a-minute cap, 429 and `retry_after`, rate-limit headers, back-off and give-up on 5xx and network errors, fatal 404/401, the bounded queue, encrypted storage, and the Chronicle watcher (first run, new events, chosen types, wipes, half-written files).
- UI smoke test (`launcher/scripts/discord-screens.mjs`, under xvfb): the card mounts in Settings, a non-Discord URL is refused, the saved hint hides the token and the bridge exposes only get/save/test/forget. It never presses **Send test**.
- **UNVERIFIED:** posting to a real Discord webhook from this build environment (no network calls are made in tests). Discord does not publish exact per-webhook limits; the queue is deliberately slower than the commonly seen 5 requests per 2 seconds and follows the headers Discord returns. Event types from RealmLaws, RealmDynasties, RealmRenown, RealmTreasury and RealmRavens are now registered in RealmChronicle, so they post once the updated RealmChronicle.cs is deployed (Update plugins).
