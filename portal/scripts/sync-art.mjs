#!/usr/bin/env node
// Copies the art pack (art/) and the OFL fonts (launcher/renderer/fonts) into the three public-facing
// packages, so each one stays self-contained and works offline:
//
//   portal/assets/art, portal/assets/fonts
//   chronicle/public/assets/art, chronicle/public/assets/fonts
//   streamkit/public/assets/art, streamkit/public/assets/fonts
//
// It also copies chronicle/public/assets/realm-art.js (the browser heraldry helper the Chronicle pages
// use) to streamkit/public/assets/realm-art.js, so the overlays and the stream scenes draw the same art.
//
//   node portal/scripts/sync-art.mjs           copy (overwrites the copies, never the sources)
//   node portal/scripts/sync-art.mjs --check   exit 1 if any copy differs from its source
//
// The copies are committed. Edit the art in art/ and run this again; never edit a copy.

import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
export const REPO = resolve(HERE, '..', '..');
export const HOUSES = ['varrow', 'ashgrove', 'corvane', 'dunmere', 'halloran', 'merrin'];
// Renown title badges (art/badges/titles), named after the ids in art/src/titles.json.
export const TITLES = JSON.parse(readFileSync(join(REPO, 'art', 'src', 'titles.json'), 'utf8')).titles.map((t) => t.id.replace(/_/g, '-'));

// [source under the repo, destination under the package's art folder]
const COMMON = [
  ...HOUSES.map((h) => [`art/sigils/${h}.svg`, `sigils/${h}.svg`]),
  ...HOUSES.map((h) => [`art/banners/${h}.svg`, `banners/${h}.svg`]),
  ...HOUSES.map((h) => [`art/shields/${h}.svg`, `shields/${h}.svg`]),
  ['art/sprite/icons.svg', 'icons.svg'],
  ['art/icons/event-map.json', 'event-map.json'],
  ['art/src/titles.json', 'titles.json'],
  ['art/palette.json', 'palette.json'],
  ...[1, 2, 3, 4].map((n) => [`art/badges/seasons/season-${n}.svg`, `badges/season-${n}.svg`]),
  ...TITLES.map((t) => [`art/badges/titles/${t}.svg`, `badges/titles/${t}.svg`]),
  ['art/logo/realm-emblem.svg', 'logo/realm-emblem.svg'],
  ['art/logo/realm-logo-horizontal-dark.svg', 'logo/realm-logo-horizontal-dark.svg'],
  ['art/logo/realm-wordmark-dark.svg', 'logo/realm-wordmark-dark.svg'],
  ['art/logo/realm-favicon.svg', 'logo/realm-favicon.svg'],
  ['art/textures/parchment.svg', 'textures/parchment.svg'],
  ['art/textures/iron.svg', 'textures/iron.svg'],
  ['art/keyart/old-throne-1920.svg', 'keyart/old-throne-1920.svg'],
];

export const TARGETS = {
  portal: {
    art: 'portal/assets/art',
    fonts: 'portal/assets/fonts',
    files: [
      ...COMMON,
      ['art/logo/realm-logo-stacked-dark.svg', 'logo/realm-logo-stacked-dark.svg'],
      // Link previews need a raster image: the 1200 x 630 key art with the logo, and each great
      // house's sigil for its own page.
      ['art/png/keyart/old-throne-1200-title.png', 'og/realm-card.png'],
      ...HOUSES.map((h) => [`art/png/sigils/${h}-512.png`, `og/${h}.png`]),
      ['art/png/logo/realm-emblem-192.png', 'logo/realm-emblem-192.png'],
    ],
  },
  chronicle: { art: 'chronicle/public/assets/art', fonts: 'chronicle/public/assets/fonts', files: COMMON },
  streamkit: {
    art: 'streamkit/public/assets/art',
    fonts: 'streamkit/public/assets/fonts',
    files: [...COMMON, ['chronicle/public/assets/realm-art.js', '../realm-art.js']],
  },
};

const FONTS_DIR = 'launcher/renderer/fonts';
const FONT_FILES = ['cinzel.woff2', 'ebgaramond.woff2', 'ebgaramond-italic.woff2', 'OFL-Cinzel.txt', 'OFL-EBGaramond.txt'];

const LICENSE = `# Where these files come from

Copied from the Realm art pack by \`portal/scripts/sync-art.mjs\`. Do not edit them here: edit the source in
\`art/\` (see \`art/README.md\`) and run \`node portal/scripts/sync-art.mjs\`.

| Files | Source | Licence |
|---|---|---|
| \`sigils/\`, \`banners/\`, \`shields/\`, \`badges/\`, \`logo/\`, \`textures/\`, \`keyart/\`, \`icons.svg\`, \`og/\` | \`art/\` | Original work made for the Realm of Ostreval project. The wordmark letters are outlined from Cinzel (SIL Open Font License 1.1). |
| \`event-map.json\` | \`art/icons/event-map.json\` | Same as above |
| \`../fonts/*.woff2\` | \`launcher/renderer/fonts\` | Cinzel and EB Garamond, SIL Open Font License 1.1; the licence texts are next to the fonts |

No third-party artwork is included.
`;

// Every [absolute source, absolute destination] pair for one package (or all of them).
export function plan(only = null) {
  const out = [];
  for (const [name, t] of Object.entries(TARGETS)) {
    if (only && name !== only) continue;
    for (const [src, dst] of t.files) out.push([join(REPO, src), join(REPO, t.art, dst)]);
    for (const f of FONT_FILES) out.push([join(REPO, FONTS_DIR, f), join(REPO, t.fonts, f)]);
  }
  return out;
}

// Copies that are missing or differ from their source. Sources that do not exist are reported too.
export function drift(only = null) {
  const bad = [];
  for (const [src, dst] of plan(only)) {
    if (!existsSync(src)) { bad.push(`${src} (source missing)`); continue; }
    if (!existsSync(dst) || !readFileSync(src).equals(readFileSync(dst))) bad.push(dst);
  }
  return bad;
}

function copyAll() {
  let n = 0;
  for (const [src, dst] of plan()) {
    mkdirSync(dirname(dst), { recursive: true });
    const body = readFileSync(src);
    if (existsSync(dst) && readFileSync(dst).equals(body)) continue;
    writeFileSync(dst, body);
    n++;
  }
  for (const t of Object.values(TARGETS)) writeFileSync(join(REPO, t.art, 'SOURCES.md'), LICENSE);
  return n;
}

const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  if (!existsSync(join(REPO, 'art')) || !readdirSync(join(REPO, 'art')).length) {
    console.error('[sync-art] art/ not found next to portal/');
    process.exit(2);
  }
  if (process.argv.includes('--check')) {
    const bad = drift();
    for (const b of bad) console.error(`[sync-art] out of date: ${b}`);
    console.log(bad.length ? `[sync-art] ${bad.length} file(s) out of date; run node portal/scripts/sync-art.mjs` : '[sync-art] all copies match art/');
    process.exit(bad.length ? 1 : 0);
  }
  console.log(`[sync-art] updated ${copyAll()} file(s)`);
}
