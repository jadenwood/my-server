// Test doubles: a fake Chronicle client, fake discord.js objects (interaction, guild, member, roles,
// channel, client) and a fake discord.js module. No network, no real Discord.

import { EventEmitter } from 'node:events';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { cleanEvents, cleanState, ChronicleUnavailable } from '../src/chronicle-client.js';
import { loadConfig } from '../src/config.js';

export const FIXTURES = fileURLToPath(new URL('./fixtures/', import.meta.url));
export const SAMPLE_DIR = fileURLToPath(new URL('../../chronicle/sample-data/', import.meta.url));
export const NOW = Date.parse('2026-10-02T19:30:00Z'); // a Friday, during the fixture tournament

export const tmpDir = () => mkdtempSync(join(tmpdir(), 'realm-bot-'));

export const quietLogger = () => {
  const lines = [];
  const rec = (lvl) => (...a) => lines.push(`${lvl} ${a.map(String).join(' ')}`);
  return { log: rec('log'), warn: rec('warn'), error: rec('error'), lines };
};

export function testConfig(overrides = {}, env = {}) {
  const cfg = loadConfig({
    DISCORD_TOKEN: 'x'.repeat(60),
    DISCORD_APPLICATION_ID: '111111111111111111',
    DISCORD_GUILD_ID: '222222222222222222',
    REALM_STATUS_CHANNEL_ID: '333333333333333333',
    REALM_DATA_DIR: join(FIXTURES, 'data'),
    REALM_CONFIG_DIR: join(FIXTURES, 'config'),
    REALM_JOIN_ADDRESS: 'play.example.invalid',
    REALM_JOIN_PORT: '7350',
    REALM_SERVER_ID: 'ostreval',
    REALM_PLAYER_APP_URL: 'https://example.invalid/realm',
    REALM_SWEAR_ENABLED: 'true',
    REALM_BOT_STATE_FILE: join(tmpDir(), 'state.json'),
    ...env,
  }, { baseDir: FIXTURES });
  return { ...cfg, ...overrides };
}

export const SAMPLE_STATE = {
  king: 'Aldric Varrow', house: 'Varrow', since: '2026-09-30T19:42:10Z',
  houses: [
    { name: 'Varrow', sigil: 'Iron Stag', liege: null, members: 9 },
    { name: 'Ashgrove', sigil: 'White Oak', liege: 'Varrow', members: 6 },
    { name: 'Corvane', sigil: 'Black Raven', liege: null, members: 7 },
    { name: 'Dunmere', sigil: 'Drowned Bell', liege: 'Corvane', members: 4 },
    { name: 'Halloran', sigil: 'Ember Hound', liege: null, members: 5 },
    { name: 'Merrin', sigil: 'Silver Eel', liege: 'Varrow', members: 3 },
  ],
  online: 23, maxPlayers: 40, updated: '2026-10-02T19:29:40Z', stale: false,
};

export const SAMPLE_EVENTS = [
  { id: 1, ts: '2026-09-27T18:02:11Z', type: 'house_founded', title: 'House Varrow is founded', detail: 'Aldric Varrow raises the Iron Stag.', actors: ['Aldric Varrow'] },
  { id: 2, ts: '2026-09-28T17:31:40Z', type: 'coronation', title: 'Ysolde Corvane takes the throne', detail: 'House Corvane now holds the crown.', actors: ['Ysolde Corvane'] },
  { id: 3, ts: '2026-09-29T18:00:00Z', type: 'claim_declared', title: 'House Varrow claims the crown', detail: 'Aldric Varrow calls the banners.', actors: [] },
  { id: 4, ts: '2026-09-30T19:42:10Z', type: 'coronation', title: 'Aldric Varrow takes the throne', detail: 'House Varrow now holds the crown.', actors: [] },
  { id: 5, ts: '2026-10-01T20:00:00Z', type: 'tournament_champion', title: 'Ysolde Corvane wins the Royal Tournament', detail: '6 kills.', actors: [] },
  { id: 6, ts: '2026-10-02T19:00:00Z', type: 'event_started', title: 'The Royal Tournament begins', detail: '', actors: [] },
];

// A Chronicle client double with the same surface as createChronicleClient().
export function fakeChronicle({ state = SAMPLE_STATE, events = SAMPLE_EVENTS, down = false } = {}) {
  const calls = [];
  const c = {
    calls,
    down,
    state: async () => {
      calls.push('state');
      if (c.down) throw new ChronicleUnavailable('down');
      return { ...cleanState(c.stateValue), warning: null };
    },
    events: async (limit = 50) => {
      calls.push(`events:${limit}`);
      if (c.down) throw new ChronicleUnavailable('down');
      return cleanEvents(c.eventsValue).slice(-limit);
    },
    health: async () => ({ ok: true }),
    stateValue: state,
    eventsValue: events,
  };
  return c;
}

