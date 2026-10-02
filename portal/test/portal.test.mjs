// Portal generator tests. Run: npm test (from portal/). No network, writes only to a temp folder.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, mkdir, rm, readdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';

import { sanitizeEvent, sanitizeState, sanitizeHousesData, sanitizeCrownData, sanitizeSeasons, sanitizeLegends, scheduleOccurrences, sanitizeEventsData, readRealmData, pick } from '../lib/data.mjs';
import { render, inline } from '../lib/markdown.mjs';
import { buildReigns, buildUpcoming, housesOfEvent, parseHouseLore, buildModel } from '../lib/model.mjs';
import { buildSite, normalizeConfig, docLink } from '../lib/generate.mjs';
import { dyeFor, esc, slug, safeUrl } from '../lib/util.mjs';
import { parseArgs } from '../build.mjs';

const REPO = fileURLToPath(new URL('../../', import.meta.url));
const NOW = Date.parse('2026-10-01T21:08:00Z');
const tmp = () => mkdtemp(join(tmpdir(), 'portal-test-'));

// ---------- data readers

test('events: whitelisted fields, unknown-but-well-formed types kept, junk dropped', () => {
  const e = sanitizeEvent({ id: 3, ts: '2026-09-30T19:00:00', type: 'law_proclaimed', title: 'T', detail: 'D', actors: ['A', 7, '76561190000000003'], steamId: 'x', pos: [1, 2] });
  assert.deepEqual(e, { id: 3, ts: '2026-09-30T19:00:00Z', type: 'law_proclaimed', title: 'T', detail: 'D', actors: ['A'] });
  assert.equal(sanitizeEvent({ id: 1, type: '<script>' }), null);
  assert.equal(sanitizeEvent({ id: '1', type: 'decree' }), null);
  assert.equal(sanitizeEvent(null), null);
});

test('state: tolerant of missing and wrong-typed fields', () => {
  const s = sanitizeState({ king: 5, houses: [{ name: '' }, { name: 'Varrow', members: 'x' }, null], online: -3 });
  assert.equal(s.king, null);
  assert.equal(s.houses.length, 1);
  assert.equal(s.houses[0].members, 0);
  assert.equal(s.online, 0);
  assert.deepEqual(sanitizeState(undefined).houses, []);
});

test('RealmHouses: member ids never leave the reader; id-like names are dropped', () => {
  const raw = JSON.parse(readFileSync(join(REPO, 'portal/sample-data/RealmHouses.json'), 'utf8'));
  const out = sanitizeHousesData(raw);
  const json = JSON.stringify(out);
  assert.ok(!/7656119\d{10}/.test(json), 'no Steam ids in sanitized house data');
  const varrow = out.houses.find((h) => h.name === 'Varrow');
  assert.equal(varrow.leader, 'Aldric Varrow');
  assert.deepEqual(varrow.officers, ['Odo the Tall']);
  assert.equal(varrow.members, 3);
  assert.equal(varrow.swornSince, null, '.NET MinValue is not a date');
  assert.equal(out.treaties.length, 2);
});

test('Crown data: claims, council names; ids dropped', () => {
  const raw = JSON.parse(readFileSync(join(REPO, 'portal/sample-data/CrownAndConsequences.json'), 'utf8'));
  const c = sanitizeCrownData(raw);
  assert.equal(c.claims[0].status, 'pending');
  assert.equal(c.claims[0].windowStart, '2026-10-10T19:00:00Z');
  assert.ok(!/7656119/.test(JSON.stringify(c)));
  assert.deepEqual(c.council.map((x) => x.seat), ['Voice of the Crown', 'Marshal']);
});

