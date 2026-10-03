# Realm Discord bot

An interactive Discord bot for the Realm server. The owner runs it on the server PC, next to the game server and the Realm Chronicle service. It:

- answers `/realm` slash commands about the crown, the houses, the Chronicle, realm events and how to join;
- keeps one **live status message** up to date in a channel you pick;
- optionally lets members take a **"Sworn to &lt;House&gt;"** role with `/realm swear <house>`.

It only **reads** the realm's data, from the local Chronicle service (`http://127.0.0.1:8787`) and the Oxide data files. It never writes to the game, the server folder or the plugins' files. It does not touch any game file and needs no Steam login.

It works alongside the **Discord herald** in Realm Steward (`docs/discord-herald.md`). The herald posts each new Chronicle event to a channel through a webhook. This bot answers questions and keeps a status board. You can run either one, or both.

## Commands

| Command | Who sees the answer | What it shows |
|---|---|---|
| `/realm status` | everyone in the channel | The crown, players online, houses, the realm event running or coming next, the join address, the latest three Chronicle entries. Says so when the server has stopped reporting. |
| `/realm king` | everyone | The monarch, how long they have reigned (shown in each reader's own time zone), the ruling house and its vassals, recent matters of the crown (coronations, claims, rebellions, decrees). |
| `/realm houses` | everyone | Every house, with vassals listed under their liege, sigils, sworn counts, and a crown on the ruling house. |
| `/realm chronicle [count]` | everyone | The latest 1 to 15 Chronicle entries, newest first (default 5). |
| `/realm events` | everyone | The realm event running now (tournament leaders, the King's Hunt quarry, Crown Night captures), the weekly schedule for the next 8 days, and recent results. |
| `/realm join` | only you | Server address and port, the Realm player app download button, and the `realm://join/<id>` link. |
| `/realm whois <house>` | everyone | One house: sworn count, liege, vassals, leaders and officers, founding date, oath and treaty record, live treaties, the lore of the six great houses, and recent Chronicle mentions. House names autocomplete. |
| `/realm swear <house>` | only you | Gives you the role "Sworn to &lt;House&gt;" and removes any other "Sworn to ..." role. Only shown when house roles are on. |
| `/realm forswear` | only you | Removes your "Sworn to ..." role. |

All player-written text (house names, sigils, Chronicle titles) is Markdown-escaped and no message can ping anyone: every reply is sent with mentions turned off.

### House roles are self-declared

`/realm swear` is a **Discord badge only**. There is no verified link between a Discord account and a game account, so the bot cannot check that you are really in that house in game, and it does not try. The reply says this to the member. To join a house in game, its leader still uses `/house invite`.

Rules the bot follows for house roles:

- You can only swear to a house that exists in the Chronicle right now. Members cannot invent roles.
- The bot only manages roles whose name starts with `Sworn to `. It never touches any other role.
- It **refuses to hand out a "Sworn to ..." role that has any permission**. If someone gives such a role a permission, the bot stops using it until the permission is removed.
- Roles it creates have no permissions, are not shown separately in the member list, cannot be mentioned, and use the house's banner colour (the same colour as on the stream overlay and the portal).
- A member must wait `REALM_SWEAR_COOLDOWN_MINUTES` (default 60) between oaths. Forswearing is always allowed and does not reset the wait.
- The bot creates at most `REALM_SWEAR_MAX_ROLES` (default 25) roles.

## Set it up

You need Node.js 22 or newer on the server PC (https://nodejs.org, the LTS installer).

### 1. Create the Discord application

1. Open https://discord.com/developers/applications and choose **New Application**. Name it, for example "Realm Herald".
2. On **General Information**, copy the **Application ID**.
3. On **Bot**:
   - Press **Reset Token** and copy the token. Treat it like a password: anyone who has it controls the bot. You see it only once.
   - Turn **Public Bot** off, so only you can add it to servers.
   - Leave all three **Privileged Gateway Intents** off. The bot uses only the `Guilds` intent. It never reads messages.
4. On **Installation**, you can set **Install Link** to *None*. You invite the bot with the link in step 2 instead.

### 2. Invite it to your server

Replace `APPLICATION_ID` and open the link in a browser:

- With house roles (Manage Roles):
  `https://discord.com/oauth2/authorize?client_id=APPLICATION_ID&scope=bot%20applications.commands&permissions=268553216`
- Without house roles:
  `https://discord.com/oauth2/authorize?client_id=APPLICATION_ID&scope=bot%20applications.commands&permissions=117760`

**Scopes:** `bot` and `applications.commands`.

**Permissions:**

| Permission | Why | Value |
|---|---|---|
| View Channels | see the status channel | 1024 |
| Send Messages | post the status message | 2048 |
| Embed Links | the status message and every answer are embeds | 16384 |
| Read Message History | find its own status message again after a restart | 65536 |
| Attach Files | the pictures on the status message (the house sigil, the realm emblem) | 32768 |
| Manage Roles | only for house roles | 268435456 |

Slash command answers do not need channel permissions. The five channel permissions are only needed in the status channel. Without Attach Files the status message still works, just without pictures; the console says so once.

**For house roles:** in **Server Settings > Roles**, drag the bot's role **above** every "Sworn to ..." role. Discord only lets a bot give roles that sit below its own highest role. Manage Roles does not let the bot give any role that has permissions, because the bot refuses those itself.

### 3. Get the ids

In Discord, open **User Settings > Advanced** and turn on **Developer Mode**. Then right-click your server and choose **Copy Server ID**. For the status message, right-click the channel and choose **Copy Channel ID**. A read-only channel such as `#realm-status` works well. Give the bot the permissions above in that channel.

### 4. Configure

```powershell
cd <repo>\bot
npm install
copy .env.example .env
notepad .env
```

Fill in `DISCORD_TOKEN`, `DISCORD_APPLICATION_ID`, `DISCORD_GUILD_ID` and, if you want the status message, `REALM_STATUS_CHANNEL_ID`. Set `REALM_DATA_DIR` to the same Oxide `data` folder the Chronicle service reads. Set the join address, port and player app link. Every setting is explained in `.env.example`.

`.env` is gitignored (`bot/.gitignore`). Never put the token anywhere else. Settings in real environment variables override `.env`.

| Setting | Default | Meaning |
|---|---|---|
| `DISCORD_TOKEN` | (required) | The bot token |
| `DISCORD_APPLICATION_ID` | (required) | The application id |
| `DISCORD_GUILD_ID` | empty | Your server. With it, `/realm` is registered to that server and appears at once |
| `REALM_REGISTER_COMMANDS` | `guild` if a server id is set, else `global` | Register `/realm` on start: `guild`, `global` (can take up to an hour to show) or `off` |
| `REALM_STATUS_CHANNEL_ID` | empty (off) | Channel for the live status message |
| `REALM_STATUS_INTERVAL_SECONDS` | `60` (minimum 15) | How often the status is checked |
| `REALM_CHRONICLE_URL` | `http://127.0.0.1:8787` | The Chronicle service. A non-local address is allowed but gives a warning |
| `REALM_DATA_DIR` | `G:\RealmTest\server\oxide\data` | Oxide data folder: `RealmHouses.json`, `RealmEvents.json` |
| `REALM_CONFIG_DIR` | `config` next to the data folder | Oxide config folder: the `RealmEvents.json` schedule |
| `REALM_NAME` | `The Realm of Ostreval` | Name used in titles |
| `REALM_JOIN_ADDRESS` / `REALM_JOIN_PORT` | empty / `7350` | What `/realm join` shows. A DNS name works (`docs/join-and-scale.md` §1.4) |
| `REALM_SERVER_ID` | empty | The server's id in the player app's signed list, for `realm://join/<id>` |
| `REALM_PLAYER_APP_URL` | empty | https download page for the Realm player app (shown as a button) |
| `REALM_PORTAL_URL` | empty | https address of the Realm Portal (title link and button) |
| `REALM_SWEAR_ENABLED` | `false` | Turn on `/realm swear` and `/realm forswear` |
| `REALM_SWEAR_CREATE_ROLES` | `true` | Create a missing "Sworn to ..." role on first use. With `false`, admins create them (with no permissions) |
| `REALM_SWEAR_COOLDOWN_MINUTES` | `60` | Wait between oaths |
| `REALM_SWEAR_MAX_ROLES` | `25` | Most roles the bot creates |
| `REALM_BOT_STATE_FILE` | `state/bot-state.json` | The bot's own state: status message id and oath times. Gitignored |
| `REALM_EMBED_ART` | `true` | Pictures on the embeds (see "Pictures" below). `false` sends text only |
| `REALM_ART_DIR` | `..\art\png` | Where the art pack's PNGs are |

### Pictures

Every answer carries the realm emblem next to the realm's name, and a picture that fits it:

| Answer | Picture |
|---|---|
| `status`, `king`, `houses`, the status message | the ruling house's sigil (one of the six great houses), else the crown |
| `chronicle` | the newest entry's icon, a season medal for a season, or the badge of a renown title |
| `events` | the beacon, lit when a realm event is running |
| `whois`, `swear` | the house's sigil (great houses), else the house or oath icon |
| `join` | the realm emblem |

The pictures are PNGs from the Realm art pack (`art/png` in this repository). They are attached to the message and shown with `attachment://`, so nothing has to be hosted. A house a player founds has no drawn sigil; its embeds keep its banner colour. **UNVERIFIED:** these attachments have only been checked against fakes; post `/realm king` once on your server to see the sigil thumbnail and the emblem.

### 5. Check, then start

Start the Chronicle service first (`chronicle/README.md`). Then:

```powershell
npm run check-config   # validates .env, reaches the Chronicle service and reads the data files; does not sign in to Discord
npm start
```

On start the bot signs in, registers `/realm`, and posts the status message. The console shows each step. If something is wrong it says what to fix, for example a missing scope or permission. Stop it with Ctrl+C.

Other commands:

```powershell
npm run commands                              # print the /realm command JSON (no token needed)
npm run register                              # register /realm without starting the bot
node scripts/register-commands.js --clear     # remove /realm from your server
npm test                                      # unit tests (no network, no Discord)
```

To keep the bot running after you log off, run `npm start` from a Task Scheduler task "At log on" with "Start in" set to the `bot` folder, as the same Windows user that runs the server. **UNVERIFIED:** this has not been tried on the owner's PC.

## The live status message

- The bot posts one message in `REALM_STATUS_CHANNEL_ID` and then keeps **editing** it. The message id is saved in the state file, so a restart edits the same message. If someone deletes the message, the bot posts a new one.
- It edits only when something shown has changed, plus once every 10 minutes. Times use Discord's own timestamps (`<t:...>`), so "2 hours ago" stays correct without edits.
- If the game server stops refreshing the Chronicle (`stale` in `/api/state`), the message says the server is not reporting. If the Chronicle service itself cannot be reached, it shows "The ravens are late".
- If Discord refuses the edit (missing permission, deleted channel), the bot logs the reason once and waits longer between tries: it doubles the wait each time, up to 10 minutes.

## Where the data comes from

| Data | Source | Notes |
|---|---|---|
| Crown, houses, online count, staleness | `GET /api/state` on the Chronicle service | Written by `plugins/RealmChronicle.cs` |
| Chronicle entries | `GET /api/events?limit=N` | Labels for event types come from the Chronicle pages' list (`chronicle/public/assets/common.js`, read as text at start) and from `plugins/docs/*/EVENTS.json` for types that are not yet registered |
| Leaders, officers, founding date, oath and treaty record, treaties | `<data>/RealmHouses.json` | Optional. Steam ids in that file are never shown |
| Running realm events | `<data>/RealmEvents.json` | Optional |
| Event schedule | `<config>/RealmEvents.json` | Optional. Same day rules as the plugin: weekday names, `Daily`, `Weekdays`, `Weekends`, UTC |

Each file is read again only when it changes. If the plugin is in the middle of rewriting a file, the bot keeps the last good copy. Missing files simply leave those parts out.

The bot adds no Chronicle event types. **No new event types need registering.**

## Files

```
index.js                    entry point (npm start, npm run check-config)
scripts/register-commands.js register, print or clear /realm
src/config.js               .env and environment, validation; never prints the token
src/chronicle-client.js     Chronicle API client (timeout, short cache, response checks)
src/realm-data.js           RealmHouses / RealmEvents files and the event schedule
src/commands.js             /realm definitions (raw Discord API JSON) and limit checks
src/handlers.js             interaction routing, private vs public replies, error messages
src/views.js                every embed and button
src/status-board.js         the live status message
src/swear.js                "Sworn to <House>" roles
src/state-store.js          bot state file (atomic writes)
src/text.js                 labels, house colours, escaping, Discord limits
src/lore.js                 the six great houses (docs/community/lore.md)
test/                       node:test suites, fixtures and fakes for discord.js
```

## What is verified

- `npm test`: 84 tests in this build environment, Node 22, all passing, with discord.js 14.27.0 installed. They use a fake discord.js and a fake Chronicle service, plus these real pieces:
  - the real `chronicle/server.js` serving its sample data over HTTP, read by the bot's client and rendered by the `/realm` handlers (end to end);
  - the real discord.js package: the constants the bot uses, the `/realm` JSON validated by `SlashCommandBuilder`, what `commands.set()` sends (guild-only context and option limits kept), and a real `Client` routing an interaction to the handlers (no login).
- Event labels and house colours are checked against `chronicle/public/assets/common.js`, so they match the overlay.
- `npm run check-config` was run against the real Chronicle service with sample data. It reported the king, 6 houses, and the fixture event files.
- **UNVERIFIED:** signing in to Discord, registering commands, posting or editing messages, and creating or assigning roles against the real Discord API. No token was used and no network call to Discord was made from this build environment.
- **UNVERIFIED:** the exact permission integers above against Discord's current invite screen. They are the documented bit values (View Channel 1<<10, Send Messages 1<<11, Embed Links 1<<14, Read Message History 1<<16, Manage Roles 1<<28).
- **UNVERIFIED:** Discord's handling of the `realm://` link. Discord does not make it clickable, so the bot shows it as text to copy, next to the https download button.
- **UNVERIFIED:** the in-game direct-connect wording in `/realm join`. The game has a direct-connect join path (`docs/join-and-scale.md` §1.4), but the exact menu labels were not checked in the client.
