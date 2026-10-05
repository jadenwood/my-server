'use strict';

// "Server game files": does the server's <Data>\Managed folder hold every assembly that its
// Assembly-CSharp.dll references? READ-ONLY: it reads one DLL's metadata (lib/clrmeta.js) and lists
// two folders. It never copies, loads or runs anything.
//
// Why: an owner's server stopped at start-up with a TypeInitializationException for
// CodeHatch.Networking.Events.EventManager. That type's static constructor walks the exported types
// of Assembly-CSharp, and that throws as soon as one assembly Assembly-CSharp references cannot be
// loaded. The cause was game DLLs missing from ROK_Data\Managed in the server copy.
//
// Framework assemblies (mscorlib, System, System.*, Mono.*) are never reported: Unity's Mono may
// load them from <Data>\Managed or from its own Mono folder, so their absence from Managed proves
// nothing. Every other referenced assembly must be <name>.dll in Managed.

const fsp = require('fs/promises');
const path = require('path');
const C = require('./clrmeta');

const MAX_DLL_BYTES = 64 * 1024 * 1024;

// Assemblies the runtime may provide from outside <Data>\Managed (conservative on purpose).
function isFrameworkAssembly(name) {
  return /^(mscorlib|netstandard|System|System\..+|Mono\..+)$/i.test(String(name || ''));
}

// Pure: references + the file names in Managed (+ optionally the Steam copy's) -> what is missing.
function compareReferences(info, managedNames, { steamNames = null } = {}) {
  const have = new Set((managedNames || []).map((n) => String(n).toLowerCase()));
  const own = info.assembly ? info.assembly.name.toLowerCase() : 'assembly-csharp';
  const seen = new Set();
  const checked = [];
  const ignored = [];
  const missing = [];
  for (const r of info.references || []) {
    const key = r.name.toLowerCase();
    if (!r.name || seen.has(key) || key === own) continue;
    seen.add(key);
    if (isFrameworkAssembly(r.name)) {
      ignored.push(r.name);
      continue;
    }
    checked.push(r.name);
    if (have.has(`${key}.dll`) || have.has(`${key}.exe`)) continue;
    const m = { name: r.name, file: `${r.name}.dll`, version: r.version };
    if (steamNames) m.inSteam = new Set(steamNames.map((n) => String(n).toLowerCase())).has(`${key}.dll`);
    missing.push(m);
  }
  return { checked, ignored, missing };
}

async function listNames(dir) {
  try {
    return (await fsp.readdir(dir, { withFileTypes: true })).filter((e) => e.isFile()).map((e) => e.name);
  } catch {
    return null;
  }
}

// The game's data folder (ROK_Data first; any *_Data with a Managed folder otherwise).
async function findDataFolder(root) {
  let entries = [];
  try {
    entries = await fsp.readdir(root, { withFileTypes: true });
  } catch {
    return null;
  }
  const names = [];
  for (const e of entries) {
    if (!e.isDirectory() || !/_Data$/i.test(e.name)) continue;
    try {
      if ((await fsp.stat(path.join(root, e.name, 'Managed'))).isDirectory()) names.push(e.name);
    } catch {
      /* no Managed folder */
    }
  }
  if (names.includes('ROK_Data')) return 'ROK_Data';
  return names.sort()[0] || null;
}

const result = (status, detail, fix = '', extra = {}) => ({ status, detail, fix, ...extra });

