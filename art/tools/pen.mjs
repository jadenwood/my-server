// Pen helpers for drawing heraldry by hand: they turn a few points into smooth SVG path data.
//   blob(points)   smooth closed outline through [x, y] points; add 'c' as a third item for a sharp corner
//   brush(points)  a stroke of varying width through [x, y, width] points, with round ends (pointed when width is 0)
//   line(points)   an open smooth curve, for detail lines
// The sigils' path data was drawn with these: print a shape and paste it into a sigil's <g id="charge">.
//   node -e "import('./art/tools/pen.mjs').then((p) => console.log(p.brush([[0, 0, 8], [40, 10, 4], [80, 0, 0]])))"
const f = (v) => +v.toFixed(1);
const P = (p) => `${f(p[0])} ${f(p[1])}`;

// Catmull-Rom through points -> cubic bezier segments (open or closed). Returns path (no M for continuation if cont).
function cr(points, closed, tension = 1) {
  const n = points.length;
  const get = (i) => (closed ? points[(i + n) % n] : points[Math.max(0, Math.min(n - 1, i))]);
  let d = '';
  const last = closed ? n : n - 1;
  for (let i = 0; i < last; i++) {
    const p0 = get(i - 1), p1 = get(i), p2 = get(i + 1), p3 = get(i + 2);
    const t = tension / 6;
    const c1 = [p1[0] + (p2[0] - p0[0]) * t, p1[1] + (p2[1] - p0[1]) * t];
    const c2 = [p2[0] - (p3[0] - p1[0]) * t, p2[1] - (p3[1] - p1[1]) * t];
    d += `C${P(c1)} ${P(c2)} ${P(p2)}`;
  }
  return d;
}

// Smooth closed outline through points. Points can be [x,y] or [x,y,'c'] for a sharp corner (split into separate smooth runs).
export function blob(points, tension = 1) {
  const corners = points.map((p, i) => (p[2] === 'c' ? i : -1)).filter((i) => i >= 0);
  if (!corners.length) return `M${P(points[0])}${cr(points, true, tension)}Z`;
  // rotate so we start at a corner, then split into runs between corners
  const start = corners[0];
  const pts = [...points.slice(start), ...points.slice(0, start), points[start]];
  let d = `M${P(pts[0])}`;
  let run = [pts[0]];
  for (let i = 1; i < pts.length; i++) {
    run.push(pts[i]);
    if (pts[i][2] === 'c' || i === pts.length - 1) {
      d += run.length === 2 ? `L${P(run[1])}` : cr(run, false, tension);
      run = [pts[i]];
    }
  }
  return d + 'Z';
}

// Variable-width stroke: points [x,y,width]. Smooth sides, round (or pointed when width 0) ends.
export function brush(points, tension = 1) {
  const n = points.length;
  const L = [], R = [];
  for (let i = 0; i < n; i++) {
    const a = points[Math.max(0, i - 1)], b = points[Math.min(n - 1, i + 1)];
    let dx = b[0] - a[0], dy = b[1] - a[1];
    const len = Math.hypot(dx, dy) || 1; dx /= len; dy /= len;
    const w = points[i][2] / 2;
    L.push([points[i][0] - dy * w, points[i][1] + dx * w]);
    R.push([points[i][0] + dy * w, points[i][1] - dx * w]);
  }
  const we = points[n - 1][2] / 2, ws = points[0][2] / 2;
  let d = `M${P(L[0])}${cr(L, false, tension)}`;
  d += we > 0.05 ? `A${f(we)} ${f(we)} 0 0 0 ${P(R[n - 1])}` : `L${P(R[n - 1])}`;
  d += cr([...R].reverse(), false, tension);
  d += ws > 0.05 ? `A${f(ws)} ${f(ws)} 0 0 0 ${P(L[0])}` : '';
  return d + 'Z';
}

// Open smooth curve through points (for detail lines).
export function line(points, tension = 1) {
  return `M${P(points[0])}${cr(points, false, tension)}`;
}
