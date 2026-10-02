import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import test from 'node:test';
import { createRealmBot } from '../src/bot.js';
import { commandDefinitions, validateDefinitions } from '../src/commands.js';
import { createStateStore } from '../src/state-store.js';
import { NOW, fakeChronicle, fakeDiscordModule, fakeInteraction, quietLogger, testConfig, tmpDir } from './helpers.js';

async function waitFor(cond, timeoutMs = 2000) {
  const end = Date.now() + timeoutMs;
  while (!cond() && Date.now() < end) await new Promise((r) => setTimeout(r, 5));
}

test('command definitions pass Discord limits, with and without house roles', () => {
  for (const swearEnabled of [true, false]) {
    const defs = commandDefinitions({ swearEnabled });
    assert.deepEqual(validateDefinitions(defs), []);
    const subs = defs[0].options.map((o) => o.name);
    assert.deepEqual(subs.slice(0, 7), ['status', 'king', 'houses', 'chronicle', 'events', 'join', 'whois']);
    assert.equal(subs.includes('swear'), swearEnabled);
  }
  assert.ok(validateDefinitions([{ name: 'Bad Name', description: '' }]).length >= 2);
});

test('wiring: Guilds intent only, no mentions, login with the token, ready registers /realm and starts the board', async () => {
  const discord = fakeDiscordModule();
  const cfg = testConfig();
  const logger = quietLogger();
  const timers = { setTimeout: () => ({}), clearTimeout() {} };
  const bot = createRealmBot({ discord, cfg, logger, chronicle: fakeChronicle(), now: () => NOW, timers });
  const client = discord.made[0];
  assert.deepEqual(client.opts.intents, [1]);
  assert.deepEqual(client.opts.allowedMentions, { parse: [] });
  await bot.start();
  assert.equal(client.loggedInWith, cfg.token);
  client.emit('clientReady', client);
  // The ready handler registers commands and starts the board asynchronously; wait for its log line
  // instead of a fixed sleep, which raced on slow CI runners.
  await waitFor(() => logger.lines.some((l) => /status message: channel/.test(l)));
  assert.equal(client.registered.length, 1);
  assert.equal(client.registered[0].guildId, cfg.guildId);
  assert.equal(client.registered[0].defs[0].name, 'realm');
  assert.ok(logger.lines.some((l) => /status message: channel/.test(l)));
  assert.ok(!logger.lines.some((l) => l.includes(cfg.token)), 'token never logged');

  const i = fakeInteraction('houses');
  client.emit('interactionCreate', i);
  await waitFor(() => i.replies.length > 0);
  assert.equal(i.replies.length, 1);
  await bot.stop();
  assert.equal(client.destroyed, true);
});

test('registration failure is logged with a hint, the bot keeps running', async () => {
  const discord = fakeDiscordModule();
  const logger = quietLogger();
  const bot = createRealmBot({ discord, cfg: testConfig({ status: { channelId: null, intervalSec: 60 } }), logger, chronicle: fakeChronicle() });
  const client = discord.made[0];
  client.application.commands.set = async () => { throw Object.assign(new Error('Missing Access'), { code: 50001 }); };
  await bot.start();
  client.emit('clientReady', client);
  await new Promise((r) => setTimeout(r, 20));
  assert.ok(logger.lines.some((l) => /applications\.commands scope/.test(l)));
});

test('state store: atomic save, reload, and a corrupt file is set aside', async () => {
  const dir = tmpDir();
  const path = join(dir, 'sub', 'state.json');
  const s = createStateStore(path, { logger: quietLogger() });
  await s.load();
  s.data.status = { channelId: '1', messageId: '2' };
  await s.save();
  const again = createStateStore(path, { logger: quietLogger() });
  await again.load();
  assert.equal(again.data.status.messageId, '2');
  writeFileSync(path, '{ broken');
  const logger = quietLogger();
  const third = createStateStore(path, { logger });
  await third.load();
  assert.equal(third.data.status.messageId, null);
  assert.ok(logger.lines.some((l) => /moved it to/.test(l)));
  assert.throws(() => readFileSync(path), /ENOENT/);
});

