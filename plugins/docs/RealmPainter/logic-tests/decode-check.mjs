#!/usr/bin/env node
// Decodes every PNG the C# tests wrote (plugins/RealmPainter.cs's encoder) with the independent decoder in
// art/tools/painter/png.mjs (node:zlib inflates and checks the Adler-32; every chunk CRC is checked) and compares the
// pixels with the RGBA the C# side encoded. Also checks the C# decoder against Node's for two bundle sprites.
//
//   node plugins/docs/RealmPainter/logic-tests/decode-check.mjs <dir>
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { decodePng } from '../../../../art/tools/painter/png.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '../../../..');
const dir = process.argv[2];
if (!dir) { console.error('usage: decode-check.mjs <dir>'); process.exit(2); }

let pass = 0, fail = 0;
const ok = (cond, name, extra = '') => {
  if (cond) { pass++; console.log('PASS ' + name); } else { fail++; console.log('FAIL ' + name + (extra ? '\n     ' + extra : '')); }
};

const pngs = fs.readdirSync(dir).filter((f) => f.endsWith('.png')).sort();
let matched = 0;
const bad = [];
for (const f of pngs) {
  const base = f.slice(0, -4);
  try {
    const img = decodePng(fs.readFileSync(path.join(dir, f)));
    const [w, h] = fs.readFileSync(path.join(dir, base + '.size'), 'utf8').split('x').map(Number);
    if (img.width !== w || img.height !== h) { bad.push(`${f}: ${img.width}x${img.height}, expected ${w}x${h}`); continue; }
    const want = path.join(dir, base + '.rgba');
    if (fs.existsSync(want) && !img.rgba.equals(fs.readFileSync(want))) { bad.push(`${f}: pixels differ`); continue; }
    if (!['IHDR', 'IDAT', 'IEND'].every((c) => img.chunks.includes(c))) { bad.push(`${f}: chunks ${img.chunks}`); continue; }
    matched++;
  } catch (e) {
    bad.push(`${f}: ${e.message}`);
  }
}
ok(pngs.length >= 40 && bad.length === 0, `Node decodes all ${pngs.length} PNGs from the C# encoder to exactly the pixels encoded (${matched} matched)`, bad.join('\n     '));
ok(pngs.some((f) => f.startsWith('codec-stored')) && pngs.some((f) => f.startsWith('codec-deflate')) && pngs.some((f) => f.startsWith('board-')), 'stored, deflate and board PNGs are all among them');

// The C# decoder against Node's, on sprites from the art bundle (Node's zlib wrote these with dynamic Huffman blocks).
const bundle = JSON.parse(fs.readFileSync(path.join(REPO, 'art/paintings/RealmPainterArt.json'), 'utf8'));
for (const id of ['sigil-96-varrow', 'tile-parchment']) {
  const item = bundle.Items.find((i) => i.Id === id);
  const node = decodePng(Buffer.from(item.Png, 'base64')).rgba;
  const cs = path.join(dir, `bundle-${id}.rgba`);
  ok(fs.existsSync(cs) && node.equals(fs.readFileSync(cs)), `the C# decoder reads bundle sprite ${id} exactly as Node does`);
}

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
