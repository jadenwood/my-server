'use strict';

// A small, read-only reader for the .NET (CLI) metadata of a PE file: enough to list the assemblies a
// DLL references (the AssemblyRef table) and its own name (the Assembly table). Used by the
// "Server game files" check (lib/gamefiles.js). Pure: it takes a Buffer and never runs, loads or
// writes anything. Every offset is bounds-checked; a damaged or non-.NET file throws a ClrMetaError
// with a short reason instead of reading past the end.
//
// Layout (ECMA-335 6th edition, Partition II): the PE optional header's data directory 14 points
// to the CLI header (II.25.3.3), whose MetaData entry points to the metadata root "BSJB"
// (II.24.2.1). The root lists the streams; "#~" (or the uncompressed "#-") holds the tables
// (II.24.2.6), "#Strings" the names. Row sizes depend on the heap-size flags, the row counts and the
// coded-index rules (II.24.2.6), so every table before AssemblyRef (0x23) is sized to find it.

class ClrMetaError extends Error {}

const fail = (why) => {
  throw new ClrMetaError(why);
};

const MAX_TABLES = 64;

// Table numbers (II.22).
const T = {
  Module: 0x00, TypeRef: 0x01, TypeDef: 0x02, FieldPtr: 0x03, Field: 0x04, MethodPtr: 0x05, MethodDef: 0x06,
  ParamPtr: 0x07, Param: 0x08, InterfaceImpl: 0x09, MemberRef: 0x0a, Constant: 0x0b, CustomAttribute: 0x0c,
  FieldMarshal: 0x0d, DeclSecurity: 0x0e, ClassLayout: 0x0f, FieldLayout: 0x10, StandAloneSig: 0x11, EventMap: 0x12,
  EventPtr: 0x13, Event: 0x14, PropertyMap: 0x15, PropertyPtr: 0x16, Property: 0x17, MethodSemantics: 0x18,
  MethodImpl: 0x19, ModuleRef: 0x1a, TypeSpec: 0x1b, ImplMap: 0x1c, FieldRVA: 0x1d, EncLog: 0x1e, EncMap: 0x1f,
  Assembly: 0x20, AssemblyProcessor: 0x21, AssemblyOS: 0x22, AssemblyRef: 0x23, AssemblyRefProcessor: 0x24,
  AssemblyRefOS: 0x25, File: 0x26, ExportedType: 0x27, ManifestResource: 0x28, NestedClass: 0x29,
  GenericParam: 0x2a, MethodSpec: 0x2b, GenericParamConstraint: 0x2c
};

// Coded indexes (II.24.2.6): the tables a tag can point to, in tag order (null = unused tag value).
const CODED = {
  TypeDefOrRef: [T.TypeDef, T.TypeRef, T.TypeSpec],
  HasConstant: [T.Field, T.Param, T.Property],
  HasCustomAttribute: [
    T.MethodDef, T.Field, T.TypeRef, T.TypeDef, T.Param, T.InterfaceImpl, T.MemberRef, T.Module, T.DeclSecurity, T.Property, T.Event,
    T.StandAloneSig, T.ModuleRef, T.TypeSpec, T.Assembly, T.AssemblyRef, T.File, T.ExportedType, T.ManifestResource, T.GenericParam,
    T.GenericParamConstraint, T.MethodSpec
  ],
  HasFieldMarshal: [T.Field, T.Param],
  HasDeclSecurity: [T.TypeDef, T.MethodDef, T.Assembly],
  MemberRefParent: [T.TypeDef, T.TypeRef, T.ModuleRef, T.MethodDef, T.TypeSpec],
  HasSemantics: [T.Event, T.Property],
  MethodDefOrRef: [T.MethodDef, T.MemberRef],
  MemberForwarded: [T.Field, T.MethodDef],
  Implementation: [T.File, T.AssemblyRef, T.ExportedType],
  CustomAttributeType: [null, null, T.MethodDef, T.MemberRef, null],
  ResolutionScope: [T.Module, T.ModuleRef, T.AssemblyRef, T.TypeRef],
  TypeOrMethodDef: [T.TypeDef, T.MethodDef]
};

