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
    if (players != null && max && players >= max) card.appendChild(el('p', 'sc-note', 'Full right now. If you join, the game puts you in its queue and lets you in when a place opens.'));
    const actions = el('div', 'sc-actions');
    const joinBtn = el('button', 'btn primary');
    joinBtn.dataset.join = s.id;
    joinBtn.append(icon('i-play', 'ico'), el('span', null, 'Join'));
    if (st && st.online === false) joinBtn.disabled = true;
    const copyBtn = el('button', 'btn ghost small');
    copyBtn.dataset.copy = s.id;
    copyBtn.title = 'Copy the address';
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

  async function doJoin(id, viaBest) {
    try {
      const r = viaBest ? await api.joinBest() : await api.join(id);
      if (r.needInstall) {
        const ok = await confirmBox({ title: r.steam === false ? 'Steam is needed' : 'Install Reign of Kings first', text: r.steam === false ? 'Realm plays through your own Steam copy of Reign of Kings. Install Steam, then the game, then press Join again.' : 'Realm plays through your own Steam copy of Reign of Kings, and it is not installed yet. Steam can install it now.', ok: r.steam === false ? 'Get Steam' : 'Install in Steam' });
        if (ok) installGame(r.steam === false);
        return;
      }
      toast(r.method === 'quick' ? `Opening Steam to join ${r.name}${r.why ? ' (' + r.why + ')' : ''}. The address is copied too.` : `Opening Steam. Then Direct Connect to ${r.address} port ${r.port} (address copied).`, 'ok');
    } catch (e) {
      fail(e);
    }
  }

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
        toast('Copied ' + (await api.copyAddress(c.dataset.copy)) + '.', 'ok');
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

  let introStep = 0;
  function renderIntro() {
    const c = CARDS[introStep];
    $('onboard-glyph').firstElementChild.setAttribute('href', '#' + c.glyph);
    $('onboard-kicker').textContent = `${introStep + 1} of ${CARDS.length}`;
    $('onboard-title').textContent = c.title;
    $('onboard-text').textContent = c.text;
    $('onboard-cmd').textContent = c.cmd;
    const dots = $('onboard-dots');
    dots.replaceChildren();
    CARDS.forEach((_, i) => dots.appendChild(el('i', i === introStep ? 'on' : '')));
    const last = introStep === CARDS.length - 1;
    $('onboard-next').textContent = last ? 'Enter the Realm' : 'Next';
    $('onboard-rules').hidden = !last || !(config && config.links.some((l) => l.id === 'rules'));
  }
  function openIntro() {
    introStep = 0;
    renderIntro();
    $('onboard').hidden = false;
  }
  async function closeIntro() {
    $('onboard').hidden = true;
    try {
      prefs = await api.setPrefs({ onboarded: true });
    } catch {
      /* ignore */
    }
    checkLink();
  }
  $('onboard-next').addEventListener('click', () => {
    if (introStep < CARDS.length - 1) {
      introStep++;
      renderIntro();
    } else closeIntro();
  });
  $('onboard-skip').addEventListener('click', closeIntro);
  $('onboard-rules').addEventListener('click', () => api.openLink('rules').catch(fail));
  $('guide-intro').addEventListener('click', openIntro);

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
    if (prefs && !prefs.onboarded) openIntro();
    else checkLink();
  }

  start();
})();
