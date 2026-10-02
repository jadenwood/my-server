'use strict';

// Up to four server instances on one PC (steward edition only).
//
// Each instance is a full test copy in its own folder, because Configuration\, Saves\ and Mods\
// are relative to the working directory and the server has no config-path argument
// (docs/join-and-scale.md §4.2 [DEC]). Ports per instance, from the port plan in
// docs/seamless-design.md O1 (spaced by 10 so that RCON on query+1 never meets another
// instance's query port):
//
//   slot  game = pingPort   steamAuthPort (A2S query)   rConPort (RCON stays off)
//   s1    7350              27015                       27016
//   s2    7360              27025                       27026
//   s3    7370              27035                       27036
//   s4    7380              27045                       27046
//
// The game port carries UDP game traffic and the TCP ping system (pingPort shares the number;
// the game's own config comment allows this [DEC PingGraphManager]). UNVERIFIED: that four
// instances run side by side (the Steam client port 8766 is hard-coded in the game).

const path = require('path');
const S = require('./safety');

const MAX_INSTANCES = 4;
// The game code has no limit on maxPlayers (int, no clamp; uLink is started with int.MaxValue
// connections [DEC]). 120 is Realm's own cap: what the owner asked for, and well above the code
// default of 30. UNVERIFIED: CPU, RAM and bandwidth at 120 players.
const MAX_PLAYERS_CAP = 120;
const ID_RE = /^s[1-4]$/;
// Ports Realm never hands to an instance: the game's unauthenticated admin console (-cport,
// default 11000 on all interfaces [DEC SocketAdminConsole]) and Realm's own local services.
const RESERVED = [
  { from: 11000, to: 11003, why: 'the game admin console (never use or forward these)' },
  { from: 8786, to: 8790, why: "Realm's local services (Chronicle)" },
  { from: 8766, to: 8766, why: 'the Steam client port the game uses' }
];
const NUMERALS = ['I', 'II', 'III', 'IV'];

function portsForSlot(slot) {
  if (!Number.isInteger(slot) || slot < 1 || slot > MAX_INSTANCES) throw new RangeError('slot must be 1 to 4');
  const d = (slot - 1) * 10;
  return { game: 7350 + d, query: 27015 + d, rcon: 27016 + d };
}

function slotOf(id) {
  return ID_RE.test(id) ? Number(id.slice(1)) : null;
}

function defaultName(slot) {
  return `Realm ${NUMERALS[slot - 1] || slot}`;
}

// s1 keeps the existing test folder; sN goes next to it: G:\RealmTest\s2\server.
function defaultRoot(slot, s1Root, platform = 'win32') {
  if (slot === 1) return s1Root;
  const P = S.pathFor(platform);
  return P.join(S.realmHome(s1Root, platform), `s${slot}`, 'server');
}

function makeInstance(slot, extra = {}) {
  return {
    id: `s${slot}`,
    ports: portsForSlot(slot),
    autoRestart: true,
    backupBeforeRestart: true,
    dailyRestart: { enabled: false, time: '06:00' },
    network: 'local',
    listed: false,
    ...extra
  };
}

function nextFreeSlot(list) {
  const used = new Set(list.map((i) => slotOf(i.id)));
  for (let s = 1; s <= MAX_INSTANCES; s++) if (!used.has(s)) return s;
  return null;
}

// Every socket an instance opens, with protocol (docs/join-and-scale.md §4.1).
function socketsOf(inst) {
  const p = inst.ports;
  return [
    { port: p.game, proto: 'udp', what: 'game' },
    { port: p.game, proto: 'tcp', what: 'ping' },
    { port: p.query, proto: 'udp', what: 'Steam query' }
  ];
}

function isTime(t) {
  return typeof t === 'string' && /^([01]\d|2[0-3]):[0-5]\d$/.test(t);
}

