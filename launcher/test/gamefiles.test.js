// The "Server game files" check: lib/clrmeta.js (the .NET metadata reader) on synthetic assemblies built
// here, lib/gamefiles.js on temporary server folders, the Doctor step and the log explainer rule.
// The real Oxide-patched Assembly-CSharp.dll is read only when the compile-check cache has it.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const fsp = require('fs/promises');
const os = require('os');
const path = require('path');
const C = require('../lib/clrmeta');
const GF = require('../lib/gamefiles');
const DOC = require('../lib/doctor');
const GL = require('../lib/shared/gamelog');

// ---------------------------------------------------------------- a tiny synthetic assembly
//
// A PE32 file with one section that holds a CLI header and a metadata root with #~, #Strings, #GUID and
// #Blob streams. Tables: Module (1 row), optionally TypeRef rows (to prove earlier tables are skipped
// with the right row size), Assembly (1 row) and one AssemblyRef row per reference.
// opts.bigStrings sets the 4-byte #Strings index flag; opts.uncompressed names the stream "#-" and adds
// the extra 4 bytes after the row counts.
function buildAssembly({ name = 'Assembly-CSharp', refs = [], typeRefs = 0, bigStrings = false, uncompressed = false } = {}) {
  const strings = [Buffer.from([0])];
  let strLen = 1;
  const str = (s) => {
    const at = strLen;
    const b = Buffer.from(s + '\0', 'utf8');
    strings.push(b);
    strLen += b.length;
    return at;
  };
  const si = bigStrings ? 4 : 2;
  const u16 = (v) => { const b = Buffer.alloc(2); b.writeUInt16LE(v); return b; };
  const u32 = (v) => { const b = Buffer.alloc(4); b.writeUInt32LE(v); return b; };
  const sIdx = (v) => (si === 4 ? u32(v) : u16(v));

  const rows = [];
  // Module: Generation, Name, Mvid, EncId, EncBaseId
  rows.push(Buffer.concat([u16(0), sIdx(str(`${name}.dll`)), u16(1), u16(0), u16(0)]));
  // TypeRef: ResolutionScope (coded, 2 bytes), TypeName, TypeNamespace
  for (let i = 0; i < typeRefs; i++) rows.push(Buffer.concat([u16(((i % 3) << 2) | 2), sIdx(str(`T${i}`)), sIdx(str('NS'))]));
  // Assembly: HashAlgId, Major, Minor, Build, Revision, Flags, PublicKey, Name, Culture
  rows.push(Buffer.concat([u32(0x8004), u16(1), u16(2), u16(3), u16(4), u32(0), u16(0), sIdx(str(name)), sIdx(0)]));
  // AssemblyRef: Major, Minor, Build, Revision, Flags, PublicKeyOrToken, Name, Culture, HashValue
  for (const r of refs) {
    const [n, v = '1.0.0.0'] = Array.isArray(r) ? r : [r];
    const [a, b, c, d] = v.split('.').map(Number);
    rows.push(Buffer.concat([u16(a), u16(b), u16(c), u16(d), u32(0), u16(0), sIdx(str(n)), sIdx(0), u16(0)]));
  }
  const validLo = 1 | (typeRefs ? 1 << 1 : 0);
  const validHi = (1 << (0x20 - 32)) | (refs.length ? 1 << (0x23 - 32) : 0);
  const counts = [u32(1)];
  if (typeRefs) counts.push(u32(typeRefs));
  counts.push(u32(1));
  if (refs.length) counts.push(u32(refs.length));
  const heap = (bigStrings ? 1 : 0) | (uncompressed ? 0x40 : 0);
  const tHeader = Buffer.concat([u32(0), Buffer.from([2, 0, heap, 1]), u32(validLo), u32(validHi), u32(0), u32(0)]);
  const pad4 = (b) => Buffer.concat([b, Buffer.alloc((4 - (b.length % 4)) % 4)]);
  const tables = pad4(Buffer.concat([tHeader, ...counts, ...(uncompressed ? [u32(0)] : []), ...rows]));
  const strHeap = pad4(Buffer.concat(strings));
  const guid = Buffer.alloc(16, 0xab);
  const blob = pad4(Buffer.from([0]));
  const streams = [[uncompressed ? '#-' : '#~', tables], ['#Strings', strHeap], ['#GUID', guid], ['#Blob', blob]];
  const ver = Buffer.alloc(12);
  ver.write('v2.0.50727');
  const headerLen = (n) => 8 + ((n.length + 4) & ~3);
  let off = 16 + ver.length + 4 + streams.reduce((s, [n]) => s + headerLen(n), 0);
  const heads = [];
  const bodies = [];
  for (const [n, b] of streams) {
    const nm = Buffer.alloc((n.length + 4) & ~3);
    nm.write(n);
    heads.push(Buffer.concat([u32(off), u32(b.length), nm]));
    bodies.push(b);
    off += b.length;
  }
  const md = Buffer.concat([u32(0x424a5342), u16(1), u16(1), u32(0), u32(ver.length), ver, u16(0), u16(streams.length), ...heads, ...bodies]);

  const SEC_RVA = 0x2000;
  const SEC_RAW = 0x200;
  const cli = Buffer.alloc(72);
  cli.writeUInt32LE(72, 0);
  cli.writeUInt16LE(2, 4);
  cli.writeUInt16LE(5, 6);
  cli.writeUInt32LE(SEC_RVA + 72, 8);
  cli.writeUInt32LE(md.length, 12);
  const section = pad4(Buffer.concat([cli, md]));
  const rawSize = Math.ceil(section.length / 0x200) * 0x200;

  const file = Buffer.alloc(SEC_RAW + rawSize);
  file.writeUInt16LE(0x5a4d, 0);
  file.writeUInt32LE(0x40, 0x3c);
  file.writeUInt32LE(0x00004550, 0x40);
  const coff = 0x44;
  file.writeUInt16LE(0x14c, coff);
  file.writeUInt16LE(1, coff + 2);
  file.writeUInt16LE(224, coff + 16);
  file.writeUInt16LE(0x2102, coff + 18);
  const opt = coff + 20;
  file.writeUInt16LE(0x10b, opt);
  file.writeUInt32LE(16, opt + 92);
  file.writeUInt32LE(SEC_RVA, opt + 96 + 14 * 8);
  file.writeUInt32LE(72, opt + 96 + 14 * 8 + 4);
  const sec = opt + 224;
  file.write('.text', sec);
  file.writeUInt32LE(section.length, sec + 8);
  file.writeUInt32LE(SEC_RVA, sec + 12);
  file.writeUInt32LE(rawSize, sec + 16);
  file.writeUInt32LE(SEC_RAW, sec + 20);
  section.copy(file, SEC_RAW);
  return file;
}