// The real discord.js is only installed after `npm install`; these checks are skipped without it.
const require = createRequire(import.meta.url);
let realDiscord = null;
try {
  require.resolve('discord.js');
  realDiscord = await import('discord.js');
} catch {
  realDiscord = null;
}

test('real discord.js: the constants and classes the bot relies on exist', { skip: !realDiscord && 'discord.js not installed (run npm install)' }, () => {
  const d = realDiscord;
  assert.equal(typeof d.Client, 'function');
  assert.equal(typeof d.GatewayIntentBits.Guilds, 'number');
  assert.equal(d.MessageFlags.Ephemeral, 64, 'EPHEMERAL constant in handlers.js');
  assert.equal(d.ApplicationCommandOptionType.Subcommand, 1);
  assert.equal(d.ApplicationCommandOptionType.String, 3);
  assert.equal(d.ApplicationCommandOptionType.Integer, 4);
  assert.equal(d.ButtonStyle.Link, 5);
  assert.equal(d.ComponentType.ActionRow, 1);
  assert.equal(d.ComponentType.Button, 2);
  assert.equal(d.Routes.applicationGuildCommands('1', '2'), '/applications/1/guilds/2/commands');
  assert.ok(d.Events.InteractionCreate && d.Events.ClientReady);
});

test('real discord.js: the command JSON round-trips through SlashCommandBuilder validation', { skip: !realDiscord && 'discord.js not installed' }, () => {
  const { SlashCommandBuilder } = realDiscord;
  const def = commandDefinitions({ swearEnabled: true })[0];
  const b = new SlashCommandBuilder().setName(def.name).setDescription(def.description);
  for (const o of def.options) {
    b.addSubcommand((s) => {
      s.setName(o.name).setDescription(o.description);
      for (const x of o.options || []) {
        if (x.type === 3) s.addStringOption((y) => y.setName(x.name).setDescription(x.description).setRequired(!!x.required).setAutocomplete(!!x.autocomplete).setMinLength(x.min_length).setMaxLength(x.max_length));
        if (x.type === 4) s.addIntegerOption((y) => y.setName(x.name).setDescription(x.description).setRequired(!!x.required).setMinValue(x.min_value).setMaxValue(x.max_value));
      }
      return s;
    });
  }
  const built = b.toJSON(); // throws if any name, description or limit is invalid
  assert.deepEqual(built.options.map((o) => o.name), def.options.map((o) => o.name));
  // What client.application.commands.set() actually sends keeps the guild-only context and the limits.
  const sent = realDiscord.ApplicationCommandManager.transformCommand(def);
  assert.deepEqual(sent.contexts, [0]);
  assert.deepEqual(sent.integration_types, [0]);
  const chronicle = sent.options.find((o) => o.name === 'chronicle').options[0];
  assert.deepEqual([chronicle.min_value, chronicle.max_value], [1, 15]);
  const whois = sent.options.find((o) => o.name === 'whois').options[0];
  assert.deepEqual([whois.autocomplete, whois.max_length], [true, 64]);
});

test('real discord.js: createRealmBot builds a real Client and routes an interaction (no login)', { skip: !realDiscord && 'discord.js not installed' }, async () => {
  const bot = createRealmBot({ discord: realDiscord, cfg: testConfig(), logger: quietLogger(), chronicle: fakeChronicle(), now: () => NOW });
  assert.ok(bot.client instanceof realDiscord.Client);
  const i = fakeInteraction('king');
  bot.client.emit(realDiscord.Events.InteractionCreate, i);
  await new Promise((r) => setTimeout(r, 30));
  assert.equal(i.replies.length, 1);
  assert.match(i.replies[0].embeds[0].title, /Aldric Varrow/);
  await bot.client.destroy();
});
