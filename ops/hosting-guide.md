# Hosting Realm on a Windows server

This guide takes you from "I have a credit card" to "the realm of Ostreval is online, backed up offsite every night, and Discord hears when it falls silent". It is written for one owner who is comfortable with Remote Desktop and PowerShell.

Tags used here: **[PROVEN]** = proven in this repo's research (`docs/join-and-scale.md`, `docs/server-reference.md`), **[GENERIC]** = standard Windows/Steam/networking practice, **ESTIMATE** = a planning number to replace with your own measurements, **UNVERIFIED** = nobody has checked it yet.

---

## 1. What runs where

| Piece | Where it runs | Notes |
|---|---|---|
| Reign of Kings Dedicated Server (`ROK.exe`), up to 4 instances | the Windows server | Windows only. Unity 5.1 (2015) [PROVEN]. |
| Realm Steward (owner desktop app) | the same Windows server, in a logged-in desktop session | Starts and stops the instances, holds their admin consoles, restarts them on crashes and on a schedule. **It runs the servers as child processes of a desktop app** (see section 4.6). |
| Oxide 2.0.3867 + Realm plugins | inside each instance folder | Installed by Steward's wizard. Server side only. |
| Ops tasks (this folder) | Windows Task Scheduler, as SYSTEM | The nightly offsite backup and the uptime monitor every 2 minutes. |
| Offsite backups | any S3-compatible bucket at a **different provider** | Reached through rclone. |
| Realm Portal (static site), Discord | elsewhere | Host the portal somewhere other than the game server, so the game server's IP is not your website's IP (section 7). |

Players run **their own Steam copy** of Reign of Kings and join by address. Nothing on this server is ever sent to players.

---

## 2. Sizing

### 2.1 What is known

- `maxPlayers` has no clamp in code, and 120 is accepted. The code default is **30**. Whether one RoK server stays playable at 120 is **UNVERIFIED**; only a load test can show it (`docs/join-and-scale.md` section 2). [PROVEN]
- The default `timeBetweenPlayerJoin` of 10 s means filling 120 slots takes about 20 minutes. Lower it for launch nights. [PROVEN]
- Unity servers of this era run gameplay on **one main thread**, so single-core speed matters more than core count. [GENERIC]
- Whether the shipped `ROK.exe` is 32-bit or 64-bit is **UNVERIFIED**. A 32-bit process cannot use more than about 4 GB of RAM whatever the machine has. Check it in Task Manager > Details (add the "Platform" column) on the first boot.
- Bandwidth per player for RoK has never been measured: **UNVERIFIED**.

### 2.2 Planning numbers (ESTIMATE: replace them after the load test in 2.4)

| | 1 server, 120 players | 4 servers, 120 players each (480) |
|---|---|---|
| CPU | 4 **dedicated** vCPU or cores, high clock (3.5 GHz or more boost, a recent generation) | 8 to 12 dedicated cores, high clock. At this size prefer a **dedicated (bare-metal) server** over a VPS. |
| RAM | 16 GB (Windows about 3 GB, the server ESTIMATE 4 to 8 GB, Steward and headroom) | 32 to 48 GB |
| Disk | 100 GB NVMe/SSD: C: for Windows, a separate data volume as **G:** | 250 GB NVMe. Four instance copies, the master, local backups and staging. |
| Network | 1 Gbit/s port. ESTIMATE 50 to 150 kbit/s per player upstream, so about 6 to 18 Mbit/s at 120 | ESTIMATE 25 to 75 Mbit/s sustained. Check the provider's monthly traffic allowance. |
| DDoS | provider with always-on volumetric UDP mitigation (section 7) | same, plus game-aware filtering if offered |
| Windows | Windows Server 2022 Standard (Desktop Experience) | same |

Other options at 4 servers:
- **Two machines with two instances each** limit the damage when one machine fails or is attacked. The ops scripts run on each machine with its own config and its own bucket folder.
- Do not oversubscribe: "shared" or "burstable" vCPUs (credit-based instances) will stutter at 120 players.

### 2.3 Disk layout

| Drive | Holds |
|---|---|
| C: | Windows, page file, Realm Steward (installs per user) |
| **G:** | `G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server` (master), `G:\RealmTest\server` and `G:\Realm\s2..s4` (instances), `G:\RealmOps` (SteamCMD, rclone, config, logs, local backups) |

Why **G:**: every Realm script and Realm Steward default to G:. `server\New-TestServer.ps1` and Steward refuse to put an instance on C:. Most providers let you attach a second block-storage volume; give it the letter G: in Disk Management. If your plan has only one disk, ask the provider for a second volume or partition. Changing the defaults everywhere is not worth it.

