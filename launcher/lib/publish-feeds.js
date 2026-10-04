'use strict';

// Realm Steward: "Publish news" and "Publish update" (ROADMAP STW-4, PLA-3).
//
// Both write a signed file next to servers.json, in the same output folder the server list uses, with
// the same owner key (lib/news.js buildNews/signNews, lib/updater.js buildUpdate/signUpdate). Nothing
// is uploaded: the owner hosts the files where servers.json is hosted (docs/player-launcher.md).
//
// Rules this module keeps:
// - seq only goes up. The next seq is one more than the highest of the saved counter and the seq of a
//   news.json / update.json already in the output folder signed with this key, so a lost profile or a
//   second PC cannot publish a feed players would refuse as a rollback.
// - every file is checked with the public key exactly as the player app will check it before it is
//   written, and written atomically (a crash never leaves half a file).
// - drafts live in <userData>\publish\feeds.json. If that file is damaged it is never overwritten:
//   the screens say so and writing stops until the owner moves it away.
// - an update is only signed for an installer the owner picked on disk: its size and SHA-256 are read
//   from the file, and when the release folder has SHA256SUMS.txt (scripts/build-release.mjs) the
//   file must match the line for it.

const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const N = require('./news');
const U = require('./updater');
const M = require('./shared/manifest');
const F = require('./fsops');

const STORE_VERSION = 1;
const MAX_DRAFT_ITEMS = N.MAX_ITEMS;
const INSTALLER_RE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\.exe$/;
const PLAYER_INSTALLER_VERSION_RE = /^Realm-Setup-(.+)\.exe$/;

function friendly(message) {
  return Object.assign(new Error(message), { friendly: true });
}

// ---------- drafts store ----------

class FeedStore {
  constructor(dir) {
    this.dir = dir;
    this.file = path.join(dir, 'feeds.json');
    this.data = { version: STORE_VERSION, news: { seq: 0, validDays: 90, items: [], lastWritten: null }, update: { seq: 0, validDays: 60, draft: {}, lastWritten: null } };
    this.error = null;
  }

  async load() {
    let text;
    try {
      text = await fsp.readFile(this.file, 'utf8');
    } catch (e) {
      if (e.code === 'ENOENT') return this;
      this.error = `Your publishing drafts (${this.file}) could not be read: ${e.message}`;
      return this;
    }
    try {
      const raw = JSON.parse(text.replace(/^﻿/, ''));
      if (!raw || typeof raw !== 'object' || Array.isArray(raw) || !raw.news || !raw.update) throw new Error('not a drafts file');
      const seqOf = (v) => (Number.isInteger(v) && v >= 0 && v <= 2 ** 31 - 1 ? v : 0);
      this.data.news.seq = seqOf(raw.news.seq);
      this.data.news.validDays = Number.isInteger(raw.news.validDays) ? raw.news.validDays : 90;
      this.data.news.items = Array.isArray(raw.news.items) ? raw.news.items.filter((x) => x && typeof x === 'object' && !Array.isArray(x)).slice(0, MAX_DRAFT_ITEMS) : [];
      this.data.news.lastWritten = typeof raw.news.lastWritten === 'string' ? raw.news.lastWritten : null;
      this.data.update.seq = seqOf(raw.update.seq);
      this.data.update.validDays = Number.isInteger(raw.update.validDays) ? raw.update.validDays : 60;
      this.data.update.draft = raw.update.draft && typeof raw.update.draft === 'object' && !Array.isArray(raw.update.draft) ? raw.update.draft : {};
      this.data.update.lastWritten = typeof raw.update.lastWritten === 'string' ? raw.update.lastWritten : null;
    } catch (e) {
      // Never overwrite a damaged file: the seq counters in it matter.
      this.error = `Your publishing drafts (${this.file}) are damaged (${e.message.slice(0, 80)}). Realm will not overwrite them. Move the file away to start again; the next version number is then read from the files you published.`;
    }
    return this;
  }

  async save() {
    if (this.error) throw friendly(this.error);
    await fsp.mkdir(this.dir, { recursive: true });
    await F.writeFileAtomic(this.file, JSON.stringify(this.data, null, 2) + '\n');
  }
}

// ---------- news ----------

