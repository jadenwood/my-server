'use strict';

// The Sentinel (Realm Steward): RealmSentinel's live alerts, suspects and evidence, with actions.
// Builds its DOM inside #sentinel-root and talks only to window.realm.sentinel / fleet / court.
// Main process: lib/sentinel-feed.js. Plugin: plugins/RealmSentinel.cs, guide plugins/docs/RealmSentinel.md.
(function () {
  const api = window.realm;
  if (!api || !api.sentinel) return;
  const SN = api.sentinel;

  const POLL_MS = 3000;
  const KIND = {
    speed: 'Speed', teleport: 'Teleport', fly: 'Flying', reach: 'Reach', damage: 'Damage', fire_rate: 'Fire rate', kill_rate: 'Kill rate',
    item_jump: 'Item jump', gather_rate: 'Gathering', craft_rate: 'Crafting', chat_flood: 'Chat flood', command_flood: 'Command flood',
    reconnect_cycle: 'Reconnects', impersonation: 'Staff name', impersonation_near: 'Near staff name', admin_action: 'Admin action', response: 'Response'
  };
  const kindLabel = (k) => KIND[k] || String(k || '').replace(/_/g, ' ');

  let root = null;
  let built = false;
  let visible = false;
  let sel = 's1';
  let status = null;
  let selected = null; // { playerId, playerName }
  let evidence = null;
  let timer = null;
  const ui = {};

  // ---------------------------------------------------------------- helpers

  function el(tag, cls, text) {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = String(text);
    return n;
  }
  function icon(id, cls) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', cls || 'ico');
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', '#' + id);
    svg.appendChild(use);
    return svg;
  }
  function btn(label, iconId, cls, title) {
    const b = el('button', 'btn ' + (cls || ''));
    b.type = 'button';
    if (iconId) b.appendChild(icon(iconId));
    b.appendChild(el('span', null, label));
    if (title) b.title = title;
    return b;
  }
  function card(title, extraHead) {
    const c = el('article', 'card sn-card');
    const h = el('header', 'card-head');
    h.appendChild(el('h2', 'card-title', title));
    if (extraHead) h.appendChild(extraHead);
    c.appendChild(h);
    return c;
  }
  function toast(msg, kind) {
    const box = document.getElementById('toasts');
    if (!box) return;
    const t = el('div', 'toast' + (kind ? ' ' + kind : ''), msg);
    t.setAttribute('role', kind === 'bad' ? 'alert' : 'status');
    box.appendChild(t);
    setTimeout(() => t.remove(), kind === 'bad' ? 9000 : 6000);
  }
  function fail(e) {
    toast(e && e.message ? e.message : String(e), 'bad');
  }
  function busy(button, fn) {
    return async (...a) => {
      if (button.disabled) return;
      button.disabled = true;
      try {
        await fn(...a);
      } catch (e) {
        fail(e);
      } finally {
        button.disabled = false;
      }
    };
  }
  function ago(t) {
    const d = Date.parse(t);
    if (!Number.isFinite(d)) return '';
    const s = Math.max(0, Math.round((Date.now() - d) / 1000));
    if (s < 60) return `${s} s ago`;
    if (s < 3600) return `${Math.round(s / 60)} min ago`;
    if (s < 86400) return `${Math.round(s / 3600)} h ago`;
    return new Date(d).toLocaleDateString([], { month: 'short', day: 'numeric' });
  }
  function clock(t) {
    const d = new Date(t);
    return isNaN(d) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }
  function confirmAsk({ title, text, ok, days }) {
    // A small modal of our own (the app's confirm box lives inside app.js).
    return new Promise((resolve) => {
      const scrim = el('div', 'sn-modal-scrim');
      const box = el('div', 'sn-modal card');
      box.setAttribute('role', 'dialog');
      box.setAttribute('aria-modal', 'true');
      box.setAttribute('aria-label', title);
      box.appendChild(el('h2', 'card-title', title));
      box.appendChild(el('p', 'sn-modal-text', text));
      let sel2 = null;
      if (days) {
        const lab = el('label', 'field');
        lab.appendChild(el('span', null, 'How long'));
        sel2 = el('select');
        for (const [v, l] of [['1', '1 day'], ['7', '7 days'], ['30', '30 days'], ['0', 'Forever']]) {
          const o = el('option', null, l);
          o.value = v;
          sel2.appendChild(o);
        }
        sel2.value = '7';
        lab.appendChild(sel2);
        box.appendChild(lab);
      }
      const row = el('div', 'row gap sn-modal-acts');
      const yes = btn(ok, null, 'primary danger');
      const no = btn('Cancel', null, 'ghost');
      row.append(no, yes);
      box.appendChild(row);
      scrim.appendChild(box);
      document.body.appendChild(scrim);
      const prev = document.activeElement;
      const done = (v) => {
        scrim.remove();
        document.removeEventListener('keydown', onKey, true);
        if (prev && prev.focus) prev.focus();
        resolve(v);
      };
      const onKey = (ev) => {
        if (ev.key === 'Escape') {
          ev.preventDefault();
          done(null);
        } else if (ev.key === 'Tab') {
          const f = [...box.querySelectorAll('button, select')];
          const i = f.indexOf(document.activeElement);
          ev.preventDefault();
          f[(i + (ev.shiftKey ? f.length - 1 : 1)) % f.length].focus();
        }
      };
      document.addEventListener('keydown', onKey, true);
      yes.addEventListener('click', () => done({ days: sel2 ? Number(sel2.value) : 0 }));
      no.addEventListener('click', () => done(null));
      scrim.addEventListener('click', (ev) => ev.target === scrim && done(null));
      no.focus();
    });
  }

  // ---------------------------------------------------------------- build

  function build() {
    root = document.getElementById('sentinel-root');
    if (!root || built) return;
    built = true;

    const head = el('header', 'view-head');
    const titles = el('div');
    titles.append(el('p', 'eyebrow', 'The watch on the walls'), el('h1', 'view-title', 'The Sentinel'));
    head.appendChild(titles);
    const right = el('div', 'sn-headright');
    ui.servers = el('div', 'court-servers');
    ui.servers.setAttribute('role', 'tablist');
    ui.servers.setAttribute('aria-label', 'Server');
    ui.mode = el('span', 'pill big', 'Checking');
    ui.updated = el('span', 'fine sn-updated');
    ui.reload = btn('Reload plugin', 'i-restart', 'ghost small', 'oxide.reload RealmSentinel over the admin console (re-reads its config)');
    ui.reload.addEventListener('click', busy(ui.reload, async () => {
      const r = await SN.act(sel, 'reload', {});
      toast(r.ok ? 'RealmSentinel reloaded.' : `Reload failed: ${r.result || 'no answer'}`, r.ok ? 'ok' : 'bad');
    }));
    right.append(ui.servers, ui.mode, ui.updated, ui.reload);
    head.appendChild(right);
    root.appendChild(head);

    ui.banner = el('div', 'sn-banner');
    ui.banner.hidden = true;
    root.appendChild(ui.banner);

    ui.stats = el('div', 'sn-stats');
    root.appendChild(ui.stats);

    const grid = el('div', 'sn-grid');
    root.appendChild(grid);

    const markAll = btn('Mark all seen', 'i-check', 'ghost small');
    markAll.addEventListener('click', busy(markAll, markSeen));
    const ac = card('Live alerts', markAll);
    ui.alerts = el('div', 'sn-alerts');
    ui.alerts.setAttribute('role', 'log');
    ui.alerts.setAttribute('aria-live', 'polite');
    ac.appendChild(ui.alerts);
    grid.appendChild(ac);

    const sc = card('Suspects');
    ui.suspectsNote = el('p', 'fine sn-note');
    ui.suspects = el('div', 'sn-suspects');
    ui.suspects.setAttribute('role', 'list');
    sc.append(ui.suspectsNote, ui.suspects);
    grid.appendChild(sc);

    const ec = card('Evidence');
    ui.evHead = el('div', 'sn-evhead');
    ui.evActs = el('div', 'sn-evacts');
    ui.ev = el('div', 'sn-evidence');
    ec.append(ui.evHead, ui.evActs, ui.ev);
    grid.appendChild(ec);
  }

  // ---------------------------------------------------------------- render

  function renderServers(list) {
    ui.servers.replaceChildren();
    for (const s of list) {
      const b = el('button', 'court-srv' + (s.id === sel ? ' active' : '') + (s.state === 'running' ? ' up' : ''));
      b.type = 'button';
      b.setAttribute('role', 'tab');
      b.setAttribute('aria-selected', s.id === sel ? 'true' : 'false');
      b.append(el('b', null, s.id.toUpperCase()), document.createTextNode(' ' + (s.name || '').slice(0, 18)));
      b.addEventListener('click', () => {
        sel = s.id;
        selected = null;
        evidence = null;
        refresh();
      });
      ui.servers.appendChild(b);
    }
  }

  function banner(kind, text, action) {
    ui.banner.replaceChildren();
    ui.banner.hidden = !text;
    if (!text) return;
    ui.banner.className = 'sn-banner ' + (kind || '');
    ui.banner.append(icon('i-alert', 'sn-banner-ico'), el('span', null, text));
    if (action) {
      const b = btn(action.label, null, 'small');
      b.addEventListener('click', action.onClick);
      ui.banner.appendChild(b);
    }
  }

  function stat(label, value, cls) {
    const s = el('div', 'sn-stat' + (cls ? ' ' + cls : ''));
    s.append(el('b', null, value), el('span', null, label));
    return s;
  }

  function render() {
    const st = status;
    const f = st && st.feed;
    ui.mode.className = 'pill big ' + (!f ? 'bad' : f.mode === 'enforce' ? 'warn' : 'ok');
    ui.mode.textContent = !f ? 'No feed' : f.mode === 'enforce' ? 'Enforce mode' : 'Watch mode';
    ui.updated.textContent = f && f.generated ? `Feed written ${ago(f.generated)}` : '';
    ui.reload.disabled = !st || !st.console || !st.installed;
    ui.reload.title = st && st.console ? 'oxide.reload RealmSentinel over the admin console (re-reads its config)' : 'Needs the live admin console: start the server from Steward with the live console on.';

    if (!st || st.state === 'no-server') banner('warn', 'No server copy is set up yet. Run Setup first.');
    else if (!st.installed) banner('warn', 'RealmSentinel is not on this server yet. Use Update plugins on the Servers screen; it starts in watch mode.', { label: 'Servers', onClick: () => document.querySelector('.rail-btn[data-go="server"]').click() });
    else if (st.state === 'missing') banner('', 'No feed yet. RealmSentinel writes oxide\\data\\RealmSentinelFeed.json once the server runs with the plugin loaded (at most every 5 s when something changes).');
    else if (st.state === 'damaged') banner('bad', `The feed could not be read: ${st.reason}. The last good view stays until the plugin writes it again.`);
    else if (f.dataDamaged) banner('bad', 'RealmSentinel could not read its own data file (RealmSentinel.json), so it is not saving anything. Look at the server log, fix or move the file away, then reload the plugin.');
    else if (!st.console) banner('', 'Kick and Ban go through the live admin console, which is not connected to this server now. The /sentinel commands can always be copied and typed in game.');
    else banner(null, null);

    const th = (st && st.thresholds) || { alert: 20, freeze: 50, kick: 80, ban: 150 };
    ui.stats.replaceChildren(
      stat('Online', f ? f.online : '-'),
      stat('Frozen', f ? f.frozen : '-', f && f.frozen ? 'warn' : ''),
      stat('Alerts kept', f ? f.alerts.length : '-'),
      stat('Unseen', st && st.unseen != null ? st.unseen : '-', st && st.unseen ? 'hot' : ''),
      stat('Scores', `${th.alert} alert · ${th.freeze} freeze · ${th.kick} kick · ${th.ban} ban`, 'wide')
    );

    renderAlerts();
    renderSuspects();
    renderEvidence();
  }

  function respClass(a) {
    return ['note', 'would', 'acted', 'ban'][a.severity] || 'note';
  }

  function renderAlerts() {
    ui.alerts.replaceChildren();
    const f = status && status.feed;
    if (!f || !f.alerts.length) {
      ui.alerts.appendChild(window.RealmArt ? Object.assign(window.RealmArt.emptyState({ art: 'sheathed', title: 'All quiet on the walls', body: f ? 'No alerts yet. Honest players stay well under the alert score.' : 'Alerts appear here as soon as the feed exists.' }), {}) : el('p', 'fine', 'No alerts.'));
      return;
    }
    for (const a of f.alerts.slice(0, 100)) {
      const row = el('button', `sn-alert ${respClass(a)}` + (a.id > status.seenId ? ' unseen' : '') + (selected && selected.playerId === a.playerId ? ' active' : ''));
      row.type = 'button';
      const top = el('span', 'sn-alert-top');
      top.append(el('b', null, a.playerName || a.playerId || '?'), el('span', 'sn-kind', kindLabel(a.kind)), el('span', 'sn-score', a.score.toFixed(1)));
      const mid = el('span', 'sn-alert-detail', a.detail);
      const bot = el('span', 'sn-alert-meta');
      bot.append(el('span', 'sn-resp', a.response || 'alert'), el('span', null, `${clock(a.time)} · #${a.id}`));
      row.append(top, mid, bot);
      row.addEventListener('click', () => select(a.playerId, a.playerName));
      ui.alerts.appendChild(row);
    }
  }

  function renderSuspects() {
    ui.suspects.replaceChildren();
    const f = status && status.feed;
    const th = (status && status.thresholds) || { alert: 20, freeze: 50, kick: 80, ban: 150 };
    ui.suspectsNote.textContent = f ? `${f.suspects.length} player${f.suspects.length === 1 ? '' : 's'} with a score or a freeze. Scores fade by themselves.` : '';
    if (!f || !f.suspects.length) {
      ui.suspects.appendChild(el('p', 'fine sn-empty', 'No one is under watch.'));
      return;
    }
    for (const s of f.suspects) {
      const row = el('button', 'sn-suspect' + (selected && selected.playerId === s.playerId ? ' active' : ''));
      row.type = 'button';
      row.setAttribute('role', 'listitem');
      const top = el('span', 'sn-sus-top');
      const name = el('b', null, s.playerName || s.playerId);
      const chips = el('span', 'sn-chips');
      chips.appendChild(el('span', 'sn-chip ' + (s.online ? 'on' : 'off'), s.online ? 'online' : 'offline'));
      if (s.frozen) chips.appendChild(el('span', 'sn-chip frozen', 'frozen'));
      top.append(name, chips);
      const bar = el('span', 'sn-bar');
      bar.setAttribute('role', 'meter');
      bar.setAttribute('aria-label', `Score ${s.score} of ban score ${th.ban}`);
      bar.setAttribute('aria-valuemin', '0');
      bar.setAttribute('aria-valuemax', String(th.ban));
      bar.setAttribute('aria-valuenow', String(s.score));
      const fill = el('i', s.score >= th.kick ? 'kick' : s.score >= th.freeze ? 'freeze' : s.score >= th.alert ? 'alert' : '');
      fill.style.width = Math.min(100, (s.score / th.ban) * 100) + '%';
      bar.appendChild(fill);
      for (const [k, cls] of [['alert', 'a'], ['freeze', 'f'], ['kick', 'k']]) {
        const tick = el('u', cls);
        tick.style.left = Math.min(100, (th[k] / th.ban) * 100) + '%';
        bar.appendChild(tick);
      }
      const meta = el('span', 'sn-sus-meta', `score ${s.score.toFixed(1)} · peak ${s.peakScore.toFixed(1)} · ${Object.entries(s.counts).filter(([, n]) => n > 0).map(([k, n]) => `${kindLabel(k)} ${n}`).join(', ') || 'no findings'} · seen ${ago(s.lastSeen)}`);
      row.append(top, bar, meta);
      row.addEventListener('click', () => select(s.playerId, s.playerName));
      ui.suspects.appendChild(row);
    }
  }

  function suspectOf(id) {
    return status && status.feed ? status.feed.suspects.find((s) => s.playerId === id) : null;
  }

  function renderEvidence() {
    ui.evHead.replaceChildren();
    ui.evActs.replaceChildren();
    ui.ev.replaceChildren();
    if (!selected) {
      ui.ev.appendChild(el('p', 'fine sn-empty', 'Choose an alert or a suspect to read their evidence lines and act.'));
      return;
    }
    const s = suspectOf(selected.playerId);
    const name = selected.playerName || selected.playerId;
    const who = el('div', 'sn-who');
    who.append(el('b', null, name), el('span', 'mono fine', selected.playerId));
    ui.evHead.appendChild(who);
    if (s) ui.evHead.appendChild(el('span', 'fine', `score ${s.score.toFixed(1)} · peak ${s.peakScore.toFixed(1)}${s.frozen ? ' · frozen' : ''}${s.online ? '' : ' · offline'}`));

    const con = el('div', 'row gap sn-actrow');
    const kick = btn('Kick', 'i-x', 'small', 'The game\'s /kick over the admin console');
    const ban = btn('Ban', 'i-shield', 'small danger', 'The game\'s /ban over the admin console');
    const lastAlert = status.feed ? status.feed.alerts.find((a) => a.playerId === selected.playerId) : null;
    const kind = lastAlert ? lastAlert.kind : 'review';
    for (const b of [kick, ban]) b.disabled = !status.console;
    kick.addEventListener('click', busy(kick, async () => {
      const ok = await confirmAsk({ title: `Kick ${name}?`, text: `The game's own /kick, with the reason "Sentinel: ${kind}". They can rejoin; a Sentinel freeze still applies on return.`, ok: 'Kick' });
      if (!ok) return;
      const r = await SN.act(sel, 'kick', { name, kind });
      toast(r.ok ? `${name} was kicked. Written to the Court rolls.` : `Kick failed: ${r.result || 'no answer'}`, r.ok ? 'ok' : 'bad');
    }));
    ban.addEventListener('click', busy(ban, async () => {
      const ok = await confirmAsk({ title: `Ban ${name}?`, text: `The game's own /ban, with the reason "Sentinel: ${kind}". The evidence stays in the Sentinel's log for an appeal (docs/community/ops/ban-appeals.md). Unban from the Court.`, ok: 'Ban', days: true });
      if (!ok) return;
      const r = await SN.act(sel, 'ban', { name, kind, days: ok.days });
      toast(r.ok ? `${name} was banned (${r.summary}). Written to the Court rolls.` : `Ban failed: ${r.result || 'no answer'}`, r.ok ? 'ok' : 'bad');
    }));
    con.append(el('span', 'sn-actlabel', 'Console'), kick, ban);
    ui.evActs.appendChild(con);

    const copyRow = el('div', 'row gap sn-actrow');
    copyRow.appendChild(el('span', 'sn-actlabel', 'In game'));
    const cmds = (s && s.commands) || [];
    for (const c of cmds) {
      const b = btn(c.label, 'i-copy', 'small ghost', `Copy ${c.command}`);
      b.addEventListener('click', busy(b, async () => {
        const text = await SN.copy(sel, name, c.id);
        toast(`Copied: ${text}  Paste it in game chat (needs realmsentinel.admin).`, 'ok');
      }));
      copyRow.appendChild(b);
    }
    if (!cmds.length) copyRow.appendChild(el('span', 'fine', 'Not a current suspect: use /sentinel report in game.'));
    ui.evActs.appendChild(copyRow);

    if (!evidence) {
      ui.ev.appendChild(el('p', 'fine', 'Reading the evidence log...'));
      return;
    }
    if (!evidence.lines.length) {
      ui.ev.appendChild(el('p', 'fine sn-empty', evidence.files ? 'No evidence lines for this player in the daily logs.' : 'No evidence logs yet (oxide\\logs\\RealmSentinel).'));
      return;
    }
    for (const e of evidence.lines) {
      const row = el('div', 'sn-ev' + (e.admin ? ' admin' : ''));
      const top = el('div', 'sn-ev-top');
      top.append(el('span', 'sn-kind', kindLabel(e.kind)), el('span', 'sn-ev-time', `${new Date(e.time).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' })} · #${e.id}`));
      row.appendChild(top);
      row.appendChild(el('div', 'sn-ev-detail', e.detail));
      if (!e.admin) row.appendChild(el('div', 'sn-ev-meta', `+${e.points} → ${e.scoreAfter}${e.pos ? ` · at ${e.pos}` : ''} · ping ${e.pingMs} ms`));
      ui.ev.appendChild(row);
    }
  }

  // ---------------------------------------------------------------- data

  async function select(playerId, playerName) {
    if (!playerId) return;
    selected = { playerId, playerName };
    evidence = null;
    renderAlerts();
    renderSuspects();
    renderEvidence();
    try {
      evidence = await SN.evidence(sel, playerId);
    } catch (e) {
      evidence = { files: 0, lines: [] };
      fail(e);
    }
    if (selected && selected.playerId === playerId) renderEvidence();
  }

  async function markSeen() {
    const f = status && status.feed;
    if (!f || !f.alerts.length) return;
    await SN.markSeen(sel, f.alerts[0].id);
    await refresh();
  }

  async function refresh() {
    if (!built) build();
    try {
      const list = await api.fleet.list();
      if (!list.some((s) => s.id === sel) && list[0]) sel = list[0].id;
      renderServers(list);
    } catch {
      /* the fleet list is only for the server tabs */
    }
    try {
      status = await SN.status(sel);
    } catch (e) {
      status = null;
      fail(e);
    }
    render();
  }

  function show() {
    build();
    visible = true;
    refresh();
    clearInterval(timer);
    timer = setInterval(() => {
      if (!visible || document.body.dataset.view !== 'sentinel') {
        visible = false;
        clearInterval(timer);
        return;
      }
      refresh();
    }, POLL_MS);
  }

  // Rail badge and a toast for each new alert, from the main process watch (lib/sentinel-feed.js).
  api.onPush((msg) => {
    if (msg.type !== 'sentinel') return;
    const badge = document.getElementById('rail-sentinel-badge');
    if (badge) {
      badge.hidden = !msg.total;
      badge.textContent = msg.total > 99 ? '99+' : String(msg.total || '');
      badge.setAttribute('aria-label', `${msg.total} unseen Sentinel alert${msg.total === 1 ? '' : 's'}`);
    }
    if (msg.fresh) toast(`Sentinel: ${msg.fresh.playerName} - ${kindLabel(msg.fresh.kind)}, score ${Number(msg.fresh.score).toFixed(1)} (${msg.fresh.response || 'alert'})`, msg.fresh.response && /ban|kick|frozen/.test(msg.fresh.response) ? 'bad' : '');
    if (visible && msg.id === sel) refresh();
  });

  window.RealmSentinel = { show, select: (id) => (sel = id) };
  // The small confirm dialog, shared with renderer/features.js.
  window.RealmConfirm = confirmAsk;
})();