test('Seasons: the RealmSeasons plugin shape gives the current season and scored standings', () => {
  const raw = JSON.parse(readFileSync(join(REPO, 'portal/sample-data/RealmSeasons.json'), 'utf8'));
  const legends = JSON.parse(readFileSync(join(REPO, 'portal/sample-data/RealmLegends.json'), 'utf8'));
  const s = sanitizeSeasons(raw, NOW, legends);
  assert.equal(s.current.number, 1);
  assert.equal(s.current.name, 'The Season of the Thaw');
  assert.deepEqual(s.standings.map((x) => x.house), ['Corvane', 'Varrow', 'Ashgrove', 'Dunmere', 'Halloran']);
  // Corvane: 180551 s crown = 2.09 days * 10 + 1 defended * 15 + 1 contract * 2 = 37.9 -> 38 (default weights)
  assert.equal(s.standings[0].score, 38);
  assert.equal(s.standings[0].rank, 1);
  assert.deepEqual(s.standings[1].honours, ['Royal Tournament champion: Odo the Tall']);
  const ended = sanitizeSeasons({ Number: 1, Active: false, Houses: {} }, NOW, { Seasons: [{ Number: 1, Name: 'Season 1', Start: '2026-08-01T00:00:00Z', End: '2026-09-01T00:00:00Z', Champion: 'Varrow', ChampionScore: 120, LongestReign: 'Aldric of Varrow (9 days)', Top: [{ Rank: 1, House: 'Varrow', Score: 120 }] }] });
  assert.equal(ended.current, null);
  assert.equal(ended.past[0].championHouse, 'Varrow');
  assert.equal(ended.past[0].championScore, 120);
  assert.equal(ended.past[0].top[0].house, 'Varrow');
});

test('Legends: the Hall of Kings across wipes', () => {
  const l = sanitizeLegends({ Current: { Monarch: 'Aldric', House: 'Varrow', Start: '2026-09-30T19:42:10Z' }, Hall: [{ Monarch: 'Ysolde', House: 'Corvane', Start: '2026-09-28T17:31:40Z', End: '2026-09-30T19:40:51Z', Ending: 'overthrown by rebellion', Season: 1 }, { Monarch: '76561190000000003', Start: '2026-09-01T00:00:00Z' }, { Start: 'x' }] });
  assert.equal(l.hall.length, 1, 'id-like or incomplete reigns dropped');
  assert.equal(l.current.king, 'Aldric');
  const reigns = buildReigns([{ id: 1, ts: '2026-09-29T00:00:00Z', type: 'decree', title: '', detail: '', actors: [] }], { king: 'Aldric', house: 'Varrow', since: '2026-09-30T19:42:10Z' }, NOW, l);
  assert.deepEqual(reigns.map((r) => [r.king, r.endedBy, r.current, r.decrees]), [['Ysolde', 'overthrown', false, 1], ['Aldric', null, true, 0]]);
  assert.equal(reigns[0].endingText, 'overthrown by rebellion');
  // RealmState already shows a newer king than the legends file: the old reign closes.
  const newer = buildReigns([], { king: 'Wren', house: 'Ashgrove', since: '2026-10-01T20:00:00Z' }, NOW, l);
  assert.deepEqual(newer.map((r) => [r.king, r.current]), [['Ysolde', false], ['Aldric', false], ['Wren', true]]);
});

test('RealmEvents: weekly schedule from the plugin config, disabled slots skipped, live events kept', () => {
  const cfg = JSON.parse(readFileSync(join(REPO, 'portal/sample-data/config/RealmEvents.json'), 'utf8'));
  const occ = scheduleOccurrences(cfg, NOW, 14); // NOW is Thursday 1 Oct 2026, 21:08 UTC
  assert.equal(occ[0].title, 'Royal Tournament');
  assert.equal(occ[0].at, '2026-10-02T19:00:00Z');
  assert.equal(occ[1].title, 'Crown Night');
  assert.equal(occ[1].end, '2026-10-03T20:30:00Z');
  assert.ok(!occ.some((o) => o.kind === 'truce'), 'disabled slot skipped');
  assert.ok(scheduleOccurrences({ EnableTournament: false }, NOW).every((o) => o.kind !== 'tournament'), 'Enable* flag honoured with the default schedule');
  assert.ok(scheduleOccurrences({}, NOW).some((o) => o.kind === 'truce'), 'defaults when Schedule is missing');
  assert.deepEqual(scheduleOccurrences({ Schedule: [{ Event: 'crown_night', Days: ['Daily'], StartUtc: '25:00' }] }, NOW), []);
  assert.equal(scheduleOccurrences({ Schedule: [{ Event: 'truce', Days: ['Weekends'], StartUtc: '12:00', DurationMinutes: 60 }] }, NOW)[0].at, '2026-10-03T12:00:00Z');
  const live = sanitizeEventsData({ Active: [{ Kind: 'tournament', Start: '2026-10-01T21:00:00Z', End: '2026-10-01T22:00:00Z', Entrants: { '76561190000000003': {} } }] });
  assert.deepEqual(live, { active: [{ kind: 'tournament', start: '2026-10-01T21:00:00Z', end: '2026-10-01T22:00:00Z' }] });
  const up = buildUpcoming({ crown: { claims: [] }, seasons: { current: null, upcoming: [] }, housesData: { treaties: [] }, realmEvents: { active: live.active, schedule: occ }, now: NOW });
  assert.equal(up[0].title, 'Royal Tournament');
  assert.equal(up[0].live, true);
  assert.equal(up.filter((u) => u.title === 'Royal Tournament').length, 1, 'one entry per kind');
});

