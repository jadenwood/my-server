// servers.json: schema, Ed25519 signing (steward) and verification (player). Run: npm test
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('crypto');
const M = require('../lib/shared/manifest');
const PB = require('../lib/publish');

const NOW = Date.parse('2026-10-02T12:00:00Z');

function sample(over = {}) {
  return {
    schema: 1,
    realm: 'The Realm',
    seq: 3,
    issued: '2026-10-01T00:00:00Z',
    expires: '2026-11-01T00:00:00Z',
    servers: [
      { id: 's1', name: 'Realm I', region: 'EU', address: 'play.example.org', port: 7350, queryPort: 27015, maxPlayers: 120 },
      { id: 's2', name: 'Realm II', region: 'EU', address: '203.0.113.7', port: 7360, queryPort: 27025, maxPlayers: 120, chronicleUrl: 'https://chronicle.example.org/' }
    ],
    links: { discord: 'https://discord.gg/abc' },
    ...over
  };
}

const kp = PB.generateKeyPair();
const other = PB.generateKeyPair();

test('generated key pair: raw 32-byte public key, PEM private key, both forms accepted', () => {
  assert.equal(Buffer.from(kp.publicKey, 'base64').length, 32);
  assert.match(kp.privateKeyPem, /BEGIN PRIVATE KEY/);
  assert.equal(PB.publicKeyOfPrivate(kp.privateKeyPem), kp.publicKey);
  const spki = M.publicKeyFromBase64(kp.publicKey).export({ format: 'der', type: 'spki' }).toString('base64');
  assert.equal(M.keyIdOf(spki), M.keyIdOf(kp.publicKey));
  assert.match(M.keyIdOf(kp.publicKey), /^[0-9a-f]{16}$/);
  assert.throws(() => M.publicKeyFromBase64('not base64!'), /base64/);
  assert.throws(() => M.publicKeyFromBase64(Buffer.alloc(31).toString('base64')), /Ed25519/);
  const rsa = crypto.generateKeyPairSync('rsa', { modulusLength: 1024 }).publicKey.export({ format: 'der', type: 'spki' }).toString('base64');
  assert.throws(() => M.publicKeyFromBase64(rsa), /Ed25519/);
});

test('sign then verify round-trips, and drops unknown fields', () => {
  const env = PB.signManifest({ ...sample(), extra: 'dropped' }, kp.privateKeyPem);
  assert.equal(env.format, 'realm-servers/1');
  assert.equal(env.alg, 'Ed25519');
  assert.equal(Buffer.from(env.signature, 'base64').length, 64);
  const r = M.verifyEnvelope(JSON.stringify(env), kp.publicKey, { now: NOW });
  assert.equal(r.ok, true, r.reason);
  assert.equal(r.manifest.servers.length, 2);
  assert.equal(r.manifest.servers[1].chronicleUrl, 'https://chronicle.example.org');
  assert.equal(r.manifest.extra, undefined);
  assert.equal(r.keyId, M.keyIdOf(kp.publicKey));
});

test('a single flipped byte in the payload or signature is refused', () => {
  const env = PB.signManifest(sample(), kp.privateKeyPem);
  const payload = Buffer.from(env.payload, 'base64');
  payload[payload.indexOf('7350')] = '8'.charCodeAt(0);
  const tampered = { ...env, payload: payload.toString('base64') };
  assert.match(M.verifyEnvelope(tampered, kp.publicKey, { now: NOW }).reason, /signature does not match/);
  const sig = Buffer.from(env.signature, 'base64');
  sig[10] ^= 1;
  assert.match(M.verifyEnvelope({ ...env, signature: sig.toString('base64') }, kp.publicKey, { now: NOW }).reason, /signature does not match/);
});

test('a list signed by another key is refused', () => {
  const env = PB.signManifest(sample(), other.privateKeyPem);
  const r = M.verifyEnvelope(env, kp.publicKey, { now: NOW });
  assert.equal(r.ok, false);
  assert.match(r.reason, /signature does not match/);
});

test('unsigned, malformed and oversized lists are refused', () => {
  assert.match(M.verifyEnvelope(JSON.stringify(sample()), kp.publicKey, { now: NOW }).reason, /unsigned lists are refused/);
  assert.match(M.verifyEnvelope('{not json', kp.publicKey).reason, /not valid JSON/);
  assert.match(M.verifyEnvelope('[]', kp.publicKey).reason, /not an object/);
  const env = PB.signManifest(sample(), kp.privateKeyPem);
  assert.match(M.verifyEnvelope({ ...env, alg: 'none' }, kp.publicKey).reason, /algorithm/);
  assert.match(M.verifyEnvelope({ ...env, signature: undefined }, kp.publicKey).reason, /not signed/);
  assert.match(M.verifyEnvelope({ ...env, signature: 'AAAA' }, kp.publicKey).reason, /malformed/);
  assert.match(M.verifyEnvelope('x'.repeat(70000), kp.publicKey).reason, /too large/);
  assert.match(M.verifyEnvelope(env, '').reason, /no valid public key/);
});

