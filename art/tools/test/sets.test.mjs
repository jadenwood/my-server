// node --test art/tools/test/   (no browser needed)
// The new sets and rules: one icon per Chronicle type, badges for every RealmRenown title, the key art, textures,
// the palette groups, the named-colour rule and the pen helpers.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { xmlBalanced, chronicleTypes, namedColourErrors, paletteHexes } from '../build.mjs';
import { rng, ridge, numeral, roman } from '../sets.mjs';
import { brush, blob, line } from '../pen.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const ART = path.resolve(here, '..', '..');
const REPO = path.resolve(ART, '..');
const readArt = (f) => fs.readFileSync(path.join(ART, f), 'utf8');
const ids = (svg) => [...svg.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]);

test('every Chronicle event type has its own icon, and the map names no unknown type', () => {
  const map = JSON.parse(readArt('icons/event-map.json')).icons;
  const types = chronicleTypes();
  assert.ok(types && types.length >= 42, 'chronicle/server.js EVENT_TYPES is readable');
  for (const t of types) assert.ok(map[t], `${t} has an icon`);
  for (const t of Object.keys(map)) assert.ok(types.includes(t), `${t} is a Chronicle type`);
  const icons = Object.values(map);
  assert.equal(new Set(icons).size, icons.length, 'no two event types share an icon');
  for (const i of icons) assert.ok(fs.existsSync(path.join(ART, 'icons', `${i}.svg`)), `${i}.svg exists`);
});

test('the event map agrees with the plugin\'s closed list of types', () => {
  const src = fs.readFileSync(path.join(REPO, 'plugins', 'RealmChronicle.cs'), 'utf8');
  const block = src.match(/KnownTypes\s*=\s*\{([\s\S]*?)\};/)[1].replace(/\/\/.*$/gm, '');
  const known = [...block.matchAll(/"([a-z_]+)"/g)].map((m) => m[1]);
  const map = JSON.parse(readArt('icons/event-map.json')).icons;
  assert.deepEqual(Object.keys(map).sort(), known.sort());
});

test('the sprite carries every icon as a symbol', () => {
  const sprite = readArt('sprite/icons.svg');
  for (const f of fs.readdirSync(path.join(ART, 'icons')).filter((x) => x.endsWith('.svg'))) {
    assert.ok(sprite.includes(`id="realm-icon-${f.replace('.svg', '')}"`), f);
  }
});

