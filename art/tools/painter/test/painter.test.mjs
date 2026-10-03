// Tests for the Realm painter: the PNG codec, atlas packing and the self-check (no browser needed).
// Run: node --test art/tools/painter/test/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import zlib from 'node:zlib';
import { encodePng, decodePng, crc32, downscale } from '../png.mjs';
import { check, packAtlas, pack, BUNDLE_NAME } from '../paint.mjs';
import { OUT, FACES, CHARSET, DISPLAY_CHARSET, HOUSES, paintings, sprites, LIMITS, boardPalette, charsetOf } from '../designs.mjs';

function noise(n, seed) {
  const b = Buffer.alloc(n);
  let s = seed >>> 0;
  for (let i = 0; i < n; i++) { s = (s * 1664525 + 1013904223) >>> 0; b[i] = s >>> 24; }
  return b;
}

test('crc32 matches the PNG specification value for IEND', () => {
  assert.equal(crc32(Buffer.from('IEND')), 0xae426082);
});

test('every channel layout round-trips through encode and decode', () => {
  for (const channels of [1, 2, 3, 4]) {
    for (const [w, h] of [[1, 1], [3, 2], [17, 9], [64, 40]]) {
      const data = noise(w * h * channels, w * 31 + h + channels);
      const img = decodePng(encodePng({ width: w, height: h, data, channels }));
      assert.equal(img.width, w); assert.equal(img.height, h); assert.equal(img.channels, channels);
      assert.deepEqual(img.data, data, `${channels}ch ${w}x${h}`);
      assert.equal(img.rgba.length, w * h * 4);
    }
  }
});

test('all five row filters are chosen somewhere and decode exactly', () => {
  // gradients favour Sub/Up/Average/Paeth, noise favours None
  const w = 48, h = 48, data = Buffer.alloc(w * h * 4);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = (y * w + x) * 4;
    const band = Math.floor(y / 12);
    data[i] = band === 0 ? (x * 5) & 255 : band === 1 ? (y * 7) & 255 : band === 2 ? ((x + y) * 3) & 255 : noise(1, x * 977 + y)[0];
    data[i + 1] = (x * y) & 255; data[i + 2] = band * 60; data[i + 3] = 255 - x;
  }
  const png = encodePng({ width: w, height: h, data, channels: 4 });
  const idat = png.subarray(png.indexOf('IDAT') + 4, png.indexOf('IEND') - 8);
  const raw = zlib.inflateSync(idat);
  const filters = new Set();
  for (let y = 0; y < h; y++) filters.add(raw[y * (w * 4 + 1)]);
  assert.ok(filters.size >= 3, `only filters ${[...filters]}`);
  assert.deepEqual(decodePng(png).data, data);
});

test('the decoder refuses damaged or unsupported files', () => {
  const png = encodePng({ width: 4, height: 4, data: noise(64, 1), channels: 4 });
  assert.throws(() => decodePng(Buffer.from('nope')), /signature/);
  const bad = Buffer.from(png); bad[bad.indexOf('IDAT') + 6] ^= 0xff;
  assert.throws(() => decodePng(bad), /CRC/);
  assert.throws(() => decodePng(png.subarray(0, png.length - 12)), /IEND/);
  const deep = Buffer.from(png); deep[24] = 16;  // bit depth 16
  // fix the IHDR CRC so the depth check is what fails
  deep.writeUInt32BE(crc32(deep, 12, 29), 29);
  assert.throws(() => decodePng(deep), /bit depth/);
  assert.throws(() => encodePng({ width: 2, height: 2, data: Buffer.alloc(3), channels: 4 }), /size/);
});

test('pack stores opaque art as RGB, masks as grey and keeps transparency', () => {
  const opaque = Buffer.alloc(4 * 4 * 4, 255);
  assert.equal(decodePng(pack(opaque, 4, 4, 'rgba')).channels, 3);
  const clear = Buffer.from(opaque); clear[3] = 0;
  assert.equal(decodePng(pack(clear, 4, 4, 'rgba')).channels, 4);
  const mask = decodePng(pack(clear, 4, 4, 'mask'));
  assert.equal(mask.channels, 1); assert.equal(mask.data[0], 0); assert.equal(mask.data[1], 255);
});

test('downscale averages with premultiplied alpha', () => {
  const rgba = Buffer.from([255, 0, 0, 255, 0, 0, 255, 0, 255, 0, 0, 255, 0, 0, 255, 0]);
  const d = downscale(rgba, 2, 2, 2);
  assert.deepEqual([...d.data], [255, 0, 0, 128]);
});

