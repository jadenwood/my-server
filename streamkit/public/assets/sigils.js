// Sigil drawings for the Throne Room and War Board. Line art drawn for this project on a 100x100 grid.
// A house's free-text sigil ("Iron Stag", "Black Raven") is matched by keyword; anything else gets a
// shield with the house monogram. Every path has pathLength=1 so CSS can "draw" it in.

import { el, monogram } from './kit.js';

const ART = {
  stag: [
    'M50 88 L40 66 Q38 56 44 50 L56 50 Q62 56 60 66 Z',
    'M44 52 L33 46 M56 52 L67 46',
    'M44 50 Q36 34 26 20 M37 37 L22 36 M31 28 L20 14 M41 44 Q31 41 23 46',
    'M56 50 Q64 34 74 20 M63 37 L78 36 M69 28 L80 14 M59 44 Q69 41 77 46',
    'M45 61 L46 61 M54 61 L55 61 M47 80 Q50 83 53 80',
  ],
  oak: [
    'M50 58 C30 62 16 48 25 36 C19 22 36 11 46 18 C52 7 71 11 70 24 C85 28 83 48 70 52 C66 60 56 60 50 58 Z',
    'M50 58 L39 42 M50 58 L61 40 M50 58 L50 32',
    'M46 86 L47 58 M54 86 L53 58',
    'M46 86 Q37 88 28 93 M54 86 Q63 88 72 93 M50 86 L50 95',
  ],
  raven: [
    'M22 62 Q28 41 49 38 Q57 29 68 31 L80 35 L70 40 Q66 51 60 58 Q51 70 35 72 L16 81 L25 68 Z',
    'M33 58 Q44 49 58 52 M38 64 Q48 58 57 60',
    'M66 34 L67 34',
    'M43 71 L41 85 M50 69 L50 85 M38 85 L45 85 M46 85 L54 85',
    'M80 37 L90 43 M86 41 L84 45 M90 43 m-3 0 a3 3 0 1 0 6 0 a3 3 0 1 0 -6 0',
  ],
  bell: [
    'M34 62 Q33 29 50 25 Q67 29 66 62 L72 68 L28 68 Z',
    'M50 25 V19 M45 19 H55 M40 48 Q50 44 60 48',
    'M50 68 m-4 0 a4 4 0 1 0 8 0',
    'M14 78 q9 -6 18 0 t18 0 t18 0 t18 0',
    'M14 87 q9 -6 18 0 t18 0 t18 0 t18 0',
    'M22 95 q7 -5 14 0 t14 0 t14 0 t14 0',
  ],
  hound: [
    'M30 86 L34 61 Q29 47 40 38 L44 24 L50 35 Q61 33 67 40 L81 46 L79 55 L64 57 Q57 63 60 86',
    'M44 24 L40 45',
    'M57 43 L58 43 M79 48 L82 48',
    'M30 86 H40 M52 86 H62',
    'M22 42 q-5 -7 0 -14 q2 7 7 9 q-3 2 -7 5',
    'M76 22 q-4 -6 0 -12 q2 6 6 8 q-3 2 -6 4',
    'M73 74 q-4 -6 0 -11 q2 6 5 7 q-2 2 -5 4',
  ],
  eel: [
    'M18 72 C18 40 50 30 60 46 C68 60 50 71 44 58 C40 50 52 44 58 52',
    'M60 46 C66 30 80 26 87 34 L90 38 L84 39',
    'M84 33 L85 33',
    'M12 20 L36 44 M24 12 L48 36 M12 36 L36 12 M24 46 L46 24',
    'M64 80 L78 66 M70 88 L88 70 M66 66 L86 86',
  ],
};

const KEYWORDS = [
  ['stag', /stag|hart|deer|elk/i],
  ['oak', /\boak|tree|\belm\b/i],
  ['raven', /raven|crow|rook|bird|hawk|eagle/i],
  ['bell', /bell/i],
  ['hound', /hound|dog|wolf|fox/i],
  ['eel', /eel|fish|serpent|snake|pike/i],
];

export function sigilKind(sigil) {
  for (const [kind, re] of KEYWORDS) if (re.test(sigil || '')) return kind;
  return null;
}

// <svg class="sigil-art"> with drawable strokes. `name` is used for the monogram fallback.
export function sigilSvg(sigil, name, cls = 'sigil-art') {
  const kind = sigilKind(sigil);
  const svg = el('svg', { viewBox: '0 0 100 100', class: cls, 'aria-hidden': 'true', fill: 'none', stroke: 'currentColor',
    'stroke-width': '3', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'data-kind': kind || 'monogram' });
  if (kind) {
    ART[kind].forEach((d, i) => svg.appendChild(el('path', { d, pathLength: '1', class: 'stroke', style: { '--i': String(i) } })));
  } else {
    svg.appendChild(el('path', { d: 'M50 10 L84 22 V50 C84 72 70 86 50 92 C30 86 16 72 16 50 V22 Z', pathLength: '1', class: 'stroke', style: { '--i': '0' } }));
    svg.appendChild(el('text', { x: '50', y: '64', 'text-anchor': 'middle', class: 'mono', fill: 'currentColor', stroke: 'none' }, monogram(name)));
  }
  return svg;
}
