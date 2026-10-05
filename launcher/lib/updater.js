'use strict';

// update.json: the signed update manifest for Realm (the player app), and the download that it
// vouches for.
//
// Envelope: { "format": "realm-update/1", "alg": "Ed25519", "keyId", "payload", "signature" }, signed
// with the owner's key like servers.json and news.json (lib/news.js signDocument / openDocument).
// The payload names its kind ("realm-update") so no other signed Realm file can pass for it.
//
// Payload:
//   { schema: 1, kind: "realm-update", app: "realm-player", seq, issued, expires,
//     version: "1.1.0", released, hotfix: false, minVersion?: "1.0.2",
//     file: { name: "Realm-Setup-1.1.0.exe", url: "https://...", size: 81234567, sha256: "<64 hex>" },
//     notes: { title?: "...", items: ["...", ...] } }
//
// The rules the player follows:
// - an update is offered only when the signature matches the key built into the app, the manifest
//   has not expired, is not older (seq) than one already accepted, and its version is newer;
// - hotfix (or a minVersion above the running version) marks it urgent: the banner cannot be
//   dismissed and the download starts by itself, but nothing is ever run without a click;
// - the installer is streamed to disk, its size and SHA-256 must equal the signed values, and it is
//   hashed again right before it runs. A mismatch deletes the file. Unsigned or mismatched files
//   are never run.
// No child_process here: the player app hands the verified installer to Windows (shell.openPath).

const crypto = require('crypto');
const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const M = require('./shared/manifest');
const N = require('./news');

const FORMAT = 'realm-update/1';
const KIND = 'realm-update';
const APP = 'realm-player';
const MAX_SIZE = 512 * 1024 * 1024;
const VERSION_RE = /^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,5})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;
const FILE_NAME_RE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\.exe$/;
const SHA_RE = /^[0-9a-f]{64}$/;

// ---------- versions ----------

function parseVersion(v) {
  const m = typeof v === 'string' ? VERSION_RE.exec(v.trim()) : null;
  if (!m) return null;
  return { major: +m[1], minor: +m[2], patch: +m[3], pre: m[4] ? m[4].split('.') : [] };
}

// Semantic-version precedence: 1.0.0-beta.2 < 1.0.0-beta.10 < 1.0.0 < 1.0.1. Returns -1, 0 or 1.
// An unparseable version sorts below every valid one.
function compareVersions(a, b) {
  const x = parseVersion(a);
  const y = parseVersion(b);
  if (!x || !y) return x ? 1 : y ? -1 : 0;
  for (const k of ['major', 'minor', 'patch']) if (x[k] !== y[k]) return x[k] < y[k] ? -1 : 1;
  if (!x.pre.length || !y.pre.length) return x.pre.length === y.pre.length ? 0 : x.pre.length ? -1 : 1;
  for (let i = 0; i < Math.max(x.pre.length, y.pre.length); i++) {
    const p = x.pre[i];
    const q = y.pre[i];
    if (p === undefined) return -1;
    if (q === undefined) return 1;
    const pn = /^\d+$/.test(p);
    const qn = /^\d+$/.test(q);
    if (pn && qn && +p !== +q) return +p < +q ? -1 : 1;
    if (pn !== qn) return pn ? -1 : 1;
    if (!pn && p !== q) return p < q ? -1 : 1;
  }
  return 0;
}

// ---------- schema ----------

// opts.allowLocalHttp: development runs may download from http://127.0.0.1 (the walk-through).
function downloadUrlAllowed(u, { allowLocalHttp = false } = {}) {
  if (typeof u !== 'string' || u.length > 400) return false;
  if (M.isWebUrl(u, { httpsOnly: true })) return true;
  if (!allowLocalHttp) return false;
  try {
    const x = new URL(u);
    return x.protocol === 'http:' && x.hostname === '127.0.0.1' && !x.username && !x.password;
  } catch {
    return false;
  }
}

