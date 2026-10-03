// Preview renderer. projectScene() turns a sculpture into flat-shaded polygons (orthographic camera, painter's
// order, hidden faces between blocks culled); sceneSvg() draws them with a ground, a shadow and a 1.8 m figure for
// scale; renderPreviews() rasterises the SVG in headless Chromium and writes PNGs. The SVG is only an intermediate.
//
// What it shows: block cells, the ASSUMED shape geometry of shapes.mjs, and either the block colours ("painted")
// or materials.json previewColour guesses ("unpainted"). It does not show the game's textures, bevels or lighting.
import fs from 'node:fs';
import path from 'node:path';
import { blockFaces, insideBlock } from './shapes.mjs';
import { hexToRgb, rgbToHex, pal } from './palette.mjs';
import { MATERIALS } from './voxel.mjs';
import { loadModule } from '../lib.mjs';

const BLOCK_M = 1.2;                      // metres per block (BlockManager.BLOCK_SIZE)
const FIGURE_BLOCKS = 1.8 / BLOCK_M;      // a 1.8 m person

export const VIEWS = {
  front: { yaw: 0, pitch: 10, label: 'front' },
  'three-quarter': { yaw: 38, pitch: 24, label: 'three-quarter' },
  side: { yaw: 90, pitch: 10, label: 'side' },
};

const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const norm = (a) => { const l = Math.hypot(...a) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };

function newell(poly) {
  const n = [0, 0, 0];
  for (let i = 0; i < poly.length; i++) {
    const a = poly[i], b = poly[(i + 1) % poly.length];
    n[0] += (a[1] - b[1]) * (a[2] + b[2]);
    n[1] += (a[2] - b[2]) * (a[0] + b[0]);
    n[2] += (a[0] - b[0]) * (a[1] + b[1]);
  }
  return norm(n);
}

// The outward normal of a block face: Newell's normal, flipped if a point just outside along it is inside.
function outward(prefab, rot, poly) {
  let n = newell(poly);
  const c = poly.reduce((s, p) => [s[0] + p[0] / poly.length, s[1] + p[1] / poly.length, s[2] + p[2] / poly.length], [0, 0, 0]);
  const probe = [c[0] + n[0] * 0.05, c[1] + n[1] * 0.05, c[2] + n[2] * 0.05];
  const inBox = probe.every((v) => Math.abs(v) <= 0.5);
  if (inBox && insideBlock(prefab, rot, probe)) n = [-n[0], -n[1], -n[2]];
  return n;
}

// Axis direction (0..5 as DIRS order) of a face that fully covers one side of its cell, else -1.
const AXES = [[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]];
function fullSide(poly, n) {
  for (let d = 0; d < 6; d++) {
    const a = AXES[d];
    if (dot(n, a) < 0.999) continue;
    const ax = a.findIndex((v) => v !== 0);
    if (!poly.every((p) => Math.abs(p[ax] - 0.5 * a[ax]) < 1e-6)) return -1;
    const others = [0, 1, 2].filter((i) => i !== ax);
    let area = 0;
    for (let i = 0; i < poly.length; i++) {
      const p = poly[i], q = poly[(i + 1) % poly.length];
      area += p[others[0]] * q[others[1]] - q[others[0]] * p[others[1]];
    }
    return Math.abs(Math.abs(area / 2) - 1) < 1e-6 ? d : -1;
  }
  return -1;
}

const shapeCache = new Map();
function shapeFaces(prefab, rot) {
  const k = `${prefab}/${rot}`;
  if (!shapeCache.has(k)) {
    shapeCache.set(k, blockFaces(prefab, rot).map((poly) => {
      const n = outward(prefab, rot, poly);
      return { poly, n, side: fullSide(poly, n) };
    }));
  }
  return shapeCache.get(k);
}

const shade = (hex, f) => rgbToHex(hexToRgb(hex).map((c) => c * f));
const mix = (a, b, t) => rgbToHex(hexToRgb(a).map((c, i) => c + (hexToRgb(b)[i] - c) * t));