const REFS = [['UnityEngine', '0.0.0.0'], ['mscorlib', '2.0.0.0'], ['System.Core', '3.5.0.0'], ['System', '2.0.0.0'], ['uLink', '0.0.0.0'], ['Newtonsoft.Json', '4.5.0.0'], ['Mono.Security', '2.0.0.0'], ['Oxide.Core', '2.0.4077.0'], ['Assembly-CSharp', '0.0.0.0']];

async function tmpdir() {
  return fsp.mkdtemp(path.join(os.tmpdir(), 'realm-gamefiles-'));
}

async function makeServer(dir, { dlls = [], acs = buildAssembly({ refs: REFS }), data = 'ROK_Data' } = {}) {
  const managed = path.join(dir, data, 'Managed');
  await fsp.mkdir(managed, { recursive: true });
  if (acs) await fsp.writeFile(path.join(managed, 'Assembly-CSharp.dll'), acs);
  for (const d of dlls) await fsp.writeFile(path.join(managed, d), 'MZ');
  return managed;
}

// ---------------------------------------------------------------- the metadata reader

test('clrmeta reads the Assembly row and every AssemblyRef of a synthetic assembly', () => {
  const r = C.readAssemblyInfo(buildAssembly({ name: 'Tiny', refs: [['UnityEngine', '0.0.0.0'], ['Oxide.Core', '2.0.4077.0']] }));
  assert.equal(r.runtime, 'v2.0.50727');
  assert.deepEqual(r.assembly, { name: 'Tiny', version: '1.2.3.4', culture: '' });
  assert.deepEqual(r.references, [
    { name: 'UnityEngine', version: '0.0.0.0', culture: '' },
    { name: 'Oxide.Core', version: '2.0.4077.0', culture: '' }
  ]);
});

