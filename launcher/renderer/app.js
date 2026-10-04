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

  // "1 server", "2 servers".
  function plural(n, word) {
    return `${n} ${word}${n === 1 ? '' : 's'}`;
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

  // In-app dialog. Resolves true/false, or the chosen value when options are given.
  //   kicker   small line above the title           tone     'warn' | 'bad' | null (accent of the card)
  //   facts    [{ k, v }] shown as a fact list       fix      one clear "what to do" line
  //   options  [{ value, label, detail, disabled, why, badge, okLabel }] shown as choice tiles; the
  //            first enabled one is selected. okLabel renames the OK button while that tile is chosen.
  // Keyboard: Tab stays inside the dialog, arrows move between choices, Enter confirms, Escape cancels.
  function confirmBox({ title, text, ok = 'OK', danger = false, options = null, kicker = null, facts = null, fix = null, tone = null, cancel = 'Cancel' }) {
    return new Promise((resolve) => {
      const modal = $('modal');
      const card = modal.querySelector('.modal-card');
      const back = document.activeElement;
      card.className = 'modal-card' + (tone ? ' tone-' + tone : '') + (options ? ' has-choices' : '');
      $('modal-kicker').textContent = kicker || '';
      $('modal-kicker').hidden = !kicker;
      $('modal-title').textContent = title;
      $('modal-text').textContent = text || '';
      $('modal-text').hidden = !text;
      const extra = $('modal-extra');
      extra.replaceChildren();
      if (facts && facts.length) {
        const dl = el('dl', 'facts-list modal-facts');
        for (const f of facts) {
          const d = el('div');
          d.append(el('dt', null, f.k), el('dd', f.mono ? 'mono' : '', f.v));
          dl.appendChild(d);
        }
        extra.appendChild(dl);
      }
      if (fix) {
        const p = el('div', 'modal-fix');
        p.append(icon('i-compass', 'ico'), el('p', null, fix));
        extra.appendChild(p);
      }
      const okBtn = $('modal-ok');
      const cancelBtn = $('modal-cancel');
      let group = null;
      const name = 'modal-choice-' + Date.now();
      if (options) {
        group = el('div', 'choice-list');
        group.setAttribute('role', 'radiogroup');
        group.setAttribute('aria-labelledby', 'modal-title');
        let first = true;
        for (const o of options) {
          const lab = el('label', 'choice' + (o.disabled ? ' disabled' : ''));
          const input = el('input');
          input.type = 'radio';
          input.name = name;
          input.value = o.value;
          input.disabled = !!o.disabled;
          if (!o.disabled && first) {
            input.checked = true;
            first = false;
          }
          const body = el('span', 'choice-body');
          const head = el('span', 'choice-label', o.label);
          if (o.badge) head.appendChild(el('span', 'choice-badge', o.badge));
          body.appendChild(head);
          if (o.detail) body.appendChild(el('span', 'choice-detail', o.detail));
          if (o.disabled && o.why) body.appendChild(el('span', 'choice-why', o.why));
          lab.append(input, el('span', 'choice-dot'), body);
          group.appendChild(lab);
        }
        extra.appendChild(group);
      }
      const chosen = () => (group ? (group.querySelector('input:checked') || {}).value || null : true);
      const syncOk = () => {
        const v = chosen();
        const o = options && options.find((x) => x.value === v);
        okBtn.textContent = (o && o.okLabel) || ok;
        okBtn.disabled = !!group && !v;
      };
      if (group) group.addEventListener('change', syncOk);
      okBtn.className = 'btn ' + (danger ? 'danger' : 'primary');
      cancelBtn.textContent = cancel;
      cancelBtn.hidden = cancel === null;
      syncOk();
      modal.hidden = false;
      const focusables = () => Array.from(card.querySelectorAll('button:not([disabled]):not([hidden]), input:not([disabled]):checked, input:not([disabled]):not([type="radio"])'));
      (group ? group.querySelector('input:checked') || okBtn : okBtn).focus();
      const onKey = (ev) => {
        if (ev.key === 'Escape') {
          ev.preventDefault();
          done(false);
        } else if (ev.key === 'Enter' && ev.target.type === 'radio') {
          ev.preventDefault();
          okBtn.click();
        } else if (ev.key === 'Tab') {
          const f = focusables();
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
      function done(v) {
        modal.hidden = true;
        okBtn.onclick = null;
        cancelBtn.onclick = null;
        okBtn.disabled = false;
        modal.removeEventListener('keydown', onKey);
        if (back && document.contains(back) && typeof back.focus === 'function') back.focus();
        resolve(v);
      }
      okBtn.onclick = () => done(group ? chosen() : true);
      cancelBtn.onclick = () => done(false);
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
    for (const b of $$('.rail-btn[data-go]')) {
      b.classList.toggle('active', b.dataset.go === name);
      if (b.dataset.go === name) b.setAttribute('aria-current', 'page');
      else b.removeAttribute('aria-current');
    }
    if (views[name].show) views[name].show();
  }

  // ---------------------------------------------------------------- shared state

  let config = null;
  // srv: the server selected on the Servers screen. home: Server 1 (Home, Chronicle and overlay follow it).
  let selId = 's1';
  let srv = { state: 'stopped' };
  const srvById = {};
  let fleetList = [];
  let busyLabel = null;

  function home() {
    return srvById.s1 || { state: 'stopped' };
  }

  function updateTitleStatus() {
    const box = $('title-status');
    box.className = 'title-status';
    const list = fleetList.length ? fleetList : [{ id: 's1', state: home().state, ready: home().ready }];
    const up = list.filter((f) => f.state === 'running' && f.ready).length;
    const moving = list.filter((f) => f.state === 'starting' || f.state === 'stopping' || (f.state === 'running' && !f.ready)).length;
    let text;
    if (busyLabel) {
      text = busyLabel + '...';
      box.classList.add('busy');
    } else if (list.length === 1) {
      const f = list[0];
      text = f.state === 'running' ? (f.ready ? 'Server running' : 'Server loading') : f.state === 'starting' ? 'Server starting' : f.state === 'stopping' ? 'Server stopping' : 'Server stopped';
      if (f.state === 'running') box.classList.add(f.ready ? 'running' : 'busy');
      else if (f.state !== 'stopped') box.classList.add('busy');
    } else {
      text = `${up} of ${list.length} servers running` + (moving ? `, ${moving} changing` : '');
      if (moving) box.classList.add('busy');
      else if (up) box.classList.add('running');
    }
    $('title-status-text').textContent = text;
  }

  // ================================================================ HOME

  // Every Chronicle event has its own icon and tone (renderer/heraldry.js, from art/icons/event-map.json).
  const ART = window.RealmArt;
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
    $('server-address').textContent = config.server.address + '   port ' + config.server.port;
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
    const srv = home();
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
      detail = 'The server is starting. It is ready when the console shows "Game has started."';
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

  // null until the first poll answers; then 'ok' or 'down' (the Chronicle service did not answer).
  let chronicleReach = null;

  function renderEvents() {
    const list = $('events');
    list.replaceChildren();
    list.setAttribute('aria-busy', String(chronicleReach === null));
    if (!events.length) {
      if (chronicleReach === null && ART) list.append(...ART.skeletonRows(4));
      else if (chronicleReach === 'down' && ART)
        list.appendChild(ART.errorBox({ tag: 'li', title: 'The Chronicle is not answering', body: 'Realm runs it on this PC at 127.0.0.1:8787.', fix: 'Open the Overlay screen to see why, or start the server.', action: { label: 'Open Overlay', onClick: () => go('overlay') } }));
      else if (ART) list.appendChild(ART.emptyState({ tag: 'li', art: 'decree', title: 'The Chronicle is silent', body: 'Coronations, oaths and claims appear here once the plugins record them.' }));
      else list.appendChild(el('li', 'empty', 'The Chronicle is silent. Events appear here once the plugins record them.'));
      return;
    }
    for (const e of events) {
      const li = el('li', 'event t-' + e.type);
      const body = el('div');
      body.append(el('p', 'event-title', e.title));
      body.append(el('p', 'event-meta', [ago(e.ts), e.detail].filter(Boolean).join(' · ')));
      li.append(ART ? ART.eventMark(e) : icon('i-scroll', 'event-ico'), body);
      list.appendChild(li);
    }
  }

  async function pollChronicle() {
    if (chronicleReach === null) renderEvents(); // skeleton rows until the first answer
    try {
      chronicleState = await api.getState();
    } catch {
      chronicleState = null;
    }
    let reached = true;
    try {
      const fresh = (await api.getEvents(lastEventId)).filter((e) => e && Number.isInteger(e.id) && e.id > lastEventId);
      if (fresh.length) {
        lastEventId = Math.max(lastEventId, ...fresh.map((e) => e.id));
        events = fresh.concat(events).sort((a, b) => b.id - a.id).slice(0, MAX_EVENTS);
      }
    } catch {
      reached = false;
    }
    chronicleReach = reached ? 'ok' : 'down';
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
      toast('Opening Steam. Then Direct Connect: address ' + config.server.address + ', port ' + config.server.port + '.', 'ok');
    } catch {
      toast('Could not open Steam. Is it installed?', 'bad');
    }
  });
  $('console-logs').addEventListener('click', async () => {
    try {
      await api.settings.openFolder('applogs');
    } catch (e) {
      fail(e);
    }
  });
  $('copy-address').addEventListener('click', async () => {
    try {
      const a = await api.copyAddress();
      toast('Copied ' + a.address + '. In Direct Connect paste it in the address box, and type ' + a.port + ' in the port box.', 'ok');
    } catch (e) {
      fail(e);
    }
  });

  // ================================================================ SERVER

  let lastLineId = 0;
  const MAX_DOM_LINES = 2000;

  // The two lines that mean the game is up (lib/readiness.js); highlighted in the console.
  const READY_LINE = /(^|\]\s*)(Server for \d+ players started on port \d+\.|Game has started\.)\s*$/;

  function lineNode(l) {
    const cls = l.src === 'err' ? 'err' : l.src === 'sys' ? 'sys' : l.src === 'log' ? 'log' : '';
    const n = el('span', 'ln ' + cls + (READY_LINE.test(l.text) ? ' ready' : ''));
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

  const ROMAN = { s1: 'I', s2: 'II', s3: 'III', s4: 'IV' };

  function renderServer(st) {
    if (st && st.id && st.id !== selId) {
      srvById[st.id] = { ...(srvById[st.id] || {}), ...st };
      updateTitleStatus();
      if (current === 'home' && st.id === 's1') renderHomeStatus();
      return;
    }
    if (st) {
      srv = { ...srv, ...st };
      srvById[selId] = srv;
    }
    const s = srv;
    const pill = $('srv-pill');
    const map = { running: s.ready ? ['ok', 'Running'] : ['warn', 'Loading'], starting: ['warn', 'Starting'], stopping: ['warn', 'Stopping'], stopped: ['', 'Stopped'] };
    const [kind, label] = map[s.state] || ['', s.state];
    pill.className = 'pill big ' + kind;
    pill.textContent = label;
    const running = s.state !== 'stopped';
    $('srv-meta-1').textContent = running ? `${s.exe || 'Server'}.exe · pid ${s.pid || '-'}` : s.copied === false ? 'No test copy yet' : 'Not running';
    $('srv-meta-2').textContent = running
      ? `started ${ago(s.startedAt)} · ${plural((s.players || []).length, 'player')}`
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
    for (const id of ['act-backup', 'act-restore', 'act-undo-oxide']) $(id).disabled = noCopy || !!busyLabel;
    $('act-plugins').disabled = !!busyLabel || !fleetList.some((f) => f.copied);
    if ('oxideBackup' in s) $('act-undo-oxide').disabled = $('act-undo-oxide').disabled || !s.oxideBackup;
    if ('testRoot' in s) {
      $('fact-root').textContent = s.testRoot || 'Not set';
      $('fact-oxide').textContent = s.oxide ? `Installed (${s.oxide})` : 'Not installed';
      $('fact-oxide').className = s.oxide ? 'ok' : '';
      $('fact-cfg').textContent = s.cfgExists ? 'Present' : 'Not created yet (first start creates it)';
    }
    if (s.ports) {
      $('fact-net').textContent = (s.network === 'public' ? 'Public (0.0.0.0)' : 'This PC only (127.0.0.1)') + ` · game ${s.ports.game} · query ${s.ports.query} · RCON off`;
    }
    renderSupLine(s.supervisor);
    if (external) $('srv-meta-2').textContent = 'A server from this folder is running outside Realm (pid ' + s.external.map((p) => p.pid).join(', ') + ').';
    updateTitleStatus();
    if (current === 'home') renderHomeStatus();
  }

  async function refreshServer() {
    try {
      renderServer(await api.server.status(selId));
      if (selId !== 's1') srvById.s1 = { ...(srvById.s1 || {}), ...(await api.server.status('s1')) };
    } catch (e) {
      fail(e);
    }
    refreshFleet();
  }

  function renderSupLine(sup) {
    const line = $('inst-sup');
    line.className = 'fine sup-line';
    if (!sup) return;
    const parts = [];
    if (sup.halted) {
      line.classList.add('bad');
      parts.push(sup.halted);
    }
    if (sup.nextRestartAt) parts.push('Restarting at ' + new Date(sup.nextRestartAt).toLocaleTimeString() + ' after a crash.');
    if (sup.plannedAt) parts.push('Next daily restart ' + when(sup.plannedAt) + '.');
    if (sup.crashes24h) parts.push(sup.crashes24h + ' crash(es) in 24 h.');
    if (sup.lastBackup) parts.push('Last automatic backup ' + ago(sup.lastBackup.at) + '.');
    line.textContent = parts.join(' ');
  }

  // ---------- fleet strip (up to four servers)

  function fleetCard(f) {
    const b = el('button', 'fleet-card' + (f.id === selId ? ' active' : ''));
    b.setAttribute('role', 'tab');
    b.dataset.inst = f.id;
    const up = f.state === 'running' && f.ready;
    const moving = f.state !== 'stopped' && !up;
    const num = el('span', 'num' + (up ? ' on' : moving ? ' busy' : f.supervisor && f.supervisor.halted ? ' bad' : ''), ROMAN[f.id] || f.id);
    const name = el('span', 'fc-name', f.name);
    let meta = !f.copied ? 'Not set up yet' : up ? `${f.players}/${f.maxPlayers || '?'} players` : f.state === 'stopped' ? (f.supervisor && f.supervisor.nextRestartAt ? 'Restarting soon' : 'Stopped') : f.state;
    meta += ` · ${f.ports.game}` + (f.network === 'public' ? ' · public' : '');
    b.append(num, name, el('span', 'fc-meta', meta));
    b.title = f.root;
    return b;
  }

  function renderFleet() {
    const strip = $('fleet-strip');
    strip.replaceChildren();
    for (const f of fleetList) strip.appendChild(fleetCard(f));
    if (fleetList.length < 4) {
      const add = el('button', 'fleet-card add');
      add.id = 'fleet-add';
      add.append(el('span', 'num', '+'), el('span', 'fc-name', 'Add server'), el('span', 'fc-meta', 'Up to 4 on this PC'));
      strip.appendChild(add);
    }
    const sel = fleetList.find((f) => f.id === selId);
    if (sel) $('srv-title').textContent = sel.name;
    for (const f of fleetList) srvById[f.id] = { ...(srvById[f.id] || {}), state: f.state, ready: f.ready };
    updateTitleStatus();
  }

  async function refreshFleet() {
    try {
      fleetList = await api.fleet.list();
      renderFleet();
    } catch {
      /* ignore */
    }
  }

  async function selectInstance(id) {
    if (id === selId) return;
    selId = id;
    srv = { state: 'stopped' };
    lastLineId = 0;
    $('console').replaceChildren();
    renderFleet();
    await views.server.show();
  }

  $('fleet-strip').addEventListener('click', async (ev) => {
    const card = ev.target.closest('.fleet-card');
    if (!card) return;
    if (card.id === 'fleet-add') return addInstance();
    selectInstance(card.dataset.inst);
  });

  async function addInstance() {
    const n = fleetList.length + 1;
    const ok = await confirmBox({
      title: 'Add another server?',
      text: `Realm makes another full copy of the dedicated server (several GB) in its own folder, with its own ports, then runs the same setup as the first one. Up to four servers can run on this PC; whether four fit in its memory is untested, so add them one at a time.`,
      ok: 'Choose folder'
    });
    if (!ok) return;
    let root = null;
    try {
      root = await api.settings.browse('instanceRoot');
    } catch (e) {
      return fail(e);
    }
    try {
      const r = await api.fleet.add(root ? { root } : {});
      toast(`Server ${n} added (game port ${r.ports.game}, query port ${r.ports.query}). Now set it up.`, 'ok');
      await refreshFleet();
      await selectInstance(r.id);
      openWizard(r.id);
    } catch (e) {
      fail(e);
    }
  }

  // ---------- per-server settings card

  let instData = null;

  async function loadInstance() {
    try {
      instData = await api.fleet.instance(selId);
    } catch (e) {
      return fail(e);
    }
    const d = instData;
    const cfg = d.cfg || {};
    $('inst-id').textContent = `Server ${d.id.slice(1)} · ${d.id}`;
    for (const id of ['inst-name', 'inst-max', 'inst-pacing']) $(id).disabled = !cfg.exists;
    $('inst-name').value = cfg.serverName || '';
    $('inst-max').value = cfg.maxPlayers || '';
    $('inst-max').max = String(d.maxPlayersCap || 120);
    $('inst-pacing').value = cfg.joinPacing || '';
    $('inst-port').value = d.ports.game;
    $('inst-query').value = d.ports.query;
    $('inst-ports-note').textContent = `Players connect to UDP ${d.ports.game}; TCP ${d.ports.game} carries the ping check; UDP ${d.ports.query} answers Steam queries. RCON stays off (its port is ${d.ports.rcon}).`;
    $('inst-note').textContent = cfg.exists ? 'Name, max players and join pacing are saved into this server\'s ServerSettings.cfg (stop it first). Ports and the restart plan are applied at every start.' : 'This copy has no ServerSettings.cfg yet. Run its setup first.';
    $('inst-auto').checked = d.autoRestart;
    $('inst-backup').checked = d.backupBeforeRestart;
    $('inst-daily').checked = d.dailyRestart.enabled;
    $('inst-time').value = d.dailyRestart.time;
    $('inst-remove').hidden = d.id === 's1';
    const f = fleetList.find((x) => x.id === d.id);
    $('inst-setup').hidden = !!(f && f.copied && cfg.exists);
    renderSupLine(d.supervisor);
  }

  $('inst-save').addEventListener('click', async () => {
    if (!instData) return;
    const patch = {
      ports: { game: $('inst-port').value, query: $('inst-query').value },
      autoRestart: $('inst-auto').checked,
      backupBeforeRestart: $('inst-backup').checked,
      dailyRestart: { enabled: $('inst-daily').checked, time: $('inst-time').value }
    };
    if (instData.cfg && instData.cfg.exists) {
      patch.cfg = { serverName: $('inst-name').value, maxPlayers: $('inst-max').value };
      if ($('inst-pacing').value !== '') patch.cfg.joinPacing = $('inst-pacing').value;
    }
    try {
      const r = await api.fleet.update(selId, patch);
      const changed = (r.cfgChanges || []).map((c) => `${c.key} '${c.from}' -> '${c.to}'`);
      const missing = (r.cfgMissing || []).length ? ` Not in the file (left alone): ${r.cfgMissing.join(', ')}.` : '';
      toast((changed.length ? 'Saved. ServerSettings.cfg: ' + changed.join(', ') + ' (old file backed up).' : 'Server settings saved.') + missing, 'ok');
      await loadInstance();
      refreshServer();
    } catch (e) {
      fail(e);
    }
  });
  $('inst-setup').addEventListener('click', () => openWizard(selId));
  $('inst-remove').addEventListener('click', async () => {
    const ok = await confirmBox({ title: `Remove Server ${selId.slice(1)}?`, text: 'Realm forgets this server. Its folder, world and backups stay on disk; delete them yourself if you no longer need them.', ok: 'Remove', danger: true });
    if (!ok) return;
    try {
      const r = await api.fleet.remove(selId);
      toast(`Removed. The folder ${r.folderKept} was kept.`, 'ok');
      await refreshFleet();
      selId = 'x';
      await selectInstance('s1');
    } catch (e) {
      fail(e);
    }
  });

  views.server = {
    async show() {
      await refreshServer();
      try {
        appendLines(await api.server.log(selId, lastLineId));
      } catch {
        /* ignore */
      }
      consolePlaceholder();
      loadInstance();
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

  // Pre-start dialog (lib/prestart.js and lib/worlds.js do the work). Before Start, ask the app what
  // the start would run into: another program on the ports, or a world choice.
  const HOLDER_KIND = {
    leftover: 'Server from this folder',
    watchdog: 'Server.exe watchdog',
    'game-client': 'The game',
    'other-server': 'Server from another folder',
    hidden: 'Hidden ROK.exe',
    unknown: 'Another program'
  };

  function holderFacts(s) {
    const facts = [];
    for (const h of (s.holders || []).slice(0, 4)) {
      facts.push({ k: HOLDER_KIND[h.kind] || 'Program', v: `${h.name}.exe · pid ${h.pid}${h.ports && h.ports.length ? ' · ' + h.ports.join(', ') : ''}${h.path ? '\n' + h.path : ''}`, mono: true });
    }
    if (!facts.length && s.busy && s.busy.length) facts.push({ k: 'In use', v: s.busy.map((x) => `${x.proto.toUpperCase()} ${x.port}`).join(', '), mono: true });
    return facts;
  }

  async function startWithChecks(id, opts) {
    let pre = null;
    try {
      pre = await api.server.prestart(id);
    } catch (e) {
      return fail(e);
    }
    const s = pre && pre.survey;
    const label = 'Server ' + (ROMAN[id] || id);
    if (s && !s.clear) {
      const adopt = s.actions.adopt;
      const stop = s.actions.stop;
      if (!adopt.ok && !stop.ok) {
        // Nothing Steward can safely do: say what holds the ports and the one thing that fixes it.
        const again = await confirmBox({
          kicker: label + ' · before start',
          title: s.verdict === 'game-client' ? 'Reign of Kings is running' : s.verdict === 'watchdog' ? 'The Server.exe watchdog is running' : 'Something else holds this server\u2019s ports',
          text: s.message,
          facts: holderFacts(s),
          fix: stop.why || adopt.why,
          tone: 'warn',
          ok: 'Check again',
          cancel: 'Close'
        });
        if (again) return startWithChecks(id, opts);
        return refreshServer();
      }
      const choice = await confirmBox({
        kicker: label + ' · before start',
        title: 'This server is already running',
        text: s.message,
        facts: holderFacts(s),
        tone: 'warn',
        options: [
          { value: 'adopt', label: 'Adopt it', badge: adopt.ok ? 'Recommended' : null, detail: adopt.ok ? adopt.why : 'Keep it running and let Steward show and control it.', disabled: !adopt.ok, why: adopt.why, okLabel: 'Adopt it' },
          { value: 'stop', label: 'Stop it cleanly, then start', detail: stop.ok ? stop.why : 'Ask it to save and shut down.', disabled: !stop.ok, why: stop.why, okLabel: 'Stop it cleanly' }
        ],
        ok: 'Continue'
      });
      if (choice === 'adopt') return guarded(() => api.server.adopt(id), 'Adopted: Steward now shows and controls this server.');
      if (choice === 'stop') {
        toast('Asking the other server to save and shut down...');
        const r = await guarded(() => api.server.stopHolder(id));
        if (r) toast(r.message, r.stopped && !r.relaunched ? 'ok' : 'bad');
        if (r && r.stopped && !r.relaunched) return startWithChecks(id, opts);
      }
      return null;
    }
    const w = pre && pre.world;
    if (!opts && w && (w.action === 'choose' || w.action === 'blocked')) {
      if (w.action === 'blocked') {
        const again = await confirmBox({ kicker: label + ' · before start', title: 'The world is in use', text: w.message, tone: 'warn', ok: 'Check again', cancel: 'Close' });
        return again ? startWithChecks(id, opts) : refreshServer();
      }
      const worlds = w.choices.slice().sort((a, b) => (b.slot === w.suggest) - (a.slot === w.suggest));
      const options = worlds.map((c) => ({ value: 'slot:' + c.slot, label: `World ${c.slot}`, badge: c.slot === w.suggest ? 'Suggested' : null, detail: [c.remembered ? 'Last run under Realm' : null, c.newestMs ? 'saved ' + ago(new Date(c.newestMs).toISOString()) : null].filter(Boolean).join(', ') || null, okLabel: `Start world ${c.slot}` }));
      options.push({ value: 'new', label: 'A new world', detail: 'The game makes a fresh world. The others stay on disk.', okLabel: 'Start a new world' });
      const choice = await confirmBox({ kicker: label + ' · before start', title: 'Which world?', text: w.message, ok: 'Start', options });
      if (!choice) return null;
      if (choice === 'new') {
        const sure = await confirmBox({ title: 'Start a new world?', text: `The game makes a fresh world. The saved worlds stay in ${pre.saveLocation || 'Saves/'} and can be chosen again later.`, ok: 'Start a new world', danger: true });
        return sure ? startWithChecks(id, { newWorld: true }) : null;
      }
      return startWithChecks(id, { worldSlot: Number(choice.slice(5)) });
    }
    return guarded(() => api.server.start(id, opts || null), 'Server starting. It is ready at "Game has started."');
  }

  $('srv-start').addEventListener('click', () => startWithChecks(selId));
  $('srv-stop').addEventListener('click', () => guarded(() => api.server.stop(selId), 'Sent "quit". The server saves and closes.'));
  $('srv-restart').addEventListener('click', async () => {
    toast('Restarting: sending "quit" first...');
    const r = await guarded(() => api.server.restart(selId));
    if (r && r.needsForce) toast('The server did not close in time. Use Force stop, then Start.', 'bad');
  });
  $('srv-force').addEventListener('click', async () => {
    const ok = await confirmBox({ title: 'Force stop the server?', text: 'This ends the server process right away. Anything not yet saved is lost. Prefer waiting a little longer if it is still saving.', ok: 'Force stop', danger: true });
    if (ok) guarded(() => api.server.forceStop(selId), 'Server force stopped.');
  });
  $('console-form').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const input = $('console-cmd');
    const text = input.value.trim();
    if (!text) return;
    try {
      await api.server.command(selId, text);
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
      () => api.server.deployPlugins('all'),
      (r) => {
        const parts = [];
        parts.push(r.copied ? `Deployed ${plural(r.copied, 'plugin file')} to ${plural(r.servers, 'server')}. Oxide reloads them while the server runs.` : `All plugins are already up to date on ${plural(r.servers, 'server')}.`);
        if (r.dataCopied) parts.push(`${plural(r.dataCopied, 'data file')} (monuments, sign art, quests) copied too${r.reloaded && r.reloaded.length ? `; reloaded ${r.reloaded.filter((x) => x.ok).map((x) => x.plugin).join(', ') || 'none'}` : ''}.`);
        if (r.data && r.data.invalid.length) parts.push(`Not copied (damaged): ${r.data.invalid.map((x) => x.file).join(', ')}.`);
        return parts.join(' ');
      }
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
    await guarded(() => api.server.backup(selId, allowRunning), (r) => `Backup written: ${r.name} (${bytes(r.size)}).`);
    showOpProgress(false);
  });

  $('act-restore').addEventListener('click', async () => {
    let list;
    try {
      list = await api.server.listBackups(selId);
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
    await guarded(() => api.server.restore(selId, pick), (r) => `Restored ${r.restored} files. The previous world was moved aside${r.safetyBackup ? ' and saved as ' + r.safetyBackup : ''}.`);
    showOpProgress(false);
  });

  $('act-undo-oxide').addEventListener('click', async () => {
    const ok = await confirmBox({
      title: 'Undo Oxide?',
      text: 'This puts back the original game files of the test copy from the backup made when Oxide was installed, and removes the files Oxide added. Plugin and data folders stay. The server must be stopped.',
      ok: 'Undo Oxide',
      danger: true
    });
    if (ok) guarded(() => api.server.undoOxide(selId), (r) => `Oxide removed: ${plural(r.restored, 'original file')} restored, ${r.removed} removed.`);
  });

  for (const b of $$('[data-open]')) {
    b.addEventListener('click', () => api.settings.openFolder(b.dataset.open, selId).catch(fail));
  }

  // ================================================================ REALM

  // Great houses show their drawn shield; any other house a plain shield in its overlay dye.
  function houseShield(name, cls) {
    if (ART) return ART.houseMark(name, 'shield', cls || 'shield');
    const span = el('span', cls || 'shield', String(name).trim().charAt(0).toUpperCase());
    return span;
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
    if (!houses.length && data.stateError && ART) {
      // No answer is not the same as no houses: say so, with the one place that explains why.
      tree.appendChild(ART.errorBox({ title: 'The Chronicle did not answer', body: String(data.stateError), fix: 'Start the server, or open the Overlay screen to see why the Chronicle is down.', action: { label: 'Open Overlay', onClick: () => go('overlay') } }));
      return;
    }
    if (!houses.length) {
      tree.appendChild(ART ? ART.emptyState({ art: 'house', title: 'No houses yet', body: 'Houses appear here when players found them in game.', cmd: '/house found "Name" Sigil' }) : el('p', 'empty', 'No houses have been founded yet. In game: /house found "Name" Sigil'));
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
        renderRealm({ state: {}, stateError: (e && e.message) || String(e) });
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

  // ================================================================ GO PUBLIC

  let pubId = 's1';
  let pubData = null;

  function renderPick(boxId, active, onPick) {
    const box = $(boxId);
    box.replaceChildren();
    for (const f of fleetList.length ? fleetList : [{ id: 's1', name: 'Server 1' }]) {
      const b = el('button', f.id === active ? 'active' : '', 'Server ' + (ROMAN[f.id] || f.id));
      b.title = f.name || '';
      b.addEventListener('click', () => onPick(f.id));
      box.appendChild(b);
    }
  }

  function row(cells, cls) {
    const tr = el('tr', cls);
    for (const c of cells) {
      const td = el('td', c.cls || '', c.text);
      tr.appendChild(td);
    }
    return tr;
  }

  function head(labels) {
    const tr = el('tr');
    for (const l of labels) tr.appendChild(el('th', null, l));
    return tr;
  }

  function renderPublic() {
    const d = pubData;
    if (!d) return;
    renderPick('pub-pick', pubId, (id) => {
      pubId = id;
      views.public.show();
    });
    $('pub-net-local').checked = d.network !== 'public';
    $('pub-net-public').checked = d.network === 'public';
    $('pub-listed').checked = !!d.listed;
    $('pub-listed').disabled = d.network !== 'public';
    $('pub-net-state').textContent = `${d.name}: ${d.network === 'public' ? 'public' : 'this PC only'}${d.running ? ' (running; changes apply at the next start)' : ''}.`;

    const rules = $('pub-rules');
    rules.replaceChildren(head(['Rule', 'Action', 'Protocol', 'Ports']));
    const present = new Set((d.firewall.present || []).map((r) => r.name));
    for (const r of d.rules) {
      rules.appendChild(row([{ text: r.name, cls: present.has(r.name) ? 'present' : '' }, { text: r.action, cls: r.action === 'Block' ? 'block' : '' }, { text: r.protocol }, { text: r.ports, cls: 'mono' }]));
    }
    if (d.programError) $('pub-fw-state').textContent = d.programError;
    else if (!d.firewall.supported) $('pub-fw-state').textContent = `Every rule applies only to ${d.program}. Rules can only be added on Windows.`;
    else $('pub-fw-state').textContent = (present.size ? `${present.size} of ${d.rules.length} rules are in place (green). ` : 'No Realm rules yet. ') + `Every rule applies only to ${d.program}.`;
    $('pub-fw-add').disabled = !d.firewall.supported || !!d.programError;
    $('pub-fw-remove').disabled = !d.firewall.supported || !present.size;

    const router = $('pub-router');
    router.replaceChildren(head(['Forward', 'Protocol', 'To this PC', 'Why']));
    for (const sck of d.sockets) router.appendChild(row([{ text: String(sck.port), cls: 'mono' }, { text: sck.proto.toUpperCase() }, { text: String(sck.port), cls: 'mono' }, { text: sck.what === 'game' ? 'Game traffic' : sck.what === 'ping' ? 'Ping check (players are kicked without it)' : 'Steam query (server list ping)' }]));
    $('pub-lan').textContent = d.lan.length ? d.lan.map((l) => l.address).join(', ') : 'unknown';
  }

  views.public = {
    async show() {
      if (!fleetList.length) await refreshFleet();
      try {
        pubData = await api.goPublic.status(pubId);
        renderPublic();
      } catch (e) {
        fail(e);
      }
    }
  };

  for (const r of $$('input[name="pub-net"]')) r.addEventListener('change', () => ($('pub-listed').disabled = !$('pub-net-public').checked));
  $('pub-net-save').addEventListener('click', async () => {
    try {
      const r = await api.goPublic.setNetwork(pubId, { network: $('pub-net-public').checked ? 'public' : 'local', listed: $('pub-listed').checked });
      toast(`Saved: ${r.network === 'public' ? 'public' : 'this PC only'}${r.listed ? ', listed in the game browser' : ''}.${r.appliesAtNextStart ? ' Restart the server to apply it.' : ''}`, 'ok');
      views.public.show();
      refreshFleet();
    } catch (e) {
      fail(e);
    }
  });
  $('pub-fw-add').addEventListener('click', async () => {
    const ok = await confirmBox({
      title: 'Add Windows Firewall rules?',
      text: 'Realm will show the exact rules once more, then Windows asks for administrator permission. The rules only allow this server\'s ROK.exe on its own ports, and block the admin console ports. Remove rules undoes them.',
      ok: 'Continue'
    });
    if (!ok) return;
    try {
      await api.goPublic.firewallAdd(pubId);
      toast('Firewall rules added.', 'ok');
    } catch (e) {
      fail(e);
    }
    views.public.show();
  });
  $('pub-fw-remove').addEventListener('click', async () => {
    const ok = await confirmBox({ title: 'Remove the Realm firewall rules?', text: 'Windows asks for administrator permission, then exactly the rules listed here are deleted. Players outside this PC can no longer connect.', ok: 'Remove rules', danger: true });
    if (!ok) return;
    try {
      await api.goPublic.firewallRemove(pubId);
      toast('Firewall rules removed.', 'ok');
    } catch (e) {
      fail(e);
    }
    views.public.show();
  });
  $('pub-test').addEventListener('click', async () => {
    const list = $('pub-checks');
    list.replaceChildren(...(ART ? ART.skeletonRows(4) : [el('li', 'empty', 'Checking...')]));
    list.setAttribute('aria-busy', 'true');
    $('pub-test').disabled = true;
    try {
      const r = await api.goPublic.selfTest(pubId);
      list.replaceChildren();
      for (const c of r.checks) {
        const li = el('li', c.status);
        li.append(el('span', 'dot'), el('b', null, c.label), el('span', null, c.detail));
        list.appendChild(li);
      }
    } catch (e) {
      list.replaceChildren(ART ? ART.errorBox({ tag: 'li', title: 'The checks did not finish', body: e.message, fix: 'Start the server, then press Run checks again.' }) : el('li', 'empty', e.message));
    } finally {
      list.removeAttribute('aria-busy');
      $('pub-test').disabled = false;
    }
  });

  // ================================================================ PUBLISH

  let publishData = null;

  function plRow(sv) {
    const box = el('div', 'pl-row' + (sv.include ? '' : ' off'));
    box.dataset.id = sv.id;
    const headBox = el('div', 'pl-head');
    const inc = el('input');
    inc.type = 'checkbox';
    inc.checked = sv.include;
    inc.dataset.k = 'include';
    inc.addEventListener('change', () => box.classList.toggle('off', !inc.checked));
    const lbl = el('label', 'check');
    lbl.append(inc, el('span', null, 'List'));
    headBox.append(lbl, el('b', null, 'Server ' + (ROMAN[sv.id] || sv.id)), el('span', 'fine', `port ${sv.port} · query ${sv.queryPort}${sv.network !== 'public' ? ' · still set to this PC only' : ''}`));
    box.appendChild(headBox);
    const field = (label, key, value, cls, attrs = {}) => {
      const l = el('label', cls || '');
      const i = el('input');
      i.value = value == null ? '' : String(value);
      i.dataset.k = key;
      for (const [k, v] of Object.entries(attrs)) i.setAttribute(k, v);
      l.append(el('span', null, label), i);
      box.appendChild(l);
      return i;
    };
    field('Name', 'name', sv.name, 'span2', { maxlength: '64' });
    field('Region', 'region', sv.region, '', { maxlength: '32', placeholder: 'EU' });
    field('Public address', 'address', sv.address, 'span2', { maxlength: '253', placeholder: 'play.example.org', spellcheck: 'false' });
    field('Max players', 'maxPlayers', sv.maxPlayers, '', { type: 'number', min: '1', max: '1000' });
    field('Chronicle address (optional)', 'chronicleUrl', sv.chronicleUrl, 'span3', { maxlength: '200', placeholder: 'https://chronicle.example.org', spellcheck: 'false' });
    return box;
  }

  function renderPublish() {
    const d = publishData;
    if (!d) return;
    if (d.key) {
      $('key-state').textContent = `Key ${d.key.keyId}, made ${d.key.created ? when(d.key.created) : ''}. ${d.key.protection === 'safeStorage' ? 'Stored encrypted for your Windows user.' : 'Stored unencrypted in the Realm Steward profile folder (this system offers no key protection). Keep that folder private.'}`;
      $('key-public').textContent = d.key.publicKey;
    } else {
      $('key-state').textContent = d.keyError || 'No signing key yet. Create one once; keep using it for every list you publish.';
    }
    $('key-box').hidden = !d.key;
    $('key-create').hidden = !!d.key;
    $('key-replace').hidden = !d.key;
    $('pl-realm').value = d.realm;
    $('pl-url').value = d.manifestUrl;
    $('pl-days').value = d.validDays;
    $('pl-rules').value = d.rulesUrl;
    $('pl-out').value = d.outDir;
    $('pl-embed-row').hidden = !d.canEmbed;
    const rows = $('pl-rows');
    rows.replaceChildren();
    for (const sv of d.servers) rows.appendChild(plRow(sv));
    $('pl-write').disabled = !d.key;
  }

  views.publish = {
    async show() {
      try {
        publishData = await api.publish.status();
        renderPublish();
      } catch (e) {
        fail(e);
      }
    }
  };

  $('key-create').addEventListener('click', async () => {
    try {
      const k = await api.publish.createKey(false);
      toast(`Signing key ${k.keyId} created.`, 'ok');
      views.publish.show();
    } catch (e) {
      fail(e);
    }
  });
  $('key-replace').addEventListener('click', async () => {
    try {
      const k = await api.publish.createKey(true);
      toast(`New signing key ${k.keyId}. Build and hand out a new player installer.`, 'ok');
      views.publish.show();
    } catch (e) {
      fail(e);
    }
  });
  $('key-copy').addEventListener('click', async () => {
    try {
      await api.publish.copyKey();
      toast('Public key copied.', 'ok');
    } catch (e) {
      fail(e);
    }
  });
  $('pl-out-browse').addEventListener('click', async () => {
    try {
      const p = await api.settings.browse('publishOut');
      if (p) $('pl-out').value = p;
    } catch (e) {
      fail(e);
    }
  });
  $('pl-write').addEventListener('click', async () => {
    const servers = $$('#pl-rows .pl-row').map((r) => {
      const o = { id: r.dataset.id };
      for (const i of $$('input[data-k]', r)) o[i.dataset.k] = i.type === 'checkbox' ? i.checked : i.value;
      return o;
    });
    try {
      const r = await api.publish.write({
        realm: $('pl-realm').value,
        manifestUrl: $('pl-url').value,
        validDays: $('pl-days').value,
        rulesUrl: $('pl-rules').value,
        outDir: $('pl-out').value,
        embed: !$('pl-embed-row').hidden && $('pl-embed').checked,
        servers
      });
      const list = $('pl-result-list');
      list.replaceChildren();
      const add = (k, v) => {
        const div = el('div');
        div.append(el('dt', null, k), el('dd', 'mono', v));
        list.appendChild(div);
      };
      add('List', r.files.servers);
      add('Player config', r.files.playerConfig);
      add('Version', `${r.seq} · ${plural(r.servers, 'server')} · key ${r.keyId}`);
      add('Expires', when(r.expires));
      if (r.embedded) add('Player build', r.embedded);
      $('pl-result').hidden = false;
      toast('servers.json signed and written. Nothing was uploaded.', 'ok');
    } catch (e) {
      fail(e);
    }
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
    if (!list.length && ART) box.appendChild(ART.emptyState({ art: 'decree', title: 'No news', body: 'Home shows the three newest items. Press Add to write one.' }));
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
  const wiz = { id: 's1', states: {}, running: false, failedAt: -1, phase: 'intro', lastError: null, status: null };

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
    $('wizard').dataset.phase = error ? 'error' : wiz.phase;
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
    wiz.status = await api.setup.status(wiz.id);
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
    $('wiz-root').disabled = wiz.id !== 's1';
    $('wiz-root-browse').disabled = wiz.id !== 's1';
    $('wiz-root-msg').textContent = st.rootOk ? (wiz.id === 's1' ? 'Realm only changes this folder. Not on C:, not inside Steam.' : 'Chosen when this server was added. Remove and add it again to use another folder.') : st.rootReason;
    $('wiz-root-msg').className = st.rootOk ? '' : 'bad';
    const remaining = STEPS.filter((s) => !st.steps[s.id].done).length;
    const extra = wiz.id !== 's1';
    wizShow({
      kicker: extra ? `Server ${wiz.id.slice(1)}` : 'Welcome',
      title: st.allDone ? (extra ? `${st.instanceName} is already standing` : 'Your Realm is already standing') : extra ? `Let’s raise Server ${wiz.id.slice(1)}` : 'Let’s raise your Realm',
      lead: st.allDone
        ? 'Every step is done. You can run the setup again at any time; finished steps are skipped.'
        : `Realm will make a private <b>${extra ? 'second copy' : 'test copy'}</b> of your dedicated server, add the Oxide mod framework and the Realm plugins. ${plural(remaining, 'step')} to go; finished steps are skipped. You will not need a command window.`,
      folder: true,
      go: st.allDone ? 'Finish' : 'Begin'
    });
    wizRenderSteps(-1);
  }

  function wizDone() {
    wiz.phase = 'done';
    const f = fleetList.find((x) => x.id === wiz.id);
    const port = f ? f.ports.game : 7350;
    wizShow({ kicker: 'All done', title: wiz.id === 's1' ? 'Your Realm stands ready' : `Server ${wiz.id.slice(1)} stands ready`, lead: `Start the server on the <b>Servers</b> screen. When its console shows it has loaded, press <b>Play</b> and direct connect to <b>127.0.0.1</b> port <b>${port}</b>.`, go: 'Enter the Realm' });
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
          await api.setup.run(s.id, wiz.id);
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

  async function openWizard(id) {
    wiz.id = typeof id === 'string' ? id : 's1';
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
        await api.setup.finish(wiz.id);
      } catch (e) {
        return fail(e);
      }
      const target = wiz.id;
      closeWizard();
      go('server');
      selectInstance(target);
      return;
    }
    if (wiz.phase === 'intro') {
      const root = $('wiz-root').value.trim();
      if (wiz.id === 's1' && root !== wiz.status.testRoot) {
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
  $('wizard').addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape' && !$('wiz-close').hidden && $('modal').hidden) closeWizard();
  });
  $('open-setup').addEventListener('click', () => openWizard(current === 'server' ? selId : 's1'));
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

  // ================================================================ connection doctor (renderer/doctor.js)

  views.doctor = { show: () => window.RealmDoctor && window.RealmDoctor.show() };

  // ================================================================ the court (renderer/court.js)

  views.court = { show: () => window.RealmCourt && window.RealmCourt.show() };

  // ================================================================ push events

  api.onPush((msg) => {
    if (msg.type === 'server-lines') {
      if ((msg.id || 's1') === selId) appendLines(msg.lines || []);
    } else if (msg.type === 'server-status') renderServer({ ...msg.status, id: msg.id || 's1' });
    else if (msg.type === 'fleet') {
      fleetList = Array.isArray(msg.list) ? msg.list : fleetList;
      renderFleet();
      if (current === 'publish' && !publishData) views.publish.show();
    }
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

  // Rail: Up and Down move between screens, Home and End jump to the ends.
  document.querySelector('.rail').addEventListener('keydown', (ev) => {
    const btns = $$('.rail .rail-btn');
    const i = btns.indexOf(document.activeElement);
    if (i < 0) return;
    const to = ev.key === 'ArrowDown' ? btns[(i + 1) % btns.length] : ev.key === 'ArrowUp' ? btns[(i - 1 + btns.length) % btns.length] : ev.key === 'Home' ? btns[0] : ev.key === 'End' ? btns[btns.length - 1] : null;
    if (!to) return;
    ev.preventDefault();
    to.focus();
  });

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
      else refreshFleet();
    }, 10000);
    try {
      const st = await api.setup.status('s1');
      if (!st.setupComplete) openWizard();
    } catch (e) {
      fail(e);
    }
  }

  start();
})();