test('Seasons (other shapes): several layouts are understood', () => {
  const a = sanitizeSeasons({ current: { number: 2, name: 'Iron Winter', endsAt: '2026-12-01T00:00:00Z' }, history: [{ Number: 1, Name: 'Thaw', StartedAt: '2026-08-01T00:00:00Z', EndedAt: '2026-09-01T00:00:00Z', Winner: 'Wren', Champion: 'Ashgrove' }] }, NOW);
  assert.equal(a.current.name, 'Iron Winter');
  assert.equal(a.past[0].champion, 'Wren');
  assert.equal(a.past[0].championHouse, 'Ashgrove');
  const b = sanitizeSeasons([{ season: 1, start: '2026-08-01T00:00:00Z', end: '2026-09-01T00:00:00Z' }, { season: 2, title: 'Second' }], NOW);
  assert.equal(b.current.name, 'Second');
  assert.equal(b.past[0].name, 'Season 1');
  const c = sanitizeSeasons({ season_schedule: 1, Schedule: [{ Title: 'Crown Night', At: '2026-10-17T19:00:00Z' }, { Title: 'no date' }] }, NOW);
  assert.equal(c.upcoming.length, 1);
  assert.deepEqual(sanitizeSeasons('nonsense', NOW), { current: null, past: [], upcoming: [], standings: [] });
  assert.equal(pick({ started_at: 1 }, 'StartedAt'), 1);
});

test('readRealmData: missing files are warnings, unreadable files too, never a throw', async () => {
  const dir = await tmp();
  await writeFile(join(dir, 'RealmState.json'), '{ not json');
  const d = await readRealmData({ dataDirs: [dir], now: NOW });
  assert.deepEqual(d.events, []);
  assert.ok(d.warnings.some((w) => /RealmState\.json: unreadable/.test(w)));
  assert.ok(d.warnings.some((w) => /RealmChronicle\.json not found/.test(w)));
  await rm(dir, { recursive: true, force: true });
});

test('readRealmData: BOM, duplicate ids and order are handled', async () => {
  const dir = await tmp();
  await writeFile(join(dir, 'RealmChronicle.json'), '﻿' + JSON.stringify([{ id: 2, type: 'decree', title: 'b' }, { id: 1, type: 'decree', title: 'a' }, { id: 2, type: 'decree', title: 'dup' }]));
  const d = await readRealmData({ dataDirs: [dir], now: NOW });
  assert.deepEqual(d.events.map((e) => e.title), ['a', 'b']);
  await rm(dir, { recursive: true, force: true });
});

// ---------- markdown

test('markdown: escapes raw HTML and drops unsafe links', () => {
  const { html } = render('# Hi <script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n[bad](javascript:alert(1)) [ok](https://example.org/x?a=1&b=2) [doc](rules.md#5-streams)', { linkFor: docLink });
  assert.ok(!html.includes('<script>'));
  assert.ok(!html.includes('<img'));
  assert.ok(!html.includes('javascript:'));
  assert.ok(html.includes('href="https://example.org/x?a=1&amp;b=2"'));
  assert.ok(html.includes('href="rules.html#5-streams"'));
});

test('markdown: tables, lists, code, quotes and GitHub-style anchors', () => {
  const md = '## 5. Streams, sniping and ghosting\n\n| A | B |\n|---|---|\n| `x|y` | **b** |\n\n- one\n  - nested\n- two\n\n1. first\n2. second\n\n```\n<raw>\n```\n\n> quoted *text*';
  const { html, headings } = render(md);
  assert.equal(headings[0].id, '5-streams-sniping-and-ghosting');
  assert.match(html, /<table>.*<th>A<\/th>.*<strong>b<\/strong>/s);
  assert.match(html, /<ul><li>one<ul><li>nested<\/li><\/ul><\/li><li>two<\/li><\/ul>/);
  assert.match(html, /<ol><li>first<\/li><li>second<\/li><\/ol>/);
  assert.match(html, /<pre class="code"><code>&lt;raw&gt;<\/code><\/pre>/);
  assert.match(html, /<blockquote><p>quoted <em>text<\/em><\/p><\/blockquote>/);
  assert.equal(inline('snake_case and `a_b`'), 'snake_case and <code>a_b</code>');
});

