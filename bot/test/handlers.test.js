import assert from 'node:assert/strict';
import { join } from 'node:path';
import test from 'node:test';
import { createHandlers, EPHEMERAL } from '../src/handlers.js';
import { createRealmData } from '../src/realm-data.js';
import { createStateStore } from '../src/state-store.js';
import { createSwearService } from '../src/swear.js';
import { LIMITS, embedSize } from '../src/text.js';
import { FIXTURES, NOW, SAMPLE_STATE, fakeChronicle, fakeGuild, fakeInteraction, fakeMember, quietLogger, testConfig } from './helpers.js';

function setup({ cfg = testConfig(), chronicle = fakeChronicle(), logger = quietLogger() } = {}) {
  const realmData = createRealmData({ dataDir: join(FIXTURES, 'data'), configDir: join(FIXTURES, 'config'), now: () => NOW });
  const store = createStateStore(cfg.stateFile, { logger });
  const swearService = createSwearService({ cfg, store, now: () => NOW, logger });
  const h = createHandlers({ cfg, chronicle, realmData, swearService, logger, now: () => NOW });
  return { h, cfg, chronicle, logger, store };
}

async function run(h, sub, opts, extra) {
  const i = fakeInteraction(sub, opts, extra);
  await h.handle(i);
  assert.equal(i.replies.length, 1, `/realm ${sub} replied ${i.replies.length} times`);
  const reply = i.replies[0];
  assert.deepEqual(reply.allowedMentions, { parse: [] }, 'every reply disables mentions');
  for (const e of reply.embeds || []) {
    assert.ok(embedSize(e) <= LIMITS.total);
    assert.ok(!e.description || e.description.length <= LIMITS.description);
  }
  return { i, reply, embed: reply.embeds?.[0], text: JSON.stringify(reply) };
}

test('/realm status: crown, online, houses, running event, join address and latest chronicle', async () => {
  const { h } = setup();
  const { i, embed, text } = await run(h, 'status');
  assert.deepEqual(i.deferred, {}, 'public reply');
  assert.match(embed.title, /The Realm of Ostreval/);
  assert.match(text, /Aldric Varrow/);
  assert.match(text, /\*\*23\*\* \/ 40/);
  assert.match(text, /Royal Tournament\*\* is on now/);
  assert.match(text, /play\.example\.invalid:7350/);
  assert.match(text, /The Royal Tournament begins/);
  assert.equal(embed.color, 0x4a2347, 'ruling house colour (Varrow)');
});

test('/realm status when the server stopped reporting says so', async () => {
  const chronicle = fakeChronicle({ state: { ...SAMPLE_STATE, stale: true } });
  const { h } = setup({ chronicle });
  const { text, embed } = await run(h, 'status');
  assert.match(text, /has not reported since/);
  assert.match(embed.footer.text, /not reporting/);
  assert.match(text, /"name":"Online","value":"unknown"/);
});

test('Chronicle down: every read command answers "The ravens are late" instead of failing', async () => {
  const chronicle = fakeChronicle({ down: true });
  const { h, logger } = setup({ chronicle });
  for (const sub of ['status', 'king', 'houses', 'chronicle', 'whois']) {
    const { embed } = await run(h, sub, { house: 'Varrow', count: 3 });
    assert.match(embed.title, /ravens are late/, sub);
  }
  assert.ok(logger.lines.some((l) => /down/.test(l)));
  // /realm events still works from the data files alone.
  const { text } = await run(h, 'events');
  assert.match(text, /Royal Tournament/);
});

test('/realm king: monarch, reign, ruling house with vassals and crown events', async () => {
  const { h } = setup();
  const { embed, text } = await run(h, 'king');
  assert.equal(embed.title, '👑 Aldric Varrow');
  assert.match(text, /House Varrow/);
  assert.match(text, /vassals: Ashgrove, Merrin/);
  assert.match(text, /<t:\d+:f>/);
  assert.match(text, /1d 23h/, 'reign length (Sep 30 19:42 to Oct 2 19:30)');
  assert.match(text, /A Claim/);
  assert.ok(!/House Varrow is founded/.test(text), 'only crown matters are listed');
});

test('/realm king with an empty throne', async () => {
  const { h } = setup({ chronicle: fakeChronicle({ state: { ...SAMPLE_STATE, king: null, house: null, since: null } }) });
  const { text } = await run(h, 'king');
  assert.match(text, /stands empty/);
});

test('/realm houses: vassals listed under their liege, crown marked', async () => {
  const { h } = setup();
  const { embed } = await run(h, 'houses');
  const lines = embed.description.split('\n');
  assert.match(lines[0], /\*\*Varrow\*\* 👑/);
  assert.match(lines[1], /↳ \*\*Ashgrove\*\*/);
  assert.match(lines[2], /↳ \*\*Merrin\*\*/);
  assert.ok(lines.findIndex((l) => /Dunmere/.test(l)) === lines.findIndex((l) => /Corvane/.test(l)) + 1);
  assert.equal(lines.length, 6);
});

