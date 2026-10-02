'use strict';

// The dedicated server's two log files under <server>\Logs:
//  - realm-server.log: Unity's -logFile (engine output; only when the launcher starts ROK.exe).
//  - Log[yyMMdd-hhmmss].txt: the game's own log ([DEC] Logger.CreateNewLogFile, LogToFile = true
//    by default). Logger.LogWithStacktrace writes game messages here, not to Unity's log, so the
//    "Server for N players started on port P." line only ever shows up in this file.
// "hh" is a 12-hour clock, so file names do not sort by time; use mtime instead.
const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');

const UNITY_LOG = 'realm-server.log';
const GAME_LOG_RE = /^Log\d{6}-\d{6}\.txt$/i;

function logsDir(root) {
  return path.join(root, 'Logs');
}

// Whether a file name in Logs is one of the two server logs.
function isServerLog(name) {
  return name === UNITY_LOG || GAME_LOG_RE.test(name);
}

// Every server log file currently in <root>\Logs (synchronous; for the snapshot before a start).
// Returns [{ path, name, kind: 'unity'|'game', size, mtimeMs }].
function serverLogCandidates(root) {
  const dir = logsDir(root);
  const out = [];
  let names = [];
  try {
    names = fs.readdirSync(dir);
  } catch {
    return out;
  }
  for (const name of names) {
    if (!isServerLog(name)) continue;
    try {
      const st = fs.statSync(path.join(dir, name));
      if (st.isFile()) out.push({ path: path.join(dir, name), name, kind: name === UNITY_LOG ? 'unity' : 'game', size: st.size, mtimeMs: st.mtimeMs });
    } catch {
      /* removed between readdir and stat */
    }
  }
  return out;
}

// The server logs worth following: Unity's log and every game log written since sinceMs
// (a game log rotated mid-run keeps its unread tail, so older ones are not dropped).
// Returns { unity: entry|null, game: [entry, ...] oldest first, newestGame: entry|null }.
async function resolveLogs(root, sinceMs = 0) {
  const dir = logsDir(root);
  const res = { unity: null, game: [], newestGame: null };
  let names = [];
  try {
    names = await fsp.readdir(dir);
  } catch {
    return res;
  }
  for (const name of names) {
    if (!isServerLog(name)) continue;
    let st;
    try {
      st = await fsp.stat(path.join(dir, name));
    } catch {
      continue;
    }
    if (!st.isFile()) continue;
    const e = { path: path.join(dir, name), name, kind: name === UNITY_LOG ? 'unity' : 'game', size: st.size, mtimeMs: st.mtimeMs };
    if (e.kind === 'unity') res.unity = e;
    else if (st.mtimeMs >= sinceMs) res.game.push(e);
  }
  res.game.sort((a, b) => a.mtimeMs - b.mtimeMs || a.name.localeCompare(b.name));
  res.newestGame = res.game.length ? res.game[res.game.length - 1] : null;
  return res;
}

// Reads up to maxBytes from file starting at offset. A file shorter than offset was truncated or
// replaced, so reading restarts at 0 (reset: true). Returns { buf, next, size, reset }.
async function readFrom(file, offset = 0, maxBytes = 1024 * 1024) {
  const fh = await fsp.open(file, 'r');
  try {
    const { size } = await fh.stat();
    let from = offset;
    let reset = false;
    if (size < from) {
      from = 0;
      reset = true;
    }
    const len = Math.min(size - from, maxBytes);
    if (len <= 0) return { buf: Buffer.alloc(0), next: from, size, reset };
    const buf = Buffer.alloc(len);
    const { bytesRead } = await fh.read(buf, 0, len, from);
    return { buf: buf.subarray(0, bytesRead), next: from + bytesRead, size, reset };
  } finally {
    await fh.close();
  }
}

module.exports = { UNITY_LOG, GAME_LOG_RE, logsDir, isServerLog, serverLogCandidates, resolveLogs, readFrom };