// Column kinds: a number = fixed bytes; 's' string index; 'g' guid index; 'b' blob index;
// ['t', table] simple index; ['c', name] coded index. Tables 0x00..0x23 are all that is needed to
// reach AssemblyRef; Assembly (0x20) is read too.
const COLUMNS = {
  [T.Module]: [2, 's', 'g', 'g', 'g'],
  [T.TypeRef]: [['c', 'ResolutionScope'], 's', 's'],
  [T.TypeDef]: [4, 's', 's', ['c', 'TypeDefOrRef'], ['t', T.Field], ['t', T.MethodDef]],
  [T.FieldPtr]: [['t', T.Field]],
  [T.Field]: [2, 's', 'b'],
  [T.MethodPtr]: [['t', T.MethodDef]],
  [T.MethodDef]: [4, 2, 2, 's', 'b', ['t', T.Param]],
  [T.ParamPtr]: [['t', T.Param]],
  [T.Param]: [2, 2, 's'],
  [T.InterfaceImpl]: [['t', T.TypeDef], ['c', 'TypeDefOrRef']],
  [T.MemberRef]: [['c', 'MemberRefParent'], 's', 'b'],
  [T.Constant]: [2, ['c', 'HasConstant'], 'b'],
  [T.CustomAttribute]: [['c', 'HasCustomAttribute'], ['c', 'CustomAttributeType'], 'b'],
  [T.FieldMarshal]: [['c', 'HasFieldMarshal'], 'b'],
  [T.DeclSecurity]: [2, ['c', 'HasDeclSecurity'], 'b'],
  [T.ClassLayout]: [2, 4, ['t', T.TypeDef]],
  [T.FieldLayout]: [4, ['t', T.Field]],
  [T.StandAloneSig]: ['b'],
  [T.EventMap]: [['t', T.TypeDef], ['t', T.Event]],
  [T.EventPtr]: [['t', T.Event]],
  [T.Event]: [2, 's', ['c', 'TypeDefOrRef']],
  [T.PropertyMap]: [['t', T.TypeDef], ['t', T.Property]],
  [T.PropertyPtr]: [['t', T.Property]],
  [T.Property]: [2, 's', 'b'],
  [T.MethodSemantics]: [2, ['t', T.MethodDef], ['c', 'HasSemantics']],
  [T.MethodImpl]: [['t', T.TypeDef], ['c', 'MethodDefOrRef'], ['c', 'MethodDefOrRef']],
  [T.ModuleRef]: ['s'],
  [T.TypeSpec]: ['b'],
  [T.ImplMap]: [2, ['c', 'MemberForwarded'], 's', ['t', T.ModuleRef]],
  [T.FieldRVA]: [4, ['t', T.Field]],
  [T.EncLog]: [4, 4],
  [T.EncMap]: [4],
  // HashAlgId, Major, Minor, Build, Revision, Flags, PublicKey, Name, Culture
  [T.Assembly]: [4, 2, 2, 2, 2, 4, 'b', 's', 's'],
  [T.AssemblyProcessor]: [4],
  [T.AssemblyOS]: [4, 4, 4],
  // Major, Minor, Build, Revision, Flags, PublicKeyOrToken, Name, Culture, HashValue
  [T.AssemblyRef]: [2, 2, 2, 2, 4, 'b', 's', 's', 'b']
};

function need(buf, off, len, what) {
  if (!Number.isInteger(off) || off < 0 || len < 0 || off + len > buf.length) fail(`${what} is outside the file (damaged or truncated)`);
}

