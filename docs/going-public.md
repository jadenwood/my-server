# Going public: run the servers, open them up, publish the list, hand out the player app

This guide is for the owner. It assumes Windows and **Realm Steward** 0.3.0 or later (`Realm-Steward-Setup-<version>.exe`). Steps marked **UNVERIFIED** come from reading the game code ([`join-and-scale.md`](join-and-scale.md), [`seamless-design.md`](seamless-design.md)) and have not yet been tried on a real PC. Do them in the order given, and stop when something does not behave as written.

Ground rules that do not change:
- Players use **their own Steam copy** of Reign of Kings. Realm never patches, repackages or launches game files. Every join goes through the player's Steam.
- Only the **test copies** under `G:\RealmTest\...` are changed (Oxide and plugins included). The Steam install of the dedicated server is only read.
- Realm Steward changes the firewall **only** when you press the button and accept Windows' prompt, and it never changes your router.

---

## 1. Run the server locally first

1. Install Realm Steward and open it. The setup wizard copies the dedicated server to `G:\RealmTest\server`, starts it once, installs Oxide 2.0.3867 (SHA-256 checked) and the Realm plugins.
2. **Settings > Server program**: if `Server.exe` fails with "EACCES" or asks for administrator rights, Realm switches to `ROK.exe -batchmode -nographics -silentcrash` by itself and remembers it.
3. **Servers > Server settings** for Server I:
   - **Server name**.
   - **Max players**: up to **120**. The game has no limit of its own ([DEC]). 120 is Realm's cap, and **UNVERIFIED** for performance.
   - **Secs between joins**: the game lets queued players in one at a time, every N seconds (default 10). **3** fills 120 slots in about 6 minutes instead of 20.
   - **Restart by itself after a crash** and **Back up the world before every restart** are on by default.
   - **Restart every day at**: pick a quiet hour, for example 06:00. The game announces it in chat from 60 minutes before. **UNVERIFIED:** that the announcements show and that the server exits by itself; Realm restarts it either way.
   - Save, with the server stopped.
4. **Start**. Wait for `Initialize engine version` in the console. Then open Reign of Kings through Steam and direct connect to `127.0.0.1`, port `7350`.

### More servers (up to four)

Press **Add server** on the Servers screen. Realm proposes `G:\RealmTest\s2\server`; pick another folder if you like (it must not be on C: or inside Steam). The wizard then sets the new copy up. Each copy needs several GB.

Stop the other servers during the new one's **first start**. Until its settings file exists, a new copy uses the game's default ports, which Server I already uses.

| Server | Game, UDP | Ping, TCP | Steam query, UDP |
|---|---|---|---|
| I | 7350 | 7350 | 27015 |
| II | 7360 | 7360 | 27025 |
| III | 7370 | 7370 | 27035 |
| IV | 7380 | 7380 | 27045 |

**UNVERIFIED:** that two real servers run side by side. The game hard-codes one Steam port, 8766. Try Server II next to Server I and check that both consoles print `Steam game server started … Secure: True` before adding more.

---

## 2. Go public

Open **Public** and pick the server at the top right.

### Step 1: Network

Choose **Public** and Save. On the next start Realm writes `bindIP = '0.0.0.0'` into that server's `ServerSettings.cfg`. Tick "Also list it in the game's own server browser" only if you want strangers to find it; it sets `isPrivate = 'False'`. That flag only hides or shows the server in the browser. **It is not a password.** For a closed test, use the server password or a whitelist plugin.

Restart the server so the change takes effect.

### Step 2: Windows Firewall

Press **Add firewall rules**. Realm shows the exact rules twice, once in the app and once in a Windows dialog, and then Windows asks for administrator permission (UAC). Accept it. Four rules are created, all limited to that server's `ROK.exe`:

| Rule | Action | Protocol | Port (Server I) |
|---|---|---|---|
| `Realm s1 game (UDP)` | Allow | UDP | 7350 |
| `Realm s1 ping (TCP)` | Allow | TCP | 7350 |
| `Realm s1 Steam query (UDP)` | Allow | UDP | 27015 |
| `Realm s1 admin console BLOCK (TCP)` | **Block** | TCP | 11000-11003 |

The block rule matters. The game opens an admin console on TCP 11000 on every network card. It has **no password**, and it shuts the server down when its last client disconnects ([DEC] `SocketAdminConsole`). If Windows ever asked "Allow ROK.exe?" and you clicked Allow, that console could be reachable. The block rule closes it again.

**Remove rules** deletes exactly these four names, after another administrator prompt.

**UNVERIFIED:** never run on Windows yet. Afterwards, check in *Windows Defender Firewall > Advanced settings > Inbound Rules* that the four "Realm s1" rules exist. **UNVERIFIED:** that `ROK.exe`, not `Server.exe`, owns the sockets. To check, run `netstat -ano | findstr 7350` and compare the PID with Task Manager. If it is `Server.exe`, tell the build team before relying on the rules.

### Step 3: Router

In your router's **port forwarding** page, forward the ports from the table above to this PC's LAN address. The Public screen shows that address, for example `192.168.1.20`.

