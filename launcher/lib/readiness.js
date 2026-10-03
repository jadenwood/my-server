'use strict';

// When is a server ready, and which log file is its own? Pure helpers for lib/server-process.js.
//
// Proven on the owner's real server (2026-10-02, docs/HANDOFF.md):
// - The server is ready to accept players only after "Server for N players started on port P."
//   and then "Game has started.". Unity's "Initialize engine version:" comes long before the world
//   has loaded, so it is not used any more.
// - Those lines are in the game's own log, <server>\Logs\Log[yyMMdd-hhmmss].txt, which the game
//   creates at each start. The -logFile file (Logs\realm-server.log) only gets Unity's lines.
// [DEC] "Server for {0} players started on port {1}." is CoreServer.Start (the network listener is
// open); "Game has started." is Game.OnLoadingComplete, after the world slot is written back to
// ServerSettings.cfg (DedicatedServerBypass.OnGameStart) and the world is saved once.
//
// Log records look like "[yyMMdd-HHmmss] [Info]   text"; over the admin console they arrive as
// "[I] text" and Steward logs the text alone. Both shapes are accepted. Player chat comes in as
// "[C] Name: text", so a player typing "Game has started." does not match.

const LISTENING_RE = /(?:^|\]\s*)Server for (\d+) players started on port (\d+)\.?\s*$/;
const GAME_STARTED_RE = /(?:^|\]\s*)Game has started\.\s*$/;
// [DEC] Game.New / Game.Load: "Save slot located at: " + <saveLocation>\Slot<N>.
const SAVE_SLOT_RE = /(?:^|\]\s*)Save slot located at:\s*(.*?)\s*$/;
// [DEC] DedicatedServerBypass.StartServer, when the slot's Session.lock is held by another process.
const WORLD_IN_USE_RE = /Could not load world (-?\d+)\. Loading new world instead\./;
// The game's own log file name ([DEC] Logger.CreateNewLogFile; "hh" is a 12-hour clock, so the
// names do not sort by time across noon).
const GAME_LOG_NAME_RE = /^Log\[(\d{6})-(\d{6})\]\.txt$/i;

function slotFromPath(p) {
  const m = /(?:^|[\\/])Slot(\d+)[\\/]*$/i.exec(String(p || ''));
  return m ? Number(m[1]) : null;
}

// Follows one run's lines. phase: 'loading' -> 'listening' -> 'ready'. Ready needs both lines, in
// that order. feed() returns 'listening', 'ready', 'slot' or 'world-in-use' when something changed.
class ReadyTracker {
  constructor() {
    this.reset();
  }

  reset() {
    this.phase = 'loading';
    this.listening = null;
    this.slot = null;
    this.worldInUse = null;
  }

  get ready() {
    return this.phase === 'ready';
  }

  feed(text) {
    const t = String(text || '');
    if (this.phase === 'loading') {
      const m = LISTENING_RE.exec(t);
      if (m) {
        this.listening = { maxPlayers: Number(m[1]), port: Number(m[2]) };
        this.phase = 'listening';
        return 'listening';
      }
    } else if (this.phase === 'listening' && GAME_STARTED_RE.test(t)) {
      this.phase = 'ready';
      return 'ready';
    }
    const s = SAVE_SLOT_RE.exec(t);
    if (s) {
      const n = slotFromPath(s[1]);
      if (n != null) {
        this.slot = n;
        return 'slot';
      }
    }
    const w = WORLD_IN_USE_RE.exec(t);
    if (w && this.worldInUse == null) {
      this.worldInUse = Number(w[1]);
      return 'world-in-use';
    }
    return null;
  }
}

// Which file in <server>\Logs is this run's game log?
// files: [{ name, birthtimeMs, mtimeMs, size }]; known: names that existed before Steward started
// the server. The newest Log[...].txt that was NOT there before wins, so Steward never follows an
// older run's log (or Unity's realm-server.log). birthtime is the creation time on Windows (NTFS);
// where a file system has none it is 0 and the modification time is used instead.
function pickGameLog(files, { known = new Set(), adopt = false } = {}) {
  let best = null;
  for (const f of files || []) {
    if (!f || !GAME_LOG_NAME_RE.test(f.name)) continue;
    if (!adopt && known.has(f.name)) continue;
    const t = adopt ? f.mtimeMs || 0 : f.birthtimeMs > 0 ? f.birthtimeMs : f.mtimeMs || 0;
    if (!best || t > best.t || (t === best.t && f.name > best.f.name)) best = { f, t };
  }
  return best ? best.f : null;
}

module.exports = { LISTENING_RE, GAME_STARTED_RE, SAVE_SLOT_RE, WORLD_IN_USE_RE, GAME_LOG_NAME_RE, ReadyTracker, pickGameLog, slotFromPath };
