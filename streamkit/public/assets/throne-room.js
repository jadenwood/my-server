// Throne Room: a lower third for coronations. The new monarch's sigil draws itself inside a turning
// medallion while the name plate unrolls. mode=always keeps the reigning monarch on screen.

import { setupScene, startFeed, getJSON, el, clear, dyeFor, houseLabel, eventIcon, healthChip } from './kit.js';
import { parseCoronation, houseIn, HOUSE_WORDS, formatUtc } from './model.js';
import { sigilSvg } from './sigils.js';

const kit = setupScene({ transparent: true, hold: 14, anchor: 'bottom left' });
const root = document.getElementById('scene');
const mode = kit.params.get('mode') === 'always' ? 'always' : 'event';
const showFalls = kit.params.get('abdication') !== '0';
const position = ['left', 'center', 'right'].includes(kit.params.get('position')) ? kit.params.get('position') : 'left';
root.style.setProperty('--ui-origin', position === 'right' ? 'bottom right' : position === 'center' ? 'bottom center' : 'bottom left');
const setHealth = healthChip();

const live = el('div', { class: 'sr-only', 'aria-live': 'polite' });
root.appendChild(live);

let state = null;
const queue = [];
let busy = false;

function sigilOf(house) {
  const h = state && Array.isArray(state.houses) ? state.houses.find((x) => house && x.name.toLowerCase() === house.toLowerCase()) : null;
  return h ? h.sigil : null;
}

