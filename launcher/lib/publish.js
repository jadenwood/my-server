'use strict';

// "Publish server list" (steward edition only): build servers.json, sign it with the owner's
// Ed25519 key and WRITE it to a folder. Nothing is uploaded; the owner hosts the file themselves
// (for example on GitHub Pages or as a gist) and points the player build at its https address.
//
// The private key is created on the owner's PC and stays there. main.js stores it encrypted with
// Electron safeStorage (Windows DPAPI, tied to the Windows user) when that is available.

const crypto = require('crypto');
const M = require('./shared/manifest');

function generateKeyPair() {
  const { publicKey, privateKey } = crypto.generateKeyPairSync('ed25519');
  return {
    publicKey: M.rawPublicKey(publicKey).toString('base64'),
    privateKeyPem: privateKey.export({ format: 'pem', type: 'pkcs8' })
  };
}

function privateKeyFromPem(pem) {
  const key = crypto.createPrivateKey({ key: pem, format: 'pem' });
  if (key.asymmetricKeyType !== 'ed25519') throw new RangeError('signing key is not Ed25519');
  return key;
}

function publicKeyOfPrivate(pem) {
  return M.rawPublicKey(crypto.createPublicKey(privateKeyFromPem(pem))).toString('base64');
}

// input: { realm, servers: [...], links, validDays, seq } -> validated manifest
function buildManifest(input, { now = new Date() } = {}) {
  const days = Number.isInteger(input.validDays) && input.validDays >= 1 && input.validDays <= 365 ? input.validDays : 30;
  const issued = new Date(Math.floor(now.getTime() / 1000) * 1000);
  const expires = new Date(issued.getTime() + days * 86400 * 1000);
  const manifest = {
    schema: 1,
    realm: input.realm,
    seq: input.seq,
    issued: issued.toISOString().replace('.000Z', 'Z'),
    expires: expires.toISOString().replace('.000Z', 'Z'),
    servers: input.servers
  };
  if (input.links && Object.keys(input.links).length) manifest.links = input.links;
  const v = M.validateManifest(manifest);
  if (!v.ok) {
    const e = new Error(v.errors.join(' '));
    e.friendly = true;
    throw e;
  }
  return v.value;
}

function signManifest(manifest, privateKeyPem) {
  const v = M.validateManifest(manifest);
  if (!v.ok) throw new Error(`refusing to sign an invalid list: ${v.errors.join('; ')}`);
  const payload = Buffer.from(JSON.stringify(v.value, null, 2), 'utf8');
  const key = privateKeyFromPem(privateKeyPem);
  const signature = crypto.sign(null, payload, key);
  const publicKey = publicKeyOfPrivate(privateKeyPem);
  return {
    format: M.FORMAT,
    alg: M.ALG,
    keyId: M.keyIdOf(publicKey),
    payload: payload.toString('base64'),
    signature: signature.toString('base64')
  };
}

// The file that goes into launcher/player/player-config.json before building the player edition.
function playerConfig({ realmName, tagline, manifestUrl, publicKey, envelope, links = {} }) {
  if (manifestUrl && !M.isWebUrl(manifestUrl, { httpsOnly: true })) throw Object.assign(new Error('The list address must be an https:// address.'), { friendly: true });
  M.publicKeyFromBase64(publicKey);
  return {
    realmName: String(realmName || 'The Realm').slice(0, 60),
    tagline: String(tagline || '').slice(0, 160),
    manifestUrl: manifestUrl || '',
    publicKey,
    bundledManifest: envelope || null,
    links: {
      discord: links.discord && M.isWebUrl(links.discord, { httpsOnly: true }) ? links.discord : '',
      rules: links.rules && M.isWebUrl(links.rules, { httpsOnly: true }) ? links.rules : ''
    },
    joinMethod: 'quick',
    steamAppId: 344760
  };
}

module.exports = { generateKeyPair, privateKeyFromPem, publicKeyOfPrivate, buildManifest, signManifest, playerConfig };
