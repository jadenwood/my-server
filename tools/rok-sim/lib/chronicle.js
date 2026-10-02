'use strict';

// Fake RealmChronicle output: oxide/data/RealmChronicle.json and oxide/data/RealmState.json, in the
// shapes plugins/RealmChronicle.cs writes (field names, limits, retention, duplicate window, UTC
// timestamps "yyyy-MM-ddTHH:mm:ssZ"). Oxide's DataFileSystem writes Newtonsoft JSON, indented; the
// simulator writes 2-space indented JSON with CRLF line ends (UNVERIFIED: Oxide's exact whitespace).
//
// The simulator only writes event types that already exist. KNOWN_TYPES is a copy; the tests check
// that every entry is accepted by plugins/RealmChronicle.cs, chronicle/server.js and
// chronicle/public/assets/common.js (this tool never edits those files).

const fs = require('fs');
const path = require('path');

const KNOWN_TYPES = [
  'coronation', 'abdication', 'claim_declared', 'rebellion_started', 'rebellion_ended',
  'house_founded', 'oath_sworn', 'oath_broken', 'treaty_signed', 'treaty_broken',
  'decree', 'ransom_set', 'ransom_paid', 'released',
  'contract_posted', 'contract_fulfilled', 'contract_ended',
  'season_started', 'season_ended', 'event_started', 'event_ended',
  'tournament_champion', 'hunt_kill', 'truce_broken'
];

const LIMITS = { title: 140, detail: 400, actor: 48, actors: 8 };

// The six great houses of Ostreval (docs/community/lore.md).
const GREAT_HOUSES = [
  { name: 'Varrow', sigil: 'Iron Stag' },
  { name: 'Ashgrove', sigil: 'White Oak' },
  { name: 'Corvane', sigil: 'Black Raven' },
  { name: 'Dunmere', sigil: 'Drowned Bell' },
  { name: 'Halloran', sigil: 'Ember Hound' },
  { name: 'Merrin', sigil: 'Silver Eel' }
];

const isoNow = (d) => d.toISOString().replace(/\.\d{3}Z$/, 'Z');
const clip = (s, n) => (s == null ? '' : String(s)).slice(0, n);

class ChronicleWriter {
  // opts: dataDir, maxEvents (500), duplicateWindowMs (300000), tornWrites (false), now() -> Date
  constructor(opts) {
    this.dir = opts.dataDir;
    this.maxEvents = opts.maxEvents || 500;
    this.duplicateWindowMs = opts.duplicateWindowMs == null ? 300000 : opts.duplicateWindowMs;
    this.tornWrites = !!opts.tornWrites;
    this.now = opts.now || (() => new Date());
    this.eventsFile = path.join(this.dir, 'RealmChronicle.json');
    this.stateFile = path.join(this.dir, 'RealmState.json');
    this.events = [];
    this.recent = []; // { key, at }
    this.state = { king: null, house: null, since: null, houses: [], online: 0, maxPlayers: 0, updated: null };
    this.timers = new Set();
    this.load();
  }

  load() {
    fs.mkdirSync(this.dir, { recursive: true });
    try {
      const list = JSON.parse(fs.readFileSync(this.eventsFile, 'utf8').replace(/^﻿/, ''));
      if (Array.isArray(list)) this.events = list.filter((e) => e && Number.isInteger(e.id));
    } catch {
      this.events = [];
    }
    try {
      const s = JSON.parse(fs.readFileSync(this.stateFile, 'utf8').replace(/^﻿/, ''));
      if (s && typeof s === 'object') Object.assign(this.state, s);
    } catch {
      /* fresh state */
    }
    this.nextId = this.events.reduce((m, e) => Math.max(m, e.id), 0) + 1;
  }

  // RealmChronicle.Log(type, title, detail, actors). Returns the event, or null when it is refused
  // (unknown type, empty title, or a duplicate of one logged within the window).
  log(type, title, detail = '', actors = []) {
    if (!KNOWN_TYPES.includes(type)) return null;
    title = clip(title, LIMITS.title).trim();
    if (!title) return null;
    detail = clip(detail, LIMITS.detail);
    const key = `${type}\u0001${title}\u0001${detail}`;
    const nowMs = this.now().getTime();
    this.recent = this.recent.filter((r) => nowMs - r.at < this.duplicateWindowMs);
    if (this.recent.some((r) => r.key === key)) return null;
    this.recent.push({ key, at: nowMs });
    const ev = {
      id: this.nextId++,
      ts: isoNow(this.now()),
      type,
      title,
      detail,
      actors: (Array.isArray(actors) ? actors : []).filter((a) => typeof a === 'string' && a.trim()).slice(0, LIMITS.actors).map((a) => clip(a.trim(), LIMITS.actor))
    };
    this.events.push(ev);
    if (this.events.length > this.maxEvents) this.events.splice(0, this.events.length - this.maxEvents);
    this.write(this.eventsFile, this.events);
    return ev;
  }