// Keeps only the fields an item can have, as plain strings. Validation proper is lib/news.js.
function cleanDraftItem(it) {
  const s = (v, max) => (typeof v === 'string' ? v.slice(0, max) : '');
  const out = { id: s(it.id, 40), type: s(it.type, 20), title: s(it.title, 200), body: s(it.body, 1200), date: s(it.date, 30) };
  if (it.pinned === true) out.pinned = true;
  for (const k of ['tag', 'at', 'link', 'server']) if (typeof it[k] === 'string' && it[k].trim()) out[k] = s(it[k], k === 'link' ? 300 : 40).trim();
  return out;
}

// Draft item -> the item lib/news.js validates. Empty optional fields are dropped.
function toNewsItem(d) {
  const it = { id: (d.id || '').trim(), type: d.type, title: (d.title || '').trim(), date: (d.date || '').trim() };
  if (d.body && d.body.trim()) it.body = d.body.trim();
  if (d.pinned === true) it.pinned = true;
  if (d.type === 'realm') it.tag = d.tag || 'other';
  for (const k of ['at', 'link', 'server']) if (d[k]) it[k] = d[k];
  return it;
}

// An item id from its title: "The Grey Heron stands" -> "the-grey-heron-stands" (unique in the list).
function itemIdFrom(title, taken = []) {
  const base = String(title || 'news').toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 32) || 'news';
  let id = base;
  for (let n = 2; taken.includes(id); n++) id = `${base.slice(0, 36)}-${n}`;
  return id;
}

// seq of a signed file already published with this key, or 0.
async function publishedSeq(file, publicKey, verify) {
  let text;
  try {
    text = await fsp.readFile(file, 'utf8');
  } catch {
    return 0;
  }
  const v = verify(text, publicKey, { ignoreExpiry: true, allowLocalHttp: true });
  if (!v.ok) return 0;
  const doc = v.news || v.update;
  return doc && Number.isInteger(doc.seq) ? doc.seq : 0;
}

async function nextNewsSeq(store, outDir, publicKey) {
  return Math.max(store.data.news.seq, await publishedSeq(path.join(outDir, 'news.json'), publicKey, N.verifyNews)) + 1;
}

async function nextUpdateSeq(store, outDir, publicKey) {
  return Math.max(store.data.update.seq, await publishedSeq(path.join(outDir, 'update.json'), publicKey, U.verifyUpdate)) + 1;
}

// input: { realm, items (drafts), validDays }; key: { pem, publicKey }. Writes <outDir>\news.json.
async function writeNews(store, { realm, items, validDays }, { key, outDir, now = new Date() }) {
  if (store.error) throw friendly(store.error);
  if (!Array.isArray(items)) throw friendly('The news list is missing.');
  if (items.length > N.MAX_ITEMS) throw friendly(`A news feed holds at most ${N.MAX_ITEMS} items. Remove some old ones first.`);
  const drafts = items.map(cleanDraftItem);
  const seq = await nextNewsSeq(store, outDir, key.publicKey);
  const days = Number.isInteger(validDays) && validDays >= 1 && validDays <= 365 ? validDays : 90;
  let news;
  try {
    news = N.buildNews({ realm, seq, validDays: days, items: drafts.map(toNewsItem) }, { now });
  } catch (e) {
    throw friendly(`The news cannot be signed yet: ${e.message}`);
  }
  const envelope = N.signNews(news, key.pem);
  const check = N.verifyNews(JSON.stringify(envelope), key.publicKey, { now: now.getTime() });
  if (!check.ok) throw new Error(`self-check failed: ${check.reason}`);
  await fsp.mkdir(outDir, { recursive: true });
  const file = path.join(outDir, 'news.json');
  await F.writeFileAtomic(file, JSON.stringify(envelope, null, 2) + '\n');
  store.data.news = { seq, validDays: days, items: drafts, lastWritten: new Date(now.getTime()).toISOString() };
  await store.save();
  const shown = N.forPlayer(news, { now: now.getTime() });
  return { file, seq, expires: news.expires, items: news.items.length, keyId: envelope.keyId, shownNow: shown.news.length + shown.realm.length, scheduled: news.items.filter((i) => Date.parse(i.date) > now.getTime()).length };
}

// ---------- update ----------

// Reads SHA256SUMS.txt next to the installer (sha256sum format: "<hex>  <name>"). Returns the hex for
// that name, or null when there is no such file or line.
async function sumsFor(installer) {
  let text;
  try {
    text = await fsp.readFile(path.join(path.dirname(installer), 'SHA256SUMS.txt'), 'utf8');
  } catch {
    return null;
  }
  const name = path.basename(installer);
  for (const line of text.split(/\r?\n/)) {
    const m = /^([0-9a-fA-F]{64})\s+\*?(.+)$/.exec(line.trim());
    if (m && m[2].trim() === name) return m[1].toLowerCase();
  }
  return null;
}