test('clrmeta sizes the tables before AssemblyRef: TypeRef rows, 4-byte string indexes, the "#-" stream', () => {
  for (const opts of [{ typeRefs: 5 }, { bigStrings: true }, { typeRefs: 3, bigStrings: true }, { uncompressed: true, typeRefs: 2 }]) {
    const r = C.readAssemblyInfo(buildAssembly({ refs: REFS, ...opts }));
    assert.deepEqual(r.references.map((x) => x.name), REFS.map((x) => x[0]), JSON.stringify(opts));
    assert.equal(r.assembly.name, 'Assembly-CSharp');
  }
  assert.deepEqual(C.readAssemblyInfo(buildAssembly({ refs: [] })).references, []);
});

test('clrmeta refuses non-.NET, damaged and truncated files with a ClrMetaError, never a crash', () => {
  assert.throws(() => C.readAssemblyInfo(Buffer.from('hello')), C.ClrMetaError);
  assert.throws(() => C.readAssemblyInfo(Buffer.alloc(4096)), /no MZ header/);
  const good = buildAssembly({ refs: REFS, typeRefs: 4 });
  const noCli = Buffer.from(good);
  noCli.writeUInt32LE(0, 0x44 + 20 + 96 + 14 * 8);
  assert.throws(() => C.readAssemblyInfo(noCli), /not a \.NET assembly/);
  const noBsjb = Buffer.from(good);
  noBsjb.writeUInt32LE(0, 0x200 + 72);
  assert.throws(() => C.readAssemblyInfo(noBsjb), /BSJB/);
  assert.throws(() => C.readAssemblyInfo('not a buffer'), C.ClrMetaError);
  // Every truncation and many single-byte changes either parse or fail cleanly.
  for (let n = 0; n < good.length; n += 7) {
    try {
      C.readAssemblyInfo(good.subarray(0, n));
    } catch (e) {
      assert.ok(e instanceof C.ClrMetaError, `truncated at ${n}: ${e.stack}`);
    }
  }
  let seed = 12345;
  const rnd = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);
  for (let i = 0; i < 3000; i++) {
    const b = Buffer.from(good);
    const at = Math.floor(rnd() * b.length);
    b[at] = Math.floor(rnd() * 256);
    try {
      C.readAssemblyInfo(b);
    } catch (e) {
      assert.ok(e instanceof C.ClrMetaError, `byte ${at}: ${e.stack}`);
    }
  }
});

const CACHED = path.join(os.homedir(), '.cache', 'realm-compile-check', 'oxide', 'ROK_Data', 'Managed', 'Assembly-CSharp.dll');
test('clrmeta reads the real Oxide-patched Assembly-CSharp.dll', { skip: fs.existsSync(CACHED) ? false : `the compile-check cache is absent (${CACHED}); synthetic assemblies cover the reader` }, () => {
  const r = C.readAssemblyInfo(fs.readFileSync(CACHED));
  assert.equal(r.assembly.name, 'Assembly-CSharp');
  const names = r.references.map((x) => x.name);
  for (const n of ['mscorlib', 'UnityEngine', 'uLink', 'Oxide.Core', 'System.Core']) assert.ok(names.includes(n), `${n} in ${names.join(', ')}`);
  // Only the game's own assemblies are checked: UnityEngine and uLink are not in the Oxide zip's Managed folder.
  const cmp = GF.compareReferences(r, fs.readdirSync(path.dirname(CACHED)));
  assert.ok(cmp.missing.some((m) => m.name === 'UnityEngine'));
  assert.ok(!cmp.missing.some((m) => m.name === 'Oxide.Core'), 'Oxide.Core.dll ships in the Oxide zip');
  assert.ok(!cmp.missing.some((m) => GF.isFrameworkAssembly(m.name)));
});

// ---------------------------------------------------------------- the comparison and the check

