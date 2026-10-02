// node --test art/tools/test/   (no browser needed)
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { innerGroup, attrsOf, esc } from '../lib.mjs';
import { suffixIds, wrap, xmlBalanced, contrast, socialCard, wordmark, cinzelWidth } from '../build.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const ART = path.resolve(here, '..', '..');
const BUILD = path.join(here, '..', 'build.mjs');

test('innerGroup returns the whole charge, nested groups included', () => {
  const svg = '<svg><g id="charge" transform="translate(1 2)"><g fill="red"><g><path d="M0 0"/></g></g><circle r="1"/></g><g id="other"/></svg>';
  const g = innerGroup(svg, 'charge');
  assert.equal(g.inner, '<g fill="red"><g><path d="M0 0"/></g></g><circle r="1"/>');
  assert.equal(g.attrs.transform, 'translate(1 2)');
  assert.throws(() => innerGroup(svg, 'missing'));
});

test('every sigil exposes a charge group the build can reuse', () => {
  for (const f of fs.readdirSync(path.join(ART, 'sigils'))) {
    const svg = fs.readFileSync(path.join(ART, 'sigils', f), 'utf8');
    const g = innerGroup(svg, 'charge');
    assert.ok(g.inner.length > 100, f);
    assert.equal(xmlBalanced(`<g>${g.inner}</g>`), null, f);
  }
});

test('suffixIds renames ids and every kind of reference', () => {
  const out = suffixIds('<linearGradient id="g"/><path id="p" fill="url(#g)"/><use href="#p"/>', 'x');
  assert.equal(out, '<linearGradient id="g-x"/><path id="p-x" fill="url(#g-x)"/><use href="#p-x"/>');
  assert.equal(suffixIds('<g id="a"/>', ''), '<g id="a"/>');
});

test('xmlBalanced catches unclosed and mismatched tags', () => {
  assert.equal(xmlBalanced('<svg><g><path d="M0 0"/></g></svg>'), null);
  assert.match(xmlBalanced('<svg><g></svg>'), /unexpected|unclosed/);
  assert.match(xmlBalanced('<svg><g>'), /unclosed/);
  assert.equal(xmlBalanced('<svg><!-- <g> --></svg>'), null);
});

test('wrap respects the budget and ellipsises overflow', () => {
  assert.deepEqual(wrap('The Iron Stag takes the Old Throne', 18, 3), ['The Iron Stag', 'takes the Old', 'Throne']);
  const w = wrap('one two three four five six seven eight nine ten', 9, 2);
  assert.equal(w.length, 2);
  assert.ok(w[1].endsWith('…'));
});

test('title wrapping by Cinzel width keeps wide letters inside the column', () => {
  assert.ok(cinzelWidth('MMMM', 66) > cinzelWidth('iiii', 66) * 2);
  const lines = wrap('MMMMMMMM WWWWWWWW MMMMMMMM WWWWW', 668, 3, (t) => cinzelWidth(t, 54));
  for (const l of lines) assert.ok(cinzelWidth(l.replace('…', ''), 54) <= 668, l);
  const svg = socialCard({ house: 'varrow', kicker: 'k', title: 'The Iron Stag takes the Old Throne and every house bends the knee before the Hearth' });
  assert.match(svg, /font-size="54"/);
  assert.ok(svg.includes('…'));
});

test('contrast matches the WCAG formula', () => {
  assert.equal(contrast('#000000', '#ffffff').toFixed(1), '21.0');
  assert.equal(contrast('#ffffff', '#ffffff').toFixed(1), '1.0');
});

test('social cards escape user text and stay well-formed', () => {
  const svg = socialCard({ house: 'merrin', kicker: '<b>x</b>', title: 'A & B <script>alert(1)</script>', subtitle: '"quoted"', footer: "it's" });
  assert.equal(xmlBalanced(svg), null);
  assert.ok(!svg.includes('<script'));
  assert.ok(svg.includes('A &amp; B &lt;script&gt;'));
  const ids = [...svg.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]);
  assert.equal(new Set(ids).size, ids.length, 'ids are unique');
  for (const m of svg.matchAll(/url\(#([^)]+)\)|href="#([^"]+)"/g)) assert.ok(ids.includes(m[1] || m[2]), `missing #${m[1] || m[2]}`);
});

test('wordmark sits inside its own box', () => {
  for (const mode of ['dark', 'light']) {
    const wm = wordmark(mode);
    assert.ok(wm.w > 400 && wm.h > 100 && wm.h < 160, `${mode} ${wm.w}x${wm.h}`);
  }
});

test('esc and attrsOf round-trip', () => {
  assert.equal(esc('<"&>'), '&lt;&quot;&amp;&gt;');
  assert.deepEqual(attrsOf('<svg viewBox="0 0 1 1" data-x="y">'), { viewBox: '0 0 1 1', 'data-x': 'y' });
});

test('card CLI writes an SVG and rejects unknown houses', () => {
  const out = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'realm-card-')), 'c.svg');
  const ok = spawnSync(process.execPath, [BUILD, 'card', '--house', 'dunmere', '--title', 'The tide returns', '--out', out], { encoding: 'utf8' });
  assert.equal(ok.status, 0, ok.stderr);
  assert.equal(xmlBalanced(fs.readFileSync(out, 'utf8')), null);
  const bad = spawnSync(process.execPath, [BUILD, 'card', '--house', 'nobody', '--title', 'x', '--out', out], { encoding: 'utf8' });
  assert.equal(bad.status, 2);
});

test('the committed pack passes the lint (PNG sizes are checked by the full run)', () => {
  const r = spawnSync(process.execPath, [BUILD, 'check', '--skip-png'], { encoding: 'utf8' });
  assert.equal(r.status, 0, r.stdout + r.stderr);
});
