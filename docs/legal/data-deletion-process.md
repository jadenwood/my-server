# Data access and deletion requests

How a player asks to see or delete the personal data Realm holds about them, and how staff carry it out. What Realm holds is described in [`privacy-notice.md`](privacy-notice.md) §1.

> **Not legal advice.** The 30-day answer time and the exceptions below follow common practice under laws like the GDPR. Whether they meet the law that applies to the Owner has not been checked by a lawyer.

---

## 1. For players

**Ask** by writing to [privacy contact email], or in the Discord `#privacy` channel (staff move it to a private thread at once). Say:

```
I am asking to: [see / delete / correct] my Realm data
In-game name(s) I have used:
SteamID64 (the 17-digit number in your Steam profile link):
Discord account (only if you want Discord-bot data covered):
Anything specific (for example "remove my name from the Chronicle"):
```

**We check it is really you.** We will give you a short one-time code and ask you to do **one** of these:
- put the code in your **Steam profile summary** for a day (staff check the public profile, then you can remove it), or
- join the server and send the code with `/warden report <a staff member's name> <code>`, which records your SteamID64 with it.

For Discord-bot data, sending the request from that Discord account is enough. Staff never ask for passwords.

**You get an answer within 30 days**: either the data (for "see"), or confirmation of what was deleted and what was kept and why (for "delete").

**Before asking for deletion,** you can do some of it yourself straight away:
- `/stats optout` removes your statistics key from every kept day file.
- `/house leave` takes you out of your house. (If you lead it, an officer takes over.)
- `/realm forswear` removes your Discord house role.
- Private letters delete themselves after 7 days.

**What deletion means for the game.** Deleting your data removes your Realm progress on that server: house membership, titles, renown, dynasty, vault contents and open market orders or contracts (open escrow is returned or settled first). It cannot be undone.

## 2. What is deleted, changed or kept

| Data | On a deletion request | Why |
|---|---|---|
| Statistics key (`RealmStats`) | Removed: the player runs `/stats optout`, or staff ask them to. Staff cannot compute the key without the salt, and must not try | The plugin scrubs every kept day |
| House membership, renown, dynasty, titles, treasury vault and marks, season standing | Removed (section 3) | Game data that identifies the player |
| Open contracts and market orders | Settled or cancelled with escrow returned **first** (`/contract admin refund`, `/treasury cancel`), then removed | Other players' items are in escrow |
| Chronicle entries naming the player | Their name is **replaced** with "a forgotten lord" in title, detail and actors; the event stays | The history involves other players too. **Copies already posted to Discord or in portal builds are deleted by hand where staff can find them**; copies others saved cannot be recalled |
| Hall of Kings (`RealmLegends.json`) | Name replaced as above | Same |
| Private letters and letter audit | Deleted (or simply left to expire in 7 days) | |
| Warden records, Court log lines, case notes | **Kept** while a penalty is active or a case or appeal is open, and for the retention in the moderation handbook (12 months; a permanent-ban summary while the ban stands). Otherwise deleted | Safety of other players; preventing ban evasion |
| Game's own ban list | Kept while a ban stands | Same |
| Game's own log files (`Logs\Log[...].txt`) | Not edited line by line. They are deleted on the normal schedule (policy: older than 90 days) | Realm does not change game files; logs are output and are cleared as a whole |
| Bot oath time (`bot/state/bot-state.json`) | Deleted | |
| Backups | **Not edited.** They expire on schedule (14 local copies, 30 days off-site by default). The deletion is re-applied if a backup from before it is ever restored (section 4) | Editing backups risks breaking restores |

## 3. Staff procedure

Do this with the **two-person rule** (one does, one checks). Work on the server PC.

1. **Log the request** in the private deletion register: date received, request type, SteamID64, Discord ID if any, who verified identity and how. Nothing else.
2. **Verify identity** (section 1). No verification, no action; tell the requester what is needed.
3. **Find the data** with the read-only search tool (it prints file and JSON paths and line numbers, never values or other players' data):

   ```
   node docs/legal/tools/find-player-data.mjs --steamid <SteamID64> --name "<in-game name>" ^
        --dir "G:\RealmTest\server\oxide\data" --dir "G:\RealmTest\server\oxide\logs" ^
        --dir "%APPDATA%\Realm\court" --dir "<repo>\bot\state"
   ```

   Add `--name` once for each name they have used. Use the real data path (`oxide\data` or `Saves\oxide\data`, the open question in `README.md`). Archives and backups are listed but not opened.
4. **For "see" requests:** copy the matching entries into a file for the player. Remove other players' Steam IDs and any letter the player did not write or receive. Send it, and log the date.
5. **For "delete" requests:**
   1. Settle the game first: refund open contracts (`/contract admin refund <id>`), cancel open market orders (`/treasury cancel <id>`), free any captive or captor link, and if they lead a house ask them to `/house leave` first (or hand over).
   2. **Back up** (Steward backup or `Backup-RealmOffsite.ps1 -LocalOnly -Label deletion`).
   3. **Unload** the plugins whose files you will edit (`oxide.unload <Plugin>`), so they do not overwrite your edit on their next save. RealmChronicle, RealmHouses and RealmContracts must be unloaded together with the plugins that call them, or the whole server stopped. Stopping the server (Steward **Stop**) is the simplest safe option.
   4. Edit each JSON file the search found: remove the player's entries, or replace their name with `a forgotten lord` where the table says so. Keep the JSON valid (check with `node -e "JSON.parse(require('fs').readFileSync(process.argv[1],'utf8'))" <file>`). Do **not** renumber Chronicle ids.
   5. Delete Court log lines and Warden log lines only where the table allows.
   6. Start the server or reload the plugins. Watch the console for load errors. Several plugins refuse to overwrite a file they cannot parse and say so: fix the file rather than deleting it.
   7. Run the search again. The only matches left should be the records the table says to keep.
   8. Rebuild the portal (`portal/README.md`) so old pages are replaced, and delete herald or bot posts in Discord that name the player, if they ask.
6. **Reply** using the template below. Log the date and what was kept.

**UNVERIFIED:** hand-editing plugin data files while the plugins are unloaded has not been tried on a live server. Do the first one on a test copy (`server/New-TestServer.ps1`) with the same data.

## 4. If a backup is restored later

After any restore (`ops/disaster-recovery.md`, `ops/restore-drill.md`), check the deletion register for requests completed **after** the backup's date and re-apply them before reopening the server. This is why the register keeps the SteamID64: it is the minimum needed to honour the request. Delete register entries 60 days after the request, once every backup from before it has expired.

## 5. Reply template

> Hello [name],
>
> We have completed your request of [date].
>
> **Deleted:** [list, for example: house membership, renown and titles, dynasty, letters, statistics key, Discord oath record].
> **Changed:** your name in [N] Chronicle entries now reads "a forgotten lord".
> **Kept, and why:** [for example: "a moderation record from [date], kept until [date] under our moderation rules", or "nothing"].
> **Backups:** older copies in our backups expire by [date]. If we ever restore one, we will remove your data again first.
>
> If anything is missing, reply to this message. You can also complain to your data-protection regulator if you are not satisfied.
>
> [Owner name], Realm

## 6. Targets

| Step | Target |
|---|---|
| Acknowledge the request | 3 days |
| Identity verified | 7 days after the requester sends the code |
| Done and answered | 30 days from receipt |
