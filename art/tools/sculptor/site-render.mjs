// Site previews: a whole site (art/tools/sculptor/site.mjs) drawn from a camera, orthographic or perspective, plus an
// annotated plan for the plugin team. Same flat shading and assumed shapes as render.mjs; perspective views clip at a
// near plane and fade into the sky with distance. Like the piece previews, these show block cells and the tools'
// assumed shapes, not the game's textures, light or terrain.
import fs from 'node:fs';
import path from 'node:path';
import { shapeFaces, shade, mix, launch, AXES } from './render.mjs';
import { pal } from './palette.mjs';
import { MATERIALS } from './voxel.mjs';
import { siteScene } from './site.mjs';
import { loadModule } from '../lib.mjs';

const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const subv = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const norm = (a) => { const l = Math.hypot(...a) || 1; return a.map((v) => v / l); };
const rad = (d) => (d * Math.PI) / 180;
const K = (x, y, z) => `${x},${y},${z}`;

/** A camera: { kind: 'persp'|'ortho', eye: [x,y,z] in cells (persp), centre (ortho), yaw (0 looks +z, 90 looks +x),
 * pitch (down positive), fov (degrees, persp), scale (pixels per cell, ortho) }. */
export function cameraBasis(cam) {
  const t = rad(cam.yaw || 0), p = rad(cam.pitch || 0);
  const f = [Math.sin(t) * Math.cos(p), -Math.sin(p), Math.cos(t) * Math.cos(p)];
  const r = [Math.cos(t), 0, -Math.sin(t)];
  const u = [Math.sin(t) * Math.sin(p), Math.cos(p), Math.cos(t) * Math.sin(p)];
  return { f, r, u };
}

function clipNear(pts, near) {
  const out = [];
  for (let i = 0; i < pts.length; i++) {
    const a = pts[i], b = pts[(i + 1) % pts.length];
    const ia = a[2] >= near, ib = b[2] >= near;
    if (ia) out.push(a);
    if (ia !== ib) { const t = (near - a[2]) / (b[2] - a[2]); out.push([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, near]); }
  }
  return out;
}

/** Projects site blocks ([x, y, z, mat, prefab, rot, colour]) for a camera into screen polygons, far to near. */
export function projectView(blocks, cam, { width, height, haze = pal('Parchment 2'), fog = 0 } = {}) {
  const { f, r, u } = cameraBasis(cam);
  const persp = cam.kind === 'persp';
  const F = persp ? width / 2 / Math.tan(rad(cam.fov || 60) / 2) : cam.scale;
  const eye = persp ? cam.eye : subv(cam.centre, f.map((v) => v * 4000));
  const near = 0.05;
  const matColour = new Map(MATERIALS.map((m) => [m.id, m.previewColour]));
  const cells = new Map(blocks.map((b) => [K(b[0], b[1], b[2]), b]));
  const sidesCache = new Map();
  const sidesOf = (b) => {
    const k = `${b[4]}/${b[5]}`;
    if (!sidesCache.has(k)) sidesCache.set(k, new Set(shapeFaces(b[4], b[5]).map((x) => x.side).filter((d) => d >= 0)));
    return sidesCache.get(k);
  };
  const light = norm([-0.5, 0.85, -0.6]);
  const toView = (p) => { const d = subv(p, eye); return [dot(d, r), dot(d, u), dot(d, f)]; };
  const toScreen = (v) => (persp ? [width / 2 + (F * v[0]) / v[2], height / 2 - (F * v[1]) / v[2]] : [width / 2 + F * v[0], height / 2 - F * v[1]]);
  const polys = [];
  for (const b of blocks) {
    const c0 = [b[0] + 0.5, b[1] + 0.5, b[2] + 0.5];
    if (persp) { const v = toView(c0); if (v[2] < -1) continue; }
    const base = b[6] || matColour.get(b[3]) || '#888888';
    for (const face of shapeFaces(b[4], b[5])) {
      if (face.side >= 0) {
        const d = AXES[face.side];
        const nb = cells.get(K(b[0] + d[0], b[1] + d[1], b[2] + d[2]));
        if (nb && sidesOf(nb).has(face.side ^ 1)) continue;
      }
      const world = face.poly.map((p) => [p[0] + c0[0], p[1] + c0[1], p[2] + c0[2]]);
      const cen = world.reduce((a, p) => [a[0] + p[0] / world.length, a[1] + p[1] / world.length, a[2] + p[2] / world.length], [0, 0, 0]);
      if (persp ? dot(face.n, subv(eye, cen)) <= 1e-6 : dot(face.n, f) >= -1e-6) continue;
      let v = world.map(toView);
      if (persp) { v = clipNear(v, near); if (v.length < 3) continue; }
      const pts = v.map(toScreen);
      if (pts.every((p) => p[0] < -2) || pts.every((p) => p[0] > width + 2) || pts.every((p) => p[1] < -2) || pts.every((p) => p[1] > height + 2)) continue;
      const dist = persp ? Math.hypot(...subv(cen, eye)) : dot(subv(cen, eye), f);
      const lit = 0.5 + 0.5 * Math.max(0, dot(face.n, light)) + (face.n[1] > 0.5 ? 0.06 : 0);
      let fill = shade(base, Math.min(1.12, lit));
      let stroke = shade(base, lit * 0.62);
      if (fog > 0) { const t = 1 - Math.exp(-dist / fog); fill = mix(fill, haze, t); stroke = mix(stroke, haze, t); }
      polys.push({ pts, depth: dist, fill, stroke });
    }
  }
  polys.sort((a, b) => b.depth - a.depth);
  return { polys, toView, toScreen, F, eye, f };
}

