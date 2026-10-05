'use strict';

// Discord herald for Realm Steward: an opt-in relay that posts new Chronicle events to a Discord
// channel through a webhook the owner pastes in Settings.
//
// - The webhook URL is a secret. It is stored only in the owner's app data folder
//   (<userData>\realm-discord.json), encrypted with Electron safeStorage (Windows DPAPI) when that
//   is available, and never written to the repo, logs or the renderer (the UI only sees a redacted hint).
// - Only https://discord.com/api/webhooks/... and https://discordapp.com/api/webhooks/... are accepted.
// - Posts go through a rate-limited queue (one request per 2 s, at most 20 per minute, up to 10 embeds
//   per message), with retries on 429 / 5xx / network errors, and honouring Discord's retry_after.
// - Mentions are never pinged (allowed_mentions.parse = []) and player text is markdown-escaped.
// - Nothing is posted for history: on first enable the relay starts after the newest existing event.
//
// Discord limits used here are from Discord's public developer documentation (webhook execute,
// embed limits). Exact webhook rate limits are not published per route; the queue is deliberately
// conservative and also obeys the X-RateLimit-* / Retry-After headers Discord sends. UNVERIFIED
// against a live webhook from this environment (no network calls are made in tests).

const fsp = require('fs/promises');
const path = require('path');

// ---------------------------------------------------------------- URL validation

const WEBHOOK_HOSTS = new Set(['discord.com', 'discordapp.com']);
const WEBHOOK_PATH = /^\/api(?:\/v\d{1,2})?\/webhooks\/(\d{15,25})\/([A-Za-z0-9_-]{20,128})\/?$/;

function validateWebhookUrl(input) {
  if (typeof input !== 'string' || !input.trim()) return { ok: false, error: 'Paste the webhook URL from Discord (Edit Channel > Integrations > Webhooks > Copy Webhook URL).' };
  const raw = input.trim();
  if (raw.length > 300) return { ok: false, error: 'That is too long to be a Discord webhook URL.' };
  let u;
  try {
    u = new URL(raw);
  } catch {
    return { ok: false, error: 'That is not a web address. It should start with https://discord.com/api/webhooks/' };
  }
  if (u.protocol !== 'https:') return { ok: false, error: 'Only https:// webhook URLs are accepted.' };
  if (u.username || u.password) return { ok: false, error: 'A webhook URL never contains a user name or password.' };
  if (u.port) return { ok: false, error: 'A Discord webhook URL has no port number.' };
  if (!WEBHOOK_HOSTS.has(u.hostname.toLowerCase())) return { ok: false, error: 'Only discord.com or discordapp.com webhook URLs are accepted.' };
  if (u.search || u.hash) return { ok: false, error: 'Remove everything after the token (the part starting with ? or #).' };
  const m = u.pathname.match(WEBHOOK_PATH);
  if (!m) return { ok: false, error: 'That is a Discord address, but not a webhook URL. It should look like https://discord.com/api/webhooks/<id>/<token>' };
  return { ok: true, url: `https://${u.hostname.toLowerCase()}/api/webhooks/${m[1]}/${m[2]}`, id: m[1] };
}

// Safe to show in the UI and logs: host and webhook id only, never the token.
function redactWebhookUrl(url) {
  const v = validateWebhookUrl(url);
  if (!v.ok) return '';
  const host = new URL(v.url).hostname;
  return `https://${host}/api/webhooks/${v.id}/•••••• (token hidden)`;
}

// Strips any webhook token from free text (error messages may echo the URL).
function scrubSecrets(text) {
  return String(text == null ? '' : text).replace(/(\/api(?:\/v\d+)?\/webhooks\/\d+\/)[A-Za-z0-9_-]+/g, '$1••••••');
}

// ---------------------------------------------------------------- event types and colours

