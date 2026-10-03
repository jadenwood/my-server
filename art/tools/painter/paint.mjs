#!/usr/bin/env node
// The Realm painter: renders original Realm art to sign-sized PNGs for the game's painted signs, plus the sprites and
// glyph atlases the RealmPainter plugin composes live boards from, and packs everything into one data file the plugin
// reads (it cannot read image files itself).
//
//   node art/tools/painter/paint.mjs build      render with headless Chromium, write art/paintings/**, then check
//   node art/tools/painter/paint.mjs check      self-check without a browser (CI): files, sizes, hashes, bundle, fonts
//   node art/tools/painter/paint.mjs list       print every painting id with its size and title
//
// Options for build: --chromium <path> (default /opt/pw-browsers/chromium when it exists; or PLAYWRIGHT_CHROMIUM).
// Playwright comes from art/tools/node_modules, $ART_NODE_MODULES or the global install (see art/README.md).
//
// Output (all generated; never edit by hand):
//   art/paintings/<group>/<id>.png        finished paintings (sigils, banners, crests, posters, emblems)
//   art/paintings/sprites/<id>.png         board pieces: tiles, small sigils, icon masks
//   art/paintings/fonts/<face>.png         glyph atlases (alpha masks), with OFL-*.txt licence copies
//   art/paintings/manifest.json            every file with its size and sha256
//   art/paintings/RealmPainterArt.json     the bundle: copy to <server>/oxide/data/RealmPainterArt.json
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { loadModule } from '../lib.mjs';
import { encodePng, decodePng } from './png.mjs';
import { OUT, FONT_DIR, REPO, LIMITS, HOUSES, BIG_ICONS, FACES, FONT_LICENSES, paintings, sprites, charsetOf, boardPalette } from './designs.mjs';

export const BUNDLE_NAME = 'RealmPainterArt.json';
export const BUNDLE_FORMAT = 1;
const ATLAS_WIDTH = 256;
const ID_RE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

const sha = (buf) => crypto.createHash('sha256').update(buf).digest('hex');
const rel = (p) => path.relative(REPO, p).split(path.sep).join('/');

function args() {
  const a = process.argv.slice(2);
  const opt = { cmd: a[0] || 'check' };
  for (let i = 1; i < a.length; i++) if (a[i].startsWith('--')) opt[a[i].slice(2)] = a[i + 1] && !a[i + 1].startsWith('--') ? a[++i] : true;
  return opt;
}

// Opaque images are stored as RGB, masks as grey; everything else RGBA.
export function pack(rgba, width, height, kind) {
  const n = width * height;
  if (kind === 'mask') {
    const g = Buffer.alloc(n);
    for (let i = 0; i < n; i++) g[i] = rgba[i * 4 + 3];
    return encodePng({ width, height, data: g, channels: 1 });
  }
  let opaque = true;
  for (let i = 0; i < n && opaque; i++) if (rgba[i * 4 + 3] !== 255) opaque = false;
  if (!opaque) return encodePng({ width, height, data: Buffer.from(rgba), channels: 4 });
  const rgb = Buffer.alloc(n * 3);
  for (let i = 0; i < n; i++) { rgb[i * 3] = rgba[i * 4]; rgb[i * 3 + 1] = rgba[i * 4 + 1]; rgb[i * 3 + 2] = rgba[i * 4 + 2]; }
  return encodePng({ width, height, data: rgb, channels: 3 });
}

// Shelf-pack glyph bitmaps into one grey atlas. Returns { width, height, data, records }.
export function packAtlas(glyphs, width = ATLAS_WIDTH) {
  const order = glyphs.map((g, i) => i).sort((a, b) => glyphs[b].h - glyphs[a].h || glyphs[a].cp - glyphs[b].cp);
  const place = new Array(glyphs.length);
  let x = 1, y = 1, shelf = 0;
  for (const i of order) {
    const g = glyphs[i];
    if (g.w === 0 || g.h === 0) { place[i] = { x: 0, y: 0 }; continue; }
    if (g.w + 2 > width) throw new Error(`glyph U+${g.cp.toString(16)} is wider than the atlas`);
    if (x + g.w + 1 > width) { x = 1; y += shelf + 1; shelf = 0; }
    place[i] = { x, y };
    x += g.w + 1;
    shelf = Math.max(shelf, g.h);
  }
  const height = y + shelf + 1;
  const data = Buffer.alloc(width * height);
  const records = [];
  glyphs.forEach((g, i) => {
    const p = place[i];
    for (let yy = 0; yy < g.h; yy++) for (let xx = 0; xx < g.w; xx++) data[(p.y + yy) * width + p.x + xx] = g.alpha[yy * g.w + xx];
    // [codepoint, x, y, w, h, left, top, advance * 64]: left/top place the bitmap relative to the pen on the baseline
    records.push([g.cp, p.x, p.y, g.w, g.h, g.left, g.top, Math.round(g.adv * 64)]);
  });
  records.sort((a, b) => a[0] - b[0]);
  return { width, height, data, records };
}