// Normalizes what settings.json holds. s1 always exists; its folder is the "Test copy folder".
function normalizeInstances(raw, s1Root, platform = process.platform) {
  const out = [];
  const seen = new Set();
  for (const r of Array.isArray(raw) ? raw : []) {
    if (!r || typeof r !== 'object' || !ID_RE.test(r.id) || seen.has(r.id)) continue;
    seen.add(r.id);
    const slot = slotOf(r.id);
    const def = makeInstance(slot);
    const ports = r.ports && typeof r.ports === 'object' ? r.ports : {};
    const inst = {
      ...def,
      ports: {
        game: validPort(ports.game) ? ports.game : def.ports.game,
        query: validPort(ports.query) ? ports.query : def.ports.query,
        rcon: validPort(ports.rcon) ? ports.rcon : def.ports.rcon
      },
      autoRestart: r.autoRestart !== false,
      backupBeforeRestart: r.backupBeforeRestart !== false,
      dailyRestart: {
        enabled: !!(r.dailyRestart && r.dailyRestart.enabled === true),
        time: r.dailyRestart && isTime(r.dailyRestart.time) ? r.dailyRestart.time : def.dailyRestart.time
      },
      network: r.network === 'public' ? 'public' : 'local',
      listed: r.listed === true
    };
    if (slot === 1) inst.root = s1Root;
    else if (typeof r.root === 'string' && r.root) inst.root = r.root;
    else continue;
    out.push(inst);
  }
  if (!seen.has('s1')) out.unshift({ ...makeInstance(1), root: s1Root });
  out.sort((a, b) => slotOf(a.id) - slotOf(b.id));
  return out.slice(0, MAX_INSTANCES);
}

function validPort(p) {
  return Number.isInteger(p) && p >= 1024 && p <= 65535;
}

// Problems with a whole fleet's ports and folders. Returns a list of plain-English messages.
function fleetProblems(list, platform = process.platform) {
  const problems = [];
  const owners = new Map();
  for (const inst of list) {
    const p = inst.ports || {};
    for (const [k, v] of Object.entries({ game: p.game, query: p.query, rcon: p.rcon })) {
      if (!validPort(v)) {
        problems.push(`${inst.id}: ${k} port must be a whole number from 1024 to 65535.`);
        continue;
      }
      const r = RESERVED.find((x) => v >= x.from && v <= x.to);
      if (r) problems.push(`${inst.id}: port ${v} is reserved for ${r.why}.`);
      const key = String(v);
      if (owners.has(key)) problems.push(`${inst.id}: port ${v} is already used by ${owners.get(key)}.`);
      else owners.set(key, `${inst.id} ${k}`);
    }
  }
  for (let i = 0; i < list.length; i++) {
    for (let j = i + 1; j < list.length; j++) {
      const a = list[i].root;
      const b = list[j].root;
      if (a && b && (S.isInside(a, b, platform) || S.isInside(b, a, platform))) problems.push(`${list[j].id}: its folder overlaps ${list[i].id}'s folder.`);
    }
  }
  return problems;
}

// ServerSettings.cfg / ConsoleSettings.cfg values applied before each start. Only keys that already
// exist in the files are rewritten (lib/safety.js rewriteCfg), so a missing key is reported, never added.
function cfgValuesFor(inst, { restartTime = 0 } = {}) {
  const pub = inst.network === 'public';
  return {
    server: {
      // bindIP binds the UDP game socket only; the TCP ping socket always binds every interface [DEC].
      bindIP: pub ? '0.0.0.0' : '127.0.0.1',
      // isPrivate only sets PR=TRUE in the game's lobby announcement; it is not access control [DEC].
      isPrivate: pub && inst.listed ? 'False' : 'True',
      portNumber: String(inst.ports.game),
      pingPort: String(inst.ports.game),
      steamAuthPort: String(inst.ports.query),
      restartTime: String(Math.max(0, Math.round(restartTime)))
    },
    console: { rConPort: String(inst.ports.rcon), enableRCon: 'False' }
  };
}

module.exports = {
  MAX_INSTANCES,
  MAX_PLAYERS_CAP,
  RESERVED,
  ID_RE,
  portsForSlot,
  slotOf,
  defaultName,
  defaultRoot,
  makeInstance,
  nextFreeSlot,
  socketsOf,
  isTime,
  normalizeInstances,
  fleetProblems,
  cfgValuesFor
};
