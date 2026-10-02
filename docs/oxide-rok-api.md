# Oxide for Reign of Kings: plugin API reference

This is the plugin API reference for the Realm server. It covers legacy **Oxide.ReignOfKings 2.0.3867**, the Oxide build for the Reign of Kings dedicated server (Steam app 381690). Plugins are C# files placed in `oxide/plugins/`. Oxide compiles them on the server.

Everything below comes from source code, from the shipped binaries, or from real plugins. Each claim has a tag:

| Tag | Meaning |
|---|---|
| **[SRC]** | Read in Oxide source at the pinned commit. The location is cited. |
| **[OPJ]** | Read in the patch manifest `resources/ReignOfKings.opj` (the list of hooks injected into the game). The line is cited. |
| **[ASM]** | Confirmed in the .NET metadata of the DLLs inside the official 2.0.3867 release zip. This covers the patched game `Assembly-CSharp.dll`, `Oxide.Core.dll`, `Oxide.CSharp.dll` and `Oxide.ReignOfKings.dll`. Type names, members and signatures were dumped with a metadata reader. No code was executed. |
| **[USE]** | Seen in real public RoK plugins (see Sources). Those plugins date from 2016–2018 and were written for older Oxide builds. |
| **[UNVERIFIED]** | An inference or a gap. Test it on the owner's machine before relying on it. |

Nothing here was compiled. This container has no dotnet/mono and no game install. See section 10 for what still needs a live test.

---

## 0. Sources and pinned versions