test('src/titles.json has a badge for every default title in RealmRenown.cs, with matching names', () => {
  const src = fs.readFileSync(path.join(REPO, 'plugins', 'RealmRenown.cs'), 'utf8');
  const body = src.match(/List<TitleDef> DefaultTitles\(\)\s*\{([\s\S]*?)return t;/)[1];
  const plugin = [...body.matchAll(/T\("([a-z_]+)",\s*"([^"]+)",\s*"[^"]*",\s*(true|false)/g)].map((m) => ({ id: m[1], name: m[2], infamous: m[3] === 'true' }));
  assert.ok(plugin.length >= 18, 'read the plugin titles');
  const titles = JSON.parse(readArt('src/titles.json')).titles;
  assert.deepEqual(titles.map(({ id, name, infamous }) => ({ id, name, infamous })), plugin);
  for (const t of titles) {
    const badge = `badges/titles/${t.id.replace(/_/g, '-')}.svg`;
    assert.ok(fs.existsSync(path.join(ART, badge)), badge);
    const svg = readArt(badge);
    assert.equal(xmlBalanced(svg), null, badge);
    const title = (svg.match(/<title>([^<]*)<\/title>/) || [])[1].replace(/&apos;|&#39;/g, "'").replace(/&amp;/g, '&');
    assert.ok(title.endsWith(t.name), `${badge} names the title`);
  }
});

test('season badges 1 to 4 exist, are well-formed and draw their numeral as paths', () => {
  for (let k = 1; k <= 4; k++) {
    const svg = readArt(`badges/seasons/season-${k}.svg`);
    assert.equal(xmlBalanced(svg), null);
    assert.ok(!/<text\b/.test(svg), 'no live text');
    assert.match(svg, new RegExp(`Season ${k}`));
  }
  assert.equal(roman(4), 'IV');
  assert.ok(numeral('III').w > numeral('I').w * 2.5);
  assert.match(numeral('IV').d, /^M[-\d.]+ 0H/);
});

test('key art comes in both sizes, with and without the logo, and resolves all its references', () => {
  for (const [f, w, h] of [['old-throne-1920', 1920, 1080], ['old-throne-1920-title', 1920, 1080], ['old-throne-1200', 1200, 630], ['old-throne-1200-title', 1200, 630]]) {
    const svg = readArt(`keyart/${f}.svg`);
    assert.match(svg, new RegExp(`viewBox="0 0 ${w} ${h}"`), f);
    const all = ids(svg);
    assert.equal(new Set(all).size, all.length, `${f}: ids are unique`);
    for (const m of svg.matchAll(/url\(#([^)]+)\)|href="#([^"]+)"/g)) assert.ok(all.includes(m[1] || m[2]), `${f}: #${m[1] || m[2]} resolves`);
  }
});

test('the scenery is deterministic: the same seed draws the same ridge', () => {
  const a = ridge([[0, 100], [500, 50], [1000, 100]], { seed: 9 });
  assert.equal(a, ridge([[0, 100], [500, 50], [1000, 100]], { seed: 9 }));
  assert.notEqual(a, ridge([[0, 100], [500, 50], [1000, 100]], { seed: 10 }));
  const r = rng(1);
  for (let i = 0; i < 100; i++) { const v = r(); assert.ok(v >= 0 && v < 1); }
});

test('textures are small tiles whose noise is stitched so they repeat', () => {
  for (const f of ['textures/parchment.svg', 'textures/iron.svg']) {
    const svg = readArt(f);
    assert.match(svg, /viewBox="0 0 512 512"/);
    assert.match(svg, /stitchTiles="stitch"/);
    assert.ok(Buffer.byteLength(svg) < 8000, `${f} is lightweight`);
  }
});

test('palette groups: scene and enamel colours are allowed, season enamels are palette colours', () => {
  const palette = JSON.parse(readArt('palette.json'));
  const hexes = new Set(paletteHexes());
  for (const g of ['scene', 'enamel']) for (const c of palette[g]) assert.ok(hexes.has(c.hex), `${g} ${c.hex}`);
  for (const e of palette.seasonEnamels) assert.ok(hexes.has(e), e);
  for (const h of Object.values(palette.houses)) for (const k of ['metalLight', 'metalShadow']) assert.match(h[k], /^#[0-9a-f]{6}$/);
});

test('named colours: only white and black, and only inside a mask', () => {
  assert.deepEqual(namedColourErrors('<mask id="m"><use fill="white"/><use fill="black"/></mask><path fill="none" stroke="currentColor"/>'), []);
  assert.equal(namedColourErrors('<path fill="white"/>').length, 1);
  assert.equal(namedColourErrors('<mask id="m"><path fill="red"/></mask>').length, 1);
  assert.equal(namedColourErrors('<stop stop-color="gold"/>').length, 1);
});

test('sigils keep their house-prefixed ids unique when banner and shield wrap them', () => {
  for (const h of Object.keys(JSON.parse(readArt('palette.json')).houses)) {
    for (const f of [`sigils/${h}.svg`, `banners/${h}.svg`, `shields/${h}.svg`]) {
      const all = ids(readArt(f));
      assert.equal(new Set(all).size, all.length, `${f}: duplicate ids`);
    }
  }
});

test('pen helpers make closed, well-formed path data', () => {
  const b = brush([[0, 0, 8], [40, 10, 4], [80, 0, 0]]);
  assert.match(b, /^M[-\d. ]+C.*A4 4 0 0 0 .*Z$/);
  assert.match(blob([[0, 0], [10, 0, 'c'], [10, 10], [0, 10]]), /^M.*Z$/);
  assert.match(line([[0, 0], [5, 5], [10, 0]]), /^M0 0C/);
  assert.equal(xmlBalanced(`<svg><path d="${b}"/></svg>`), null);
});
