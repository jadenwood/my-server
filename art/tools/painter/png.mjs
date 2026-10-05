// Minimal PNG encoder and decoder for the painter (8-bit greyscale, grey+alpha, RGB and RGBA; no interlace).
// No dependencies: node:zlib does the deflate. The decoder is also the independent check of the C# encoder in
// plugins/RealmPainter.cs (plugins/docs/RealmPainter/logic-tests/decode-check.mjs).
import zlib from 'node:zlib';

const SIG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const CHANNELS = { 0: 1, 2: 3, 4: 2, 6: 4 };

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

export function crc32(buf, start = 0, end = buf.length) {
  let c = 0xffffffff;
  for (let i = start; i < end; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const out = Buffer.alloc(12 + data.length);
  out.writeUInt32BE(data.length, 0);
  out.write(type, 4, 'ascii');
  data.copy(out, 8);
  out.writeUInt32BE(crc32(out, 4, 8 + data.length), 8 + data.length);
  return out;
}

function paeth(a, b, c) {
  const p = a + b - c;
  const pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}

// Encode { width, height, data } where data has `channels` bytes per pixel (1 grey, 2 grey+alpha, 3 RGB, 4 RGBA).
// Each row gets the filter with the smallest sum of absolute values (the usual heuristic).
export function encodePng({ width, height, data, channels = 4 }) {
  const colorType = { 1: 0, 2: 4, 3: 2, 4: 6 }[channels];
  if (colorType === undefined) throw new Error(`unsupported channel count ${channels}`);
  if (!(width > 0 && height > 0) || data.length !== width * height * channels) throw new Error('bad image size');
  const stride = width * channels;
  const raw = Buffer.alloc((stride + 1) * height);
  const cand = Array.from({ length: 5 }, () => Buffer.alloc(stride));
  for (let y = 0; y < height; y++) {
    const row = y * stride;
    let best = 0, bestSum = Infinity;
    for (let f = 0; f < 5; f++) {
      let sum = 0;
      for (let x = 0; x < stride; x++) {
        const v = data[row + x];
        const a = x >= channels ? data[row + x - channels] : 0;
        const b = y > 0 ? data[row - stride + x] : 0;
        const c = x >= channels && y > 0 ? data[row - stride + x - channels] : 0;
        const p = f === 0 ? v : f === 1 ? v - a : f === 2 ? v - b : f === 3 ? v - ((a + b) >> 1) : v - paeth(a, b, c);
        const byte = p & 0xff;
        cand[f][x] = byte;
        sum += byte < 128 ? byte : 256 - byte;
      }
      if (sum < bestSum) { bestSum = sum; best = f; }
    }
    raw[y * (stride + 1)] = best;
    cand[best].copy(raw, y * (stride + 1) + 1);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; ihdr[9] = colorType; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([SIG, chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}

// Decode to { width, height, channels, colorType, data (as stored), rgba (always RGBA) }. Checks every CRC and the
// zlib stream (node:zlib verifies the Adler-32). Throws on anything it does not support.
export function decodePng(buf) {
  buf = Buffer.from(buf);
  if (buf.length < 8 || !buf.subarray(0, 8).equals(SIG)) throw new Error('not a PNG (bad signature)');
  let pos = 8, ihdr = null, ended = false;
  const idat = [];
  const chunks = [];
  while (pos < buf.length) {
    if (pos + 12 > buf.length) throw new Error('truncated chunk');
    const len = buf.readUInt32BE(pos);
    const type = buf.toString('ascii', pos + 4, pos + 8);
    if (pos + 12 + len > buf.length) throw new Error(`truncated ${type}`);
    const crc = buf.readUInt32BE(pos + 8 + len);
    if (crc !== crc32(buf, pos + 4, pos + 8 + len)) throw new Error(`bad CRC in ${type}`);
    const body = buf.subarray(pos + 8, pos + 8 + len);
    chunks.push(type);
    if (type === 'IHDR') ihdr = body;
    else if (type === 'IDAT') idat.push(body);
    else if (type === 'IEND') { ended = true; pos += 12 + len; break; }
    pos += 12 + len;
  }
  if (!ihdr) throw new Error('no IHDR');
  if (!ended) throw new Error('no IEND');
  if (chunks[0] !== 'IHDR') throw new Error('IHDR is not first');
  const width = ihdr.readUInt32BE(0), height = ihdr.readUInt32BE(4);
  const depth = ihdr[8], colorType = ihdr[9];
  if (depth !== 8) throw new Error(`bit depth ${depth} not supported`);
  if (ihdr[10] !== 0 || ihdr[11] !== 0) throw new Error('unknown compression or filter method');
  if (ihdr[12] !== 0) throw new Error('interlaced PNGs not supported');
  const channels = CHANNELS[colorType];
  if (!channels) throw new Error(`colour type ${colorType} not supported`);
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  if (raw.length !== (stride + 1) * height) throw new Error(`image data is ${raw.length} bytes, expected ${(stride + 1) * height}`);
  const data = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) {
    const f = raw[y * (stride + 1)];
    if (f > 4) throw new Error(`bad filter ${f} on row ${y}`);
    for (let x = 0; x < stride; x++) {
      const v = raw[y * (stride + 1) + 1 + x];
      const a = x >= channels ? data[y * stride + x - channels] : 0;
      const b = y > 0 ? data[(y - 1) * stride + x] : 0;
      const c = x >= channels && y > 0 ? data[(y - 1) * stride + x - channels] : 0;
      const p = f === 0 ? 0 : f === 1 ? a : f === 2 ? b : f === 3 ? (a + b) >> 1 : paeth(a, b, c);
      data[y * stride + x] = (v + p) & 0xff;
    }
  }
  const rgba = Buffer.alloc(width * height * 4);
  for (let i = 0; i < width * height; i++) {
    const s = i * channels, d = i * 4;
    if (channels === 1) { rgba[d] = rgba[d + 1] = rgba[d + 2] = data[s]; rgba[d + 3] = 255; }
    else if (channels === 2) { rgba[d] = rgba[d + 1] = rgba[d + 2] = data[s]; rgba[d + 3] = data[s + 1]; }
    else if (channels === 3) { rgba[d] = data[s]; rgba[d + 1] = data[s + 1]; rgba[d + 2] = data[s + 2]; rgba[d + 3] = 255; }
    else { data.copy(rgba, d, s, s + 4); }
  }
  return { width, height, channels, colorType, data, rgba, chunks };
}

// Box-filter downscale of RGBA by an integer factor (used to supersample renders).
export function downscale(rgba, width, height, factor) {
  const w = Math.floor(width / factor), h = Math.floor(height / factor);
  const out = Buffer.alloc(w * h * 4);
  const n = factor * factor;
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      // premultiplied average so transparent edges do not darken
      let r = 0, g = 0, b = 0, a = 0;
      for (let dy = 0; dy < factor; dy++) {
        for (let dx = 0; dx < factor; dx++) {
          const i = ((y * factor + dy) * width + (x * factor + dx)) * 4;
          const al = rgba[i + 3];
          r += rgba[i] * al; g += rgba[i + 1] * al; b += rgba[i + 2] * al; a += al;
        }
      }
      const o = (y * w + x) * 4;
      if (a > 0) { out[o] = Math.round(r / a); out[o + 1] = Math.round(g / a); out[o + 2] = Math.round(b / a); }
      out[o + 3] = Math.round(a / n);
    }
  }
  return { width: w, height: h, data: out };
}