// Facts about an installer the owner picked: { path, name, size, sha256, version, sums }.
async function inspectInstaller(file) {
  if (typeof file !== 'string' || !file || file.length > 400) throw friendly('Choose the player installer first.');
  const name = path.basename(file);
  if (!INSTALLER_RE.test(name)) throw friendly(`${name} is not a plain .exe file name (letters, digits, dots, dashes; up to 80 characters).`);
  if (/steward/i.test(name)) throw friendly(`${name} is the Realm Steward installer. Players update with the player installer, Realm-Setup-<version>.exe.`);
  if (!(await F.isFile(file))) throw friendly(`${file} was not found.`);
  const h = await U.hashFile(file);
  if (h.size < 1 || h.size > U.MAX_SIZE) throw friendly(`${name} is ${h.size} bytes; an installer must be between 1 byte and ${U.MAX_SIZE} bytes.`);
  const listed = await sumsFor(file);
  if (listed && listed !== h.sha256) throw friendly(`${name} does not match its line in SHA256SUMS.txt. The file changed after the release was built; build the release again.`);
  const m = PLAYER_INSTALLER_VERSION_RE.exec(name);
  const version = m && U.parseVersion(m[1]) ? m[1] : null;
  return { path: file, name, size: h.size, sha256: h.sha256, version, sums: listed ? 'match' : 'none' };
}

function notesFrom(input) {
  const lines = Array.isArray(input.notesItems)
    ? input.notesItems
    : String(input.notesItems || '')
        .split(/\r?\n/)
        .map((s) => s.replace(/^\s*[-*•]\s*/, ''));
  return { title: String(input.notesTitle || '').trim(), items: lines.map((s) => String(s).trim()).filter(Boolean) };
}

// input: { installer (path), version, url, hotfix, minVersion, notesTitle, notesItems, validDays }
async function writeUpdate(store, input, { key, outDir, now = new Date(), allowLocalHttp = false }) {
  if (store.error) throw friendly(store.error);
  const inst = await inspectInstaller(input.installer);
  const version = String(input.version || inst.version || '').trim();
  if (!U.parseVersion(version)) throw friendly('Enter the version of this installer, like 1.1.0.');
  if (inst.version && inst.version !== version) throw friendly(`The installer is named for version ${inst.version}, not ${version}.`);
  const url = String(input.url || '').trim();
  if (!U.downloadUrlAllowed(url, { allowLocalHttp })) throw friendly('The download address must start with https:// (where you upload the installer, for example a GitHub release).');
  const minVersion = String(input.minVersion || '').trim();
  const days = Number.isInteger(input.validDays) && input.validDays >= 1 && input.validDays <= 365 ? input.validDays : 60;
  const seq = await nextUpdateSeq(store, outDir, key.publicKey);
  let update;
  try {
    update = U.buildUpdate(
      { seq, version, hotfix: input.hotfix === true, minVersion: minVersion || undefined, url, name: inst.name, size: inst.size, sha256: inst.sha256, notes: notesFrom(input), validDays: days, allowLocalHttp },
      { now }
    );
  } catch (e) {
    throw friendly(`The update cannot be signed yet: ${e.message}`);
  }
  const envelope = U.signUpdate(update, key.pem, { allowLocalHttp });
  const check = U.verifyUpdate(JSON.stringify(envelope), key.publicKey, { now: now.getTime(), allowLocalHttp });
  if (!check.ok) throw new Error(`self-check failed: ${check.reason}`);
  await fsp.mkdir(outDir, { recursive: true });
  const file = path.join(outDir, 'update.json');
  await F.writeFileAtomic(file, JSON.stringify(envelope, null, 2) + '\n');
  store.data.update = {
    seq,
    validDays: days,
    lastWritten: new Date(now.getTime()).toISOString(),
    draft: { installer: inst.path, version, url, hotfix: update.hotfix, minVersion, notesTitle: update.notes.title, notesItems: update.notes.items }
  };
  await store.save();
  return { file, seq, version, expires: update.expires, urgent: update.hotfix === true || !!update.minVersion, size: inst.size, sha256: inst.sha256, sums: inst.sums, keyId: envelope.keyId };
}

// ---------- Steward wiring ----------

