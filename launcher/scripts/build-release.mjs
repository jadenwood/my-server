// Builds both Windows installers and collects them, with SHA256SUMS.txt, in <repo>\release\<version>\:
//   Realm-Steward-Setup-<version>.exe   the owner app
//   Realm-Setup-<version>.exe           the player app
// Build-Realm.bat at the repo root runs this after npm ci, npm run check and npm test.
//
// Usage: node scripts/build-release.mjs [--edition steward|player|both] [--skip-build] [--print-out-dir]
//   --skip-build     collect and hash installers already in dist\steward and dist\player (no electron-builder run)
//   --print-out-dir  print the release folder and exit
//
// It never needs administrator rights: it writes only to launcher\dist\, release\ and electron-builder's own
// per-user download cache. Nothing is uploaded or published.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const LAUNCHER = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const REPO = path.resolve(LAUNCHER, '..');

export const EDITIONS = {
  steward: { config: 'build/steward.json', who: 'the owner: install it on the server PC' },
  player: { config: 'build/player.json', who: 'players: send this one to your friends' }
};

const MIN_NODE_MAJOR = 22;
const MIN_FREE_BYTES = 2 * 1024 ** 3;

// electron-builder 25 signs nothing here but still edits the .exe icon and version with rcedit from this archive.
// The archive also holds two macOS symbolic links; unpacking those fails on Windows without administrator rights
// or Developer Mode ("Cannot create symbolic link : A required privilege is not held by the client"). We unpack it
// ourselves without the macOS folder, into the cache folder electron-builder looks in first.
export const WIN_CODE_SIGN = {
  dir: 'winCodeSign-2.6.0',
  url: 'https://github.com/electron-userland/electron-builder-binaries/releases/download/winCodeSign-2.6.0/winCodeSign-2.6.0.7z',
  sha512: 'e8b408d9df413c2dd7b346684d07b5a37b4f880dbc73bf8f63af50f62fe90fc958e39a6c32d1ee2f0bf7bd1724895af7671d5c6cdc8b94147c493c0275c1f0b4'
};

export function readVersion(launcherDir = LAUNCHER) {
  const pkg = JSON.parse(fs.readFileSync(path.join(launcherDir, 'package.json'), 'utf8'));
  if (!/^\d+\.\d+\.\d+$/.test(String(pkg.version))) throw new Error(`launcher/package.json version "${pkg.version}" is not of the form 1.2.3`);
  return pkg.version;
}

export function loadEditionConfig(edition, launcherDir = LAUNCHER) {
  const e = EDITIONS[edition];
  if (!e) throw new Error(`unknown edition "${edition}" (use steward, player or both)`);
  return JSON.parse(fs.readFileSync(path.join(launcherDir, e.config), 'utf8'));
}

// The installer's file name as electron-builder will write it (nsis.artifactName with ${version} and ${ext}).
export function installerName(cfg, version) {
  const tpl = cfg && cfg.nsis && cfg.nsis.artifactName;
  if (!tpl) throw new Error('nsis.artifactName is missing from the electron-builder config');
  const name = tpl.split('${version}').join(version).split('${ext}').join('exe');
  if (name.includes('${')) throw new Error(`nsis.artifactName "${tpl}" uses a macro this script does not resolve`);
  return name;
}

export function releaseDir(version, repoDir = REPO) {
  return path.join(repoDir, 'release', version);
}

export function sha256File(file) {
  const hash = crypto.createHash('sha256');
  const fd = fs.openSync(file, 'r');
  try {
    const buf = Buffer.alloc(1024 * 1024);
    let n;
    while ((n = fs.readSync(fd, buf, 0, buf.length, null)) > 0) hash.update(buf.subarray(0, n));
  } finally {
    fs.closeSync(fd);
  }
  return hash.digest('hex');
}

// The format of GNU sha256sum: "<hex>  <name>". `sha256sum -c SHA256SUMS.txt` checks it; on Windows compare with
// `Get-FileHash <file>` (PowerShell) or `certutil -hashfile <file> SHA256`.
export function checksumsText(entries) {
  return entries.map((e) => `${e.sha256}  ${e.name}`).join('\n') + '\n';
}