async function launch(opt) {
  const { chromium } = await loadModule('playwright');
  const exe = opt.chromium || process.env.PLAYWRIGHT_CHROMIUM || (fs.existsSync('/opt/pw-browsers/chromium') ? '/opt/pw-browsers/chromium' : undefined);
  return chromium.launch(exe ? { executablePath: exe } : {});
}

async function fontsLoaded(page) {
  return page.evaluate(async () => {
    await Promise.all(['700 40px Cinzel', '600 40px Cinzel', "500 40px 'EB Garamond'", "italic 500 40px 'EB Garamond'"].map((f) => document.fonts.load(f)));
    await document.fonts.ready;
    const c = document.createElement('canvas').getContext('2d');
    const w = (f) => { c.font = f; return c.measureText('REALM OSTREVAL Hearth').width; };
    return w('700 40px Cinzel, monospace') !== w('700 40px monospace') && w("500 40px 'EB Garamond', monospace") !== w('500 40px monospace')
      && w("italic 500 40px 'EB Garamond', monospace") !== w('italic 500 40px monospace');
  });
}

async function render(page, item) {
  await page.setViewportSize({ width: item.width, height: item.height });
  await page.setContent(item.html(), { waitUntil: 'load' });
  if (/Cinzel|EB Garamond/.test(item.html()) && !(await fontsLoaded(page))) throw new Error(`${item.id}: brand fonts did not load`);
  const shot = await page.screenshot({ type: 'png', omitBackground: true, clip: { x: 0, y: 0, width: item.width, height: item.height } });
  const img = decodePng(shot);
  if (img.width !== item.width || img.height !== item.height) throw new Error(`${item.id}: rendered ${img.width}x${img.height}`);
  return img.rgba;
}

async function renderGlyphs(page, faces) {
  const { fontCss } = await import('./designs.mjs');
  await page.setViewportSize({ width: 64, height: 64 });
  await page.setContent(`<!doctype html><html><head><meta charset="utf-8"><style>${fontCss()}</style></head><body></body></html>`, { waitUntil: 'load' });
  if (!(await fontsLoaded(page))) throw new Error('brand fonts did not load for the glyph atlases');
  return page.evaluate(async ({ faces }) => {
    const out = [];
    for (const f of faces) {
      const charset = f.cps;
      const font = `${f.style} ${f.weight} ${f.size}px "${f.family}"`;
      await document.fonts.load(font);
      const S = f.size * 4, ox = f.size, base = Math.round(f.size * 2.6);
      const cv = document.createElement('canvas'); cv.width = S; cv.height = S;
      const ctx = cv.getContext('2d', { willReadFrequently: true });
      ctx.font = font;
      const m = ctx.measureText('HgÉ');
      const glyphs = [];
      for (const cp of charset) {
        const ch = String.fromCodePoint(cp);
        ctx.clearRect(0, 0, S, S);
        ctx.font = font; ctx.fillStyle = '#ffffff'; ctx.textBaseline = 'alphabetic';
        ctx.fillText(ch, ox, base);
        const adv = ctx.measureText(ch).width;
        const px = ctx.getImageData(0, 0, S, S).data;
        let x0 = S, y0 = S, x1 = -1, y1 = -1;
        for (let y = 0; y < S; y++) for (let x = 0; x < S; x++) if (px[(y * S + x) * 4 + 3] > 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        if (x1 < 0) { glyphs.push({ cp, adv, w: 0, h: 0, left: 0, top: 0, alpha: [] }); continue; }
        const w = x1 - x0 + 1, h = y1 - y0 + 1, alpha = new Array(w * h);
        for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) alpha[y * w + x] = px[((y0 + y) * S + x0 + x) * 4 + 3];
        glyphs.push({ cp, adv, w, h, left: x0 - ox, top: y0 - base, alpha });
      }
      delete f.cps;
      out.push({ ...f, ascent: Math.ceil(m.fontBoundingBoxAscent), descent: Math.ceil(m.fontBoundingBoxDescent), glyphs });
    }
    return out;
  }, { faces: faces.map((f) => ({ ...f, cps: charsetOf(f) })) });
}