### 2.4 Measure before you trust the numbers

During a test night, run this on the server and keep the CSV. It changes nothing.

```powershell
$c = '\Process(ROK*)\% Processor Time','\Process(ROK*)\Working Set - Private',
     '\Processor Information(_Total)\% Processor Performance','\Network Interface(*)\Bytes Sent/sec',
     '\Network Interface(*)\Bytes Received/sec','\Memory\Available MBytes'
Get-Counter -Counter $c -SampleInterval 10 -MaxSamples 1080 |   # 3 hours
  Export-Counter -Path G:\RealmOps\logs\loadtest.blg -FileFormat BLG
```

Open the `.blg` file in Performance Monitor. The quantity that decides how many players fit is **one core at 100 %** (`% Processor Time` of `ROK` close to 100 divided by the number of cores, while the server ticks slowly), not total CPU.

---

## 3. Choosing a provider

Requirements, in order:

1. **Windows Server available** with a license included or rentable. Licensing usually costs extra each month; compare totals, not base prices.
2. **Dedicated or guaranteed CPU** with high clock (2.2).
3. **DDoS mitigation that covers UDP**, always on, with no surprise "null routing" of your IP for hours (section 7).
4. **A second volume** (2.3), and snapshots or images if offered.
5. **A data centre near your players.** Round-trip time matters more than anything for a melee game.
6. Monthly traffic allowance that fits 2.2.

Kinds of provider to compare. Check current offers yourself; this list is **not** an endorsement, and plan names and prices change:

| Kind | Examples | Watch for |
|---|---|---|
| Game-oriented dedicated servers | OVHcloud (Game range), providers on Path.net or similar filtered networks | Best UDP DDoS handling. Windows license is often an add-on. |
| General VPS or cloud with Windows images | Vultr, AWS (EC2/Lightsail), Microsoft Azure, Google Cloud, Kamatera | Easy Windows images. Check that vCPUs are not shared or burstable, the price of egress traffic (cloud egress can dominate the bill), and the level of DDoS protection on the base tier. |
| Budget dedicated | Hetzner (dedicated), Contabo | Good price per core. Windows licensing and DDoS level vary; read the terms on game servers and on mitigation behaviour. |

**Do not** host on your home connection for the public launch: your home IP becomes the target of every DDoS.

---

## 4. Windows Server setup

All steps are [GENERIC] Windows administration unless tagged.

### 4.1 First login

1. Connect with Remote Desktop as the provider's administrator account.
2. Run Windows Update until nothing is left and reboot.
3. Create your own administrator account with a long unique password. Give the built-in `Administrator` a random 30+ character password stored in your password manager.
4. **Time zone: UTC** (`Set-TimeZone -Id UTC`). RealmEvents schedules in UTC, backup file names are UTC, and Task Scheduler and Steward use the machine's clock. With the server on UTC they all agree.
5. Power plan: High performance (`powercfg /setactive SCHEME_MIN`).
6. Disk: Disk Management > initialize the data volume (GPT) > New Simple Volume > NTFS > letter **G:**.

### 4.2 Lock down Remote Desktop

RDP open to the internet is the most common way game servers get taken over.

- In the **provider's firewall** (cloud firewall or security group), allow TCP 3389 **only from your own IP**. Better still, reach the server only through a VPN (for example WireGuard or Tailscale) and close 3389 to the internet entirely.
- Keep Network Level Authentication on (the default).
- Account lockout: `secpol.msc` > Account Policies > Account Lockout Policy > 10 attempts, 15 minutes.

### 4.3 Updates without surprise reboots

An automatic Windows Update reboot during a siege kills the server and Steward. Set Windows Update to download but **install on your schedule**: Group Policy `gpedit.msc` > Computer Configuration > Administrative Templates > Windows Components > Windows Update > Configure Automatic Updates = **3 (download and notify)**. Install the updates monthly, after the nightly backup, and announce it in Discord.

### 4.4 Software

| What | How | Notes |
|---|---|---|
| Realm Steward | `Realm-Steward-Setup-<version>.exe` built from `launcher/` (`npm run dist:steward`) | Owner app. See the repo README. |
| SteamCMD + dedicated server | `ops\scripts\Install-RealmServer.ps1` (section 6) | Anonymous login, app 381690 [PROVEN]. |
| rclone | download `rclone-<ver>-windows-amd64.zip` from rclone.org or its GitHub releases; check it against the release's `SHA256SUMS` with `Get-FileHash`; unzip `rclone.exe` to `G:\RealmOps\rclone\` | Only for offsite backups. |
| Visual C++ / DirectX runtimes | only if `ROK.exe` refuses to start and names a missing DLL | **UNVERIFIED** which, if any, a headless (`-batchmode -nographics`) server needs. Install from Microsoft only. |

