'use strict';

// Filesystem helpers with progress and cancellation. Node built-ins only.

const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const crypto = require('crypto');
const { pipeline } = require('stream/promises');
const { Transform } = require('stream');

function abortError() {
  const e = new Error('Cancelled.');
  e.name = 'AbortError';
  e.code = 'ABORT_ERR';
  return e;
}

function throwIfAborted(signal) {
  if (signal && signal.aborted) throw abortError();
}

async function exists(p) {
  try {
    await fsp.access(p);
    return true;
  } catch {
    return false;
  }
}

async function isDir(p) {
  try {
    return (await fsp.stat(p)).isDirectory();
  } catch {
    return false;
  }
}

async function isFile(p) {
  try {
    return (await fsp.stat(p)).isFile();
  } catch {
    return false;
  }
}

// Lists every file below dir (no symlink following): [{ rel, abs, size }].
async function listFiles(dir, signal) {
  const out = [];
  async function walk(abs, rel) {
    throwIfAborted(signal);
    const entries = await fsp.readdir(abs, { withFileTypes: true });
    for (const e of entries) {
      const a = path.join(abs, e.name);
      const r = rel ? path.join(rel, e.name) : e.name;
      if (e.isDirectory()) await walk(a, r);
      else if (e.isFile()) out.push({ rel: r, abs: a, size: (await fsp.stat(a)).size });
    }
  }
  await walk(dir, '');
  return out;
}

async function freeBytes(dir) {
  // Walk up to the nearest existing folder (the target may not exist yet).
  let p = path.resolve(dir);
  for (;;) {
    if (await exists(p)) break;
    const up = path.dirname(p);
    if (up === p) break;
    p = up;
  }
  const s = await fsp.statfs(p);
  return Number(s.bavail) * Number(s.bsize);
}

function progressStream(onBytes, signal) {
  return new Transform({
    transform(chunk, _enc, cb) {
      if (signal && signal.aborted) return cb(abortError());
      onBytes(chunk.length);
      cb(null, chunk);
    }
  });
}

// Copies every file of src into dest (which must not exist), reporting bytes as they move.
// Files are copied in chunks so a large DLL or save still shows progress.
async function copyTree(src, dest, { onProgress, signal, files } = {}) {
  const list = files || (await listFiles(src, signal));
  const total = list.reduce((s, f) => s + f.size, 0);
  let done = 0;
  let last = 0;
  const report = (force) => {
    const now = Date.now();
    if (onProgress && (force || now - last > 120)) {
      last = now;
      onProgress({ done, total });
    }
  };
  await fsp.mkdir(dest, { recursive: true });
  for (const f of list) {
    throwIfAborted(signal);
    const target = path.join(dest, f.rel);
    await fsp.mkdir(path.dirname(target), { recursive: true });
    await pipeline(
      fs.createReadStream(f.abs),
      progressStream((n) => {
        done += n;
        report(false);
      }, signal),
      fs.createWriteStream(target)
    );
    const st = await fsp.stat(f.abs);
    await fsp.utimes(target, st.atime, st.mtime).catch(() => {});
    // Keep execute bits off Windows; on Windows chmod would only copy the read-only flag, which is skipped.
    if (process.platform !== 'win32') await fsp.chmod(target, st.mode & 0o777).catch(() => {});
  }
  report(true);
  return { files: list.length, bytes: total };
}

async function sha256File(file) {
  const h = crypto.createHash('sha256');
  await pipeline(fs.createReadStream(file), h);
  return h.digest('hex');
}

async function removeTree(p) {
  await fsp.rm(p, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 });
}

async function readJsonFile(file, fallback) {
  try {
    return JSON.parse((await fsp.readFile(file, 'utf8')).replace(/^\uFEFF/, ''));
  } catch {
    return fallback;
  }
}

// Writes through a temp file + rename so a crash never leaves a half-written file.
async function writeFileAtomic(file, data) {
  await fsp.mkdir(path.dirname(file), { recursive: true });
  const tmp = `${file}.${process.pid}.${Date.now()}.tmp`;
  try {
    await fsp.writeFile(tmp, data);
    await fsp.rename(tmp, file);
  } catch (e) {
    await fsp.rm(tmp, { force: true }).catch(() => {});
    throw e;
  }
}

module.exports = {
  abortError,
  throwIfAborted,
  exists,
  isDir,
  isFile,
  listFiles,
  freeBytes,
  copyTree,
  sha256File,
  removeTree,
  readJsonFile,
  writeFileAtomic,
  progressStream
};
