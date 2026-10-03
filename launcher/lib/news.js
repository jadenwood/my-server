'use strict';

// news.json: the signed news feed the owner publishes for Realm (the player app).
//
// Same envelope as servers.json (lib/shared/manifest.js), with its own format string:
//   { "format": "realm-news/1", "alg": "Ed25519", "keyId": "<16 hex>",
//     "payload": "<base64 of the exact news JSON bytes>", "signature": "<base64, 64 bytes>" }
// It is signed with the same owner key, and the player checks it against the public key embedded
// in its build. The signature covers only the payload bytes, so every payload also names its own
// kind ("realm-news"): a signed server list or update manifest can never be read as news.
//
// Item types:
//   announcement  news from the owner and staff
//   season        season news (a new season, standings, the Hall of Kings)
//   chronicle     a highlight from the Chronicle, written up by the owner
//   realm         a server-side content change players see when they join ("What's new in the
//                 realm"): new sculptures, new sign art, events, atmosphere, rules. tag says which.
//
// This file also holds the generic sign/open helpers for Realm's signed documents, which
// lib/updater.js uses too. Nothing here reads a private key from disk: signing takes a PEM the
// caller already holds (Realm Steward keeps it encrypted with safeStorage).

const crypto = require('crypto');
const M = require('./shared/manifest');

const FORMAT = 'realm-news/1';
const KIND = 'realm-news';
const ALG = 'Ed25519';
const MAX_BYTES = 128 * 1024;
const MAX_ITEMS = 60;
const TYPES = ['announcement', 'season', 'chronicle', 'realm'];
const TAGS = ['sculpture', 'sign', 'event', 'atmosphere', 'rules', 'other'];
const ITEM_ID_RE = /^[a-z0-9][a-z0-9-]{0,39}$/;
const B64_RE = /^[A-Za-z0-9+/]+={0,2}$/;
const CONTROL_RE = /[\u0000-\u0008\u000b-\u001f\u007f\u2028\u2029]/; // newlines and tabs are allowed in bodies
const LINE_CONTROL_RE = /[\u0000-\u001f\u007f\u2028\u2029]/;

// ---------- signed documents (shared with lib/updater.js) ----------

function isoSeconds(d) {
  return new Date(Math.floor(d.getTime() / 1000) * 1000).toISOString().replace('.000Z', 'Z');
}

function isIsoDate(s) {
  return typeof s === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,3})?Z$/.test(s) && Number.isFinite(Date.parse(s));
}

// Signs an already validated payload object. Returns the envelope object.
function signDocument(format, payload, privateKeyPem) {
  const key = crypto.createPrivateKey({ key: privateKeyPem, format: 'pem' });
  if (key.asymmetricKeyType !== 'ed25519') throw new RangeError('signing key is not Ed25519');
  const bytes = Buffer.from(JSON.stringify(payload, null, 2), 'utf8');
  const publicKey = M.rawPublicKey(crypto.createPublicKey(key)).toString('base64');
  return {
    format,
    alg: ALG,
    keyId: M.keyIdOf(publicKey),
    payload: bytes.toString('base64'),
    signature: crypto.sign(null, bytes, key).toString('base64')
  };
}

// Checks the envelope and the signature, then parses the payload. Validation of the payload's
// fields is the caller's job. Returns { ok: true, payload } or { ok: false, reason }.
function openDocument(envelope, { format, publicKey, maxBytes = MAX_BYTES, what = 'document' }) {
  let env = envelope;
  try {
    if (typeof env === 'string' || Buffer.isBuffer(env)) {
      if (env.length > maxBytes) return { ok: false, reason: `${what} is too large` };
      env = JSON.parse(String(env).replace(/^﻿/, ''));
    }
  } catch {
    return { ok: false, reason: `${what} is not valid JSON` };
  }
  if (!env || typeof env !== 'object' || Array.isArray(env)) return { ok: false, reason: `${what} is not an object` };
  if (env.format !== format) return { ok: false, reason: `unknown format (expected ${format}); unsigned files are refused` };
  if (env.alg !== ALG) return { ok: false, reason: 'unsupported signature algorithm' };
  if (typeof env.payload !== 'string' || typeof env.signature !== 'string') return { ok: false, reason: `${what} is not signed` };
  if (!B64_RE.test(env.payload) || !B64_RE.test(env.signature)) return { ok: false, reason: 'payload or signature is not base64' };
  let key;
  try {
    key = M.publicKeyFromBase64(publicKey);
  } catch (e) {
    return { ok: false, reason: `no valid public key is embedded in this build (${e.message})` };
  }
  const bytes = Buffer.from(env.payload, 'base64');
  const sig = Buffer.from(env.signature, 'base64');
  if (bytes.length > maxBytes || sig.length !== 64) return { ok: false, reason: 'malformed signature' };
  let good = false;
  try {
    good = crypto.verify(null, bytes, key, sig);
  } catch {
    good = false;
  }
  if (!good) return { ok: false, reason: 'signature does not match the embedded public key' };
  try {
    return { ok: true, payload: JSON.parse(bytes.toString('utf8')) };
  } catch {
    return { ok: false, reason: 'signed payload is not valid JSON' };
  }
}

