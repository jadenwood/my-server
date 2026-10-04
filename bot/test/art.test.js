import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { EVENT_ICONS, GREAT_HOUSES, TITLES, createArt, decorate, greatHouse, stripArt, titleIn } from '../src/art.js';
import { createHandlers } from '../src/handlers.js';
import { createRealmData } from '../src/realm-data.js';
import { createStateStore } from '../src/state-store.js';
import { createStatusBoard } from '../src/status-board.js';
import { createSwearService } from '../src/swear.js';
import { LIMITS, embedSize } from '../src/text.js';
import { FIXTURES, NOW, fakeChannel, fakeChronicle, fakeClient, fakeInteraction, quietLogger, testConfig } from './helpers.js';

const ART = fileURLToPath(new URL('../../art/', import.meta.url));
const PNG = join(ART, 'png');
const artCfg = () => testConfig({}, { REALM_ART_DIR: PNG });

test('the art tables match the art pack (event map, great houses, renown titles)', () => {
  assert.deepEqual(EVENT_ICONS, JSON.parse(readFileSync(join(ART, 'icons', 'event-map.json'), 'utf8')).icons);
  assert.deepEqual(GREAT_HOUSES.slice().sort(), Object.keys(JSON.parse(readFileSync(join(ART, 'palette.json'), 'utf8')).houses).sort());
  assert.deepEqual(TITLES, JSON.parse(readFileSync(join(ART, 'src', 'titles.json'), 'utf8')).titles.map((t) => [t.id, t.name]));
  // Every type the Chronicle serves has a picture.
  const server = readFileSync(fileURLToPath(new URL('../../chronicle/server.js', import.meta.url)), 'utf8');
  const types = [...(server.match(/EVENT_TYPES = new Set\(\[([\s\S]*?)\]\)/)[1].replace(/\/\/.*$/gm, '').matchAll(/'([a-z_]+)'/g))].map((m) => m[1]);
  assert.equal(types.length, 43);
  for (const t of types) assert.ok(EVENT_ICONS[t], `${t} has an icon`);
});

test('createArt finds every picture in art/png, and nothing when off or missing', () => {
  const art = createArt({ dir: PNG });
  for (const t of Object.keys(EVENT_ICONS)) assert.ok(art.eventIcon(t) && existsSync(art.eventIcon(t).path), t);
  assert.equal(art.house('House Varrow').name, 'sigil-varrow.png');
  assert.equal(art.house('Thornwick'), null, 'a player-founded house has no drawn sigil');
  assert.equal(art.season(5).name, 'season-1.png', 'medals I to IV repeat');
  for (const [id] of TITLES) assert.ok(art.title(id), id);
  assert.ok(art.emblem());
  assert.equal(art.event({ type: 'title_earned', title: 'Wren is named Kingslayer' }).name, 'title-kingslayer.png');
  assert.equal(art.event({ type: 'season_started', title: 'Season 2 begins' }).name, 'season-2.png');
  assert.equal(art.event({ type: 'season_started', title: 'The Thaw begins' }).name, 'icon-dawn.png');
  assert.equal(createArt({ dir: PNG, enabled: false }).emblem(), null);
  assert.equal(createArt({ dir: join(PNG, 'nope') }).emblem(), null);
  assert.equal(greatHouse('house merrin'), 'merrin');
  assert.equal(titleIn('Odo is named the Renowned'), 'renowned');
  assert.equal(titleIn('Odo is named Lord of Nothing'), null);
});

test('decorate: author emblem and thumbnail as attachments, deduplicated, still within limits', () => {
  const art = createArt({ dir: PNG });
  const big = { title: 'T', description: 'x'.repeat(LIMITS.total - 10) };
  const out = decorate({ embeds: [big] }, { emblem: art.emblem(), thumb: art.emblem(), authorName: 'The Realm of Ostreval' });
  assert.equal(out.files.length, 1, 'one file for both uses');
  assert.equal(out.embeds[0].author.icon_url, 'attachment://realm-emblem.png');
  assert.equal(out.embeds[0].thumbnail.url, 'attachment://realm-emblem.png');
  assert.ok(embedSize(out.embeds[0]) <= LIMITS.total, 'the author name still fits the budget');
  const none = { embeds: [{ title: 'x' }] };
  assert.equal(decorate(none, {}), none);
  const plain = stripArt(out);
  assert.equal(plain.files, undefined);
  assert.equal(plain.embeds[0].thumbnail, undefined);
  assert.deepEqual(plain.embeds[0].author, { name: 'The Realm of Ostreval' });
});

