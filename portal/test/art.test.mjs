// The art pack in the portal: copies in step with art/, every Chronicle type has its own icon, pages
// only point at art files that exist, link previews are there, and the CSP stays strict.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, access } from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { EVENT_ICONS, SYMBOLS, GREAT_HOUSES, TITLES, eventIconName, greatHouse, houseArt, seasonBadge, spriteFor, titleOf } from '../lib/art.mjs';
import { TYPE_META } from '../lib/model.mjs';
import { buildSite } from '../lib/generate.mjs';
import { drift, TARGETS } from '../scripts/sync-art.mjs';

const REPO = fileURLToPath(new URL('../../', import.meta.url));
const NOW = Date.parse('2026-10-01T21:08:00Z');
const exists = (p) => access(p).then(() => true, () => false);

test('the art copies in portal, chronicle and streamkit match art/ (node portal/scripts/sync-art.mjs)', () => {
  assert.deepEqual(drift(), []);
  assert.deepEqual(Object.keys(TARGETS).sort(), ['chronicle', 'portal', 'streamkit']);
});

test('every Chronicle event type has its own icon, and the sprite has it', () => {
  const types = Object.keys(TYPE_META);
  assert.equal(types.length, 44);
  const used = new Set();
  for (const t of types) {
    assert.ok(EVENT_ICONS[t], `${t} has an icon`);
    assert.ok(SYMBOLS.has(EVENT_ICONS[t]), `${t}: symbol ${EVENT_ICONS[t]} is in the sprite`);
    assert.ok(!used.has(EVENT_ICONS[t]), `${t} shares its icon`);
    used.add(EVENT_ICONS[t]);
    assert.equal(eventIconName(t), EVENT_ICONS[t]);
  }
  assert.equal(eventIconName('not_a_type'), 'decree');
  for (const sym of SYMBOLS.values()) assert.ok(!/<title>|style=|<script|on[a-z]+=/i.test(sym), 'symbols are bare paths');
});

test('great houses get their drawn arms; any other house gets none', () => {
  assert.deepEqual(Object.keys(GREAT_HOUSES).sort(), ['ashgrove', 'corvane', 'dunmere', 'halloran', 'merrin', 'varrow']);
  assert.equal(greatHouse('House Varrow'), 'varrow');
  assert.equal(greatHouse('varrow'), 'varrow');
  assert.equal(greatHouse('Varrowmoor'), null);
  assert.equal(greatHouse('constructor'), null, 'no prototype keys');
  assert.match(houseArt('Corvane', 'banner', '../'), /src="\.\.\/assets\/art\/banners\/corvane\.svg"/);
  assert.match(houseArt('Merrin', 'shield'), /width="240" height="280"/);
  assert.equal(houseArt('Thornwick'), '');
});

test('season medals cycle I to IV; titles are found in Chronicle text', () => {
  assert.match(seasonBadge(1), /season-1\.svg/);
  assert.match(seasonBadge(6), /season-2\.svg/);
  assert.equal(seasonBadge(0), '');
  assert.equal(TITLES.length, 27);
  assert.equal(titleOf('Wren is named Champion of the Lists').id, 'champion');
  assert.equal(titleOf('Hale is named the Renowned').id, 'renowned');
  assert.equal(titleOf('Hale is named Lord of Ashes'), null);
  // Every title in plugins/RealmRenown.cs DefaultTitles has a badge file.
  const cs = readFileSync(join(REPO, 'plugins', 'RealmRenown.cs'), 'utf8');
  for (const t of TITLES) {
    assert.ok(cs.includes(`"${t.id}"`), `${t.id} is a RealmRenown title`);
    assert.ok(readFileSync(join(REPO, 'portal', 'assets', 'art', t.file)), `${t.file} exists`);
  }
});

test('the sprite carries exactly the icons a page uses', () => {
  const html = '<svg><use href="#realm-icon-crown"/></svg><svg><use href="#realm-icon-treaty"/></svg><svg><use href="#realm-icon-crown"/></svg>';
  const sprite = spriteFor(html);
  assert.equal((sprite.match(/<symbol /g) || []).length, 2);
  assert.match(sprite, /id="realm-icon-crown"/);
  assert.equal(spriteFor('<p>none</p>'), '');
});