function eventTypeIcons() {
  return JSON.parse(fs.readFileSync(path.join(REPO, 'art', 'icons', 'event-map.json'), 'utf8')).icons;
}

async function build(opt) {
  const browser = await launch(opt);
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  const manifest = { note: 'Generated by art/tools/painter/paint.mjs build. Do not edit. Check with: node art/tools/painter/paint.mjs check', format: BUNDLE_FORMAT, paintings: [], sprites: [], fonts: [] };
  const bundle = { Format: BUNDLE_FORMAT, Version: '', Note: 'Realm painter art bundle. Copy to <server>/oxide/data/' + BUNDLE_NAME + '. Generated by art/tools/painter/paint.mjs; do not edit.', Items: [], Fonts: [], TypeIcons: eventTypeIcons(), Palette: boardPalette(), Licenses: [] };
  try {
    fs.rmSync(OUT, { recursive: true, force: true });
    for (const p of paintings()) {
      const png = pack(await render(page, p), p.width, p.height, 'rgba');
      const file = path.join(OUT, p.group, `${p.id}.png`);
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, png);
      manifest.paintings.push({ id: p.id, group: p.group, title: p.title, width: p.width, height: p.height, file: rel(file), bytes: png.length, sha256: sha(png) });
      bundle.Items.push({ Id: p.id, Kind: 'painting', Title: p.title, Width: p.width, Height: p.height, Png: png.toString('base64') });
      console.log(`painting ${p.id} ${p.width}x${p.height} ${png.length} B`);
    }
    for (const s of sprites()) {
      const png = pack(await render(page, s), s.width, s.height, s.kind);
      const file = path.join(OUT, 'sprites', `${s.id}.png`);
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, png);
      manifest.sprites.push({ id: s.id, kind: s.kind, width: s.width, height: s.height, file: rel(file), bytes: png.length, sha256: sha(png) });
      bundle.Items.push({ Id: s.id, Kind: s.kind, Title: '', Width: s.width, Height: s.height, Png: png.toString('base64') });
    }
    console.log(`${manifest.sprites.length} sprites`);
    const faces = await renderGlyphs(page, FACES);
    for (const f of faces) {
      const atlas = packAtlas(f.glyphs);
      const png = encodePng({ width: atlas.width, height: atlas.height, data: atlas.data, channels: 1 });
      const file = path.join(OUT, 'fonts', `${f.id}.png`);
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, png);
      const lineHeight = Math.round(f.size * (f.family === 'Cinzel' ? 1.2 : 1.25));
      manifest.fonts.push({ id: f.id, family: f.family, weight: f.weight, style: f.style, size: f.size, ascent: f.ascent, descent: f.descent, lineHeight, glyphs: atlas.records.length, width: atlas.width, height: atlas.height, file: rel(file), bytes: png.length, sha256: sha(png) });
      bundle.Fonts.push({ Id: f.id, Family: f.family, Size: f.size, Ascent: f.ascent, Descent: f.descent, LineHeight: lineHeight, Width: atlas.width, Height: atlas.height, Png: png.toString('base64'), Glyphs: atlas.records });
      console.log(`font ${f.id} ${atlas.width}x${atlas.height} ${png.length} B`);
    }
  } finally {
    await browser.close();
  }
  for (const l of FONT_LICENSES) {
    fs.copyFileSync(path.join(FONT_DIR, l.file), path.join(OUT, 'fonts', l.file));
    bundle.Licenses.push(`${l.family} glyph atlases: ${l.copyright}. ${l.license}; see art/paintings/fonts/${l.file}.`);
  }
  bundle.Licenses.push('All paintings and sprites: original Realm artwork from art/ in this repository.');
  bundle.Version = sha(JSON.stringify([bundle.Items.map((i) => [i.Id, i.Png]), bundle.Fonts.map((f) => [f.Id, f.Png, f.Glyphs]), bundle.TypeIcons, bundle.Palette])).slice(0, 16);
  manifest.version = bundle.Version;
  fs.writeFileSync(path.join(OUT, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
  fs.writeFileSync(path.join(OUT, BUNDLE_NAME), JSON.stringify(bundle) + '\n');
  console.log(`bundle ${BUNDLE_NAME} version ${bundle.Version}, ${fs.statSync(path.join(OUT, BUNDLE_NAME)).size} B`);
}

// Self-check. Returns { errors, warnings, stats }.
export function check(out = OUT) {
  const errors = [], warnings = [];
  const E = (m) => errors.push(m);
  const mfFile = path.join(out, 'manifest.json'), bFile = path.join(out, BUNDLE_NAME);
  if (!fs.existsSync(mfFile) || !fs.existsSync(bFile)) { E('art/paintings is not built: run node art/tools/painter/paint.mjs build'); return { errors, warnings, stats: {} }; }
  const mf = JSON.parse(fs.readFileSync(mfFile, 'utf8'));
  let bundle;
  try { bundle = JSON.parse(fs.readFileSync(bFile, 'utf8')); } catch (e) { E(`${BUNDLE_NAME} is not valid JSON: ${e.message}`); return { errors, warnings, stats: {} }; }
  const bundleBytes = fs.statSync(bFile).size;
  if (bundleBytes > LIMITS.maxBundleBytes) E(`${BUNDLE_NAME} is ${bundleBytes} B, over the ${LIMITS.maxBundleBytes} B budget`);
  if (bundle.Format !== BUNDLE_FORMAT) E(`${BUNDLE_NAME} format ${bundle.Format}, expected ${BUNDLE_FORMAT}`);
  if (!bundle.Version || bundle.Version !== mf.version) E('bundle and manifest versions differ: rebuild');

  const items = new Map((bundle.Items || []).map((i) => [i.Id, i]));
  if (items.size !== (bundle.Items || []).length) E('bundle has duplicate item ids');

  const verify = (entry, kind, cap) => {
    const file = path.join(out, path.relative('art/paintings', entry.file));
    if (!ID_RE.test(entry.id)) E(`${entry.id}: id must be lower-case words joined by "-"`);
    if (!fs.existsSync(file)) { E(`${entry.file} is missing`); return null; }
    const buf = fs.readFileSync(file);
    if (sha(buf) !== entry.sha256) E(`${entry.file} changed since the build (sha256)`);
    if (buf.length > cap) E(`${entry.file} is ${buf.length} B, over the ${cap} B cap`);
    let img;
    try { img = decodePng(buf); } catch (e) { E(`${entry.file}: ${e.message}`); return null; }
    if (img.width !== entry.width || img.height !== entry.height) E(`${entry.file} is ${img.width}x${img.height}, manifest says ${entry.width}x${entry.height}`);
    if (Math.max(img.width, img.height) > LIMITS.maxSide) E(`${entry.file} is larger than ${LIMITS.maxSide} px a side`);
    if (kind === 'mask' && img.channels !== 1) E(`${entry.file}: masks are stored as grey`);
    return { buf, img };
  };

  const want = new Set(paintings().map((p) => p.id));
  for (const p of mf.paintings || []) {
    const r = verify(p, 'rgba', LIMITS.maxPaintingBytes);
    const b = items.get(p.id);
    if (!b) E(`painting ${p.id} is not in the bundle`);
    else if (r && Buffer.from(b.Png, 'base64').compare(r.buf) !== 0) E(`painting ${p.id}: bundle copy differs from ${p.file}`);
    else if (b && b.Kind !== 'painting') E(`painting ${p.id} has bundle kind ${b.Kind}`);
    if (r) {
      // a painting must not be blank: some pixels visible and not all the same colour
      const px = r.img.rgba; let vis = 0; const first = px.readUInt32BE(0); let varied = false;
      for (let i = 0; i < px.length; i += 4) { if (px[i + 3] > 0) vis++; if (!varied && px.readUInt32BE(i) !== first) varied = true; }
      if (vis < (r.img.width * r.img.height) / 10 || !varied) E(`painting ${p.id} looks blank`);
    }
    if (!want.has(p.id)) E(`painting ${p.id} is no longer designed: rebuild`);
    want.delete(p.id);
  }
  for (const id of want) E(`painting ${id} is designed but not built: rebuild`);
  for (const h of HOUSES) for (const g of ['sigil', 'banner', 'crest']) if (!(mf.paintings || []).some((p) => p.id === `${g}-${h}`)) E(`no ${g}-${h} painting`);

  const wantSprites = new Set(sprites().map((s) => s.id));
  for (const s of mf.sprites || []) {
    const r = verify(s, s.kind, LIMITS.maxSpriteBytes);
    const b = items.get(s.id);
    if (!b) E(`sprite ${s.id} is not in the bundle`);
    else if (r && Buffer.from(b.Png, 'base64').compare(r.buf) !== 0) E(`sprite ${s.id}: bundle copy differs from ${s.file}`);
    else if (b && b.Kind !== s.kind) E(`sprite ${s.id} has bundle kind ${b.Kind}, manifest ${s.kind}`);
    if (!wantSprites.has(s.id)) E(`sprite ${s.id} is no longer designed: rebuild`);
    wantSprites.delete(s.id);
  }
  for (const id of wantSprites) E(`sprite ${id} is designed but not built: rebuild`);
  for (const n of BIG_ICONS) if (!items.has(`icon-64-${n}`)) E(`no icon-64-${n} sprite`);
  for (const h of HOUSES) for (const s of [96, 32]) if (!items.has(`sigil-${s}-${h}`)) E(`no sigil-${s}-${h} sprite`);
  for (const id of ['tile-parchment', 'tile-iron', 'emblem-96', 'hammer-128']) if (!items.has(id)) E(`no ${id} sprite`);

  if (JSON.stringify(bundle.Palette) !== JSON.stringify(boardPalette())) E('bundle Palette differs from art/palette.json: rebuild');
  const typeIcons = eventTypeIcons();
  if (JSON.stringify(bundle.TypeIcons) !== JSON.stringify(typeIcons)) E('bundle TypeIcons differ from art/icons/event-map.json: rebuild');
  for (const [t, icon] of Object.entries(typeIcons)) if (!items.has(`icon-24-${icon}`)) E(`Chronicle type ${t}: no icon-24-${icon} sprite`);

  const fonts = new Map((bundle.Fonts || []).map((f) => [f.Id, f]));
  for (const face of FACES) {
    const m = (mf.fonts || []).find((f) => f.id === face.id);
    const b = fonts.get(face.id);
    if (!m || !b) { E(`font ${face.id} is missing from the ${m ? 'bundle' : 'manifest'}`); continue; }
    const r = verify(m, 'mask', LIMITS.maxAtlasBytes);
    if (r && Buffer.from(b.Png, 'base64').compare(r.buf) !== 0) E(`font ${face.id}: bundle atlas differs from ${m.file}`);
    if (b.Size !== face.size || b.Family !== face.family) E(`font ${face.id}: bundle says ${b.Family} ${b.Size}, designs say ${face.family} ${face.size}`);
    if (!(b.Ascent > 0 && b.LineHeight >= face.size)) E(`font ${face.id}: bad metrics`);
    const cps = new Set();
    for (const g of b.Glyphs || []) {
      const [cp, x, y, w, h, , , adv] = g;
      if (g.length !== 8) { E(`font ${face.id}: glyph record must have 8 numbers`); break; }
      cps.add(cp);
      if (x < 0 || y < 0 || x + w > b.Width || y + h > b.Height) E(`font ${face.id}: glyph U+${cp.toString(16)} lies outside the atlas`);
      if (adv <= 0 && cp !== 0x20 && w > 0) E(`font ${face.id}: glyph U+${cp.toString(16)} has no advance`);
    }
    for (const cp of charsetOf(face)) if (!cps.has(cp)) E(`font ${face.id} lacks U+${cp.toString(16).padStart(4, '0')}`);
    const space = (b.Glyphs || []).find((g) => g[0] === 0x20);
    if (!space || space[7] <= 0) E(`font ${face.id}: the space has no advance`);
  }
  for (const l of FONT_LICENSES) {
    if (!fs.existsSync(path.join(out, 'fonts', l.file))) E(`art/paintings/fonts/${l.file} (licence record for the ${l.family} atlases) is missing`);
    if (!(bundle.Licenses || []).some((s) => s.includes(l.family) && s.includes('Open Font License'))) E(`bundle Licenses does not credit ${l.family}`);
  }

  // Third-party names in titles and the bundle are caught by art/tools/build.mjs check, which scans every .json in art/.

  return { errors, warnings, stats: { paintings: (mf.paintings || []).length, sprites: (mf.sprites || []).length, fonts: (mf.fonts || []).length, bundleBytes, version: bundle.Version } };
}

function list() {
  for (const p of paintings()) console.log(`${p.id.padEnd(26)} ${String(p.width).padStart(3)}x${String(p.height).padEnd(4)} ${p.title}`);
}

async function main() {
  const opt = args();
  if (opt.cmd === 'list') return list();
  if (opt.cmd === 'build') await build(opt);
  else if (opt.cmd !== 'check') { console.error('usage: paint.mjs build|check|list [--chromium <path>]'); process.exit(2); }
  const r = check();
  for (const w of r.warnings) console.log('WARN ' + w);
  for (const e of r.errors) console.log('ERROR ' + e);
  console.log(`painter check: ${r.stats.paintings || 0} paintings, ${r.stats.sprites || 0} sprites, ${r.stats.fonts || 0} fonts, bundle ${r.stats.bundleBytes || 0} B (${r.stats.version || '-'}): ${r.errors.length ? r.errors.length + ' error(s)' : 'OK'}`);
  process.exit(r.errors.length ? 1 : 0);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) main().catch((e) => { console.error(e.stack || e.message); process.exit(1); });