/** An SVG of a site view. `overlay(proj)` may return extra SVG drawn on top. */
export function viewSvg(blocks, cam, { width = 1280, height = 720, fog = 0, overlay = null, title = '' } = {}) {
  const haze = mix(pal('Parchment 2'), pal('Iron 200'), 0.35);
  const proj = projectView(blocks, cam, { width, height, haze, fog });
  const persp = cam.kind === 'persp';
  const sky0 = mix(pal('Iron 200'), pal('Lapis'), 0.45), sky1 = mix(pal('Iron 200'), pal('Parchment'), 0.5);
  const groundNear = mix(pal('Moss'), pal('Haze'), 0.4), groundFar = mix(groundNear, haze, 0.75);
  let ground = '';
  if (persp) {
    // The horizon: a far point straight ahead at eye height.
    const far = proj.toScreen(proj.toView([cam.eye[0] + proj.f[0] * 1e6, cam.eye[1], cam.eye[2] + proj.f[2] * 1e6]));
    const hy = Math.max(-1, Math.min(height + 1, far[1]));
    ground = `<defs><linearGradient id="gd" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="${groundFar}"/><stop offset="1" stop-color="${groundNear}"/></linearGradient></defs>
<rect x="0" y="${hy.toFixed(1)}" width="${width}" height="${(height - hy).toFixed(1)}" fill="url(#gd)"/>`;
  } else {
    const all = proj.polys.flatMap((p) => p.pts);
    if (all.length) {
      const g = [[-60, -60], [60, -60], [60, 260], [-60, 260]].map(([x, z]) => proj.toScreen(proj.toView([x, 0, z])));
      ground = `<polygon points="${g.map((p) => p.map((v) => v.toFixed(1)).join(',')).join(' ')}" fill="${groundNear}"/>`;
    }
  }
  const sw = persp ? 0.6 : Math.max(0.3, Math.min(1, cam.scale / 14)).toFixed(2);
  const body = proj.polys.map((p) => `<polygon points="${p.pts.map((q) => `${q[0].toFixed(1)},${q[1].toFixed(1)}`).join(' ')}" fill="${p.fill}" stroke="${p.stroke}" stroke-width="${sw}" stroke-linejoin="round"/>`).join('\n');
  const label = title ? `<text x="16" y="${height - 16}" font-family="Georgia, serif" font-size="18" fill="${pal('Iron 950')}" opacity=".75">${title}</text>` : '';
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">
<defs><linearGradient id="sk" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="${sky0}"/><stop offset="1" stop-color="${sky1}"/></linearGradient></defs>
<rect width="${width}" height="${height}" fill="url(#sk)"/>
${ground}
${body}
${overlay ? overlay(proj) : ''}
${label}
</svg>`;
}

/** The views a site preview is made of: the generator's `previews` list, or one default aerial view. */
export function siteViews(def) {
  return (def && def.previews) || [{ name: 'aerial', cam: { kind: 'ortho', yaw: 150, pitch: 32, centre: [0, 0, 60], scale: 5 } }];
}

/** A top-down plan, the axis left to right, with every point, box, plugin cell and sign labelled. */
export function planSvg(site, sculptures, { width = 1900, height = 560 } = {}) {
  const blocks = siteScene(site, sculptures, { gate: 'closed', band: 'rest', context: false });
  const zs = blocks.map((b) => b[2]), xs = blocks.map((b) => b[0]);
  const z0 = Math.min(...zs) - 6, z1 = Math.max(...zs) + 6, x0 = Math.min(...xs) - 8, x1 = Math.max(...xs) + 8;
  const k = Math.min((width - 40) / (z1 - z0 + 1), (height - 70) / (x1 - x0 + 1));
  const ox = 20, oy = 40;
  // Screen: z to the right, x downward reversed so +x (the right of someone walking to the fire) is at the bottom.
  const S = (x, z) => [ox + (z - z0) * k, oy + (x1 - x) * k];
  const top = new Map();
  for (const b of blocks) { const key = `${b[0]},${b[2]}`; const t = top.get(key); if (!t || b[1] >= t[1]) top.set(key, b); }
  const matColour = new Map(MATERIALS.map((m) => [m.id, m.previewColour]));
  const cellsSvg = [...top.values()].map((b) => {
    const [sx, sy] = S(b[0] + 1, b[2]);
    const fill = shade(b[6] || matColour.get(b[3]), 0.75 + Math.min(0.35, b[1] * 0.03));
    return `<rect x="${sx.toFixed(1)}" y="${sy.toFixed(1)}" width="${k.toFixed(2)}" height="${k.toFixed(2)}" fill="${fill}"/>`;
  }).join('');
  const C = (cell) => S(cell[0] + 0.5, cell[2] + 0.5);
  const txt = (x, y, s, { size = 11, fill = pal('Iron 950'), anchor = 'middle', weight = 600 } = {}) =>
    `<text x="${x.toFixed(1)}" y="${y.toFixed(1)}" font-family="Verdana, sans-serif" font-size="${size}" font-weight="${weight}" fill="${fill}" text-anchor="${anchor}" paint-order="stroke" stroke="${pal('Parchment')}" stroke-width="3">${s}</text>`;
  let o = '';
  const circle = (cell, rM, colour, dash = '') => { const [cx, cy] = C(cell); return `<circle cx="${cx.toFixed(1)}" cy="${cy.toFixed(1)}" r="${((rM / 1.2) * k).toFixed(1)}" fill="none" stroke="${colour}" stroke-width="1.5" ${dash ? `stroke-dasharray="${dash}"` : ''}/>`; };
  const pts = site.points;
  for (const z of site.zones) {
    if (!z.radiusM || z.radiusM > 20) continue;
    const list = z.point === 'banner.*' ? Object.entries(pts).filter(([n]) => n.startsWith('banner.')).map(([, p]) => p) : [pts[z.point]];
    for (const p of list) o += circle(p.cell, z.radiusM, z.key === 'Z2' ? pal('Ember deep') : pal('Blood'), z.key === 'Z2' ? '5 4' : '');
  }
  for (const [name, b] of Object.entries(site.boxes)) {
    const [ax, ay] = S(b.max[0] + 1, b.min[2]), [bx, by] = S(b.min[0], b.max[2] + 1);
    o += `<rect x="${ax.toFixed(1)}" y="${ay.toFixed(1)}" width="${(bx - ax).toFixed(1)}" height="${(by - ay).toFixed(1)}" fill="none" stroke="${pal('Blood')}" stroke-width="2" stroke-dasharray="8 5"/>`;
    o += txt(ax + 4, ay - 5, `${name} ${b.name}`, { anchor: 'start', fill: pal('Blood') });
  }
  for (const c of site.cells.emberBand.cells) { const [sx, sy] = S(c[0] + 1, c[2]); o += `<rect x="${sx.toFixed(1)}" y="${sy.toFixed(1)}" width="${k.toFixed(1)}" height="${k.toFixed(1)}" fill="${site.cells.emberBand.flare}" stroke="${pal('Ember deep')}"/>`; }
  for (const row of site.cells.gate.rows) for (const c of row) { const [sx, sy] = S(c[0] + 1, c[2]); o += `<rect x="${sx.toFixed(1)}" y="${sy.toFixed(1)}" width="${k.toFixed(1)}" height="${k.toFixed(1)}" fill="${pal('Iron 900')}" stroke="${pal('Ember')}"/>`; }
  for (const [name, p] of Object.entries(pts)) {
    if (name === 'gateSet' || name === 'pilgrimLedge') continue;
    const [cx, cy] = C(p.cell);
    o += `<circle cx="${cx.toFixed(1)}" cy="${cy.toFixed(1)}" r="4" fill="${pal('Blood')}" stroke="${pal('Parchment')}" stroke-width="1.5"/>`;
    const lbl = name.startsWith('banner.') ? name.slice(7) : name;
    o += txt(cx, cy - 7, lbl, { size: 10 });
  }
  for (const s of site.signs) { const [cx, cy] = C(s.cell); o += `<rect x="${(cx - 3).toFixed(1)}" y="${(cy - 3).toFixed(1)}" width="6" height="6" fill="${pal('Lapis')}"/>` + txt(cx, cy + 15, s.key, { size: 9, fill: pal('Lapis') }); }
  // Piece names.
  const pieceLabel = (p, s) => { const [cx, cy] = S(p.at[0] + p.size[0] / 2, p.at[2] + p.size[2] / 2); return txt(cx, cy + 4, s, { size: 10, fill: pal('Iron 950'), weight: 400 }); };
  for (const p of site.pieces) if (p.by === 'sculptor' && !p.key.endsWith('-stone')) o += pieceLabel(p, p.slot ? `${p.slot}: house-${(site.lot.preview || {})[p.slot]}` : p.sculpture);
  // A metre scale and the axis.
  const [a0x, a0y] = S(0.5, z0 + 2), [a1x] = S(0.5, z1 - 2);
  o = `<line x1="${a0x}" y1="${a0y}" x2="${a1x}" y2="${a0y}" stroke="${pal('Ember deep')}" stroke-width="1" stroke-dasharray="2 4"/>` + o;
  const [s0x] = S(0, z0 + 2), [s1x] = S(0, z0 + 2 + 25);
  o += `<line x1="${s0x}" y1="${height - 14}" x2="${s1x}" y2="${height - 14}" stroke="${pal('Iron 950')}" stroke-width="3"/>` + txt((s0x + s1x) / 2, height - 20, '30 m', { size: 11 });
  o += txt(width / 2, 22, `${site.name}: plan (site frame; the axis runs left to right toward the fire; +x is at the bottom)`, { size: 14, weight: 700 });
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">
<rect width="${width}" height="${height}" fill="${mix(pal('Moss'), pal('Parchment 2'), 0.62)}"/>
${cellsSvg}
${o}
</svg>`;
}