function validateUpdate(input, opts = {}) {
  const errors = [];
  const m = input && typeof input === 'object' && !Array.isArray(input) ? input : {};
  if (m.schema !== 1) errors.push('schema must be 1');
  if (m.kind !== KIND) errors.push(`kind must be ${KIND}`);
  if (m.app !== APP) errors.push(`app must be ${APP}`);
  const value = {
    schema: 1,
    kind: KIND,
    app: APP,
    seq: Number.isInteger(m.seq) && m.seq >= 1 && m.seq <= 2 ** 31 - 1 ? m.seq : (errors.push('seq must be a whole number from 1 to 2147483647'), null),
    issued: m.issued,
    expires: m.expires,
    version: parseVersion(m.version) ? m.version.trim() : (errors.push('version must look like 1.2.3'), ''),
    released: m.released,
    hotfix: m.hotfix === true,
    file: null,
    notes: { title: '', items: [] }
  };
  if (m.hotfix != null && typeof m.hotfix !== 'boolean') errors.push('hotfix must be true or false');
  for (const k of ['issued', 'expires', 'released']) if (!N.isIsoDate(m[k])) errors.push(`${k} must be an ISO-8601 UTC time`);
  if (N.isIsoDate(m.issued) && N.isIsoDate(m.expires) && Date.parse(m.expires) <= Date.parse(m.issued)) errors.push('expires must be after issued');
  if (m.minVersion != null && m.minVersion !== '') {
    if (parseVersion(m.minVersion) && compareVersions(m.minVersion, m.version) <= 0) value.minVersion = m.minVersion.trim();
    else errors.push('minVersion must look like 1.2.3 and not be above version');
  }
  const f = m.file && typeof m.file === 'object' && !Array.isArray(m.file) ? m.file : null;
  if (!f) errors.push('file is required');
  else {
    value.file = {
      name: typeof f.name === 'string' && FILE_NAME_RE.test(f.name) ? f.name : (errors.push('file.name must be a plain .exe file name'), ''),
      url: downloadUrlAllowed(f.url, opts) ? f.url : (errors.push('file.url must be an https:// address'), ''),
      size: Number.isInteger(f.size) && f.size >= 1 && f.size <= MAX_SIZE ? f.size : (errors.push(`file.size must be a whole number of bytes up to ${MAX_SIZE}`), 0),
      sha256: typeof f.sha256 === 'string' && SHA_RE.test(f.sha256.toLowerCase()) ? f.sha256.toLowerCase() : (errors.push('file.sha256 must be 64 hex digits'), '')
    };
  }
  if (m.notes != null) {
    const n = m.notes && typeof m.notes === 'object' && !Array.isArray(m.notes) ? m.notes : (errors.push('notes must be an object'), {});
    if (n.title != null && n.title !== '') {
      if (typeof n.title === 'string' && n.title.trim().length <= 80 && !/[\u0000-\u001f\u007f]/.test(n.title)) value.notes.title = n.title.trim();
      else errors.push('notes.title must be one line of up to 80 characters');
    }
    if (n.items != null) {
      if (!Array.isArray(n.items) || n.items.length > 20) errors.push('notes.items must be a list of at most 20 lines');
      else
        n.items.forEach((s, i) => {
          if (typeof s === 'string' && s.trim() && s.trim().length <= 240 && !/[\u0000-\u001f\u007f]/.test(s)) value.notes.items.push(s.trim());
          else errors.push(`notes line ${i + 1} must be one line of up to 240 characters`);
        });
    }
  }
  return { ok: errors.length === 0, value, errors };
}

// ---------- owner side ----------

// input: { seq, version, hotfix, minVersion, url, size, sha256, notes, validDays = 60 }
function buildUpdate(input, { now = new Date() } = {}) {
  const days = Number.isInteger(input.validDays) && input.validDays >= 1 && input.validDays <= 365 ? input.validDays : 60;
  const issued = new Date(Math.floor(now.getTime() / 1000) * 1000);
  const version = String(input.version || '');
  const m = {
    schema: 1,
    kind: KIND,
    app: APP,
    seq: input.seq,
    issued: N.isoSeconds(issued),
    expires: N.isoSeconds(new Date(issued.getTime() + days * 86400 * 1000)),
    version,
    released: input.released || N.isoSeconds(issued),
    hotfix: input.hotfix === true,
    file: { name: input.name || installerName(version), url: input.url, size: input.size, sha256: input.sha256 },
    notes: input.notes || { items: [] }
  };
  if (input.minVersion) m.minVersion = input.minVersion;
  const v = validateUpdate(m, { allowLocalHttp: input.allowLocalHttp === true });
  if (!v.ok) throw Object.assign(new Error(v.errors.join(' ')), { friendly: true });
  return v.value;
}

function signUpdate(update, privateKeyPem, opts = {}) {
  const v = validateUpdate(update, opts);
  if (!v.ok) throw new Error(`refusing to sign an invalid update: ${v.errors.join('; ')}`);
  return N.signDocument(FORMAT, v.value, privateKeyPem);
}