// ---------- schema ----------

function text(v, max, name, errors, { required = true, multiline = false } = {}) {
  if (v == null || v === '') {
    if (required) errors.push(`${name} is required`);
    return '';
  }
  if (typeof v !== 'string') {
    errors.push(`${name} must be text`);
    return '';
  }
  const s = v.trim();
  if (s.length > max) errors.push(`${name} is longer than ${max} characters`);
  if ((multiline ? CONTROL_RE : LINE_CONTROL_RE).test(s)) errors.push(`${name} contains control characters`);
  if (required && !s) errors.push(`${name} is required`);
  return s;
}

function validateItem(it, n, errors) {
  if (!it || typeof it !== 'object' || Array.isArray(it)) {
    errors.push(`${n} is not an object`);
    return null;
  }
  const out = {
    id: typeof it.id === 'string' && ITEM_ID_RE.test(it.id) ? it.id : (errors.push(`${n}: id must be 1-40 lower-case letters, digits or '-'`), ''),
    type: TYPES.includes(it.type) ? it.type : (errors.push(`${n}: type must be one of ${TYPES.join(', ')}`), ''),
    title: text(it.title, 100, `${n} title`, errors),
    body: text(it.body, 600, `${n} body`, errors, { required: false, multiline: true }),
    date: isIsoDate(it.date) ? it.date : (errors.push(`${n}: date must be an ISO-8601 UTC time`), '')
  };
  if (it.pinned === true) out.pinned = true;
  else if (it.pinned != null && it.pinned !== false) errors.push(`${n}: pinned must be true or false`);
  if (it.link != null && it.link !== '') {
    if (typeof it.link === 'string' && it.link.length <= 300 && M.isWebUrl(it.link, { httpsOnly: true })) out.link = it.link;
    else errors.push(`${n}: link must be an https:// address`);
  }
  if (out.type === 'realm') out.tag = TAGS.includes(it.tag) ? it.tag : it.tag == null ? 'other' : (errors.push(`${n}: tag must be one of ${TAGS.join(', ')}`), 'other');
  else if (it.tag != null) errors.push(`${n}: tag is only for realm items`);
  if (it.at != null && it.at !== '') {
    if (isIsoDate(it.at)) out.at = it.at;
    else errors.push(`${n}: at must be an ISO-8601 UTC time`);
  }
  if (it.server != null && it.server !== '') {
    if (typeof it.server === 'string' && M.ID_RE.test(it.server)) out.server = it.server;
    else errors.push(`${n}: server must be a server id from the list`);
  }
  return out;
}

// Returns { ok, value, errors }. Unknown fields are dropped from value.
function validateNews(input) {
  const errors = [];
  const m = input && typeof input === 'object' && !Array.isArray(input) ? input : {};
  if (m.schema !== 1) errors.push('schema must be 1');
  if (m.kind !== KIND) errors.push(`kind must be ${KIND}`);
  const value = {
    schema: 1,
    kind: KIND,
    realm: text(m.realm, 60, 'realm', errors),
    seq: Number.isInteger(m.seq) && m.seq >= 1 && m.seq <= 2 ** 31 - 1 ? m.seq : (errors.push('seq must be a whole number from 1 to 2147483647'), null),
    issued: m.issued,
    expires: m.expires,
    items: []
  };
  if (!isIsoDate(m.issued)) errors.push('issued must be an ISO-8601 UTC time');
  if (!isIsoDate(m.expires)) errors.push('expires must be an ISO-8601 UTC time');
  else if (isIsoDate(m.issued) && Date.parse(m.expires) <= Date.parse(m.issued)) errors.push('expires must be after issued');
  if (!Array.isArray(m.items) || m.items.length > MAX_ITEMS) errors.push(`items must be a list of at most ${MAX_ITEMS} entries`);
  else {
    const ids = new Set();
    m.items.forEach((it, i) => {
      const out = validateItem(it, `item ${i + 1}`, errors);
      if (!out) return;
      if (out.id) {
        if (ids.has(out.id)) errors.push(`item ${i + 1}: duplicate id ${out.id}`);
        ids.add(out.id);
      }
      value.items.push(out);
    });
  }
  return { ok: errors.length === 0, value, errors };
}

// ---------- owner side: build and sign ----------

// input: { realm, seq, items, validDays = 90 } -> validated news payload
function buildNews(input, { now = new Date() } = {}) {
  const days = Number.isInteger(input.validDays) && input.validDays >= 1 && input.validDays <= 365 ? input.validDays : 90;
  const issued = new Date(Math.floor(now.getTime() / 1000) * 1000);
  const v = validateNews({
    schema: 1,
    kind: KIND,
    realm: input.realm,
    seq: input.seq,
    issued: isoSeconds(issued),
    expires: isoSeconds(new Date(issued.getTime() + days * 86400 * 1000)),
    items: input.items || []
  });
  if (!v.ok) throw Object.assign(new Error(v.errors.join(' ')), { friendly: true });
  return v.value;
}