// Same types and labels as chronicle/public/assets/common.js and plugins/RealmChronicle.cs (KnownTypes),
// plus the types other plugins list in plugins/docs/*/EVENTS.json (RealmLaws, RealmDynasties, RealmTreasury,
// RealmRavens). Those are only posted once RealmChronicle accepts them; until then their plugins log "decree".
const EVENT_TYPES = [
  { type: 'coronation', label: 'Coronation', group: 'Crown', tone: 'gold' },
  { type: 'abdication', label: 'The Crown Falls', group: 'Crown', tone: 'blood' },
  { type: 'decree', label: 'Royal Decree', group: 'Crown', tone: 'gold' },
  { type: 'claim_declared', label: 'A Claim', group: 'War', tone: 'blood' },
  { type: 'rebellion_started', label: 'Rebellion', group: 'War', tone: 'blood' },
  { type: 'rebellion_ended', label: 'Rebellion Ends', group: 'War', tone: 'iron' },
  { type: 'house_founded', label: 'A House Rises', group: 'Houses', tone: 'gold' },
  { type: 'oath_sworn', label: 'Oath Sworn', group: 'Houses', tone: 'gold' },
  { type: 'oath_broken', label: 'Oathbreaker', group: 'Houses', tone: 'blood' },
  { type: 'treaty_signed', label: 'Treaty', group: 'Houses', tone: 'moss' },
  { type: 'treaty_broken', label: 'Treaty Broken', group: 'Houses', tone: 'blood' },
  { type: 'ransom_set', label: 'Ransom', group: 'Ransom', tone: 'blood' },
  { type: 'ransom_paid', label: 'Ransom Paid', group: 'Ransom', tone: 'gold' },
  { type: 'released', label: 'Set Free', group: 'Ransom', tone: 'moss' },
  { type: 'contract_posted', label: 'A Price Is Set', group: 'Contracts', tone: 'iron' },
  { type: 'contract_fulfilled', label: 'Contract Paid', group: 'Contracts', tone: 'gold' },
  { type: 'contract_ended', label: 'Contract Lapses', group: 'Contracts', tone: 'iron' },
  { type: 'season_started', label: 'A New Season', group: 'Seasons & events', tone: 'gold' },
  { type: 'season_ended', label: "Season's End", group: 'Seasons & events', tone: 'gold' },
  { type: 'event_started', label: 'Realm Event', group: 'Seasons & events', tone: 'iron' },
  { type: 'event_ended', label: 'Event Ends', group: 'Seasons & events', tone: 'iron' },
  { type: 'tournament_champion', label: 'Tournament Champion', group: 'Seasons & events', tone: 'gold' },
  { type: 'hunt_kill', label: "The King's Hunt", group: 'Seasons & events', tone: 'blood' },
  { type: 'truce_broken', label: 'Truce Broken', group: 'Seasons & events', tone: 'blood' },
  { type: 'law_proclaimed', label: 'A Law Proclaimed', group: 'Law & dynasty', tone: 'gold' },
  { type: 'law_repealed', label: 'Law Repealed', group: 'Law & dynasty', tone: 'iron' },
  { type: 'accusation', label: 'Accused', group: 'Law & dynasty', tone: 'blood' },
  { type: 'trial_by_combat', label: 'Trial by Combat', group: 'Law & dynasty', tone: 'blood' },
  { type: 'verdict', label: 'Verdict', group: 'Law & dynasty', tone: 'iron' },
  { type: 'pardon', label: 'Pardoned', group: 'Law & dynasty', tone: 'moss' },
  { type: 'dynasty_founded', label: 'A Line Is Founded', group: 'Law & dynasty', tone: 'gold' },
  { type: 'heir_named', label: 'An Heir Is Named', group: 'Law & dynasty', tone: 'gold' },
  { type: 'succession', label: 'Succession', group: 'Law & dynasty', tone: 'gold' },
  { type: 'blood_claim', label: 'Blood Claim', group: 'Law & dynasty', tone: 'blood' },
  { type: 'blood_restored', label: 'The Line Restored', group: 'Law & dynasty', tone: 'gold' },
  { type: 'title_bestowed', label: 'Title Bestowed', group: 'Law & dynasty', tone: 'gold' },
  { type: 'title_earned', label: 'A Title Earned', group: 'Law & dynasty', tone: 'gold' },
  { type: 'treasury_mint', label: 'The Crown Strikes Coin', group: 'Treasury & rumours', tone: 'gold' },
  { type: 'treasury_grant', label: 'Royal Largesse', group: 'Treasury & rumours', tone: 'gold' },
  { type: 'tithe_levied', label: 'The Tithe Gathered', group: 'Treasury & rumours', tone: 'iron' },
  { type: 'great_trade', label: 'A Great Sale', group: 'Treasury & rumours', tone: 'gold' },
  { type: 'rumour', label: 'A Rumour Spreads', group: 'Treasury & rumours', tone: 'iron' },
  { type: 'holding_taken', label: 'A Holding Taken', group: 'Claims & war', tone: 'blood' },
  { type: 'census_taken', label: 'The Census', group: 'Seasons & events', tone: 'iron' },
  { type: 'vote_held', label: 'The Realm Votes', group: 'Crown', tone: 'gold' },
  { type: 'blade_claimed', label: 'The Ironbreaker Taken Up', group: 'Claims & war', tone: 'gold' },
  { type: 'blade_lost', label: 'The Ironbreaker Returns', group: 'Claims & war', tone: 'iron' }
];
const TYPE_BY_ID = new Map(EVENT_TYPES.map((t) => [t.type, t]));
const DEFAULT_TYPES = ['coronation', 'abdication', 'decree', 'claim_declared', 'rebellion_started', 'rebellion_ended', 'house_founded', 'oath_broken', 'treaty_signed', 'treaty_broken', 'ransom_paid', 'season_started', 'season_ended', 'event_started', 'tournament_champion'];
const TYPE_RE = /^[a-z][a-z0-9_]{1,31}$/;