// root: the server folder. steamRoot: the Steam copy of the dedicated server (optional, read-only),
// used to say whether the missing files can be copied from there.
async function checkGameFiles(root, { steamRoot = null } = {}) {
  if (!root) return result('skip', 'No server folder is set up yet.');
  const data = await findDataFolder(root);
  if (!data) {
    return result('bad', `No ROK_Data\\Managed folder in ${root}. The server copy is incomplete.`, 'Make a fresh copy of the server from Steam (Servers screen, Set up), or copy the ROK_Data folder from the Steam copy of the dedicated server.');
  }
  const managed = path.join(root, data, 'Managed');
  const dll = path.join(managed, 'Assembly-CSharp.dll');
  let buf;
  try {
    const st = await fsp.stat(dll);
    if (st.size > MAX_DLL_BYTES) return result('warn', `${data}\\Managed\\Assembly-CSharp.dll is larger than 64 MB; it was not read.`, 'Restore it from the Steam copy, then install Oxide again from Realm.', { dataFolder: data });
    buf = await fsp.readFile(dll);
  } catch (e) {
    if (e.code === 'ENOENT') {
      return result('bad', `${data}\\Managed\\Assembly-CSharp.dll is missing. The server cannot start without it.`, 'Copy Assembly-CSharp.dll from the Steam copy of the dedicated server, then install Oxide again from Realm (it patches that file).', { dataFolder: data });
    }
    return result('warn', `${data}\\Managed\\Assembly-CSharp.dll could not be read (${e.code || e.message}).`, 'Close any program that has it open, then run the checks again.', { dataFolder: data });
  }
  let info;
  try {
    info = C.readAssemblyInfo(buf);
  } catch (e) {
    return result('bad', `${data}\\Managed\\Assembly-CSharp.dll is damaged: ${e.message}.`, 'Copy Assembly-CSharp.dll from the Steam copy of the dedicated server, then install Oxide again from Realm (it patches that file).', { dataFolder: data });
  }
  const names = (await listNames(managed)) || [];
  let steamNames = null;
  if (steamRoot && path.resolve(steamRoot) !== path.resolve(root)) {
    const sd = await findDataFolder(steamRoot);
    if (sd) steamNames = await listNames(path.join(steamRoot, sd, 'Managed'));
  }
  const cmp = compareReferences(info, names, { steamNames });
  const extra = { dataFolder: data, references: cmp.checked.length, ignored: cmp.ignored.length, missing: cmp.missing };
  if (!cmp.missing.length) {
    return result('ok', `Assembly-CSharp.dll references ${cmp.checked.length} game assemblies; all of them are in ${data}\\Managed (framework assemblies such as System.* are not checked).`, '', extra);
  }
  const list = cmp.missing.map((m) => m.file).join(', ');
  const notInSteam = cmp.missing.filter((m) => m.inSteam === false).map((m) => m.file);
  let fix = `Copy only the missing file${cmp.missing.length > 1 ? 's' : ''} (${list}) from the Steam copy of the dedicated server (its ${data}\\Managed folder) into ${managed}. Do not copy or overwrite Assembly-CSharp.dll: the server's copy is the Oxide-patched one.`;
  if (notInSteam.length) fix += ` ${notInSteam.join(', ')} ${notInSteam.length > 1 ? 'are' : 'is'} missing from the Steam copy too: in Steam, right-click Reign of Kings Dedicated Server > Properties > Installed Files > Verify integrity of game files, then copy.`;
  else fix += ' If a file is missing from the Steam copy too, run Verify integrity of game files on Reign of Kings Dedicated Server in Steam first.';
  // Only a file the Steam copy has is certainly part of a working server. When the Steam copy lacks every missing
  // file too (or cannot be read), the build may simply not ship it, so this is a warning, not a start-up blocker.
  const status = cmp.missing.some((m) => m.inSteam === true) ? 'bad' : 'warn';
  if (status === 'warn') fix += ' If the server starts normally (the log shows "Server for N players started on port P."), this build may not ship these files and nothing needs doing.';
  return result(status, `${cmp.missing.length} assembl${cmp.missing.length > 1 ? 'ies' : 'y'} that Assembly-CSharp.dll needs ${cmp.missing.length > 1 ? 'are' : 'is'} missing from ${data}\\Managed: ${list}. ${status === 'bad' ? 'The server stops at start-up' : 'If it is really needed, the server stops at start-up'} (TypeInitializationException for CodeHatch.Networking.Events.EventManager).`, fix, extra);
}

module.exports = { isFrameworkAssembly, compareReferences, findDataFolder, checkGameFiles, MAX_DLL_BYTES };
