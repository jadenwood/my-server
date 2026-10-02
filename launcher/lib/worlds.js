'use strict';

// World continuity: Steward always starts the same world, and never lets a start make a new one
// without the owner saying so. The reasoning and the evidence are in docs/worlds.md. In short
// ([DEC] = read in the decompiled Assembly-CSharp of Oxide.ReignOfKings 2.0.3867):
//
// - Worlds live in <server>\<saveLocation>\Slot<N> (saveLocation 'Saves/' by default). The folder
//   names Saves\Slot3, Slot6, ... were seen on the owner's server.
// - ServerSettings.cfg worldSlot picks the world. -1 (and a slot number with no folder) makes the
//   game create a new world; -1 takes the first free number [DEC Game.New, GetNextAvailableSlot].
// - A running server holds <slot>\Session.lock open exclusively. If worldSlot names a slot whose lock
//   is held, the game logs "Could not load world N. Loading new world instead.", writes worldSlot =
//   -1 into the file and makes a new world [DEC DedicatedServerBypass.StartServer]. That is how the
//   owner's failed starts (a second server from the same folder still running) made Slot3, 6, 8, 9
//   and 10, and why every later start would have made yet another world.
// - Once the world has loaded the game writes the slot it runs back into worldSlot
//   [DEC DedicatedServerBypass.OnGameStart], just before "Game has started.".
//
// Steward remembers, per server, the slot that last reached "Game has started." under it. Before each
// start it reads worldSlot and the slot folders and decides (planWorld): load, put the remembered
// slot back (pin), or stop and ask the owner. Nothing here deletes or moves a save.

const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const S = require('./safety');

const SLOT_DIR_RE = /^Slot(\d+)$/i;
const LOCK_FILE = 'Session.lock';
// A slot whose folder holds no more than this (besides Session.lock) never had a world saved in it:
// failed starts leave only the slot's small info file. UNVERIFIED: the real save layout was not
// examined; a saved world is assumed to hold more files or more bytes than this.
const EMPTY_MAX_FILES = 1;
const EMPTY_MAX_BYTES = 16 * 1024;
const MAX_SLOT = 9999;

// Is Session.lock held by a running server? On Windows the game holds it with FileShare.None, so
// opening it fails with EBUSY (the owner's backup crash "EBUSY ... Session.lock" proved that). The
// file stays behind after a crash but is then not held. Elsewhere there are no share modes and this
// always says false (tests pass their own probe). Steward opens it for a moment only, before a start.
async function defaultLockProbe(file) {
  let fh;
  try {
    fh = await fsp.open(file, 'r');
    return false;
  } catch (e) {
    return e.code === 'EBUSY' || e.code === 'EPERM' || e.code === 'EACCES';
  } finally {
    if (fh) await fh.close().catch(() => {});
  }
}

function saveRootOf(root, saveLocation) {
  const loc = String(saveLocation == null || saveLocation === '' ? 'Saves/' : saveLocation);
  // The game resolves it against its working folder (the server folder).
  return path.isAbsolute(loc) || /^[A-Za-z]:[\\/]/.test(loc) ? path.resolve(loc) : path.resolve(root, loc);
}