function handlers(cfg) {
  const logger = quietLogger();
  const realmData = createRealmData({ dataDir: join(FIXTURES, 'data'), configDir: join(FIXTURES, 'config'), now: () => NOW });
  const store = createStateStore(cfg.stateFile, { logger });
  return createHandlers({ cfg, chronicle: fakeChronicle(), realmData, swearService: createSwearService({ cfg, store, now: () => NOW, logger }), logger, now: () => NOW });
}

test('/realm replies carry the emblem and a fitting picture', async () => {
  const h = handlers(artCfg());
  const reply = async (sub, opts) => {
    const i = fakeInteraction(sub, opts);
    await h.handle(i);
    return i.replies[0];
  };
  const king = await reply('king');
  assert.equal(king.embeds[0].thumbnail.url, 'attachment://sigil-varrow.png', 'the ruling great house');
  assert.equal(king.embeds[0].author.icon_url, 'attachment://realm-emblem.png');
  assert.deepEqual(king.files.map((f) => f.name).sort(), ['realm-emblem.png', 'sigil-varrow.png']);
  for (const f of king.files) assert.ok(existsSync(f.attachment));
  const chronicle = await reply('chronicle', { count: 3 });
  assert.equal(chronicle.embeds[0].thumbnail.url, 'attachment://icon-beacon.png', 'the newest entry is a realm event');
  const whois = await reply('whois', { house: 'Corvane' });
  assert.equal(whois.embeds[0].thumbnail.url, 'attachment://sigil-corvane.png');
  const events = await reply('events');
  assert.match(events.embeds[0].thumbnail.url, /^attachment:\/\/icon-beacon/);
  const join = await reply('join');
  assert.equal(join.embeds[0].thumbnail.url, 'attachment://realm-emblem.png');
  // Pictures off: no files at all.
  const off = handlers(testConfig({}, { REALM_ART_DIR: PNG, REALM_EMBED_ART: '0' }));
  const i = fakeInteraction('king');
  await off.handle(i);
  assert.equal(i.replies[0].files, undefined);
  assert.equal(i.replies[0].embeds[0].thumbnail, undefined);
});

test('status message: pictures attached, replaced on edit, dropped once if Discord refuses them', async () => {
  const cfg = artCfg();
  const logger = quietLogger();
  const channel = fakeChannel();
  const chronicle = fakeChronicle();
  const store = createStateStore(cfg.stateFile, { logger });
  const realmData = createRealmData({ dataDir: join(FIXTURES, 'data'), configDir: join(FIXTURES, 'config'), now: () => NOW });
  const t = { now: NOW };
  const board = createStatusBoard({ client: fakeClient(channel), cfg, chronicle, realmData, store, logger, now: () => t.now, timers: { setTimeout() {}, clearTimeout() {} } });
  assert.equal(await board.tick(), 'posted');
  assert.equal(channel.sent[0].embeds[0].thumbnail.url, 'attachment://sigil-varrow.png');
  assert.equal(channel.sent[0].files.length, 2);
  chronicle.stateValue = { ...chronicle.stateValue, online: 30 };
  assert.equal(await board.tick(), 'edited');
  assert.deepEqual(channel.edits[0].body.attachments, [], 'an edit replaces the old pictures');

  // A channel where the bot may not attach files: the message still goes out, without pictures.
  const ch2 = fakeChannel();
  let refused = 0;
  const send = ch2.send;
  ch2.send = async (body) => {
    if (body.files) { refused++; throw Object.assign(new Error('Missing Permissions'), { code: 50013 }); }
    return send(body);
  };
  const store2 = createStateStore(testConfig().stateFile, { logger });
  const board2 = createStatusBoard({ client: fakeClient(ch2), cfg, chronicle, realmData, store: store2, logger, now: () => t.now, timers: { setTimeout() {}, clearTimeout() {} } });
  assert.equal(await board2.tick(), 'posted');
  assert.equal(refused, 1);
  assert.equal(ch2.sent[0].files, undefined);
  assert.equal(ch2.sent[0].embeds[0].thumbnail, undefined);
  assert.equal(logger.lines.filter((l) => /Attach Files/.test(l)).length, 1);
  t.now += 11 * 60000;
  assert.equal(await board2.tick(), 'edited');
  assert.equal(refused, 1, 'no second try with pictures');
});