test('compareReferences ignores framework assemblies and the assembly itself, matches names case-insensitively', () => {
  assert.ok(GF.isFrameworkAssembly('mscorlib'));
  assert.ok(GF.isFrameworkAssembly('System'));
  assert.ok(GF.isFrameworkAssembly('System.Windows.Forms'));
  assert.ok(GF.isFrameworkAssembly('Mono.Security'));
  assert.ok(!GF.isFrameworkAssembly('SystemX'));
  assert.ok(!GF.isFrameworkAssembly('UnityEngine'));
  assert.ok(!GF.isFrameworkAssembly('Newtonsoft.Json'));
  const info = C.readAssemblyInfo(buildAssembly({ refs: REFS }));
  const cmp = GF.compareReferences(info, ['unityengine.DLL', 'Assembly-CSharp.dll', 'Oxide.Core.dll'], { steamNames: ['uLink.dll'] });
  assert.deepEqual(cmp.checked, ['UnityEngine', 'uLink', 'Newtonsoft.Json', 'Oxide.Core']);
  assert.deepEqual(cmp.ignored, ['mscorlib', 'System.Core', 'System', 'Mono.Security']);
  assert.deepEqual(cmp.missing, [
    { name: 'uLink', file: 'uLink.dll', version: '0.0.0.0', inSteam: true },
    { name: 'Newtonsoft.Json', file: 'Newtonsoft.Json.dll', version: '4.5.0.0', inSteam: false }
  ]);
});

