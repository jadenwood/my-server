// Builds the static portal: reads the data files and docs, writes HTML/CSS/JS/JSON to the output folder.
// No server, no network. The output folder is replaced atomically-ish (staged, then swapped), and a
// folder is only ever cleared if it carries the .realm-portal marker this generator writes.

import { mkdir, writeFile, readFile, rm, rename, cp, readdir, stat, access } from 'node:fs/promises';
import { join, resolve, dirname, basename, parse as parsePath } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readRealmData, readJson } from './data.mjs';
import { buildModel, parseHouseLore } from './model.mjs';
import { render } from './markdown.mjs';
import * as P from './pages.mjs';
import { safeUrl } from './util.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
export const PORTAL_DIR = resolve(HERE, '..');
export const REPO_DIR = resolve(PORTAL_DIR, '..');
const MARKER = '.realm-portal';

export const DEFAULTS = {
  realmName: 'The Realm',
  kicker: 'A Reign of Kings community server',
  tagline: 'Swear an oath. Claim a crown. Answer for it.',
  siteUrl: '',
  discordInvite: '',
  download: { url: '', version: '', sha256: '' },
  server: { address: '', port: 7350 },
  streamers: [],
  events: [],
  staleAfterMinutes: 10,
};

const PLACEHOLDER_HOSTS = /(^|\.)(example\.(com|org|net|invalid)|localhost)$/i;
const isPlaceholder = (u) => {
  try {
    const url = new URL(u);
    return PLACEHOLDER_HOSTS.test(url.hostname) || /your-|replace|todo|placeholder/i.test(url.href);
  } catch {
    return true;
  }
};
const httpsOnly = (u) => {
  const s = safeUrl(u);
  return s && s.startsWith('https://') && !isPlaceholder(s) ? s : '';
};
const text = (v, max, fallback = '') => (typeof v === 'string' && v.trim() ? v.trim().slice(0, max) : fallback);

// Validates the owner's portal.config.json. Anything unsafe or a placeholder is dropped, not trusted.
export function normalizeConfig(raw = {}, now = Date.now()) {
  raw = raw && typeof raw === 'object' ? raw : {};
  const dl = raw.download && typeof raw.download === 'object' ? raw.download : {};
  const sv = raw.server && typeof raw.server === 'object' ? raw.server : {};
  const address = text(sv.address, 253);
  const port = Number.isInteger(sv.port) && sv.port > 0 && sv.port < 65536 ? sv.port : DEFAULTS.server.port;
  const site = {
    realmName: text(raw.realmName, 60, DEFAULTS.realmName),
    kicker: text(raw.kicker, 80, DEFAULTS.kicker),
    tagline: text(raw.tagline, 160, DEFAULTS.tagline),
    siteUrl: httpsOnly(raw.siteUrl),
    discordInvite: httpsOnly(raw.discordInvite),
    download: {
      url: httpsOnly(dl.url),
      version: text(dl.version, 24),
      sha256: typeof dl.sha256 === 'string' && /^[a-f0-9]{64}$/i.test(dl.sha256.trim()) ? dl.sha256.trim().toLowerCase() : '',
    },
    // The address box takes a host only (typing "host:port" there fails with "Unable to resolve host name").
    server: { address: /^[a-z0-9.-]+$/i.test(address) && !address.includes(':') ? address : '', port },
    streamers: (Array.isArray(raw.streamers) ? raw.streamers : [])
      .map((s) => s && typeof s === 'object' ? { name: text(s.name, 48), url: httpsOnly(s.url), platform: text(s.platform, 24), house: text(s.house, 64), note: text(s.note, 160) } : null)
      .filter((s) => s && s.name && s.url)
      .slice(0, 24),
    events: Array.isArray(raw.events) ? raw.events.slice(0, 16) : [],
    staleAfterMinutes: Number.isFinite(raw.staleAfterMinutes) && raw.staleAfterMinutes > 0 ? raw.staleAfterMinutes : DEFAULTS.staleAfterMinutes,
    generatedAt: now,
  };
  return site;
}