test('expiry, rollback and future dates', () => {
  const env = PB.signManifest(sample(), kp.privateKeyPem);
  const late = Date.parse('2026-12-01T00:00:00Z');
  const exp = M.verifyEnvelope(env, kp.publicKey, { now: late });
  assert.equal(exp.ok, false);
  assert.equal(exp.expired, true);
  assert.equal(M.verifyEnvelope(env, kp.publicKey, { now: late, ignoreExpiry: true }).ok, true);
  const rb = M.verifyEnvelope(env, kp.publicKey, { now: NOW, minSeq: 4 });
  assert.equal(rb.ok, false);
  assert.equal(rb.rollback, true);
  assert.equal(M.verifyEnvelope(env, kp.publicKey, { now: NOW, minSeq: 3 }).ok, true);
  const future = PB.signManifest(sample({ issued: '2027-01-01T00:00:00Z', expires: '2027-02-01T00:00:00Z' }), kp.privateKeyPem);
  assert.match(M.verifyEnvelope(future, kp.publicKey, { now: NOW }).reason, /future/);
});

test('schema: every field is checked', () => {
  assert.equal(M.validateManifest(sample()).ok, true);
  const bad = (over, re) => {
    const r = M.validateManifest(sample(over));
    assert.equal(r.ok, false, JSON.stringify(over));
    assert.match(r.errors.join(' | '), re);
  };
  bad({ schema: 2 }, /schema/);
  bad({ seq: 0 }, /seq/);
  bad({ seq: 1.5 }, /seq/);
  bad({ issued: 'yesterday' }, /issued/);
  bad({ expires: '2026-09-01T00:00:00Z' }, /after issued/);
  bad({ servers: [] }, /1 to 16/);
  bad({ servers: new Array(17).fill(sample().servers[0]) }, /1 to 16/);
  const s = sample().servers[0];
  bad({ servers: [{ ...s, id: 'S1' }] }, /id must be/);
  bad({ servers: [{ ...s, id: '../x' }] }, /id must be/);
  bad({ servers: [s, { ...s, port: 7351 }] }, /duplicate id/);
  bad({ servers: [s, { ...s, id: 's9' }] }, /listed twice/);
  bad({ servers: [{ ...s, address: '-pass secret' }] }, /address/);
  bad({ servers: [{ ...s, address: 'a b' }] }, /address/);
  bad({ servers: [{ ...s, address: '999.1.1.1' }] }, /address/);
  bad({ servers: [{ ...s, address: 'host:7350' }] }, /address/);
  bad({ servers: [{ ...s, port: 0 }] }, /port/);
  bad({ servers: [{ ...s, port: '7350' }] }, /port/);
  bad({ servers: [{ ...s, queryPort: 70000 }] }, /query port/);
  bad({ servers: [{ ...s, maxPlayers: 0 }] }, /max players/);
  bad({ servers: [{ ...s, name: '' }] }, /name is required/);
  bad({ servers: [{ ...s, name: 'x'.repeat(65) }] }, /longer than 64/);
  bad({ servers: [{ ...s, name: 'line\nbreak' }] }, /control characters/);
  bad({ servers: [{ ...s, chronicleUrl: 'https://u:p@chronicle.example.org' }] }, /chronicleUrl/);
  bad({ servers: [{ ...s, chronicleUrl: 'file:///etc/passwd' }] }, /chronicleUrl/);
  bad({ links: { discord: 'http://discord.gg/abc' } }, /links\.discord/);
});

test('buildManifest stamps dates and refuses bad input; playerConfig needs https and a real key', () => {
  const m = PB.buildManifest({ realm: 'The Realm', seq: 7, servers: sample().servers, validDays: 14 }, { now: new Date('2026-10-02T12:34:56.789Z') });
  assert.equal(m.issued, '2026-10-02T12:34:56Z');
  assert.equal(m.expires, '2026-10-16T12:34:56Z');
  assert.throws(() => PB.buildManifest({ realm: 'R', seq: 1, servers: [] }), /servers must list/);
  const env = PB.signManifest(m, kp.privateKeyPem);
  const pc = PB.playerConfig({ realmName: 'The Realm', manifestUrl: 'https://example.github.io/realm/servers.json', publicKey: kp.publicKey, envelope: env, links: { rules: 'https://example.org/rules', discord: 'javascript:alert(1)' } });
  assert.equal(pc.publicKey, kp.publicKey);
  assert.equal(pc.links.rules, 'https://example.org/rules');
  assert.equal(pc.links.discord, '');
  assert.equal(M.verifyEnvelope(pc.bundledManifest, pc.publicKey, { ignoreExpiry: true }).ok, true);
  assert.throws(() => PB.playerConfig({ manifestUrl: 'http://example.org/servers.json', publicKey: kp.publicKey }), /https/);
  assert.throws(() => PB.playerConfig({ manifestUrl: '', publicKey: 'nope' }), /base64|Ed25519/);
  assert.throws(() => PB.playerConfig({ manifestUrl: '', publicKey: 'not base64!' }), /base64/);
  assert.throws(() => PB.signManifest({ schema: 1 }, kp.privateKeyPem), /invalid list/);
});