// Dyed-cloth palette and FNV-1a hash, identical to chronicle/public/assets/common.js (dyeFor) and
// portal/lib/util.mjs, so a house has the same colour on stream, on the website and in Discord.
const DYES = ['#7a1f1c', '#1f2f5c', '#24472d', '#86601a', '#4a2347', '#2c3b42', '#7a3a1a', '#1c4a4a', '#3a2a1a', '#5a5a22'];
function fnv(s) {
  let h = 2166136261;
  for (const ch of String(s || '').toLowerCase()) h = Math.imul(h ^ ch.codePointAt(0), 16777619);
  return h >>> 0;
}
function houseColour(name) {
  return parseInt(DYES[fnv(name) % DYES.length].slice(1), 16);
}
const TONE_COLOURS = { gold: 0xd6a043, blood: 0x8b2b22, moss: 0x4d6b3a, iron: 0x5d6068 };

// ---------------------------------------------------------------- embeds

// Discord embed limits (developer docs, "Embed Limits").
const LIMITS = { title: 256, description: 4096, fields: 25, fieldName: 256, fieldValue: 1024, footer: 2048, author: 256, total: 6000, embedsPerMessage: 10, username: 80 };

const cut = (s, n) => {
  const t = String(s == null ? '' : s);
  return t.length > n ? t.slice(0, n - 1) + '…' : t;
};

