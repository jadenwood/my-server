'use strict';

// The Sentinel screen in Realm Steward (ROADMAP SEN-5, STW-3): RealmSentinel's alerts, suspects and evidence,
// read-only from the files the plugin writes, and actions through the admin console.
//
// Sources (plugins/docs/RealmSentinel.md):
//   oxide/data/RealmSentinelFeed.json                     "The Steward feed": Version 1, Generated, Mode,
//                                                          DataDamaged, Online, Frozen, Alerts[], Suspects[]
//   oxide/logs/RealmSentinel/realmsentinel_evidence-<yyyy-MM-dd>.txt
//                                                          one line per finding, written by the plugin's
//                                                          WriteLog("evidence", ...) through Oxide LogToFile:
//     <ISO> #<id> <kind> <name> (<steam id>) +<points> -> <score>[ at <x,y,z>] ping <ms> ms: <detail>
//     <ISO> #<id> admin_action <name> (<steam id>): by <admin>: <what>
//
// Actions. The game's admin console runs commands as the server player, and Oxide runs a plugin's chat
// command (/sentinel ...) only for a sender it knows as a Covalence player, which the console is not
// (lib/moderation.js PLUGIN_ADMIN, docs/admin-console.md). So:
//   - one click over the console: the game's own /kick and /ban (CoreCommandHandler.Kick / .Ban), through
//     the Court so they land in the Court rolls, and /oxide.reload RealmSentinel;
//   - the /sentinel commands (report, freeze, unfreeze, clear, ban confirm) are filled in for the player and
//     copied, for an admin to paste in game chat. A console route needs server-console commands in
//     RealmSentinel itself, like RealmCourt's /realm.save (follow-up for the plugin's owners).
// Nothing here writes to the plugin's files.

const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const MOD = require('./moderation');

const FEED_FILE = 'RealmSentinelFeed.json';
const FEED_MAX_BYTES = 4 * 1024 * 1024;
const LOG_TAIL_BYTES = 2 * 1024 * 1024;
const ID_RE = /^\d{1,20}$/;
const RESPONSES = ['alert', 'would freeze', 'would kick', 'ban recommended', 'frozen 15 min', 'kicked', 'kicked, frozen 15 min on return', 'banned', 'ban failed'];

// Text from the feed goes to the screen with textContent; still, keep it to one tidy line.
function line(v, max) {
  return String(v == null ? '' : v)
    .replace(/[\u0000-\u001f\u007f\u200b-\u200f\u2028\u2029\u202a-\u202e\u2066-\u2069]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, max);
}
function num(v) {
  const n = Number(v);
  return Number.isFinite(n) ? Math.round(n * 10) / 10 : 0;
}
function int(v) {
  const n = Number(v);
  return Number.isInteger(n) && n >= 0 ? n : 0;
}
function iso(v) {
  return typeof v === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z$/.test(v) && Number.isFinite(Date.parse(v)) ? v : null;
}
// Response severity, for the colour of a row: 0 note, 1 would act, 2 acted, 3 ban.
function severity(response) {
  const r = String(response || '');
  if (/ban/.test(r)) return 3;
  if (/^(kicked|frozen)/.test(r)) return 2;
  if (/^would/.test(r)) return 1;
  return 0;
}