  setCrown(king, house) {
    const changed = king !== this.state.king;
    this.state.king = king || null;
    this.state.house = king ? house || null : null;
    if (changed) this.state.since = king ? isoNow(this.now()) : null;
  }

  setHouses(houses) {
    this.state.houses = houses.map((h) => ({ name: h.name, sigil: h.sigil || null, liege: h.liege || null, members: h.members | 0 }));
  }

  writeState(online, maxPlayers) {
    this.state.online = online | 0;
    this.state.maxPlayers = maxPlayers | 0;
    this.state.updated = isoNow(this.now());
    this.write(this.stateFile, this.state);
  }

  write(file, value) {
    const text = JSON.stringify(value, null, 2).replace(/\n/g, '\r\n');
    if (!this.tornWrites) {
      fs.writeFileSync(file, text);
      return;
    }
    // Chaos: leave a half-written file for a moment, as an in-place rewrite can.
    fs.writeFileSync(file, text.slice(0, Math.max(1, Math.floor(text.length / 2))));
    const t = setTimeout(() => {
      this.timers.delete(t);
      fs.writeFileSync(file, text);
    }, 150);
    this.timers.add(t);
  }

  flush() {
    for (const t of this.timers) clearTimeout(t);
    this.timers.clear();
    fs.writeFileSync(this.eventsFile, JSON.stringify(this.events, null, 2).replace(/\n/g, '\r\n'));
    fs.writeFileSync(this.stateFile, JSON.stringify(this.state, null, 2).replace(/\n/g, '\r\n'));
  }
}

// A deterministic story for --chronicle demo: each call returns the next [type, title, detail, actors]
// plus an optional crown change. Names and houses follow docs/community/lore.md.
const DEMO_FOUNDERS = ['Aldric Varrow', 'Maelis Ashgrove', 'Ysolde Corvane', 'Tamsin Dunmere', 'Bram Halloran', 'Corin Merrin'];
function demoStory(step) {
  const n = GREAT_HOUSES.length;
  if (step < n) {
    const h = GREAT_HOUSES[step];
    return { ev: ['house_founded', `House ${h.name} is founded`, `${DEMO_FOUNDERS[step]} raises the ${h.sigil}.`, [DEMO_FOUNDERS[step]]] };
  }
  const round = Math.floor((step - n) / 8);
  const k = (step - n) % 8;
  const a = (round * 2) % n;
  const b = (a + 1 + (round % (n - 1))) % n;
  const A = GREAT_HOUSES[a].name;
  const B = GREAT_HOUSES[b].name;
  const kingA = DEMO_FOUNDERS[a];
  const kingB = DEMO_FOUNDERS[b];
  const tag = round ? ` (${round + 1})` : '';
  switch (k) {
    case 0:
      return { ev: ['oath_sworn', `House ${B} swears to House ${A}${tag}`, `${kingB} kneels at the Hearth.`, [kingB, kingA]], liege: [B, A] };
    case 1:
      return { ev: ['coronation', `${kingA} takes the Old Throne${tag}`, `House ${A} holds the seat.`, [kingA]], crown: [kingA, A] };
    case 2:
      return { ev: ['decree', `The crown decrees a harvest tithe${tag}`, 'Authority spent: 2.', [kingA]] };
    case 3:
      return { ev: ['claim_declared', `House ${B} declares a claim on the crown${tag}`, 'The Lawful Hours open at dusk.', [kingB]] };
    case 4:
      return { ev: ['rebellion_started', `House ${B} rises against the crown${tag}`, 'The rebellion window is open.', [kingB, kingA]] };
    case 5:
      return { ev: ['ransom_set', `${kingA} is held for ransom${tag}`, 'The Charter caps the price at 500 gold.', [kingA, kingB]] };
    case 6:
      return { ev: ['ransom_paid', `House ${A} pays the ransom${tag}`, `${kingA} walks free.`, [kingA]] };
    default:
      return { ev: ['rebellion_ended', `The rebellion of House ${B} ends${tag}`, 'The window closes.', [kingB]], crown: [kingB, B] };
  }
}

module.exports = { ChronicleWriter, KNOWN_TYPES, GREAT_HOUSES, LIMITS, demoStory, isoNow };