// Player-written text (house names, titles from chat commands) must not format, link or ping.
function escapeText(s) {
  return String(s == null ? '' : s)
    .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, '')
    .replace(/([\\*_~`|[\]])/g, '\\$1')
    .replace(/^(\s*)([#>-])/gm, '$1\\$2')
    .replace(/@(everyone|here)/gi, '@\u200b$1')
    .replace(/<(@[!&]?|#)(\d+)>/g, '<$1\u200b$2>')
    .replace(/https?:\/\//gi, (m) => m.replace('//', '/\u200b/'));
}

function houseNameOf(text, houseNames) {
  const sorted = (houseNames || []).slice().sort((a, b) => b.length - a.length);
  for (const n of sorted) {
    const bare = n.replace(/^house\s+/i, '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    if (new RegExp(`\\bHouse\\s+${bare}(?![\\p{L}\\p{N}])`, 'iu').test(text)) return n;
  }
  return null;
}

// Which house an event belongs to, for its colour: "House X" in the title, the crown's house on a
// coronation ("<king> of X takes the throne"), or the house of the first actor on a roster.
function houseForEvent(ev, ctx = {}) {
  const names = ctx.houseNames || [];
  const fromTitle = houseNameOf(ev.title || '', names);
  if (fromTitle) return fromTitle;
  const crown = String(ev.title || '').match(/ of (.+?) takes the throne/);
  if (crown && !/^no house$/i.test(crown[1])) return crown[1];
  const fromDetail = houseNameOf(ev.detail || '', names);
  if (fromDetail) return fromDetail;
  const roster = ctx.rosterIndex;
  if (roster) for (const a of ev.actors || []) if (roster.has(String(a).toLowerCase())) return roster.get(String(a).toLowerCase());
  return null;
}

function safeHttps(u) {
  try {
    const url = new URL(u);
    return url.protocol === 'https:' && !url.username && !url.password ? url.href.replace(/\/$/, '') : null;
  } catch {
    return null;
  }
}

function buildEmbed(ev, ctx = {}) {
  const meta = TYPE_BY_ID.get(ev.type) || { label: String(ev.type || 'Chronicle').replace(/_/g, ' '), tone: 'iron' };
  const house = houseForEvent(ev, ctx);
  const embed = {
    author: { name: cut(meta.label, LIMITS.author) },
    title: cut(escapeText(ev.title || meta.label), LIMITS.title),
    color: house ? houseColour(house) : TONE_COLOURS[meta.tone] || TONE_COLOURS.iron,
    footer: { text: cut(`${ctx.realmName || 'The Realm'} · Chronicle #${Number.isInteger(ev.id) ? ev.id : '?'}`, LIMITS.footer) }
  };
  if (ev.detail) embed.description = cut(escapeText(ev.detail), LIMITS.description);
  const ts = Date.parse(ev.ts || '');
  if (!Number.isNaN(ts)) embed.timestamp = new Date(ts).toISOString();
  const base = ctx.portalUrl ? safeHttps(ctx.portalUrl) : null;
  if (base && Number.isInteger(ev.id)) embed.url = `${base}/chronicle.html#e${ev.id}`;
  const fields = [];
  if (house) fields.push({ name: 'House', value: cut(escapeText(/^house\s/i.test(house) ? house : `House ${house}`), LIMITS.fieldValue), inline: true });
  const actors = (Array.isArray(ev.actors) ? ev.actors : []).filter((a) => typeof a === 'string' && a.trim() && !/^\d{15,20}$/.test(a)).slice(0, 8);
  if (actors.length) fields.push({ name: actors.length === 1 ? 'Named' : 'Named in the record', value: cut(actors.map(escapeText).join(', '), LIMITS.fieldValue), inline: true });
  if (fields.length) embed.fields = fields.slice(0, LIMITS.fields);
  // Keep the whole embed under Discord's 6000-character total by shortening the description.
  const over = embedSize(embed) - (LIMITS.total - 200);
  if (over > 0 && embed.description) embed.description = cut(embed.description, Math.max(1, embed.description.length - over));
  return embed;
}

function embedSize(e) {
  let n = (e.title || '').length + (e.description || '').length + ((e.footer && e.footer.text) || '').length + ((e.author && e.author.name) || '').length;
  for (const f of e.fields || []) n += f.name.length + f.value.length;
  return n;
}

// Discord rejects webhook usernames containing "discord" or "clyde".
function webhookUsername(realmName) {
  const n = cut(String(realmName || 'The Realm').replace(/discord|clyde/gi, '').replace(/\s+/g, ' ').trim() || 'The Realm', 60);
  return cut(`${n} Chronicle`, LIMITS.username);
}

function buildPayload(embeds, ctx = {}) {
  return { username: webhookUsername(ctx.realmName), embeds: embeds.slice(0, LIMITS.embedsPerMessage), allowed_mentions: { parse: [] } };
}

// ---------------------------------------------------------------- rate-limited queue

const realClock = {
  now: () => Date.now(),
  sleep: (ms) => new Promise((r) => {
    const t = setTimeout(r, Math.max(0, ms));
    if (t.unref) t.unref();
  })
};

// send(payload) -> { status, retryAfterMs?, remaining?, resetAfterMs?, error? }   (never throws)
class RelayQueue {
  constructor({ send, clock = realClock, minIntervalMs = 2000, perMinute = 20, maxQueue = 200, maxAttempts = 5, baseBackoffMs = 2000, maxWaitMs = 120000, ctx = () => ({}), log = () => {}, onFatal = () => {} }) {
    this.send = send;
    this.clock = clock;
    this.minIntervalMs = minIntervalMs;
    this.perMinute = perMinute;
    this.maxQueue = maxQueue;
    this.maxAttempts = maxAttempts;
    this.baseBackoffMs = baseBackoffMs;
    this.maxWaitMs = maxWaitMs;
    this.ctx = ctx;
    this.log = log;
    this.onFatal = onFatal;
    this.items = [];
    this.sentTimes = [];
    this.pausedUntil = 0;
    this.running = null;
    this.stats = { sent: 0, failed: 0, dropped: 0, lastError: null, lastSentAt: null };
  }

  get length() {
    return this.items.length;
  }

  enqueue(embeds) {
    for (const e of embeds) {
      this.items.push(e);
      if (this.items.length > this.maxQueue) {
        this.items.shift();
        this.stats.dropped++;
      }
    }
    return this.pump();
  }

  clear() {
    this.stats.dropped += this.items.length;
    this.items = [];
  }

  // How long until another request is allowed (0 = now).
  waitMs() {
    const now = this.clock.now();
    this.sentTimes = this.sentTimes.filter((t) => now - t < 60000);
    let wait = Math.max(0, this.pausedUntil - now);
    const last = this.sentTimes[this.sentTimes.length - 1];
    if (last !== undefined) wait = Math.max(wait, last + this.minIntervalMs - now);
    if (this.sentTimes.length >= this.perMinute) wait = Math.max(wait, this.sentTimes[0] + 60000 - now);
    return wait;
  }

  async acquire() {
    for (;;) {
      const w = this.waitMs();
      if (w <= 0) break;
      await this.clock.sleep(w);
    }
    this.sentTimes.push(this.clock.now());
  }

  // Sends one payload with rate limiting and retries. Returns { ok, status, attempts, error, fatal }.
  async deliver(payload) {
    let attempts = 0;
    for (;;) {
      await this.acquire();
      attempts++;
      const r = (await this.send(payload).catch((e) => ({ status: 0, error: e && e.message ? e.message : String(e) }))) || { status: 0, error: 'no response' };
      if (Number.isFinite(r.remaining) && r.remaining <= 0 && r.resetAfterMs > 0) {
        this.pausedUntil = Math.max(this.pausedUntil, this.clock.now() + Math.min(r.resetAfterMs, this.maxWaitMs));
      }
      if (r.status >= 200 && r.status < 300) return { ok: true, status: r.status, attempts };
      if (r.status === 429) {
        const wait = Math.min(Math.max(r.retryAfterMs || 1000, 250), this.maxWaitMs);
        this.pausedUntil = Math.max(this.pausedUntil, this.clock.now() + wait);
        if (attempts < this.maxAttempts * 2) continue; // rate limits are expected; give them more room
        return { ok: false, status: 429, attempts, error: 'Discord kept rate-limiting the webhook.' };
      }
      if (r.status === 401 || r.status === 403 || r.status === 404) {
        return { ok: false, status: r.status, attempts, fatal: true, error: r.status === 404 ? 'Discord says this webhook does not exist any more (deleted, or the URL is wrong).' : 'Discord refused the webhook token (it was reset or the URL is wrong).' };
      }
      if (r.status >= 400 && r.status < 500) {
        return { ok: false, status: r.status, attempts, error: `Discord rejected the message (HTTP ${r.status})${r.error ? `: ${r.error}` : ''}.` };
      }
      // 5xx or network: back off and retry.
      if (attempts >= this.maxAttempts) {
        return { ok: false, status: r.status, attempts, error: r.status ? `Discord had a server error (HTTP ${r.status}) ${attempts} times in a row.` : `Could not reach Discord: ${r.error || 'network error'}.` };
      }
      await this.clock.sleep(Math.min(this.baseBackoffMs * 2 ** (attempts - 1), this.maxWaitMs));
    }
  }

  // Takes up to 10 embeds that fit in one message (6000 characters in total).
  takeBatch() {
    const batch = [];
    let size = 0;
    while (this.items.length && batch.length < LIMITS.embedsPerMessage) {
      const s = embedSize(this.items[0]);
      if (batch.length && size + s > LIMITS.total - 200) break;
      batch.push(this.items.shift());
      size += s;
    }
    return batch;
  }

  pump() {
    if (this.running) return this.running;
    this.running = (async () => {
      try {
        while (this.items.length) {
          const batch = this.takeBatch();
          const res = await this.deliver(buildPayload(batch, this.ctx()));
          if (res.ok) {
            this.stats.sent += batch.length;
            this.stats.lastSentAt = new Date(this.clock.now()).toISOString();
            this.stats.lastError = null;
          } else {
            this.stats.failed += batch.length;
            this.stats.lastError = scrubSecrets(res.error);
            this.log('warn', `Discord herald: ${batch.length} message(s) not delivered: ${this.stats.lastError}`);
            if (res.fatal) {
              this.clear();
              this.onFatal(res);
              break;
            }
          }
        }
      } finally {
        this.running = null;
      }
    })();
    return this.running;
  }

  drain() {
    return this.running || Promise.resolve();
  }
}

// ---------------------------------------------------------------- HTTP (real network; not used in tests)

function makeSender(getUrl, fetchImpl = globalThis.fetch, timeoutMs = 10000) {
  return async (payload) => {
    const url = getUrl();
    if (!url) return { status: 0, error: 'no webhook URL saved' };
    try {
      const res = await fetchImpl(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'User-Agent': 'RealmSteward (community server tool)' },
        body: JSON.stringify(payload),
        redirect: 'error',
        signal: AbortSignal.timeout(timeoutMs)
      });
      const out = { status: res.status };
      const h = (k) => (res.headers && typeof res.headers.get === 'function' ? res.headers.get(k) : null);
      const remaining = h('x-ratelimit-remaining');
      const resetAfter = h('x-ratelimit-reset-after');
      if (remaining !== null && remaining !== undefined && remaining !== '') out.remaining = Number(remaining);
      if (resetAfter) out.resetAfterMs = Math.ceil(Number(resetAfter) * 1000);
      if (res.status === 429) {
        let body = null;
        try {
          body = await res.json();
        } catch {
          /* not json */
        }
        const sec = body && Number.isFinite(body.retry_after) ? body.retry_after : Number(h('retry-after'));
        out.retryAfterMs = Number.isFinite(sec) ? Math.ceil(sec * 1000) : 1000;
      } else if (res.status >= 400) {
        try {
          const body = await res.json();
          if (body && typeof body.message === 'string') out.error = body.message.slice(0, 200);
        } catch {
          /* ignore */
        }
      }
      return out;
    } catch (e) {
      return { status: 0, error: e && e.name === 'TimeoutError' ? 'timed out' : scrubSecrets(e && e.message ? e.message : String(e)) };
    }
  };
}

