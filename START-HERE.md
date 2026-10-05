# Start here: from a fresh clone to a friend on your server

This is the owner's short path. About 10 minutes of your own time, plus waiting for downloads and copies. Each step says what you should see. If you see something else, stop and look it up in [`docs/troubleshooting.md`](docs/troubleshooting.md).

**What has been seen working on the owner's PC (Windows 11, 2026-10-02):** steps 2 to 4 with an earlier Steward build. The owner joined their own server, all 14 plugins loaded and Season 1 started. **Not yet seen on real hardware (UNVERIFIED):** `Build-Realm.bat` itself and the 1.0.0 installers (tried only under wine on Linux: the batch file's checks, the build with Windows Node.js, and installing and uninstalling both apps), the router forwarding, a friend joining from outside, and the published server list in the player app.

## 0. What you need (once)

- A Windows 10 or 11 PC that stays on while people play.
- Steam, signed in, with **Reign of Kings** and **Reign Of Kings Dedicated Server** installed (Steam > Library > Tools for the server).
- A drive **other than C:** with about 10 GB free. Realm refuses to put the server copy on C: or inside a Steam folder. The default is `G:\RealmTest\server`.
- **Node.js 22 LTS** from <https://nodejs.org> and **Git for Windows** from <https://git-scm.com/download/win>. Default options are fine for both.
- The login for your router's admin page.

You never need administrator rights, except for one Windows prompt when Realm adds the firewall rules in step 5.

## 1. Get the code and build the installers (2 minutes, then wait 5 to 10)

1. Open a command window on the drive you picked (not C:; `Build-Realm.bat` refuses to run from C:) and clone the repository:
   ```
   git clone https://github.com/jadenwood/my-server.git G:\Realm
   ```
   (Or download the ZIP from GitHub and unzip it to `G:\Realm`.)
2. In `G:\Realm`, double-click **`Build-Realm.bat`**.

