// The scenes use the Realm art pack and the OFL fonts from assets/, never the internet.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { createApp, parseOptions, SCENES } from '../server.js';
import { EVENT_ICONS, GREAT_HOUSES, ART_BASE } from '../public/assets/realm-art.js';

const FIXTURES = fileURLToPath(new URL('../fixtures', import.meta.url));
const host = { headers: { Host: '127.0.0.1' } };
let server;
let base;

before(async () => {
  server = await new Promise((ok) => {
    const s = createServer(createApp(parseOptions(['--fixtures', FIXTURES, '--port', '0'], {}))).listen(0, '127.0.0.1', () => ok(s));
  });
  base = `http://127.0.0.1:${server.address().port}`;
});
after(() => server.close());

test('realm-art.js is the same file as the Chronicle pages use (portal/scripts/sync-art.mjs)', () => {
  const mine = readFileSync(new URL('../public/assets/realm-art.js', import.meta.url), 'utf8');
  const theirs = readFileSync(new URL('../../chronicle/public/assets/realm-art.js', import.meta.url), 'utf8');
  assert.equal(mine, theirs);
  assert.equal(Object.keys(EVENT_ICONS).length, 45);
});

test('every scene loads only local fonts and art, under a CSP without third parties', async () => {
  for (const scene of ['', ...SCENES]) {
    const r = await fetch(`${base}/${scene}`, host);
    assert.equal(r.status, 200, scene);
    const html = await r.text();
    assert.ok(!/https?:\/\//.test(html), `/${scene} names no other site`);
    const csp = r.headers.get('content-security-policy');
    assert.match(csp, /font-src 'self'/);
    assert.ok(!/googleapis|gstatic/.test(csp));
  }
  const css = readFileSync(new URL('../public/assets/kit.css', import.meta.url), 'utf8');
  assert.match(css, /url\('fonts\/cinzel\.woff2'\)/);
  const font = await fetch(`${base}/assets/fonts/ebgaramond.woff2`, host);
  assert.equal(font.status, 200);
  assert.equal(font.headers.get('content-type'), 'font/woff2');
});

test('the art the scenes draw is served', async () => {
  const files = ['icons.svg', 'keyart/old-throne-1920.svg', 'logo/realm-favicon.svg'];
  for (const k of Object.keys(GREAT_HOUSES)) files.push(`sigils/${k}.svg`, `banners/${k}.svg`, `shields/${k}.svg`);
  for (const f of files) {
    const r = await fetch(`${base}${ART_BASE}${f}`, host);
    assert.equal(r.status, 200, f);
    assert.equal(r.headers.get('content-type'), 'image/svg+xml', f);
  }
});
