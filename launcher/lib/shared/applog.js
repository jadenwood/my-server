'use strict';

// Plain-text app log: <userData>\logs\realm-YYYY-MM-DD.log. Keeps 14 days. Also turns uncaught
// errors in the main process into logged, non-fatal events instead of Electron's crash dialog.
const fs = require('fs');
const path = require('path');

let dir = null;
const KEEP_DAYS = 14;

function day(d = new Date()) {
  return d.toISOString().slice(0, 10);
}

function init(logDir) {
  dir = logDir;
  try {
    fs.mkdirSync(dir, { recursive: true });
    const cutoff = Date.now() - KEEP_DAYS * 86400000;
    for (const n of fs.readdirSync(dir)) {
      if (!/^(realm|server-s[1-4])-\d{4}-\d{2}-\d{2}\.log$/.test(n)) continue;
      const p = path.join(dir, n);
      if (fs.statSync(p).mtimeMs < cutoff) fs.rmSync(p, { force: true });
    }
  } catch {
    /* logging must never break the app */
  }
  return dir;
}

function append(file, text) {
  if (!dir) return;
  try {
    fs.appendFileSync(path.join(dir, `${file}-${day()}.log`), text);
  } catch {
    /* disk full or locked: ignore */
  }
}

function write(level, msg) {
  const line = `${new Date().toISOString()} ${level.toUpperCase().padEnd(5)} ${String(msg).replace(/\r?\n/g, '\n    ')}\n`;
  append('realm', line);
}

// Server console lines go to their own file per instance so a session can be read afterwards.
function serverLine(id, line) {
  const src = line && typeof line === 'object' ? line.src || 'out' : 'out';
  const text = line && typeof line === 'object' ? line.text : line;
  append(`server-${id}`, `${new Date().toISOString()} [${src}] ${text}\n`);
}

function describe(err) {
  if (err instanceof Error) return err.stack || `${err.name}: ${err.message}`;
  return String(err);
}

function installCrashGuards(onError) {
  process.on('uncaughtException', (err) => {
    write('error', `Uncaught exception: ${describe(err)}`);
    if (onError) onError(err);
  });
  process.on('unhandledRejection', (reason) => {
    write('error', `Unhandled rejection: ${describe(reason)}`);
    if (onError) onError(reason);
  });
}

module.exports = { init, write, serverLine, installCrashGuards, logDir: () => dir };
