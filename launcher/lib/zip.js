'use strict';

// Thin promise wrappers around yauzl (read) and yazl (write).

const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const { pipeline } = require('stream/promises');
const yauzl = require('yauzl');
const yazl = require('yazl');
const { abortError, throwIfAborted, progressStream } = require('./fsops');

function openZip(file) {
  return new Promise((resolve, reject) => {
    yauzl.open(file, { lazyEntries: true, autoClose: false, validateEntrySizes: true }, (err, zip) => (err ? reject(err) : resolve(zip)));
  });
}

// Reads the central directory: [{ name, size, entry }].
async function readEntries(zip) {
  return new Promise((resolve, reject) => {
    const list = [];
    zip.on('entry', (entry) => {
      list.push({ name: entry.fileName, size: entry.uncompressedSize, entry });
      zip.readEntry();
    });
    zip.once('end', () => resolve(list));
    zip.once('error', reject);
    zip.readEntry();
  });
}

async function listZip(file) {
  const zip = await openZip(file);
  try {
    return (await readEntries(zip)).map(({ name, size }) => ({ name, size }));
  } finally {
    zip.close();
  }
}

function openEntryStream(zip, entry) {
  return new Promise((resolve, reject) => zip.openReadStream(entry, (err, s) => (err ? reject(err) : resolve(s))));
}

// Extracts entries named in `targets` (Map entryName -> absolute destination). Each file is
// written to <dest>.realm-part first and renamed, so an interrupted extract leaves no torn file.
// Returns the list of destinations written, in order.
async function extractTo(file, targets, { onProgress, signal } = {}) {
  const zip = await openZip(file);
  const written = [];
  try {
    const entries = (await readEntries(zip)).filter((e) => targets.has(e.name));
    const total = entries.reduce((s, e) => s + e.size, 0);
    let done = 0;
    for (const e of entries) {
      throwIfAborted(signal);
      const dest = targets.get(e.name);
      await fsp.mkdir(path.dirname(dest), { recursive: true });
      const part = dest + '.realm-part';
      try {
        await pipeline(
          await openEntryStream(zip, e.entry),
          progressStream((n) => {
            done += n;
            if (onProgress) onProgress({ done, total });
          }, signal),
          fs.createWriteStream(part)
        );
        await fsp.rename(part, dest);
      } catch (err) {
        await fsp.rm(part, { force: true }).catch(() => {});
        throw err;
      }
      written.push(dest);
    }
    if (onProgress) onProgress({ done: total, total });
    return written;
  } finally {
    zip.close();
  }
}

// Writes a zip from files on disk plus in-memory buffers. Goes to <out>.part, renamed on success,
// removed on failure or cancel.
async function writeZip(out, files, buffers = [], { onProgress, signal } = {}) {
  const part = out + '.part';
  const zip = new yazl.ZipFile();
  const total = files.reduce((s, f) => s + f.size, 0);
  let done = 0;
  await fsp.mkdir(path.dirname(out), { recursive: true });
  const writing = pipeline(zip.outputStream, fs.createWriteStream(part));
  try {
    for (const f of files) {
      throwIfAborted(signal);
      const src = fs.createReadStream(f.abs);
      const counted = src.pipe(
        progressStream((n) => {
          done += n;
          if (onProgress) onProgress({ done, total });
        }, signal)
      );
      src.on('error', (e) => counted.destroy(e));
      // Without a listener, a read error on "counted" is an uncaught exception that kills the app.
      counted.on('error', (e) => zip.outputStream.destroy(e));
      zip.addReadStream(counted, f.name, { mtime: f.mtime || new Date(), size: f.size });
    }
    for (const b of buffers) zip.addBuffer(b.data, b.name, { mtime: new Date() });
    zip.end();
    if (signal) {
      await Promise.race([
        writing,
        new Promise((_, reject) => {
          if (signal.aborted) reject(abortError());
          signal.addEventListener('abort', () => {
            zip.outputStream.destroy(abortError());
            reject(abortError());
          }, { once: true });
        })
      ]);
    } else {
      await writing;
    }
    await fsp.rename(part, out);
  } catch (e) {
    zip.outputStream.destroy();
    await writing.catch(() => {});
    await fsp.rm(part, { force: true }).catch(() => {});
    throw e;
  }
  if (onProgress) onProgress({ done: total, total });
  return out;
}

module.exports = { listZip, extractTo, writeZip };
