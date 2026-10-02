# Realm local smoke test

This is a three-stage check on the owner's Windows PC. Each stage runs only after the previous one passes:

- **A. Vanilla**: the test copy of the dedicated server starts and you can join it on `127.0.0.1`.
- **B. Oxide**: Oxide 2.0.3867 loads in the test copy.
- **C. Plugins**: the Realm plugins compile and load, they write their data files, and the Chronicle web service reads those files.
- **D. Atmosphere preset** (optional, needs only A): the "grim but readable" Mods preset installs, applies and reverts.

Ground rules for every stage:

- Everything runs against the **test copy** at `G:\RealmTest\server`. The Steam folder `G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server` is only read once, by `New-TestServer.ps1`.
- **Do not change the firewall or the router.** If Windows Defender Firewall asks whether to allow `Server.exe` or `ROK.exe`, click **Cancel** (do not allow). A game client on the same PC connects through loopback, and the firewall does not filter loopback.
- Run each script with `-WhatIf` first. It prints what it would do and changes nothing. Then run it again without `-WhatIf` and answer the prompt.
- The scripts are in the repo's `server\` folder. Open **Windows PowerShell** in that folder. If script execution is blocked, allow it for this window only with `Set-ExecutionPolicy -Scope Process Bypass`.

Tags used in this doc (see `docs/server-reference.md` and `docs/oxide-rok-api.md`): **[V]** means verified from a primary source. **[S]** means secondary (search snippets of official guides). **[U]** means unverified, and this test is what confirms it.

---

## A. Vanilla local smoke test

### Steps

1. **Make the test copy.**
   ```powershell
   .\New-TestServer.ps1 -WhatIf
   .\New-TestServer.ps1
   ```
   The script refuses a target on C:, a target that overlaps the Steam folder, and a drive that does not have room for the copy plus 2 GB.
2. **Take a clean baseline backup.** It is optional, but it makes later resets cheap. It only works once a `Saves` folder exists, so you can also do this after step 4.
3. **First start.**
   ```powershell
   .\Start-LocalServer.ps1
   ```
   On the first run `Configuration\ServerSettings.cfg` does not exist yet, so the script warns that this run uses the default `bindIP = '0.0.0.0'`. That is fine: the firewall is untouched and you declined its prompt, so nothing outside the PC can connect.
   - If `Server.exe` asks for an app id, type `381690` [S].
   - Wait for a console line that starts with `Initialize engine version:` [V]. It may be followed by world generation, which can take several minutes on first run.
4. **Stop the server.** Type `quit` in its console [V for AMP; U for the game itself]. If `quit` does nothing, close the window.
5. **Second start, now local-only.**
   ```powershell
   .\Start-LocalServer.ps1 -WhatIf     # shows the cfg changes: bindIP 127.0.0.1, isPrivate True, rConPort 27016, enableRCon False
   .\Start-LocalServer.ps1
   ```
   The old cfg files are saved in `G:\RealmTest\server\_realm-backups\config`.
6. **Join.** Start Reign of Kings from Steam as usual. Use direct connect, the "Local Host" field in the server UI [S], with IP `127.0.0.1` and port `7350`. Spawn, walk around, and place one block or build one campfire.
7. **Check that the world saves.** Type `quit`, start the server again with `.\Start-LocalServer.ps1`, rejoin, and confirm your block is still there.
8. **Back up and restore.**
   ```powershell
   .\Backup-Saves.ps1
   .\Restore-Saves.ps1 -ZipPath G:\RealmTest\backups\<the zip just made>.zip -WhatIf
   ```
   You only need to run the restore for real if you want to test it: stop the server, run it without `-WhatIf`, start the server, and rejoin.
9. **Start the Mods folder.** You have now started the server at least twice, so `G:\RealmTest\server\Mods` should exist [S]. Run `.\Export-ModKeys.ps1` and keep `docs\mods-keys.txt` (see `mods/README.md`).

### Pass/fail: vanilla