test('/realm houses survives a liege loop and an empty realm', async () => {
  const loop = { ...SAMPLE_STATE, houses: [{ name: 'A', liege: 'B', members: 1 }, { name: 'B', liege: 'A', members: 1 }] };
  const { h } = setup({ chronicle: fakeChronicle({ state: loop }) });
  const { embed } = await run(h, 'houses');
  assert.equal(embed.description.split('\n').length, 2);
  const { h: h2 } = setup({ chronicle: fakeChronicle({ state: { ...SAMPLE_STATE, houses: [] } }) });
  assert.match((await run(h2, 'houses')).text, /No house has been founded/);
});

test('/realm chronicle [n]: newest first, defaults to 5, clamps to 15', async () => {
  const { h, chronicle } = setup();
  const { embed } = await run(h, 'chronicle', { count: 2 });
  assert.match(embed.description, /^📯 \*\*Realm Event\*\*/);
  assert.match(embed.description, /`#6`[\s\S]*`#5`/);
  assert.ok(!/`#4`/.test(embed.description));
  await run(h, 'chronicle', {});
  assert.ok(chronicle.calls.includes('events:5'));
  await run(h, 'chronicle', { count: 999 });
  assert.ok(chronicle.calls.includes('events:15'));
});

test('/realm chronicle escapes player text', async () => {
  const evil = [{ id: 9, ts: '2026-10-02T10:00:00Z', type: 'decree', title: '@everyone [free](https://evil) **x**', detail: '<@1234567890>', actors: [] }];
  const { h } = setup({ chronicle: fakeChronicle({ events: evil }) });
  const { embed } = await run(h, 'chronicle', { count: 1 });
  assert.ok(!embed.description.includes('@everyone'));
  assert.ok(!embed.description.includes('[free](https'));
  assert.ok(!embed.description.includes('<@1234567890>'));
});

test('/realm events: running tournament leaders, upcoming schedule, recent results', async () => {
  const { h } = setup();
  const { embed, text } = await run(h, 'events');
  assert.equal(embed.fields[0].name, '🏆 Now: The Royal Tournament');
  assert.match(embed.fields[0].value, /1\. Ysolde Corvane \(6\), 2\. Aldric Varrow \(4\)/);
  assert.match(embed.fields[1].value, /Truce of the Realm[\s\S]*Crown Night[\s\S]*Royal Tournament/);
  assert.match(text, /Tournament Champion/);
});

test('/realm join: private, address, port, realm:// link and an https button', async () => {
  const { h } = setup();
  const { i, reply, text } = await run(h, 'join');
  assert.deepEqual(i.deferred, { flags: EPHEMERAL });
  assert.match(text, /`play\.example\.invalid`/);
  assert.match(text, /`7350`/);
  assert.match(text, /realm:\/\/join\/ostreval/);
  assert.deepEqual(reply.components, [{ type: 1, components: [{ type: 2, style: 5, label: 'Get the Realm player app', url: 'https://example.invalid/realm' }] }]);
});

test('/realm join without an address configured', async () => {
  const cfg = testConfig({}, { REALM_JOIN_ADDRESS: '', REALM_PLAYER_APP_URL: '' });
  const { h } = setup({ cfg });
  const { reply, text } = await run(h, 'join');
  assert.match(text, /has not set the public address/);
  assert.deepEqual(reply.components, []);
});

test('/realm whois: chronicle data, houses file detail, treaties, lore, chronicle mentions', async () => {
  const { h } = setup();
  const { embed, text } = await run(h, 'whois', { house: 'house varrow' });
  assert.equal(embed.title, '🛡️ House Varrow');
  assert.equal(embed.color, 0x4a2347);
  const f = Object.fromEntries(embed.fields.map((x) => [x.name, x.value]));
  assert.equal(f.Sworn, '9');
  assert.equal(f.Vassals, 'Ashgrove, Merrin');
  assert.match(f['The crown'], /Aldric Varrow/);
  assert.equal(f['Led by'], 'Aldric Varrow');
  assert.equal(f.Officers, 'Bryn');
  assert.match(f.Record, /1 treaty broken/);
  assert.match(f.Treaties, /with Merrin/);
  assert.ok(!/Corvane/.test(f.Treaties), 'expired treaty not shown');
  assert.match(f['Of old'], /We Stand Our Ground/);
  assert.match(f['Lately in the Chronicle'], /House Varrow claims the crown/);
  assert.ok(!text.includes('7656119'), 'no Steam ids');
});

