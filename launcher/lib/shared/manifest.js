'use strict';

// servers.json: the signed list of Realm servers that the player client trusts.
//
// The hosted file is a small JSON envelope:
//   { "format": "realm-servers/1", "alg": "Ed25519", "keyId": "<16 hex>",
//     "payload": "<base64 of the exact manifest JSON bytes>", "signature": "<base64, 64 bytes>" }
// The signature covers the payload bytes exactly, so no JSON canonicalisation is needed.
// The public key is embedded in the player build; the private key never leaves the owner's PC.
//
// This module only VERIFIES (both editions use it). Signing lives in lib/publish.js (steward only).

const crypto = require('crypto');
const { isValidHost } = require('./steam');

const FORMAT = 'realm-servers/1';
const ALG = 'Ed25519';
const MAX_ENVELOPE_BYTES = 64 * 1024;
const MAX_SERVERS = 16;
const ID_RE = /^[a-z0-9][a-z0-9-]{0,23}$/;
const SPKI_ED25519_PREFIX = Buffer.from('302a300506032b6570032100', 'hex');
const CONTROL_RE = /[\u0000-\u001f\u007f\u2028\u2029]/;

// ---------- keys ----------

// Accepts the raw 32-byte public key or its SPKI DER, base64 encoded.
function publicKeyFromBase64(b64) {
  if (typeof b64 !== 'string' || !/^[A-Za-z0-9+/]+={0,2}$/.test(b64.trim())) throw new TypeError('public key must be base64');
  let der = Buffer.from(b64.trim(), 'base64');
  if (der.length === 32) der = Buffer.concat([SPKI_ED25519_PREFIX, der]);
  if (der.length !== 44 || !der.subarray(0, 12).equals(SPKI_ED25519_PREFIX)) throw new RangeError('not an Ed25519 public key');
  return crypto.createPublicKey({ key: der, format: 'der', type: 'spki' });
}

function rawPublicKey(keyObject) {
  const der = keyObject.export({ format: 'der', type: 'spki' });
  return der.subarray(der.length - 32);
}

function keyIdOf(publicKeyB64) {
  const raw = rawPublicKey(publicKeyFromBase64(publicKeyB64));
  return crypto.createHash('sha256').update(raw).digest('hex').slice(0, 16);
}

// ---------- schema ----------

function cleanText(v, max, name, errors, { required = true } = {}) {
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
  if (CONTROL_RE.test(s)) errors.push(`${name} contains control characters`);
  if (required && !s) errors.push(`${name} is required`);
  return s;
}

function cleanInt(v, min, max, name, errors) {
  if (!Number.isInteger(v) || v < min || v > max) {
    errors.push(`${name} must be a whole number from ${min} to ${max}`);
    return null;
  }
  return v;
}

function isIsoDate(s) {
  return typeof s === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,3})?Z$/.test(s) && Number.isFinite(Date.parse(s));
}

function isWebUrl(s, { httpsOnly = false } = {}) {
  try {
    const u = new URL(s);
    if (u.username || u.password || !u.hostname) return false;
    return u.protocol === 'https:' || (!httpsOnly && u.protocol === 'http:');
  } catch {
    return false;
  }
}

// Returns { ok, value, errors }. Unknown fields are dropped from value.
function validateManifest(input) {
  const errors = [];
  const m = input && typeof input === 'object' && !Array.isArray(input) ? input : {};
  if (m.schema !== 1) errors.push('schema must be 1');
  const value = {
    schema: 1,
    realm: cleanText(m.realm, 60, 'realm', errors),
    seq: cleanInt(m.seq, 1, 2 ** 31 - 1, 'seq', errors),
    issued: m.issued,
    expires: m.expires,
    servers: []
  };
  if (!isIsoDate(m.issued)) errors.push('issued must be an ISO-8601 UTC time');
  if (!isIsoDate(m.expires)) errors.push('expires must be an ISO-8601 UTC time');
  else if (isIsoDate(m.issued) && Date.parse(m.expires) <= Date.parse(m.issued)) errors.push('expires must be after issued');
  if (!Array.isArray(m.servers) || m.servers.length < 1 || m.servers.length > MAX_SERVERS) {
    errors.push(`servers must list 1 to ${MAX_SERVERS} servers`);
  } else {
    const ids = new Set();
    const endpoints = new Set();
    m.servers.forEach((s, i) => {
      const n = `server ${i + 1}`;
      if (!s || typeof s !== 'object' || Array.isArray(s)) {
        errors.push(`${n} is not an object`);
        return;
      }
      const out = {
        id: typeof s.id === 'string' && ID_RE.test(s.id) ? s.id : (errors.push(`${n}: id must be 1-24 lower-case letters, digits or '-'`), ''),
        name: cleanText(s.name, 64, `${n} name`, errors),
        region: cleanText(s.region, 32, `${n} region`, errors, { required: false }),
        address: typeof s.address === 'string' && isValidHost(s.address) ? s.address : (errors.push(`${n}: address must be a host name or IPv4 address`), ''),
        port: cleanInt(s.port, 1, 65535, `${n} port`, errors),
        queryPort: cleanInt(s.queryPort, 1, 65535, `${n} query port`, errors),
        maxPlayers: cleanInt(s.maxPlayers, 1, 1000, `${n} max players`, errors)
      };
      if (s.chronicleUrl != null && s.chronicleUrl !== '') {
        if (typeof s.chronicleUrl === 'string' && s.chronicleUrl.length <= 200 && isWebUrl(s.chronicleUrl)) out.chronicleUrl = s.chronicleUrl.replace(/\/+$/, '');
        else errors.push(`${n}: chronicleUrl must be an http(s) address without a user name or password`);
      }
      if (out.id) {
        if (ids.has(out.id)) errors.push(`${n}: duplicate id ${out.id}`);
        ids.add(out.id);
      }
      const ep = `${String(out.address).toLowerCase()}:${out.port}`;
      if (out.address && out.port) {
        if (endpoints.has(ep)) errors.push(`${n}: ${ep} is listed twice`);
        endpoints.add(ep);
      }
      value.servers.push(out);
    });
  }
  if (m.links != null) {
    const links = {};
    if (typeof m.links !== 'object' || Array.isArray(m.links)) errors.push('links must be an object');
    else {
      for (const k of ['discord', 'rules', 'website']) {
        if (m.links[k] == null || m.links[k] === '') continue;
        if (typeof m.links[k] === 'string' && m.links[k].length <= 300 && isWebUrl(m.links[k], { httpsOnly: true })) links[k] = m.links[k];
        else errors.push(`links.${k} must be an https:// address`);
      }
    }
    if (Object.keys(links).length) value.links = links;
  }
  return { ok: errors.length === 0, value, errors };
}