// The PE sections, and a function that turns an RVA into a file offset.
function readPe(buf) {
  if (!Buffer.isBuffer(buf)) fail('not a file buffer');
  need(buf, 0, 64, 'the DOS header');
  if (buf.readUInt16LE(0) !== 0x5a4d) fail('not a Windows executable (no MZ header)');
  const pe = buf.readUInt32LE(0x3c);
  need(buf, pe, 24, 'the PE header');
  if (buf.readUInt32LE(pe) !== 0x00004550) fail('not a PE file (no PE signature)');
  const sections = buf.readUInt16LE(pe + 6);
  const optSize = buf.readUInt16LE(pe + 20);
  const opt = pe + 24;
  need(buf, opt, optSize, 'the optional header');
  const magic = buf.readUInt16LE(opt);
  let dirBase;
  let dirCountOff;
  if (magic === 0x10b) {
    dirCountOff = opt + 92;
    dirBase = opt + 96;
  } else if (magic === 0x20b) {
    dirCountOff = opt + 108;
    dirBase = opt + 112;
  } else fail(`unknown optional header magic 0x${magic.toString(16)}`);
  if (dirCountOff + 4 > opt + optSize) fail('the optional header has no data directories');
  const dirCount = buf.readUInt32LE(dirCountOff);
  if (dirCount <= 14 || dirBase + 15 * 8 > opt + optSize) fail('not a .NET assembly (no CLI header directory)');
  const cliRva = buf.readUInt32LE(dirBase + 14 * 8);
  const cliSize = buf.readUInt32LE(dirBase + 14 * 8 + 4);
  if (!cliRva || cliSize < 16) fail('not a .NET assembly (empty CLI header directory)');
  const secBase = opt + optSize;
  if (sections < 1 || sections > 96) fail(`implausible section count ${sections}`);
  need(buf, secBase, sections * 40, 'the section table');
  const secs = [];
  for (let i = 0; i < sections; i++) {
    const s = secBase + i * 40;
    secs.push({ va: buf.readUInt32LE(s + 12), vsize: buf.readUInt32LE(s + 8), rawSize: buf.readUInt32LE(s + 16), raw: buf.readUInt32LE(s + 20) });
  }
  const toOffset = (rva, len, what) => {
    for (const s of secs) {
      const span = Math.max(s.vsize, s.rawSize);
      if (rva >= s.va && rva < s.va + span) {
        const off = s.raw + (rva - s.va);
        if (rva - s.va + len > s.rawSize) fail(`${what} runs past its section`);
        need(buf, off, len, what);
        return off;
      }
    }
    return fail(`${what} (RVA 0x${rva.toString(16)}) is in no section`);
  };
  return { cliRva, cliSize, toOffset };
}

function readCString(buf, off, end, what) {
  if (off < 0 || off >= end) fail(`${what} is outside its heap`);
  const stop = buf.indexOf(0, off);
  if (stop < 0 || stop >= end) fail(`${what} is not terminated`);
  return buf.toString('utf8', off, stop);
}

// The metadata root and its streams: { name: { off, size } } as file offsets.
function readStreams(buf, pe) {
  const cli = pe.toOffset(pe.cliRva, 16, 'the CLI header');
  const mdRva = buf.readUInt32LE(cli + 8);
  const mdSize = buf.readUInt32LE(cli + 12);
  if (!mdRva || mdSize < 32) fail('the CLI header has no metadata');
  const md = pe.toOffset(mdRva, mdSize, 'the metadata');
  if (buf.readUInt32LE(md) !== 0x424a5342) fail('the metadata root has no BSJB signature');
  const verLen = buf.readUInt32LE(md + 12);
  if (verLen > 255 || 16 + verLen + 4 > mdSize) fail('the metadata version string is damaged');
  const version = readCString(buf, md + 16, md + 16 + verLen + 1, 'the metadata version').replace(/\0+$/, '');
  let p = md + 16 + verLen;
  const count = buf.readUInt16LE(p + 2);
  p += 4;
  const streams = {};
  for (let i = 0; i < count; i++) {
    need(buf, p, 8, 'a stream header');
    if (p + 8 > md + mdSize) fail('the stream headers run past the metadata');
    const off = buf.readUInt32LE(p);
    const size = buf.readUInt32LE(p + 4);
    const name = readCString(buf, p + 8, Math.min(md + mdSize, p + 8 + 32), 'a stream name');
    if (off + size > mdSize) fail(`stream ${name} runs past the metadata`);
    streams[name] = { off: md + off, size };
    p += 8 + ((name.length + 4) & ~3);
  }
  return { version, streams };
}