### 4.5 Windows Defender

Keep it on. If the server stutters when it saves, add a **folder exclusion for the instance `Saves` folders only** (`Add-MpPreference -ExclusionPath 'G:\RealmTest\server\Saves'` and so on). Never exclude whole drives.

### 4.6 Keeping the servers running after a reboot

Realm Steward runs the server processes as its own children in **your desktop session**. On a server with nobody looking at it, three rules apply:

- **Disconnect** from RDP (close the window). **Never "Sign out"**, because signing out closes Steward, and with the live console on, closing Steward makes its servers save and stop (README, "Live admin console").
- After a reboot, someone has to log in again before Steward starts. To make that automatic, turn on Windows automatic logon for a **dedicated, non-administrator local user** that runs Steward, using Sysinternals **Autologon** (it stores the password LSA-encrypted, not in plain text). Then put a Steward shortcut in that user's Startup folder (`shell:startup`).
- Whether Steward then starts the servers by itself is a Steward setting: **UNVERIFIED**. Check it on your first test reboot. If it does not, the uptime monitor will tell you within minutes.

The ops tasks run as SYSTEM and do not depend on anyone being logged in.

---

## 5. Ports and firewall

Each instance needs these ports [PROVEN, `docs/join-and-scale.md` section 4]:

| Instance | Game (UDP) `portNumber` | Ping (TCP) `pingPort` | Steam query / A2S (UDP) `steamAuthPort` | Admin console (TCP, local only) |
|---|---|---|---|---|
| 1 `G:\RealmTest\server` | 7350 | 7350 | 27015 | 11000 |
| 2 `G:\Realm\s2` | 7351 | 7351 | 27016 | 11001 |
| 3 `G:\Realm\s3` | 7352 | 7352 | 27017 | 11002 |
| 4 `G:\Realm\s4` | 7353 | 7353 | 27018 | 11003 |

- The **admin console (11000 to 11003) has no authentication**. Anyone who reaches it can run admin commands and shut the server down. It must **never** be reachable from outside, so block it in the provider's firewall **and** in Windows.
- RCON in `ConsoleSettings.cfg` stays **off** (`enableRCon = 'False'`).
- Steam's own client port (UDP 8766) is outbound only; it needs no inbound rule.

