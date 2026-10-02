// Breaking News: an alert queue for betrayals, claims, rebellions and the fall of a crown. Alerts play
// one at a time for `hold` seconds; urgent ones (rebellion, oathbreaking) jump the queue.

import { setupScene, startFeed, el, dyeFor, houseLabel, icon, healthChip, TYPE_ICON, TYPE_LABEL } from './kit.js';
import { AlertQueue, BREAKING_TYPES, breakingSeverity, formatUtc } from './model.js';

const kit = setupScene({ transparent: true, hold: 10, anchor: 'top center' });
const root = document.getElementById('scene');
const position = kit.params.get('position') === 'bottom' ? 'bottom' : 'top';
root.style.setProperty('--ui-origin', position === 'bottom' ? 'bottom center' : 'top center');
const typesParam = (kit.params.get('types') || '').split(',').map((s) => s.trim()).filter(Boolean);
const queue = new AlertQueue({
  max: Math.max(1, Math.min(30, Number(kit.params.get('max')) || 8)),
  types: typesParam.length ? typesParam : BREAKING_TYPES,
  houses: kit.houses,
});
const setHealth = healthChip();
const live = el('div', { class: 'sr-only', 'aria-live': 'assertive' });
root.appendChild(live);

let busy = false;
const HOUSE_IN_TITLE = /House ([\p{L}\p{N}'’ -]+?)(?= (?:and|breaks|claims|rises|renounces|swears|ends)\b|$)/u;

function card(e) {
  const sev = breakingSeverity(e.type);
  const m = HOUSE_IN_TITLE.exec(e.title || '');
  const house = m ? m[1] : null;
  const dye = dyeFor(house || e.title);
  const more = queue.length;
  return el('section', { class: `alert sev-${sev} ${position}`, '--hold': `${kit.hold}s`, ...dye, 'data-id': String(e.id) },
    el('div', { class: 'tag' },
      el('div', { class: 'tag-icon' }, icon(TYPE_ICON[e.type] || 'scroll')),
      el('div', { class: 'tag-text' }, el('span', { class: 'big' }, 'Breaking'), el('span', { class: 'small' }, 'The Chronicle of Ostreval'))),
    el('div', { class: 'body parchment' },
      el('div', { class: 'kind' }, TYPE_LABEL[e.type] || e.type.replace(/_/g, ' '),
        house ? el('span', { class: 'house' }, el('i', { class: 'dot' }), houseLabel(house)) : null),
      el('div', { class: 'headline' }, e.title || ''),
      e.detail ? el('div', { class: 'detail' }, e.detail) : null,
      el('div', { class: 'meta' }, e.ts ? formatUtc(Date.parse(e.ts)) : '', more > 0 ? el('span', { class: 'more' }, `+${more} more`) : null)),
    el('div', { class: 'timer' }));
}

async function pump() {
  if (busy) return;
  busy = true;
  try {
    let e;
    while ((e = queue.next())) {
      const c = card(e);
      root.appendChild(c);
      live.textContent = `${TYPE_LABEL[e.type] || 'Breaking'}: ${e.title}`;
      void c.offsetWidth;
      c.classList.add('enter');
      document.documentElement.dataset.ready = '1';
      if (kit.frozenNow != null) return; // screenshot preview: keep the first alert up
      await kit.sleep(kit.hold * 1000);
      c.classList.add('leave');
      await kit.sleep(kit.reduced ? 300 : 700);
      c.remove();
      await kit.sleep(400);
    }
  } finally {
    busy = false;
  }
}

startFeed(kit, {
  onSnapshot: ({ events }) => {
    // History is not news: only replay=N re-plays the last N matching events (for positioning in OBS).
    const old = events.filter((e) => queue.accepts(e));
    const keep = kit.replay ? old.slice(-kit.replay) : [];
    for (const e of old) if (!keep.includes(e)) queue.seen.add(e.id);
    queue.push(keep);
    pump();
  },
  onEvents: (batch) => {
    queue.push(batch);
    pump();
  },
  onHealth: setHealth,
});