test('packAtlas places every glyph without overlap and records metrics', () => {
  const glyphs = [];
  for (let i = 0; i < 60; i++) {
    const w = 3 + (i % 9), h = 4 + (i % 7);
    glyphs.push({ cp: 65 + i, adv: w + 0.5, w, h, left: -1, top: -h, alpha: Array.from({ length: w * h }, () => 200) });
  }
  glyphs.push({ cp: 32, adv: 4.25, w: 0, h: 0, left: 0, top: 0, alpha: [] });
  const a = packAtlas(glyphs, 64);
  assert.equal(a.records.length, glyphs.length);
  const used = new Uint8Array(a.width * a.height);
  for (const [cp, x, y, w, h, left, top, adv] of a.records) {
    const g = glyphs.find((q) => q.cp === cp);
    assert.equal(w, g.w); assert.equal(h, g.h); assert.equal(left, g.left); assert.equal(top, g.top);
    assert.equal(adv, Math.round(g.adv * 64));
    for (let yy = 0; yy < h; yy++) for (let xx = 0; xx < w; xx++) {
      assert.ok(x + xx < a.width && y + yy < a.height);
      assert.equal(used[(y + yy) * a.width + x + xx], 0, 'overlap');
      used[(y + yy) * a.width + x + xx] = 1;
      assert.equal(a.data[(y + yy) * a.width + x + xx], 200);
    }
  }
  assert.throws(() => packAtlas([{ cp: 1, adv: 1, w: 70, h: 2, left: 0, top: 0, alpha: [] }], 64), /wider/);
});

test('the designs cover every house, stay inside the size caps and use unique ids', () => {
  const ids = new Set();
  for (const p of [...paintings(), ...sprites()]) {
    assert.ok(!ids.has(p.id), p.id); ids.add(p.id);
    assert.ok(Math.max(p.width, p.height) <= LIMITS.maxSide, p.id);
  }
  for (const h of HOUSES) for (const g of ['sigil', 'banner', 'crest']) assert.ok(ids.has(`${g}-${h}`));
  assert.ok(CHARSET.includes(0xe9) && CHARSET.includes(0x2019));
  assert.equal(new Set(FACES.map((f) => f.id)).size, FACES.length);
  for (const h of HOUSES) assert.ok(ids.has(`sigil-32-${h}`) && ids.has(`sigil-96-${h}`), h);
});

test('the built paintings pass the self-check', () => {
  const r = check();
  assert.deepEqual(r.errors, []);
  assert.ok(r.stats.paintings >= 25 && r.stats.fonts === FACES.length);
});

test('the self-check catches a changed file, a stale bundle and a missing licence', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'painter-'));
  try {
    fs.cpSync(OUT, tmp, { recursive: true });
    assert.deepEqual(check(tmp).errors, []);
    // a painting edited by hand
    const f = path.join(tmp, 'sigils', 'sigil-varrow.png');
    const img = decodePng(fs.readFileSync(f));
    img.rgba[4000] ^= 0x40;
    fs.writeFileSync(f, encodePng({ width: img.width, height: img.height, data: img.rgba, channels: 4 }));
    // a bundle item swapped for another
    const bundle = JSON.parse(fs.readFileSync(path.join(tmp, BUNDLE_NAME), 'utf8'));
    bundle.Items.find((i) => i.Id === 'banner-merrin').Png = bundle.Items.find((i) => i.Id === 'banner-varrow').Png;
    const body = bundle.Fonts.find((f) => f.Id === 'body');
    body.Glyphs = body.Glyphs.filter((g) => g[0] !== 0xe9);
    bundle.Palette.Brand.ink = '#000000';
    fs.writeFileSync(path.join(tmp, BUNDLE_NAME), JSON.stringify(bundle));
    fs.rmSync(path.join(tmp, 'fonts', 'OFL-Cinzel.txt'));
    const errs = check(tmp).errors.join('\n');
    assert.match(errs, /sigil-varrow\.png changed since the build/);
    assert.match(errs, /banner-merrin: bundle copy differs/);
    assert.match(errs, /font body lacks U\+00e9/);
    assert.match(errs, /Palette differs/);
    assert.match(errs, /OFL-Cinzel\.txt/);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

test('the display face is capitals only and every other face covers the full charset', () => {
  assert.ok(!DISPLAY_CHARSET.includes(0x61) && DISPLAY_CHARSET.includes(0x41) && DISPLAY_CHARSET.includes(0xc9) && !DISPLAY_CHARSET.includes(0xd7));
  for (const f of FACES) assert.equal(charsetOf(f), f.id === 'display' ? DISPLAY_CHARSET : CHARSET, f.id);
  const bundle = JSON.parse(fs.readFileSync(path.join(OUT, BUNDLE_NAME), 'utf8'));
  const display = bundle.Fonts.find((f) => f.Id === 'display');
  assert.equal(display.Glyphs.length, DISPLAY_CHARSET.length);
});

test('the bundle carries the board palette from art/palette.json', () => {
  const pal = boardPalette();
  assert.equal(pal.Brand.parchment, '#ecdfbf');
  assert.ok(pal.Brand.emberDeep && pal.Brand.iron900 && pal.Brand.parchmentEdge);
  assert.deepEqual(Object.keys(pal.Houses), HOUSES);
  const bundle = JSON.parse(fs.readFileSync(path.join(OUT, BUNDLE_NAME), 'utf8'));
  assert.deepEqual(bundle.Palette, pal);
});