In an elevated PowerShell (these only change Windows Firewall; Steward's **Public** screen can do the same with its **Add firewall rules** button):

```powershell
New-NetFirewallRule -DisplayName "Realm game (UDP)"  -Direction Inbound -Protocol UDP -LocalPort 7350-7353   -Action Allow
New-NetFirewallRule -DisplayName "Realm ping (TCP)"  -Direction Inbound -Protocol TCP -LocalPort 7350-7353   -Action Allow
New-NetFirewallRule -DisplayName "Realm query (UDP)" -Direction Inbound -Protocol UDP -LocalPort 27015-27018 -Action Allow
New-NetFirewallRule -DisplayName "Realm admin console BLOCK" -Direction Inbound -Protocol TCP -LocalPort 11000-11003 -Action Block
```

In the **provider's firewall**, allow only: UDP 7350-7353, TCP 7350-7353, UDP 27015-27018, and RDP (3389) from your IP or VPN. Deny everything else inbound. A VPS has no home router, so there are no router port forwards; the provider's firewall plays that role.

Give players a **DNS name** (for example `play.<your-domain>` as an A record with TTL 300) rather than the raw IP. The game resolves names [PROVEN, `docs/join-and-scale.md` 1.4], and you can move to a new IP during an attack or a rebuild without new server lists.

---

## 6. Installing the server

1. Create the ops folder and config:
   ```powershell
   New-Item -ItemType Directory G:\RealmOps, G:\RealmOps\secure, G:\RealmOps\downloads | Out-Null
   Copy-Item <repo>\ops\config\realm-ops.example.json G:\RealmOps\realm-ops.json
   ```
2. Install SteamCMD and the server (anonymous; it downloads Valve's `steamcmd.zip` and then app 381690):
   ```powershell
   cd <repo>\ops\scripts
   .\Install-RealmServer.ps1 -ConfigPath G:\RealmOps\realm-ops.json -WhatIf
   .\Install-RealmServer.ps1 -ConfigPath G:\RealmOps\realm-ops.json
   ```
   It retries the download up to 3 times, records the build in `G:\RealmOps\state\master.json`, and leaves the files in the master folder. On Windows it checks the Authenticode signature of `steamcmd.exe`. If the file is unsigned it prints a warning, because whether Valve signs it is **UNVERIFIED**; if the signature is invalid it stops.
3. Download Oxide `Oxide.ReignOfKings.zip` 2.0.3867 from its GitHub release to `G:\RealmOps\downloads\Oxide.ReignOfKings-2.0.3867.zip` and check it:
   ```powershell
   (Get-FileHash G:\RealmOps\downloads\Oxide.ReignOfKings-2.0.3867.zip).Hash -eq '6C35C623FA9EE412945F61A32E8196091B40D56BFE9F8376D3D8E6243B72C6C8'
   ```
4. Install Realm Steward and run its setup wizard. It finds the master at the default path, copies it to `G:\RealmTest\server`, starts it once, installs Oxide in the copy and deploys the plugins. Use **Add server** for instances 2 to 4 (ports as in section 5). Set **max players** to 120 per server and pick a daily restart time.
5. Put each instance in `servers` in `realm-ops.json` with its `root`, `gamePort` and `queryPort`. For the monitor, `host` can stay `127.0.0.1` (it checks from the same machine). To check from **outside**, run a second copy of the monitor on another machine with `host` set to your DNS name; this catches firewall and provider problems the local check cannot see.

---

## 7. DDoS and abuse

Game servers do get attacked, usually right after a dispute in chat or a rival's war going badly.

- **Choose mitigation at the provider** (section 3). Nothing on the server itself can absorb a volumetric UDP flood. Windows Firewall cannot rate-limit.
- Web CDNs and HTTP proxies (Cloudflare's free plan and similar) **do not protect a UDP game port**. Proxies that tunnel UDP game traffic exist (GRE or "game proxy" products), but they are extra cost and complexity. Consider them only if attacks keep coming.
- **Keep the game server's IP separate from everything else.** Host the Realm Portal and any website on static hosting elsewhere. Use a different machine (or none) for anything that posts your IP publicly. The DNS name `play.<domain>` points at the game server only.
- **A2S reflection:** the server uses Steamworks SDK 1.34 from 2015 [PROVEN]. Newer Steam libraries answer A2S_INFO with a challenge first, which stops spoofed-source amplification. Whether this old library does is **UNVERIFIED** (the monitor handles both cases). If your provider reports your query ports being used for reflection, restrict UDP 27015-27018 to known ranges or close them. The Realm player app then shows the servers as offline in its live status, but joining by address still works.
- **Never** expose 11000-11003 or RDP (section 4.2, 5).
- When an attack happens, follow `disaster-recovery.md` scenario F.

---

## 8. Offsite backups and the uptime monitor

### 8.1 The bucket

Use a bucket at a **different company** from your server host, so one account problem cannot take both. Any S3-compatible service works with rclone (for example Backblaze B2, Cloudflare R2, Wasabi, AWS S3, or a MinIO you run elsewhere).

1. Create a private bucket. Turn on **object versioning**, and object lock or retention if offered. Then a stolen key or a mistake cannot destroy old backups.
2. Create an access key **limited to that bucket**. Ideally it can read and write but not delete. In that case set `"remoteKeepDays": 0` and let a **lifecycle rule** in the bucket expire objects after 30 to 60 days.
3. Create the rclone remote, storing the config in `G:\RealmOps\secure\rclone.conf`. Type the keys at the prompts or as below; they go into that file only, never into the repo:
   ```powershell
   G:\RealmOps\rclone\rclone.exe config create realm-offsite s3 provider=Other `
       access_key_id=<KEY-ID> secret_access_key=<SECRET> endpoint=<https://s3.example-region.provider.tld> `
       --config G:\RealmOps\secure\rclone.conf
   ```
   (Use `provider=Cloudflare`, `provider=Wasabi`, `provider=AWS` and so on when it applies; `rclone config` asks interactively if you prefer.) Optional: wrap it in an rclone `crypt` remote so the provider only sees encrypted files. Keep the crypt passwords in your password manager too: **without them the backups cannot be restored**.
4. In `realm-ops.json` set `backup.rcloneExe`, `backup.rcloneConfig` and `backup.remote` (for example `realm-offsite:my-realm-backups/realm`).

What a backup holds, per server: `Saves\`, `oxide\data\` (Chronicle, houses, crown, seasons and every other plugin's data), `oxide\config\`, `oxide\plugins\`, `Configuration\`, and your `Mods\*.cfg` overrides (not the game's `*.defaults.cfg`). Each zip has a manifest with the SHA-256 of every file and a `.sha256` sidecar. It is checked right after it is made, and its size is checked at the bucket after upload (`verifyByDownload: true` also downloads it back and compares hashes, at the cost of egress).

**When to schedule it:** the backup works while servers run. It copies with sharing and retries if a save happens mid-copy (manifest `consistent: true/false`). The cleanest copy, though, is taken **10 minutes after Steward's daily restart**. Set `-BackupTime` to that.

### 8.2 Discord

1. In your staff Discord channel: Edit Channel > Integrations > Webhooks > New Webhook > Copy Webhook URL.
2. Paste it into `monitor.discordWebhookUrl` in `realm-ops.json`. Optional: `monitor.mention` = `<@&ROLE_ID>` to ping a staff role on "down" (the role must be mentionable).
3. Test: `.\Test-RealmOpsConfig.ps1 -ConfigPath G:\RealmOps\realm-ops.json -SendTest`.

Treat the webhook URL like a password: anyone who has it can post in that channel. If it leaks, delete the webhook in Discord and make a new one.

### 8.3 Schedule both

```powershell
.\Register-RealmTasks.ps1 -ConfigPath G:\RealmOps\realm-ops.json -BackupTime 04:20 -ProtectConfig
```

This creates `\Realm\Realm Nightly Backup` (daily, retried twice if it fails, up to 3 hours) and `\Realm\Realm Uptime Monitor` (every 2 minutes and at boot), both as SYSTEM. `-ProtectConfig` restricts `realm-ops.json` and `rclone.conf` to SYSTEM and Administrators. After that, edit them from an elevated editor.

What the monitor reports:

| Verdict | Means | Discord |
|---|---|---|
| up | A2S answers on the query port | "back up" once after a problem |
| degraded | the process or ping port is alive, but A2S is silent: the server's Steam login failed, or the query port is blocked | "DEGRADED" after 2 checks (about 4 minutes) |
| down | nothing answers | "DOWN" after 2 checks, a reminder every 60 minutes |
| stale backup | the newest offsite backup is older than 26 h | "No fresh backup", once a day until fixed |

A2S always reports the name "Another ROK Server" and 0 max players [PROVEN], so the monitor uses names from the config and shows A2S player counts as approximate.

Logs: `G:\RealmOps\logs\backup-YYYYMMDD.log`, `monitor-YYYYMMDD.log` and so on, kept for 30 days. State: `G:\RealmOps\state\*.json`.

---

## 9. Updating

```powershell
.\Update-RealmServer.ps1 -ConfigPath G:\RealmOps\realm-ops.json            # backup, then SteamCMD on the master
.\Update-RealmServer.ps1 -ConfigPath G:\RealmOps\realm-ops.json -ApplyToInstances   # also refresh instances + Oxide
```

Reign of Kings has not had a server build in years, so expect "already up to date". If a new build ever appears:
1. Copy the master folder somewhere first (`robocopy <master> G:\RealmOps\master-previous /E`), so you can go back.
2. Stop all servers in Steward, then run with `-ApplyToInstances`.
3. Start **one** server and watch for the ready line and Oxide loading. Oxide 2.0.3867 was built for the current build; whether it works with a new one is **UNVERIFIED**. If it fails, follow `disaster-recovery.md` scenario D.

Plugin updates are not part of this. Use Steward (or `server\Deploy-Plugins.ps1`) for those.

---

## 10. Launch-day checklist

- [ ] `Test-RealmOpsConfig.ps1` shows no FAIL, and the `-SendTest` message arrived in Discord.
- [ ] One manual backup completed. A drill restore from the bucket passed (`restore-drill.md`, steps 1 to 4).
- [ ] From a **different network**, a player joined each server by DNS name.
- [ ] `Test-NetConnection <public ip> -Port 11000` from outside **fails** (the admin console is closed).
- [ ] RDP is reachable only from your IP or VPN.
- [ ] A test reboot was done: Steward came back after automatic logon, the servers started, and the monitor reported "back up".
- [ ] `timeBetweenPlayerJoin` was lowered for the launch window. Staff hold `codehatch.login.ignore.playerqueue`.
- [ ] The load-test counters (2.4) were recorded during the first busy evening.
