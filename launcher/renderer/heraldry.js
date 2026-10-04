'use strict';

// Realm heraldry for both apps (Steward and the player app): which art/ file belongs to each Chronicle
// event, great house, season and renown title. The files themselves are copies of art/ in
// renderer/assets (see assets/README.md), so the apps work offline. Exposes window.RealmArt.
//
// Sources, checked by scripts/design-screens.mjs:
//   EVENTS  icon: art/icons/event-map.json; label and tone: chronicle/public/assets/common.js TYPE_META
//   HOUSES  art/palette.json houses (field = the overlay dye for the name, metal = the charge)
//   TITLES  art/src/titles.json (ids and names match DefaultTitles in plugins/RealmRenown.cs)
(function () {
  const NS = 'http://www.w3.org/2000/svg';
  const SPRITE = 'assets/icons.svg';

  // type: [icon, label, tone]. Tones are the overlay's: gold, blood, moss, iron.
  const EVENTS = {
    coronation: ['crown', 'Coronation', 'gold'],
    abdication: ['abdication', 'The Crown Falls', 'blood'],
    claim_declared: ['claim', 'A Claim', 'blood'],
    rebellion_started: ['rebellion', 'Rebellion', 'blood'],
    rebellion_ended: ['sheathed', 'Rebellion Ends', 'iron'],
    house_founded: ['house', 'A House Rises', 'gold'],
    oath_sworn: ['oath', 'Oath Sworn', 'gold'],
    oath_broken: ['oath-broken', 'Oathbreaker', 'blood'],
    treaty_signed: ['treaty', 'Treaty', 'moss'],
    treaty_broken: ['treaty-broken', 'Treaty Broken', 'blood'],
    decree: ['decree', 'Royal Decree', 'gold'],
    ransom_set: ['ransom', 'Ransom', 'blood'],
    ransom_paid: ['coins', 'Ransom Paid', 'gold'],
    released: ['released', 'Set Free', 'moss'],
    contract_posted: ['contract', 'A Price Is Set', 'iron'],
    contract_fulfilled: ['contract-paid', 'Contract Paid', 'gold'],
    contract_ended: ['contract-lapsed', 'Contract Lapses', 'iron'],
    season_started: ['dawn', 'A New Season', 'gold'],
    season_ended: ['laurel', "Season's End", 'gold'],
    event_started: ['beacon', 'Realm Event', 'iron'],
    event_ended: ['beacon-out', 'Event Ends', 'iron'],
    tournament_champion: ['trophy', 'Tournament Champion', 'gold'],
    hunt_kill: ['hunt', "The King's Hunt", 'blood'],
    truce_broken: ['dagger', 'Truce Broken', 'blood'],
    law_proclaimed: ['law', 'A Law Proclaimed', 'gold'],
    law_repealed: ['law-repealed', 'Law Repealed', 'iron'],
    accusation: ['accuse', 'Accused', 'blood'],
    trial_by_combat: ['helm', 'Trial by Combat', 'blood'],
    verdict: ['scales', 'Verdict', 'iron'],
    pardon: ['pardon', 'Pardoned', 'moss'],
    dynasty_founded: ['lineage', 'A Line Is Founded', 'gold'],
    heir_named: ['heir', 'An Heir Is Named', 'gold'],
    succession: ['succession', 'Succession', 'gold'],
    blood_claim: ['blood-claim', 'Blood Claim', 'blood'],
    blood_restored: ['sprout', 'The Line Restored', 'gold'],
    title_bestowed: ['collar', 'Title Bestowed', 'gold'],
    title_earned: ['medal', 'A Title Earned', 'gold'],
    treasury_mint: ['mint', 'The Crown Strikes Coin', 'gold'],
    treasury_grant: ['grant', 'Royal Largesse', 'gold'],
    tithe_levied: ['tithe', 'The Tithe Gathered', 'iron'],
    great_trade: ['purse', 'A Great Sale', 'gold'],
    rumour: ['whisper', 'A Rumour Spreads', 'iron'],
    holding_taken: ['holding', 'A Holding Taken', 'blood']
  };

  // The six great houses of Ostreval (docs/community/lore.md, art/palette.json).
  const HOUSES = [
    { id: 'varrow', name: 'Varrow', sigil: 'Iron Stag', words: 'We Stand Our Ground.', field: '#4a2347', fieldLight: '#674664', fieldDark: '#2e162c', metal: '#9aa0a8' },
    { id: 'ashgrove', name: 'Ashgrove', sigil: 'White Oak', words: 'Deep Roots, Long Memory.', field: '#7a3a1a', fieldLight: '#8f5a3f', fieldDark: '#4c2410', metal: '#e8dfc8' },
    { id: 'corvane', name: 'Corvane', sigil: 'Black Raven', words: 'Every Secret Has a Price.', field: '#2c3b42', fieldLight: '#4e5a60', fieldDark: '#1b2529', metal: '#c9ced4' },
    { id: 'dunmere', name: 'Dunmere', sigil: 'Drowned Bell', words: 'The Tide Returns.', field: '#5a5a22', fieldLight: '#747445', fieldDark: '#383815', metal: '#e0b56a' },
    { id: 'halloran', name: 'Halloran', sigil: 'Ember Hound', words: 'Loyal Until the Last Coal.', field: '#3a2a1a', fieldLight: '#5a4c3f', fieldDark: '#241a10', metal: '#e27a2c' },
    { id: 'merrin', name: 'Merrin', sigil: 'Silver Eel', words: 'Slip the Net.', field: '#24472d', fieldLight: '#47644f', fieldDark: '#162c1c', metal: '#c9ced4' }
  ];

  // Renown titles with a badge. A server may add its own titles in the plugin config; those get the
  // medal icon instead.
  const TITLES = [
    ['kingslayer', 'Kingslayer', false], ['usurper', 'Usurper', false], ['kingmaker', 'Kingmaker', false],
    ['unbowed', 'The Unbowed', false], ['shield_of_crown', 'Shield of the Crown', false], ['long_reign', 'The Long Reign', false],
    ['warden_of_roads', 'Warden of Roads', false], ['sellsword', 'Sellsword', false], ['headtaker', 'Headtaker', false],
    ['champion', 'Champion of the Lists', false], ['huntsman', "Crown's Huntsman", false], ['renowned', 'the Renowned', false],
    ['legend', 'Legend of Ostreval', false], ['oathbreaker', 'Oathbreaker', true], ['faithless', 'The Faithless', true],
    ['trucebreaker', 'Trucebreaker', true], ['hunted', 'The Hunted', true], ['black_name', 'Black Name', true]
  ].map(([id, name, infamous]) => ({ id, name, infamous, file: 'assets/badges/titles/' + id.replace(/_/g, '-') + '.svg' }));

  // The overlay's house dye (chronicle/public/assets/common.js dyeFor): a player-founded house gets
  // the same colour here as on stream.
  const DYES = ['#7a1f1c', '#1f2f5c', '#24472d', '#86601a', '#4a2347', '#2c3b42', '#7a3a1a', '#1c4a4a', '#3a2a1a', '#5a5a22'];
  function hash(s) {
    let h = 2166136261;
    for (const ch of String(s).toLowerCase()) h = Math.imul(h ^ ch.codePointAt(0), 16777619);
    return h >>> 0;
  }
  function shade(hex, amt) {
    const n = parseInt(hex.slice(1), 16);
    const mix = (c) => Math.round(amt >= 0 ? c + (255 - c) * amt : c * (1 + amt));
    const r = mix(n >> 16), g = mix((n >> 8) & 255), b = mix(n & 255);
    return '#' + ((1 << 24) | (r << 16) | (g << 8) | b).toString(16).slice(1);
  }
  function dyeFor(name) {
    const dye = DYES[hash(name || '') % DYES.length];
    return { field: dye, fieldLight: shade(dye, 0.16), fieldDark: shade(dye, -0.38) };
  }

  const bare = (name) => String(name || '').trim().replace(/^house\s+/i, '').toLowerCase();
  const greatHouse = (name) => HOUSES.find((h) => h.id === bare(name) || h.name.toLowerCase() === bare(name)) || null;

  function img(src, cls, alt) {
    const i = document.createElement('img');
    i.src = src;
    i.alt = alt || '';
    i.decoding = 'async';
    i.draggable = false;
    if (cls) i.className = cls;
    return i;
  }

  function svg(cls) {
    const s = document.createElementNS(NS, 'svg');
    if (cls) s.setAttribute('class', cls);
    s.setAttribute('aria-hidden', 'true');
    return s;
  }

  // One event icon from the sprite, coloured by the surrounding text (currentColor).
  function icon(name, cls) {
    const s = svg(cls);
    const use = document.createElementNS(NS, 'use');
    use.setAttribute('href', SPRITE + '#realm-icon-' + name);
    s.appendChild(use);
    return s;
  }

  function eventMeta(type) {
    const m = EVENTS[type];
    return m ? { icon: m[0], label: m[1], tone: m[2] } : { icon: 'decree', label: 'Chronicle', tone: 'iron' };
  }

  function seasonOf(e) {
    const m = /\bseason\s+(\d{1,3})\b/i.exec(String((e && e.title) || ''));
    return m ? Number(m[1]) : null;
  }

  function seasonBadge(n, cls) {
    const k = ((Math.max(1, n) - 1) % 4) + 1;
    return img('assets/badges/seasons/season-' + k + '.svg', cls, 'Season ' + n);
  }

  function titleFor(text) {
    const t = String(text || '').toLowerCase();
    let best = null;
    for (const x of TITLES) if (t.endsWith(x.name.toLowerCase()) || t.includes(' ' + x.name.toLowerCase())) if (!best || x.name.length > best.name.length) best = x;
    return best;
  }

  function titleBadge(title, cls) {
    return img(title.file, cls, title.name);
  }

  // The mark for one Chronicle event: a season medal for season events, a renown badge for a known
  // title, otherwise the event's own icon in a toned roundel. Returns an element with class
  // `ev-mark tone-<tone>` (+ ` badge` when it is a badge).
  function eventMark(e, cls) {
    const meta = eventMeta(e && e.type);
    const wrap = document.createElement('span');
    wrap.className = 'ev-mark tone-' + meta.tone + (cls ? ' ' + cls : '');
    wrap.title = meta.label;
    let badge = null;
    if (e && (e.type === 'season_started' || e.type === 'season_ended')) {
      const n = seasonOf(e);
      if (n) badge = seasonBadge(n, 'ev-badge');
    } else if (e && (e.type === 'title_earned' || e.type === 'title_bestowed')) {
      const t = titleFor(e.title);
      if (t) badge = titleBadge(t, 'ev-badge');
    }
    if (badge) {
      wrap.classList.add('badge');
      wrap.appendChild(badge);
    } else wrap.appendChild(icon(meta.icon, 'ev-icon'));
    return wrap;
  }

  // A house's arms. Great houses use their drawn sigil (roundel), shield or banner; any other house
  // gets a plain heater shield in its overlay dye with its initial, so it never borrows a great
  // house's charge (docs/brand.md: player-founded houses).
  function houseMark(name, variant, cls) {
    const g = greatHouse(name);
    const kind = variant === 'banner' ? 'banners' : variant === 'shield' ? 'shields' : 'sigils';
    if (g) {
      const i = img('assets/' + kind + '/' + g.id + '.svg', cls, 'Arms of House ' + g.name);
      i.dataset.house = g.id;
      return i;
    }
    const d = dyeFor(bare(name));
    const s = svg(cls);
    s.setAttribute('viewBox', '0 0 120 140');
    const id = 'hm' + (hash(name) % 1e8).toString(36) + Math.random().toString(36).slice(2, 6);
    s.innerHTML =
      '<defs><linearGradient id="' + id + '" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="' + d.fieldLight + '"/><stop offset="1" stop-color="' + d.fieldDark + '"/></linearGradient></defs>' +
      '<path d="M10 8h100v52c0 38-24 62-50 74C34 122 10 98 10 60z" fill="#131417"/>' +
      '<path d="M15 13h90v47c0 34-21 56-45 67C36 116 15 94 15 60z" fill="url(#' + id + ')" stroke="#d6a043" stroke-width="3"/>' +
      '<path d="M21 19h78v41c0 30-18 49-39 59C39 109 21 90 21 60z" fill="none" stroke="#f4c96d" stroke-opacity=".35" stroke-width="1"/>';
    const t = document.createElementNS(NS, 'text');
    t.setAttribute('x', '60');
    t.setAttribute('y', '84');
    t.setAttribute('text-anchor', 'middle');
    t.setAttribute('class', 'hm-initial');
    const m = bare(name).match(/\p{L}|\p{N}/u);
    t.textContent = m ? m[0].toUpperCase() : '?';
    s.appendChild(t);
    return s;
  }

  // ------------------------------------------------------------------ shared states
  // The same empty, loading and error looks on every screen (styles.css "states").

  function text(tag, cls, t) {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (t != null) n.textContent = String(t);
    return n;
  }

  // An empty list: an event icon, a short title, one sentence, optionally one action button.
  // tag: 'li' inside lists, 'div' elsewhere.
  function emptyState({ tag = 'div', art = 'decree', title, body, cmd, action } = {}) {
    const box = text(tag, 'empty-state');
    box.appendChild(icon(art, 'es-art'));
    if (title) box.appendChild(text('p', 'es-title', title));
    if (body) box.appendChild(text('p', 'es-text', body));
    if (cmd) box.appendChild(text('code', 'es-cmd', cmd));
    if (action) {
      const b = text('button', 'btn small ghost', action.label);
      b.type = 'button';
      b.addEventListener('click', action.onClick);
      box.appendChild(b);
    }
    return box;
  }

  // Placeholder rows while the first answer is on its way.
  function skeletonRows(n, tag = 'li') {
    const out = [];
    for (let i = 0; i < n; i++) {
      const row = text(tag, 'skel-row');
      row.setAttribute('aria-hidden', 'true');
      const lines = text('span', 'lines');
      lines.append(text('span', 'skel line'), text('span', 'skel line short'));
      row.append(text('span', 'skel dot'), lines);
      out.push(row);
    }
    return out;
  }

  // An error with one clear fix: what happened, what to do, and (optionally) the button that does it.
  function errorBox({ tag = 'div', title, body, fix, action } = {}) {
    const box = text(tag, 'error-inline');
    box.setAttribute('role', 'alert');
    const s = svg('ico');
    const use = document.createElementNS(NS, 'use');
    use.setAttribute('href', '#i-alert');
    s.appendChild(use);
    const main = text('div');
    if (title) main.appendChild(text('b', null, title));
    if (body) main.appendChild(text('p', null, body));
    if (fix) main.appendChild(text('p', 'fix', fix));
    if (action) {
      const b = text('button', 'btn small', action.label);
      b.type = 'button';
      b.addEventListener('click', action.onClick);
      main.appendChild(b);
    }
    box.append(s, main);
    return box;
  }

  // An <img> whose file is missing (for example a build without renderer/assets) hides itself
  // instead of showing a broken-image box.
  document.addEventListener(
    'error',
    (ev) => {
      const t = ev.target;
      if (t && t.tagName === 'IMG') t.classList.add('img-missing');
    },
    true
  );
  // Images in the page itself may have failed before this script ran.
  window.addEventListener('load', () => {
    for (const i of document.images) if (i.complete && !i.naturalWidth) i.classList.add('img-missing');
  });

  window.RealmArt = { EVENTS, HOUSES, TITLES, eventMeta, eventMark, icon, houseMark, greatHouse, dyeFor, seasonOf, seasonBadge, titleFor, titleBadge, emptyState, skeletonRows, errorBox };
})();
