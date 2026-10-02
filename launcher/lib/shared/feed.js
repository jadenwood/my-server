'use strict';

// The player's "Realm feed": the latest public Chronicle events of every listed server, plus the
// next scheduled event when a server's Chronicle state announces one. Pure functions, plain text
// only; the network calls live in player/main.js.
//
// Next-event contract (optional, additive): a Chronicle /api/state MAY carry
//   "next": { "title": "Crown Night", "at": "2026-10-03T19:00:00Z" }   (or "nextEvent", same shape;
//   "name" is accepted for "title" and "startsAt" for "at").
// UNVERIFIED that any plugin writes it today: RealmChronicle's RealmState has no such field and the
// Chronicle service's sanitizeState() drops unknown keys, so until both pass it through the feed
// shows events only and no countdown.

const MAX_AHEAD_MS = 45 * 24 * 3600 * 1000;

function clean(s, max) {
  return String(s == null ? '' : s).replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, max);
}

function isoOrNull(v) {
  if (typeof v !== 'string') return null;
  const t = Date.parse(v);
  return Number.isFinite(t) ? new Date(t).toISOString() : null;
}

// { title, at } from a Chronicle state, or null when none is announced, it is malformed, already
// started, or implausibly far away.
function nextFromState(state, now = Date.now()) {
  if (!state || typeof state !== 'object') return null;
  const raw = state.next && typeof state.next === 'object' ? state.next : state.nextEvent && typeof state.nextEvent === 'object' ? state.nextEvent : null;
  if (!raw || Array.isArray(raw)) return null;
  const title = clean(raw.title != null ? raw.title : raw.name, 80);
  const at = isoOrNull(raw.at != null ? raw.at : raw.startsAt);
  if (!title || !at) return null;
  const ms = Date.parse(at) - now;
  if (ms <= 0 || ms > MAX_AHEAD_MS) return null;
  return { title, at };
}

// entries: [{ server: { id, name }, events: [...], state }]. Returns
// { events: [{ key, serverId, serverName, type, title, detail, ts }], next: { title, at, serverId, serverName } | null }.
function mergeFeed(entries, { limit = 8, now = Date.now() } = {}) {
  const events = [];
  let next = null;
  for (const entry of Array.isArray(entries) ? entries : []) {
    if (!entry || !entry.server || typeof entry.server.id !== 'string') continue;
    const serverId = entry.server.id;
    const serverName = clean(entry.server.name, 60);
    for (const e of Array.isArray(entry.events) ? entry.events : []) {
      if (!e || typeof e !== 'object' || !Number.isInteger(e.id) || typeof e.type !== 'string') continue;
      const ts = isoOrNull(e.ts);
      const title = clean(e.title, 160);
      if (!ts || !title) continue;
      events.push({ key: `${serverId}:${e.id}`, serverId, serverName, type: clean(e.type, 32), title, detail: clean(e.detail, 240), ts });
    }
    const n = nextFromState(entry.state, now);
    if (n && (!next || Date.parse(n.at) < Date.parse(next.at))) next = { ...n, serverId, serverName };
  }
  events.sort((a, b) => Date.parse(b.ts) - Date.parse(a.ts) || (a.key < b.key ? 1 : -1));
  return { events: events.slice(0, Math.max(0, limit)), next };
}

module.exports = { nextFromState, mergeFeed, MAX_AHEAD_MS };