test('/realm whois for a house without a houses-file entry or lore', async () => {
  const state = { ...SAMPLE_STATE, houses: [...SAMPLE_STATE.houses, { name: 'Thornwick', sigil: null, liege: null, members: 1 }] };
  const { h } = setup({ chronicle: fakeChronicle({ state }) });
  const { embed } = await run(h, 'whois', { house: 'Thornwick' });
  const names = embed.fields.map((x) => x.name);
  assert.deepEqual(names, ['Sworn', 'Liege']);
});

test('/realm whois unknown house suggests close names, privately', async () => {
  const { h } = setup();
  const { embed } = await run(h, 'whois', { house: 'Var' });
  assert.equal(embed.title, 'No such house');
  assert.match(embed.description, /Did you mean \*\*Varrow\*\*/);
});

test('autocomplete: house names, prefix matches first, at most 25', async () => {
  const many = { ...SAMPLE_STATE, houses: Array.from({ length: 40 }, (_, k) => ({ name: `House${k}`, members: k })) };
  const { h } = setup({ chronicle: fakeChronicle({ state: many }) });
  const i = fakeInteraction('whois', {}, { autocomplete: { name: 'house', value: '' } });
  await h.handle(i);
  assert.equal(i.choices.length, 25);
  const { h: h2 } = setup();
  const j = fakeInteraction('swear', {}, { autocomplete: { name: 'house', value: 'house ash' } });
  await h2.handle(j);
  assert.deepEqual(j.choices, [{ name: 'House Ashgrove (White Oak)', value: 'Ashgrove' }]);
  const { h: h3 } = setup({ chronicle: fakeChronicle({ down: true }) });
  const k = fakeInteraction('whois', {}, { autocomplete: { name: 'house', value: 'x' } });
  await h3.handle(k);
  assert.deepEqual(k.choices, [], 'chronicle down gives no choices, not an error');
});

test('unknown subcommand and foreign commands', async () => {
  const { h } = setup();
  const i = fakeInteraction('dance');
  await h.handle(i);
  assert.equal(i.replies[0].flags, EPHEMERAL);
  const other = fakeInteraction('status');
  other.commandName = 'other';
  await h.handle(other);
  assert.equal(other.replies.length, 0);
});

test('an unexpected error gives a generic message and is logged, never thrown', async () => {
  const chronicle = fakeChronicle();
  chronicle.state = async () => { throw new TypeError('boom'); };
  const { h, logger } = setup({ chronicle });
  const { embed } = await run(h, 'houses');
  assert.equal(embed.title, 'Something went wrong');
  assert.ok(logger.lines.some((l) => /boom/.test(l)));
});

test('an expired interaction (editReply fails) is logged, not thrown', async () => {
  const { h, logger } = setup();
  const i = fakeInteraction('status');
  i.editReply = async () => { throw Object.assign(new Error('Unknown interaction'), { code: 10062 }); };
  await h.handle(i);
  assert.ok(logger.lines.some((l) => /10062/.test(l)));
});

test('swear and forswear reply privately', async () => {
  const { h } = setup();
  const guild = fakeGuild();
  const member = fakeMember();
  const { i, embed } = await run(h, 'swear', { house: 'Ashgrove' }, { guild, member });
  assert.deepEqual(i.deferred, { flags: EPHEMERAL });
  assert.match(embed.description, /bent the knee to \*\*House Ashgrove\*\*/);
  assert.match(embed.description, /not linked to your game account/);
  const f = await run(h, 'forswear', {}, { guild, member });
  assert.deepEqual(f.i.deferred, { flags: EPHEMERAL });
  assert.match(f.embed.description, /Sworn to Ashgrove/);
});

test('end to end: real chronicle/server.js (sample data) -> HTTP client -> /realm commands', async (t) => {
  const { createServer } = await import('node:http');
  const { createApp } = await import('../../chronicle/server.js');
  const { createChronicleClient } = await import('../src/chronicle-client.js');
  const { SAMPLE_DIR } = await import('./helpers.js');
  const server = createServer(createApp({ dataDir: SAMPLE_DIR, staleAfterSec: 0 }));
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  t.after(() => server.close());
  const chronicle = createChronicleClient({ baseUrl: `http://127.0.0.1:${server.address().port}` });
  const { h } = setup({ chronicle });
  assert.match((await run(h, 'status')).text, /Aldric Varrow/);
  assert.equal((await run(h, 'houses')).embed.description.split('\n').length, 6);
  const c = await run(h, 'chronicle', { count: 15 });
  assert.equal((c.embed.description.match(/`#\d+`/g) || []).length, Math.min(15, c.embed.description.split('\n\n').length));
  assert.match((await run(h, 'whois', { house: 'Corvane' })).text, /Every Secret Has a Price/);
});