// ---------- player side ----------

// opts: { now, minSeq, ignoreExpiry, allowLocalHttp } -> { ok: true, update } | { ok: false, reason }
function verifyUpdate(envelope, publicKeyB64, opts = {}) {
  const now = opts.now != null ? opts.now : Date.now();
  const d = N.openDocument(envelope, { format: FORMAT, publicKey: publicKeyB64, maxBytes: 64 * 1024, what: 'update.json' });
  if (!d.ok) return d;
  const v = validateUpdate(d.payload, opts);
  if (!v.ok) return { ok: false, reason: `signed update is invalid: ${v.errors.slice(0, 3).join('; ')}` };
  if (!opts.ignoreExpiry && Date.parse(v.value.expires) <= now) return { ok: false, reason: `update manifest expired on ${v.value.expires}`, expired: true };
  if (Date.parse(v.value.issued) > now + 24 * 3600 * 1000) return { ok: false, reason: 'update manifest is dated in the future' };
  if (opts.minSeq && v.value.seq < opts.minSeq) return { ok: false, reason: `older update manifest (seq ${v.value.seq}) than already seen (seq ${opts.minSeq}); refusing a rollback`, rollback: true };
  return { ok: true, update: v.value };
}

// Is this update urgent for the running version?
function isUrgent(update, currentVersion) {
  return !!update && (update.hotfix === true || (!!update.minVersion && compareVersions(currentVersion, update.minVersion) < 0));
}

// input: { currentVersion, url, publicKey, maxSeq, now, allowLocalHttp }; deps: { fetchText(url) }
// Returns { status: 'available'|'current'|'error'|'off', update?, hotfix, reason, cacheText?, maxSeq }.
async function checkForUpdate(input, deps) {
  const maxSeq = Number.isInteger(input.maxSeq) && input.maxSeq > 0 ? input.maxSeq : 0;
  if (!input.publicKey || !input.url) return { status: 'off', hotfix: false, reason: input.publicKey ? 'No update address in this build.' : 'This build has no public key, so it cannot trust any update.', maxSeq };
  let textIn;
  try {
    textIn = await deps.fetchText(input.url);
  } catch (e) {
    return { status: 'error', hotfix: false, reason: `Could not check for updates (${e.message}).`, maxSeq };
  }
  const v = verifyUpdate(textIn, input.publicKey, { now: input.now, minSeq: maxSeq, allowLocalHttp: input.allowLocalHttp });
  if (!v.ok) return { status: 'error', hotfix: false, reason: `Update manifest refused: ${v.reason}.`, maxSeq };
  const next = Math.max(maxSeq, v.update.seq);
  if (compareVersions(v.update.version, input.currentVersion) <= 0) return { status: 'current', hotfix: false, reason: null, update: v.update, cacheText: textIn, maxSeq: next };
  return { status: 'available', hotfix: isUrgent(v.update, input.currentVersion), reason: null, update: v.update, cacheText: textIn, maxSeq: next };
}

function installerName(version) {
  if (!parseVersion(version)) throw new TypeError('invalid version');
  return `Realm-Setup-${version}.exe`;
}

function sameHex(a, b) {
  const x = Buffer.from(String(a), 'hex');
  const y = Buffer.from(String(b), 'hex');
  return x.length === 32 && y.length === 32 && crypto.timingSafeEqual(x, y);
}

async function hashFile(file) {
  const hash = crypto.createHash('sha256');
  let size = 0;
  await new Promise((resolve, reject) => {
    fs.createReadStream(file)
      .on('data', (c) => {
        size += c.length;
        hash.update(c);
      })
      .on('error', reject)
      .on('end', resolve);
  });
  return { sha256: hash.digest('hex'), size };
}

// True only when the file on disk has exactly the signed size and SHA-256.
async function verifyInstaller(file, update) {
  try {
    const h = await hashFile(file);
    return h.size === update.file.size && sameHex(h.sha256, update.file.sha256);
  } catch {
    return false;
  }
}

function friendly(message) {
  return Object.assign(new Error(message), { friendly: true });
}