// Reads the tables stream far enough to return the Assembly row and every AssemblyRef row.
function readAssemblyInfo(buf) {
  const pe = readPe(buf);
  const { version, streams } = readStreams(buf, pe);
  const tables = streams['#~'] || streams['#-'];
  const strings = streams['#Strings'];
  if (!tables) fail('the metadata has no tables stream');
  if (!strings) fail('the metadata has no #Strings heap');
  const end = tables.off + tables.size;
  need(buf, tables.off, 24, 'the tables header');
  const heapSizes = buf.readUInt8(tables.off + 6);
  const validLo = buf.readUInt32LE(tables.off + 8);
  const validHi = buf.readUInt32LE(tables.off + 12);
  const rows = new Array(MAX_TABLES).fill(0);
  let p = tables.off + 24;
  for (let t = 0; t < MAX_TABLES; t++) {
    const present = t < 32 ? (validLo >>> t) & 1 : (validHi >>> (t - 32)) & 1;
    if (!present) continue;
    if (p + 4 > end) fail('the table row counts run past the tables stream');
    rows[t] = buf.readUInt32LE(p);
    if (rows[t] > 0x00ffffff) fail(`implausible row count in table 0x${t.toString(16)}`);
    p += 4;
  }
  // The uncompressed "#-" form may carry 4 extra bytes after the row counts (heap-size bit 0x40).
  if (heapSizes & 0x40) p += 4;
  const strIdx = heapSizes & 1 ? 4 : 2;
  const guidIdx = heapSizes & 2 ? 4 : 2;
  const blobIdx = heapSizes & 4 ? 4 : 2;
  const simple = (t) => (rows[t] > 0xffff ? 4 : 2);
  const coded = (name) => {
    const list = CODED[name];
    const bits = Math.ceil(Math.log2(list.length));
    const max = Math.max(...list.map((t) => (t == null ? 0 : rows[t])));
    return max < 1 << (16 - bits) ? 2 : 4;
  };
  const colSize = (c) => {
    if (typeof c === 'number') return c;
    if (c === 's') return strIdx;
    if (c === 'g') return guidIdx;
    if (c === 'b') return blobIdx;
    if (c[0] === 't') return simple(c[1]);
    return coded(c[1]);
  };
  const rowSize = (t) => COLUMNS[t].reduce((s, c) => s + colSize(c), 0);
  const start = {};
  for (let t = 0; t <= T.AssemblyRef; t++) {
    start[t] = p;
    p += rows[t] * rowSize(t);
    if (p > end) fail(`table 0x${t.toString(16)} runs past the tables stream`);
  }
  const strEnd = strings.off + strings.size;
  const str = (i) => (i === 0 ? '' : readCString(buf, strings.off + i, strEnd, 'a name'));
  const readRow = (t, i) => {
    let q = start[t] + i * rowSize(t);
    const out = [];
    for (const c of COLUMNS[t]) {
      const n = colSize(c);
      out.push(n === 1 ? buf.readUInt8(q) : n === 2 ? buf.readUInt16LE(q) : buf.readUInt32LE(q));
      q += n;
    }
    return out;
  };
  let assembly = null;
  if (rows[T.Assembly] > 0) {
    const r = readRow(T.Assembly, 0);
    assembly = { name: str(r[7]), version: `${r[1]}.${r[2]}.${r[3]}.${r[4]}`, culture: str(r[8]) };
  }
  const references = [];
  for (let i = 0; i < rows[T.AssemblyRef]; i++) {
    const r = readRow(T.AssemblyRef, i);
    references.push({ name: str(r[6]), version: `${r[0]}.${r[1]}.${r[2]}.${r[3]}`, culture: str(r[7]) });
  }
  return { runtime: version, assembly, references };
}

module.exports = { ClrMetaError, readAssemblyInfo, TABLES: T };