// ---------------------------------------------------------------- settings store

const STORE_FILE = 'realm-discord.json';
const STORE_DEFAULTS = Object.freeze({ version: 1, enabled: false, webhook: null, types: DEFAULT_TYPES, realmName: '', portalUrl: '', lastId: null });

// crypt: { available(): bool, encrypt(text): base64, decrypt(base64): text }  (Electron safeStorage in the app)
class DiscordStore {
  constructor(dir, crypt = null) {
    this.file = path.join(dir, STORE_FILE);
    this.crypt = crypt;
    this.data = { ...STORE_DEFAULTS, types: DEFAULT_TYPES.slice() };
  }

  async load() {
    try {
      const raw = JSON.parse((await fsp.readFile(this.file, 'utf8')).replace(/^﻿/, ''));
      if (raw && typeof raw === 'object') {
        this.data.enabled = raw.enabled === true;
        this.data.webhook = raw.webhook && typeof raw.webhook === 'object' ? raw.webhook : null;
        this.data.types = sanitizeTypes(raw.types);
        this.data.realmName = typeof raw.realmName === 'string' ? raw.realmName.slice(0, 60) : '';
        this.data.portalUrl = typeof raw.portalUrl === 'string' && safeHttps(raw.portalUrl) ? raw.portalUrl : '';
        this.data.lastId = Number.isInteger(raw.lastId) ? raw.lastId : null;
      }
    } catch {
      /* first run or unreadable: defaults */
    }
    if (!this.url()) this.data.enabled = false;
    return this.data;
  }

