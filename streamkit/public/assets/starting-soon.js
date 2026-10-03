// Starting Soon: a full-screen holding scene. Title, an optional countdown (in= / at=), the reigning
// crown, the house banners, the next Lawful Hours and a rotating "lately in the realm" line.

import { setupScene, startFeed, loadSchedule, el, clear, put, artIcon, eventIcon, banner, houseLabel, healthChip } from './kit.js';
import {
  buildWarBoard, chooseCountdown, normalizeSchedule, parseWindowsParam, parseDurationParam, splitDuration,
  formatUtc, eventMentions, houseIn,
} from './model.js';

const kit = setupScene({ transparent: false, anchor: 'center' });
const root = document.getElementById('scene');
const setHealth = healthChip();
const title = (kit.params.get('title') || 'The Chronicle Resumes').slice(0, 60);
const sub = (kit.params.get('sub') || 'The stream begins shortly').slice(0, 100);
const bannersParam = Number(kit.params.get('banners') ?? 6);
const maxBanners = Number.isFinite(bannersParam) ? Math.max(0, Math.min(8, Math.round(bannersParam))) : 6;
const loadedAt = kit.now();
let target = null;
if (kit.params.get('at') && !Number.isNaN(Date.parse(kit.params.get('at')))) target = Date.parse(kit.params.get('at'));
else if (kit.params.get('in')) {
  const d = parseDurationParam(kit.params.get('in'));
  if (d != null) target = loadedAt + d;
}
let windows = parseWindowsParam(kit.params.get('windows'));
let offsetHours = Number(kit.params.get('offset')) || 0;

const embers = el('div', { class: 'ss-embers', 'aria-hidden': 'true' },
  ...Array.from({ length: 34 }, (_, i) => el('i', { style: {
    '--x': `${(i * 53) % 100}%`, '--d': `${((i * 7) % 17) * 0.6}s`, '--t': `${9 + (i % 5) * 2.5}s`, '--s': `${0.5 + (i % 4) * 0.3}`,
  } })));
const timer = el('div', { class: 'ss-timer', role: 'timer', hidden: target == null }, '');
const crown = el('div', { class: 'ss-crown' });
const banners = el('div', { class: 'ss-banners' });
const lately = el('div', { class: 'ss-lately iron' },
  el('div', { class: 'plaque' }, artIcon('decree'), 'Lately in the realm'),
  el('div', { class: 'line', 'aria-live': 'polite' }));
const nextWar = el('div', { class: 'ss-next' });

root.append(el('div', { class: 'ss-keyart', 'aria-hidden': 'true' }), embers,
  el('div', { class: 'ss-fog', 'aria-hidden': 'true' }),
  el('header', { class: 'ss-head' },
    el('div', { class: 'eyebrow' }, 'The Realm of Ostreval'),
    el('h1', { class: 'gold-text' }, title),
    el('div', { class: 'ss-rule', 'aria-hidden': 'true' }, el('span'), artIcon('crown'), el('span')),
    el('div', { class: 'ss-sub' }, sub),
    timer),
  crown, banners, nextWar, lately);

let data = { state: null, events: [] };
let lines = [];
let lineIdx = 0;

function render() {
  const s = data.state || {};
  const now = kit.now();
  clear(crown);
  if (s.king) {
    const since = Date.parse(s.since);
    const reign = Number.isNaN(since) ? '' : (() => { const { d, h, m } = splitDuration(now - since); return d ? `${d}d ${h}h` : `${h}h ${m}m`; })();
    put(crown, el('span', { class: 'lbl' }, 'The Crown'), el('span', { class: 'king' }, s.king),
      s.house ? el('span', { class: 'of' }, `of ${houseLabel(s.house)}`) : null,
      reign ? el('span', { class: 'reign' }, `reigning ${reign}`) : null);
  } else {
    put(crown, el('span', { class: 'lbl' }, 'The Crown'), el('span', { class: 'king' }, 'The Old Throne stands empty'));
  }
  if (s.maxPlayers) put(crown, el('span', { class: 'souls' }, `${s.online} of ${s.maxPlayers} souls in the realm`));

  clear(banners);
  const houses = (Array.isArray(s.houses) ? s.houses : []).filter((h) => houseIn(h.name, kit.houses));
  houses.sort((a, b) => (b.name === s.house) - (a.name === s.house) || b.members - a.members);
  houses.slice(0, maxBanners).forEach((h, i) => {
    const b = banner(h, h.name === s.house ? 'crowned' : '');
    b.style.setProperty('--i', String(i));
    b.appendChild(el('div', { class: 'bname' }, h.name));
    banners.appendChild(b);
  });

  const board = data.state ? buildWarBoard(data.state, data.events, { now, windows: windows.length ? windows : undefined, offsetHours }) : null;
  const war = chooseCountdown({ board, events: data.events, now, mode: 'rebellion', windows: windows.length ? windows : undefined, offsetHours, houses: kit.houses });
  clear(nextWar);
  if (war) {
    const label = war.kind === 'claim' ? `House ${war.house} rises ${formatUtc(war.at)}`
      : war.live ? `${war.title} until ${formatUtc(war.at)}`
        : `Next Lawful Hours: ${formatUtc(war.at)}`;
    put(nextWar, artIcon(war.live || war.kind === 'claim' ? 'rebellion' : 'claim'), label);
    nextWar.className = `ss-next${war.live || war.kind === 'claim' ? ' hot' : ''}`;
  }

  lines = data.events.filter((e) => eventMentions(e, kit.houses)).slice(-8).reverse();
  showLine();
  document.documentElement.dataset.ready = '1';
}

function showLine() {
  const box = lately.querySelector('.line');
  if (!lines.length) { box.textContent = 'The Chronicle is quiet.'; return; }
  const e = lines[lineIdx % lines.length];
  const node = el('span', { class: 'entry', 'data-type': e.type }, eventIcon(e.type), el('b', {}, e.title), e.detail ? ` — ${e.detail}` : '');
  clear(box).appendChild(node);
}

function tickTimer() {
  if (target == null) return;
  const left = target - kit.now();
  if (left <= 0) { timer.textContent = 'Now'; timer.classList.add('now'); return; }
  const { d, h, m, s } = splitDuration(left);
  const p = (n) => String(n).padStart(2, '0');
  timer.textContent = d ? `${d}d ${p(h)}:${p(m)}:${p(s)}` : h ? `${h}:${p(m)}:${p(s)}` : `${p(m)}:${p(s)}`;
}

(async () => {
  const sched = normalizeSchedule(await loadSchedule());
  if (!windows.length && sched.windows) windows = sched.windows;
  if (!kit.params.has('offset')) offsetHours = sched.offsetHours;
  tickTimer();
  setInterval(tickTimer, 250);
  startFeed(kit, {
    onSnapshot: (snap) => { data = snap; render(); },
    onEvents: (batch, events) => { data = { ...data, events }; lineIdx = 0; render(); },
    onState: (state) => { data = { ...data, state }; render(); },
    onHealth: setHealth,
  });
  if (kit.frozenNow == null) {
    setInterval(() => { lineIdx++; showLine(); }, 7000);
  }
})();