/** Polygons in screen space for one view; also the projected figure and ground. */
export function projectScene(s, { yaw = 0, pitch = 20, mode = 'painted' } = {}) {
  const matColour = new Map(MATERIALS.map((m) => [m.id, m.previewColour]));
  const cells = new Map();
  for (const b of s.blocks) cells.set(`${b[0]},${b[1]},${b[2]}`, b);
  const fullSides = (b) => new Set(shapeFaces(b[4], b[5]).map((f) => f.side).filter((d) => d >= 0));
  const sidesCache = new Map();
  const sidesOf = (b) => { const k = `${b[4]}/${b[5]}`; if (!sidesCache.has(k)) sidesCache.set(k, fullSides(b)); return sidesCache.get(k); };

  const cx = s.size[0] / 2, cz = s.size[2] / 2;
  const cy = s.size[1] / 2;
  const ry = (yaw * Math.PI) / 180, rx = (pitch * Math.PI) / 180;
  const view = (p) => {
    const x = p[0] - cx, y = p[1] - cy, z = p[2] - cz;
    const x1 = Math.cos(ry) * x - Math.sin(ry) * z, z1 = Math.sin(ry) * x + Math.cos(ry) * z;
    const y2 = Math.cos(rx) * y + Math.sin(rx) * z1, z2 = -Math.sin(rx) * y + Math.cos(rx) * z1;
    return [x1, -y2, z2];
  };
  const viewDir = (n) => view([n[0] + cx, n[1] + cy, n[2] + cz]).map((v, i) => v - view([cx, cy, cz])[i]);
  const light = norm([-0.5, 0.85, -0.6]);

  const polys = [];
  for (const b of s.blocks) {
    const base = mode === 'painted' ? b[6] : (matColour.get(b[3]) || '#888888');
    for (const f of shapeFaces(b[4], b[5])) {
      if (f.side >= 0) {
        const d = AXES[f.side];
        const nb = cells.get(`${b[0] + d[0]},${b[1] + d[1]},${b[2] + d[2]}`);
        if (nb && sidesOf(nb).has(f.side ^ 1)) continue;          // the neighbour's full face covers this one
      }
      const vn = viewDir(f.n);
      if (vn[2] > 1e-6) continue;                                   // faces away from the camera
      const pts = f.poly.map((p) => view([p[0] + b[0] + 0.5, p[1] + b[1] + 0.5, p[2] + b[2] + 0.5]));
      const depth = pts.reduce((a, p) => a + p[2], 0) / pts.length;
      const lit = 0.5 + 0.5 * Math.max(0, dot(f.n, light)) + (f.n[1] > 0.5 ? 0.06 : 0);
      polys.push({ pts, depth, fill: shade(base, Math.min(1.12, lit)), stroke: shade(base, lit * 0.62) });
    }
  }
  polys.sort((a, b) => b.depth - a.depth);

  // A person standing beside the front-right corner, and the ground disc.
  const fx = s.size[0] + 1.2, fz = -0.6;
  const fig = [[-0.22, 0], [0.22, 0], [0.26, FIGURE_BLOCKS * 0.55], [0.18, FIGURE_BLOCKS * 0.8], [0.12, FIGURE_BLOCKS * 0.86],
    [0.12, FIGURE_BLOCKS], [-0.12, FIGURE_BLOCKS], [-0.12, FIGURE_BLOCKS * 0.86], [-0.18, FIGURE_BLOCKS * 0.8], [-0.26, FIGURE_BLOCKS * 0.55]];
  const facing = -ry;
  const figure = fig.map(([u, h]) => view([fx + Math.cos(facing) * u, h, fz + Math.sin(facing) * u]));
  const groundR = Math.hypot(s.size[0], s.size[2]) * 0.62 + 2.4;
  const ground = [];
  for (let i = 0; i < 48; i++) {
    const a = (i / 48) * Math.PI * 2;
    ground.push(view([cx + Math.cos(a) * groundR, 0, cz + Math.sin(a) * groundR]));
  }
  const shadow = [];
  for (let i = 0; i < 32; i++) {
    const a = (i / 32) * Math.PI * 2;
    shadow.push(view([cx + 0.6 + Math.cos(a) * (s.size[0] * 0.62 + 0.6), 0.01, cz + 0.6 + Math.sin(a) * (s.size[2] * 0.62 + 0.6)]));
  }
  return { polys, figure, ground, shadow };
}