  async save() {
    const { writeFileAtomic } = require('./fsops');
    await writeFileAtomic(this.file, JSON.stringify(this.data, null, 2));
  }

  url() {
    const w = this.data.webhook;
    if (!w) return null;
    let s = null;
    try {
      if (typeof w.enc === 'string' && this.crypt && this.crypt.available()) s = this.crypt.decrypt(w.enc);
      else if (typeof w.plain === 'string') s = w.plain;
    } catch {
      s = null; // encrypted on another Windows account or machine
    }
    const v = validateWebhookUrl(s || '');
    return v.ok ? v.url : null;
  }

  setUrl(url) {
    if (url === null) {
      this.data.webhook = null;
      this.data.enabled = false;
      return;
    }
    const v = validateWebhookUrl(url);
    if (!v.ok) throw Object.assign(new Error(v.error), { friendly: true });
    this.data.webhook = this.crypt && this.crypt.available() ? { enc: this.crypt.encrypt(v.url) } : { plain: v.url };
  }

  encrypted() {
    return !!(this.data.webhook && this.data.webhook.enc);
  }
}

function sanitizeTypes(list) {
  if (!Array.isArray(list)) return DEFAULT_TYPES.slice();
  return [...new Set(list.filter((t) => typeof t === 'string' && TYPE_RE.test(t)))].slice(0, 64);
}

// ---------------------------------------------------------------- chronicle watcher

