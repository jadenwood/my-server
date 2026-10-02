'use strict';

// World save slots, emulated from the decompiled game ([DEC] CodeHatch.Engine.Gaming.GameSlotManager,
// DedicatedServerBypass.StartServer, Game.New / Game.Load, Assembly-CSharp of Oxide.ReignOfKings
// 2.0.3867). docs/worlds.md has the whole story; in short:
//
// - Each world lives in <saveLocation>/Slot<N> ("Slot{0}" is scene data; the folder names Saves\Slot3,
//   Saves\Slot6, ... were seen on the owner's server on 2026-10-02).
// - A running server holds <slot>/Session.lock open with File.OpenWrite (FileShare.None). Another
//   process that opens it fails; that is how SlotIsLocked works. The file stays behind after a crash,
//   but the handle dies with the process, so a crashed server does not leave its world locked.
// - StartServer: if worldSlot >= 0 and that slot is locked, the game logs "Could not load world N.
//   Loading new world instead." and sets worldSlot to -1. Then: worldSlot < 0, or no such folder ->
//   Game.New (a slot below 0 becomes the first free number: GetNextAvailableSlot); otherwise Game.Load.
//   Both lock the slot (creating its folder) and log "Save slot located at: <path>" before the world
//   loads, so a start that fails later (busy game port) can leave a new, empty slot folder behind.
//
// Linux has no Windows share modes, so the simulator writes "rok-sim lock pid=<pid> token=<t>" into
// Session.lock and treats a slot as locked while that process (or that in-process RokSim) is alive.
// The real game's Session.lock is empty. lockHeld() is exported so launcher tests can use the same rule.

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const LOCK_FILE = 'Session.lock';
const SLOT_RE = /^Slot(\d+)$/;
const LOCK_RE = /^rok-sim lock pid=(\d+) token=([0-9a-f]+)/;

// Tokens of RokSim instances in THIS process that hold a slot (tests run several in one process).
const LIVE = new Set();

function slotName(n) {
  return `Slot${n}`;
}

function isAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (e) {
    return e.code === 'EPERM';
  }
}

// True while the simulator process (or in-process RokSim) that wrote this lock file is running.
function lockHeld(file) {
  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch {
    return false;
  }
  const m = LOCK_RE.exec(text);
  if (!m) return false;
  const pid = Number(m[1]);
  if (pid === process.pid) return LIVE.has(m[2]);
  return isAlive(pid);
}

class SlotManager {
  // saveRoot: absolute path of saveLocation (relative to the server folder in the game).
  constructor(saveRoot) {
    this.saveRoot = saveRoot;
    this.current = -1;
    this.token = null;
  }

  slotPath(n) {
    return n < 0 ? this.saveRoot : path.join(this.saveRoot, slotName(n));
  }

  exists(n) {
    try {
      return fs.statSync(this.slotPath(n)).isDirectory();
    } catch {
      return false;
    }
  }

  nextAvailable() {
    let i = 0;
    while (this.exists(i)) i++;
    return i;
  }

  isLocked(n) {
    const dir = this.slotPath(n);
    return this.exists(n) && lockHeld(path.join(dir, LOCK_FILE));
  }

  // LockSlot: creates the folder and holds Session.lock.
  lock(n) {
    if (n < 0) return false;
    const dir = this.slotPath(n);
    fs.mkdirSync(dir, { recursive: true });
    if (this.token) this.unlock();
    this.token = crypto.randomBytes(6).toString('hex');
    LIVE.add(this.token);
    fs.writeFileSync(path.join(dir, LOCK_FILE), `rok-sim lock pid=${process.pid} token=${this.token}\n`);
    this.current = n;
    return true;
  }

  // UnlockCurrentSlot: closes the handle and deletes Session.lock (a clean exit).
  unlock() {
    if (!this.token) return false;
    LIVE.delete(this.token);
    this.token = null;
    try {
      fs.unlinkSync(path.join(this.slotPath(this.current), LOCK_FILE));
    } catch {
      /* already gone */
    }
    this.current = -1;
    return true;
  }

  // A crash: the handle dies with the process, the file stays.
  release() {
    if (this.token) LIVE.delete(this.token);
    this.token = null;
  }

  list() {
    let names = [];
    try {
      names = fs.readdirSync(this.saveRoot);
    } catch {
      return [];
    }
    return names.map((n) => SLOT_RE.exec(n)).filter(Boolean).map((m) => Number(m[1])).sort((a, b) => a - b);
  }
}

module.exports = { SlotManager, lockHeld, slotName, LOCK_FILE, SLOT_RE };