| Item | Value |
|---|---|
| Oxide.ReignOfKings repo | `https://github.com/OxideMod/Oxide.ReignOfKings`, tag `2.0.3867` = commit `2e6ee8f6c4a1f067fff45ae8b9d9d0d0dab35700` (2023-10-09). Its `src/` and `resources/` are identical to `master` (`f690b9c`, 2024-04-28); the only later diff is a CI workflow. |
| Release asset | `https://github.com/OxideMod/Oxide.ReignOfKings/releases/download/2.0.3867/Oxide.ReignOfKings.zip`, sha256 `6c35c623fa9ee412945f61a32e8196091b40d56bfe9f8376d3d8e6243b72c6c8` (11,939,101 bytes, as downloaded 2026-10-02) |
| DLL versions inside the zip [ASM] | `Oxide.ReignOfKings` 2.0.3867, `Oxide.Core` 2.0.4077, `Oxide.CSharp` 2.0.4126 |
| Oxide.Core source read | `https://github.com/OxideMod/Oxide.Core` @ `0b5521e` (the last commit before the bundled DLL's Dec 2023 build date) |
| Oxide.CSharp source read | `https://github.com/OxideMod/Oxide.CSharp` @ `49500b8` (Oct 2023) |
| Oxide.Patcher source (for hook return semantics) | `https://github.com/OxideMod/Oxide.Patcher` @ `3272439`, `src/OpjData/Hooks/Simple.cs` |
| Real plugins | `https://github.com/RustServers-IO/oxideplugins` @ `4de21d4` (2018), folder `reign of kings/`: 66 C# plugins. Cited by file name below. |
| uMod docs | `https://umod.org/documentation/games/reign-of-kings`. **umod.org was blocked from this build environment.** A search-engine summary of that page lists the same throne and player hook names found in the OPJ. Nothing here depends on uMod docs. |

Paths in this doc are relative to each repo root. For example, `src/ReignOfKingsHooks.cs:25` means line 25 of Oxide.ReignOfKings at tag 2.0.3867.

---

## 1. How hooks reach your plugin

1. **The game DLL is patched.** The release zip ships a pre-patched `ROK_Data/Managed/Assembly-CSharp.dll` (8.2 MB) that **overwrites the server's copy** [ASM]. Each hook in the OPJ is an `Interface.CallHook("<HookName>", args)` call woven into a game method. This modifies **server** files only; game clients are untouched.
2. **Hooks whose names start with `I` are internal.** Examples are `IOnPlayerChat` and `IOnUserApprove`. Only the core plugin `ReignOfKingsCore` handles them (`src/ReignOfKingsHooks.cs`). The core then calls one or more public hooks, usually a RoK-specific one plus a game-independent "Covalence" one (`OnUser*`). Do not write `I*` methods in plugins.
3. **Every other OPJ hook goes straight to plugins** with the game's event object.
4. **Hook names and parameters are matched loosely** [SRC] (`Oxide.Core: src/Plugins/HookMethod.cs:33-93`, `src/Plugins/CSPlugin.cs:125-145`):
   - The name must match exactly. The method can be `private`.
   - A parameter can be declared as a **base type** of the real argument (`IsInstanceOfType`). That is why `OnPlayerRespawn(PlayerRespawnEvent e)` receives all four respawn event subclasses.
   - You may declare **fewer** parameters than are passed; the extra arguments are dropped.
5. **Only non-public instance methods declared in your plugin class are hooks** [SRC] (`Oxide.CSharp: src/CSharpPlugin.cs:224-268`, the constructor scans `type.GetMembers(BindingFlags.NonPublic | BindingFlags.Instance)` and calls `AddHookMethod` for every method declared in the class). The same table serves `plugin.Call("Name", ...)` (`Oxide.Core: src/Plugins/Plugin.cs:311` → `CSPlugin.OnCallHook`). **A `public` method is never found by `Call`, which then returns null.** Cross-plugin API methods must therefore be `private` (or `protected`/`internal`), exactly like hooks. Corollary: every private instance method is a potential hook, so do not give helpers the name of a hook.
6. **`[PluginReference]`** [SRC] (`Oxide.CSharp: src/CSharpPlugin.cs:91-104, 241-247`): a non-public instance field of type `Plugin`, marked `[PluginReference]`, is set to the loaded plugin whose name equals the field name (or the attribute's `Name`), and back to null when that plugin unloads.

### 1.1 What a hook's return value does

The OPJ gives each hook a `ReturnBehavior` (RB) [OPJ]. The patcher compiles that RB into the game method as follows [SRC] (`Oxide.Patcher: src/OpjData/Hooks/Simple.cs:16, 399-454`):

| RB | Name | Effect when the patched game method returns `void` |
|---|---|---|
| 0 | `Continue` | The plugin's return value is **ignored**. |
| 1 | `ExitWhenValidType` | If any plugin returns **non-null**, the game method **returns immediately**. The rest of the game's handler is skipped. |

For a non-void game method, RB=1 returns your value only if it has the method's return type. The only such case here is `CommandManager.ExecuteCommand` → `bool`.

> **Return vs `Cancel()` — read this.** RB=1 only skips the rest of *that one listener method*. Every hook argument is a CodeHatch event deriving from `CodeHatch.Networking.Events.BaseEvent`, which has `Cancel()`, `Cancel(string reason)`, `Uncancel()`, `bool Cancelled { get; }` and `string CancelReason { get; }` [ASM]. Real plugins **block actions by calling `evt.Cancel(...)`** [USE]. Examples: `NoFriendlyFire.cs:116` and `ProtectedZone.cs:481` (damage), `LevelSystem.cs:2019` (throne capture) and `:2043` (rope capture), `LockPickManager.cs:180` (lockpick), `AntiLoot.cs:160` (interact), `LevelSystem.cs:2072` (cube placement).
> **Recommended pattern:** call `evt.Cancel("reason")` and also `return true` when the hook is RB=1. Check `if (evt.Cancelled) return;` first so you respect other plugins.
> **[UNVERIFIED]** Whether `Cancel()` alone stops each specific action. It depends on game internals that cannot be read from metadata. Test every blocking rule in-game.

### 1.2 Hooks are called on the server player too

The core filters out the server's own pseudo-player (id `9999999999`) **only** in the core-dispatched hooks (`src/ReignOfKingsHooks.cs:66, 94, 143`). Direct game hooks such as `OnEntityHealthChange` and `OnEntityDeath` do not filter it. Guard with `player.IsServer` or `entity.Owner.IsServer` [ASM] (`DeathMessages.cs:20` [USE]).

---

## 2. Hook reference

Columns:
- **Signature**: what to declare in your plugin.
- **Return**: what a non-null return does.
- **Game method (inj. idx)**: the patched method and its injection index. Index 0 is the very start of the method, before the game does anything.
- **Source**: where it was verified.

Event-type members were checked in [ASM]. Section 3 lists them.

### 2.1 Lifecycle hooks (Oxide, not game patches)

| Signature | Called when | Return | Source |
|---|---|---|---|
| `void Init()` | After config and lang load, when the plugin is added. Runs on every (re)load. | ignored | [SRC] Oxide.Core `src/Plugins/CSPlugin.cs:95`; order: `src/Plugins/Plugin.cs:201-216` (LoadConfig → LoadDefaultMessages → Init) |
| `void Loaded()` | Right after `Init` | ignored | [SRC] Oxide.CSharp `src/CSharpPlugin.cs:341` |
| `void Unload()` | Plugin unloading or server shutdown | ignored | [SRC] Oxide.CSharp `src/CSharpPlugin.cs:354` |
| `protected override void LoadDefaultConfig()` | Config file `oxide/config/<Name>.json` does not exist. Overriding it is what turns config on. | — | [SRC] Oxide.Core `src/Plugins/Plugin.cs:375-396`; Oxide.CSharp `src/CSharpPlugin.cs:299-300` |
| `protected override void LoadDefaultMessages()` | Every load, if overridden. Register lang strings here. | — | [SRC] Oxide.Core `src/Plugins/Plugin.cs:209-212, 425` |
| `void OnServerInitialized()` | Once when the world finishes loading. **Also called on plugins hot-loaded after startup.** | ignored | [OPJ] L62 → `CodeHatch.Engine.Core.Gaming.Game.OnLoadingComplete()` idx 69; [SRC] `src/ReignOfKingsCore.cs:112-120` |
| `void OnServerSave()` | Game world save | ignored (RB 0) | [OPJ] L86 → `Game.Save()` idx 6; core handler `src/ReignOfKingsCore.cs:167-172` |
| `void OnServerShutdown()` | `CodeHatch.Engine.Networking.Server.Shutdown()` | ignored (RB 0) | [OPJ] L946 idx 2; core `src/ReignOfKingsCore.cs:177-182` |
| `void OnPluginLoaded(Plugin plugin)` / `void OnPluginUnloaded(Plugin plugin)` | Another plugin loads or unloads | ignored | [SRC] Oxide.Core `src/OxideMod.cs:501, 540` |

### 2.2 Connection, chat and command hooks (core-dispatched)

| Signature | Return semantics | Dispatch / game method | Source |
|---|---|---|---|
| `object CanClientLogin(ConnectionRequestEvent evt)` | Return a `string` or `false` to reject. The core then returns `uLink.NetworkConnectionError.ApprovalDenied`, which makes the game's `OnConnectionRequest` exit early. The string is **not** sent to the client. If this returns null, the result of `CanUserLogin` is used. | `IOnUserApprove` ← `CoreServer.OnConnectionRequest(ConnectionRequestEvent)` idx 80, RB 1 | [SRC] `src/ReignOfKingsHooks.cs:25-55`; [OPJ] L1637 |
| `object CanUserLogin(string name, string id, string ip)` | Same as above (Covalence variant) | same | same |
| `object OnUserApprove(ConnectionRequestEvent evt)` | Any non-null value is returned to the game, so the method exits early. **[UNVERIFIED]** what that does to the connection; it probably leaves the request unapproved. | same | `src/ReignOfKingsHooks.cs:52-54` |
| `object OnUserApproved(string name, string id, string ip)` | Used only if `OnUserApprove` returned null | same | same |
| `void OnPlayerConnected(Player player)` | ignored. Called after `player.IPlayer` is set and the player has been added to the Oxide `default` group. Admins with the game `admin` permission are also added to the `admin` group. | `IOnPlayerConnected` ← `CoreServer.NotifyOfPlayerJoin(Player)` idx 0 | [SRC] `src/ReignOfKingsHooks.cs:90-133`; [OPJ] L526 |
| `void OnUserConnected(IPlayer player)` | ignored | same | same |
| `void OnPlayerDisconnected(Player player)` | ignored | `IOnPlayerDisconnected` ← `CoreServer.RemovePlayer(Player)` idx 33 | [SRC] `src/ReignOfKingsHooks.cs:139-154`; [OPJ] L110 |
| `void OnUserDisconnected(IPlayer player, string reason)` | ignored. `reason` is **always** the localized string "Unknown". | same | `src/ReignOfKingsHooks.cs:150` |
| `object OnPlayerChat(PlayerMessageEvent evt)` | **Non-null blocks the message.** The core calls `evt.Cancel()` and returns `true`. If this returns null, the result of `OnUserChat` is used. | `IOnPlayerChat` ← `PlayerListener.OnPlayerMessage(PlayerMessageEvent)`, injected **twice** (idx 5 "[guild]" and idx 86 "[player]"), RB 1 | [SRC] `src/ReignOfKingsHooks.cs:62-83`; [OPJ] L136, L500 |
| `object OnUserChat(IPlayer player, string message)` | same | same | same |
| `object OnServerCommand(string command, string[] args)` | Non-null blocks **every** command (player or console). `command` has no leading `/`. | `IOnServerCommand` ← `CommandManager.ExecuteCommand(ulong, string) : bool` idx 0, RB 1 | [SRC] `src/ReignOfKingsHooks.cs:193-215`; [OPJ] L1009 |
| `object OnPlayerCommand(Player player, string command, string[] args)` | Non-null blocks a player's command. Called only when the sender is a real online player. | same | `src/ReignOfKingsHooks.cs:217-232` |
| `object OnUserCommand(IPlayer player, string command, string[] args)` | same (Covalence) | same | same |

Chat event subclasses [ASM]: `PlayerMessageEvent : PlayerEvent`, with property `string Message`.
- `CodeHatch.Networking.Events.Entities.Players.PlayerChatEvent : PlayerMessageEvent`
- `CodeHatch.Networking.Events.Social.GuildMessageEvent : PlayerChatEvent`
- `CodeHatch.Networking.Events.Players.PlayerLocalChatEvent : PlayerChatEvent`
- `CodeHatch.Engine.Events.Player.PlayerWhisperEvent : PlayerChatEvent`

To tell guild, local and whisper chat apart, test `evt is GuildMessageEvent` and so on (`OOC.cs:100-105` [USE]). **[UNVERIFIED]** Whether the two injection points can both fire for one message.

> The OPJ also injects `IOnChatCommand` into `PlayerListener.OnPlayerCommand(PlayerCommandEvent)` (idx 0, RB 1) [OPJ L214]. **No handler exists**: the string is absent from the shipped `Oxide.ReignOfKings.dll` [ASM] and from the source. In practice it is a no-op. Use `OnPlayerCommand` instead.

### 2.3 Spawn, sleep and respawn hooks

| Signature | Return | Game method (idx) | Source |
|---|---|---|---|
| `void OnPlayerSpawn(PlayerFirstSpawnEvent evt)` | ignored (RB 0) | `PlayerListener.OnPlayerFirstSpawnVeryEarly` (153) | [OPJ] L240; also re-dispatched as `OnUserSpawn(IPlayer)` `src/ReignOfKingsHooks.cs:160-165` |
| `void OnPlayerSpawned(PlayerPreSpawnCompleteEvent evt)` | ignored | `PlayerListener.OnPreSpawnComplete` (41) | [OPJ] L1244; `OnUserSpawned(IPlayer)` `src/ReignOfKingsHooks.cs:171-176` |
| `void OnPlayerRespawn(PlayerRespawnEvent evt)` | ignored | Injected into **four** methods of `CharacterRespawn`: `OnPlayerRespawnRandomly` (14), `OnPlayerRespawnNormal` (16), `OnPlayerRespawnAtBed` (22), `OnPlayerRespawnAtBase` (22). The argument is the matching subclass `PlayerRespawn{Randomly,Normal,AtBed,AtBase}Event`. | [OPJ] L344-L422; `OnUserRespawn(IPlayer)` `src/ReignOfKingsHooks.cs:182-187`. The core comments "Not being called every time?" |
| `void OnPlayerSleep(PlayerSleepEvent evt)` | ignored | `PlayerListener.OnPlayerSleep` (3) | [OPJ] L448 |

### 2.4 Combat, entity and structure hooks

| Signature | Return | Game method (idx) | Source |
|---|---|---|---|
| `object OnEntityHealthChange(EntityDamageEvent evt)` | RB 1: non-null skips the rest of `InvokeDamage`. The usual pattern is `evt.Cancel(); evt.Damage.Amount = 0f;` [USE `NoFriendlyFire.cs:116-117`]. | `EntityHealth.InvokeDamage(EntityDamageEvent)` (15) | [OPJ] L162 |
| `object OnEntityDeath(EntityDeathEvent evt)` | RB 1 | `EntityHealth.InvokeDeath(EntityDeathEvent)` (10) | [OPJ] L188 |
| `void OnCubePlacement(CubePlaceEvent evt)` | ignored. Block with `evt.Cancel()` [USE `LevelSystem.cs:2072`]. | `CubeListener.OnCubePlace` (0) | [OPJ] L266 |
| `void OnCubeTakeDamage(CubeDamageEvent evt)` | ignored. Use `Cancel()`. | `CubeListener.OnCubeDamage` (0) | [OPJ] L292 |
| `void OnCubeDestroyed(CubeDestroyEvent evt)` | ignored | `CubeListener.OnCubeDestroy` (0) | [OPJ] L318 |
| `object OnPlayerInteract(InteractEvent evt)` | RB 1 (also `evt.Cancel(...)` [USE `AntiLoot.cs:160`]) | `InteractableListener.OnInteract` (114) | [OPJ] L1036 |
| `object OnPlayerUnlock(ObjectUnlockEvent evt)` | RB 1 (lockpicking; use `evt.Cancel(...)` [USE `LockPickManager.cs:180`]) | `CodeHatch.Thrones.Lockpick.OnUnlock` (0) | [OPJ] L1114 |
| `void OnObjectDeploy(NetworkInstantiateEvent evt)` | ignored | `ObjectSupplier.OnInstantiate` (16) | [OPJ] L474 |

### 2.5 Item, crafting and gadget hooks (all RB 1)

| Signature | Game method (idx) | Source |
|---|---|---|
| `object OnItemCraft(ItemCrafterStartEvent evt)` | `ItemCrafterListener.OnItemCrafterStart` (9) | [OPJ] L1062 |
| `object OnItemCrafted(ItemCrafterFinishEvent evt)` | `ItemCrafterListener.OnItemCrafterFinish` (9) | [OPJ] L1088 |
| `object OnGadgetCollect(GadgetCollectEvent evt)` | `GadgetListener.OnGadgetCollect` (17) | [OPJ] L789 |
| `object OnGadgetPlace(GadgetPlaceEvent evt)` | `GadgetListener.OnGadgetPlace` (3) | [OPJ] L815 |
| `object OnGadgetEnable(GadgetEnableEvent evt)` | `GadgetListener.OnGadgetEnable` (3) | [OPJ] L841 |
| `object OnGadgetDisable(GadgetDisableEvent evt)` | `GadgetListener.OnGadgetDisable` (3) | [OPJ] L867 |
| `object OnGadgetReposition(GadgetRepositionEvent evt)` | `GadgetListener.OnGadgetReposition` (3) | [OPJ] L893 |

### 2.6 Throne and crown hooks (central to the Realm design)

All of these live in `CodeHatch.Thrones.AncientThrone.AncientThroneListener` [OPJ].

| Signature | Return | Game method (idx) | Notes | Source |
|---|---|---|---|---|
| `object OnThroneCapture(AncientThroneCaptureEvent evt)` | RB 1. Block a capture **attempt** with `evt.Cancel()` [USE `LevelSystem.cs:2006-2020`]. | `OnAncientThroneCapture` (**24**, early) | `evt.Player` is the capturer. `evt.State` is `Capturing`, `Cancelled` or `Completed` (a nested enum `States`). `evt.Time` is a `double`. | [OPJ] L581 |
| `void OnThroneCaptured(AncientThroneCaptureEvent evt)` | ignored | same method (**130**, late) | Fires later in the same handler. Real plugins treat it as "a new king is crowned" [USE `RaidBoss.cs:605-616`]. **[UNVERIFIED]** whether it also fires for `Capturing` or `Cancelled` states, so check `evt.State == AncientThroneCaptureEvent.States.Completed`. | [OPJ] L607 |
| `void OnThroneReleased(AncientThroneReleaseEvent evt)` | ignored | `OnAncientThroneRelease` (39) | `evt.Sender` (a `Player`) is the old king [USE `RaidBoss.cs:626`]. Fields: `string Name`, `bool IsDeath`. This is a `NetworkEvent`, **not** a `PlayerEvent`. | [OPJ] L633 |
| `void OnThroneRename(AncientThroneRenameEvent evt)` | ignored | `OnAncientThroneRename` (20) | `evt.Player`, `string Name` | [OPJ] L659 |
| `void OnThroneTax(AncientThroneTaxEvent evt)` | ignored | `OnAncientThroneTax` (16) | `evt.Player`, `float Tax` | [OPJ] L685 |
| `object OnKingDeath(PlayerDeathEvent evt)` | RB 1 | `OnPlayerDeath(PlayerDeathEvent)` (15) | **[UNVERIFIED]** whether this fires only when the king dies (the name suggests so) and what a non-null return suppresses. | [OPJ] L1140 |
| `object OnKingSleeperDeath(SleeperDeathEvent evt)` | RB 1 | `OnSleeperDeath(SleeperDeathEvent)` (15) | Same caveat | [OPJ] L1166 |

### 2.7 Capture (rope, chain, cage) and escape hooks — basis for "bounded ransom"

All of these live in `CodeHatch.Thrones.Capture.PlayerCaptureManager` [OPJ].

| Signature | Return | Game method (idx) | Notes | Source |
|---|---|---|---|---|
| `object OnPlayerCapture(PlayerCaptureEvent evt)` | RB 1. Block with `evt.Cancel()` [USE `LevelSystem.cs:2023-2052`, `GrandExchange.cs:2535-2557`]. | `OnCaptureEvent` (140) | `evt.Captor` (`Entity`; use `.Owner` for the `Player`), `evt.TargetEntity` (`Entity`), field `Player Target`, field `CaptureType Type` (`Rope`, `Chain`, `Cage`, `Auto`), field `int CageID` | [OPJ] L711 |
| `object OnPlayerEscape(PlayerEscapeEvent evt)` | RB 1 | `OnEscapeEvent` (6, early) | field `Entity Escapee` | [OPJ] L737 |
| `object OnPlayerRelease(PlayerEscapeEvent evt)` | RB 1 | same method (20, later) | **[UNVERIFIED]** how "escape" differs from "release"; probably the attempt vs. the completed release | [OPJ] L763 |

### 2.8 Guild ("house") hooks (all RB 1)

All of these live in `CodeHatch.Thrones.SocialSystem.ServerSupplier` [OPJ].

| Signature | Game method (idx) | Event fields [ASM] | Source |
|---|---|---|---|
| `object OnGuildInvite(GuildInvitationEvent evt)` | `OnGuildInvite` (116) | `ulong Inviter`, `ulong Invitee`, `InvitationInfo Information` | [OPJ] L1192 |
| `object OnGuildInvitationAccept(GuildInvitationAcceptEvent evt)` | `OnInvitationAccept` (36) | `ulong PlayerId`, `ulong GroupId` | [OPJ] L1218 |
| `object OnGuildAbandon(RemoveMembershipInfoEvent evt)` | `OnRemoveRequest` (14) | `ulong PlayerId`, `bool TransferInfo`, `string Message` | [OPJ] L1270 |
| `object OnGuildBanish(BanishMemberEvent evt)` | `OnBanishMember` (31) | `ulong PlayerToBanish`, `ulong VotingPlayer`, `bool Vote` | [OPJ] L1296 |

There is **no hook** for guild creation, rename, or reject/remove of invitations, although the event types `GuildInvitationRejectEvent` and `GuildInvitationRemoveEvent` exist [ASM]. Sworn-allegiance rules must work from these four hooks plus polling of `GuildScheme`.

### 2.9 Internal patches (no plugin hook)

These OPJ entries change game code but give plugins nothing to hook [OPJ]:

- `InitOxide` (L14): boots Oxide from `SocketAdminConsole.OnEnable`.
- `InitLogging` (L38).
- `FixStartupNREs`, `FixDupeConsoleLog`, `FixShutdownNRE`, `HandleDynamicTypes`, `RecountFoldersManaged/Root`.
- `CalculateOriginalHash` (L1362): makes the game's file hasher read `Managed/Assembly-CSharp_Original.dll` instead of the patched DLL. The release zip does **not** contain that `_Original` file. **[UNVERIFIED]** Whether Oxide or the user is expected to keep a copy of the vanilla DLL under that name. See section 10.

### 2.10 Full name list (for search)

`OnServerInitialized, OnServerSave, OnServerShutdown, OnPlayerConnected, OnUserConnected, OnPlayerDisconnected, OnUserDisconnected, CanClientLogin, CanUserLogin, OnUserApprove, OnUserApproved, OnPlayerChat, OnUserChat, OnServerCommand, OnPlayerCommand, OnUserCommand, OnPlayerSpawn, OnUserSpawn, OnPlayerSpawned, OnUserSpawned, OnPlayerRespawn, OnUserRespawn, OnPlayerSleep, OnEntityHealthChange, OnEntityDeath, OnCubePlacement, OnCubeTakeDamage, OnCubeDestroyed, OnPlayerInteract, OnPlayerUnlock, OnObjectDeploy, OnItemCraft, OnItemCrafted, OnGadgetCollect, OnGadgetPlace, OnGadgetEnable, OnGadgetDisable, OnGadgetReposition, OnThroneCapture, OnThroneCaptured, OnThroneReleased, OnThroneRename, OnThroneTax, OnKingDeath, OnKingSleeperDeath, OnPlayerCapture, OnPlayerEscape, OnPlayerRelease, OnGuildInvite, OnGuildInvitationAccept, OnGuildAbandon, OnGuildBanish` plus the lifecycle hooks `Init, Loaded, Unload, LoadDefaultConfig, LoadDefaultMessages, OnPluginLoaded, OnPluginUnloaded`.

Every injected hook name string was confirmed present in the shipped patched `Assembly-CSharp.dll` [ASM].

---

## 3. Game types (CodeHatch) — verified members

These were dumped from the shipped patched `ROK_Data/Managed/Assembly-CSharp.dll` [ASM]. Only public members are listed. Property getters appear as properties, and `object[]` parameters are `params` in C#.

### 3.1 Event base classes

- `CodeHatch.Networking.Events.BaseEvent`: `Cancel()`, `Cancel(string reason)`, `Cancel(string format, params object[])`, `Uncancel()`, `bool Cancelled`, `string CancelReason`, `string EventName`.
- `CodeHatch.Networking.Events.NetworkEvent` (via `PermissionEvent`) adds:
  - `ulong SenderId`
  - `Player Sender`
  - `List<Player> Recipients`
- `CodeHatch.Networking.Events.Players.PlayerEvent : NetworkEvent`: `ulong PlayerId`, `Player Player`, `bool IsPlayer`, `string PlayerName`, `string DisplayName`.
- `CodeHatch.Networking.Events.Entities.EntityEvent : NetworkEvent`: field `Entity Entity`.

### 3.2 `CodeHatch.Engine.Networking.Player`

| Kind | Members |
|---|---|
| Identity | `ulong Id` (SteamID64), `string Name`, `string DisplayName`, `string OriginalName` |
| Connection | `Connection Connection`, `int AveragePing` |
| State | `bool IsServer`, `Entity Entity` (has `.Position`), `Character CurrentCharacter` |
| Chat formats | `string ChatFormat`, `string GuildChatFormat` |
| Oxide | field `Oxide.Core.Libraries.Covalence.IPlayer IPlayer`, added by Oxide's patch. Valid once `OnPlayerConnected` has fired. |

### 3.3 `CodeHatch.Common.PlayerExtensions` (needs `using CodeHatch.Common;`)

Static extension methods on `Player`:

| Purpose | Methods |
|---|---|
| Chat | `SendMessage(string)`, `SendMessage(string format, params object[])`, `SendError(string)`, `SendError(string, params object[])`, `SendGuildMessage(...)`, `SendLocalMessage(...)` |
| Popups | `ShowPopup(title, message, buttonText, OnSubmit handler, bool interupt, bool broadcast)`, `ShowConfirmPopup(...)`, `ShowInputPopup(...)`. Plugins call these with only `(title, message)` [USE `GuildInfo.cs:136`], so trailing parameters have defaults. **[UNVERIFIED]** The exact default values. |
| Game permissions | `bool HasPermission(string permission)`: the **game's own** permission system, not Oxide's (see 4.3) |
| Guild | `Guild GetGuild()` |
| Health | `PlayerHealth GetHealth()`, `Heal(float)`, `bool Kill(DamageType)`, `bool IsAlive()`, `SetGodMode(bool, bool)` |
| Inventory | `Container GetInventory()`, `bool ClearInventory()` |

### 3.4 `CodeHatch.Engine.Networking.Server` (all static)

| Purpose | Members |
|---|---|
| Player lists | `ReadOnlyCollection<Player> AllPlayers`, `List<Player> ClientPlayers`, `ClientPlayersExcept(params Player[])`, `AllPlayersExcept(params Player[])` |
| Lookup | `Player GetPlayerById(ulong)`, `Player GetPlayerByName(string)`, `Player MatchFirstPlayerByName(string)`, `List<Player> MatchPlayerByName(string)`, `bool PlayerIsOnline(ulong)` |
| Server player | `Player ServerPlayer` |
| Counts | `int PlayerCount`, `int PlayerLimit` |
| Chat | `BroadcastMessage(string)`, `BroadcastMessage(string format, params object[])`, `SendPlayerMessage(ulong playerId, string message)`, `Notice(string)` |
| Moderation | `Kick(Player, string reason)`, the `Ban(...)` overloads, `Unban(ulong)`, `bool IdIsBanned(ulong)` |
| Permissions | `CodeHatch.Permissions.Permission Permissions` (game permissions) |

`AllPlayers` includes the server pseudo-player. Plugins exclude it with `Server.AllPlayersExcept(Server.GetPlayerByName("Server"), ...)` [USE `AllianceTracker.cs:253`]. **[UNVERIFIED]** Whether `ClientPlayers` already excludes it, so filter with `!p.IsServer` to be safe.

### 3.5 `CodeHatch.Engine.Core.Cache.Entity` (partial)

`Vector3 Position`, `bool IsPlayer`, `Player Owner`, `ulong OwnerId`, `T TryGet<T>()`, `bool Has<T>()`.

`CodeHatch.Damaging.Damage`:
- `float Amount` (settable [USE])
- `bool IsFatal`
- `DamageType DamageTypes`
- `Entity DamageSource`
- `UnityEngine.Object Damager`
- `Neutralize()`

### 3.6 Guilds ("houses")

| Type | Members [ASM] |
|---|---|
| `CodeHatch.Engine.Modules.SocialSystem.SocialAPI` (static) | `T Get<T>()`, `ulong GetGroupId(ulong playerId)`, `bool ShareGroupMembership(ulong, ulong)`, `SendGroupMessage(ulong playerId, string)` |
| `CodeHatch.Thrones.SocialSystem.GuildScheme` (via `SocialAPI.Get<GuildScheme>()` [USE `GuildInfo.cs:140`]) | `Guild TryGetGuildByMember(ulong playerId)`, `ulong GetGuildIdByMember(ulong)`, `Guild TryGetGuild(ulong guildId)`, `List<Player> GetPlayersByGuildId(ulong)`, `bool ShareGuildMembership(ulong, ulong)`, `bool IsGuildMember(ulong playerId, ulong guildId)`, `SocialStorage Storage` (`Storage.TryGetAllGroups()` lists every guild [USE `GuildInfo.cs:186`]), `SetGuildName(ulong, string)`, `InviteToGuild(...)`, `RemoveGuildMembership(...)` |
| `CodeHatch.Thrones.SocialSystem.Guild : RootSocialGroup` | `ulong BaseID` (the guild id), `ulong OwnerId`, `string Name`, `string DisplayName`, `Members Members()`, `Relationships Relationships()`, field `BannerData Banner` |
| `CodeHatch.Engine.Modules.SocialSystem.Members` | `ReadOnlyCollection<Member> GetAllMembers()`, `List<Player> GetMemberPlayers()`, `bool Has(ulong)`, `int MemberCount()`, `Member TryGetMember(ulong)` |
| `CodeHatch.Engine.Modules.SocialSystem.Member` | fields `ulong PlayerId`, `string Name`, `bool OnlineStatus`; `byte SecurityLevel`, `bool IsAdmin`, `Player GetPlayer()` |

### 3.7 The crown (king, realm name, tax)

**`CodeHatch.Thrones.SocialSystem.KingsScheme`**, obtained with `SocialAPI.Get<KingsScheme>()` [USE `RaidBoss.cs:496`] [ASM]:

| Purpose | Members |
|---|---|
| Who is king | `bool HasKing()`, `Player GetKing()`, `ulong GetKingID()`, `string GetKingName()`, `bool IsKing(Player)`, `bool IsKing(ulong)`, `bool IsKingGuild(Player)`, `bool IsKingGuild(ulong)` |
| Realm settings | `string GetRealm()`, `float GetTax()`, `SetTax(float)`, `SetRealm(string)`, `KingsRealm GetKingsRealm()` |

`KingsRealm` holds:
- instance: `ulong King`, `string Name`, `string Realm`, `float Tax`
- static: `TaxMinimum`, `TaxMaximum`, `TaxDefault`

`AncientThroneListener` instance members include `Player Usurper`, `float Progress`, `float CaptureTime`. **[UNVERIFIED]** How to obtain the listener instance; the likely way is `UnityEngine.Object.FindObjectOfType<AncientThroneListener>()`.

### 3.8 Namespaces and capture state (checked in the 2.0.3867 metadata) [ASM]

- `CodeHatch.Thrones.AncientThrone`: `AncientThroneCaptureEvent` (nested enum `States`), `AncientThroneReleaseEvent`, `AncientThroneTaxEvent`.
- `CodeHatch.Networking.Events`: `PlayerCaptureEvent`, `PlayerEscapeEvent`.
- `CodeHatch.Damaging.DamageType` (enum; members include `Unknown`, `Suicide`, `Impact`, `Melee`). `PlayerExtensions.Kill(Player, DamageType)` takes it.
- `KingsRealm.TaxMaximum` / `TaxMinimum` / `TaxDefault` are static `float` properties.
- `Server.PlayerIsOnline(ulong)` and `PlayerIsOnline(string)`.
- `CodeHatch.Thrones.Capture.PlayerCaptureManager` (an `EntityBehaviour`, read with `player.Entity.TryGet<PlayerCaptureManager>()` [USE `LockPickManager.cs:152`]): fields `bool Captured`, `bool HoldingCaptive`, `Entity Captive`, `ulong CaptivePlayerID`, `int CageID`; properties `Entity Captor`, `ulong CaptorPlayerID`, `CaptureType CurrentType`, `bool InCage`; methods `void Release()`, `void Abandon()`, `void BeCaptured(CaptureType, Entity)`. **[UNVERIFIED]** which player's manager has `Captured == true` (expected: the captive's own) and what `Release()` does when the server calls it (expected: drops a rope/chain bind; cages are `CageCaptureManager` and may need `RemovePrisoner`). CrownAndConsequences relies on this for automatic release after a ransom term; smoke test C8 checks it.

---

## 4. Plugin base classes and libraries

### 4.1 Choosing a base class

| Base class | Use it when | Chat command callback | Source |
|---|---|---|---|
| `ReignOfKingsPlugin : CSharpPlugin` (**recommended** for Realm, since you need `Player`) | You use RoK game types | `[ChatCommand("name")] private void Cmd(Player player, string command, string[] args)` | [SRC] `src/ReignOfKingsPlugin.cs`; `src/Libraries/Command.cs:65` (callback invoked as `CallHook(callback, player, command, args)`) |
| `CovalencePlugin : CSharpPlugin` | You want game-independent code | `[Command("name"), Permission("perm")] private void Cmd(IPlayer player, string command, string[] args)` | [SRC] Oxide.CSharp `src/Covalence/Plugin.cs:13-105` |

Required class shape:

```csharp
namespace Oxide.Plugins
{
    [Info("Title", "Author", "1.0.0")]
    [Description("...")]
    public class Title : ReignOfKingsPlugin { }
}
```

The class name must match the file name. `[Info]` is at Oxide.CSharp `src/CSharpPlugin.cs:27` and `[Description]` at `:77`.

**Rules for `[ChatCommand]` in a `ReignOfKingsPlugin`** [SRC]:
- The method must be a **non-public instance** method, because only `BindingFlags.NonPublic | Instance` is scanned (`src/ReignOfKingsPlugin.cs:49-59`).
- The command name is lower-cased and registered into the game's `CommandManager` as `/name`.
- It can **override a vanilla command**, with a warning. The vanilla command is restored when the plugin unloads (`src/Libraries/Command.cs:73-148, 182-207`).
- Players type `/name arg1 "arg two"`. The core parser supports double-quoted arguments (`src/ReignOfKingsCore.cs:194-251`).

**Members of `ReignOfKingsPlugin`** [SRC `src/ReignOfKingsPlugin.cs`]:

| Member | Behavior | Line |
|---|---|---|
| `PrintToChat(Player player, string format, params object[] args)` | `player.SendMessage(format, args)` | :96 |
| `PrintToChat(string format, params object[] args)` | `Server.BroadcastMessage(format, args)`, only if `Server.PlayerCount >= 1` | :103-109 |
| `SendReply(Player player, string format, params object[] args)` | Same as `PrintToChat(player, ...)` | :117 |
| field `cmd` | The RoK `Command` library | :14 |
| field `rok` | The RoK utility library: `BroadcastChat(name, msg)`, `SendChatMessage(player, name, msg)`, `IdFromPlayer(player)`, `PlayerFromId(string)` (searches `Server.ClientPlayers`), `GetEventSenderId(NetworkEvent)` | :15; `src/Libraries/ReignOfKings.cs:27-82` |
| `[OnlinePlayers]` field support | A `Hash<Player, T>` field kept in sync with online players | :19-47 |

**Inherited from `CSharpPlugin` and `Plugin`** [SRC Oxide.CSharp `src/CSharpPlugin.cs:203-208, 484-560`; Oxide.Core `src/Plugins/Plugin.cs`]:

| Kind | Members |
|---|---|
| Library fields | `permission`, `lang`, `timer`, `webrequest`, `plugins`, `covalence` |
| Logging | `Puts(format, args)`, `PrintWarning(...)`, `PrintError(...)`, `LogToFile(string filename, string text, Plugin plugin, bool datedFilename = true, bool timestampPrefix = false)`. `LogToFile` writes `oxide/logs/<Plugin>/<plugin>_<filename>-yyyy-MM-dd.txt`. |
| Scheduling | `NextTick(Action)`, `NextFrame(Action)`, `QueueWorkerThread(Action<object>)` |
| Info | `Name`, `Title`, `Version` |
| Config | `Config` (`DynamicConfigFile`), `SaveConfig()` |
| Commands | `AddCovalenceCommand(...)` |
| Hooks | `Subscribe(string hook)`, `Unsubscribe(string hook)` |

### 4.2 Sending chat, broadcasting and popups

Color tags in raw RoK chat are `[RRGGBB]`, for example `"[FF0000]Herald[FFFFFF]: text"` [USE `GuildInfo.cs:53`]. Covalence `IPlayer.Message` and `server.Broadcast` convert universal `[#rrggbb]…[/#]` markup into that form [SRC Oxide.Core `src/Libraries/Covalence/Formatter.cs:425-428`; `src/Libraries/Covalence/ReignOfKingsPlayer.cs:269-279`].

| Task | Code | Verified |
|---|---|---|
| To one player | `player.SendMessage("text");` or `SendReply(player, "text");` | [ASM][SRC][USE] |
| Error-styled message | `player.SendError("text");` | [ASM][USE] |
| Broadcast | `Server.BroadcastMessage("text");` or `PrintToChat("text");` | [ASM][SRC] |
| Popup | `player.ShowPopup("Title", "Body");` | [ASM][USE] |

**Watch out for braces.** The `format, params object[]` overloads pass your text as a format string. **[UNVERIFIED]** Whether the game calls `string.Format` when `args` is empty. Until that is tested, send player-supplied text, such as chat or names containing `{`, through the **single-`string` overloads** (`player.SendMessage(string)`, `Server.BroadcastMessage(string)`).

### 4.3 Oxide permissions (not the game's permissions)

[SRC Oxide.Core `src/Libraries/Permission.cs`; ASM Oxide.Core.dll]

```csharp
permission.RegisterPermission("realm.admin", this);                 // :347, call in Init()
bool ok = permission.UserHasPermission(player.Id.ToString(), "realm.admin");  // :535
permission.GrantUserPermission(id, perm, this);                     // :860
permission.UserHasGroup(id, "admin");                               // :744
permission.AddUserGroup(id, group);                                 // :683
```

- User ids are SteamID64 strings. The core only accepts ids with 17 or more digits (`src/ReignOfKingsCore.cs:144-153`).
- Default groups `default` (players) and `admin` are created at server start (`src/ReignOfKingsCore.cs:132-142`; names from Oxide.Core `src/Configuration/OxideConfig.cs:109`). Every player is added to `default` on connect, and players with the game `admin` permission are also added to `admin` (`src/ReignOfKingsHooks.cs:102-114`).
- Admins grant permissions with console commands: `oxide.grant user <name|id> <perm>` and `oxide.grant group admin <perm>` (aliases `o.grant` and `perm.grant`, registered at `src/ReignOfKingsCore.cs:94-98`).

> `player.HasPermission("x")` (the `PlayerExtensions` method) checks the **game's** permission system, not Oxide's. Several old plugins mix the two; for example `GuildInfo.cs` registers Oxide permissions but checks `player.HasPermission`. **Realm plugins should use `permission.UserHasPermission`.**

### 4.4 Lang

[SRC Oxide.Core `src/Libraries/Lang.cs:51, 135`]

```csharp
protected override void LoadDefaultMessages() =>
    lang.RegisterMessages(new Dictionary<string, string> { ["Key"] = "Text {0}" }, this);
string msg = lang.GetMessage("Key", this, player.Id.ToString());
```

### 4.5 Timers

[SRC Oxide.CSharp `src/PluginTimers.cs:74-114`; ASM]

| Call | Effect |
|---|---|
| `timer.Once(float seconds, Action)` | Runs once |
| `timer.In(float seconds, Action)` | Same as `Once` |
| `timer.Every(float interval, Action)` | Repeats forever |
| `timer.Repeat(float interval, int repeats, Action)` | Repeats `repeats` times; 0 means forever [USE `Announcer.cs:108`] |

Each returns an `Oxide.Plugins.Timer` with `Destroy()`, `Reset(...)` and `bool Destroyed`. Timers are destroyed automatically when the plugin unloads. **[UNVERIFIED]** That automatic cleanup is the standard Oxide behavior, but the code was not traced here.

### 4.6 Data files

[SRC Oxide.Core `src/DataFileSystem.cs:99-111`; `src/OxideMod.cs:57, 136, 167`]

```csharp
StoredData data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>("RealmChronicle");
// If the file is missing, ReadObject creates a new StoredData() and writes it out.
Interface.Oxide.DataFileSystem.WriteObject("RealmChronicle", data);
```

- Files are saved to `<server root>/oxide/data/<name>.json`, serialized with Newtonsoft.Json.
- `"Sub/Name"` creates a subfolder.
- Older plugins use `Interface.GetMod().DataFileSystem`, which is the same object [USE `AllianceTracker.cs:137`].

### 4.7 Config

[SRC Oxide.Core `src/Configuration/DynamicConfigFile.cs:49-108`; `src/Plugins/Plugin.cs:375-420`]

Typed pattern:

```csharp
private PluginConfig config;
protected override void LoadDefaultConfig() => Config.WriteObject(new PluginConfig(), true);
private void Init() { config = Config.ReadObject<PluginConfig>(); }
```

- The file is `oxide/config/<PluginName>.json`.
- Old plugins use the untyped form, `Config["key"] = value; SaveConfig();` together with a `GetConfig<T>(name, default)` helper [USE `GuildInfo.cs:213`, `DeclarationOfWar.cs:530`]. Both forms work.

### 4.8 Web requests

[SRC Oxide.Core `src/Libraries/WebRequests.cs:444-506`; ASM]

```csharp
webrequest.Enqueue(url, body, (int code, string response) => { /* runs on main thread */ }, this,
                   RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, 10f);
```

- `RequestMethod` lives in `Oxide.Core.Libraries`.
- `EnqueueGet` and `EnqueuePost` still exist but are marked `[Obsolete]` (`WebRequests.cs:443, 459`). Use `Enqueue`.
- None of the 2018 RoK plugins use webrequest, so there is no [USE] evidence.
- **[UNVERIFIED]** Whether Unity 5.1 Mono's TLS stack can reach modern HTTPS endpoints. For the Realm Chronicle, prefer a **local HTTP** collector such as `http://127.0.0.1:<port>`, or write to a data file that a local process reads.

### 4.9 Online players, ids and names

| Need | Code |
|---|---|
| Online players (RoK) | `foreach (Player p in Server.ClientPlayers) if (!p.IsServer) { ... }` [ASM][USE `AdminChat.cs:61`] |
| Online players (Covalence) | `covalence.Players.Connected` (`IEnumerable<IPlayer>`) [SRC `src/Libraries/Covalence/ReignOfKingsPlayerManager.cs:83`]. Note that `covalence.Players.Sleeping` returns **null** in RoK (`:89`). |
| Id and name | `player.Id` (ulong SteamID64), `player.Id.ToString()` for Oxide APIs, `player.Name`, `player.DisplayName` [ASM] |
| Lookup | `Server.GetPlayerById(ulong)`, `Server.GetPlayerByName(string)`, `rok.PlayerFromId(string)` |
| Player's guild | `Guild g = player.GetGuild();` [USE `NoFriendlyFire.cs:114`], or `SocialAPI.Get<GuildScheme>().TryGetGuildByMember(player.Id)` [USE `GuildInfo.cs:140-143`] |
| Same guild? | `SocialAPI.Get<GuildScheme>().ShareGuildMembership(a.Id, b.Id)` [ASM] |
| Is king? | `SocialAPI.Get<KingsScheme>().IsKing(player)` [ASM][USE] |

### 4.10 Allowed namespaces and assemblies

- `ReignOfKingsExtension` declares `WhitelistAssemblies` = `Assembly-CSharp, mscorlib, Oxide.Core, System, System.Core, UnityEngine` and `WhitelistNamespaces` = `CodeHatch, Steamworks, System.Collections, System.Security.Cryptography, System.Text, UnityEngine` (`src/ReignOfKingsExtension.cs:63-74`).
- No enforcement code for that list was found in Oxide.CSharp at the pinned commit. A grep for sandbox/whitelist found only an assembly blacklist at `src/CSharpPluginLoader.cs:22`.
- **Still, avoid `System.IO`, `System.Net` and threads.** Use `DataFileSystem`, `webrequest` and `timer` instead, so plugins stay portable to sandboxed builds.
- **Compiler and language level.** Oxide.CSharp at the pinned commit compiles plugins out of process with a downloaded Roslyn-based `Oxide.Compiler` (`src/CompilerService.cs:46-66`), and `CompilerData` defaults to `CompilerLanguageVersion.Preview` (`src/ObjectStream/Data/CompilerData.cs:13`) [SRC]. Modern syntax therefore compiles in practice, but the Realm plugins are held to **C# 3 syntax** (no expression-bodied members, dictionary index initializers, `$""`, `?.`, `nameof`, auto-property initializers, optional/named arguments) so they also build with older Oxide compilers. They are checked by compiling with Roslyn `-langversion:3` against the shipped `Assembly-CSharp.dll`, `Oxide.*.dll` and the .NET 2.0/3.5 reference assemblies (plus a compile-only stub for the two Unity/uLink base classes). The skeleton in section 5 uses C# 6 syntax and is **not** a style model for Realm plugins.
- Target **C# features supported by the Oxide compiler but .NET 3.5 APIs** (`TargetFramework net35` in `src/Oxide.ReignOfKings.csproj`). For example, there is no `Task`, `HashSet` is fine, and `string.Join(string, IEnumerable<string>)` is **not** available.

---

## 5. Minimal plugin skeleton (verified APIs only)

Save as `oxide/plugins/RealmSkeleton.cs`. It was not compiled here; every call it makes is tagged above.

```csharp
using System;
using System.Collections.Generic;
using CodeHatch.Common;                       // PlayerExtensions: SendMessage, GetGuild      [ASM]
using CodeHatch.Engine.Modules.SocialSystem;  // SocialAPI                                    [ASM]
using CodeHatch.Engine.Networking;            // Player, Server                               [ASM]
using CodeHatch.Networking.Events;            // PlayerCaptureEvent                           [ASM]
using CodeHatch.Networking.Events.Players;    // PlayerMessageEvent                           [ASM]
using CodeHatch.Thrones.AncientThrone;        // AncientThroneCaptureEvent                    [ASM]
using CodeHatch.Thrones.SocialSystem;         // Guild, KingsScheme                           [ASM]
using Oxide.Core;                             // Interface                                    [SRC]
using Oxide.Core.Libraries;                   // RequestMethod                                [SRC]

namespace Oxide.Plugins
{
    [Info("RealmSkeleton", "Realm", "0.1.0")]
    [Description("Minimal Realm plugin skeleton built only on verified Oxide/RoK APIs")]
    public class RealmSkeleton : ReignOfKingsPlugin
    {
        private const string PermAdmin = "realmskeleton.admin";
        private const int MaxChronicleLines = 500;

        private PluginConfig config;
        private StoredData data;

        private class PluginConfig
        {
            public float SaveIntervalSeconds = 300f;
            public string ChronicleUrl = "";          // empty = disabled; e.g. http://127.0.0.1:8787/chronicle
        }

        private class StoredData
        {
            public List<string> Chronicle = new List<string>();
        }

        #region Lifecycle

        protected override void LoadDefaultConfig() => Config.WriteObject(new PluginConfig(), true);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Welcome"] = "[C8A050]Herald[FFFFFF]: Welcome to the realm.",
                ["Status"] = "House: {0} | Crown: {1}",
                ["NoHouse"] = "none",
                ["NoKing"] = "vacant",
                ["NoPermission"] = "You may not do that.",
                ["Saved"] = "Chronicle saved."
            }, this);
        }

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            timer.Every(config.SaveIntervalSeconds, SaveData);
        }

        private void OnServerSave() => SaveData();

        private void Unload() => SaveData();

        #endregion

        #region Hooks

        private void OnPlayerConnected(Player player)
        {
            if (player == null || player.IsServer) return;
            player.SendMessage(Msg("Welcome", player));
        }

        private object OnPlayerChat(PlayerMessageEvent evt)
        {
            return null;                              // null = allow; non-null = core cancels the message
        }

        private void OnThroneCaptured(AncientThroneCaptureEvent evt)
        {
            if (evt == null || evt.Cancelled || evt.Player == null) return;
            if (evt.State != AncientThroneCaptureEvent.States.Completed) return;   // see 2.6 [UNVERIFIED]
            Guild house = evt.Player.GetGuild();
            AddChronicle("CROWNED " + evt.Player.Name + " of " + (house != null ? house.Name : "no house"));
        }

        private void OnThroneReleased(AncientThroneReleaseEvent evt)
        {
            if (evt == null || evt.Sender == null) return;
            AddChronicle("THRONE_RELEASED " + evt.Sender.Name + (evt.IsDeath ? " (death)" : ""));
        }

        private object OnPlayerCapture(PlayerCaptureEvent evt)
        {
            if (evt == null || evt.Cancelled) return null;
            if (evt.Captor == null || !evt.Captor.IsPlayer || evt.Target == null) return null;
            Player captor = evt.Captor.Owner;
            if (captor == null) return null;
            // Example rule hook-point: block captures between members of the same house.
            if (SocialAPI.Get<GuildScheme>().ShareGuildMembership(captor.Id, evt.Target.Id))
            {
                evt.Cancel("Same house");
                return true;                          // RB 1: also stop the rest of the game handler
            }
            AddChronicle("CAPTURE " + captor.Name + " took " + evt.Target.Name + " (" + evt.Type + ")");
            return null;
        }

        #endregion

        #region Commands

        [ChatCommand("realm")]
        private void CmdRealm(Player player, string command, string[] args)
        {
            if (args.Length > 0 && args[0] == "save")
            {
                if (!permission.UserHasPermission(player.Id.ToString(), PermAdmin))
                {
                    player.SendError(Msg("NoPermission", player));
                    return;
                }
                SaveData();
                player.SendMessage(Msg("Saved", player));
                return;
            }

            Guild house = player.GetGuild();
            KingsScheme crown = SocialAPI.Get<KingsScheme>();
            string houseName = house != null ? house.Name : Msg("NoHouse", player);
            string kingName = crown.HasKing() ? crown.GetKingName() : Msg("NoKing", player);
            player.SendMessage(string.Format(Msg("Status", player), houseName, kingName));
        }

        #endregion

        #region Helpers

        private string Msg(string key, Player player) => lang.GetMessage(key, this, player.Id.ToString());

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, data);

        private void AddChronicle(string line)
        {
            string entry = DateTime.UtcNow.ToString("o") + " " + line;
            data.Chronicle.Add(entry);
            if (data.Chronicle.Count > MaxChronicleLines) data.Chronicle.RemoveAt(0);
            Puts(entry);

            if (string.IsNullOrEmpty(config.ChronicleUrl)) return;
            string body = "{\"line\":\"" + JsonEscape(entry) + "\"}";
            webrequest.Enqueue(config.ChronicleUrl, body, (code, response) =>
            {
                if (code != 200) PrintWarning("Chronicle POST failed: " + code);
            }, this, RequestMethod.POST, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, 5f);
        }

        private static string JsonEscape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

        #endregion
    }
}
```

Points to check on the first real compile:
- `AncientThroneCaptureEvent.States` is a nested enum. Metadata shows its members `Capturing`, `Cancelled` and `Completed`; plugins reference it exactly this way [USE `LevelSystem.cs:2018`].
- `evt.Sender` on `AncientThroneReleaseEvent` comes from `NetworkEvent` [ASM][USE `RaidBoss.cs:626`].
- `string.Format` is used only on our own lang strings, never on player text, so braces in names are safe.

---

## 6. Running and testing locally (Windows, local only)

1. Install the 2.0.3867 zip by **copying `ROK_Data/Managed/*` over the server's `<Data>/Managed`**. Back up the original `Assembly-CSharp.dll` first.
2. **Check which data folder the owner's server has.** The release, `Steam.ps1` (`ManagedDir` = `ROK_Data/Managed`) and `_start-example.bat` (`ROK.exe -batchmode -nographics -silentcrash`) all assume `ROK.exe` and `ROK_Data`. The project brief says the owner's install has `Server.exe` in `G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server`. **[UNVERIFIED]** Whether that install has `ROK_Data\Managed` or `Server_Data\Managed`. `Oxide.ReignOfKings.csproj` lists `GameExe` = `ROK.exe;Server.exe`, so both names have existed. Copy into whichever `*_Data\Managed` sits next to the exe that is launched.
3. **Check the build match.** **[UNVERIFIED]** That the bundled patched `Assembly-CSharp.dll` was built from the same server build as the owner's (server build 4088734). The DLL holds no build-id string to compare. After installing, the server console should show `Oxide.ReignOfKings 2.0.3867` in its status line (`src/ReignOfKingsExtension.cs:285`), and `oxide.version` or `o.version` in the console should answer. If the server crashes or hooks do not fire, restore the backup.
4. Oxide creates folders under the working directory, or under `-oxide.directory` if that argument is given (`Oxide.Core src/OxideMod.cs:125-149`):
   - `oxide\plugins` (drop `.cs` files here; they hot-reload)
   - `oxide\config`
   - `oxide\data`
   - `oxide\lang`
   - `oxide\logs`
5. Useful console commands (`src/ReignOfKingsCore.cs:88-103`):
   - `oxide.plugins`
   - `oxide.reload <Name>`
   - `oxide.load <Name>` / `oxide.unload <Name>`
   - `oxide.grant user <name|id> <perm>`
   - `oxide.show perms`

   Each also has `o.` and `plugin.`/`perm.` aliases.

---

## 7. Gotchas

- **Blocking**: use `evt.Cancel(...)`, and also return non-null on RB-1 hooks. RB-0 hooks ignore the return value (section 1.1).
- **Server pseudo-player** (`9999999999`) shows up in direct game hooks and in `Server.AllPlayers`. Filter it with `IsServer`.
- **Game vs Oxide permissions**: `player.HasPermission` is the game's system; `permission.UserHasPermission` is Oxide's.
- **`OnUserDisconnected` reason** is always "Unknown".
- **`covalence.Players.Sleeping`** is `null` in RoK.
- **`IOnChatCommand`** is injected but has no handler, so it is a no-op.
- **Hot reload**: `OnServerInitialized` is re-sent to plugins loaded after startup (`src/ReignOfKingsCore.cs:112-120`). Put one-time setup there and make it idempotent.
- **`[ChatCommand]` methods must be private or protected.** Public ones are silently ignored by `ReignOfKingsPlugin`.
- **.NET 3.5 API surface** (section 4.10).
- **Old plugin signatures**: they used `OnPlayerChat(PlayerEvent e)`. That still binds because `PlayerMessageEvent : PlayerEvent` (base-type matching), but declare `PlayerMessageEvent` in new code.

---

## 8. Hook-to-feature map for Realm

| Realm feature | Hooks and APIs available |
|---|---|
| Player houses, sworn allegiance | `OnGuildInvite`, `OnGuildInvitationAccept`, `OnGuildAbandon`, `OnGuildBanish` (all cancellable via RB 1 and `Cancel`). Membership comes from `GuildScheme` and `Guild.Members()`. |
| Contested crown | `OnThroneCapture` (gate attempts, e.g. only inside rebellion windows), `OnThroneCaptured`, `OnThroneReleased`, `OnKingDeath`, `OnThroneRename`, `OnThroneTax`. State comes from `KingsScheme` (`GetKingID`, `GetTax`/`SetTax`, `GetRealm`). |
| Decrees and council | `[ChatCommand]` gated by Oxide permissions and `KingsScheme.IsKing(player)`. Persist in data files. Use `ShowConfirmPopup`/`ShowInputPopup` for votes [ASM][USE `AllianceTracker.cs:191`]. |
| Declared rebellions in windows | `timer.Every` plus a schedule in config. Gate `OnThroneCapture`, `OnEntityHealthChange` and `OnCubeTakeDamage` with `Cancel()` outside windows. Prior art: `DeclarationOfWar.cs`, `WarTime.cs` in the plugin corpus. |
| Bounded ransom | `OnPlayerCapture` (captor, target, `CaptureType`), `OnPlayerEscape`, `OnPlayerRelease`. Timers enforce the maximum hold. |
| Realm Chronicle | Append to `oxide/data`, then POST to a local collector with `webrequest.Enqueue` or let a local process tail the data or log file (`LogToFile`). |

---

## 9. Real plugins consulted

All files are in `https://github.com/RustServers-IO/oxideplugins/tree/master/reign%20of%20kings` @ `4de21d4`:

- `GuildInfo.cs`: guild lookup, lang, config, popups
- `LevelSystem.cs`: throne and capture gating
- `RaidBoss.cs`: `OnThroneCaptured`, `OnThroneReleased`, `KingsScheme`
- `NoFriendlyFire.cs`: damage cancel and same-guild check
- `AllianceTracker.cs`: data files, popups, `GuildScheme`
- `DeclarationOfWar.cs`: timers, config
- `OOC.cs`: chat subtypes
- `LockPickManager.cs`, `AntiLoot.cs`, `DeathMessages.cs`, `Announcer.cs`, `AdminChat.cs`

Usage counts across the 66 C# files:
- `[ChatCommand` appears 260 times (52 files).
- `PrintToChat(` appears 1,408 times.
- `OnEntityHealthChange(EntityDamageEvent)` appears in 22 files.
- No file uses `CovalencePlugin` or `webrequest`.

---

## 10. Open items to verify on the owner's machine

1. Which data folder (`ROK_Data` or `Server_Data`) exists next to `Server.exe` (section 6.2).
2. That the bundled patched `Assembly-CSharp.dll` matches server build 4088734 and the server boots with Oxide (section 6.3).
3. Whether the server needs an `Assembly-CSharp_Original.dll` (the vanilla copy) for the `CalculateOriginalHash` patch (section 2.9), and how EAC on the server reacts.
4. Exact firing conditions of `OnThroneCaptured` (which `State`), `OnKingDeath`, `OnKingSleeperDeath`, and `OnPlayerEscape` vs `OnPlayerRelease`.
5. Whether `Cancel()` alone blocks each action for each hook, especially the RB-0 cube hooks and the guild hooks.
6. Whether the `format, params object[]` chat overloads call `string.Format` when given no args (brace safety).
7. Whether `Server.ClientPlayers` excludes the server pseudo-player.
8. Whether `webrequest` HTTPS works under Unity 5.1 Mono; local HTTP is assumed.
9. The default parameter values of `ShowPopup`, `ShowConfirmPopup` and `ShowInputPopup`, and the `OnSubmit` delegate signature.