// Copies each installer into outDir and writes SHA256SUMS.txt over every installer there. outDir only ever holds
// this script's output, so anything else in it is removed first, except the names in `keep` (the other edition's
// installer when only one edition was built). files: [{ src, name }]. Returns [{ name, sha256, bytes, kept }],
// in the order of `order` (default: kept files, then built files).
export function collectRelease(outDir, files, { keep = [], order = null } = {}) {
  for (const f of files) {
    if (!fs.existsSync(f.src)) throw new Error(`expected installer is missing: ${f.src}`);
  }
  const built = files.map((f) => f.name);
  const kept = keep.filter((n) => !built.includes(n));
  fs.mkdirSync(outDir, { recursive: true });
  for (const entry of fs.readdirSync(outDir)) {
    if (!kept.includes(entry)) fs.rmSync(path.join(outDir, entry), { recursive: true, force: true });
  }
  for (const f of files) {
    const dest = path.join(outDir, f.name);
    fs.copyFileSync(f.src, dest);
    if (sha256File(dest) !== sha256File(f.src)) throw new Error(`copy of ${f.name} does not match the original`);
  }
  const present = [...kept.filter((n) => fs.existsSync(path.join(outDir, n))), ...built];
  const names = order ? order.filter((n) => present.includes(n)) : present;
  const entries = names.map((name) => {
    const file = path.join(outDir, name);
    return { name, sha256: sha256File(file), bytes: fs.statSync(file).size, kept: !built.includes(name) };
  });
  fs.writeFileSync(path.join(outDir, 'SHA256SUMS.txt'), checksumsText(entries));
  return entries;
}

// A player build without a published server list opens with no servers. Not an error: the owner builds once,
// publishes from Steward, then builds again.
export function playerConfigWarning(launcherDir = LAUNCHER) {
  let cfg;
  try {
    cfg = JSON.parse(fs.readFileSync(path.join(launcherDir, 'player', 'player-config.json'), 'utf8').replace(/^﻿/, ''));
  } catch (e) {
    return `launcher\\player\\player-config.json could not be read (${e.message}). Realm-Setup will show no servers.`;
  }
  if (!cfg.publicKey || !(cfg.manifestUrl || cfg.bundledManifest)) {
    return 'Realm-Setup has no published server list yet, so your friends would see no servers. That is fine for now. ' +
      'After Steward > Publish (START-HERE.md step 7), copy player-config.json from the publish folder ' +
      '(default G:\\RealmTest\\publish) into launcher\\player\\, then run Build-Realm.bat again before you send Realm-Setup.';
  }
  return null;
}

// Where electron-builder (app-builder) keeps its downloads. We set ELECTRON_BUILDER_CACHE to this before building,
// so the folder we prepare and the folder it reads are the same by construction.
export function electronBuilderCacheDir(env = process.env, platform = process.platform, home = os.homedir()) {
  if (env.ELECTRON_BUILDER_CACHE) return env.ELECTRON_BUILDER_CACHE;
  if (platform === 'win32') return path.join(env.LOCALAPPDATA || path.join(home, 'AppData', 'Local'), 'electron-builder', 'Cache');
  if (platform === 'darwin') return path.join(home, 'Library', 'Caches', 'electron-builder');
  return path.join(env.XDG_CACHE_HOME || path.join(home, '.cache'), 'electron-builder');
}

const sha512 = (data) => crypto.createHash('sha512').update(data).digest('hex');

// An archive electron-builder already downloaded: after a failed unpack it leaves <random>.7z in the cache folder.
function leftoverArchive(dir) {
  let names = [];
  try {
    names = fs.readdirSync(dir).filter((n) => n.endsWith('.7z'));
  } catch {
    return null;
  }
  for (const n of names) {
    try {
      const data = fs.readFileSync(path.join(dir, n));
      if (sha512(data) === WIN_CODE_SIGN.sha512) return data;
    } catch {
      // unreadable: try the next one
    }
  }
  return null;
}

