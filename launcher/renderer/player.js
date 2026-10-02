'use strict';

// Realm (player edition) renderer. Talks only to window.realm from player/preload.js.
(function () {
  const api = window.realm;
  const $ = (id) => document.getElementById(id);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  if (!api) {
    document.body.textContent = 'The Realm bridge is unavailable.';
    return;
  }

  // ---------------------------------------------------------------- helpers

  function el(tag, cls, text) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text != null) node.textContent = String(text);
    return node;
  }

  function icon(id, cls) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    if (cls) svg.setAttribute('class', cls);
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', '#' + id);
    svg.appendChild(use);
    return svg;
  }

  function toast(message, kind) {
    const t = el('div', 'toast' + (kind ? ' ' + kind : ''), message);
    $('toasts').appendChild(t);
    setTimeout(() => t.remove(), kind === 'bad' ? 9000 : 6000);
    while ($('toasts').children.length > 4) $('toasts').firstChild.remove();
  }

  function fail(e) {
    toast((e && e.message) || String(e), 'bad');
  }

  function ago(iso) {
    const ms = Date.now() - Date.parse(iso);
    if (!Number.isFinite(ms)) return '';
    const s = Math.max(0, Math.round(ms / 1000));
    if (s < 60) return 'just now';
    const m = Math.round(s / 60);
    if (m < 60) return m + ' min ago';
    const h = Math.round(m / 60);
    if (h < 48) return h + ' h ago';
    return Math.round(h / 24) + ' days ago';
  }

  function when(iso) {
    const t = Date.parse(iso);
    return Number.isFinite(t) ? new Date(t).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' }) : '-';
  }

  function confirmBox({ title, text, ok = 'OK' }) {
    return new Promise((resolve) => {
      $('modal-title').textContent = title;
      $('modal-text').textContent = text;
      $('modal-extra').replaceChildren();
      const okBtn = $('modal-ok');
      okBtn.textContent = ok;
      $('modal').hidden = false;
      okBtn.focus();
      const done = (v) => {
        $('modal').hidden = true;
        okBtn.onclick = null;
        $('modal-cancel').onclick = null;
        resolve(v);
      };
      okBtn.onclick = () => done(true);
      $('modal-cancel').onclick = () => done(false);
    });
  }

  // ---------------------------------------------------------------- navigation

  const views = {};
  let current = 'home';
  function go(name) {
    if (!views[name]) return;
    current = name;
    document.body.dataset.view = name;
    for (const v of $$('.view')) v.hidden = v.dataset.view !== name;
    for (const b of $$('.rail-btn[data-go]')) b.classList.toggle('active', b.dataset.go === name);
    if (views[name].show) views[name].show();
  }

  // ---------------------------------------------------------------- state

  const ROMAN = ['I', 'II', 'III', 'IV', 'V', 'VI', 'VII', 'VIII', 'IX', 'X', 'XI', 'XII', 'XIII', 'XIV', 'XV', 'XVI'];
  let list = null;
  let statuses = {};
  let install = null;
  let prefs = null;

  // The best pick comes from the main process (lib/shared/pick.js), with the reason.
  let best = null;
  function bestId() {
    return best ? best.id : null;
  }

  // ================================================================ HOME

  function statusPill(st) {
    if (!st) return ['', 'Checking'];
    if (st.online === true) {
      const max = st.maxPlayers;
      if (Number.isInteger(st.players) && max && st.players >= max) return ['warn', 'Full'];
      return ['ok', 'Online'];
    }
    if (st.online === false) return ['bad', 'Offline'];
    return ['', 'Unknown'];
  }

  function serverCard(s, i, isBest) {
    const st = statuses[s.id];
    const card = el('article', 'server-card' + (isBest ? ' best' : ''));
    card.dataset.id = s.id;
    card.appendChild(el('div', 'sc-num', ROMAN[i] || String(i + 1)));
    card.appendChild(el('div', 'sc-name', s.name));
    const [kind, label] = statusPill(st);
    const pill = el('span', 'pill sc-pill ' + kind, label);
    card.appendChild(pill);
    card.appendChild(el('div', 'sc-sub', [s.region, `${s.address}:${s.port}`].filter(Boolean).join(' · ')));
    card.appendChild(el('div', 'sc-ping', st && Number.isFinite(st.pingMs) ? `${Math.round(st.pingMs)} ms` : st ? '- ms' : ''));
    const barRow = el('div', 'sc-bar');
    const bar = el('div', 'bar');
    const fill = el('i');
    const max = (st && st.maxPlayers) || s.maxPlayers;
    const players = st && Number.isInteger(st.players) ? st.players : null;
    fill.style.width = players != null && max ? Math.min(100, Math.round((players / max) * 100)) + '%' : '0';
    bar.appendChild(fill);
    barRow.append(bar, el('span', 'count', players != null ? `${st.approx ? '~' : ''}${players} / ${max}` : `? / ${max}`));
    card.appendChild(barRow);
    if (allegiance && st && Array.isArray(st.houses) && st.houses.some((h) => h && h.name.toLowerCase() === allegiance.name.toLowerCase())) {
      card.classList.add('sworn');
      card.appendChild(el('p', 'sc-house', (st.house && st.house.toLowerCase() === allegiance.name.toLowerCase() ? 'Your house holds the crown here' : 'House ' + allegiance.name + ' plays here')));
    }
    if (players != null && max && players >= max) card.appendChild(el('p', 'sc-note', 'Full right now. If you join, the game puts you in its queue and lets you in when a place opens.'));
    const actions = el('div', 'sc-actions');
    const joinBtn = el('button', 'btn primary');
    joinBtn.dataset.join = s.id;
    joinBtn.append(icon('i-play', 'ico'), el('span', null, 'Join'));
    if (st && st.online === false) joinBtn.disabled = true;
    const copyBtn = el('button', 'btn ghost small');
    copyBtn.dataset.copy = s.id;
    copyBtn.title = 'Copy the address (the port goes in its own box)';
    copyBtn.setAttribute('aria-label', `Copy the address of ${s.name}`);
    copyBtn.append(icon('i-copy', 'ico'));
    actions.append(joinBtn, copyBtn);
    card.appendChild(actions);
    return card;
  }

  function renderServers() {
    const box = $('server-list');
    box.replaceChildren();
    if (!list) return;
    const pickId = bestId();
    list.servers.forEach((s, i) => box.appendChild(serverCard(s, i, s.id === pickId && list.servers.length > 1)));
    const anyUp = list.servers.some((s) => statuses[s.id] && statuses[s.id].online !== false);
    $('join-best').disabled = !anyUp && Object.keys(statuses).length > 0;
    const b = list.servers.find((s) => s.id === pickId);
    $('join-best-sub').textContent = b ? `${list.servers.length > 1 ? 'Best now: ' : ''}${b.name}${best && best.why ? ' · ' + best.why : ''}` : Object.keys(statuses).length ? 'No server is reachable right now' : 'Checking the servers...';
    renderCrown();
    renderTitle();
    renderBanner();
  }

  function renderTitle() {
    const n = list ? list.servers.filter((s) => statuses[s.id] && statuses[s.id].online === true).length : 0;
    const box = $('title-status');
    box.className = 'title-status' + (n ? ' running' : '');
    $('title-status-text').textContent = list ? `${n} of ${list.servers.length} server${list.servers.length === 1 ? '' : 's'} online` : 'Looking for servers';
  }

  function renderCrown() {
    const withKing = Object.values(statuses).find((st) => st && st.king);
    if (withKing) {
      $('king').textContent = withKing.king;
      $('king-detail').textContent = withKing.house ? 'of House ' + withKing.house : '';
    } else {
      $('king').textContent = 'The throne is empty';
      $('king-detail').textContent = list && list.servers.some((s) => s.hasChronicle) ? 'Any house may press its claim.' : 'News of the crown appears here when a server shares its Chronicle.';
    }
  }

  function renderList() {
    if (!list) return;
    const pill = $('list-pill');
    const map = { online: ['ok', 'Signed'], cache: ['warn', 'Saved copy'], bundled: ['warn', 'Built in'], builtin: ['bad', 'Test only'] };
    const [kind, label] = map[list.source] || ['', list.source];
    pill.className = 'pill ' + kind;
    pill.textContent = label;
    const parts = [];
    if (list.source === 'online') parts.push(`Signed list version ${list.seq}, checked against the Realm key in this app.`);
    if (list.source === 'cache') parts.push(`Using the last signed list you downloaded (version ${list.seq}).`);
    if (list.source === 'bundled') parts.push(`Using the signed list that came with this app (version ${list.seq}).`);
    if (list.source === 'builtin') parts.push('No signed server list is available, so only a local test server is shown.');
    if (list.reason) parts.push(list.reason);
    $('list-note').textContent = parts.join(' ');
    $('trust-source').textContent = label + (list.seq ? ` (version ${list.seq})` : '');
    $('trust-key').textContent = list.keyId || 'No key in this build';
    $('trust-expires').textContent = list.expires ? when(list.expires) : '-';
  }

  function renderInstall() {
    const i = install;
    const missing = i && i.installed === false;
    $('install-banner').hidden = !missing;
    if (missing) {
      $('install-title').textContent = i.steam === false ? 'Steam is not installed' : 'Reign of Kings is not installed';
      $('install-text').textContent = i.steam === false ? 'Realm plays through your own Steam copy of Reign of Kings. Install Steam first, then the game.' : 'Realm plays through your own Steam copy. Install it once; Steam does the rest.';
      $('install-btn').lastChild.textContent = i.steam === false ? 'Get Steam' : 'Install in Steam';
    }
    $('game-steam').textContent = !i || i.steam == null ? 'Could not check on this system' : i.steam ? 'Installed' : 'Not found';
    $('game-installed').textContent = !i || i.installed == null ? 'Could not check on this system' : i.installed ? 'Installed' + (i.library ? ` (${i.library})` : '') : 'Not installed';
    $('game-install').hidden = !!(i && i.installed);
  }

  async function loadAll(refresh) {
    try {
      list = await api.servers(refresh === true);
    } catch (e) {
      fail(e);
    }
    renderList();
    renderServers();
    try {
      const r = await api.status(refresh === true);
      statuses = r.statuses || {};
      best = r.best || null;
    } catch {
      statuses = {};
    }
    renderServers();
  }

  views.home = {
    show() {
      renderServers();
    }
  };

  // ---------------------------------------------------------------- join progress
  // Checking server -> Launching Steam -> Game starting -> Joining <server>. Realm cannot see inside
  // the game, so the last two steps are timed guidance, not a report; the address/port boxes stay on
  // screen for the "stopped at the main menu" case. Step timings are estimates (UNVERIFIED per PC).
  const JF_STEPS = ['check', 'steam', 'game', 'join'];
  let jfTimers = [];
  let jfServer = null;
  let jfReturnFocus = null;

  function jfSet(step, state, sub) {
    const li = $('jf-steps').querySelector(`[data-step="${step}"]`);
    if (!li) return;
    li.className = state || '';
    if (sub != null) $('jf-sub-' + step).textContent = sub;
  }
  function jfStatus(text) {
    $('jf-status').textContent = text;
  }
  function jfOpen(title) {
    for (const t of jfTimers) clearTimeout(t);
    jfTimers = [];
    jfServer = null;
    jfReturnFocus = document.activeElement;
    $('jf-title').textContent = title;
    $('jf-join-label').textContent = 'Joining';
    $('jf-addr').textContent = '-';
    $('jf-port').textContent = '-';
    $('jf-manual').hidden = true;
    jfSet('check', 'now', 'Is it up, and is there room?');
    jfSet('steam', '', 'Steam starts your own copy of Reign of Kings.');
    jfSet('game', '', prefs && prefs.joinMethod === 'classic' ? 'Steam starts the game at its main menu.' : 'Steam may ask once to allow the launch options: choose OK.');
    jfSet('join', '', prefs && prefs.joinMethod === 'classic' ? 'Paste the address and type the port in Direct Connect.' : 'The game connects by itself.');
    jfStatus('Checking the server...');
    $('joinflow').hidden = false;
    $('jf-close').focus();
  }
  function jfClose() {
    for (const t of jfTimers) clearTimeout(t);
    jfTimers = [];
    $('joinflow').hidden = true;
    if (jfReturnFocus && document.contains(jfReturnFocus)) jfReturnFocus.focus();
  }
  function jfFail(step, text) {
    jfSet(step, 'bad');
    jfStatus(text);
  }
  function jfShowAddress(address, port) {
    jfServer = { address: String(address), port: String(port) };
    $('jf-addr').textContent = jfServer.address;
    $('jf-port').textContent = jfServer.port;
    $('jf-manual').hidden = false;
  }

  async function doJoin(id, viaBest) {
    const target = viaBest ? null : list && list.servers.find((s) => s.id === id);
    jfOpen(target ? target.name : 'The best server');
    // 1. checking server
    try {
      const r = await api.status(true);
      statuses = r.statuses || {};
      best = r.best || null;
      renderServers();
    } catch {
      /* status is advisory; Steam can still try */
    }
    const pickId = viaBest ? bestId() : id;
    const pick = list && list.servers.find((s) => s.id === pickId);
    const st = pick ? statuses[pick.id] : null;
    if (viaBest && !pick) return jfFail('check', 'No server is reachable right now. Try again in a minute, or press Can\'t join?.');
    if (st && st.online === false) return jfFail('check', `${pick.name} is not answering right now. Pick another server, or press Can't join? to see why.`);
    if (pick) $('jf-title').textContent = pick.name;
    const full = st && Number.isInteger(st.players) && st.maxPlayers && st.players >= st.maxPlayers;
    jfSet('check', 'done', !st || st.online == null ? 'Could not confirm it is up; trying anyway.' : full ? `Full (${st.players}/${st.maxPlayers}): the game queues you.` : `Up${Number.isInteger(st.players) ? `, ${st.approx ? '~' : ''}${st.players}/${st.maxPlayers} players` : ''}${Number.isFinite(st.pingMs) ? `, ${Math.round(st.pingMs)} ms` : ''}.`);
    // 2. launching steam
    jfSet('steam', 'now');
    jfStatus('Asking Steam to start Reign of Kings...');
    let r;
    try {
      r = viaBest ? await api.joinBest() : await api.join(id);
    } catch (e) {
      return jfFail('steam', (e && e.message) || String(e));
    }
    if (r.needInstall) {
      jfClose();
      const ok = await confirmBox({ title: r.steam === false ? 'Steam is needed' : 'Install Reign of Kings first', text: r.steam === false ? 'Realm plays through your own Steam copy of Reign of Kings. Install Steam, then the game, then press Join again.' : 'Realm plays through your own Steam copy of Reign of Kings, and it is not installed yet. Steam can install it now.', ok: r.steam === false ? 'Get Steam' : 'Install in Steam' });
      if (ok) installGame(r.steam === false);
      return;
    }
    $('jf-title').textContent = r.name;
    $('jf-join-label').textContent = 'Joining ' + r.name;
    jfShowAddress(r.address, r.port);
    jfSet('steam', 'done', 'Steam has the request. The address is copied.');
    // 3. game starting, 4. joining (timed guidance)
    jfSet('game', 'now');
    jfStatus(r.method === 'quick' ? 'Steam is starting your game. Choose OK if it asks about launch options.' : 'Steam is starting your game at its main menu.');
    jfTimers.push(
      setTimeout(() => {
        jfSet('game', 'done');
        jfSet('join', 'now');
        jfStatus(r.method === 'quick' ? `The game should now be connecting to ${r.name}. If it stays at the main menu, use the two boxes below.` : `In the game: Direct Connect, paste the address, type port ${r.port}, Join.`);
      }, 9000)
    );
    jfTimers.push(setTimeout(() => jfSet('join', 'done'), 30000));
  }

  async function copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      return false;
    }
  }
  $('jf-copy-addr').addEventListener('click', async () => {
    if (!jfServer) return;
    toast((await copyText(jfServer.address)) ? `Copied the address ${jfServer.address}. Paste it in the address box.` : `Could not copy. Type ${jfServer.address} in the address box.`, 'ok');
  });
  $('jf-copy-port').addEventListener('click', async () => {
    if (!jfServer) return;
    toast((await copyText(jfServer.port)) ? `Copied the port ${jfServer.port}. Paste it in the port box.` : `Could not copy. Type ${jfServer.port} in the port box.`, 'ok');
  });
  $('jf-close').addEventListener('click', jfClose);
  $('joinflow').addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape') jfClose();
  });

  function installGame(steamMissing) {
    (steamMissing ? api.openLink('steam') : api.installGame()).catch(fail);
  }

  $('join-best').addEventListener('click', () => doJoin(null, true));
  $('server-list').addEventListener('click', async (ev) => {
    const j = ev.target.closest('[data-join]');
    if (j) return doJoin(j.dataset.join);
    const c = ev.target.closest('[data-copy]');
    if (c) {
      try {
        toast('Copied ' + (await api.copyAddress(c.dataset.copy)) + '. In Direct Connect, paste it in the address box and type the port in the port box.', 'ok');
      } catch (e) {
        fail(e);
      }
    }
  });
  $('install-btn').addEventListener('click', () => installGame(install && install.steam === false));
  $('game-install').addEventListener('click', () => installGame(install && install.steam === false));
  $('list-refresh').addEventListener('click', async () => {
    $('list-pill').textContent = 'Checking';
    await loadAll(true);
    loadFeed(true);
    toast('Server list and status refreshed.', 'ok');
  });

  // ================================================================ GUIDE + INTRO

  const CARDS = [
    { glyph: 'i-shield', title: 'Found a house', text: 'A house is your faction: a name, a sigil, a leader and up to three officers. Recruit friends and grow it.', cmd: '/house found "Ashveil" Grey Heron' },
    { glyph: 'i-chain', title: 'Swear an oath', text: 'A house can swear fealty to a stronger liege and become its vassal. Breaking an oath marks your house as oathbreakers, for everyone to see.', cmd: '/swear <house>' },
    { glyph: 'i-crown', title: 'Hold the crown', text: 'Whoever holds the Old Throne is king. The king issues decrees, names a council and sets the tax, within limits.', cmd: '/crown' },
    { glyph: 'i-flame', title: 'Rebel by the rules', text: 'An occupied throne can only be taken in a scheduled rebellion window, by a house that declared its claim at least an hour before.', cmd: '/claim declare' },
    { glyph: 'i-scroll', title: 'Ransom, not torment', text: 'Captives can be held for 10 minutes at most, for a ransom of up to 500 gold. Betrayal is the game; harassment is not.', cmd: '/ransom list' }
  ];

  function renderGuide() {
    const grid = $('guide-grid');
    grid.replaceChildren();
    for (const c of CARDS) {
      const card = el('article', 'card guide-card');
      card.append(icon(c.glyph, 'glyph'), el('h3', null, c.title), el('p', null, c.text), el('span', 'mono', c.cmd));
      grid.appendChild(card);
    }
    const rules = el('article', 'card guide-card');
    rules.append(icon('i-banners', 'glyph'), el('h3', null, 'Every act is written down'), el('p', null, 'Coronations, oaths, betrayals and ransoms go into the public Chronicle with player names only, never places or inventories. Type /chronicle in game.'), el('span', 'mono', '/chronicle'));
    grid.appendChild(rules);
  }

  views.guide = { show: renderGuide };

  // ================================================================ FIRST RUN
  // Sigil reveal -> sworn allegiance (six great houses, docs/community/lore.md) -> 4-card crown tour.

  // Colours: primary = the overlay dye for that name (chronicle/public/assets/common.js), secondary = lore.
  const HOUSES = [
    { id: 'varrow', name: 'Varrow', sigil: 'Iron Stag', emblem: 'em-stag', field: '#4a2347', metal: '#b7bcc4', words: 'We Stand Our Ground.', hook: 'Crown-holders: take the throne and keep it.' },
    { id: 'ashgrove', name: 'Ashgrove', sigil: 'White Oak', emblem: 'em-oak', field: '#7a3a1a', metal: '#efe6d2', words: 'Deep Roots, Long Memory.', hook: 'Steady liege or loyal vassal; builders and diplomats.' },
    { id: 'corvane', name: 'Corvane', sigil: 'Black Raven', emblem: 'em-raven', field: '#2c3b42', metal: '#d4d9de', words: 'Every Secret Has a Price.', hook: 'Intrigue: treaties, timing and the right betrayal.' },
    { id: 'dunmere', name: 'Dunmere', sigil: 'Drowned Bell', emblem: 'em-bell', field: '#5a5a22', metal: '#c9a46a', words: 'The Tide Returns.', hook: 'Rebels: declare the claim, fight in the Lawful Hours.' },
    { id: 'halloran', name: 'Halloran', sigil: 'Ember Hound', emblem: 'em-hound', field: '#3a2a1a', metal: '#f08a3a', words: 'Loyal Until the Last Coal.', hook: 'Hired swords and fair, fast ransoms.' },
    { id: 'merrin', name: 'Merrin', sigil: 'Silver Eel', emblem: 'em-eel', field: '#24472d', metal: '#d4d9de', words: 'Slip the Net.', hook: 'Traders and go-betweens who stay neutral and matter.' }
  ];
  const houseById = (id) => HOUSES.find((h) => h.id === id) || null;

  // The allegiance lives in this PC's browser storage for the Realm window only; it is never sent anywhere.
  const ALLEGIANCE_KEY = 'realm.allegiance';
  function loadAllegiance() {
    try {
      return houseById(localStorage.getItem(ALLEGIANCE_KEY));
    } catch {
      return null;
    }
  }
  function saveAllegiance(h) {
    try {
      if (h) localStorage.setItem(ALLEGIANCE_KEY, h.id);
      else localStorage.removeItem(ALLEGIANCE_KEY);
    } catch {
      /* storage blocked: the choice lasts for this session only */
    }
  }
  let allegiance = loadAllegiance();

  function shieldSvg(h, cls) {
    const NS = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(NS, 'svg');
    svg.setAttribute('viewBox', '0 0 120 140');
    svg.setAttribute('aria-hidden', 'true');
    if (cls) svg.setAttribute('class', cls);
    const field = document.createElementNS(NS, 'path');
    field.setAttribute('d', 'M10 8h100v52c0 38-24 62-50 74C34 122 10 98 10 60z');
    field.setAttribute('fill', h.field);
    field.setAttribute('stroke', h.metal);
    field.setAttribute('stroke-width', '4');
    const shine = document.createElementNS(NS, 'path');
    shine.setAttribute('d', 'M10 8h100v52c0 38-24 62-50 74C34 122 10 98 10 60z');
    shine.setAttribute('fill', 'url(#fieldShine)');
    const em = document.createElementNS(NS, 'use');
    em.setAttribute('href', '#' + h.emblem);
    em.setAttribute('x', '28');
    em.setAttribute('y', '26');
    em.setAttribute('width', '64');
    em.setAttribute('height', '64');
    em.setAttribute('fill', h.metal);
    em.setAttribute('color', h.metal);
    svg.append(field, shine, em);
    return svg;
  }

  // "How the crown works": four cards, matching the plugins (see docs/community/how-to-play.md).
  const TOUR = [
    { glyph: 'i-shield', title: 'Houses and oaths', text: 'A house is your faction: a name, a sigil, a leader and officers. Houses swear fealty to a stronger liege. Breaking an oath brands your house an oathbreaker for all to see.', cmd: '/house found Varrow Iron Stag' },
    { glyph: 'i-crown', title: 'The crown is the seat', text: 'Whoever sits the Old Throne is king. The crown spends authority on decrees, names a council of three and sets the tax, always within the Charter\'s limits.', cmd: '/crown' },
    { glyph: 'i-flame', title: 'Claims and the Lawful Hours', text: 'No crown is taken by stealth. Declare your claim at least an hour before a rebellion window, then fight for the throne while the window is open. /crown shows the next one.', cmd: '/claim declare' },
    { glyph: 'i-scroll', title: 'The Chronicle remembers', text: 'Coronations, oaths, betrayals and ransoms are written down with player names only. Captives are held 10 minutes at most. Betrayal is the game; harassment is not.', cmd: '/chronicle' }
  ];

  let tourStep = 0;
  let pickedHouse = null;

  function stage(name) {
    for (const id of ['ob-reveal', 'ob-houses', 'ob-tour']) $(id).hidden = id !== name;
    const focusTarget = { 'ob-reveal': 'ob-begin', 'ob-houses': null, 'ob-tour': 'onboard-next' }[name];
    if (name === 'ob-houses') {
      const first = $('house-grid').querySelector('input:checked') || $('house-grid').querySelector('input');
      if (first) first.focus();
    } else if (focusTarget) $(focusTarget).focus();
  }

  function renderHouseGrid() {
    const grid = $('house-grid');
    grid.replaceChildren();
    for (const h of HOUSES) {
      const label = el('label', 'house-opt');
      const input = document.createElement('input');
      input.type = 'radio';
      input.name = 'allegiance';
      input.value = h.id;
      input.checked = !!(pickedHouse && pickedHouse.id === h.id);
      const body = el('span', 'house-body');
      body.append(el('b', 'house-name', 'House ' + h.name), el('span', 'house-sigil', 'The ' + h.sigil), el('i', 'house-words', '"' + h.words + '"'), el('span', 'house-hook', h.hook));
      label.append(input, shieldSvg(h, 'house-shield'), body);
      grid.appendChild(label);
    }
    syncSwear();
  }

  function syncSwear() {
    $('ob-swear').disabled = !pickedHouse;
    $('ob-swear').textContent = pickedHouse ? 'Swear to House ' + pickedHouse.name : 'Choose a house';
    for (const l of $$('.house-opt', $('house-grid'))) l.classList.toggle('on', !!pickedHouse && l.querySelector('input').value === pickedHouse.id);
  }

  function renderTour() {
    const c = TOUR[tourStep];
    $('onboard-glyph').firstElementChild.setAttribute('href', '#' + c.glyph);
    $('onboard-kicker').textContent = `How the crown works · ${tourStep + 1} of ${TOUR.length}`;
    $('onboard-title').textContent = c.title;
    $('onboard-text').textContent = c.text;
    $('onboard-cmd').textContent = c.cmd;
    const dots = $('onboard-dots');
    dots.replaceChildren();
    TOUR.forEach((_, i) => dots.appendChild(el('i', i === tourStep ? 'on' : '')));
    const last = tourStep === TOUR.length - 1;
    $('onboard-next').textContent = last ? 'Take the road' : 'Next';
    $('onboard-back').hidden = false;
    $('onboard-rules').hidden = !last || !(config && config.links.some((l) => l.id === 'rules'));
    const card = $('ob-tour');
    card.classList.remove('flip');
    void card.offsetWidth; // restart the card animation
    card.classList.add('flip');
  }

  // fromStep: 'reveal' (first run), 'houses' (change allegiance from Home) or 'tour' (Guide button).
  let introMode = 'first';
  function openIntro(fromStep = 'reveal') {
    introMode = fromStep === 'reveal' ? 'first' : fromStep;
    tourStep = 0;
    pickedHouse = allegiance;
    $('ob-heading').textContent = (config && config.realmName) || 'The Realm';
    $('ob-tagline').textContent = (config && config.tagline) || '';
    renderHouseGrid();
    renderTour();
    $('onboard').hidden = false;
    $('onboard').classList.toggle('replay', fromStep !== 'reveal');
    stage(fromStep === 'houses' ? 'ob-houses' : fromStep === 'tour' ? 'ob-tour' : 'ob-reveal');
  }
  async function closeIntro() {
    $('onboard').hidden = true;
    renderServers(); // also redraws the banner and the "your house plays here" marks
    if (introMode !== 'first') return;
    try {
      prefs = await api.setPrefs({ onboarded: true });
    } catch {
      /* ignore */
    }
    checkLink();
  }
  $('ob-begin').addEventListener('click', () => stage('ob-houses'));
  $('house-grid').addEventListener('change', (ev) => {
    if (ev.target.name !== 'allegiance') return;
    pickedHouse = houseById(ev.target.value);
    syncSwear();
  });
  function swear(h) {
    allegiance = h;
    saveAllegiance(h);
    if (introMode === 'houses') {
      if (h) toast(`You are sworn to House ${h.name}. "${h.words}"`, 'ok');
      closeIntro();
    } else stage('ob-tour');
  }
  $('ob-swear').addEventListener('click', () => pickedHouse && swear(pickedHouse));
  $('ob-unsworn').addEventListener('click', () => swear(null));
  $('onboard-next').addEventListener('click', () => {
    if (tourStep < TOUR.length - 1) {
      tourStep++;
      renderTour();
    } else closeIntro();
  });
  $('onboard-back').addEventListener('click', () => {
    if (tourStep > 0) {
      tourStep--;
      renderTour();
    } else if (introMode === 'first') stage('ob-houses');
    else closeIntro();
  });
  $('onboard-skip').addEventListener('click', closeIntro);
  $('onboard-rules').addEventListener('click', () => api.openLink('rules').catch(fail));
  $('guide-intro').addEventListener('click', () => openIntro('tour'));
  $('onboard').addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape' && introMode !== 'first') closeIntro();
  });

  // ================================================================ ALLEGIANCE ON HOME

  // Where the sworn house already plays: its king's server, else the server where it has the most
  // members, else the best pick (and the in-game command to found it).
  function suggestion() {
    if (!allegiance || !list) return null;
    const name = allegiance.name.toLowerCase();
    let crowned = null;
    let most = null;
    for (const s of list.servers) {
      const st = statuses[s.id];
      if (!st || st.online === false) continue;
      if (st.house && st.house.toLowerCase() === name) crowned = crowned || s;
      const h = Array.isArray(st.houses) ? st.houses.find((x) => x && x.name.toLowerCase() === name) : null;
      if (h && (!most || (h.members || 0) > (most.members || 0))) most = { server: s, members: h.members };
    }
    if (crowned) return { server: crowned, text: `House ${allegiance.name} holds the crown on ${crowned.name}. Ride to defend it.` };
    if (most) return { server: most.server, text: `House ${allegiance.name} has ${most.members != null ? most.members + ' sworn' : 'a seat'} on ${most.server.name}.` };
    const b = list.servers.find((s) => s.id === bestId());
    return { server: b || null, found: true, text: `No server has a House ${allegiance.name} yet. Be its founder: /house found ${allegiance.name} ${allegiance.sigil}` };
  }

  function renderBanner() {
    const h = allegiance;
    $('banner-card').classList.toggle('sworn', !!h);
    $('banner-field').setAttribute('fill', h ? h.field : '#2a1d14');
    $('banner-field').setAttribute('stroke', h ? h.metal : '#7a5a1e');
    $('banner-emblem').setAttribute('href', '#' + (h ? h.emblem : 'i-banners'));
    $('banner-emblem').setAttribute('fill', h ? h.metal : '#a3927a');
    $('banner-emblem').setAttribute('color', h ? h.metal : '#a3927a');
    $('banner-name').textContent = h ? 'House ' + h.name : 'Unsworn';
    $('banner-words').textContent = h ? `"${h.words}"` : 'Pick a great house to follow its story.';
    $('banner-change').lastChild.textContent = h ? 'Change' : 'Choose a house';
    const sg = suggestion();
    $('banner-suggest').textContent = sg ? sg.text : '';
    const canJoin = !!(sg && sg.server && !(statuses[sg.server.id] && statuses[sg.server.id].online === false));
    $('banner-join').hidden = !canJoin;
    if (canJoin) {
      $('banner-join').dataset.join = sg.server.id;
      $('banner-join-label').textContent = 'Join ' + sg.server.name;
    }
  }
  $('banner-change').addEventListener('click', () => openIntro('houses'));
  $('banner-join').addEventListener('click', (ev) => {
    const id = ev.currentTarget.dataset.join;
    if (id) doJoin(id);
  });

  // ================================================================ REALM FEED
  // Latest public Chronicle events from every listed server, plus a countdown to the next event when
  // a server's Chronicle announces one (lib/shared/feed.js documents that optional field).

  const FEED_GLYPH = { coronation: 'i-crown', abdication: 'i-crown', claim_declared: 'i-flame', rebellion_started: 'i-flame', rebellion_ended: 'i-flame', house_founded: 'i-shield', oath_sworn: 'i-chain', oath_broken: 'i-chain', treaty_signed: 'i-seal', treaty_broken: 'i-seal', decree: 'i-scroll', ransom_set: 'i-chest', ransom_paid: 'i-chest', released: 'i-chest' };
  let feed = null;
  let countTimer = null;

  function renderFeed() {
    const ol = $('feed-list');
    ol.replaceChildren();
    const items = (feed && feed.events) || [];
    if (!items.length) {
      const anyChronicle = list && list.servers.some((s) => s.hasChronicle);
      ol.appendChild(el('li', 'feed-empty', anyChronicle ? 'The Chronicle is quiet. Oaths, claims and coronations appear here as they happen.' : 'No server in the list shares its Chronicle yet, so there is no news to show.'));
    }
    const many = list && list.servers.length > 1;
    for (const e of items.slice(0, 6)) {
      const li = el('li', 'feed-item t-' + e.type);
      const body = el('div');
      body.append(el('p', 'feed-title', e.title), el('p', 'feed-meta', [ago(e.ts), many ? e.serverName : null].filter(Boolean).join(' · ')));
      li.append(icon(FEED_GLYPH[e.type] || 'i-banners', 'feed-ico'), body);
      ol.appendChild(li);
    }
    $('feed-updated').textContent = feed && feed.at ? 'updated ' + ago(feed.at) : '';
    const n = feed && feed.next;
    $('feed-next').hidden = !n;
    clearInterval(countTimer);
    if (n) {
      $('feed-next-title').textContent = n.title;
      $('feed-next-where').textContent = `${many ? n.serverName + ' · ' : ''}${new Date(Date.parse(n.at)).toLocaleString(undefined, { weekday: 'long', hour: '2-digit', minute: '2-digit' })}`;
      tickCount();
      countTimer = setInterval(tickCount, 1000);
    }
  }

  function tickCount() {
    const n = feed && feed.next;
    if (!n) return;
    const ms = Math.max(0, Date.parse(n.at) - Date.now());
    const t = Math.floor(ms / 1000);
    const pad = (v) => String(v).padStart(2, '0');
    $('feed-count-d').textContent = String(Math.floor(t / 86400));
    $('feed-count-h').textContent = pad(Math.floor((t % 86400) / 3600));
    $('feed-count-m').textContent = pad(Math.floor((t % 3600) / 60));
    $('feed-count-s').textContent = pad(t % 60);
    $('feed-count').setAttribute('aria-label', ms ? `Starts in ${Math.floor(t / 86400)} days ${Math.floor((t % 86400) / 3600)} hours ${Math.floor((t % 3600) / 60)} minutes` : 'Starting now');
    if (!ms) {
      clearInterval(countTimer);
      $('feed-next-where').textContent = 'Starting now. Join in!';
    }
  }

  async function loadFeed(refresh) {
    if (!api.feed) return;
    try {
      feed = await api.feed(refresh === true);
    } catch {
      /* the Chronicle is optional */
    }
    renderFeed();
  }

  // ================================================================ SETTINGS

  function renderPrefs() {
    if (!prefs) return;
    $('jm-quick').checked = prefs.joinMethod === 'quick';
    $('jm-classic').checked = prefs.joinMethod === 'classic';
    $('pref-background').checked = prefs.background;
    $('pref-notify').checked = prefs.notifications;
    $('notify-note').textContent = list && list.servers.some((s) => s.hasChronicle) ? 'Notifications come from each server\'s public Chronicle.' : 'No server in the list shares a Chronicle yet, so there is nothing to notify about.';
  }

  views.settings = {
    async show() {
      renderPrefs();
      renderList();
      try {
        install = await api.installState();
      } catch {
        /* ignore */
      }
      renderInstall();
    }
  };

  async function savePrefs(patch, msg) {
    try {
      prefs = await api.setPrefs(patch);
      renderPrefs();
      if (msg) toast(msg, 'ok');
    } catch (e) {
      fail(e);
    }
  }
  for (const r of $$('input[name="join-method"]')) {
    r.addEventListener('change', () => savePrefs({ joinMethod: r.value }, r.value === 'quick' ? 'Join will connect you directly.' : 'Join will open the game and show where to paste the address.'));
  }
  $('pref-background').addEventListener('change', (e) => savePrefs({ background: e.target.checked }, e.target.checked ? 'Realm stays in the tray when you close the window.' : 'Closing the window quits Realm.'));
  $('pref-notify').addEventListener('change', (e) => savePrefs({ notifications: e.target.checked }, e.target.checked ? 'Notifications on.' : 'Notifications off.'));

  // ================================================================ deep links

  async function confirmLink(id) {
    const s = list && list.servers.find((x) => x.id === id);
    if (!s) return;
    if (current !== 'home') go('home');
    const st = statuses[id];
    const extra = st && Number.isInteger(st.players) ? ` ${st.players} of ${st.maxPlayers || s.maxPlayers} players are on it now.` : '';
    const ok = await confirmBox({ title: `Join ${s.name}?`, text: `A link asked Realm to join ${s.name} (${s.address}:${s.port}).${extra} This server is in the signed Realm list.`, ok: 'Join' });
    if (ok) doJoin(id);
  }

  async function checkLink() {
    if (!$('onboard').hidden) return;
    try {
      const id = await api.takeLink();
      if (id) confirmLink(id);
    } catch {
      /* ignore */
    }
  }

  // ================================================================ push + start

  api.onPush((msg) => {
    if (msg.type === 'status') {
      statuses = msg.statuses || {};
      best = msg.best || null;
      renderServers();
    } else if (msg.type === 'servers') loadAll(false);
    else if (msg.type === 'deeplink') {
      if (msg.error) toast(msg.error, 'bad');
      else checkLink();
    }
  });

  document.addEventListener('click', (ev) => {
    const linkBtn = ev.target.closest('[data-link]');
    if (linkBtn) api.openLink(linkBtn.dataset.link).catch(fail);
    const winBtn = ev.target.closest('[data-win]');
    if (winBtn) api.windowAction(winBtn.dataset.win).catch(() => {});
    const nav = ev.target.closest('[data-go]');
    if (nav) go(nav.dataset.go);
  });

  let config = null;

  async function start() {
    try {
      config = await api.getConfig();
      $('realm-name').textContent = config.realmName;
      $('tagline').textContent = config.tagline;
      document.title = config.realmName;
      const links = $('links');
      links.replaceChildren();
      for (const l of config.links) {
        const b = el('button', 'btn ghost');
        b.dataset.link = l.id;
        b.append(icon('i-link', 'ico'), el('span', null, l.label));
        links.appendChild(b);
      }
    } catch (e) {
      fail(e);
    }
    try {
      $('app-version').textContent = 'v' + (await api.appInfo()).version;
    } catch {
      /* ignore */
    }
    try {
      prefs = await api.prefs();
    } catch {
      prefs = null;
    }
    go('home');
    await loadAll(false);
    try {
      install = await api.installState();
      renderInstall();
    } catch {
      /* ignore */
    }
    renderBanner();
    loadFeed(false);
    setInterval(() => {
      if (current === 'home' && !document.hidden) loadFeed(false);
    }, 60000);
    if (prefs && !prefs.onboarded) openIntro('reveal');
    else checkLink();
  }

  start();
})();