test('built pages: art files exist, icons resolve, link previews, manifest, strict CSP', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'portal-art-'));
  const out = join(dir, 'site');
  const r = await buildSite({ outDir: out, now: NOW, config: { siteUrl: 'https://realm.test/', realmName: 'The Realm of Ostreval' } });
  for (const [rel, body] of r.files) {
    if (!rel.endsWith('.html')) continue;
    const base = dirname(join(out, rel));
    // Every local src/href that is not a page link or anchor must exist in the output.
    for (const m of body.matchAll(/\s(?:src|href)="([^"#:?]+\.(?:svg|png|css|js|webmanifest|woff2))"/g)) {
      assert.ok(await exists(resolve(base, m[1])), `${rel}: ${m[1]} exists`);
    }
    for (const m of body.matchAll(/<use href="#([a-z0-9-]+)"/g)) assert.ok(body.includes(`<symbol id="${m[1]}"`), `${rel}: #${m[1]} defined`);
    assert.match(body, /Content-Security-Policy" content="default-src 'self'; img-src 'self' data:; style-src 'self'; font-src 'self'; script-src 'self'/);
    assert.ok(!/\ssrc="https?:/.test(body), `${rel} loads nothing from another site`);
    assert.match(body, /<meta property="og:image" content="https:\/\/realm\.test\/assets\/art\/og\/[a-z-]+\.png">/, `${rel} has an absolute og:image`);
    assert.match(body, /<meta name="twitter:card" content="summary(_large_image)?">/);
  }
  const home = r.files.get('index.html');
  assert.match(home, /assets\/art\/keyart|hero-art/);
  assert.match(home, /<link rel="canonical" href="https:\/\/realm\.test\/index\.html">/);
  assert.match(home, /assets\/art\/banners\/varrow\.svg/, 'the monarch hangs the house banner');
  assert.match(home, /assets\/art\/badges\/season-1\.svg/, 'season medal on the home page');
  const varrow = r.files.get('houses/varrow.html');
  assert.match(varrow, /og\/varrow\.png/);
  assert.match(varrow, /content="summary"/);
  const kings = r.files.get('kings.html');
  assert.equal((kings.match(/class="renown-badge"/g) || []).length, 27, 'every title badge on the Hall of Kings');
  const css = r.files.get('assets/houses.css');
  assert.match(css, /\.dye-varrow\{[^}]*--sigil:url\('art\/sigils\/varrow\.svg'\)/);
  const manifest = JSON.parse(r.files.get('manifest.webmanifest'));
  assert.equal(manifest.name, 'The Realm of Ostreval');
  assert.ok(await exists(join(out, manifest.icons[0].src)));
  // Without siteUrl, previews fall back to a relative image and no canonical link.
  const r2 = await buildSite({ outDir: join(dir, 'site2'), now: NOW, config: {}, dryRun: true });
  assert.match(r2.files.get('index.html'), /og:image" content="assets\/art\/og\/realm-card\.png"/);
  assert.ok(!/rel="canonical"/.test(r2.files.get('index.html')));
  await rm(dir, { recursive: true, force: true });
});

test('title badges and season medals reach the Chronicle (showcase sample data)', async () => {
  const r = await buildSite({
    now: NOW, dryRun: true, config: {},
    dataDirs: [join(REPO, 'portal', 'sample-data', 'showcase'), join(REPO, 'chronicle', 'sample-data'), join(REPO, 'portal', 'sample-data')],
  });
  const chron = r.files.get('chronicle.html');
  assert.match(chron, /<span class="ev-ico badge"><img class="ev-badge" src="assets\/art\/badges\/titles\/champion\.svg"/);
  assert.match(chron, /<span class="ev-ico badge infamous"><img class="ev-badge" src="assets\/art\/badges\/titles\/oathbreaker\.svg"/);
  const kings = r.files.get('kings.html');
  assert.match(kings, /renown earned[^"]*"[\s\S]*?Odo the Tall/);
  assert.match(kings, /2 of 27 claimed/);
});