| # | Check | Pass | Fail → do this |
|---|---|---|---|
| A1 | `G:\RealmTest\server` exists, contains `.realm-test-copy`, and the Steam folder's modified time did not change | ☐ | Re-run with `-WhatIf` and read the refusal message |
| A2 | Which exe exists: `Server.exe`, `ROK.exe`, or both? Which `*_Data\Managed` folder exists (`ROK_Data` or `Server_Data`)? **Write both down.** They decide part B [U] | ☐ | — |
| A3 | The console shows `Initialize engine version:` | ☐ | Try `.\Start-LocalServer.ps1 -Exe ROK` (runs `ROK.exe -batchmode -nographics -silentcrash` [V]) |
| A4 | `Configuration\ServerSettings.cfg` was created. Diff its keys against section 4.1 of `docs/server-reference.md` and note any extra keys, such as an autosave interval or `gameMode` | ☐ | — |
| A5 | After the second start, `bindIP = '127.0.0.1'` and `isPrivate = 'True'` | ☐ | Check the `Write-Warning` output |
| A6 | The client joins at `127.0.0.1:7350`. The server log shows `Authentication verified for <name> (<steamid>).` [V] | ☐ | Bound to loopback, Steam auth may fail [U]. Retry with `.\Start-LocalServer.ps1 -BindIP 0.0.0.0` (still no firewall change), and then with `-SetSteamAppId` |
| A7 | The block is still there after a restart | ☐ | Check `allowSaving = 'True'` and `saveLocation = 'Saves/'` |
| A8 | `Backup-Saves.ps1` produced a zip containing `Saves/...` and `realm-backup.json` | ☐ | — |
| A9 | `Mods\` exists, and `docs\mods-keys.txt` lists its `*.defaults.cfg` files | ☐ | Start and stop the server one more time |
| A10 | No firewall rule was added: `Get-NetFirewallRule \| Where-Object DisplayName -match 'Server\|ROK\|Reign'` returns nothing new | ☐ | Delete the rule in Windows Defender Firewall → Advanced settings, and decline the prompt next time |

**Stop here if A3 or A6 fails.** Oxide cannot be tested until vanilla works.

---

## B. Oxide test

### Steps

1. Download `Oxide.ReignOfKings.zip` yourself from the **2.0.3867** release:
   `https://github.com/OxideMod/Oxide.ReignOfKings/releases/download/2.0.3867/Oxide.ReignOfKings.zip`
   The expected size is 11,939,101 bytes and the SHA-256 is `6c35c623fa9ee412945f61a32e8196091b40d56bfe9f8376d3d8e6243b72c6c8` [V].
