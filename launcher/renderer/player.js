'use strict';

// Realm (player edition) renderer: one screen. Talks only to window.realm from player/preload.js.
//   brand and key art, Play (or Install), server status, News, What's new in the realm, updates.
(function () {
  const api = window.realm;
  const $ = (id) => document.getElementById(id);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  if (!api) {
    document.body.textContent = 'The Realm bridge is unavailable.';
    return;
  }
  const ART = window.RealmArt || null;

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
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', '#' + id);
    svg.appendChild(use);
    return svg;
  }

  // Icons from the art pack (assets/icons.svg, a copy of art/sprite/icons.svg).
  function artIcon(name, cls) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    if (cls) svg.setAttribute('class', cls);
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', 'assets/icons.svg#realm-icon-' + name);
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
    if (h < 24) return h + ' h ago';
    const d = Math.round(h / 24);
    return d === 1 ? 'yesterday' : d + ' days ago';
  }

  function day(iso) {
    const t = Date.parse(iso);
    if (!Number.isFinite(t)) return '';
    const sameYear = new Date(t).getFullYear() === new Date().getFullYear();
    return new Date(t).toLocaleDateString(undefined, sameYear ? { day: 'numeric', month: 'short' } : { day: 'numeric', month: 'short', year: 'numeric' });
  }

  function until(iso) {
    const ms = Date.parse(iso) - Date.now();
    if (!Number.isFinite(ms)) return '';
    if (ms <= 0) return 'now';
    const h = Math.round(ms / 3600e3);
    if (h < 1) return 'in ' + Math.max(1, Math.round(ms / 60e3)) + ' min';
    if (h < 36) return 'in ' + h + ' h';
    return 'in ' + Math.round(h / 24) + ' days';
  }

  function when(iso) {
    const t = Date.parse(iso);
    return Number.isFinite(t) ? new Date(t).toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) : '';
  }

  function mb(bytes) {
    return Number.isFinite(bytes) && bytes > 0 ? (bytes / 1048576).toFixed(bytes < 10 * 1048576 ? 1 : 0) + ' MB' : '';
  }

  // Dialogs: Tab stays inside, Escape closes, focus returns to where it was.
  function trapFocus(modal, onEscape) {
    const card = modal.querySelector('.modal-card, .joinflow-card');
    const onKey = (ev) => {
      if (ev.key === 'Escape') {
        ev.preventDefault();
        onEscape();
      } else if (ev.key === 'Tab') {
        const f = $$('button:not([disabled]):not([hidden])', card).filter((b) => b.offsetParent !== null);
        if (!f.length) return;
        const i = f.indexOf(document.activeElement);
        if (ev.shiftKey && i <= 0) {
          ev.preventDefault();
          f[f.length - 1].focus();
        } else if (!ev.shiftKey && i === f.length - 1) {
          ev.preventDefault();
          f[0].focus();
        }
      }
    };
    modal.addEventListener('keydown', onKey);
    return () => modal.removeEventListener('keydown', onKey);
  }

  function confirmBox({ title, text, ok = 'OK', kicker = null }) {
    return new Promise((resolve) => {
      const modal = $('modal');
      const back = document.activeElement;
      $('modal-kicker').textContent = kicker || '';
      $('modal-kicker').hidden = !kicker;
      $('modal-title').textContent = title;
      $('modal-text').textContent = text;
      const okBtn = $('modal-ok');
      const cancelBtn = $('modal-cancel');
      okBtn.textContent = ok;
      modal.hidden = false;
      okBtn.focus();
      const untrap = trapFocus(modal, () => done(false));
      function done(v) {
        modal.hidden = true;
        okBtn.onclick = null;
        cancelBtn.onclick = null;
        untrap();
        if (back && document.contains(back) && typeof back.focus === 'function') back.focus();
        resolve(v);
      }
      okBtn.onclick = () => done(true);
      cancelBtn.onclick = () => done(false);
    });
  }

  // ---------------------------------------------------------------- state

  let config = null;
  let list = null;
  let listError = null;
  let statuses = {};
  let best = null;
  let install = null;
  let newsData = null;
  let upd = null;
  let filter = 'all';

  const PICK_KEY = 'realm.server';
  function loadPick() {
    try {
      return localStorage.getItem(PICK_KEY) || 'auto';
    } catch {
      return 'auto';
    }
  }
  let pick = loadPick();
  function savePick(v) {
    pick = v;
    try {
      localStorage.setItem(PICK_KEY, v);
    } catch {
      /* storage blocked: the choice lasts for this session */
    }
  }

  function servers() {
    return (list && list.servers) || [];
  }

  // The server Play joins: the one the player picked, else the best pick from the main process.
  function target() {
    const all = servers();
    if (pick !== 'auto') {
      const s = all.find((x) => x.id === pick);
      if (s) return { server: s, auto: false };
    }
    const b = best && all.find((x) => x.id === best.id);
    if (b) return { server: b, auto: true };
    return { server: all.length === 1 ? all[0] : null, auto: true };
  }

  function fillOf(s) {
    const st = statuses[s.id];
    const max = (st && st.maxPlayers) || s.maxPlayers;
    const players = st && Number.isInteger(st.players) ? st.players : null;
    return { st, max, players, full: players != null && max && players >= max };
  }

  // ---------------------------------------------------------------- Play, status, server picker

  function renderStatus() {
    const t = target();
    const s = t.server;
    const known = Object.keys(statuses).length > 0;
    const line = $('status-line');
    let tone = '';
    let state = 'Checking the realm';
    let players = '';
    if (!list) state = listError ? 'Server list unavailable' : 'Checking the realm';
    else if (!servers().length) state = 'No servers listed';
    else if (!s) {
      tone = known ? 'bad' : '';
      state = known ? 'No server is answering' : 'Checking the servers';
    } else {
      const f = fillOf(s);
      if (!f.st) state = 'Checking ' + s.name;
      else if (f.st.online === false) {
        tone = 'bad';
        state = 'Offline';
      } else if (f.full) {
        tone = 'warn';
        state = 'Full';
      } else if (f.st.online === true) {
        tone = 'ok';
        state = 'Online';
      } else state = 'Status unknown';
      if (f.st && f.players != null) players = `${f.st.approx ? '~' : ''}${f.players} / ${f.max} players`;
      else if (f.st && f.st.online !== false) players = `up to ${f.max} players`;
      if (f.st && Number.isFinite(f.st.pingMs)) players += (players ? ' · ' : '') + Math.round(f.st.pingMs) + ' ms';
    }
    line.className = 'status-line ' + tone;
    $('sl-state').textContent = state;
    $('sl-players').textContent = players;
    $('sl-players').hidden = !players;
    line.querySelector('.sl-sep').hidden = !players;
    // Who holds the Old Throne (from a server's public Chronicle).
    const withKing = (s && statuses[s.id] && statuses[s.id].king ? statuses[s.id] : null) || Object.values(statuses).find((st) => st && st.king);
    $('sl-king').hidden = !withKing;
    if (withKing) $('sl-king-text').textContent = withKing.king + (withKing.house ? ' of House ' + withKing.house : '') + ' holds the throne';
    renderPlay();
  }

  function renderPlay() {
    const btn = $('play');
    const t = target();
    const s = t.server;
    const missing = install && install.installed === false;
    btn.classList.toggle('install', !!missing);
    btn.disabled = false;
    if (missing) {
      $('play-label').textContent = 'Install';
      $('play-sub').textContent = install.steam === false ? 'Get Steam first, then Reign of Kings' : 'Reign of Kings through Steam';
      return;
    }
    $('play-label').textContent = 'Play';
    const st = s ? statuses[s.id] : null;
    if (!list || !servers().length) {
      btn.disabled = !!list;
      $('play-sub').textContent = list ? 'The signed list has no servers yet' : 'Reading the server list';
    } else if (!s) {
      const known = Object.keys(statuses).length > 0;
      btn.disabled = known;
      $('play-sub').textContent = known ? 'No server is answering right now' : 'Checking the servers';
    } else if (st && st.online === false) {
      btn.disabled = true;
      $('play-sub').textContent = servers().length > 1 ? `${s.name} is not answering. Pick another server` : `${s.name} is not answering right now`;
    } else {
      const full = fillOf(s).full;
      $('play-sub').textContent = `Join ${s.name}${full ? ' · you queue for a place' : ''}`;
    }
  }

  function renderPicker() {
    const all = servers();
    const sel = $('server-select');
    const single = all.length <= 1;
    sel.hidden = single;
    $('server-chev').hidden = single;
    $('server-name').hidden = !single;
    $('copy-address').disabled = !target().server;
    if (single) {
      $('server-name').textContent = all[0] ? all[0].name : list ? 'None listed' : 'Loading';
      return;
    }
    const focused = document.activeElement === sel;
    const opts = [];
    const b = best && all.find((x) => x.id === best.id);
    opts.push(['auto', b ? `Best now: ${b.name}` : 'Best server (automatic)']);
    for (const s of all) {
      const f = fillOf(s);
      const bits = [s.name];
      if (s.region) bits.push(s.region);
      if (f.st && f.st.online === false) bits.push('offline');
      else if (f.players != null) bits.push(`${f.st.approx ? '~' : ''}${f.players}/${f.max}`);
      opts.push([s.id, bits.join(' · ')]);
    }
    if (pick !== 'auto' && !all.some((s) => s.id === pick)) savePick('auto');
    // Rebuild only when the text changed, so an open drop-down is not disturbed.
    const sig = opts.map((o) => o.join('=')).join('|');
    if (sel.dataset.sig !== sig && !focused) {
      sel.replaceChildren(...opts.map(([v, label]) => Object.assign(document.createElement('option'), { value: v, textContent: label })));
      sel.dataset.sig = sig;
    }
    sel.value = pick;
  }

  function renderAll() {
    renderPicker();
    renderStatus();
    renderTrust();
  }

  function renderTrust() {
    const box = $('trust');
    if (!list) return;
    const map = { online: ['ok', 'Signed server list'], cache: ['warn', 'Saved server list (offline)'], bundled: ['warn', 'Server list from this app'], builtin: ['bad', 'No signed server list'] };
    const [kind, label] = map[list.source] || ['', list.source];
    box.className = 'fb-trust ' + kind;
    $('trust-text').textContent = label + (list.seq ? ` · v${list.seq}` : '');
    const tip = [];
    if (list.keyId) tip.push(`Checked against the Realm key ${list.keyId} built into this app.`);
    if (list.expires) tip.push(`Valid until ${day(list.expires)}.`);
    if (list.reason) tip.push(list.reason);
    box.title = tip.join(' ');
  }

  async function loadAll(refresh) {
    try {
      list = await api.servers(refresh === true);
      listError = null;
    } catch (e) {
      listError = (e && e.message) || String(e);
    }
    renderAll();
    try {
      const r = await api.status(refresh === true);
      statuses = r.statuses || {};
      best = r.best || null;
    } catch {
      statuses = {};
    }
    renderAll();
  }

  async function loadInstall() {
    try {
      install = await api.installState();
    } catch {
      install = null;
    }
    renderPlay();
  }

  $('server-select').addEventListener('change', (ev) => {
    savePick(ev.target.value);
    renderAll();
  });

  $('copy-address').addEventListener('click', async () => {
    const s = target().server;
    if (!s) return;
    try {
      toast('Copied ' + (await api.copyAddress(s.id)) + '. In Direct Connect, paste it in the address box and type the port in the port box.', 'ok');
    } catch (e) {
      fail(e);
    }
  });

  $('play').addEventListener('click', () => {
    if (install && install.installed === false) return installGame(install.steam === false);
    const t = target();
    if (t.auto) doJoin(null, true);
    else doJoin(t.server.id);
  });

  function installGame(steamMissing) {
    (steamMissing ? api.openLink('steam') : api.installGame()).then(
      () => toast(steamMissing ? 'The Steam website is open. Install Steam, then Reign of Kings.' : 'Steam is opening the Reign of Kings install. Play appears here when it is done.', 'ok'),
      fail
    );
  }

  // ---------------------------------------------------------------- join progress

  let jfTimers = [];
  let jfServer = null;
  let jfReturnFocus = null;
  let jfUntrap = null;

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
    const classic = config && config.joinMethod === 'classic';
    $('jf-title').textContent = title;
    $('jf-join-label').textContent = 'Joining';
    $('jf-addr').textContent = '-';
    $('jf-port').textContent = '-';
    $('jf-manual').hidden = true;
    jfSet('check', 'now', 'Is it up, and is there room?');
    jfSet('steam', '', 'Steam starts your own copy of Reign of Kings.');
    jfSet('game', '', classic ? 'Steam starts the game at its main menu.' : 'Steam may ask once to allow the launch options: choose OK.');
    jfSet('join', '', classic ? 'Paste the address and type the port in Direct Connect.' : 'The game connects by itself.');
    jfStatus('Checking the server...');
    $('joinflow').hidden = false;
    if (jfUntrap) jfUntrap();
    jfUntrap = trapFocus($('joinflow'), jfClose);
    $('jf-close').focus();
  }
  function jfClose() {
    for (const t of jfTimers) clearTimeout(t);
    jfTimers = [];
    $('joinflow').hidden = true;
    if (jfUntrap) jfUntrap();
    jfUntrap = null;
    if (jfReturnFocus && document.contains(jfReturnFocus)) jfReturnFocus.focus();
  }
  function jfFail(step, text) {
    jfSet(step, 'bad');
    jfStatus(text);
  }

  async function doJoin(id, viaBest) {
    const chosen = viaBest ? null : servers().find((s) => s.id === id);
    jfOpen(chosen ? chosen.name : 'The best server');
    try {
      const r = await api.status(true);
      statuses = r.statuses || {};
      best = r.best || null;
      renderAll();
    } catch {
      /* status is advisory; Steam can still try */
    }
    const pickId = viaBest ? best && best.id : id;
    const s = servers().find((x) => x.id === pickId);
    const st = s ? statuses[s.id] : null;
    if (viaBest && !s) return jfFail('check', "No server is reachable right now. Try again in a minute, or press Can't join?.");
    if (st && st.online === false) return jfFail('check', `${s.name} is not answering right now. Pick another server, or press Can't join? to see why.`);
    if (s) $('jf-title').textContent = s.name;
    const full = st && Number.isInteger(st.players) && st.maxPlayers && st.players >= st.maxPlayers;
    jfSet('check', 'done', !st || st.online == null ? 'Could not confirm it is up; trying anyway.' : full ? `Full (${st.players}/${st.maxPlayers}): the game queues you.` : `Up${Number.isInteger(st.players) ? `, ${st.approx ? '~' : ''}${st.players}/${st.maxPlayers} players` : ''}${Number.isFinite(st.pingMs) ? `, ${Math.round(st.pingMs)} ms` : ''}.`);
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
      install = { installed: false, steam: r.steam };
      renderPlay();
      const ok = await confirmBox({ title: r.steam === false ? 'Steam is needed' : 'Install Reign of Kings first', text: r.steam === false ? 'Realm plays through your own Steam copy of Reign of Kings. Install Steam, then the game, then press Play again.' : 'Realm plays through your own Steam copy of Reign of Kings, and it is not installed yet. Steam can install it now.', ok: r.steam === false ? 'Get Steam' : 'Install in Steam' });
      if (ok) installGame(r.steam === false);
      return;
    }
    $('jf-title').textContent = r.name;
    $('jf-join-label').textContent = 'Joining ' + r.name;
    jfServer = { address: String(r.address), port: String(r.port) };
    $('jf-addr').textContent = jfServer.address;
    $('jf-port').textContent = jfServer.port;
    $('jf-manual').hidden = false;
    jfSet('steam', 'done', 'Steam has the request. The address is copied.');
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

  // ---------------------------------------------------------------- news

  const TYPE = {
    announcement: { label: 'Announcement', art: 'decree' },
    season: { label: 'Season', art: 'laurel' },
    chronicle: { label: 'Chronicle', art: 'treaty' }
  };
  const TAG = {
    sculpture: { label: 'New sculpture', art: 'trophy' },
    sign: { label: 'New sign art', art: 'claim' },
    event: { label: 'Event', art: 'beacon' },
    atmosphere: { label: 'Atmosphere', art: 'dawn' },
    rules: { label: 'Rules', art: 'scales' },
    other: { label: 'New in the realm', art: 'sprout' }
  };

  function serverName(id) {
    const s = id && servers().find((x) => x.id === id);
    return s ? s.name : null;
  }

  function newsItem(it) {
    const t = TYPE[it.type] || TYPE.announcement;
    const li = el('li', 'pn-item t-' + it.type + (it.pinned ? ' pinned' : ''));
    const mark = el('span', 'ni-mark');
    mark.appendChild(artIcon(t.art, 'ni-art'));
    const body = el('div', 'ni-body');
    const kicker = el('p', 'ni-kicker');
    kicker.append(el('span', 'ni-type', t.label));
    if (it.pinned) kicker.append(el('span', 'ni-pin', 'Pinned'));
    const where = serverName(it.server);
    kicker.append(el('time', 'ni-date', [day(it.date), servers().length > 1 ? where : null].filter(Boolean).join(' · ')));
    kicker.lastChild.setAttribute('datetime', it.date);
    body.append(kicker, el('h3', 'ni-title', it.title));
    if (it.body) {
      const p = el('p', 'ni-text', it.body);
      body.appendChild(p);
    }
    if (it.link) {
      const a = el('button', 'link-btn ni-link');
      a.type = 'button';
      a.dataset.news = it.id;
      a.append(el('span', null, 'Read more'), icon('i-link', 'ico'));
      body.appendChild(a);
    }
    li.append(mark, body);
    return li;
  }

  function renderNews() {
    const ol = $('news-list');
    ol.replaceChildren();
    ol.setAttribute('aria-busy', String(!newsData));
    if (!newsData) {
      if (ART) ol.append(...ART.skeletonRows(4));
      return;
    }
    const items = newsData.items.filter((it) => filter === 'all' || it.type === filter);
    if (!items.length && ART) {
      const none = !newsData.items.length;
      ol.appendChild(
        ART.emptyState({
          tag: 'li',
          art: 'decree',
          title: none ? 'No news yet' : 'Nothing here yet',
          body: none ? (newsData.source === 'none' && newsData.reason && !/No news yet/.test(newsData.reason) ? 'The realm\'s heralds post announcements, season news and Chronicle highlights here. ' + newsData.reason : 'The realm\'s heralds post announcements, season news and Chronicle highlights here.') : 'Other news is under All.'
        })
      );
    }
    for (const it of items) ol.appendChild(newsItem(it));
    renderNewsMeta();
    renderRealmNew();
  }

  function renderNewsMeta() {
    if (!newsData) return;
    const meta = $('news-meta');
    const src = { online: 'Signed', cache: 'Saved copy', bundled: 'From this app', none: '' }[newsData.source] || '';
    meta.textContent = src && newsData.issued ? `${src} · ${ago(newsData.issued)}` : src;
    meta.title = newsData.reason || '';
  }

  function renderRealmNew() {
    const items = (newsData && newsData.realm) || [];
    $('realm-new').hidden = !items.length;
    const ul = $('rn-list');
    ul.replaceChildren();
    for (const it of items.slice(0, 3)) {
      const t = TAG[it.tag] || TAG.other;
      const li = el('li', 'rn-card tag-' + it.tag);
      const mark = el('span', 'rn-mark');
      mark.appendChild(artIcon(t.art, 'rn-art'));
      const body = el('div', 'rn-body');
      const soon = it.at && Date.parse(it.at) > Date.now();
      body.append(el('p', 'rn-tag', soon ? `${t.label} · ${until(it.at)}` : t.label), el('p', 'rn-title', it.title));
      const sub = it.at ? when(it.at) : it.body || day(it.date);
      if (sub) body.appendChild(el('p', 'rn-sub', sub));
      li.title = [it.title, it.body].filter(Boolean).join('\n\n');
      li.append(mark, body);
      ul.appendChild(li);
    }
  }

  async function loadNews(refresh) {
    try {
      newsData = await api.news(refresh === true);
    } catch {
      newsData = newsData || { source: 'none', items: [], realm: [], reason: null };
    }
    renderNews();
  }

  function setFilter(f, focus) {
    filter = f;
    for (const b of $$('[role="tab"]', $('news-tabs'))) {
      const on = b.dataset.filter === f;
      b.setAttribute('aria-selected', String(on));
      b.tabIndex = on ? 0 : -1;
      if (on) {
        $('news-list').setAttribute('aria-labelledby', b.id);
        if (focus) b.focus();
      }
    }
    renderNews();
    $('news-list').scrollTop = 0;
  }

  $('news-tabs').addEventListener('click', (ev) => {
    const b = ev.target.closest('[role="tab"]');
    if (b) setFilter(b.dataset.filter);
  });
  $('news-tabs').addEventListener('keydown', (ev) => {
    const tabs = $$('[role="tab"]', $('news-tabs'));
    const i = tabs.indexOf(document.activeElement);
    if (i < 0) return;
    let n = null;
    if (ev.key === 'ArrowRight') n = (i + 1) % tabs.length;
    else if (ev.key === 'ArrowLeft') n = (i + tabs.length - 1) % tabs.length;
    else if (ev.key === 'Home') n = 0;
    else if (ev.key === 'End') n = tabs.length - 1;
    if (n == null) return;
    ev.preventDefault();
    setFilter(tabs[n].dataset.filter, true);
  });
  $('news-list').addEventListener('click', (ev) => {
    const b = ev.target.closest('[data-news]');
    if (b) api.openNewsLink(b.dataset.news).catch(fail);
  });

  // ---------------------------------------------------------------- updates

  let laterFor = null;

  function renderUpdate() {
    const u = upd;
    const bar = $('update-bar');
    const offered = u && u.version && ['available', 'downloading', 'ready', 'installing', 'error'].includes(u.status);
    const show = offered && (u.hotfix || laterFor !== u.version || u.status !== 'available');
    bar.hidden = !show;
    document.body.classList.toggle('has-update', !!show);
    if (!show) return;
    bar.className = 'update-bar s-' + u.status + (u.hotfix ? ' urgent' : '');
    const v = 'Realm ' + u.version;
    const title = $('update-title');
    const sub = $('update-sub');
    const go = $('update-go');
    const goLabel = $('update-go-label');
    const notesTitle = u.notes && u.notes.title ? u.notes.title + '. ' : '';
    $('update-progress').hidden = u.status !== 'downloading';
    $('update-cancel').hidden = u.status !== 'downloading';
    $('update-later').hidden = u.hotfix || u.status !== 'available';
    $('update-notes').hidden = !(u.notes && u.notes.items && u.notes.items.length);
    go.hidden = u.status === 'downloading' || u.status === 'installing';
    go.disabled = false;
    $('update-ico').setAttribute('href', u.status === 'ready' ? '#i-check' : u.status === 'error' ? '#i-alert' : '#i-download');
    $('update-go-ico').setAttribute('href', u.status === 'ready' ? '#i-restart' : u.status === 'error' ? '#i-restart' : '#i-download');
    if (u.status === 'available') {
      title.textContent = u.hotfix ? `Urgent update: ${v}` : `Update available: ${v}`;
      sub.textContent = u.hotfix ? 'Fixes an important problem. Update before you play.' : `${notesTitle}${mb(u.size)}`;
      goLabel.textContent = 'Update';
    } else if (u.status === 'downloading') {
      const pct = u.size ? Math.min(100, Math.floor((u.received / u.size) * 100)) : 0;
      title.textContent = (u.hotfix ? 'Urgent update: ' : 'Downloading ') + v;
      sub.textContent = `${u.hotfix ? 'Fixes an important problem. ' : ''}${mb(u.received) || '0 MB'} of ${mb(u.size)}, checked against the realm's signature when done.`;
      $('update-fill').style.width = pct + '%';
      $('update-meter').setAttribute('aria-valuenow', String(pct));
      $('update-pct').textContent = pct + '%';
    } else if (u.status === 'ready') {
      title.textContent = `${v} is ready`;
      sub.textContent = u.portable ? 'Downloaded and verified. This portable copy cannot update itself: run the installer it shows you.' : 'Downloaded and verified against the realm\'s signature. Realm restarts to finish.';
      goLabel.textContent = u.portable ? 'Show installer' : 'Restart and update';
    } else if (u.status === 'installing') {
      title.textContent = `Installing ${v}`;
      sub.textContent = 'The installer is starting. Realm closes now and opens again when it is done.';
    } else {
      title.textContent = 'The update did not finish';
      sub.textContent = u.reason || 'Something went wrong.';
      goLabel.textContent = 'Try again';
    }
  }

  async function updateAction() {
    const u = upd;
    if (!u) return;
    try {
      if (u.status === 'ready') {
        const r = await api.installUpdate();
        if (r && r.shown) toast('The verified installer is shown in its folder. Close Realm, then run it.', 'ok');
      } else await api.downloadUpdate();
    } catch (e) {
      fail(e);
    }
  }

  $('update-go').addEventListener('click', updateAction);
  $('update-cancel').addEventListener('click', () => api.cancelUpdate().catch(fail));
  $('update-later').addEventListener('click', () => {
    laterFor = upd && upd.version;
    renderUpdate();
    toast('Realm will remind you next time it starts.', 'ok');
  });
  $('update-notes').addEventListener('click', () => upd && upd.notes && openNotes({ kicker: `Coming in Realm ${upd.version}`, title: upd.notes.title || `Realm ${upd.version}`, items: upd.notes.items, foot: upd.released ? 'Released ' + day(upd.released) + '.' : '' }));

  // ---------------------------------------------------------------- What's new (patch notes)

  let notesUntrap = null;
  let notesBack = null;
  function openNotes({ kicker, title, items, foot }) {
    notesBack = document.activeElement;
    $('notes-kicker').textContent = kicker;
    $('notes-title').textContent = title;
    const ul = $('notes-list');
    ul.replaceChildren();
    for (const s of items || []) {
      const li = el('li');
      li.append(icon('i-spark', 'ico'), el('span', null, s));
      ul.appendChild(li);
    }
    if (!items || !items.length) ul.appendChild(el('li', 'notes-empty', 'Fixes and polish.'));
    $('notes-foot').textContent = foot || '';
    $('notes-foot').hidden = !foot;
    $('notes').hidden = false;
    if (notesUntrap) notesUntrap();
    notesUntrap = trapFocus($('notes'), closeNotes);
    $('notes-close').focus();
  }
  function closeNotes() {
    $('notes').hidden = true;
    if (notesUntrap) notesUntrap();
    notesUntrap = null;
    if (notesBack && document.contains(notesBack)) notesBack.focus();
    checkLink(); // a realm:// link that arrived while the notes were open
  }
  $('notes-close').addEventListener('click', closeNotes);
  $('notes').addEventListener('click', (ev) => {
    if (ev.target === $('notes')) closeNotes();
  });

  async function showWhatsNew(auto) {
    let w;
    try {
      w = await api.whatsNew();
    } catch {
      return;
    }
    if (!w || (auto && !w.justUpdated)) return;
    openNotes({ kicker: w.justUpdated ? `Updated to Realm ${w.version}` : `Realm ${w.version}`, title: w.title, items: w.items, foot: w.justUpdated && w.from ? `You were on ${w.from}.` : '' });
  }
  $('whats-new-open').addEventListener('click', () => showWhatsNew(false));

  // ---------------------------------------------------------------- deep links

  async function confirmLink(id) {
    const s = servers().find((x) => x.id === id);
    if (!s) return;
    const st = statuses[id];
    const extra = st && Number.isInteger(st.players) ? ` ${st.players} of ${st.maxPlayers || s.maxPlayers} players are on it now.` : '';
    const ok = await confirmBox({ title: `Join ${s.name}?`, text: `A link asked Realm to join ${s.name} (${s.address}:${s.port}).${extra} This server is in the signed Realm list.`, ok: 'Join', kicker: 'Realm link' });
    if (ok) doJoin(id);
  }

  async function checkLink() {
    if (!$('notes').hidden || !$('modal').hidden) return;
    try {
      const id = await api.takeLink();
      if (id) confirmLink(id);
    } catch {
      /* ignore */
    }
  }

  // ---------------------------------------------------------------- pushes, chrome, start

  api.onPush((msg) => {
    if (msg.type === 'status') {
      statuses = msg.statuses || {};
      best = msg.best || null;
      renderAll();
    } else if (msg.type === 'servers') loadAll(false);
    else if (msg.type === 'news') loadNews(false);
    else if (msg.type === 'update') {
      upd = msg.update;
      renderUpdate();
    } else if (msg.type === 'deeplink') {
      if (msg.error) toast(msg.error, 'bad');
      else checkLink();
    }
  });

  document.addEventListener('click', (ev) => {
    const linkBtn = ev.target.closest('[data-link]');
    if (linkBtn) api.openLink(linkBtn.dataset.link).catch(fail);
    const winBtn = ev.target.closest('[data-win]');
    if (winBtn) api.windowAction(winBtn.dataset.win).catch(() => {});
  });

  // The skip link focuses Play itself (a fragment link alone only scrolls).
  document.querySelector('.skip-link').addEventListener('click', (ev) => {
    ev.preventDefault();
    $('play').focus();
  });

  // Back from installing the game in Steam: look again.
  window.addEventListener('focus', () => {
    if (install && install.installed === false) loadInstall();
  });

  async function start() {
    try {
      config = await api.getConfig();
      $('realm-name').textContent = config.realmName;
      $('tagline').textContent = config.tagline;
      $('tagline').hidden = !config.tagline;
      document.title = config.realmName === 'Realm' ? 'Realm' : `Realm · ${config.realmName}`;
      const links = $('links');
      const help = links.querySelector('[data-doctor-open]');
      for (const l of config.links) {
        const b = el('button', 'link-btn', l.label);
        b.type = 'button';
        b.dataset.link = l.id;
        links.insertBefore(b, help);
      }
    } catch (e) {
      fail(e);
    }
    try {
      $('app-version').textContent = 'Realm ' + (await api.appInfo()).version;
    } catch {
      /* ignore */
    }
    renderAll();
    renderNews();
    try {
      upd = await api.update();
      renderUpdate();
    } catch {
      /* ignore */
    }
    await Promise.all([loadAll(false), loadInstall(), loadNews(false)]);
    await showWhatsNew(true);
    checkLink();
    setInterval(() => {
      // Keeps "2 h ago" and "in 3 days" fresh without rebuilding the list under the reader.
      if (!document.hidden && newsData) {
        renderNewsMeta();
        renderRealmNew();
      }
    }, 60000);
  }

  start();
})();
