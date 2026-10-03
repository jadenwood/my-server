// Countdown: time to the next rebellion window (the Lawful Hours), a declared claim, a realm event,
// or a custom moment (at= / in=). Claims come from the chronicle; the weekly schedule comes from
// /schedule.json or URL params and falls back to the plugin defaults.

import { setupScene, startFeed, loadSchedule, el, clear, put, icon, artIcon, houseLabel, dyeFor, healthChip } from './kit.js';
import {
  buildWarBoard, chooseCountdown, normalizeSchedule, parseWindowsParam, parseEventsParam, parseDurationParam,
  splitDuration, formatUtc,
} from './model.js';

const kit = setupScene({ transparent: true, anchor: 'center' });
const root = document.getElementById('scene');
const layout = kit.params.get('layout') === 'compact' ? 'compact' : 'full';
const mode = ['rebellion', 'event', 'custom'].includes(kit.params.get('for')) ? kit.params.get('for') : 'auto';
if (layout === 'compact') root.style.setProperty('--ui-origin', 'top right');
const setHealth = healthChip();

const loadedAt = kit.now();
let custom = null;
if (kit.params.get('at')) {
  const t = Date.parse(kit.params.get('at'));
  if (!Number.isNaN(t)) custom = { at: t };
} else if (kit.params.get('in')) {
  const d = parseDurationParam(kit.params.get('in'));
  if (d != null) custom = { at: loadedAt + d };
}
if (custom) {
  custom.label = (kit.params.get('label') || '').slice(0, 80) || null;
  custom.sub = (kit.params.get('sub') || '').slice(0, 120) || '';
}

let windows = parseWindowsParam(kit.params.get('windows'));
let realmEvents = parseEventsParam(kit.params.get('events'));
let offsetHours = Number(kit.params.get('offset')) || 0;
let data = { state: null, events: [] };

// ---------- frame ----------
const units = ['d', 'h', 'm', 's'].map((u) => el('div', { class: `unit u-${u}` },
  el('div', { class: 'num' }, '00'), el('div', { class: 'lbl' }, { d: 'Days', h: 'Hours', m: 'Minutes', s: 'Seconds' }[u])));
const panel = el('section', { class: `countdown ${layout} iron` },
  el('div', { class: 'glass' }, icon('hourglass', 'icon hourglass')),
  el('div', { class: 'eyebrow', id: 'cd-title' }, ' '),
  el('div', { class: 'digits', role: 'timer', 'aria-live': 'off' }, ...units),
  el('div', { class: 'sub', id: 'cd-sub' }, ' '),
  el('div', { class: 'foot', id: 'cd-foot' }, ' '));
root.appendChild(panel);
const live = el('div', { class: 'sr-only', 'aria-live': 'polite' });
root.appendChild(live);

let target = null;
let lastKey = '';
let zeroAt = null;

function pickTarget(now) {
  const board = data.state ? buildWarBoard(data.state, data.events, { now, windows: windows.length ? windows : undefined, offsetHours }) : null;
  return chooseCountdown({
    board, events: data.events, now, mode,
    windows: windows.length ? windows : undefined,
    realmEvents: realmEvents.length ? realmEvents : undefined,
    offsetHours, custom, houses: kit.houses,
  });
}

function tick() {
  const now = kit.now();
  if (!target || (target.at <= now && zeroAt == null)) {
    if (target && target.at <= now) {
      // Hold "now" on screen for a few seconds before moving to the next target.
      zeroAt = now;
      panel.classList.add('arrived');
      document.getElementById('cd-title').textContent = target.live ? 'The time is up' : target.kind === 'custom' ? 'It begins' : 'It begins now';
      live.textContent = document.getElementById('cd-title').textContent;
      units.forEach((u) => { u.firstChild.textContent = '00'; });
      return;
    }
    target = pickTarget(now);
  }
  if (zeroAt != null) {
    if (now - zeroAt < 6000) return;
    zeroAt = null;
    panel.classList.remove('arrived');
    target = pickTarget(now);
  }
  if (!target) {
    document.getElementById('cd-title').textContent = 'No hour is appointed';
    document.getElementById('cd-sub').textContent = 'The heralds have nothing on the calendar.';
    panel.classList.add('idle');
    return;
  }
  panel.classList.remove('idle');
  const key = `${target.kind}|${target.at}|${target.house || ''}`;
  if (key !== lastKey) {
    lastKey = key;
    panel.className = `countdown ${layout} iron kind-${target.kind}${target.live ? ' live' : ''}`;
    if (target.house) Object.entries(dyeFor(target.house)).forEach(([k, v]) => panel.style.setProperty(k, v));
    document.getElementById('cd-title').textContent = target.title;
    document.getElementById('cd-sub').textContent = target.sub || '';
    const foot = clear(document.getElementById('cd-foot'));
    put(foot, artIcon(target.live ? 'rebellion' : target.kind === 'event' || target.kind === 'event-live' ? 'beacon' : 'claim'),
      target.live ? `Until ${formatUtc(target.at)}` : formatUtc(target.at),
      target.house ? el('span', { class: 'house' }, el('i', { class: 'dot' }), houseLabel(target.house)) : null);
    live.textContent = `${target.title} ${formatUtc(target.at)}`;
    document.documentElement.dataset.ready = '1';
  }
  const { d, h, m, s } = splitDuration(target.at - now);
  const vals = { d, h, m, s };
  panel.classList.toggle('no-days', d === 0);
  panel.classList.toggle('final', target.at - now <= 60 * 1000);
  for (const u of units) {
    const k = u.className.match(/u-(\w)/)[1];
    const text = String(vals[k]).padStart(2, '0');
    const num = u.firstChild;
    if (num.textContent !== text) {
      num.textContent = text;
      num.classList.remove('tick');
      void num.offsetWidth;
      num.classList.add('tick');
    }
  }
}

(async () => {
  const sched = normalizeSchedule(await loadSchedule());
  if (!windows.length && sched.windows) windows = sched.windows;
  if (!realmEvents.length && sched.events) realmEvents = sched.events;
  if (!kit.params.has('offset')) offsetHours = sched.offsetHours;
  const refresh = () => { target = null; lastKey = ''; zeroAt = null; panel.classList.remove('arrived'); tick(); };
  if (mode === 'custom' && custom) refresh(); // a custom countdown needs no chronicle
  startFeed(kit, {
    onSnapshot: (snap) => { data = snap; refresh(); },
    onEvents: (batch, events) => { data = { ...data, events }; refresh(); },
    onState: (state) => { data = { ...data, state }; },
    onHealth: setHealth,
  });
  tick();
  setInterval(tick, 250);
})();