function signNews(news, privateKeyPem) {
  const v = validateNews(news);
  if (!v.ok) throw new Error(`refusing to sign invalid news: ${v.errors.join('; ')}`);
  return signDocument(FORMAT, v.value, privateKeyPem);
}

// ---------- player side: verify, order, load with an offline cache ----------

// opts: { now = Date.now(), minSeq = 0, ignoreExpiry = false }
// Returns { ok: true, news } or { ok: false, reason, rollback?, expired? }.
function verifyNews(envelope, publicKeyB64, opts = {}) {
  const now = opts.now != null ? opts.now : Date.now();
  const d = openDocument(envelope, { format: FORMAT, publicKey: publicKeyB64, what: 'news.json' });
  if (!d.ok) return d;
  const v = validateNews(d.payload);
  if (!v.ok) return { ok: false, reason: `signed news is invalid: ${v.errors.slice(0, 3).join('; ')}` };
  if (!opts.ignoreExpiry && Date.parse(v.value.expires) <= now) return { ok: false, reason: `news expired on ${v.value.expires}`, expired: true };
  if (Date.parse(v.value.issued) > now + 24 * 3600 * 1000) return { ok: false, reason: 'news is dated in the future' };
  if (opts.minSeq && v.value.seq < opts.minSeq) return { ok: false, reason: `older news (seq ${v.value.seq}) than already seen (seq ${opts.minSeq}); refusing a rollback`, rollback: true };
  return { ok: true, news: v.value };
}

// Items dated in the future are scheduled: they stay hidden until their date (5 minutes of clock
// slack). Pinned items first, then newest first; the id breaks ties so the order never jitters.
function orderNews(items, { now = Date.now() } = {}) {
  return (items || [])
    .filter((it) => Date.parse(it.date) <= now + 5 * 60 * 1000)
    .sort((a, b) => (b.pinned === true) - (a.pinned === true) || Date.parse(b.date) - Date.parse(a.date) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
}

// What the player sees: { news: announcements, season and chronicle items; realm: content changes }.
// Realm items for events that are already over (at more than 6 hours ago) are dropped.
function forPlayer(news, { now = Date.now(), limit = 30, realmLimit = 6 } = {}) {
  const ordered = orderNews(news ? news.items : [], { now });
  const realm = ordered.filter((it) => it.type === 'realm' && !(it.at && Date.parse(it.at) < now - 6 * 3600 * 1000));
  return { news: ordered.filter((it) => it.type !== 'realm').slice(0, limit), realm: realm.slice(0, realmLimit) };
}

// The news sibling of the server list: https://host/path/servers.json -> https://host/path/news.json.
function siblingUrl(manifestUrl, name) {
  if (typeof manifestUrl !== 'string' || !manifestUrl) return '';
  try {
    const u = new URL(name, manifestUrl);
    u.search = '';
    u.hash = '';
    return u.toString();
  } catch {
    return '';
  }
}

// Online first, then the saved copy, then the copy inside the build. Never goes back to a feed
// older (lower seq) than one this PC has accepted.
// deps: { fetchText(url) -> text }
// input: { url, publicKey, cacheText, maxSeq, bundled, now }
// Returns { source: 'online'|'cache'|'bundled'|'none', news|null, reason|null, cacheText?, maxSeq }.
async function loadNews(input, deps) {
  const now = input.now != null ? input.now : Date.now();
  const maxSeq = Number.isInteger(input.maxSeq) && input.maxSeq > 0 ? input.maxSeq : 0;
  const notes = [];
  if (!input.publicKey) return { source: 'none', news: null, reason: 'This build has no public key, so it cannot trust any news.', maxSeq };
  if (input.url) {
    try {
      const textIn = await deps.fetchText(input.url);
      const v = verifyNews(textIn, input.publicKey, { now, minSeq: maxSeq });
      if (v.ok) return { source: 'online', news: v.news, reason: null, cacheText: textIn, maxSeq: Math.max(maxSeq, v.news.seq) };
      notes.push(`Downloaded news refused: ${v.reason}.`);
    } catch (e) {
      notes.push(`Could not download the news (${e.message}).`);
    }
  }
  if (input.cacheText) {
    const v = verifyNews(input.cacheText, input.publicKey, { now, minSeq: maxSeq, ignoreExpiry: true });
    if (v.ok) return { source: 'cache', news: v.news, reason: notes.join(' ') || null, maxSeq };
  }
  if (input.bundled) {
    const v = verifyNews(input.bundled, input.publicKey, { now, minSeq: maxSeq, ignoreExpiry: true });
    if (v.ok) return { source: 'bundled', news: v.news, reason: notes.join(' ') || null, maxSeq };
  }
  return { source: 'none', news: null, reason: notes.join(' ') || (input.url ? 'No news yet.' : 'No news address in this build.'), maxSeq };
}

module.exports = {
  FORMAT,
  KIND,
  TYPES,
  TAGS,
  MAX_ITEMS,
  isIsoDate,
  isoSeconds,
  signDocument,
  openDocument,
  validateNews,
  buildNews,
  signNews,
  verifyNews,
  orderNews,
  forPlayer,
  siblingUrl,
  loadNews
};