// Unpacks winCodeSign without its macOS folder into the cache, once. The archive comes from an earlier
// electron-builder attempt when one is in the cache, otherwise it is downloaded. Returns 'present', 'prepared' or
// 'skipped: why'. A failure here is never fatal: electron-builder then tries its own download.
export async function ensureWinCodeSign({ cacheDir, fetchImpl = globalThis.fetch, sevenZip, log = () => {} }) {
  const base = path.join(cacheDir, 'winCodeSign');
  const final = path.join(base, WIN_CODE_SIGN.dir);
  if (fs.existsSync(path.join(final, 'rcedit-x64.exe'))) return 'present';
  const tmp = path.join(base, `.realm-${process.pid}-${Date.now()}`);
  try {
    if (!sevenZip) sevenZip = createRequire(import.meta.url)('7zip-bin').path7za;
    log(`Preparing electron-builder's Windows tools (${WIN_CODE_SIGN.dir}, 5.6 MB, once)...`);
    let data = leftoverArchive(base);
    if (!data) {
      const res = await fetchImpl(WIN_CODE_SIGN.url, { redirect: 'follow' });
      if (!res.ok) throw new Error(`download failed: HTTP ${res.status}`);
      data = Buffer.from(await res.arrayBuffer());
      if (sha512(data) !== WIN_CODE_SIGN.sha512) throw new Error('download does not match the expected SHA-512');
    }
    fs.mkdirSync(tmp, { recursive: true });
    const archive = path.join(tmp, 'winCodeSign.7z');
    fs.writeFileSync(archive, data);
    const out = path.join(tmp, 'x');
    execFileSync(sevenZip, ['x', '-bd', '-y', `-o${out}`, archive, '-xr!darwin'], { stdio: 'pipe' });
    if (!fs.existsSync(path.join(out, 'rcedit-x64.exe'))) throw new Error('rcedit-x64.exe missing after unpacking');
    fs.rmSync(final, { recursive: true, force: true });
    fs.renameSync(out, final);
    return 'prepared';
  } catch (e) {
    return `skipped: ${e.message}`;
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
}

// One plain sentence with the fix, for the errors people actually hit.
export function explainBuildError(err) {
  const msg = String((err && (err.stack || err.message)) || err);
  if (/symbolic link|privilege is not held/i.test(msg)) {
    return 'Windows refused to unpack a tool that contains symbolic links. Delete the folder %LOCALAPPDATA%\\electron-builder\\Cache\\winCodeSign and run Build-Realm.bat again; it unpacks the tool without them.';
  }
  if (/EBUSY|EPERM|resource busy|being used by another process|Access is denied/i.test(msg)) {
    return 'A file in launcher\\dist or release\\ is in use. Close any Realm or Realm Steward window started from launcher\\dist, close Explorer windows showing those folders, then run Build-Realm.bat again.';
  }
  if (/ENOSPC|no space left/i.test(msg)) return 'The disk is full. Free at least 2 GB on this drive and run Build-Realm.bat again.';
  if (/ENOTFOUND|ETIMEDOUT|ECONNRESET|ECONNREFUSED|EAI_AGAIN|socket hang up|getaddrinfo|status code 40\d|status code 5\d\d/i.test(msg)) {
    return 'A download failed. The first build downloads Electron (about 115 MB) and the installer tools from github.com. Check the internet connection (and any VPN or proxy), then run Build-Realm.bat again.';
  }
  if (/wine is required|wine: /i.test(msg)) return 'Building Windows installers on Linux or macOS needs wine. On Windows nothing extra is needed: run Build-Realm.bat there.';
  return 'electron-builder failed. The lines above say why. Run Build-Realm.bat again once; if it fails the same way, copy the window\'s text into an issue.';
}

function freeBytes(dir) {
  try {
    const s = fs.statfsSync(dir);
    return s.bavail * s.bsize;
  } catch {
    return null;
  }
}

function onPath(cmd) {
  try {
    execFileSync(process.platform === 'win32' ? 'where' : 'which', [cmd], { stdio: 'ignore' });
    return true;
  } catch {
    return false;
  }
}

function mb(bytes) {
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

async function buildEdition(edition, version) {
  const require = createRequire(import.meta.url);
  const eb = require('electron-builder');
  const cfg = loadEditionConfig(edition);
  const output = `dist/${edition}`;
  fs.rmSync(path.join(LAUNCHER, output), { recursive: true, force: true });
  await eb.build({
    projectDir: LAUNCHER,
    targets: eb.Platform.WINDOWS.createTarget(['nsis'], eb.Arch.x64),
    config: { ...cfg, directories: { ...cfg.directories, output } },
    publish: 'never'
  });
  const file = path.join(LAUNCHER, output, installerName(cfg, version));
  if (!fs.existsSync(file)) {
    const made = fs.existsSync(path.join(LAUNCHER, output)) ? fs.readdirSync(path.join(LAUNCHER, output)).join(', ') : 'nothing';
    throw new Error(`electron-builder finished but ${path.basename(file)} is not in launcher\\${output.replace('/', '\\')} (found: ${made})`);
  }
  return file;
}

function parseArgs(argv) {
  const o = { edition: 'both', skipBuild: false, printOutDir: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--edition') o.edition = argv[++i];
    else if (a === '--skip-build') o.skipBuild = true;
    else if (a === '--print-out-dir') o.printOutDir = true;
    else throw new Error(`unknown option ${a}`);
  }
  if (o.edition !== 'both' && !EDITIONS[o.edition]) throw new Error(`unknown edition "${o.edition}" (use steward, player or both)`);
  return o;
}

export async function main(argv = process.argv.slice(2)) {
  const opts = parseArgs(argv);
  const version = readVersion();
  const outDir = releaseDir(version);
  if (opts.printOutDir) {
    console.log(outDir);
    return 0;
  }
  const editions = opts.edition === 'both' ? ['steward', 'player'] : [opts.edition];

  const major = Number(process.versions.node.split('.')[0]);
  if (major < MIN_NODE_MAJOR) {
    console.error(`PROBLEM: Node.js ${process.versions.node} is too old. Install Node.js ${MIN_NODE_MAJOR} LTS from https://nodejs.org and run again.`);
    return 1;
  }

  const files = [];
  if (!opts.skipBuild) {
    const free = freeBytes(LAUNCHER);
    if (free != null && free < MIN_FREE_BYTES) {
      console.error(`PROBLEM: only ${mb(free)} free on the drive that holds ${LAUNCHER}. The build needs about 2 GB. Free some space and run again.`);
      return 1;
    }
    if (process.platform !== 'win32' && !onPath('wine')) {
      console.error('PROBLEM: building Windows installers on Linux or macOS needs wine (https://electron.build/multi-platform-build). On Windows nothing extra is needed.');
      return 1;
    }
    process.env.ELECTRON_BUILDER_CACHE = electronBuilderCacheDir();
    if (process.platform === 'win32') {
      const r = await ensureWinCodeSign({ cacheDir: process.env.ELECTRON_BUILDER_CACHE, log: (m) => console.log(m) });
      if (r.startsWith('skipped')) console.log(`Note: could not prepare the Windows tools ourselves (${r.slice(9)}); electron-builder will try.`);
    }
  }

  for (const [i, edition] of editions.entries()) {
    const cfg = loadEditionConfig(edition);
    const name = installerName(cfg, version);
    let src = path.join(LAUNCHER, 'dist', edition, name);
    if (!opts.skipBuild) {
      console.log(`\n--- Building ${name} (${i + 1} of ${editions.length}) ---`);
      const started = Date.now();
      try {
        src = await buildEdition(edition, version);
      } catch (e) {
        // On Windows the usual first failure is electron-builder unpacking its tools archive (macOS symbolic links).
        // Its download is then in the cache: unpack it without the macOS folder and try once more.
        const retry = process.platform === 'win32' &&
          (await ensureWinCodeSign({ cacheDir: process.env.ELECTRON_BUILDER_CACHE, fetchImpl: () => Promise.reject(new Error('no second download')), log: (m) => console.log(m) })) === 'prepared';
        if (retry) {
          console.log(`Trying ${name} again with the Windows tools unpacked...`);
          try {
            src = await buildEdition(edition, version);
          } catch (e2) {
            e = e2;
            src = null;
          }
        }
        if (!retry || !src) {
          const toolsMissing = process.platform === 'win32' && !fs.existsSync(path.join(process.env.ELECTRON_BUILDER_CACHE, 'winCodeSign', WIN_CODE_SIGN.dir, 'rcedit-x64.exe'));
          console.error(`\n${e && e.message ? e.message : e}`);
          let why = explainBuildError(e);
          if (toolsMissing && why === explainBuildError(null)) why = explainBuildError(new Error('symbolic link'));
          console.error(`\nPROBLEM: building ${name} failed. ${why}`);
          return 1;
        }
      }
      console.log(`Built ${name} in ${Math.round((Date.now() - started) / 1000)} s.`);
    }
    files.push({ edition, src, name });
  }

  const all = Object.keys(EDITIONS).map((ed) => ({ edition: ed, name: installerName(loadEditionConfig(ed), version) }));
  let entries;
  try {
    entries = collectRelease(outDir, files, {
      keep: all.filter((a) => !editions.includes(a.edition)).map((a) => a.name),
      order: all.map((a) => a.name)
    });
  } catch (e) {
    console.error(`PROBLEM: could not write ${outDir}: ${e.message}. ${explainBuildError(e)}`);
    return 1;
  }

  console.log(`\nRelease ${version} is in ${outDir}`);
  for (const e of entries) {
    const ed = all.find((a) => a.name === e.name).edition;
    console.log(`  ${e.name.padEnd(32)} ${mb(e.bytes).padStart(9)}  for ${EDITIONS[ed].who}${e.kept ? ' (from an earlier build)' : ''}`);
    console.log(`    SHA-256 ${e.sha256}`);
  }
  console.log(`  ${'SHA256SUMS.txt'.padEnd(32)} ${''.padStart(9)}  the checksums above, for anyone who wants to check a download`);
  if (editions.includes('player')) {
    const w = playerConfigWarning();
    if (w) console.log(`\nNote: ${w}`);
  }
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main().then(
    (code) => process.exit(code),
    (e) => {
      console.error(`PROBLEM: ${e && e.message ? e.message : e}`);
      process.exit(1);
    }
  );
}