2. Stop the server and back up the saves (`.\Backup-Saves.ps1 -Label pre-oxide`).
3. Install into the test copy:
   ```powershell
   .\Install-Oxide.ps1 -ZipPath "$env:USERPROFILE\Downloads\Oxide.ReignOfKings.zip" -WhatIf
   .\Install-Oxide.ps1 -ZipPath "$env:USERPROFILE\Downloads\Oxide.ReignOfKings.zip"
   ```
   - Every entry in the zip is under `ROK_Data/Managed/` [V]. It overwrites `Assembly-CSharp.dll`, the System/Mono DLLs it ships and `x86\mono-2.0.dll`. Every original is backed up to `_realm-backups\oxide-<time>\` first.
   - If A2 found `Server_Data` and no `ROK_Data`, add `-DataFolder Server_Data` [U].
4. Start the server with `.\Start-LocalServer.ps1`. If the Oxide console does not show, stop the server and try `-Exe ROK` [U: which exe Oxide's console attaches to].
5. In the server console, type `oxide.version` (or `o.version`), then `oxide.plugins` [V].
6. Join from the client again, as in A6.

### Pass/fail: Oxide

| # | Check | Pass | Fail → do this |
|---|---|---|---|
| B1 | `Install-Oxide.ps1` reported `SHA-256 OK` | ☐ | Download the file again. Never use `-ExpectedSha256` to get around this check |
| B2 | The server boots, and the console or status line shows `Oxide.ReignOfKings 2.0.3867` [V] | ☐ | `.\Install-Oxide.ps1 -Rollback`, then re-run A3. Record the error. The likely cause is a game/patched-DLL build mismatch [U] |
| B3 | `oxide.version` answers | ☐ | Same as B2 |
| B4 | `G:\RealmTest\server\oxide\` exists with `plugins`, `config`, `data`, `lang` and `logs` [V]. **Note** whether Oxide instead created `Saves\oxide` [S] | ☐ | Check that the working directory is the server root (Start-LocalServer sets it) |
| B5 | The client still joins and the earlier world is intact | ☐ | If the client gets a version or hash mismatch: `-Rollback`, then reinstall with `-WriteOriginalHashCopy` [U] and retest. Record which one worked |
| B6 | `oxide\logs\` has a log with no repeated errors. Note any outbound web request, e.g. Oxide looking up the public IP via `api.ipify.org` [V], or downloading its compiler [U] | ☐ | Record it. If the plugin compiler cannot be fetched offline, part C fails with compile errors |
| B7 | `.\Install-Oxide.ps1 -Rollback -WhatIf` lists the files it would restore and delete | ☐ | — |

---

## C. Plugin test

### Steps

1. With the server running under Oxide, deploy the plugins:
   ```powershell
   .\Deploy-Plugins.ps1 -WhatIf
   .\Deploy-Plugins.ps1
   ```
   This copies `RealmChronicle.cs`, `RealmHouses.cs`, `CrownAndConsequences.cs` and `RealmContracts.cs` into `oxide\plugins`. Oxide hot-reloads them, so there is no restart.
2. Watch the server console and `oxide\logs` for `Loaded plugin ...` or for compile errors. A compile error gives the file and line. Fix it in the repo and run `Deploy-Plugins.ps1` again.
3. Give yourself admin in the server console:
   ```
   oxide.grant user <yourname> realmhouses.admin
   oxide.grant user <yourname> crownandconsequences.admin
   oxide.grant user <yourname> realmcontracts.admin
   ```
4. In game chat, run:
   - `/chronicle`, which should report that the chronicle is empty or list its events.
   - `/house found Ashveil <sigil text>`, which founds a house and should log a `house_founded` event.
   - `/house list` and `/house info`.
   - `/crown`, `/claim list` and `/ransom list`, which should show their status or usage text and must not throw errors.
5. Check the data files:
   - `G:\RealmTest\server\oxide\data\RealmChronicle.json` should be a JSON array whose events have `id`, `ts`, `type`, `title`, `detail` and `actors`.
   - `G:\RealmTest\server\oxide\data\RealmState.json` should have `king`, `houses`, `online`, `maxPlayers` and `updated`.
6. Start the Chronicle web service against the **test copy**. Its default data directory is `G:\RealmTest\server\oxide\data`; passing `--data` explicitly makes the path visible (use `...\Saves\oxide\data` if Oxide created its folder there):
   ```powershell
   node ..\chronicle\server.js --data "G:\RealmTest\server\oxide\data"
   ```
   Open `http://127.0.0.1:8787/api/state`, `/api/events?since=0`, `/overlay` and `/realm`.
7. Run `oxide.reload RealmChronicle` and confirm the events are still there.
8. Stop the server and run `.\Backup-Saves.ps1 -Label plugins-ok`.

### Pass/fail: plugins