test('doc links map onto portal pages; repo paths become plain text', () => {
  assert.equal(docLink('lore.md'), 'lore.html');
  assert.equal(docLink('how-to-play.md#2-oaths'), 'play.html#2-oaths');
  assert.equal(docLink('launch-plan.md'), null);
  assert.equal(docLink('../../plugins/RealmHouses.cs'), null);
  assert.equal(docLink('http://insecure.example'), null);
});

// ---------- model

test('reigns are rebuilt from coronations and abdications', () => {
  const ev = [
    { id: 1, ts: '2026-09-28T17:00:00Z', type: 'coronation', title: 'Ysolde Corvane of Corvane takes the throne', detail: '', actors: ['Ysolde Corvane'] },
    { id: 2, ts: '2026-09-28T18:00:00Z', type: 'decree', title: '', detail: '', actors: [] },
    { id: 3, ts: '2026-09-29T17:00:00Z', type: 'abdication', title: '', detail: '', actors: [] },
    { id: 4, ts: '2026-09-30T17:00:00Z', type: 'coronation', title: 'Aldric Varrow takes the throne', detail: 'House Varrow now holds the crown.', actors: ['Aldric Varrow'] },
    { id: 5, ts: '2026-10-01T17:00:00Z', type: 'coronation', title: 'Wren of no house takes the throne', detail: '', actors: ['Wren'] },
  ];
  const r = buildReigns(ev, { king: 'Wren', house: null, since: '2026-10-01T17:00:00Z', updated: '2026-10-01T21:00:00Z' }, NOW);
  assert.deepEqual(r.map((x) => [x.king, x.house, x.endedBy, x.current]), [
    ['Ysolde Corvane', 'Corvane', 'abdicated', false],
    ['Aldric Varrow', 'Varrow', 'succeeded', false],
    ['Wren', null, null, true],
  ]);
  assert.equal(r[0].decrees, 1);
  assert.equal(r[0].ms, 24 * 3600 * 1000);
});

test('reigns: a king in RealmState but trimmed from the log still appears', () => {
  const r = buildReigns([], { king: 'Odo', house: 'Varrow', since: '2026-10-01T00:00:00Z' }, NOW);
  assert.equal(r.length, 1);
  assert.equal(r[0].current, true);
});

test('house mentions: longest name wins, roster actors count', () => {
  const names = ['Grey', 'Grey Water', 'Varrow'];
  const e = { title: 'House Grey Water swears to House Varrow', detail: '', actors: ['Bramwell'] };
  assert.deepEqual(housesOfEvent(e, names, new Map([['bramwell', 'Grey']])), ['Grey Water', 'Varrow', 'Grey']);
  assert.deepEqual(housesOfEvent({ title: 'House Greyish rises', detail: '', actors: [] }, names), []);
});

test('upcoming: future only, sorted, de-duplicated, config events validated', () => {
  const up = buildUpcoming({
    crown: { claims: [{ house: 'Corvane', against: 'Varrow', status: 'pending', windowStart: '2026-10-10T19:00:00Z', windowEnd: '2026-10-10T20:30:00Z' }, { house: 'Old', status: 'ended', windowStart: '2026-09-01T00:00:00Z' }] },
    seasons: { current: null, upcoming: [{ title: 'Crown Night', at: '2026-10-05T19:00:00Z' }] },
    housesData: { treaties: [{ a: 'A', b: 'B', expires: '2026-09-01T00:00:00Z' }] },
    configEvents: [{ title: 'Crown Night', at: '2026-10-05T19:00:00Z' }, { title: 'bad', at: 'never' }, 'junk'],
    now: NOW,
  });
  assert.deepEqual(up.map((u) => u.title), ['Crown Night', 'Rebellion window: House Corvane']);
});

test('lore parser reads the six great houses from docs/community/lore.md', () => {
  const lore = parseHouseLore(readFileSync(join(REPO, 'docs/community/lore.md'), 'utf8'));
  assert.ok(lore.varrow, 'House Varrow lore found');
  assert.match(lore.varrow.words, /Stand Our Ground/);
  assert.ok(Object.keys(lore).length >= 6);
});