You should see five numbered steps: `[1/5] Checking Node.js and Git`, `[2/5] Installing the launcher's packages`, `[3/5] Checking the launcher code`, `[4/5] Running the launcher tests`, `[5/5] Building both installers`. It ends with **Done** and opens `release\1.0.0\`, which holds:

| File | For |
|---|---|
| `Realm-Steward-Setup-1.0.0.exe` | you: the owner app, installed on the server PC |
| `Realm-Setup-1.0.0.exe` | your players |
| `SHA256SUMS.txt` | the SHA-256 of both, so anyone can check a download |

The first build downloads about 300 MB. If a step fails, the window prints `PROBLEM:` and `FIX:` lines and waits for a key. Do what the FIX line says, then double-click `Build-Realm.bat` again.

At the end it may print a note that Realm-Setup has no published server list yet. That is expected now; step 7 fixes it.

## 2. Install Realm Steward (1 minute)

Run `release\1.0.0\Realm-Steward-Setup-1.0.0.exe`. Windows SmartScreen may say "Windows protected your PC", because the installer is not code-signed: press **More info > Run anyway**.

It installs for your Windows user only (no administrator prompt) into `%LOCALAPPDATA%\Programs\Realm Steward` and adds a desktop and a Start-menu shortcut. If an older Realm Steward is installed, this one replaces it in place. Your settings (`%APPDATA%\Realm`, including the signing key) and your worlds (`G:\RealmTest\...`) are kept, also when you uninstall.

## 3. Let the setup wizard build the server (wait 5 to 15 minutes)

Open **Realm Steward**. The setup wizard starts by itself and shows a progress bar per step:

1. Finds `Reign Of Kings Dedicated Server` in your Steam libraries.
2. Copies it to `G:\RealmTest\server`. The Steam copy is only read, never changed.
3. Starts it once so the game writes its settings.
4. Downloads Oxide 2.0.3867 and checks its SHA-256.
5. Installs Oxide into the copy, with a backup of every replaced file.
6. Copies the 14 Realm plugins into it.

Then go to **Servers** and press **Start**. The server is ready when the console shows `Server for N players started on port 7350.` and then `Game has started.` This takes 1 to 3 minutes.

## 4. Join it yourself (2 minutes)

Start Reign of Kings **from Steam** and open direct connect. Type **`127.0.0.1` in the address box and `7350` in the port box**. Never type `127.0.0.1:7350` into the address box: the game then says "Unable to resolve host name".

You should reach character creation. In chat, `/season`, `/house list`, `/crown` and `/events` should answer (UNVERIFIED: the plugins loaded on the owner's server, but these chat replies have not been checked yet).

## 5. Go public (3 minutes, plus your router)

Open **Public** in Steward and pick Server I at the top right.

1. **Network:** choose **Public** and press **Save**. Then on **Servers**, press **Restart** so the server listens on every network card.
2. **Windows Firewall:** press **Add firewall rules** and accept the Windows prompt. Realm creates four rules, all limited to this server's `ROK.exe`:

   | Rule | What it does |
   |---|---|
   | `Realm s1 game (UDP)` | allows UDP 7350 |
   | `Realm s1 ping (TCP)` | allows TCP 7350 |
   | `Realm s1 Steam query (UDP)` | allows UDP 27015 |
   | `Realm s1 admin console BLOCK (TCP)` | **blocks TCP 11000-11003** |

   The block rule is the important one. The game's admin console listens on TCP 11000 on every network card, has no password, and shuts the server down when its last client leaves. Open **Doctor** and press **Run checks**: the firewall line must say **4 Realm rules found**. If it says "3 of 4 Realm rules", the block rule is missing (this happened on the owner's PC): press **Add firewall rules** again.
3. **Router:** on your router's port-forwarding page, forward these to the LAN address the Public screen shows (on the owner's PC, `192.168.1.75`):
   - **UDP 7350** (the game)
   - **TCP 7350** (the ping check; players who cannot reach it get kicked)
   - **UDP 27015** (Steam query: online status and ping in the player app)

   **Never forward TCP 11000, 11001, 11002 or 11003.**
4. Back on **Public**, press **Run checks**. Green is fine. An amber "not reachable from inside your own network" is normal: many routers cannot loop back to their own public address. A red **carrier-grade NAT** (100.64.x.x) means port forwarding cannot work on your connection; see [`docs/troubleshooting.md`](docs/troubleshooting.md).

UNVERIFIED: no friend has joined from outside yet. More detail: [`docs/going-public.md`](docs/going-public.md).

## 6. Optional: a name instead of your IP

A free dynamic-DNS name pointed at your home IP means players never need a new list when your IP changes. Use it as the public address in step 7.

## 7. Publish the server list and rebuild the player app (3 minutes)

1. In Steward, open **Publish**. Press **Create signing key** once. Back up `%APPDATA%\Realm\realm-signing-key.json` somewhere private; the player app trusts only this key.
2. Fill in where you will host `servers.json` (an `https://` address you control, for example GitHub Pages or a public gist's **Raw** link), and for Server I its public address (your DNS name or public IP), region and max players.
3. Press **Sign & write servers.json**. The publish folder (default `G:\RealmTest\publish`) now holds `servers.json`, `player-config.json` and `PUBLISH-README.txt`.
4. Upload `servers.json` to the address from item 2. Realm uploads nothing itself.
5. Copy `player-config.json` from the publish folder to `G:\Realm\launcher\player\player-config.json`.
6. Double-click `Build-Realm.bat` again. The note about "no published server list" is gone, and the new `Realm-Setup-1.0.0.exe` carries your key and list.

Publish again before the list expires (30 days by default) and whenever an address changes.

## 8. Send Realm-Setup to your friends

Send `release\1.0.0\Realm-Setup-1.0.0.exe` (and `SHA256SUMS.txt` if they want to check it), for example on your Discord. Do **not** send `Realm-Steward-Setup`: that is the owner app.

Your friend needs their own Steam copy of Reign of Kings. They run the installer (SmartScreen: **More info > Run anyway**), pick a house, and should see your server **Online** with a ping. **Join** asks Steam to start their game and connect. UNVERIFIED: that Easy Anti-Cheat passes the address on; if the game stops at the menu, Realm shows the address and port to type in the direct-connect boxes.

## If something goes wrong

- **Doctor** in Steward (or **Can't join?** in the player app): press **Run checks**. The top line names the problem and the fix. Paste a game popup into its box to have it explained. **Copy report** gives a text you can share; public IPs, Steam IDs, the Steam auth ticket, passwords and your Windows user name are removed.
- [`docs/troubleshooting.md`](docs/troubleshooting.md): every problem seen so far, as symptom, cause and fix.