- UDP 7350 and TCP 7350 (game and ping; players who cannot reach the ping port get kicked)
- UDP 27015 (Steam query: players' ping display and the server list)
- **Never** forward TCP 11000-11003.

Do this for each public server, using its own ports.

If Realm's check says your public address is a **carrier-grade NAT** address (100.64.x.x), port forwarding cannot work from your connection. Ask your provider for a public IPv4, or host the server on a rented machine (VPS).

### Step 4: Check

Press **Run checks** while the server runs. A green dot means fine, amber means look at it, red means it will not work. The checks:
- bindIP is what Realm wants;
- the three ports are open;
- the Steam query answers on this PC and on the LAN address;
- the game's own `Steam game server started. (IP: …, Logged: True, Secure: True)` line, which gives the public IP that Steam sees;
- a TCP connection to your public address;
- the firewall rules.

Many routers cannot reach their own public address from inside the house, so an amber "not reachable from inside your own network" is normal. **The real test is a friend outside your network**: they install the Realm player app (section 4) and should see the server **Online** with a ping.

Use a **DNS name** rather than your raw IP, for example from a free dynamic-DNS service pointed at your home IP. Players then never need a new list when your IP changes. A public home IP also invites attacks; plan a VPS before going beyond a closed test.

---

## 3. Publish the server list

Open **Publish**.

1. **Create signing key**, once. Keep using the same key: the player app trusts only the key it was built with. The private key stays in `%APPDATA%\Realm\realm-signing-key.json`, encrypted for your Windows user. Never share that file; anyone holding it can point players at other servers. Back it up somewhere private, such as a USB stick.
2. **Where you will host servers.json**: an `https://` address you control. Two free options:
   - **GitHub Pages**: a repository with Pages turned on, then `https://<you>.github.io/<repo>/servers.json`.
   - **A public gist**: use its **Raw** link, without the commit id in it, so the link always serves the latest version.
3. For each server you tick:
   - **Public address**: your DNS name or public IP.
   - **Region**: for example EU.
   - **Max players**.
   - **Chronicle address**, optional. It is only useful if players can reach it; the Chronicle normally listens on this PC only.
4. **Sign & write servers.json**. Three files appear in the output folder (default `G:\RealmTest\publish`):
   - `servers.json`: upload this to the address from item 2. **Realm does not upload anything.**
   - `player-config.json`: goes into the player build (section 4).
   - `PUBLISH-README.txt`: a reminder of these steps and the expiry date.
5. Publish again before the list **expires** (30 days by default) and whenever an address changes. Each publish gets a higher version number; player apps refuse anything older than the newest they have seen.

---

## 4. Give players the player installer

1. Copy `player-config.json` from the publish folder to `launcher\player\player-config.json` in this repository. If you run Realm Steward from the repository, tick "Also put the key and list into this repo's player build" on the Publish screen instead.
2. Build: `cd launcher`, `npm install`, `npm run dist:player`. The installer is `launcher\dist\Realm-Setup-<version>.exe`, and `Realm-<version>-portable.exe` runs without installing.
3. Share the installer, for example as a GitHub release asset or on your Discord. Windows SmartScreen will warn about an unsigned installer the first time ("More info > Run anyway"). Code signing is a separate, paid decision.

What players get:
- They see the signed list with live status, and **Join** or **Join best server**.
- Steam starts their own copy with `-ip <address> -port <port>`, and the game connects by itself. **UNVERIFIED:** that Easy Anti-Cheat's launcher passes these arguments on (test T1 to T3 below).
- Whatever happens, the address is copied and a small card shows where to paste it.
- If the game is missing, Realm offers to install it through Steam.
- Links like `realm://join/s1` open Realm and ask before joining. In Discord, post your https download page next to them, because Discord does not make `realm://` clickable.

The player app contains no server controls of any kind. It only reads the list, pings the servers and asks Steam to start the game.

---

## 5. Tests to run on your PC (they unblock the UNVERIFIED parts)

| # | Test | Pass looks like |
|---|---|---|
| T1 | In Steam, set Reign of Kings' launch options to `-ip 127.0.0.1 -port 7350`, then press Play while Server I runs. Clear the options afterwards. | The game skips the menu and joins. |
| T2 | In the player app, press Join on Server I. | Steam may ask once about the launch options; the game joins. If it stops at the menu, the card's paste steps work. |
| T3 | Same as T2 with a DNS name in the list. | It joins. |
| T6 | Run Server I and Server II together. | Both consoles print `Steam game server started … Secure: True`, and a player can join each one. |
| T7 | Set a daily restart 15 minutes ahead. | Chat shows the countdown; the server restarts by itself, after an `-auto` backup. |
| T8 | While public, run `netstat -ano` and look at 7350 and 27015. | The PID is `ROK.exe`. |
| T9 | Player app on another PC. | The server shows Online with a ping (the A2S answer). |
| Firewall | Add rules, then look in Windows Defender Firewall. | Four "Realm s1" rules, scoped to `G:\RealmTest\server\ROK.exe`. Remove rules deletes them. |
| Load | Fill toward 120 players. | Watch CPU, RAM and the console. Lower **Max players** if the server struggles. |

Report each result. Once T1 and T2 pass, joining is proven end to end.