test('house colour matches the overlay palette (chronicle/public/assets/common.js)', () => {
  const src = readFileSync(join(REPO, 'chronicle/public/assets/common.js'), 'utf8');
  const dyes = JSON.parse(src.match(/const DYES = (\[[^\]]+\])/)[1].replace(/'/g, '"'));
  assert.equal(dyeFor('Varrow'), '#4a2347', 'lore.md says Varrow is plum #4a2347');
  assert.ok(dyes.includes(dyeFor('Ashgrove')));
});

test('house colour matches the Discord relay (launcher/lib/discord.js)', () => {
  const require = createRequire(import.meta.url);
  let D;
  try {
    D = require(join(REPO, 'launcher/lib/discord.js'));
  } catch {
    return; // launcher not present in this checkout
  }
  for (const n of ['Varrow', 'Ashgrove', 'Corvane', 'Dunmere', 'Halloran', 'Merrin', 'Grey Water']) {
    assert.equal('#' + D.houseColour(n).toString(16).padStart(6, '0'), dyeFor(n), n);
  }
});

// ---------- config

test('config: placeholders, non-https and bad values are dropped', () => {
  const s = normalizeConfig({
    realmName: '  ',
    download: { url: 'https://example.invalid/Realm-Setup.exe', sha256: 'nothex' },
    discordInvite: 'http://discord.gg/x',
    server: { address: '127.0.0.1:7350', port: 99999 },
    streamers: [{ name: 'A', url: 'javascript:alert(1)' }, { name: 'B', url: 'https://twitch.realm.test/b' }],
  }, NOW);
  assert.equal(s.realmName, 'The Realm');
  assert.equal(s.download.url, '');
  assert.equal(s.download.sha256, '');
  assert.equal(s.discordInvite, '');
  assert.equal(s.server.address, '', 'host:port in the address box is the owner\'s real failure');
  assert.equal(s.server.port, 7350);
  assert.deepEqual(s.streamers.map((x) => x.name), ['B']);
});

test('util: escaping, slugs, safe urls', () => {
  assert.equal(esc('<a href="x">\'&'), '&lt;a href=&quot;x&quot;&gt;&#39;&amp;');
  assert.equal(slug('Grey Water'), 'grey-water');
  assert.equal(slug('../..'), 'house');
  assert.equal(safeUrl('javascript:alert(1)'), null);
  assert.equal(safeUrl('https://user:pw@x.test/'), null);
});

test('CLI args', () => {
  const o = parseArgs(['--data', 'a', '--data=b', '--config', 'c.json', '--out', 'o', '--watch']);
  assert.deepEqual(o.dataDirs, ['a', 'b']);
  assert.equal(o.watch, 60);
  assert.throws(() => parseArgs(['--nope']));
});

// ---------- full build

test('full build from sample data: every page, feeds, no ids, no inline scripts', async () => {
  const dir = await tmp();
  const out = join(dir, 'site');
  const r = await buildSite({ outDir: out, now: NOW, config: { download: { url: 'https://downloads.realm.test/Realm-Setup.exe' } } });
  const files = await readdir(out);
  for (const f of ['index.html', 'chronicle.html', 'houses.html', 'kings.html', 'play.html', 'rules.html', 'lore.html', 'streamers.html', 'download.html', '404.html', 'status.json', 'feed.xml', '.realm-portal', '.nojekyll']) {
    assert.ok(files.includes(f), `${f} built`);
  }
  assert.deepEqual((await readdir(join(out, 'houses'))).sort(), ['ashgrove.html', 'corvane.html', 'dunmere.html', 'halloran.html', 'merrin.html', 'varrow.html']);
  const status = JSON.parse(await readFile(join(out, 'status.json'), 'utf8'));
  assert.equal(status.king, 'Aldric Varrow');
  assert.equal(status.stale, false);
  assert.equal(status.lastEvent.id, 18);
  for (const [rel, body] of r.files) {
    if (typeof body !== 'string') continue;
    assert.ok(!/7656119\d{10}/.test(body), `${rel} leaks no Steam id`);
    if (rel.endsWith('.html')) {
      assert.ok(!/<script(?![^>]*\ssrc=)/.test(body), `${rel} has no inline script`);
      assert.ok(!/\sstyle="/.test(body), `${rel} has no style attribute (CSP)`);
      assert.ok(!/\son[a-z]+="/.test(body), `${rel} has no inline handlers`);
    }
  }
  const home = await readFile(join(out, 'index.html'), 'utf8');
  assert.match(home, /Aldric Varrow/);
  assert.match(home, /Rebellion window: House Corvane/);
  assert.match(await readFile(join(out, 'download.html'), 'utf8'), /href="https:\/\/downloads\.realm\.test\/Realm-Setup\.exe"/);
  const feed = await readFile(join(out, 'feed.xml'), 'utf8');
  assert.match(feed, /<feed xmlns="http:\/\/www\.w3\.org\/2005\/Atom">/);
  assert.equal((feed.match(/<entry>/g) || []).length, 18);
  const houses = await readFile(join(out, 'houses.html'), 'utf8');
  assert.match(houses, /The Season of the Thaw standings/);
  const kings = await readFile(join(out, 'kings.html'), 'utf8');
  assert.match(kings, /fell while holding the crown/);
  assert.match(home, /Crown Night/, 'RealmEvents schedule reaches the home page');
  const css = await readFile(join(out, 'assets/houses.css'), 'utf8');
  assert.match(css, /\.dye-varrow\{--dye:#4a2347/);
  await rm(dir, { recursive: true, force: true });
});

test('build with no data at all still produces a site', async () => {
  const dir = await tmp();
  const empty = join(dir, 'empty');
  await mkdir(empty);
  const r = await buildSite({ outDir: join(dir, 'site'), dataDirs: [empty], now: NOW, config: {} });
  assert.equal(r.model.events.length, 0);
  const home = await readFile(join(dir, 'site', 'index.html'), 'utf8');
  assert.match(home, /The throne stands empty/);
  assert.match(await readFile(join(dir, 'site', 'download.html'), 'utf8'), /Download coming soon/);
  await rm(dir, { recursive: true, force: true });
});

test('hostile data is escaped everywhere', async () => {
  const dir = await tmp();
  const data = join(dir, 'data');
  await mkdir(data);
  const evil = '<img src=x onerror=alert(1)>"\'';
  await writeFile(join(data, 'RealmChronicle.json'), JSON.stringify([{ id: 1, ts: '2026-10-01T00:00:00Z', type: 'house_founded', title: `House ${evil} is founded`, detail: evil, actors: [evil] }]));
  await writeFile(join(data, 'RealmState.json'), JSON.stringify({ king: evil, house: evil, since: '2026-10-01T00:00:00Z', houses: [{ name: evil, sigil: evil, members: 1 }], updated: '2026-10-01T21:00:00Z' }));
  const r = await buildSite({ outDir: join(dir, 'site'), dataDirs: [data], now: NOW, config: {} });
  for (const [rel, body] of r.files) {
    // status.json is data (read with textContent), so only markup and feeds are checked.
    if (typeof body === 'string' && !rel.endsWith('.json')) assert.ok(!body.includes('<img src=x'), `${rel} escapes hostile text`);
  }
  await rm(dir, { recursive: true, force: true });
});

test('refuses to replace a non-empty folder it did not create', async () => {
  const dir = await tmp();
  const out = join(dir, 'precious');
  await mkdir(out);
  await writeFile(join(out, 'keep.txt'), 'mine');
  await assert.rejects(buildSite({ outDir: out, now: NOW, config: {} }), /Refusing to replace/);
  assert.equal(await readFile(join(out, 'keep.txt'), 'utf8'), 'mine');
  assert.deepEqual((await readdir(dir)).sort(), ['precious'], 'no staging folder left behind');
  // A folder made by the generator is replaced fine, twice.
  const site = join(dir, 'site');
  await buildSite({ outDir: site, now: NOW, config: {} });
  await buildSite({ outDir: site, now: NOW, config: {} });
  assert.ok((await readdir(site)).includes('index.html'));
  await rm(dir, { recursive: true, force: true });
});

test('model: Steam-id-like actor names are not shown', () => {
  const m = buildModel({
    events: [sanitizeEvent({ id: 1, type: 'decree', title: 't', actors: ['76561190000000003', 'Wren'] })],
    state: sanitizeState({}), housesData: { houses: [], treaties: [] }, crown: sanitizeCrownData(null), seasons: sanitizeSeasons(null), typeMeta: {}, warnings: [],
  }, { now: NOW });
  assert.deepEqual(m.events[0].actors, ['Wren']);
});