function buildPlate(item) {
  const fallen = item.kind === 'abdication';
  const dye = dyeFor(item.house || item.king || '');
  const sigil = item.house ? sigilOf(item.house) : null;
  const art = fallen
    ? el('svg', { viewBox: '0 0 100 100', class: 'sigil-art', fill: 'none', stroke: 'currentColor', 'stroke-width': '3', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' },
      el('path', { class: 'stroke', pathLength: '1', style: { '--i': '0' }, d: 'M18 70h64M22 70 18 30l18 14 14-24 14 24 18-14-4 40' }),
      el('path', { class: 'stroke', pathLength: '1', style: { '--i': '2' }, d: 'M34 86 66 54M66 86 34 54' }))
    : sigilSvg(sigil, item.house || item.king);
  const words = item.house ? HOUSE_WORDS[item.house.toLowerCase()] : null;

  const ring = fallen ? 'THE OLD THRONE STANDS EMPTY · ANYONE MAY SIT · '
    : 'THE CROWN BELONGS TO THE SEAT · ALL IS WRITTEN · ';
  const runes = el('svg', { viewBox: '0 0 200 200', class: 'runes', 'aria-hidden': 'true' },
    el('defs', {}, el('path', { id: 'rune-ring', d: 'M100 100 m-84 0 a84 84 0 1 1 168 0 a84 84 0 1 1 -168 0' })),
    el('text', {}, el('textPath', { href: '#rune-ring', textLength: '520', lengthAdjust: 'spacing' }, ring)));

  const embers = el('div', { class: 'embers', 'aria-hidden': 'true' },
    ...Array.from({ length: 14 }, (_, i) => el('i', { style: { '--x': `${(i * 37) % 100}%`, '--d': `${(i % 7) * 0.35}s`, '--s': `${0.6 + (i % 4) * 0.25}` } })));

  const sub = fallen
    ? 'The crown lies unclaimed. Anyone may sit the Old Throne.'
    : [item.house ? `of ${houseLabel(item.house)}` : 'of no house', sigil ? `the ${sigil}` : null].filter(Boolean).join(' · ');
  const foot = fallen
    ? [item.when ? formatUtc(item.when) : null].filter(Boolean).join('')
    : [item.when ? `Crowned ${formatUtc(item.when)}` : null, item.previous && item.previous !== item.king ? `succeeds ${item.previous}` : null].filter(Boolean).join(' · ');

  const burst = el('div', { class: 'burst', 'aria-hidden': 'true' },
    ...Array.from({ length: 22 }, (_, i) => {
      const a = (i / 22) * Math.PI * 2;
      const r = 170 + ((i * 53) % 130);
      return el('b', { style: { '--x': `${Math.round(Math.cos(a) * r)}px`, '--y': `${Math.round(Math.sin(a) * r)}px`, '--d': `${((i * 7) % 5) * 0.06}s` } });
    }));

  return el('section', { class: `throne ${position}${fallen ? ' fallen' : ''}`, ...dye },
    el('div', { class: 'medallion' }, el('div', { class: 'rays', 'aria-hidden': 'true' }), el('div', { class: 'flare' }), runes, el('div', { class: 'disc' }, art), burst),
    el('div', { class: 'plate iron' },
      embers,
      el('div', { class: 'eyebrow' }, fallen ? 'The crown falls' : item.replayed && mode === 'always' ? 'The reigning crown' : 'Long live the crown'),
      el('div', { class: 'king gold-text' }, fallen ? `${item.king || 'The monarch'} no longer reigns` : item.king || 'A new monarch'),
      el('div', { class: 'sub' }, sub),
      words && !fallen ? el('div', { class: 'words' }, `“${words}”`) : null,
      foot ? el('div', { class: 'foot' }, eventIcon(fallen ? 'abdication' : 'coronation'), foot) : null));
}

async function show(item) {
  const plate = buildPlate(item);
  const old = root.querySelector('.throne');
  if (old) {
    old.classList.add('leave');
    await kit.sleep(kit.reduced ? 250 : 700);
    old.remove();
  }
  root.appendChild(plate);
  live.textContent = item.kind === 'abdication' ? `${item.king} no longer reigns.` : `Long live ${item.king}${item.house ? `, of ${houseLabel(item.house)}` : ''}.`;
  void plate.offsetWidth;
  plate.classList.add('enter');
  plate.querySelector('.sigil-art').classList.add('draw');
  document.documentElement.dataset.ready = '1';
  if (mode === 'always' && item.kind !== 'abdication') return; // stays until the next change
  if (kit.frozenNow != null) return; // screenshot preview: keep it on screen
  await kit.sleep(kit.hold * 1000);
  plate.classList.add('leave');
  await kit.sleep(kit.reduced ? 250 : 900);
  plate.remove();
  if (mode === 'always' && state && state.king) {
    queue.unshift({ kind: 'coronation', king: state.king, house: state.house, when: Date.parse(state.since) || null, replayed: true });
  }
}

async function pump() {
  if (busy) return;
  busy = true;
  try {
    while (queue.length) await show(queue.shift());
  } finally {
    busy = false;
  }
}

function toItem(e) {
  if (e.type === 'coronation') {
    const c = parseCoronation(e);
    return { kind: 'coronation', king: c.king, house: c.house, previous: (e.actors || [])[1] || null, when: Date.parse(e.ts) || null, id: e.id };
  }
  if (e.type === 'abdication' && showFalls) {
    return { kind: 'abdication', king: (e.actors || [])[0] || e.title.replace(/ no longer reigns$/, ''), house: null, when: Date.parse(e.ts) || null, id: e.id };
  }
  return null;
}

const wanted = (item) => item && (!kit.houses.length || (item.house && houseIn(item.house, kit.houses)));

startFeed(kit, {
  onSnapshot: ({ state: s, events }) => {
    state = s;
    if (kit.replay) {
      const items = events.map(toItem).filter(wanted).slice(-kit.replay);
      queue.push(...items);
    } else if (mode === 'always' && s.king && wanted({ house: s.house })) {
      queue.push({ kind: 'coronation', king: s.king, house: s.house, when: Date.parse(s.since) || null, replayed: true });
    }
    pump();
  },
  onState: (s) => { state = s; },
  onEvents: async (batch) => {
    // A coronation comes with a fresh state (for the sigil); re-read it before showing the plate.
    if (batch.some((e) => e.type === 'coronation')) {
      try { state = await getJSON('/api/state'); } catch { /* keep the old one */ }
    }
    queue.push(...batch.map(toItem).filter(wanted));
    pump();
  },
  onHealth: setHealth,
});