// ---- discord.js doubles ----

export class FakeCollection extends Map {
  find(fn) {
    for (const v of this.values()) if (fn(v)) return v;
    return undefined;
  }
  filter(fn) {
    return new FakeCollection([...this].filter(([, v]) => fn(v)));
  }
}

let nextId = 900000000000000000n;
export const snowflake = () => String(nextId++);

export function fakeRole(name, { permissions = 0n, editable = true, managed = false, id = snowflake() } = {}) {
  return { id, name, permissions: { bitfield: permissions }, editable, managed };
}

export function fakeGuild({ roles = [], failCreate = null } = {}) {
  const guild = {
    id: '222222222222222222',
    created: [],
    roles: {
      cache: new FakeCollection(roles.map((r) => [r.id, r])),
      async create(opts) {
        if (failCreate) throw failCreate;
        const r = fakeRole(opts.name);
        r.color = opts.color;
        r.createOptions = opts;
        guild.roles.cache.set(r.id, r);
        guild.created.push(r);
        return r;
      },
    },
    members: { fetch: async () => { throw new Error('members.fetch should not be needed'); } },
  };
  return guild;
}

export function fakeMember(roles = []) {
  const m = {
    log: [],
    roles: {
      cache: new FakeCollection(roles.map((r) => [r.id, r])),
      async add(role, reason) {
        m.log.push(['add', role.name, reason]);
        m.roles.cache.set(role.id, role);
      },
      async remove(role, reason) {
        m.log.push(['remove', role.name, reason]);
        m.roles.cache.delete(role.id);
      },
    },
  };
  return m;
}

// A chat-input interaction for /realm <sub> with the given options.
export function fakeInteraction(sub, opts = {}, { guild = fakeGuild(), member = fakeMember(), userId = '444444444444444444', autocomplete = null } = {}) {
  const i = {
    commandName: 'realm',
    user: { id: userId, tag: 'tester#0001' },
    guild,
    member,
    replies: [],
    deferred: null,
    isChatInputCommand: () => !autocomplete,
    isAutocomplete: () => !!autocomplete,
    options: {
      getSubcommand: () => sub,
      getInteger: (n) => (n in opts ? opts[n] : null),
      getString: (n, required) => {
        if (n in opts) return opts[n];
        if (required) throw new Error(`missing required option ${n}`);
        return null;
      },
      getFocused: () => autocomplete,
    },
    async deferReply(o) {
      i.deferred = o;
    },
    async editReply(o) {
      i.replies.push(o);
      return o;
    },
    async reply(o) {
      i.replies.push(o);
      return o;
    },
    async respond(choices) {
      i.choices = choices;
    },
  };
  return i;
}

export function fakeChannel({ botId = 'bot-user' } = {}) {
  const ch = {
    sent: [],
    edits: [],
    stored: new Map(),
    failSend: null,
    failFetch: null,
    async send(body) {
      if (ch.failSend) throw ch.failSend;
      const msg = { id: snowflake(), author: { id: botId }, body, edit: async (b) => { ch.edits.push({ id: msg.id, body: b }); msg.body = b; return msg; } };
      ch.stored.set(msg.id, msg);
      ch.sent.push(body);
      return msg;
    },
    messages: {
      async fetch(id) {
        if (ch.failFetch) throw ch.failFetch;
        const m = ch.stored.get(id);
        if (!m) throw Object.assign(new Error('Unknown Message'), { code: 10008 });
        return m;
      },
    },
  };
  return ch;
}

export function fakeClient(channel) {
  return {
    user: { id: 'bot-user', tag: 'Realm#0001' },
    channels: {
      async fetch(id) {
        if (!channel) throw Object.assign(new Error('Unknown Channel'), { code: 10003 });
        return channel;
      },
    },
  };
}

export const discordError = (code, message = `Discord error ${code}`) => Object.assign(new Error(message), { code });

// A fake discord.js module for createRealmBot().
export function fakeDiscordModule() {
  const made = [];
  class Client extends EventEmitter {
    constructor(opts) {
      super();
      this.opts = opts;
      this.user = { id: 'bot-user', tag: 'Realm#0001' };
      this.guilds = { cache: new Map([['222222222222222222', {}]]) };
      this.channels = fakeClient(fakeChannel()).channels;
      this.registered = [];
      this.application = { commands: { set: async (defs, guildId) => { this.registered.push({ defs, guildId }); return defs; } } };
      this.loggedInWith = null;
      made.push(this);
    }
    async login(token) {
      this.loggedInWith = token;
      return token;
    }
    async destroy() {
      this.destroyed = true;
    }
  }
  return {
    made,
    Client,
    GatewayIntentBits: { Guilds: 1 },
    Events: { InteractionCreate: 'interactionCreate', ClientReady: 'clientReady', Error: 'error', Warn: 'warn' },
  };
}
