'use strict';

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
    setTimeout(() => t.remove(), kind === 'bad' ? 9000 : 5000);
    while ($('toasts').children.length > 4) $('toasts').firstChild.remove();
  }

  function fail(e) {
    if (e && e.cancelled) toast(e.message);
    else toast((e && e.message) || String(e), 'bad');
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

  function bytes(n) {
    if (n >= 1024 ** 3) return (n / 1024 ** 3).toFixed(1) + ' GB';
    if (n >= 1024 ** 2) return (n / 1024 ** 2).toFixed(1) + ' MB';
    return Math.max(0, Math.round(n / 1024)) + ' KB';
  }

  function when(iso) {
    const t = Date.parse(iso);
    if (!Number.isFinite(t)) return '';
    return new Date(t).toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit', day: 'numeric', month: 'short' });
  }

  // In-app confirm dialog. Resolves true/false, or the value of a <select> when options are given.
  function confirmBox({ title, text, ok = 'OK', danger = false, options = null }) {
    return new Promise((resolve) => {
      $('modal-title').textContent = title;
      $('modal-text').textContent = text;
      const extra = $('modal-extra');
      extra.replaceChildren();
      let select = null;
      if (options) {
        select = el('select');
        for (const o of options) {
          const opt = el('option', null, o.label);
          opt.value = o.value;
          select.appendChild(opt);
        }
        extra.appendChild(select);
      }
      const okBtn = $('modal-ok');
      okBtn.textContent = ok;
      okBtn.className = 'btn ' + (danger ? 'danger' : 'primary');
      $('modal').hidden = false;
      okBtn.focus();
      const done = (v) => {
        $('modal').hidden = true;
        okBtn.onclick = null;
        $('modal-cancel').onclick = null;
        resolve(v);
      };
      okBtn.onclick = () => done(select ? select.value : true);
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

  // ---------------------------------------------------------------- shared state

  let config = null;
  let srv = { state: 'stopped' };
  let busyLabel = null;

  function updateTitleStatus() {
    const box = $('title-status');
    let text = 'Server stopped';
    box.className = 'title-status';
    if (busyLabel) {
      text = busyLabel + '...';
      box.classList.add('busy');
    } else if (srv.state === 'running') {
      text = srv.ready ? 'Server running' : 'Server loading';
      box.classList.add(srv.ready ? 'running' : 'busy');
    } else if (srv.state === 'starting' || srv.state === 'stopping') {
      text = srv.state === 'starting' ? 'Server starting' : 'Server stopping';
      box.classList.add('busy');
    }
    $('title-status-text').textContent = text;
  }

  // ================================================================ HOME

  const EVENT_STYLE = {
    coronation: ['k-crown', 'i-crown'],
    abdication: ['k-crown', 'i-crown'],
    claim_declared: ['k-crown', 'i-scroll'],
    decree: ['k-crown', 'i-scroll'],
    rebellion_started: ['k-war', 'i-flame'],
    rebellion_ended: ['k-war', 'i-sword'],
    oath_broken: ['k-war', 'i-sword'],
    treaty_broken: ['k-war', 'i-sword'],
    house_founded: ['k-house', 'i-shield'],
    oath_sworn: ['k-house', 'i-shield'],
    treaty_signed: ['k-house', 'i-scroll'],
    ransom_set: ['k-bond', 'i-chain'],
    ransom_paid: ['k-bond', 'i-chain'],
    released: ['k-bond', 'i-chain'],
    contract_posted: ['k-bond', 'i-scroll'],
    contract_fulfilled: ['k-bond', 'i-chest'],
    contract_ended: ['k-bond', 'i-scroll']
  };
  const MAX_EVENTS = 14;
  const STALE_MS = 2 * 60 * 1000;
  let events = [];
  let lastEventId = 0;
  let chronicleState = null;

  function renderConfig() {
    $('realm-name').textContent = config.realmName;
    $('tagline').textContent = config.tagline;
    document.title = config.realmName;
    $('server-name').textContent = config.server.name;
    $('server-address').textContent = config.server.address + ':' + config.server.port;
    const links = $('links');
    links.replaceChildren();
    for (const link of config.links) {
      if (link.id === 'chronicle') continue;
      const b = el('button', 'btn ghost');
      b.append(icon('i-link', 'ico'), el('span', null, link.label));
      b.dataset.link = link.id;
      links.appendChild(b);
    }
  }

  async function renderNews() {
    const list = $('news');
    list.replaceChildren();
    let news = [];
    try {
      news = await api.getNews();
    } catch {
      news = [];
    }
    if (!news.length) {
      list.appendChild(el('p', 'empty', 'No news from the heralds yet.'));
      return;
    }
    for (const item of news.slice(0, 3)) {
      const card = el('article', 'news-item');
      const time = el('time', null, item.date || '');
      if (item.date) time.dateTime = item.date;
      card.append(time, el('h3', null, item.title || ''), el('p', null, item.body || ''));
      list.appendChild(card);
    }
  }

  function renderHomeStatus() {
    const s = chronicleState;
    const pill = $('status-pill');
    let kind = 'bad';
    let label = 'Offline';
    let detail = '';
    const fresh = s && Number.isFinite(Date.now() - Date.parse(s.updated)) && Date.now() - Date.parse(s.updated) < STALE_MS;
    if (srv.state === 'running' && srv.ready) {
      kind = 'ok';
      label = 'Running';
      detail = 'Your test server is up. Press Play, then direct connect to the address on the left.';
    } else if (srv.state === 'running' || srv.state === 'starting') {
      kind = 'warn';
      label = 'Loading';
      detail = 'The server is starting. It is ready when the console shows "Initialize engine version".';
    } else if (srv.state === 'stopping') {
      kind = 'warn';
      label = 'Stopping';
      detail = 'Saving the world and closing.';
    } else if (fresh) {
      kind = 'ok';
      label = 'Online';
      detail = 'Reported by the Chronicle ' + ago(s.updated) + '.';
    } else {
      detail = s && s.updated ? 'Last Chronicle report ' + ago(s.updated) + '. Start the server on the Server screen.' : 'The server is not running. Start it on the Server screen.';
    }
    pill.className = 'pill ' + kind;
    pill.textContent = label;
    $('status-detail').textContent = detail;
    $('stat-server').textContent = srv.state === 'running' ? (srv.ready ? 'Up' : 'Loading') : srv.state === 'stopped' ? 'Down' : srv.state;
    const online = s && fresh && Number.isFinite(s.online) ? s.online : srv.players ? srv.players.length : 0;
    const max = (s && s.maxPlayers) || (config && config.server.maxPlayers) || '-';
    $('players').textContent = online + ' / ' + max;
    $('house-count').textContent = s && Array.isArray(s.houses) ? String(s.houses.length) : '-';
    if (s && s.king) {
      $('king').textContent = s.king;
      const parts = [];
      if (s.house) parts.push('of House ' + s.house);
      if (s.since) parts.push('crowned ' + ago(s.since));
      $('king-detail').textContent = parts.join(', ');
    } else {
      $('king').textContent = 'The throne is empty';
      $('king-detail').textContent = 'Any house may press its claim.';
    }
  }

  function renderEvents() {
    const list = $('events');
    list.replaceChildren();
    if (!events.length) {
      list.appendChild(el('li', 'empty', 'The Chronicle is silent. Events appear here once the plugins record them.'));
      return;
    }
    for (const e of events) {
      const [kind, glyph] = EVENT_STYLE[e.type] || ['k-house', 'i-scroll'];
      const li = el('li', 'event ' + kind);
      const body = el('div');
      body.append(el('p', 'event-title', e.title));
      body.append(el('p', 'event-meta', [ago(e.ts), e.detail].filter(Boolean).join(' · ')));
      li.append(icon(glyph, 'event-ico'), body);
      list.appendChild(li);
    }
  }

  async function pollChronicle() {
    try {
      chronicleState = await api.getState();
    } catch {
      chronicleState = null;
    }
    try {
      const fresh = (await api.getEvents(lastEventId)).filter((e) => e && Number.isInteger(e.id) && e.id > lastEventId);
      if (fresh.length) {
        lastEventId = Math.max(lastEventId, ...fresh.map((e) => e.id));
        events = fresh.concat(events).sort((a, b) => b.id - a.id).slice(0, MAX_EVENTS);
      }
    } catch {
      /* chronicle down */
    }
    renderEvents();
    renderHomeStatus();
  }

  views.home = {
    show() {
      pollChronicle();
    }
  };

  $('play').addEventListener('click', async () => {
    try {
      await api.play();
      toast('Opening Steam. Then direct connect to ' + config.server.address + ':' + config.server.port + '.', 'ok');
    } catch {
      toast('Could not open Steam. Is it installed?', 'bad');
    }
  });
  $('copy-address').addEventListener('click', async () => {
    try {
      const text = await api.copyAddress();
      toast('Copied ' + text + '. Paste it in the direct connect field.', 'ok');
    } catch (e) {
      fail(e);
    }
  });

  // ================================================================ SERVER

  let lastLineId = 0;
  const MAX_DOM_LINES = 2000;

  function lineNode(l) {
    const cls = l.src === 'err' ? 'err' : l.src === 'sys' ? 'sys' : l.src === 'log' ? 'log' : '';
    const n = el('span', 'ln ' + cls + (/^\s*Initialize engine version:/.test(l.text) ? ' ready' : ''));
    const d = new Date(l.t);
    const p = (x) => String(x).padStart(2, '0');
    n.append(el('span', 't', `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`), document.createTextNode(l.text));
    return n;
  }

  function appendLines(lines) {
    const box = $('console');
    const fresh = lines.filter((l) => l.id > lastLineId);
    if (!fresh.length) return;
    const ph = box.querySelector('.placeholder');
    if (ph) ph.remove();
    const frag = document.createDocumentFragment();
    for (const l of fresh) frag.appendChild(lineNode(l));
    lastLineId = fresh[fresh.length - 1].id;
    box.appendChild(frag);
    while (box.childElementCount > MAX_DOM_LINES) box.firstElementChild.remove();
    if ($('console-follow').checked) box.scrollTop = box.scrollHeight;
  }

  function consolePlaceholder() {
    const box = $('console');
    if (!box.childElementCount) box.appendChild(el('p', 'placeholder', 'The forge is cold. Press Start to light the server; its console appears here.'));
  }

  function renderServer(st) {
    if (st) srv = { ...srv, ...st };
    const s = srv;
    const pill = $('srv-pill');
    const map = { running: s.ready ? ['ok', 'Running'] : ['warn', 'Loading'], starting: ['warn', 'Starting'], stopping: ['warn', 'Stopping'], stopped: ['', 'Stopped'] };
    const [kind, label] = map[s.state] || ['', s.state];
    pill.className = 'pill big ' + kind;
    pill.textContent = label;
    const running = s.state !== 'stopped';
    $('srv-meta-1').textContent = running ? `${s.exe || 'Server'}.exe · pid ${s.pid || '-'}` : s.copied === false ? 'No test copy yet' : 'Not running';
    $('srv-meta-2').textContent = running
      ? `started ${ago(s.startedAt)} · ${(s.players || []).length} player(s)`
      : s.lastExit
        ? `last stop ${ago(s.lastExit.at)}${s.lastExit.code != null ? ' (exit ' + s.lastExit.code + ')' : ''}`
        : '';
    const noCopy = s.copied === false;
    const external = (s.external || []).length > 0;
    $('srv-start').disabled = running || noCopy || external || !!busyLabel;
    $('srv-stop').disabled = !running || s.state === 'stopping';
    $('srv-restart').disabled = !running || s.state === 'stopping' || !!busyLabel;
    $('srv-force').hidden = !(running && (s.stopOverdue || s.state === 'stopping'));
    $('console-cmd').disabled = !running;
    for (const id of ['act-plugins', 'act-backup', 'act-restore', 'act-undo-oxide']) $(id).disabled = noCopy || !!busyLabel;
    if ('oxideBackup' in s) $('act-undo-oxide').disabled = $('act-undo-oxide').disabled || !s.oxideBackup;
    if ('testRoot' in s) {
      $('fact-root').textContent = s.testRoot || 'Not set';
      $('fact-oxide').textContent = s.oxide ? `Installed (${s.oxide})` : 'Not installed';
      $('fact-oxide').className = s.oxide ? 'ok' : '';
      $('fact-cfg').textContent = s.cfgExists ? 'Present' : 'Not created yet (first start creates it)';
    }
    if (external) $('srv-meta-2').textContent = 'A server from this folder is running outside Realm (pid ' + s.external.map((p) => p.pid).join(', ') + ').';
    updateTitleStatus();
    if (current === 'home') renderHomeStatus();
  }

  async function refreshServer() {
    try {
      renderServer(await api.server.status());
    } catch (e) {
      fail(e);
    }
  }

  views.server = {
    async show() {
      await refreshServer();
      try {
        appendLines(await api.server.log(lastLineId));
      } catch {
        /* ignore */
      }
      consolePlaceholder();
    }
  };

  async function guarded(fn, okMsg) {
    try {
      const r = await fn();
      if (okMsg) toast(typeof okMsg === 'function' ? okMsg(r) : okMsg, 'ok');
      return r;
    } catch (e) {
      fail(e);
      return null;
    } finally {
      refreshServer();
    }
  }

  $('srv-start').addEventListener('click', () => guarded(() => api.server.start(), 'Server starting. Watch the console for "Initialize engine version".'));
  $('srv-stop').addEventListener('click', () => guarded(() => api.server.stop(), 'Sent "quit". The server saves and closes.'));
  $('srv-restart').addEventListener('click', async () => {
    toast('Restarting: sending "quit" first...');
    const r = await guarded(() => api.server.restart());
    if (r && r.needsForce) toast('The server did not close in time. Use Force stop, then Start.', 'bad');
  });
  $('srv-force').addEventListener('click', async () => {
    const ok = await confirmBox({ title: 'Force stop the server?', text: 'This ends the server process right away. Anything not yet saved is lost. Prefer waiting a little longer if it is still saving.', ok: 'Force stop', danger: true });
    if (ok) guarded(() => api.server.forceStop(), 'Server force stopped.');
  });
  $('console-form').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const input = $('console-cmd');
    const text = input.value.trim();
    if (!text) return;
    try {
      await api.server.command(text);
      input.value = '';
    } catch (e) {
      fail(e);
    }
  });
  $('console-copy').addEventListener('click', async () => {
    const text = $$('#console .ln').map((n) => n.textContent).join('\n');
    await api.copyText(text.slice(-19000));
    toast('Console copied.', 'ok');
  });

  function showOpProgress(on, text) {
    $('op-progress').hidden = !on;
    if (text) $('op-progress-text').textContent = text;
    if (!on) $('op-progress-bar').style.width = '0';
  }

  $('act-plugins').addEventListener('click', () =>
    guarded(
      () => api.server.deployPlugins(),
      (r) => (r.copied ? `Deployed ${r.copied} plugin file(s). Oxide reloads them while the server runs.` : 'All plugins are already up to date.')
    )
  );

  $('act-backup').addEventListener('click', async () => {
    let allowRunning = false;
    if (srv.state !== 'stopped') {
      const ok = await confirmBox({ title: 'Back up while running?', text: 'The server is running. A save written during the backup can be inconsistent. For a clean backup, stop the server first.', ok: 'Back up anyway' });
      if (!ok) return;
      allowRunning = true;
    }
    showOpProgress(true, 'Packing the world into a chest...');
    await guarded(() => api.server.backup(allowRunning), (r) => `Backup written: ${r.name} (${bytes(r.size)}).`);
    showOpProgress(false);
  });

  $('act-restore').addEventListener('click', async () => {
    let list;
    try {
      list = await api.server.listBackups();
    } catch (e) {
      return fail(e);
    }
    if (!list.list.length) return toast('No backups yet. Use Back up world first.', 'bad');
    const pick = await confirmBox({
      title: 'Restore a backup',
      text: 'The current Saves and oxide\\data are backed up and moved aside first, so nothing is deleted. The server must be stopped.',
      ok: 'Restore',
      options: list.list.map((b) => ({ value: b.name, label: `${b.name}  (${bytes(b.size)})` }))
    });
    if (!pick) return;
    showOpProgress(true, 'Restoring ' + pick + '...');
    await guarded(() => api.server.restore(pick), (r) => `Restored ${r.restored} files. The previous world was moved aside${r.safetyBackup ? ' and saved as ' + r.safetyBackup : ''}.`);
    showOpProgress(false);
  });

  $('act-undo-oxide').addEventListener('click', async () => {
    const ok = await confirmBox({
      title: 'Undo Oxide?',
      text: 'This puts back the original game files of the test copy from the backup made when Oxide was installed, and removes the files Oxide added. Plugin and data folders stay. The server must be stopped.',
      ok: 'Undo Oxide',
      danger: true
    });
    if (ok) guarded(() => api.server.undoOxide(), (r) => `Oxide removed: ${r.restored} original file(s) restored, ${r.removed} removed.`);
  });

  for (const b of $$('[data-open]')) {
    b.addEventListener('click', () => api.settings.openFolder(b.dataset.open).catch(fail));
  }

  // ================================================================ REALM

  const TINCTURES = ['#8e1b1b', '#1f3f73', '#2f5a2a', '#4b2463', '#7a5418', '#1c1c1c', '#6b2a14', '#284e57'];

  function hashName(s) {
    let h = 7;
    for (const ch of String(s)) h = (h * 31 + ch.codePointAt(0)) >>> 0;
    return h;
  }

  function houseShield(name, cls) {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 120 140');
    svg.setAttribute('class', cls || 'shield');
    const h = hashName(name);
    const color = TINCTURES[h % TINCTURES.length];
    const p = document.createElementNS(ns, 'path');
    p.setAttribute('d', 'M10 8h100v52c0 38-24 62-50 74C34 122 10 98 10 60z');
    p.setAttribute('fill', color);
    p.setAttribute('class', 'field-fill');
    svg.appendChild(p);
    const division = (h >> 3) % 4;
    if (division) {
      const d = document.createElementNS(ns, 'path');
      d.setAttribute('d', division === 1 ? 'M60 8h50v52c0 38-24 62-50 74z' : division === 2 ? 'M10 60h100c0 30-24 62-50 74C34 122 10 98 10 60z' : 'M10 8l100 100v-48C110 98 86 122 60 134z');
      d.setAttribute('fill', '#000');
      d.setAttribute('opacity', '.28');
      svg.appendChild(d);
    }
    const t = document.createElementNS(ns, 'text');
    t.setAttribute('x', '60');
    t.setAttribute('y', '86');
    t.setAttribute('text-anchor', 'middle');
    t.setAttribute('font-family', 'Cinzel, serif');
    t.setAttribute('font-weight', '700');
    t.setAttribute('font-size', '54');
    t.setAttribute('fill', '#f4dc98');
    t.textContent = String(name).trim().charAt(0).toUpperCase();
    svg.appendChild(t);
    return svg;
  }

  function houseCard(h, { top, royal }) {
    const c = el('div', 'house' + (top ? ' top' : '') + (royal ? ' royal' : ''));
    c.appendChild(houseShield(h.name));
    const info = el('div');
    info.append(el('div', 'name', 'House ' + h.name));
    if (h.sigil) info.append(el('div', 'sig', h.sigil));
    info.append(el('div', 'members', (h.members || 0) + (h.members === 1 ? ' sworn sword' : ' sworn swords')));
    c.appendChild(info);
    if (royal) c.appendChild(el('span', 'crown-tag', 'The Crown'));
    return c;
  }

  function renderRealm(data) {
    const s = data.state || {};
    $('realm-king').textContent = s.king || 'The throne is empty';
    $('realm-king-house').textContent = s.king ? (s.house ? 'of House ' + s.house : '') + (s.since ? ' · crowned ' + ago(s.since) : '') : 'Any house may press its claim.';
    $('realm-updated').textContent = data.stateError ? 'The Chronicle did not answer (' + data.stateError + ').' : s.updated ? 'Last report ' + ago(s.updated) + (s.stale ? ' (stale)' : '') : 'No report from the plugins yet.';

    const council = $('realm-council');
    council.replaceChildren();
    const seats = (data.crown && data.crown.council) || [];
    if (!seats.length) council.appendChild(el('li', 'empty', s.king ? 'The king rules without a council.' : 'No crown, no council.'));
    for (const c of seats) {
      const li = el('li');
      li.append(el('span', 'seat', c.seat), el('span', 'who', c.name));
      council.appendChild(li);
    }

    const claims = $('realm-claims');
    claims.replaceChildren();
    const cl = (data.crown && data.crown.claims) || [];
    const dec = (data.crown && data.crown.decrees) || [];
    for (const c of cl) {
      const li = el('li', 'war');
      li.append(el('span', 'what', `House ${c.house} ${c.status === 'active' ? 'is in rebellion' : 'claims the crown'}`), el('span', 'when', c.status === 'active' ? 'until ' + when(c.windowEnd) : 'window ' + when(c.windowStart)));
      claims.appendChild(li);
    }
    for (const d of dec) {
      const li = el('li');
      li.append(el('span', 'what', 'Decree: ' + d.id.replace(/_/g, ' ')), el('span', 'when', d.expiresAt ? 'until ' + when(d.expiresAt) : ''));
      claims.appendChild(li);
    }
    if (!cl.length && !dec.length) claims.appendChild(el('li', 'empty', 'No open claims and no decrees in force.'));

    const tree = $('realm-tree');
    tree.replaceChildren();
    const houses = Array.isArray(s.houses) ? s.houses : [];
    $('realm-house-count').textContent = houses.length ? houses.length + ' houses' : '';
    if (!houses.length) {
      tree.appendChild(el('p', 'empty', 'No houses have been founded yet. In game: /house found "Name" Sigil'));
      return;
    }
    const byName = new Map(houses.map((h) => [h.name, h]));
    const vassalsOf = new Map();
    const roots = [];
    for (const h of houses) {
      if (h.liege && byName.has(h.liege) && h.liege !== h.name) {
        if (!vassalsOf.has(h.liege)) vassalsOf.set(h.liege, []);
        vassalsOf.get(h.liege).push(h);
      } else roots.push(h);
    }
    const weight = (h) => (h.name === s.house ? 1e6 : 0) + (vassalsOf.get(h.name) || []).length * 1000 + (h.members || 0);
    roots.sort((a, b) => weight(b) - weight(a));
    const seen = new Set();
    function branch(h, depth) {
      seen.add(h.name);
      const col = el('div', depth ? '' : 'lord-col');
      col.appendChild(houseCard(h, { top: depth === 0, royal: h.name === s.house && !!s.king }));
      const vs = (vassalsOf.get(h.name) || []).filter((v) => !seen.has(v.name));
      if (vs.length && depth < 4) {
        const wrap = el('div', 'vassals');
        wrap.appendChild(el('span', 'oath', 'Sworn to ' + h.name));
        for (const v of vs) wrap.appendChild(branch(v, depth + 1));
        col.appendChild(wrap);
      }
      return col;
    }
    for (const r of roots) tree.appendChild(branch(r, 0));
    for (const h of houses) if (!seen.has(h.name)) tree.appendChild(branch(h, 0));
  }

  views.realm = {
    async show() {
      try {
        renderRealm(await api.getRealm());
      } catch (e) {
        fail(e);
      }
    }
  };

  // ================================================================ OVERLAY

  let overlayStatus = null;

  function frameSrc() {
    return (overlayStatus ? overlayStatus.overlayUrl : 'http://127.0.0.1:8787/overlay') + ($('ov-bg').checked ? '?bg=1' : '');
  }

  function renderOverlay(st) {
    if (st) overlayStatus = st;
    const s = overlayStatus;
    if (!s) return;
    const pill = $('ov-pill');
    const live = s.running || s.external;
    pill.className = 'pill big ' + (s.running ? 'ok' : s.external ? 'warn' : 'bad');
    pill.textContent = s.running ? 'Serving' : s.external ? 'Shared port' : 'Stopped';
    $('ov-url').textContent = s.overlayUrl;
    $('ov-data').textContent = s.dataDir || '-';
    $('ov-note').textContent = s.error || 'The overlay updates by itself as the plugins write to the Chronicle. It is only reachable from this PC.';
    $('ov-empty').hidden = !!live;
    $('ov-frame').hidden = !live;
    if (current === 'overlay' && live && $('ov-frame').dataset.src !== frameSrc()) {
      $('ov-frame').dataset.src = frameSrc();
      $('ov-frame').src = frameSrc();
    }
  }

  views.overlay = {
    async show() {
      try {
        renderOverlay(await api.overlay.status());
      } catch (e) {
        fail(e);
      }
    }
  };

  $('ov-copy').addEventListener('click', async () => {
    try {
      toast('Copied ' + (await api.overlay.copyUrl()) + '. Paste it into an OBS Browser source.', 'ok');
    } catch (e) {
      fail(e);
    }
  });
  $('ov-bg').addEventListener('change', () => renderOverlay());
  $('ov-reload').addEventListener('click', () => {
    $('ov-frame').dataset.src = '';
    renderOverlay();
  });

  // ================================================================ SETTINGS

  let settingsData = null;

  function newsRow(item) {
    const row = el('div', 'news-row');
    const date = el('input');
    date.placeholder = '2026-10-01';
    date.maxLength = 10;
    date.value = item.date || '';
    date.dataset.k = 'date';
    const title = el('input');
    title.placeholder = 'Title';
    title.maxLength = 120;
    title.value = item.title || '';
    title.dataset.k = 'title';
    const body = el('textarea');
    body.placeholder = 'What the heralds proclaim...';
    body.maxLength = 800;
    body.value = item.body || '';
    body.dataset.k = 'body';
    const rm = el('button', 'btn ghost small rm');
    rm.append(icon('i-x', 'ico'));
    rm.title = 'Remove';
    rm.addEventListener('click', () => row.remove());
    row.append(date, title, rm, body);
    return row;
  }

  function renderNewsRows(list) {
    const box = $('news-rows');
    box.replaceChildren();
    for (const n of list) box.appendChild(newsRow(n));
  }

  function readNewsRows() {
    return $$('#news-rows .news-row').map((r) => {
      const o = {};
      for (const f of $$('[data-k]', r)) o[f.dataset.k] = f.value;
      return o;
    });
  }

  async function loadSettings() {
    settingsData = await api.settings.get();
    const d = settingsData;
    $('set-root').value = d.testRoot;
    $('set-root-msg').textContent = d.testRootOk ? 'Realm only ever changes this copy. It must not be on C: or inside a Steam folder.' : d.testRootReason;
    $('set-root-msg').className = d.testRootOk ? '' : 'bad';
    $('set-exe').value = d.serverExe;
    $('set-discord').value = d.discordUrl || '';
    $('set-steam').textContent = d.steamServer || 'Detected automatically during setup';
    const cfg = d.cfg || {};
    for (const id of ['set-name', 'set-max', 'set-port']) $(id).disabled = !cfg.exists;
    $('set-name').value = cfg.serverName || '';
    $('set-max').value = cfg.maxPlayers || '';
    $('set-port').value = cfg.portNumber || '';
    $('set-cfg-note').textContent = cfg.exists ? 'Read from the test copy. Saved straight into its ServerSettings.cfg.' : 'ServerSettings.cfg does not exist yet. The setup starts the server once to create it.';
    renderNewsRows(d.news || []);
  }

  views.settings = {
    show() {
      loadSettings().catch(fail);
    }
  };

  $('set-save').addEventListener('click', async () => {
    const payload = {
      testRoot: $('set-root').value,
      serverExe: $('set-exe').value,
      discordUrl: $('set-discord').value
    };
    if (settingsData && settingsData.cfg && settingsData.cfg.exists) {
      payload.serverName = $('set-name').value;
      payload.maxPlayers = $('set-max').value;
      payload.port = $('set-port').value;
    }
    try {
      const r = await api.settings.save(payload);
      const changed = (r.cfgChanges || []).map((c) => `${c.key} '${c.from}' -> '${c.to}'`);
      toast(changed.length ? 'Saved. ServerSettings.cfg: ' + changed.join(', ') + ' (old file backed up).' : 'Settings saved.', 'ok');
      config = await api.getConfig();
      renderConfig();
      await loadSettings();
      if (r.needsSetup) {
        toast('That folder has no test copy yet. The setup will create it.');
        openWizard();
      }
    } catch (e) {
      fail(e);
    }
  });
  $('set-root-browse').addEventListener('click', async () => {
    try {
      const p = await api.settings.browse('testRoot');
      if (p) $('set-root').value = p;
    } catch (e) {
      fail(e);
    }
  });
  $('set-steam-browse').addEventListener('click', async () => {
    try {
      const p = await api.settings.browse('steamServer');
      if (p) {
        $('set-steam').textContent = p;
        toast('Steam server folder set.', 'ok');
      }
    } catch (e) {
      fail(e);
    }
  });
  $('news-add').addEventListener('click', () => {
    const today = new Date().toISOString().slice(0, 10);
    $('news-rows').prepend(newsRow({ date: today, title: '', body: '' }));
  });
  $('news-reset').addEventListener('click', async () => {
    try {
      renderNewsRows(await api.settings.saveNews(null));
      renderNews();
      toast('News reset to the bundled list.', 'ok');
    } catch (e) {
      fail(e);
    }
  });
  $('news-save').addEventListener('click', async () => {
    try {
      renderNewsRows(await api.settings.saveNews(readNewsRows()));
      renderNews();
      toast('News saved. It shows on Home.', 'ok');
    } catch (e) {
      fail(e);
    }
  });

  // ================================================================ SETUP WIZARD

  const STEPS = [
    { id: 'steam', label: 'Find the server', sub: 'Reign Of Kings Dedicated Server in Steam', title: 'Finding your Steam server', lead: 'Looking in Steam’s libraries for <b>Reign Of Kings Dedicated Server</b>. It is only read, never changed.' },
    { id: 'copy', label: 'Make a test copy', sub: 'Copied, never moved', title: 'Copying the server to your test folder', lead: 'Realm works on its own copy so your Steam install stays clean. This can take a few minutes.' },
    { id: 'firstRun', label: 'First start', sub: 'Lets the game write its settings', title: 'Starting the server once', lead: 'The game creates its settings files on the first start. Realm stops it again by itself. <b>If Windows asks about the firewall, choose Cancel.</b>' },
    { id: 'download', label: 'Download Oxide', sub: 'Version 2.0.3867, SHA-256 checked', title: 'Downloading Oxide', lead: 'Oxide is the open-source mod framework the Realm plugins run on. The file is checked against its known fingerprint before use.' },
    { id: 'install', label: 'Install Oxide', sub: 'Test copy only, every file backed up', title: 'Installing Oxide into the test copy', lead: 'Every file Oxide replaces is backed up first, so <b>Undo Oxide</b> can put the originals back at any time.' },
    { id: 'plugins', label: 'Raise the banners', sub: 'Houses, crown and chronicle plugins', title: 'Deploying the Realm plugins', lead: 'Copying the Realm plugins into the server. Oxide compiles them the next time the server starts.' }
  ];
  const SKIPPABLE = new Set(['firstRun', 'download', 'install', 'plugins']);
  const wiz = { states: {}, running: false, failedAt: -1, phase: 'intro', lastError: null, status: null };

  function wizRenderSteps(activeIdx) {
    const list = $('step-list');
    list.replaceChildren();
    STEPS.forEach((s, i) => {
      const st = wiz.states[s.id] || 'pending';
      const li = el('li', 'step ' + st + (i === activeIdx ? ' active' : ''));
      const seal = el('span', 'seal');
      if (st === 'done') seal.appendChild(icon('i-check'));
      else if (st === 'failed') seal.appendChild(icon('i-alert'));
      else if (st === 'skipped') seal.appendChild(icon('i-skip'));
      else seal.textContent = String(i + 1);
      const text = el('div');
      text.append(el('div', 'label', s.label), el('div', 'sub', st === 'done' && wiz.status && wiz.status.steps[s.id].done && !wiz.ran?.[s.id] ? 'Already done' : st === 'skipped' ? 'Skipped' : s.sub));
      li.append(seal, text);
      list.appendChild(li);
    });
    const done = STEPS.filter((s) => wiz.states[s.id] === 'done' || wiz.states[s.id] === 'skipped').length;
    $('wiz-overall').style.width = Math.round((done / STEPS.length) * 100) + '%';
    $('wiz-overall-text').textContent = `${done} of ${STEPS.length} steps complete`;
  }

  function wizSetBar(p) {
    const bar = $('wiz-bar');
    const wrap = bar.parentElement;
    if (!p || p.indeterminate || !p.total) {
      wrap.classList.add('indeterminate');
    } else {
      wrap.classList.remove('indeterminate');
      bar.style.width = Math.min(100, Math.round((p.done / p.total) * 100)) + '%';
    }
    if (p && p.text) $('wiz-status').textContent = p.text;
  }

  function wizShow({ kicker, title, lead, folder, progress, error, go, skip, cancel, close }) {
    $('wiz-kicker').textContent = kicker || '';
    $('wiz-title').textContent = title || '';
    $('wiz-lead').innerHTML = lead || '';
    $('wiz-folder').hidden = !folder;
    $('wiz-progress').hidden = !progress;
    $('wiz-error').hidden = !error;
    $('wiz-go').hidden = !go;
    if (go) $('wiz-go-label').textContent = go;
    $('wiz-skip').hidden = !skip;
    $('wiz-cancel').hidden = !cancel;
    $('wiz-close').hidden = close === false;
    const doneMark = $('wiz-body').querySelector('.done-mark');
    if (doneMark) doneMark.remove();
  }

  async function wizRefresh() {
    wiz.status = await api.setup.status();
    for (const s of STEPS) {
      if (wiz.status.steps[s.id].done && wiz.states[s.id] !== 'skipped') wiz.states[s.id] = 'done';
      else if (wiz.states[s.id] === 'done') wiz.states[s.id] = 'pending';
    }
    return wiz.status;
  }

  function wizIntro() {
    wiz.phase = 'intro';
    const st = wiz.status;
    $('wiz-root').value = st.testRoot;
    $('wiz-root-msg').textContent = st.rootOk ? 'Realm only changes this folder. Not on C:, not inside Steam.' : st.rootReason;
    $('wiz-root-msg').className = st.rootOk ? '' : 'bad';
    const remaining = STEPS.filter((s) => !st.steps[s.id].done).length;
    wizShow({
      kicker: 'Welcome',
      title: st.allDone ? 'Your Realm is already standing' : 'Let’s raise your Realm',
      lead: st.allDone
        ? 'Every step is done. You can run the setup again at any time; finished steps are skipped.'
        : `Realm will make a private <b>test copy</b> of your dedicated server, add the Oxide mod framework and the Realm plugins. ${remaining} step(s) to go; finished steps are skipped. You will not need a command window.`,
      folder: true,
      go: st.allDone ? 'Finish' : 'Begin'
    });
    wizRenderSteps(-1);
  }

  function wizDone() {
    wiz.phase = 'done';
    wizShow({ kicker: 'All done', title: 'Your Realm stands ready', lead: 'Start the server on the <b>Server</b> screen. When its console shows it has loaded, press <b>Play</b> and direct connect to <b>127.0.0.1</b> port <b>7350</b>.', go: 'Enter the Realm' });
    const mark = el('div', 'done-mark');
    mark.appendChild(icon('i-check'));
    $('wiz-body').prepend(mark);
    wizRenderSteps(-1);
  }

  async function wizRunFrom(start) {
    wiz.running = true;
    wiz.ran = wiz.ran || {};
    try {
      for (let i = start; i < STEPS.length; i++) {
        const s = STEPS[i];
        await wizRefresh();
        if (wiz.states[s.id] === 'skipped') continue;
        if (wiz.status.steps[s.id].done) {
          wiz.states[s.id] = 'done';
          continue;
        }
        wiz.states[s.id] = 'active';
        wiz.current = i;
        wizRenderSteps(i);
        wizShow({ kicker: `Step ${i + 1} of ${STEPS.length}`, title: s.title, lead: s.lead, progress: true, cancel: s.id === 'copy' || s.id === 'download' || s.id === 'firstRun', close: false });
        wizSetBar({ indeterminate: true, text: 'Working...' });
        $('wiz-bar').style.width = '0';
        try {
          await api.setup.run(s.id);
          wiz.states[s.id] = 'done';
          wiz.ran[s.id] = true;
        } catch (e) {
          wiz.states[s.id] = 'failed';
          wiz.failedAt = i;
          wiz.lastError = e;
          wizRenderSteps(i);
          wizShow({
            kicker: `Step ${i + 1} of ${STEPS.length}`,
            title: e.cancelled ? 'Stopped' : s.title,
            lead: '',
            error: true,
            go: 'Retry',
            skip: SKIPPABLE.has(s.id)
          });
          $('wiz-error-msg').textContent = e.message;
          $('wiz-choose-steam').hidden = !(s.id === 'steam' || s.id === 'copy');
          return;
        }
      }
      await wizRefresh();
      wizDone();
    } finally {
      wiz.running = false;
    }
  }

  async function openWizard() {
    $('wizard').hidden = false;
    wiz.states = {};
    wiz.ran = {};
    wiz.failedAt = -1;
    try {
      await wizRefresh();
    } catch (e) {
      fail(e);
      return;
    }
    wizIntro();
  }

  function closeWizard() {
    if (wiz.running) return;
    $('wizard').hidden = true;
    refreshServer();
  }

  $('wiz-go').addEventListener('click', async () => {
    if (wiz.running) return;
    if (wiz.phase === 'done') {
      try {
        await api.setup.finish();
      } catch (e) {
        return fail(e);
      }
      closeWizard();
      go('server');
      return;
    }
    if (wiz.phase === 'intro') {
      const root = $('wiz-root').value.trim();
      if (root !== wiz.status.testRoot) {
        try {
          await api.settings.save({ testRoot: root });
        } catch (e) {
          $('wiz-root-msg').textContent = e.message;
          $('wiz-root-msg').className = 'bad';
          return;
        }
      }
      if (wiz.status.allDone && root === wiz.status.testRoot) {
        await wizRefresh();
        return wizDone();
      }
      wiz.phase = 'run';
      return wizRunFrom(0);
    }
    // Retry after a failure
    return wizRunFrom(Math.max(0, wiz.failedAt));
  });
  $('wiz-skip').addEventListener('click', () => {
    if (wiz.running || wiz.failedAt < 0) return;
    wiz.states[STEPS[wiz.failedAt].id] = 'skipped';
    return wizRunFrom(wiz.failedAt + 1);
  });
  $('wiz-cancel').addEventListener('click', () => api.setup.cancel());
  $('wiz-close').addEventListener('click', closeWizard);
  $('open-setup').addEventListener('click', openWizard);
  $('wiz-copy-details').addEventListener('click', async () => {
    const e = wiz.lastError;
    if (!e) return;
    await api.copyText(`${e.message}\n\n${e.details || ''}`);
    toast('Details copied. Paste them wherever you ask for help.', 'ok');
  });
  $('wiz-choose-steam').addEventListener('click', async () => {
    try {
      const p = await api.settings.browse('steamServer');
      if (p) toast('Using ' + p + '. Press Retry.', 'ok');
    } catch (e) {
      fail(e);
    }
  });
  $('wiz-root').addEventListener('input', () => {
    $('wiz-root-msg').textContent = 'Realm only changes this folder. Not on C:, not inside Steam.';
    $('wiz-root-msg').className = '';
  });
  $('wiz-root-browse').addEventListener('click', async () => {
    try {
      const p = await api.settings.browse('testRoot');
      if (p) $('wiz-root').value = p;
    } catch (e) {
      fail(e);
    }
  });

  // ================================================================ push events

  api.onPush((msg) => {
    if (msg.type === 'server-lines') appendLines(msg.lines || []);
    else if (msg.type === 'server-status') renderServer(msg.status);
    else if (msg.type === 'busy') {
      busyLabel = msg.label;
      updateTitleStatus();
      renderServer();
    } else if (msg.type === 'progress') {
      if (typeof msg.op === 'string' && msg.op.startsWith('setup:')) wizSetBar(msg);
      else if (msg.op === 'backup' || msg.op === 'restore') {
        $('op-progress').hidden = false;
        if (msg.total) $('op-progress-bar').style.width = Math.round((msg.done / msg.total) * 100) + '%';
        $('op-progress-text').textContent = `${msg.op === 'backup' ? 'Backing up' : 'Restoring'} ${bytes(msg.done || 0)} of ${bytes(msg.total || 0)}`;
      }
    } else if (msg.type === 'overlay') renderOverlay(msg.status);
    else if (msg.type === 'closing') toast('Stopping the server before closing...');
  });

  // ================================================================ start

  document.addEventListener('click', (ev) => {
    const linkBtn = ev.target.closest('[data-link]');
    if (linkBtn) api.openLink(linkBtn.dataset.link).catch(fail);
    const winBtn = ev.target.closest('[data-win]');
    if (winBtn) api.windowAction(winBtn.dataset.win).catch(() => {});
    const nav = ev.target.closest('[data-go]');
    if (nav) go(nav.dataset.go);
  });

  async function start() {
    try {
      config = await api.getConfig();
    } catch (e) {
      fail(e);
      return;
    }
    renderConfig();
    renderNews();
    try {
      const info = await api.appInfo();
      $('app-version').textContent = 'v' + info.version;
    } catch {
      /* ignore */
    }
    await refreshServer();
    go(new URLSearchParams(location.search).get('view') || 'home');
    pollChronicle();
    setInterval(() => {
      if (current === 'home') pollChronicle();
      if (current === 'realm') views.realm.show();
    }, config.pollSeconds * 1000);
    setInterval(() => {
      if (current === 'server') refreshServer();
    }, 10000);
    try {
      const st = await api.setup.status();
      if (!st.setupComplete) openWizard();
    } catch (e) {
      fail(e);
    }
  }

  start();
})();