// Markdown links inside docs/community/*.md, mapped onto portal pages.
const DOC_MAP = { 'how-to-play.md': 'play.html', 'rules.md': 'rules.html', 'lore.md': 'lore.html', 'streamer-kit.md': 'streamers.html' };
export function docLink(href) {
  if (href.startsWith('#')) return href;
  if (/^https:\/\//i.test(href)) return safeUrl(href);
  const m = href.match(/^(?:\.\/)?([a-z0-9-]+\.md)(#[\w-]*)?$/i);
  if (m && DOC_MAP[m[1].toLowerCase()]) return DOC_MAP[m[1].toLowerCase()] + (m[2] || '');
  return null; // repo-internal paths (plugins/, chronicle/, launch-plan.md) are shown as text only
}

async function readText(path) {
  try {
    return (await readFile(path, 'utf8')).replace(/^﻿/, '');
  } catch {
    return null;
  }
}

async function exists(p) {
  try {
    await access(p);
    return true;
  } catch {
    return false;
  }
}

async function findTypeDocs(pluginsDir) {
  try {
    const dirs = await readdir(join(pluginsDir, 'docs'), { withFileTypes: true });
    return dirs.filter((d) => d.isDirectory()).map((d) => join(pluginsDir, 'docs', d.name, 'EVENTS.json'));
  } catch {
    return [];
  }
}

export async function buildSite(opts = {}) {
  const now = opts.now ?? Date.now();
  const dataDirs = (opts.dataDirs && opts.dataDirs.length ? opts.dataDirs : [join(REPO_DIR, 'chronicle', 'sample-data'), join(PORTAL_DIR, 'sample-data')]).map((d) => resolve(d));
  const docsDir = resolve(opts.docsDir || join(REPO_DIR, 'docs', 'community'));
  const out = resolve(opts.outDir || join(PORTAL_DIR, 'dist'));
  const fontsDir = opts.fontsDir === null ? null : resolve(opts.fontsDir || join(REPO_DIR, 'launcher', 'renderer', 'fonts'));
  const rawConfig = opts.config !== undefined ? opts.config : (opts.configFile ? (await readJson(resolve(opts.configFile))).value : null);
  const site = normalizeConfig(rawConfig || {}, now);

  // oxide/config holds the RealmEvents schedule. Use --oxide-config, or the "config" folder next to a "data" folder.
  let configDirs = (opts.configDirs || []).map((d) => resolve(d));
  if (!configDirs.length) {
    for (const d of dataDirs) {
      const sib = join(dirname(d), 'config');
      if (basename(d).toLowerCase() === 'data' && (await exists(sib))) configDirs.push(sib);
      if (await exists(join(d, 'config'))) configDirs.push(join(d, 'config')); // sample-data/config
    }
  }
  const data = await readRealmData({ dataDirs, configDirs, typeDocs: await findTypeDocs(opts.pluginsDir || join(REPO_DIR, 'plugins')), now });
  const loreMd = await readText(join(docsDir, 'lore.md'));
  const model = buildModel(data, { lore: parseHouseLore(loreMd), config: site, now });
  const newsRaw = (await readJson(join(docsDir, 'news-seed.json'))).value;
  const news = (Array.isArray(newsRaw) ? newsRaw : [])
    .filter((n) => n && typeof n.title === 'string' && typeof n.body === 'string' && /^\d{4}-\d\d-\d\d$/.test(n.date || ''))
    .map((n) => ({ date: n.date, title: n.title.slice(0, 120), body: n.body.slice(0, 600) }));

  const doc = async (file) => render((await readText(join(docsDir, file))) || '', { linkFor: docLink });
  const stripH1 = (r) => ({ ...r, html: r.html.replace(/^<h1[^>]*>.*?<\/h1>\n?/, '') });

  const files = new Map();
  files.set('index.html', P.homePage(model, site, news));
  files.set('chronicle.html', P.chroniclePage(model, site));
  files.set('houses.html', P.housesPage(model, site));
  for (const h of model.houses) files.set(`houses/${h.slug}.html`, P.housePage(h, model, site));
  files.set('kings.html', P.kingsPage(model, site));
  files.set('play.html', P.docPage({ site, id: 'play', title: 'How to play', kicker: 'The codex of the realm', rendered: stripH1(await doc('how-to-play.md')) }));
  files.set('rules.html', P.docPage({ site, id: 'rules', title: 'Rules of the Realm', kicker: 'The codex of the realm', rendered: stripH1(await doc('rules.md')) }));
  files.set('lore.html', P.docPage({ site, id: 'lore', title: 'The Realm of Ostreval', kicker: 'The codex of the realm', rendered: stripH1(await doc('lore.md')) }));
  files.set('streamers.html', P.streamersPage(model, site, stripH1(await doc('streamer-kit.md'))));
  files.set('download.html', P.downloadPage(model, site));
  files.set('404.html', P.notFoundPage(site));
  files.set('status.json', JSON.stringify(P.statusJson(model, site), null, 2) + '\n');
  files.set('feed.xml', P.atomFeed(model, site));
  files.set('assets/houses.css', P.houseCss(model));
  files.set('.nojekyll', '');
  files.set(MARKER, `Generated by portal/build.mjs. This folder is replaced on every build.\n`);

  if (opts.dryRun) return { out, files, model, site, warnings: data.warnings };

  // Stage next to the target, then swap, so a host serving `out` never sees a half-written site.
  const stage = `${out}.staging-${process.pid}`;
  await rm(stage, { recursive: true, force: true });
  for (const [rel, body] of files) {
    const p = join(stage, rel);
    await mkdir(dirname(p), { recursive: true });
    await writeFile(p, body);
  }
  await cp(join(PORTAL_DIR, 'assets'), join(stage, 'assets'), { recursive: true });
  if (fontsDir && (await exists(fontsDir))) {
    await mkdir(join(stage, 'assets', 'fonts'), { recursive: true });
    for (const f of await readdir(fontsDir)) if (/\.(woff2|txt)$/i.test(f)) await cp(join(fontsDir, f), join(stage, 'assets', 'fonts', f));
  }
  await swapInto(stage, out);
  return { out, files, model, site, warnings: data.warnings };
}

async function swapInto(stage, out) {
  if (await exists(out)) {
    const entries = await readdir(out);
    if (entries.length && !entries.includes(MARKER)) {
      await rm(stage, { recursive: true, force: true });
      throw Object.assign(new Error(`Refusing to replace ${out}: it is not empty and was not made by the portal generator (no ${MARKER} file). Choose an empty or new --out folder.`), { friendly: true });
    }
    const root = parsePath(out).root;
    if (out === root || basename(out) === '') throw new Error(`Refusing to use ${out} as the output folder.`);
    const old = `${out}.old-${process.pid}`;
    await rename(out, old);
    try {
      await rename(stage, out);
    } catch (e) {
      await rename(old, out).catch(() => {});
      throw e;
    }
    await rm(old, { recursive: true, force: true });
  } else {
    await mkdir(dirname(out), { recursive: true });
    await rename(stage, out);
  }
}

// Signature of the inputs, so --watch only rebuilds when something changed.
export async function inputSignature(paths) {
  const parts = [];
  for (const p of paths) {
    try {
      const s = await stat(p);
      parts.push(`${p}:${s.mtimeMs}:${s.size}`);
    } catch {
      parts.push(`${p}:-`);
    }
  }
  return parts.join('|');
}
