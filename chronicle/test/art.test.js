// The art pack on /overlay and /realm: realm-art.js agrees with art/, the files it names are served,
// and the pages need nothing from the internet (fonts included).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { createApp, parseOptions, EVENT_TYPES } from '../server.js';
import { EVENT_ICONS, GREAT_HOUSES, TITLES, ART_BASE, greatHouse, titleOf, seasonNumber } from '../public/assets/realm-art.js';

const ART = new URL('../../art/', import.meta.url);
const SAMPLE = fileURLToPath(new URL('../sample-data', import.meta.url));
let server;
let base;

before(async () => {
  server = await new Promise((ok) => {
    const s = createServer(createApp(parseOptions(['--data', SAMPLE, '--stale', '0'], {}))).listen(0, '127.0.0.1', () => ok(s));
  });
  base = `http://127.0.0.1:${server.address().port}`;
});
after(() => server.close());

const json = async (rel) => JSON.parse(await readFile(new URL(rel, ART), 'utf8'));

test('realm-art.js matches the art pack: event icons, great houses, renown titles', async () => {
  assert.deepEqual(EVENT_ICONS, (await json('icons/event-map.json')).icons);
  assert.deepEqual([...EVENT_TYPES].sort(), Object.keys(EVENT_ICONS).sort(), 'every Chronicle type has its own icon');
  const palette = (await json('palette.json')).houses;
  assert.deepEqual(Object.keys(GREAT_HOUSES).sort(), Object.keys(palette).sort());
  for (const [k, h] of Object.entries(GREAT_HOUSES)) {
    for (const f of ['name', 'sigil', 'words', 'field', 'fieldLight', 'fieldDark', 'metal']) assert.equal(h[f], palette[k][f], `${k}.${f}`);
  }
  assert.deepEqual(TITLES.map((t) => [t.id, t.name, t.infamous]), (await json('src/titles.json')).titles.map((t) => [t.id, t.name, t.infamous]));
});

test('realm-art.js helpers', () => {
  assert.equal(greatHouse('House Corvane'), 'corvane');
  assert.equal(greatHouse('Thornwick'), null);
  assert.equal(greatHouse('toString'), null);
  assert.equal(titleOf('Wren is named Champion of the Lists').id, 'champion');
  assert.equal(titleOf('Wren is named the Renowned').id, 'renowned');
  assert.equal(titleOf('Wren is named nothing at all'), null);
  assert.equal(seasonNumber({ title: 'Season 3 begins' }), 3);
  assert.equal(seasonNumber({ title: 'The Thaw begins' }), null);
});

test('every art file the pages use is served, with the right type', async () => {
  const paths = ['icons.svg', 'logo/realm-emblem.svg', 'logo/realm-favicon.svg', 'keyart/old-throne-1920.svg', 'textures/parchment.svg'];
  for (const k of Object.keys(GREAT_HOUSES)) paths.push(`sigils/${k}.svg`, `banners/${k}.svg`, `shields/${k}.svg`);
  for (const n of [1, 2, 3, 4]) paths.push(`badges/season-${n}.svg`);
  for (const t of TITLES) paths.push(t.file);
  for (const p of paths) {
    const r = await fetch(`${base}${ART_BASE}${p}`);
    assert.equal(r.status, 200, p);
    assert.equal(r.headers.get('content-type'), 'image/svg+xml', p);
  }
  const sprite = await (await fetch(`${base}${ART_BASE}icons.svg`)).text();
  for (const name of Object.values(EVENT_ICONS)) assert.ok(sprite.includes(`id="realm-icon-${name}"`), name);
  const font = await fetch(`${base}/assets/fonts/cinzel.woff2`);
  assert.equal(font.status, 200);
  assert.equal(font.headers.get('content-type'), 'font/woff2');
});

test('the pages work offline: no third-party fonts or scripts, and the CSP says so', async () => {
  for (const p of ['/overlay', '/realm', '/assets/theme.css', '/assets/overlay.css', '/assets/realm.css']) {
    const r = await fetch(`${base}${p}`);
    const body = await r.text();
    assert.ok(!/https?:\/\//.test(body.replace(/http:\/\/www\.w3\.org\/2000\/svg/g, '')), `${p} names no other site`);
    assert.match(r.headers.get('content-security-policy'), /font-src 'self'/);
    assert.ok(!/googleapis|gstatic/.test(r.headers.get('content-security-policy')));
  }
});