// Returns { ok: true, feed } or { ok: false, reason }.
function parseFeed(text) {
  let raw;
  try {
    raw = JSON.parse(String(text).replace(/^\uFEFF/, ''));
  } catch (e) {
    return { ok: false, reason: `the feed is not valid JSON (${e.message.slice(0, 60)}); the plugin rewrites it within seconds of the next change` };
  }
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return { ok: false, reason: 'the feed is not an object' };
  if (raw.Version !== 1) return { ok: false, reason: `feed version ${line(raw.Version, 10)} is not one this Steward reads (1); update Realm Steward` };
  const alerts = (Array.isArray(raw.Alerts) ? raw.Alerts : []).slice(0, 1000).filter((a) => a && typeof a === 'object').map((a) => ({
    id: int(a.Id),
    time: iso(a.Time),
    kind: line(a.Kind, 40),
    playerId: ID_RE.test(String(a.PlayerId)) ? String(a.PlayerId) : '',
    playerName: line(a.PlayerName, 64),
    score: num(a.Score),
    response: line(a.Response, 60),
    detail: line(a.Detail, 300),
    severity: severity(a.Response)
  }));
  alerts.sort((a, b) => b.id - a.id);
  const suspects = (Array.isArray(raw.Suspects) ? raw.Suspects : []).slice(0, 500).filter((s) => s && typeof s === 'object' && ID_RE.test(String(s.PlayerId))).map((s) => {
    const counts = {};
    if (s.Counts && typeof s.Counts === 'object' && !Array.isArray(s.Counts)) for (const [k, v] of Object.entries(s.Counts).slice(0, 40)) if (/^[a-z_]{1,40}$/.test(k)) counts[k] = int(v);
    return { playerId: String(s.PlayerId), playerName: line(s.PlayerName, 64), score: num(s.Score), peakScore: num(s.PeakScore), online: s.Online === true, frozen: s.Frozen === true, lastSeen: iso(s.LastSeen), counts };
  });
  suspects.sort((a, b) => b.score - a.score || b.peakScore - a.peakScore);
  return {
    ok: true,
    feed: {
      version: 1,
      generated: iso(raw.Generated),
      mode: raw.Mode === 'enforce' ? 'enforce' : raw.Mode === 'watch' ? 'watch' : line(raw.Mode, 20) || 'unknown',
      dataDamaged: raw.DataDamaged === true,
      online: int(raw.Online),
      frozen: int(raw.Frozen),
      alerts,
      suspects
    }
  };
}

// { state: 'missing'|'damaged'|'ok', file, feed?, reason?, mtime? }
async function readFeed(dataDir) {
  const file = path.join(dataDir, FEED_FILE);
  let st;
  try {
    st = await fsp.stat(file);
  } catch {
    return { state: 'missing', file };
  }
  if (st.size > FEED_MAX_BYTES) return { state: 'damaged', file, reason: 'the feed is larger than 4 MB', mtime: st.mtime.toISOString() };
  let text;
  try {
    text = await fsp.readFile(file, 'utf8');
  } catch (e) {
    return { state: 'damaged', file, reason: e.message, mtime: st.mtime.toISOString() };
  }
  const p = parseFeed(text);
  return p.ok ? { state: 'ok', file, feed: p.feed, mtime: st.mtime.toISOString() } : { state: 'damaged', file, reason: p.reason, mtime: st.mtime.toISOString() };
}

const EVIDENCE_RE = /^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ) #(\d+) ([a-z_]{1,40}) (.+?) \((\d{1,20})\)(?: \+([\d.]+) -> ([\d.]+)(?: at (.+?))? ping (-?\d+) ms)?: (.*)$/;

function parseEvidenceLine(text) {
  const m = EVIDENCE_RE.exec(String(text).trim());
  if (!m) return null;
  return {
    time: m[1],
    id: Number(m[2]),
    kind: m[3],
    playerName: line(m[4], 64),
    playerId: m[5],
    points: m[6] != null ? Number(m[6]) : null,
    scoreAfter: m[7] != null ? Number(m[7]) : null,
    pos: m[8] ? line(m[8], 60) : null,
    pingMs: m[9] != null ? Number(m[9]) : null,
    detail: line(m[10], 300),
    admin: m[3] === 'admin_action'
  };
}

async function tail(file, bytes) {
  const fh = await fsp.open(file, 'r');
  try {
    const st = await fh.stat();
    const start = Math.max(0, st.size - bytes);
    const buf = Buffer.alloc(st.size - start);
    await fh.read(buf, 0, buf.length, start);
    let text = buf.toString('utf8');
    if (start > 0) text = text.slice(text.indexOf('\n') + 1); // drop the cut first line
    return text;
  } finally {
    await fh.close();
  }
}

// A player's evidence lines from the daily files, newest first.
async function readEvidence(logsDir, playerId, { days = 30, limit = 200 } = {}) {
  if (!ID_RE.test(String(playerId))) throw new Error('Unknown player.');
  const dir = path.join(logsDir, 'RealmSentinel');
  let names = [];
  try {
    names = (await fsp.readdir(dir)).filter((n) => /^realmsentinel_evidence-\d{4}-\d\d-\d\d\.txt$/.test(n)).sort().reverse().slice(0, days);
  } catch {
    return { dir, files: 0, lines: [] };
  }
  const out = [];
  for (const n of names) {
    if (out.length >= limit) break;
    let text;
    try {
      text = await tail(path.join(dir, n), LOG_TAIL_BYTES);
    } catch {
      continue;
    }
    const rows = text.split(/\r?\n/);
    for (let i = rows.length - 1; i >= 0 && out.length < limit; i--) {
      const e = parseEvidenceLine(rows[i]);
      if (e && e.playerId === String(playerId)) out.push(e);
    }
  }
  return { dir, files: names.length, lines: out };
}