async function scanSlot(dir) {
  let files = 0;
  let bytes = 0;
  let newestMs = 0;
  let hasLock = false;
  const walk = async (d, depth) => {
    let entries = [];
    try {
      entries = await fsp.readdir(d, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const p = path.join(d, e.name);
      if (depth === 0 && e.name.toLowerCase() === LOCK_FILE.toLowerCase()) {
        hasLock = true;
        continue;
      }
      if (e.isDirectory()) {
        files++;
        if (depth < 3) await walk(p, depth + 1);
        continue;
      }
      try {
        const st = await fsp.stat(p);
        files++;
        bytes += st.size;
        if (st.mtimeMs > newestMs) newestMs = st.mtimeMs;
      } catch {
        /* locked or gone */
      }
    }
  };
  await walk(dir, 0);
  return { files, bytes, newestMs, hasLock, empty: files <= EMPTY_MAX_FILES && bytes <= EMPTY_MAX_BYTES };
}

// Reads worldSlot / saveLocation / allowSaving and every Slot<N> folder.
async function readWorlds(root, { probeLock = defaultLockProbe } = {}) {
  const cfgFile = path.join(root, 'Configuration', 'ServerSettings.cfg');
  let text = null;
  try {
    text = S.decodeText(await fsp.readFile(cfgFile)).text;
  } catch {
    /* first run: the game writes it */
  }
  const get = (k) => (text == null ? null : S.getCfgValue(text, k));
  const rawSlot = get('worldSlot');
  const cfgSlot = rawSlot != null && /^-?\d+$/.test(rawSlot.trim()) ? Number(rawSlot.trim()) : null;
  const saveLocation = get('saveLocation') || 'Saves/';
  const allowSaving = !/^false$/i.test(String(get('allowSaving') || 'True').trim());
  const saveRoot = saveRootOf(root, saveLocation);
  const slots = [];
  let names = [];
  try {
    names = await fsp.readdir(saveRoot, { withFileTypes: true });
  } catch {
    /* no Saves folder yet */
  }
  for (const e of names) {
    const m = SLOT_DIR_RE.exec(e.name);
    if (!m || !e.isDirectory()) continue;
    const n = Number(m[1]);
    if (n > MAX_SLOT) continue;
    const dir = path.join(saveRoot, e.name);
    const info = await scanSlot(dir);
    const locked = info.hasLock ? await probeLock(path.join(dir, LOCK_FILE)) : false;
    slots.push({ slot: n, dir, ...info, locked });
  }
  slots.sort((a, b) => a.slot - b.slot);
  return { cfgExists: text != null, cfgFile, cfgSlot, rawSlot, saveLocation, saveRoot, allowSaving, slots };
}

// The slot the owner most likely means when Steward has nothing remembered: the non-empty world
// saved most recently, then the biggest.
function suggestSlot(slots) {
  const real = (slots || []).filter((s) => !s.empty);
  if (!real.length) return null;
  return real.slice().sort((a, b) => b.newestMs - a.newestMs || b.bytes - a.bytes || a.slot - b.slot)[0].slot;
}

// Decides what a start would do with the worlds, and what Steward does about it.
// w: readWorlds() result. remembered: the slot that last reached "Game has started." under Steward
// (null when unknown). Returns { action, slot, message, notes[], choices[], suggest }:
//   'load'     the game loads `slot` as configured
//   'pin'      Steward writes worldSlot = `slot` first (the file said -1, or a slot with no world)
//   'new'      a new world, and that is fine: there is no world here yet
//   'choose'   stop: the start would make a new world, or two worlds compete. The owner picks.
//   'blocked'  stop: the world is open in another running server (its Session.lock is held)
function planWorld(w, remembered = null) {
  const slots = (w && w.slots) || [];
  const by = new Map(slots.map((s) => [s.slot, s]));
  const cfg = w && Number.isInteger(w.cfgSlot) ? w.cfgSlot : -1;
  const notes = [];
  const real = slots.filter((s) => !s.empty);
  const choicesFor = () => real.map((s) => ({ slot: s.slot, newestMs: s.newestMs, bytes: s.bytes, remembered: s.slot === remembered }));
  const suggest = remembered != null && by.has(remembered) ? remembered : suggestSlot(slots);
  const base = { choices: choicesFor(), suggest, cfgSlot: cfg, remembered };
  if (!w || !w.cfgExists) return { ...base, action: 'new', slot: null, notes, message: 'First start: the game creates its settings and a world.' };
  const blockedBy = (n) => ({
    ...base,
    action: 'blocked',
    slot: n,
    notes,
    message: `World ${n} is open in another running server (Saves\\Slot${n}\\Session.lock is held). Starting now would make the game skip it and create a NEW world. Close the other server first (Stop it cleanly, or Adopt it).`
  });

  if (remembered != null && by.has(remembered)) {
    const target = by.get(remembered);
    if (target.locked) return blockedBy(remembered);
    if (cfg === remembered) return { ...base, action: 'load', slot: remembered, notes, message: `Loads world ${remembered}, as last time.` };
    const other = by.get(cfg);
    if (cfg >= 0 && other && !other.empty) {
      return {
        ...base,
        action: 'choose',
        slot: null,
        notes,
        message: `ServerSettings.cfg now says world ${cfg}, but this server last ran world ${remembered} under Realm. Choose which world to start.`
      };
    }
    notes.push(cfg < 0 ? `worldSlot was -1, which would have created a new world` : `worldSlot was ${cfg}, a slot with no saved world`);
    return { ...base, action: 'pin', slot: remembered, notes, message: `Puts world ${remembered} back (${notes[0]}).` };
  }
  if (remembered != null) notes.push(`world ${remembered}, which this server last ran, is no longer in ${w.saveLocation}`);

  if (cfg >= 0 && by.has(cfg)) {
    const s = by.get(cfg);
    if (s.locked) return blockedBy(cfg);
    if (!s.empty) return { ...base, action: 'load', slot: cfg, notes, message: `Loads world ${cfg}.` };
  }
  if (!real.length && remembered == null) {
    return { ...base, action: 'new', slot: null, notes, message: cfg >= 0 && by.has(cfg) ? `Loads world ${cfg} (it has no saved world yet).` : 'There is no world yet: the game creates the first one.' };
  }
  const what = cfg < 0 ? 'worldSlot is -1, so the game would create a NEW world' : by.has(cfg) ? `worldSlot is ${cfg}, a slot with no saved world in it` : `worldSlot is ${cfg}, and there is no Slot${cfg} folder, so the game would create a NEW world there`;
  return {
    ...base,
    action: 'choose',
    slot: null,
    notes,
    message: `${what}${notes.length ? ` (${notes.join('; ')})` : ''}. ${real.length ? `Saved worlds here: ${real.map((x) => x.slot).join(', ')}.` : 'There is no other saved world here.'} Choose a world, or start a new one on purpose.`
  };
}

// Writes worldSlot. Only an existing key is rewritten (the game always writes it); the previous file
// is backed up (lib/realm.js applyCfg). slot -1 asks the game for a new world.
async function setWorldSlot(root, slot, applyCfg) {
  if (!Number.isInteger(slot) || slot < -1 || slot > MAX_SLOT) throw new RangeError('world slot must be -1 to 9999');
  const cfg = path.join(root, 'Configuration', 'ServerSettings.cfg');
  const backupDir = path.join(root, '_realm-backups', 'config');
  return applyCfg(cfg, { worldSlot: String(slot) }, backupDir);
}

// The slot a running server reports through ServerSettings.cfg once "Game has started." (the game
// writes it just before). Returns a number or null.
async function runningSlot(root) {
  try {
    const { text } = S.decodeText(await fsp.readFile(path.join(root, 'Configuration', 'ServerSettings.cfg')));
    const v = S.getCfgValue(text, 'worldSlot');
    return v != null && /^\d+$/.test(v.trim()) ? Number(v.trim()) : null;
  } catch {
    return null;
  }
}

// The world lock of THIS folder: is any Slot<N>\Session.lock held right now? Used by lib/prestart.js
// to tell a server running from this folder apart from the game client.
async function heldSlots(root, { probeLock = defaultLockProbe } = {}) {
  const w = await readWorlds(root, { probeLock });
  return w.slots.filter((s) => s.locked).map((s) => s.slot);
}

// Synchronous existence check for tests and callers that only need the folder.
function slotDir(root, saveLocation, slot) {
  return path.join(saveRootOf(root, saveLocation), `Slot${slot}`);
}

function slotExistsSync(root, saveLocation, slot) {
  try {
    return fs.statSync(slotDir(root, saveLocation, slot)).isDirectory();
  } catch {
    return false;
  }
}

module.exports = { readWorlds, planWorld, suggestSlot, setWorldSlot, runningSlot, heldSlots, defaultLockProbe, saveRootOf, slotDir, slotExistsSync, SLOT_DIR_RE, LOCK_FILE, MAX_SLOT };