| # | Check | Pass | Fail → do this |
|---|---|---|---|
| C1 | All four plugins report as loaded. `oxide.plugins` lists RealmChronicle, RealmHouses, CrownAndConsequences and RealmContracts | ☐ | Fix the compile error in the repo and redeploy |
| C2 | `/chronicle` answers in chat | ☐ | Check that the `[ChatCommand]` methods are private (`docs/oxide-rok-api.md` section 7) |
| C3 | `/house found ...` succeeds, and a `house_founded` event appears in `RealmChronicle.json` | ☐ | Check that RealmHouses calls RealmChronicle (`[PluginReference]` / `Call("Log", ...)`) |
| C4 | `RealmState.json` matches the shared contract, and `actors` holds only public names (no positions, no inventory) | ☐ | — |
| C5 | `/api/state` and `/api/events?since=0` return JSON. `/overlay` shows the event | ☐ | Check the `--data` path |
| C6 | The events survive `oxide.reload RealmChronicle` and a full server restart | ☐ | — |
| C7 | The vanilla behaviour from A6/A7 still works with the plugins loaded | ☐ | Unload plugins one at a time with `oxide.unload <Name>` to find the cause |
| C8 | Ransom auto-release (needs two clients or a helper): tie a player with rope; `/ransom list` shows them; set `RansomMaxMinutes` to 1 in `oxide/config/CrownAndConsequences.json` and reload; within about a minute after expiry the captive is untied without anyone acting, and a `released` event appears | ☐ | The release uses `PlayerCaptureManager.Captured` / `Release()` (UNVERIFIED semantics). If the captive stays bound, admins get an alert; report what `Captured` showed and whether `/ransom free` worked |
| C9 | `/crown` reports the king after a throne capture, and the overlay shows the same king (proves `plugin.Call` reaches `GetKingName`) | ☐ | Cross-plugin API methods must be non-public; see `docs/oxide-rok-api.md` section 1 |
| C10 | Item names: `/contract items wood` lists real item names. Write down the exact names for wood and stone | ☐ | If nothing is found, `InvBlueprints.Instance` may not be ready yet. Retry after the world has loaded |
| C11 | Escrow without duplication (one client): note your Stone count. Run `/contract post delivery 1 "Wood" 5 "Stone"` with the exact names from C10. Stone **drops by 5 at once** in the inventory window. Then `/contract cancel <id>`: Stone returns to the **exact** original count. Do it twice; the count never ends higher than it started | ☐ | If the server took the items but the client view did not change (UNVERIFIED sync, `docs/oxide-rok-api.md` 3.9), relog and recount. If the counts are wrong, set `"ItemEscrow": false` in `oxide/config/RealmContracts.json` (honour mode) and report it |
| C12 | Royal Stores: as king, with your house crown-sworn, `/decree stores` gives each online member of a crown-sworn house 25 of the configured item, then again every 15 min while in force. The `decree ... ends` chronicle line reports the total given | ☐ | `cannot be issued: the item ... is not known` means `Item` in `oxide/config/CrownAndConsequences.json` must be set to a name from C10. No authority is spent on that refusal |
| C13 | Bounty (needs three players A, B, C, with C not in B's house): the king runs `/contract outlaw B`; A posts `/contract post bounty B 5 "Stone"`; C kills B. The reward reaches C's inventory, and a `contract_fulfilled` event appears | ☐ | If nothing happens, check the server log for `Death handling failed`, and whether `KillingDamage.DamageSource.Owner` was null for that weapon (melee vs. ranged) |
| C14 | Escrow survives a reload: post a delivery with a Stone reward, run `oxide.reload RealmContracts`, then `/contract list` still shows it and `/contract cancel <id>` returns the Stone. Repeat with a full server restart in between | ☐ | Check `oxide/data/RealmContracts.json`. If the plugin refuses to load with `Could not read ...RealmContracts.json`, it is protecting escrow records: fix or move the file, never delete it while contracts are open |
| C15 | Paid out while away: post a delivery, have a second client deliver it while the poster is **offline**. When the poster rejoins, the delivered goods arrive within about 30 s (`You receive ...`). With a full inventory the message is `Your packs are full`, and `/contract collect` delivers after you make room | ☐ | Report the inventory counts before and after; the ledger only shrinks by what was really added |
| C16 | Anti-grief (king): `/contract outlaw B` works once; a second `/contract outlaw <other>` within 10 min is refused (`OutlawProclaimCooldownMinutes`). `/contract pardon B` then `/contract outlaw B` is refused for 24 h (`OutlawRepeatCooldownHours`). A bounty on B is refunded to its poster on pardon, and within about 30 s after B's outlawry expires (set `OutlawHours` to 1 to test) | ☐ | — |
| C17 | Honour mode: set `"ItemEscrow": false`, reload, post a delivery. A second player runs `/contract deliver <id>`; the poster can `/contract confirm <id>` (chronicle shows the honour debt) or `/contract cancel <id>` to refuse the claim. No item counts change in either case | ☐ | — |
| C18 | Mercenary (needs a declared claim and three players): the claimant house leader posts `/contract post merc 5 "Stone"`; a player of another house runs `/contract accept <id>`; during the window that player kills a member of a crown-sworn house. After the window the reward arrives. Kills of the mercenary's own housemates, and a second kill of the same victim, do not count (`/contract info <id>` shows the kill count) | ☐ | If no kill is counted, see C13 (killer attribution) |
| C19 | Chronicle flood guard: post and cancel bounties repeatedly (as admin, no post cooldown). After `ChronicleMaxPerHour` (default 20) contract lines in an hour, further contract lines appear only in the server log, not in `RealmChronicle.json` | ☐ | — |

Testing alone covers only part of the plugins. Oaths, treaties, ransom, throne capture, bounties and mercenary hire need more players and are beyond the solo part of this smoke test.

---

## D. Atmosphere preset (Mods)

Run this only after A passes; it does not need Oxide. Stop the server first: the script refuses to run while it is running.

1. From `mods\presets\grim-but-readable\`:
   ```powershell
   .\Apply-Preset.ps1 -WhatIf
   ```
   For each preset line it prints the `Mods\<Name>.cfg` it would go to, the server's own default from `Mods\*.defaults.cfg`, and any key the server does not list (skipped, never guessed).
2. Run `.\Apply-Preset.ps1`, start the server, join, and look at fog, sky and night.
3. Revert with `.\Apply-Preset.ps1 -Revert`, restart, and check that the defaults are back.

| # | Check | Pass | Fail → do this |
|---|---|---|---|
| D1 | `-WhatIf` finds a target file for the `Atmosphere.*`, `Weather.*` keys. **Write down the file name** (UNVERIFIED: the name lives in scene data, not the DLL) | ☐ | If every key is skipped, the dedicated server does not list them (UNVERIFIED for a headless server); the preset cannot apply there |
| D2 | Defaults printed by `-WhatIf` match the assumed white / `1` values, or the mismatches are written down | ☐ | Adjust the preset values relative to the real defaults |
| D3 | After a restart the preset lines are still in `<Name>.cfg` and are **not** turned into `#` comments | ☐ | A `#`-commented line means the server did not recognise the key; report it |
| D4 | In game: fog is visibly denser and cooler, and nights stay readable (you can see your own hands and nearby blocks without a torch at midnight) | ☐ | Lower `Atmosphere.FogDensity` or brighten `MoonColor` |
| D5 | The decimals in the cfg still use a dot after the server rewrites `<Name>.defaults.cfg` (locale risk on a comma-decimal Windows) | ☐ | Set the Windows format to English (United States) for the server account and retest |
| D6 | `-Revert` restores the original files exactly | ☐ | Restore from the `.realm-backup` files by hand |

---

## Resetting

- To roll the world back to a known point, stop the server and run `.\Restore-Saves.ps1 -ZipPath <zip>`. The current state is backed up and moved aside first.
- To remove Oxide, run `.\Install-Oxide.ps1 -Rollback`.
- To get a fresh copy of the game files, run `.\Install-Oxide.ps1 -Rollback` and then `.\New-TestServer.ps1 -Refresh`. The refresh keeps saves and configuration, but it overwrites game files. After a Steam update to the dedicated server, refresh and then reinstall Oxide [V: updates overwrite the patched DLL].