test('checkGameFiles: complete server is Good; missing DLLs are a Problem with a copy-only-these fix', async () => {
  const base = await tmpdir();
  const root = path.join(base, 'server');
  const all = ['UnityEngine.dll', 'uLink.dll', 'Newtonsoft.Json.dll', 'Oxide.Core.dll'];
  await makeServer(root, { dlls: all });
  const ok = await GF.checkGameFiles(root);
  assert.equal(ok.status, 'ok', ok.detail);
  assert.equal(ok.references, 4);
  assert.match(ok.detail, /all of them are in ROK_Data\\Managed/);

  const managed = path.join(root, 'ROK_Data', 'Managed');
  await fsp.rm(path.join(managed, 'uLink.dll'));
  await fsp.rm(path.join(managed, 'Newtonsoft.Json.dll'));
  const steam = path.join(base, 'steam');
  await makeServer(steam, { dlls: ['uLink.dll'] });
  const bad = await GF.checkGameFiles(root, { steamRoot: steam });
  assert.equal(bad.status, 'bad');
  assert.deepEqual(bad.missing.map((m) => [m.file, m.inSteam]), [['uLink.dll', true], ['Newtonsoft.Json.dll', false]]);
  assert.match(bad.detail, /2 assemblies .* missing from ROK_Data\\Managed: uLink\.dll, Newtonsoft\.Json\.dll/);
  assert.match(bad.detail, /TypeInitializationException for CodeHatch\.Networking\.Events\.EventManager/);
  assert.match(bad.fix, /Copy only the missing files \(uLink\.dll, Newtonsoft\.Json\.dll\) from the Steam copy/);
  assert.match(bad.fix, /Do not copy or overwrite Assembly-CSharp\.dll: the server's copy is the Oxide-patched one/);
  assert.match(bad.fix, /Newtonsoft\.Json\.dll is missing from the Steam copy too: .*Verify integrity of game files/);

  // Without a Steam copy the fix still names Verify integrity; the check never writes anything.
  const before = fs.readdirSync(managed).sort();
  const noSteam = await GF.checkGameFiles(root);
  assert.match(noSteam.fix, /Verify integrity of game files/);
  // Unknown to the Steam copy: maybe this build never shipped the file, so only a warning, never a blocker.
  assert.equal(noSteam.status, 'warn');
  assert.match(noSteam.fix, /starts normally/);
  assert.deepEqual(fs.readdirSync(managed).sort(), before);
  // The same folder as the Steam copy is not compared with itself.
  assert.equal((await GF.checkGameFiles(root, { steamRoot: root })).missing.every((m) => m.inSteam === undefined), true);
});

test('checkGameFiles: no data folder, no Assembly-CSharp.dll, a damaged one, no folder at all', async () => {
  const base = await tmpdir();
  const empty = path.join(base, 'empty');
  await fsp.mkdir(empty);
  const r1 = await GF.checkGameFiles(empty);
  assert.equal(r1.status, 'bad');
  assert.match(r1.detail, /No ROK_Data\\Managed folder/);

  const noDll = path.join(base, 'nodll');
  await makeServer(noDll, { acs: null });
  const r2 = await GF.checkGameFiles(noDll);
  assert.equal(r2.status, 'bad');
  assert.match(r2.detail, /Assembly-CSharp\.dll is missing/);

  const damaged = path.join(base, 'damaged');
  await makeServer(damaged, { acs: buildAssembly({ refs: REFS }).subarray(0, 700) });
  const r3 = await GF.checkGameFiles(damaged);
  assert.equal(r3.status, 'bad');
  assert.match(r3.detail, /Assembly-CSharp\.dll is damaged/);

  assert.equal((await GF.checkGameFiles(null)).status, 'skip');
  // Another *_Data folder is used when there is no ROK_Data.
  const other = path.join(base, 'other');
  await makeServer(other, { data: 'Game_Data', dlls: ['UnityEngine.dll', 'uLink.dll', 'Newtonsoft.Json.dll', 'Oxide.Core.dll'] });
  assert.equal(await GF.findDataFolder(other), 'Game_Data');
  assert.equal((await GF.checkGameFiles(other)).status, 'ok');
});

// ---------------------------------------------------------------- the Doctor step and the log rule

const inst = { id: 's1', ports: { game: 7350, query: 27015 }, network: 'local' };
const deps = (over = {}) => ({
  listeners: async () => ({ supported: true, list: [] }),
  a2s: async () => ({ ok: false, error: 'timeout' }),
  steam: async () => ({ running: true, signedIn: true }),
  game: async () => ({ installed: true, library: 'D:\\SteamLibrary' }),
  firewall: async () => ({ supported: false, present: [] }),
  tcp: async () => ({ ok: true }),
  ...over
});

test('Doctor: "Server game files" comes first and, when DLLs are missing, is the verdict', async () => {
  const bad = { status: 'bad', detail: '1 assembly that Assembly-CSharp.dll needs is missing from ROK_Data\\Managed: uLink.dll.', fix: 'Copy only the missing file (uLink.dll) ...' };
  const r = await DOC.runChecks({ inst, server: { running: false } }, deps({ gameFiles: async () => bad }));
  assert.equal(r.steps[0].id, 'game-files');
  assert.equal(r.steps[0].label, 'Server game files');
  assert.equal(r.steps[0].status, 'bad');
  assert.equal(r.verdict.status, 'bad');
  assert.match(r.verdict.title, /^Server game files: 1 assembly/);
  assert.equal(r.verdict.fix, bad.fix);

  const good = await DOC.runChecks({ inst, server: { running: false } }, deps({ gameFiles: async () => ({ status: 'ok', detail: 'all there' }) }));
  assert.equal(good.steps[0].status, 'ok');
  assert.equal(good.verdict.title.startsWith('Server process'), true);

  const threw = await DOC.runChecks({ inst, server: { running: false } }, deps({ gameFiles: async () => { throw new Error('EACCES'); } }));
  assert.equal(threw.steps[0].status, 'skip');
  assert.match(threw.steps[0].detail, /EACCES/);

  const none = await DOC.runChecks({ inst, server: { running: false } }, deps());
  assert.ok(!none.steps.some((s) => s.id === 'game-files'), 'no gameFiles dep, no step');
});

test('log explainer: TypeInitializationException for EventManager points to the game files check', () => {
  const lines = [
    'TypeInitializationException: An exception was thrown by the type initializer for CodeHatch.Networking.Events.EventManager',
    "System.TypeInitializationException: The type initializer for 'CodeHatch.Networking.Events.EventManager' threw an exception. ---> System.Reflection.ReflectionTypeLoadException",
    "The type initializer for 'CodeHatch.Networking.Events.EventManager' threw an exception."
  ];
  for (const l of lines) {
    const f = GL.classify(l);
    assert.ok(f, l);
    assert.equal(f.id, 'missing-game-files', l);
    assert.equal(f.severity, 'bad');
    assert.match(f.fix, /"Server game files" check/);
    assert.match(f.fix, /never over the Oxide-patched Assembly-CSharp\.dll/);
  }
  assert.equal(GL.classify('TypeInitializationException: An exception was thrown by the type initializer for SomethingElse'), null);
});

test('Doctor: the EventManager line in the server log is a start-up blocker', async () => {
  const scan = GL.scan('[261005-101010] [Except]  TypeInitializationException: An exception was thrown by the type initializer for CodeHatch.Networking.Events.EventManager');
  const r = await DOC.runChecks({ inst, server: { running: true, pid: 1, exe: 'ROK' }, serverScan: scan }, deps());
  const ready = r.steps.find((s) => s.id === 'ready');
  assert.equal(ready.status, 'bad');
  assert.match(ready.detail, /Game files are missing from the server/);
  assert.match(ready.fix, /Server game files/);
});
