'use strict';

// The two logs a dedicated ROK.exe keeps, emulated from the decompiled Logger class [DEC]:
//
// 1. GameLogger: the game's own log, Logs/Log[yyMMdd-hhmmss].txt in the working folder. The file
//    name uses a 12-hour clock (hh); each record is "[yyMMdd-HHmmss] " + text + "\n" where text is
//    "[Info]   message\r\n" + the current stack trace ("    Type.Method()\r\n" per frame).
//    Exceptions: "[Except] Type: message\r\n    frame\r\n..." trimmed, then "\n".
//    While LogToFile is on (the default) these lines do NOT go to Unity's log.
//    Duplicate suppression (Logger.Log): a counter per message TEMPLATE (the format string, before
//    {0} is filled in). A message is written while its count is below MaxDuplicateLogs (50), and after
//    that only at the counts CanLogWithCount allows (every 5th up to 100, every 20th up to 500, ...).
//    Every 300 s counters at or below 100 are dropped. The same filter decides what the admin console
//    gets, because SocketAdminConsole listens to Logger.InfoLogged and friends.
//
// 2. UnityLog: Unity's player log, written to the -logFile path (or ROK_Data/output_log.txt). It only
//    gets Unity's own lines. The exact Unity 5.1 wording is [SEC]/UNVERIFIED; the launcher only relies
//    on "Initialize engine version:".

const fs = require('fs');
const path = require('path');

const pad = (n, w = 2) => String(n).padStart(w, '0');
function stamp(d, twelveHour) {
  let h = d.getHours();
  if (twelveHour) h = h % 12 === 0 ? 12 : h % 12;
  return `${pad(d.getFullYear() % 100)}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(h)}${pad(d.getMinutes())}${pad(d.getSeconds())}`;
}

const LEVEL_TAG = { debug: '[Debug]  ', info: '[Info]   ', warn: '[Warn]   ', error: '[Error]  ', exception: '[Except] ' };
const CONSOLE_TAG = { debug: '[D]', info: '[I]', warn: '[W]', error: '[E]', exception: '[X]' };

function canLogWithCount(n) {
  return (n <= 100 && n % 5 === 0) || (n <= 500 && n % 20 === 0) || (n <= 1000 && n % 100 === 0) || (n <= 5000 && n % 500 === 0) || n % 10000 === 0;
}

// string.Format with {0} {1} ... (no format specifiers needed here).
function format(template, args) {
  if (!args || !args.length) return template;
  return template.replace(/\{(\d+)\}/g, (m, i) => (Number(i) < args.length ? String(args[i]) : m));
}

// ColorUtil.StripHexColor: removes [RRGGBB] and [-] tags.
function stripHexColor(s) {
  return String(s).replace(/\[[0-9A-Fa-f]{6}\]|\[-\]/g, '');
}

class GameLogger {
  // opts: cwd, now() -> Date, maxDuplicateLogs (50), trackerCleanMs (300000), debug (false: Debug
  // lines are not written, the default level is UNVERIFIED), mirror (fn(record) for --echo-logs)
  constructor(opts = {}) {
    this.cwd = opts.cwd || process.cwd();
    this.now = opts.now || (() => new Date());
    this.maxDuplicateLogs = opts.maxDuplicateLogs == null ? 50 : opts.maxDuplicateLogs;
    this.trackerCleanMs = opts.trackerCleanMs || 300000;
    this.debug = !!opts.debug;
    this.mirror = opts.mirror || null;
    this.tracker = new Map();
    this.lastClean = Date.now();
    this.listeners = new Set();
    this.file = null;
    this.records = []; // every line written, for tests: { level, text }
  }

  // Logger.CreateNewLogFile: "Logs/Log[{yyMMdd-hhmmss}].txt", opened lazily on the first line.
  open() {
    if (this.file) return this.file;
    const dir = path.join(this.cwd, 'Logs');
    fs.mkdirSync(dir, { recursive: true });
    this.file = path.join(dir, `Log[${stamp(this.now(), true)}].txt`);
    fs.appendFileSync(this.file, '');
    return this.file;
  }

  onLog(fn) {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  cleanTracker() {
    if (Date.now() - this.lastClean <= this.trackerCleanMs) return;
    this.lastClean = Date.now();
    for (const [k, v] of this.tracker) if (v <= 100) this.tracker.delete(k);
  }

  // level: debug|info|warn|error ; template + args like LoggerExtensions.LogInfo(this, fmt, args)
  // frames: the stack frames to print under the line (class names from the real call sites).
  log(level, template, args = [], frames = []) {
    const key = String(template);
    const count = (this.tracker.get(key) || 0) + 1;
    this.tracker.set(key, count);
    this.cleanTracker();
    if (!(count < this.maxDuplicateLogs || canLogWithCount(count))) return false;
    if (level === 'debug' && !this.debug) return false;
    const text = format(key, args);
    this.write(level, `${LEVEL_TAG[level]}${text}\r\n${frames.map((f) => `    ${f}\r\n`).join('')}`, text);
    this.emitConsole(level, text);
    return true;
  }

  info(t, args, frames) {
    return this.log('info', t, args, frames);
  }
  warn(t, args, frames) {
    return this.log('warn', t, args, frames);
  }
  error(t, args, frames) {
    return this.log('error', t, args, frames);
  }
  debugLine(t, args, frames) {
    return this.log('debug', t, args, frames);
  }

  // Logger.Exception -> FormatExceptionStacktrace. The tracker key is ex.ToString().
  exception(type, message, frames = []) {
    const head = `${type}: ${message}`;
    const key = `${head}\n${frames.join('\n')}`;
    const count = (this.tracker.get(key) || 0) + 1;
    this.tracker.set(key, count);
    if (!(count < this.maxDuplicateLogs || canLogWithCount(count))) return false;
    const body = `${LEVEL_TAG.exception}${head}\r\n${frames.map((f) => `    ${f}\r\n`).join('')}`.replace(/[\r\n]+$/, '');
    this.write('exception', body, head);
    // SocketAdminConsole.OnExceptionLog: "[X] {Name}: {Message}\n{StackTrace}" in ONE message.
    for (const fn of this.listeners) fn('exception', `${CONSOLE_TAG.exception} ${head}\n${frames.map((f) => `   at ${f}`).join('\n')}`);
    return true;
  }

  write(level, text, plain) {
    this.open();
    fs.appendFileSync(this.file, `[${stamp(this.now(), false)}] ${text}\n`);
    this.records.push({ level, text: plain });
    if (this.mirror) this.mirror(`${LEVEL_TAG[level].trim()} ${plain}`);
  }

  emitConsole(level, text) {
    for (const fn of this.listeners) fn(level, `${CONSOLE_TAG[level]} ${stripHexColor(text)}`);
  }
}

class UnityLog {
  // target: a file path, or null/'' / '-' for stdout (Unity's -logFile without a usable path).
  constructor(target, { stdout = process.stdout } = {}) {
    this.target = target || null;
    this.stdout = stdout;
    if (this.target && this.target !== '-') {
      fs.mkdirSync(path.dirname(this.target), { recursive: true });
      fs.writeFileSync(this.target, ''); // Unity truncates its log at start
    }
  }

  line(text) {
    const s = `${text}\n`;
    if (this.target && this.target !== '-') fs.appendFileSync(this.target, s);
    else this.stdout.write(s);
  }
}

module.exports = { GameLogger, UnityLog, stamp, format, stripHexColor, canLogWithCount };