// Streams the installer into dir and checks it against the signed size and SHA-256.
// deps: { fetch(url, init) -> Response with a web ReadableStream body }
// opts: { dir, onProgress(received, total), signal, allowLocalHttp }
// Returns { file, reused }. Throws a friendly error and leaves nothing behind on any mismatch.
async function downloadInstaller(update, opts, deps) {
  if (!update || !update.file || !downloadUrlAllowed(update.file.url, opts)) throw friendly('The update address is not an https:// address. Nothing was downloaded.');
  await fsp.mkdir(opts.dir, { recursive: true });
  const file = path.join(opts.dir, installerName(update.version));
  if (await verifyInstaller(file, update)) return { file, reused: true };
  const part = `${file}.partial`;
  await fsp.rm(part, { force: true });
  let fh = null;
  try {
    const res = await deps.fetch(update.file.url, { signal: opts.signal, cache: 'no-store', redirect: 'follow' });
    if (!res.ok) throw friendly(`The download failed (HTTP ${res.status}). Try again later.`);
    const declared = Number(res.headers && res.headers.get ? res.headers.get('content-length') : NaN);
    if (Number.isFinite(declared) && declared > 0 && declared !== update.file.size) throw friendly('The download is not the size the signed update says. It was stopped and deleted.');
    if (!res.body) throw friendly('The download was empty.');
    fh = await fsp.open(part, 'w');
    const hash = crypto.createHash('sha256');
    const reader = res.body.getReader();
    let received = 0;
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      const chunk = Buffer.from(value.buffer, value.byteOffset, value.byteLength);
      received += chunk.length;
      if (received > update.file.size) {
        reader.cancel().catch(() => {});
        throw friendly('The download is larger than the signed update says. It was stopped and deleted.');
      }
      hash.update(chunk);
      await fh.write(chunk);
      if (opts.onProgress) opts.onProgress(received, update.file.size);
    }
    await fh.close();
    fh = null;
    if (received !== update.file.size) throw friendly('The download ended early. Try again.');
    if (!sameHex(hash.digest('hex'), update.file.sha256)) throw friendly('The download does not match the signed update (SHA-256 differs). It was deleted and will not run.');
    await fsp.rm(file, { force: true });
    await fsp.rename(part, file);
    return { file, reused: false };
  } catch (e) {
    if (fh) await fh.close().catch(() => {});
    await fsp.rm(part, { force: true }).catch(() => {});
    if (e && e.name === 'AbortError') throw friendly('The download was cancelled.');
    throw e && e.friendly ? e : friendly(`The download failed (${e && e.message ? e.message : e}). Try again later.`);
  }
}

// Removes installers of other versions and leftover partial downloads.
async function pruneDownloads(dir, keepVersion) {
  let names = [];
  try {
    names = await fsp.readdir(dir);
  } catch {
    return 0;
  }
  const keep = keepVersion && parseVersion(keepVersion) ? installerName(keepVersion) : null;
  let n = 0;
  for (const name of names) {
    if (!/^Realm-Setup-.+\.exe(\.partial)?$/.test(name) || name === keep) continue;
    await fsp.rm(path.join(dir, name), { force: true }).catch(() => {});
    n++;
  }
  return n;
}

// "What's new" after an update: shown once, when the running version is newer than the last one
// this PC ran. Notes come from the signed manifest that offered the update (kept in the profile),
// else from the notes inside the build. A first install shows nothing.
function whatsNew({ currentVersion, lastVersion, cachedUpdate, bundledNotes }) {
  if (!lastVersion || compareVersions(currentVersion, lastVersion) <= 0) return null;
  let notes = null;
  if (cachedUpdate && cachedUpdate.version === currentVersion && cachedUpdate.notes && cachedUpdate.notes.items.length) notes = cachedUpdate.notes;
  else if (bundledNotes && bundledNotes[currentVersion] && Array.isArray(bundledNotes[currentVersion].items)) notes = bundledNotes[currentVersion];
  return {
    version: currentVersion,
    from: lastVersion,
    title: (notes && typeof notes.title === 'string' && notes.title) || `Realm ${currentVersion}`,
    items: notes ? notes.items.filter((s) => typeof s === 'string' && s.trim()).slice(0, 20) : []
  };
}

module.exports = {
  FORMAT,
  KIND,
  APP,
  MAX_SIZE,
  parseVersion,
  compareVersions,
  downloadUrlAllowed,
  validateUpdate,
  buildUpdate,
  signUpdate,
  verifyUpdate,
  isUrgent,
  checkForUpdate,
  installerName,
  hashFile,
  verifyInstaller,
  downloadInstaller,
  pruneDownloads,
  whatsNew
};