// Reads RealmChronicle.json (+ RealmState / RealmHouses for colours) from the plugin data folder and
// queues events newer than the last one relayed. Never writes to the data folder.
class ChronicleRelay {
  constructor({ store, queue, dataDir, readFile = (p) => fsp.readFile(p, 'utf8'), log = () => {} }) {
    this.store = store;
    this.queue = queue;
    this.dataDir = dataDir;
    this.readFile = readFile;
    this.log = log;
    this.timer = null;
    this.lastCheck = null;
    this.lastEventAt = null;
    this.houseCtx = { houseNames: [], rosterIndex: new Map() };
  }

  async readJson(name) {
    const dir = this.dataDir();
    if (!dir) return null;
    try {
      return JSON.parse(String(await this.readFile(path.join(dir, name))).replace(/^﻿/, ''));
    } catch {
      return null;
    }
  }

  async refreshHouses() {
    const names = new Set();
    const roster = new Map();
    const state = await this.readJson('RealmState.json');
    if (state && Array.isArray(state.houses)) for (const h of state.houses) if (h && typeof h.name === 'string') names.add(h.name);
    const houses = await this.readJson('RealmHouses.json');
    if (houses && Array.isArray(houses.Houses)) {
      for (const h of houses.Houses) {
        if (!h || typeof h.Name !== 'string') continue;
        names.add(h.Name);
        for (const m of Array.isArray(h.Members) ? h.Members : []) if (m && typeof m.Name === 'string') roster.set(m.Name.toLowerCase(), h.Name);
      }
    }
    this.houseCtx = { houseNames: [...names], rosterIndex: roster };
  }

  ctx() {
    return { ...this.houseCtx, realmName: this.store.data.realmName, portalUrl: this.store.data.portalUrl };
  }

  // One poll. Returns the number of events queued.
  async tick() {
    this.lastCheck = new Date().toISOString();
    const list = await this.readJson('RealmChronicle.json');
    if (!Array.isArray(list)) return 0;
    const events = list.filter((e) => e && Number.isInteger(e.id) && typeof e.type === 'string').sort((a, b) => a.id - b.id);
    const maxId = events.length ? events[events.length - 1].id : 0;
    const d = this.store.data;
    if (d.lastId === null || maxId < d.lastId) {
      // First run (or the chronicle was wiped): start after the newest event; never post history.
      d.lastId = maxId;
      await this.store.save().catch(() => {});
      return 0;
    }
    const fresh = events.filter((e) => e.id > d.lastId);
    if (!fresh.length) return 0;
    d.lastId = fresh[fresh.length - 1].id;
    this.lastEventAt = fresh[fresh.length - 1].ts || null;
    await this.store.save().catch((e) => this.log('warn', `Discord herald: could not save its position: ${e.message}`));
    if (!d.enabled || !this.store.url()) return 0;
    const wanted = new Set(d.types);
    const posts = fresh.filter((e) => wanted.has(e.type));
    if (!posts.length) return 0;
    await this.refreshHouses();
    const ctx = this.ctx();
    this.queue.enqueue(posts.map((e) => buildEmbed(e, ctx)));
    return posts.length;
  }

  start(intervalMs = 15000) {
    this.stop();
    const run = () => this.tick().catch((e) => this.log('warn', `Discord herald: poll failed: ${e.message}`));
    run();
    this.timer = setInterval(run, intervalMs);
    if (this.timer.unref) this.timer.unref();
  }

  stop() {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }
}

// ---------------------------------------------------------------- Steward wiring

function sampleEvent(realmName) {
  return {
    id: 0,
    ts: new Date().toISOString(),
    type: 'decree',
    title: 'A raven from the Steward',
    detail: `This is a test from Realm Steward. When the Chronicle of ${realmName || 'the realm'} records a new event, it will appear here like this.`,
    actors: []
  };
}