/** An SVG of one view, width x height pixels. */
export function sceneSvg(s, viewName = 'three-quarter', { width = 560, height = 560, mode = 'painted' } = {}) {
  const v = VIEWS[viewName];
  const sc = projectScene(s, { yaw: v.yaw, pitch: v.pitch, mode });
  const all = [...sc.polys.flatMap((p) => p.pts), ...sc.figure, ...sc.ground];
  const xs = all.map((p) => p[0]), ys = all.map((p) => p[1]);
  const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
  const pad = 0.08;
  const k = Math.min((width * (1 - 2 * pad)) / (maxX - minX), (height * (1 - 2 * pad)) / (maxY - minY));
  const ox = (width - (maxX - minX) * k) / 2 - minX * k, oy = (height - (maxY - minY) * k) / 2 - minY * k;
  const P = (pts) => pts.map((p) => `${(p[0] * k + ox).toFixed(1)},${(p[1] * k + oy).toFixed(1)}`).join(' ');
  const sky0 = pal('Night'), sky1 = pal('Dusk'), haze = pal('Haze');
  const groundCol = mix(pal('Iron 800'), pal('Moss'), 0.35);
  const sw = Math.max(0.4, Math.min(1.2, k / 40)).toFixed(2);
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">
<defs><linearGradient id="sky" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="${sky0}"/><stop offset=".72" stop-color="${sky1}"/><stop offset="1" stop-color="${mix(sky1, haze, 0.5)}"/></linearGradient>
<radialGradient id="gr" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="${groundCol}"/><stop offset="1" stop-color="${shade(groundCol, 0.55)}"/></radialGradient></defs>
<rect width="${width}" height="${height}" fill="url(#sky)"/>
<polygon points="${P(sc.ground)}" fill="url(#gr)"/>
<polygon points="${P(sc.shadow)}" fill="${pal('Iron 950')}" opacity=".45"/>
${sc.polys.map((p) => `<polygon points="${P(p.pts)}" fill="${p.fill}" stroke="${p.stroke}" stroke-width="${sw}" stroke-linejoin="round"/>`).join('\n')}
<polygon points="${P(sc.figure)}" fill="${pal('Iron 950')}" stroke="${pal('Parchment 2')}" stroke-width="1" opacity=".9"/>
</svg>`;
}

async function launch(chromium) {
  const exe = process.env.PLAYWRIGHT_CHROMIUM || (fs.existsSync('/opt/pw-browsers/chromium') ? '/opt/pw-browsers/chromium' : undefined);
  return chromium.launch(exe ? { executablePath: exe } : {});
}

/**
 * Writes <outDir>/<id>-front.png, -three-quarter.png, -side.png (painted) and -unpainted.png (three-quarter, material
 * guesses only) for each sculpture, plus contact-sheet.png when there is more than one. Returns the files written.
 */
export async function renderPreviews(sculptures, outDir, { size = 560, contact = true } = {}) {
  const { chromium } = await loadModule('playwright');
  fs.mkdirSync(outDir, { recursive: true });
  const browser = await launch(chromium);
  const written = [];
  try {
    const page = await browser.newPage({ viewport: { width: size, height: size } });
    const shot = async (svg, file, w = size, h = size) => {
      await page.setViewportSize({ width: w, height: h });
      await page.setContent(`<!doctype html><html><body style="margin:0;background:#000">${svg}</body></html>`);
      await page.screenshot({ path: file, clip: { x: 0, y: 0, width: w, height: h } });
      written.push(file);
    };
    for (const s of sculptures) {
      for (const v of Object.keys(VIEWS)) await shot(sceneSvg(s, v, { width: size, height: size }), path.join(outDir, `${s.id}-${v}.png`));
      await shot(sceneSvg(s, 'three-quarter', { width: size, height: size, mode: 'unpainted' }), path.join(outDir, `${s.id}-unpainted.png`));
    }
    if (contact && sculptures.length > 1) {
      const cell = 300, cols = Math.min(4, sculptures.length), rows = Math.ceil(sculptures.length / cols);
      const tiles = sculptures.map((s, i) => {
        const inner = sceneSvg(s, 'three-quarter', { width: cell, height: cell }).replace(/^<svg[^>]*>/, '').replace(/<\/svg>$/, '')
          .replace(/id="sky"/g, `id="sky${i}"`).replace(/url\(#sky\)/g, `url(#sky${i})`).replace(/id="gr"/g, `id="gr${i}"`).replace(/url\(#gr\)/g, `url(#gr${i})`);
        return `<svg x="${(i % cols) * cell}" y="${Math.floor(i / cols) * cell}" width="${cell}" height="${cell}" viewBox="0 0 ${cell} ${cell}">${inner}</svg>`;
      }).join('');
      const w = cols * cell, h = rows * cell;
      await shot(`<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}"><rect width="${w}" height="${h}" fill="${pal('Iron 950')}"/>${tiles}</svg>`,
        path.join(outDir, 'contact-sheet.png'), w, h);
    }
  } finally {
    await browser.close();
  }
  return written;
}