// The in-game commands for one suspect, filled in for copying.
function commandsFor(name) {
  const n = { player: name };
  return [
    { id: 'report', label: 'Report', template: '/sentinel report {player}' },
    { id: 'freeze', label: 'Freeze 15 min', template: '/sentinel freeze {player} {minutes}', values: { minutes: 15 } },
    { id: 'unfreeze', label: 'Unfreeze', template: '/sentinel unfreeze {player}' },
    { id: 'clear', label: 'Clear score', template: '/sentinel clear {player}' },
    { id: 'ban', label: 'Sentinel ban', template: '/sentinel ban {player} confirm' }
  ].map((c) => ({ id: c.id, label: c.label, command: MOD.pluginCommandFor(c.template, { ...n, ...(c.values || {}) }) }));
}

// The response scores from oxide/config/RealmSentinel.json (Responses section), else the plugin's defaults.
const DEFAULT_THRESHOLDS = { alert: 20, freeze: 50, kick: 80, ban: 150, fromConfig: false };
async function readThresholds(file) {
  try {
    const raw = JSON.parse((await fsp.readFile(file, 'utf8')).replace(/^\uFEFF/, ''));
    const r = raw && raw.Responses;
    const n = (v, d) => (Number.isFinite(Number(v)) && Number(v) > 0 ? Number(v) : d);
    if (!r || typeof r !== 'object') return { ...DEFAULT_THRESHOLDS };
    return { alert: n(r.AlertScore, 20), freeze: n(r.FreezeScore, 50), kick: n(r.KickScore, 80), ban: n(r.BanScore, 150), fromConfig: true };
  } catch {
    return { ...DEFAULT_THRESHOLDS };
  }
}

// Remembers the newest alert id the owner has seen, per server, in <userData>\sentinel\seen.json.
class SeenStore {
  constructor(dir) {
    this.file = path.join(dir, 'seen.json');
    this.data = {};
    try {
      const raw = JSON.parse(fs.readFileSync(this.file, 'utf8'));
      if (raw && typeof raw === 'object' && !Array.isArray(raw)) for (const [k, v] of Object.entries(raw)) if (/^s[1-4]$/.test(k) && Number.isInteger(v)) this.data[k] = v;
    } catch {
      /* first run, or a damaged file: everything counts as unseen once */
    }
  }
  get(id) {
    return this.data[id] || 0;
  }
  async set(id, alertId) {
    if (!/^s[1-4]$/.test(id) || !Number.isInteger(alertId)) return;
    this.data[id] = Math.max(this.get(id), alertId);
    await fsp.mkdir(path.dirname(this.file), { recursive: true });
    await fsp.writeFile(this.file, JSON.stringify(this.data, null, 2));
  }
}

function unseenCount(feed, seenId) {
  return feed ? feed.alerts.filter((a) => a.id > seenId).length : 0;
}