// ---------- envelope ----------

// Verifies an envelope (object or JSON text) against the embedded public key.
// opts: { now = Date.now(), minSeq = 0, ignoreExpiry = false }
// Returns { ok: true, manifest, keyId } or { ok: false, reason }.
function verifyEnvelope(envelope, publicKeyB64, opts = {}) {
  const now = opts.now != null ? opts.now : Date.now();
  let env = envelope;
  try {
    if (typeof env === 'string' || Buffer.isBuffer(env)) {
      if (env.length > MAX_ENVELOPE_BYTES) return { ok: false, reason: 'servers.json is too large' };
      env = JSON.parse(String(env).replace(/^\uFEFF/, ''));
    }
  } catch {
    return { ok: false, reason: 'servers.json is not valid JSON' };
  }
  if (!env || typeof env !== 'object' || Array.isArray(env)) return { ok: false, reason: 'servers.json is not an object' };
  if (env.format !== FORMAT) return { ok: false, reason: `unknown format (expected ${FORMAT}); unsigned lists are refused` };
  if (env.alg !== ALG) return { ok: false, reason: 'unsupported signature algorithm' };
  if (typeof env.payload !== 'string' || typeof env.signature !== 'string') return { ok: false, reason: 'servers.json is not signed' };
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(env.payload) || !/^[A-Za-z0-9+/]+={0,2}$/.test(env.signature)) return { ok: false, reason: 'payload or signature is not base64' };
  let key;
  try {
    key = publicKeyFromBase64(publicKeyB64);
  } catch (e) {
    return { ok: false, reason: `no valid public key is embedded in this build (${e.message})` };
  }
  const payload = Buffer.from(env.payload, 'base64');
  const sig = Buffer.from(env.signature, 'base64');
  if (payload.length > MAX_ENVELOPE_BYTES || sig.length !== 64) return { ok: false, reason: 'malformed signature' };
  let good = false;
  try {
    good = crypto.verify(null, payload, key, sig);
  } catch {
    good = false;
  }
  if (!good) return { ok: false, reason: 'signature does not match the embedded public key' };
  let parsed;
  try {
    parsed = JSON.parse(payload.toString('utf8'));
  } catch {
    return { ok: false, reason: 'signed payload is not valid JSON' };
  }
  const v = validateManifest(parsed);
  if (!v.ok) return { ok: false, reason: `signed list is invalid: ${v.errors.slice(0, 3).join('; ')}` };
  if (!opts.ignoreExpiry && Date.parse(v.value.expires) <= now) return { ok: false, reason: `list expired on ${v.value.expires}`, expired: true, manifest: v.value };
  if (Date.parse(v.value.issued) > now + 24 * 3600 * 1000) return { ok: false, reason: 'list is dated in the future' };
  if (opts.minSeq && v.value.seq < opts.minSeq) return { ok: false, reason: `older list (seq ${v.value.seq}) than one already seen (seq ${opts.minSeq}); refusing a rollback`, rollback: true };
  return { ok: true, manifest: v.value, keyId: keyIdOf(publicKeyB64) };
}

module.exports = {
  FORMAT,
  ALG,
  ID_RE,
  MAX_SERVERS,
  publicKeyFromBase64,
  rawPublicKey,
  keyIdOf,
  isWebUrl,
  validateManifest,
  verifyEnvelope
};