// deps: { handle, userData, dataDir: () => string|null, safeStorage, log(level, msg), fetchImpl?, pollMs? }
function registerSteward(deps) {
  const log = deps.log || (() => {});
  const safe = deps.safeStorage;
  const crypt = safe
    ? {
        available: () => {
          try {
            return safe.isEncryptionAvailable();
          } catch {
            return false;
          }
        },
        encrypt: (t) => safe.encryptString(t).toString('base64'),
        decrypt: (b) => safe.decryptString(Buffer.from(b, 'base64'))
      }
    : null;
  const store = new DiscordStore(deps.userData, crypt);
  let relay = null;
  const queue = new RelayQueue({
    send: makeSender(() => store.url(), deps.fetchImpl),
    ctx: () => (relay ? relay.ctx() : { realmName: store.data.realmName, portalUrl: store.data.portalUrl }),
    log,
    onFatal: (res) => {
      store.data.enabled = false;
      store.save().catch(() => {});
      log('warn', `Discord herald turned itself off: ${scrubSecrets(res.error)}`);
    }
  });
  relay = new ChronicleRelay({ store, queue, dataDir: deps.dataDir, log });
  const ready = store.load().then(() => relay.start(deps.pollMs || 15000));

  const status = () => ({
    enabled: store.data.enabled,
    hasUrl: !!store.url(),
    urlHint: redactWebhookUrl(store.url() || ''),
    encrypted: store.encrypted(),
    savedButUnreadable: !!store.data.webhook && !store.url(),
    types: store.data.types,
    allTypes: EVENT_TYPES.map(({ type, label, group }) => ({ type, label, group })),
    realmName: store.data.realmName,
    portalUrl: store.data.portalUrl,
    lastId: store.data.lastId,
    queued: queue.length,
    sent: queue.stats.sent,
    failed: queue.stats.failed,
    dropped: queue.stats.dropped,
    lastError: queue.stats.lastError,
    lastSentAt: queue.stats.lastSentAt,
    lastCheck: relay.lastCheck,
    watching: deps.dataDir() || null
  });

  deps.handle('discord:get', async () => {
    await ready;
    return status();
  });

  deps.handle('discord:save', async (patch) => {
    await ready;
    if (!patch || typeof patch !== 'object' || Array.isArray(patch)) throw Object.assign(new Error('Nothing to save.'), { friendly: true });
    if (typeof patch.url === 'string' && patch.url.trim()) store.setUrl(patch.url);
    if (Array.isArray(patch.types)) store.data.types = sanitizeTypes(patch.types).filter((t) => TYPE_BY_ID.has(t));
    if (typeof patch.realmName === 'string') store.data.realmName = patch.realmName.trim().slice(0, 60);
    if (typeof patch.portalUrl === 'string') {
      const p = patch.portalUrl.trim();
      if (p && !safeHttps(p)) throw Object.assign(new Error('The website address must start with https://'), { friendly: true });
      store.data.portalUrl = p;
    }
    if (typeof patch.enabled === 'boolean') {
      if (patch.enabled && !store.url()) throw Object.assign(new Error('Paste a webhook URL before turning the Discord herald on.'), { friendly: true });
      if (patch.enabled && !store.data.enabled) {
        store.data.lastId = null; // start after the newest event; never post old history
        store.data.enabled = true;
        await relay.tick().catch(() => {});
      }
      store.data.enabled = patch.enabled;
    }
    await store.save();
    log('info', `Discord herald settings saved (${store.data.enabled ? 'on' : 'off'}, ${store.data.types.length} event types)`);
    return status();
  });

  deps.handle('discord:test', async () => {
    await ready;
    if (!store.url()) throw Object.assign(new Error('Paste and save a webhook URL first.'), { friendly: true });
    await relay.refreshHouses().catch(() => {});
    const embed = buildEmbed(sampleEvent(store.data.realmName), relay.ctx());
    const res = await queue.deliver(buildPayload([embed], relay.ctx()));
    if (!res.ok) {
      log('warn', `Discord herald test failed: ${scrubSecrets(res.error)}`);
      throw Object.assign(new Error(scrubSecrets(res.error)), { friendly: true });
    }
    log('info', 'Discord herald test message delivered');
    return { delivered: true, status: res.status };
  });

  deps.handle('discord:forget', async () => {
    await ready;
    store.setUrl(null);
    queue.clear();
    await store.save();
    log('info', 'Discord herald webhook forgotten');
    return status();
  });

  return { store, queue, relay, ready, stop: () => relay.stop() };
}

module.exports = {
  validateWebhookUrl,
  redactWebhookUrl,
  scrubSecrets,
  escapeText,
  houseColour,
  houseForEvent,
  buildEmbed,
  buildPayload,
  embedSize,
  webhookUsername,
  RelayQueue,
  DiscordStore,
  ChronicleRelay,
  makeSender,
  registerSteward,
  EVENT_TYPES,
  DEFAULT_TYPES,
  LIMITS
};