// deps: { handle, userData, readSigningKey(), outDir(), manifestUrl(), realmName(), dialog, win(), log }
function registerSteward(deps) {
  const store = new FeedStore(path.join(deps.userData, 'publish'));
  const ready = store.load();
  const keyOr = async () => {
    const key = await deps.readSigningKey();
    if (!key) throw friendly('Create the signing key first (Publish > Server list).');
    return key;
  };

  deps.handle('feeds:status', async () => {
    await ready;
    const key = await deps.readSigningKey().catch(() => null);
    const outDir = deps.outDir();
    const manifestUrl = deps.manifestUrl();
    const pub = key ? key.publicKey : null;
    const existing = async (name, verify) => {
      try {
        const text = await fsp.readFile(path.join(outDir, name), 'utf8');
        if (!pub) return { exists: true, ok: false, reason: 'no signing key' };
        const v = verify(text, pub, { ignoreExpiry: true, allowLocalHttp: true });
        const doc = v.ok ? v.news || v.update : null;
        return v.ok ? { exists: true, ok: true, seq: doc.seq, expires: doc.expires, expired: Date.parse(doc.expires) <= Date.now() } : { exists: true, ok: false, reason: v.reason };
      } catch {
        return { exists: false };
      }
    };
    return {
      key: key ? { keyId: M.keyIdOf(key.publicKey) } : null,
      storeError: store.error,
      outDir,
      realm: deps.realmName(),
      newsUrl: N.siblingUrl(manifestUrl, 'news.json'),
      updateUrl: N.siblingUrl(manifestUrl, 'update.json'),
      types: N.TYPES,
      tags: N.TAGS,
      maxItems: N.MAX_ITEMS,
      news: { items: store.data.news.items, validDays: store.data.news.validDays, lastWritten: store.data.news.lastWritten, nextSeq: pub ? await nextNewsSeq(store, outDir, pub) : store.data.news.seq + 1, published: await existing('news.json', N.verifyNews) },
      update: { draft: store.data.update.draft, validDays: store.data.update.validDays, lastWritten: store.data.update.lastWritten, nextSeq: pub ? await nextUpdateSeq(store, outDir, pub) : store.data.update.seq + 1, published: await existing('update.json', U.verifyUpdate) }
    };
  });

  deps.handle('feeds:saveNewsDraft', async (items, validDays) => {
    await ready;
    if (!Array.isArray(items) || items.length > MAX_DRAFT_ITEMS) throw friendly(`A news feed holds at most ${MAX_DRAFT_ITEMS} items.`);
    store.data.news.items = items.filter((x) => x && typeof x === 'object' && !Array.isArray(x)).map(cleanDraftItem);
    if (Number.isInteger(validDays) && validDays >= 1 && validDays <= 365) store.data.news.validDays = validDays;
    await store.save();
    return { saved: store.data.news.items.length };
  });

  deps.handle('feeds:writeNews', async (input) => {
    await ready;
    if (!input || typeof input !== 'object' || Array.isArray(input)) throw new TypeError('news input must be an object');
    const key = await keyOr();
    const res = await writeNews(store, { realm: deps.realmName(), items: input.items, validDays: Number(input.validDays) }, { key, outDir: deps.outDir() });
    deps.log('info', `[publish] news.json version ${res.seq} written (${res.items} items)`);
    return res;
  });

  deps.handle('feeds:pickInstaller', async () => {
    const res = await deps.dialog.showOpenDialog(deps.win(), {
      title: 'Choose the player installer (Realm-Setup-<version>.exe)',
      properties: ['openFile', 'dontAddToRecent'],
      filters: [{ name: 'Installer', extensions: ['exe'] }]
    });
    if (res.canceled || !res.filePaths || !res.filePaths[0]) return null;
    return inspectInstaller(res.filePaths[0]);
  });

  deps.handle('feeds:inspectInstaller', async (file) => inspectInstaller(file));

  deps.handle('feeds:writeUpdate', async (input) => {
    await ready;
    if (!input || typeof input !== 'object' || Array.isArray(input)) throw new TypeError('update input must be an object');
    const key = await keyOr();
    const res = await writeUpdate(store, { ...input, validDays: Number(input.validDays) }, { key, outDir: deps.outDir(), allowLocalHttp: deps.allowLocalHttp === true });
    deps.log('info', `[publish] update.json version ${res.seq} written for Realm ${res.version}`);
    return res;
  });

  return { store, ready };
}

module.exports = { FeedStore, cleanDraftItem, toNewsItem, itemIdFrom, sumsFor, inspectInstaller, writeNews, writeUpdate, nextNewsSeq, nextUpdateSeq, registerSteward };