/** Writes preview/site-<id>-<view>.png for every view of a site, and site-<id>-plan.png. Returns the files. */
export async function renderSitePreviews(site, sculptures, outDir, def, only = null) {
  const { chromium } = await loadModule('playwright');
  fs.mkdirSync(outDir, { recursive: true });
  const browser = await launch(chromium);
  const written = [];
  try {
    const page = await browser.newPage();
    const shot = async (svg, file, w, h) => {
      await page.setViewportSize({ width: w, height: h });
      await page.setContent(`<!doctype html><html><body style="margin:0;background:#000">${svg}</body></html>`);
      await page.screenshot({ path: file, clip: { x: 0, y: 0, width: w, height: h } });
      written.push(file);
    };
    for (const v of siteViews(def)) {
      if (only && !only.includes(v.name)) continue;
      const blocks = siteScene(site, sculptures, v.state || {});
      const w = v.width || 1280, h = v.height || 720;
      await shot(viewSvg(blocks, v.cam, { width: w, height: h, fog: v.fog || 0, title: v.title || '' }), path.join(outDir, `site-${site.id}-${v.name}.png`), w, h);
    }
    if (!only || only.includes('plan')) await shot(planSvg(site, sculptures), path.join(outDir, `site-${site.id}-plan.png`), 1900, 560);
  } finally {
    await browser.close();
  }
  return written;
}

export const sitePreviewNames = (def) => [...siteViews(def).map((v) => v.name), 'plan'];
