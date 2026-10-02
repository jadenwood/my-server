'use strict';

// Realm Steward: the at-a-glance owner strip on Home. Instances up/down, players per instance, last
// crash, last backup and next restart, for every server on this PC. Read-only: it only calls
// realm.fleet.list() and realm.server.listBackups(), and clicking an instance opens the Server screen.
// Kept in its own file so app.js (edited by several teams) only needs one script tag and one container.
(function () {
  const api = window.realm;
  const box = document.getElementById('dash-strip');
  if (!api || !api.fleet || !box) return;

  const ROMAN = { s1: 'I', s2: 'II', s3: 'III', s4: 'IV' };
  let fleet = [];
  let backups = {}; // id -> ISO time of the newest backup zip (manual or automatic)
  let backupsAt = 0;

  function el(tag, cls, text) {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = String(text);
    return n;
  }

  function ago(iso) {
    const ms = Date.now() - Date.parse(iso);
    if (!Number.isFinite(ms)) return null;
    const m = Math.round(Math.max(0, ms) / 60000);
    if (m < 1) return 'just now';
    if (m < 60) return m + ' min ago';
    const h = Math.round(m / 60);
    if (h < 48) return h + ' h ago';
    return Math.round(h / 24) + ' days ago';
  }

  function until(iso) {
    const ms = Date.parse(iso) - Date.now();
    if (!Number.isFinite(ms)) return null;
    if (ms <= 60000) return 'now';
    const m = Math.round(ms / 60000);
    if (m < 60) return 'in ' + m + ' min';
    const h = Math.floor(m / 60);
    return h < 24 ? `in ${h} h ${m % 60} m` : new Date(Date.parse(iso)).toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' });
  }

  function stateOf(f) {
    if (!f.copied) return ['', 'Not set up'];
    if (f.supervisor && f.supervisor.halted) return ['bad', 'Halted'];
    if (f.state === 'running' && f.ready) return ['ok', 'Up'];
    if (f.state === 'running' || f.state === 'starting') return ['warn', 'Loading'];
    if (f.state === 'stopping') return ['warn', 'Stopping'];
    if (f.supervisor && f.supervisor.nextRestartAt) return ['warn', 'Restarting'];
    return ['bad', 'Down'];
  }

  // The soonest planned restart: a crash restart (nextRestartAt) or the daily restart (plannedAt).
  function nextRestart(f) {
    const s = f.supervisor || {};
    const times = [s.nextRestartAt, s.plannedAt].filter((t) => t && Number.isFinite(Date.parse(t)));
    times.sort((a, b) => Date.parse(a) - Date.parse(b));
    return times[0] || null;
  }

  function newest(times) {
    return times.filter((t) => t && Number.isFinite(Date.parse(t))).sort((a, b) => Date.parse(b) - Date.parse(a))[0] || null;
  }

  function tile(label, value, sub, kind) {
    const t = el('div', 'dash-tile' + (kind ? ' ' + kind : ''));
    t.append(el('span', 'dash-label', label), el('span', 'dash-value', value));
    if (sub) t.appendChild(el('span', 'dash-sub', sub));
    return t;
  }

  function render() {
    box.replaceChildren();
    if (!fleet.length) {
      box.hidden = true;
      return;
    }
    box.hidden = false;
    const up = fleet.filter((f) => f.state === 'running' && f.ready).length;
    const players = fleet.reduce((n, f) => n + (f.state === 'running' && Number.isInteger(f.players) ? f.players : 0), 0);
    const lastCrash = newest(fleet.map((f) => f.supervisor && f.supervisor.lastCrashAt));
    const crashes24 = fleet.reduce((n, f) => n + ((f.supervisor && f.supervisor.crashes24h) || 0), 0);
    const lastBackup = newest(fleet.flatMap((f) => [backups[f.id], f.supervisor && f.supervisor.lastBackup && f.supervisor.lastBackup.at]));
    const next = fleet.map(nextRestart).filter(Boolean).sort((a, b) => Date.parse(a) - Date.parse(b))[0] || null;
    const daily = fleet.find((f) => f.dailyRestart && f.dailyRestart.enabled);
    const halted = fleet.filter((f) => f.supervisor && f.supervisor.halted);

    const tiles = el('div', 'dash-tiles');
    tiles.append(
      tile('Servers up', `${up} / ${fleet.length}`, halted.length ? `${halted.length} halted by the crash breaker` : up === fleet.length ? 'All running' : up ? 'Some are down' : 'None running', halted.length ? 'bad' : up === fleet.length ? 'ok' : up ? 'warn' : 'bad'),
      tile('Players online', String(players), up ? 'On running servers' : 'No server running', ''),
      tile('Last crash', lastCrash ? ago(lastCrash) : 'None seen', lastCrash ? `${crashes24} in the last 24 h` : 'Since Steward opened', lastCrash ? 'warn' : 'ok'),
      tile('Last backup', lastBackup ? ago(lastBackup) : 'None yet', lastBackup ? new Date(Date.parse(lastBackup)).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) : 'Back up on the Server screen', lastBackup ? '' : 'warn'),
      tile('Next restart', next ? until(next) : daily ? `Daily ${daily.dailyRestart.time}` : 'Not scheduled', next ? new Date(Date.parse(next)).toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' }) : daily ? 'Planned when the server starts' : 'Set a daily restart per server', '')
    );

    const list = el('div', 'dash-instances');
    list.setAttribute('role', 'list');
    for (const f of fleet) {
      const [kind, label] = stateOf(f);
      const b = el('button', 'dash-inst');
      b.type = 'button';
      b.setAttribute('role', 'listitem');
      b.dataset.go = 'server';
      b.dataset.dashInst = f.id;
      const players = f.state === 'running' && f.ready ? `${f.players}/${f.maxPlayers || '?'}` : '-';
      b.setAttribute('aria-label', `Server ${ROMAN[f.id] || f.id}, ${f.name}: ${label}, players ${players}. Open the Server screen.`);
      const meta = el('span', 'dash-meta');
      meta.append(el('span', 'pill ' + kind, label), el('span', 'dash-players', players === '-' ? '' : players + ' players'));
      b.title = f.name;
      b.append(el('span', 'dash-num', ROMAN[f.id] || f.id), el('span', 'dash-name', f.name), meta);
      list.appendChild(b);
    }
    const head = el('h2', 'section-h dash-title');
    head.appendChild(el('span', null, 'Your realm at a glance'));
    box.append(head, tiles, list);
  }

  async function refreshBackups(force) {
    if (!force && Date.now() - backupsAt < 60000) return;
    backupsAt = Date.now();
    const out = {};
    for (const f of fleet) {
      if (!f.copied || !api.server || !api.server.listBackups) continue;
      try {
        const r = await api.server.listBackups(f.id);
        const t = newest(((r && r.list) || []).map((b) => b.mtime));
        if (t) out[f.id] = t;
      } catch {
        /* folder not set up yet */
      }
    }
    backups = out;
  }

  async function refresh(forceBackups) {
    try {
      fleet = (await api.fleet.list()) || [];
    } catch {
      return;
    }
    await refreshBackups(forceBackups);
    render();
  }

  box.addEventListener('click', (ev) => {
    // Pick the instance first; app.js then opens the Server screen through the generic data-go handler.
    const b = ev.target.closest('[data-dash-inst]');
    if (!b) return;
    const card = document.querySelector(`#fleet-strip .fleet-card[data-inst="${b.dataset.dashInst}"]`);
    if (card) setTimeout(() => card.click(), 0);
  });

  api.onPush((msg) => {
    if (msg && msg.type === 'fleet' && Array.isArray(msg.list)) {
      fleet = msg.list;
      render();
    }
  });

  refresh(true);
  setInterval(() => {
    if (document.body.dataset.view === 'home') refresh(false);
  }, 10000);
  // Countdowns ("in 12 min") stay current without another fetch.
  setInterval(() => {
    if (document.body.dataset.view === 'home' && fleet.length) render();
  }, 30000);
})();