// deps: { handle, userData, fleet(), instOf(id), rootOf(id) -> root|null, oxideDir(root) -> Promise<dir>, court, clipboard, push(msg), log, pollMs }
function registerSteward(deps) {
  const seen = new SeenStore(path.join(deps.userData, 'sentinel'));
  const last = new Map(); // id -> { mtime, feed, unseen }
  const idOf = (id) => deps.instOf(id === undefined ? 's1' : id).id;

  async function dirsFor(id) {
    const root = deps.rootOf(id);
    if (!root) return null;
    const ox = (await deps.oxideDir(root)) || path.join(root, 'oxide');
    return { root, data: path.join(ox, 'data'), logs: path.join(ox, 'logs'), plugin: path.join(ox, 'plugins', 'RealmSentinel.cs'), config: path.join(ox, 'config', 'RealmSentinel.json') };
  }

  async function status(id) {
    const d = await dirsFor(id);
    if (!d) return { id, state: 'no-server' };
    const r = await readFeed(d.data);
    const installed = fs.existsSync(d.plugin);
    const seenId = seen.get(id);
    const out = { id, state: r.state, file: r.file, reason: r.reason || null, mtime: r.mtime || null, installed, seenId, console: !!(deps.court && deps.court.consoleReady(id)), thresholds: await readThresholds(d.config) };
    if (r.state === 'ok') {
      out.feed = r.feed;
      out.unseen = unseenCount(r.feed, seenId);
      for (const s of r.feed.suspects) s.commands = commandsFor(s.playerName || s.playerId);
      // The background watch keeps its own copy (so it still sees the change and raises the toast).
      const prev = last.get(id);
      if (prev) prev.unseen = out.unseen;
    }
    return out;
  }

  // Background watch: new alerts raise the rail badge and a toast even when the screen is closed.
  async function poll() {
    for (const inst of deps.fleet()) {
      try {
        const d = await dirsFor(inst.id);
        if (!d) continue;
        const file = path.join(d.data, FEED_FILE);
        let st;
        try {
          st = await fsp.stat(file);
        } catch {
          continue;
        }
        const prev = last.get(inst.id);
        if (prev && prev.mtime === st.mtime.toISOString()) continue;
        const r = await readFeed(d.data);
        if (r.state !== 'ok') continue;
        const unseen = unseenCount(r.feed, seen.get(inst.id));
        const newest = r.feed.alerts[0] || null;
        const fresh = prev && newest && (!prev.newest || newest.id > prev.newest.id) ? newest : null;
        last.set(inst.id, { mtime: r.mtime, unseen, newest });
        deps.push({ type: 'sentinel', id: inst.id, unseen, total: [...last.values()].reduce((n, v) => n + (v.unseen || 0), 0), fresh: fresh ? { playerName: fresh.playerName, kind: fresh.kind, response: fresh.response, score: fresh.score } : null });
      } catch (e) {
        deps.log('warn', `[sentinel] ${inst.id}: ${e.message}`);
      }
    }
  }
  const timer = setInterval(() => poll().catch(() => {}), deps.pollMs || 5000);
  if (timer.unref) timer.unref();

  deps.handle('sentinel:status', (id) => status(idOf(id)));
  deps.handle('sentinel:evidence', async (id, playerId) => {
    const sid = idOf(id);
    const d = await dirsFor(sid);
    if (!d) throw Object.assign(new Error('No server copy is set up yet.'), { friendly: true });
    return readEvidence(d.logs, String(playerId));
  });
  deps.handle('sentinel:markSeen', async (id, alertId) => {
    const sid = idOf(id);
    await seen.set(sid, Number(alertId));
    const prev = last.get(sid);
    if (prev) prev.unseen = 0;
    deps.push({ type: 'sentinel', id: sid, unseen: 0, total: [...last.values()].reduce((n, v) => n + (v.unseen || 0), 0), fresh: null });
    return { seenId: seen.get(sid) };
  });
  // One click over the admin console: the game's own kick and ban, and a reload of the plugin.
  deps.handle('sentinel:act', async (id, action, args) => {
    const sid = idOf(id);
    if (!['kick', 'ban', 'reload'].includes(action)) throw Object.assign(new Error('Unknown Sentinel action.'), { friendly: true });
    const a = args && typeof args === 'object' ? args : {};
    if (action === 'reload') return deps.court.act(sid, 'reload', { plugin: 'RealmSentinel' });
    const name = line(a.name, 64);
    if (!name) throw Object.assign(new Error('Choose a player first.'), { friendly: true });
    const reason = line(a.reason || `Sentinel: ${line(a.kind, 40) || 'review'}`, 200);
    if (action === 'kick') return deps.court.act(sid, 'kick', { name, reason });
    const days = a.days == null || a.days === '' ? 0 : Number(a.days);
    return deps.court.act(sid, 'ban', { name, days, reason });
  });
  deps.handle('sentinel:copy', (id, playerName, commandId) => {
    const cmd = commandsFor(line(playerName, 64)).find((c) => c.id === commandId);
    if (!cmd) throw Object.assign(new Error('Unknown Sentinel command.'), { friendly: true });
    if (deps.clipboard) deps.clipboard.writeText(cmd.command);
    return cmd.command;
  });

  return { status, poll, seen, stop: () => clearInterval(timer) };
}

module.exports = { FEED_FILE, RESPONSES, DEFAULT_THRESHOLDS, readThresholds, parseFeed, readFeed, parseEvidenceLine, readEvidence, commandsFor, severity, SeenStore, unseenCount, registerSteward };
