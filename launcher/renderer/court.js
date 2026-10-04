'use strict';

// The Court (Realm Steward): live moderation over the game's admin console.
// Builds its own DOM inside #court-root and talks only to window.realm.court / window.realm.fleet.
// See lib/court-host.js (main process) and docs/admin-console.md.
(function () {
  const api = window.realm;
  if (!api || !api.court) return;
  const C = api.court;

  const PLAYER_POLL_MS = 20000; // the game's logger drops a line repeated 50 times in 5 minutes
  const FEED_POLL_MS = 1500;
  const FEED_KEEP = 600;

  let root = null;
  let built = false;
  let visible = false;
  let sel = 's1';
  let status = null;
  let feedLast = 0;
  let players = [];
  let playersAt = null;
  let lastPlayerPoll = 0;
  let feedTimer = null;
  let filter = { chat: true, log: true, debug: false };
  let openRow = null; // { name, kind } of the inline action form
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
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', '#' + id);
    svg.appendChild(use);
    return svg;
  }
  // An empty list in the shared look (renderer/heraldry.js), compact for the Court's narrow cards.
  function emptyNote(art, title, body) {
    if (!window.RealmArt) return el('p', 'fine court-empty', body ? `${title}. ${body}` : `${title}.`);
    const n = window.RealmArt.emptyState({ art, title, body });
    n.classList.add('compact');
    return n;
  }
  function btn(label, iconId, cls, title) {
    const b = el('button', 'btn ' + (cls || ''));
    b.type = 'button';
    if (iconId) b.appendChild(icon(iconId));
    b.appendChild(el('span', null, label));
    if (title) b.title = title;
    return b;
  }
  function input(placeholder, max, cls) {
    const i = el('input', cls || '');
    i.placeholder = placeholder || '';
    if (max) i.maxLength = max;
    i.spellcheck = false;
    return i;
  }
  function card(title, extraHead) {
    const c = el('article', 'card court-card');
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
    box.appendChild(t);
    setTimeout(() => t.remove(), kind === 'bad' ? 9000 : 5000);
  }
  function fail(e) {
    toast(e && e.message ? e.message : String(e), 'bad');
  }
  function time(t) {
    const d = new Date(t);
    return isNaN(d) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }
  function when(t) {
    const d = new Date(t);
    return isNaN(d) ? '' : d.toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
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
  function connected() {
    return !!(status && status.console && status.console.state === 'connected');
  }

  // ---------------------------------------------------------------- build

  function build() {
    root = document.getElementById('court-root');
    if (!root || built) return;
    built = true;

    const head = el('header', 'view-head');
    const titles = el('div');
    titles.appendChild(el('p', 'eyebrow', 'Justice of the realm'));
    titles.appendChild(el('h1', 'view-title', 'The Court'));
    head.appendChild(titles);
    const right = el('div', 'court-headright');
    ui.servers = el('div', 'court-servers');
    ui.servers.setAttribute('role', 'tablist');
    ui.pill = el('span', 'pill big', 'Checking');
    ui.pillNote = el('span', 'fine mono court-port');
    right.append(ui.servers, ui.pill, ui.pillNote);
    head.appendChild(right);
    root.appendChild(head);

    ui.banner = el('div', 'court-banner');
    ui.banner.hidden = true;
    root.appendChild(ui.banner);

    const grid = el('div', 'court-grid');
    root.appendChild(grid);

    // ---- column 1: those present
    const col1 = el('div', 'court-col');
    const refresh = btn('Refresh', 'i-restart', 'ghost small');
    refresh.addEventListener('click', busy(refresh, () => loadPlayers(true)));
    const pc = card('Those present', refresh);
    ui.playersMeta = el('p', 'fine court-meta');
    ui.playerFilter = input('Find a player', 64, 'court-find');
    ui.playerFilter.addEventListener('input', renderPlayers);
    ui.players = el('div', 'court-players');
    pc.append(ui.playersMeta, ui.playerFilter, ui.players);
    col1.appendChild(pc);

    const bc = card('Banished', (() => {
      const b = btn('Read list', 'i-scroll', 'ghost small');
      b.addEventListener('click', busy(b, loadBans));
      return b;
    })());
    ui.bans = el('div', 'court-bans');
    ui.bans.appendChild(el('p', 'fine', 'Press Read list to fetch the ban list from the game.'));
    const unbanRow = el('div', 'row gap court-inline');
    ui.unbanInput = input('Name, Steam ID or ban number', 64, 'grow');
    const unbanBtn = btn('Unban', 'i-undo', 'small');
    unbanBtn.addEventListener('click', busy(unbanBtn, async () => {
      const name = ui.unbanInput.value.trim();
      if (!name) throw new Error('Type a name, Steam ID or ban number first.');
      await act('unban', { name });
      ui.unbanInput.value = '';
      await loadBans();
    }));
    unbanRow.append(ui.unbanInput, unbanBtn);
    bc.append(ui.bans, unbanRow);
    col1.appendChild(bc);
    grid.appendChild(col1);

    // ---- column 2: proclamations, gates, plugin commands
    const col2 = el('div', 'court-col');
    const proc = card('Proclamations');
    ui.message = el('textarea', 'court-message');
    ui.message.maxLength = 300;
    ui.message.rows = 3;
    ui.message.placeholder = 'Hear ye: the market opens at dusk.';
    const procRow = el('div', 'row gap court-actions');
    const bNotice = btn('Notice', 'i-banners', 'primary small', 'On-screen notice for everyone (/notice)');
    const bPopup = btn('Popup', 'i-scroll', 'small', 'A popup window for every player (/popup)');
    const bSay = btn('Say in chat', 'i-chain', 'small', 'Chat line from the server');
    for (const [b, action] of [[bNotice, 'notice'], [bPopup, 'popup'], [bSay, 'say']]) {
      b.addEventListener('click', busy(b, async () => {
        const message = ui.message.value.trim();
        if (!message) throw new Error('Write the message first.');
        const r = await act(action, { message });
        if (r.ok) ui.message.value = '';
      }));
    }
    procRow.append(bNotice, bPopup, bSay);
    proc.append(ui.message, procRow, el('p', 'fine', 'Quotes become ` because the game\'s command reader drops them. Letters outside plain English (é, ø) arrive as "?".'));
    col2.appendChild(proc);

    const gates = card('The gates');
    const gRow = el('div', 'row gap court-actions');
    const wOn = btn('Whitelist on', 'i-key', 'small', '/whitelist enable: only listed players may join');
    const wOff = btn('Whitelist off', 'i-globe', 'small ghost', '/whitelist disable');
    wOn.addEventListener('click', busy(wOn, () => act('whitelist', { on: true })));
    wOff.addEventListener('click', busy(wOff, () => act('whitelist', { on: false })));
    const save = btn('Save world', 'i-chest', 'small primary', 'Saves the world now (/realm.save from the RealmCourt plugin)');
    save.addEventListener('click', busy(save, () => act('save', {})));
    gRow.append(save, wOn, wOff);
    ui.gatesNote = el('p', 'fine');
    gates.append(gRow, ui.gatesNote);
    col2.appendChild(gates);

    const plug = card('Realm laws (in-game admin commands)');
    plug.appendChild(el('p', 'fine', 'These belong to the Realm plugins and check the permission of the player who types them, so they are typed in game by an admin. Fill in, Copy, paste into game chat.'));
    ui.plugins = el('div', 'court-plugins');
    plug.appendChild(ui.plugins);
    col2.appendChild(plug);
    grid.appendChild(col2);

    // ---- column 3: live hall + rolls
    const col3 = el('div', 'court-col court-col-wide');
    const filters = el('div', 'court-filters');
    for (const [k, label] of [['chat', 'Chat'], ['log', 'Log'], ['debug', 'Debug']]) {
      const lab = el('label', 'check');
      const cb = el('input');
      cb.type = 'checkbox';
      cb.checked = filter[k];
      cb.addEventListener('change', () => {
        filter[k] = cb.checked;
        ui.feed.dataset.chat = filter.chat ? '1' : '0';
        ui.feed.dataset.log = filter.log ? '1' : '0';
        ui.feed.dataset.debug = filter.debug ? '1' : '0';
      });
      lab.append(cb, el('span', null, label));
      filters.appendChild(lab);
    }
    const hall = card('The great hall', filters);
    ui.feed = el('div', 'court-feed console');
    ui.feed.setAttribute('role', 'log');
    ui.feed.dataset.chat = '1';
    ui.feed.dataset.log = '1';
    ui.feed.dataset.debug = '0';
    const form = el('form', 'console-input court-cmd');
    form.autocomplete = 'off';
    form.appendChild(el('span', 'prompt', '›'));
    ui.cmd = input('Console command, e.g. list   (say <text> to chat)', 200);
    const send = btn('Send', null, 'small');
    send.type = 'submit';
    form.append(ui.cmd, send);
    form.addEventListener('submit', (ev) => {
      ev.preventDefault();
      const text = ui.cmd.value.trim();
      if (!text) return;
      ui.cmd.value = '';
      api.server.command(sel, text).then(() => pollFeed(), fail);
    });
    hall.append(ui.feed, form);
    col3.appendChild(hall);

    const openLog = btn('Folder', 'i-folder', 'ghost small');
    openLog.addEventListener('click', () => C.openLog().catch(fail));
    const rolls = card('Court rolls', openLog);
    ui.rolls = el('div', 'court-rolls');
    rolls.appendChild(ui.rolls);
    col3.appendChild(rolls);

    const chan = card('Live console');
    ui.chanText = el('p', 'fine');
    const chRow = el('label', 'check');
    ui.chanToggle = el('input');
    ui.chanToggle.type = 'checkbox';
    ui.chanToggle.addEventListener('change', async () => {
      try {
        status = await C.setConsole(sel, ui.chanToggle.checked);
        renderStatus();
        toast(ui.chanToggle.checked ? 'Live console on. It applies the next time this server starts.' : 'Live console off from the next start.');
      } catch (e) {
        fail(e);
      }
    });
    chRow.append(ui.chanToggle, el('span', null, 'Hold the game\'s admin console for this server (starts ROK.exe with -cport)'));
    chan.append(chRow, ui.chanText);
    col3.appendChild(chan);
    grid.appendChild(col3);

    C.pluginCommands().then(renderPluginCommands, () => {});
  }

  // ---------------------------------------------------------------- render

  async function renderServers() {
    let list = [];
    try {
      list = await api.fleet.list();
    } catch {
      list = [{ id: 's1', name: 'Server 1' }];
    }
    if (Array.isArray(list && list.list)) list = list.list;
    if (!Array.isArray(list) || !list.length) list = [{ id: 's1' }];
    if (!list.some((i) => i.id === sel)) sel = list[0].id;
    ui.servers.replaceChildren();
    for (const inst of list) {
      const b = el('button', 'court-srv' + (inst.id === sel ? ' active' : ''));
      b.type = 'button';
      b.setAttribute('role', 'tab');
      b.appendChild(el('b', null, ['I', 'II', 'III', 'IV'][Number(inst.id.slice(1)) - 1] || inst.id));
      b.appendChild(el('span', null, inst.name || `Server ${inst.id.slice(1)}`));
      const running = inst.state === 'running' || inst.running || (inst.status && inst.status.state === 'running');
      if (running) b.classList.add('up');
      b.addEventListener('click', () => {
        if (sel === inst.id) return;
        sel = inst.id;
        feedLast = 0;
        players = [];
        playersAt = null;
        lastPlayerPoll = 0;
        openRow = null;
        ui.feed.replaceChildren();
        ui.bans.replaceChildren(el('p', 'fine', 'Press Read list to fetch the ban list from the game.'));
        renderServers();
        refreshAll();
      });
      ui.servers.appendChild(b);
    }
  }

  function renderStatus() {
    const s = status;
    const con = s && s.console;
    let cls = 'pill big';
    let text = 'Not running';
    let note = '';
    ui.banner.hidden = true;
    ui.banner.replaceChildren();
    if (s && s.running && con && con.state === 'connected') {
      cls += con.stale ? ' warn' : con.degraded ? ' warn' : ' ok';
      text = con.stale ? 'Court in session (no keep-alive)' : con.degraded ? 'Console degraded' : 'Court in session';
      note = `127.0.0.1:${con.port}`;
    } else if (s && s.running && con && con.state === 'connecting') {
      cls += ' warn';
      text = 'Opening the court';
      note = `127.0.0.1:${con.port} - attempt ${con.attempts}`;
    } else if (s && s.running) {
      cls += ' bad';
      text = 'No live console';
      showBanner(
        s.exe !== 'ROK'
          ? 'This server runs through Server.exe, which keeps the game\'s console for itself. Choose ROK.exe in Settings (Server program) and restart the server to hold court here.'
          : s.cportOff
            ? `The live console is off for this session: ${s.cportOff}.`
            : !s.enabled
              ? 'The live console is turned off for this server (below). Turn it on and restart the server.'
              : 'This server was started before the live console was available. Restart it to hold court here.'
      );
    } else {
      showBanner('Start this server on the Servers screen. With ROK.exe, Steward opens the game\'s admin console the moment it starts.');
    }
    ui.pill.className = cls;
    ui.pill.textContent = text;
    ui.pillNote.textContent = note;
    ui.chanToggle.checked = !!(s && s.enabled);
    const port = s ? s.plannedCport : 11000;
    ui.chanText.textContent =
      `Port ${port}, on this PC only (127.0.0.1); Go Public blocks 11000-11003 in Windows Firewall. ` +
      'The game saves and shuts down when this connection closes, so Steward keeps it open while the server runs and closing Steward stops the server cleanly. ' +
      'Stop sends /shutdown over it. Changes apply at the next start.';
    const live = connected();
    for (const b of root.querySelectorAll('.court-needs-live, .court-actions .btn, .court-inline .btn, .court-cmd .btn')) b.disabled = !live;
    ui.gatesNote.textContent = s && s.plugin === false
      ? 'Save world needs the RealmCourt plugin on this server: use Update plugins on the Servers screen.'
      : 'Save world runs the game\'s own save (the plugin RealmCourt adds the command). The whitelist itself is edited in the server\'s Configuration folder.';
    if (s && s.whitelist != null) ui.gatesNote.textContent = `Whitelist last set ${s.whitelist ? 'ON' : 'OFF'} from the Court. ` + ui.gatesNote.textContent;
  }

  function showBanner(text) {
    ui.banner.hidden = false;
    ui.banner.replaceChildren(icon('i-alert', 'court-banner-ico'), el('span', null, text));
  }

  function renderPlayers() {
    ui.players.replaceChildren();
    const q = ui.playerFilter.value.trim().toLowerCase();
    ui.playersMeta.textContent = playersAt
      ? `${players.length} present - read ${time(playersAt)}${players.some((p) => p.id) ? '' : ' (names only: the RealmCourt plugin adds Steam IDs)'}`
      : connected()
        ? 'Reading the hall...'
        : 'Nobody can be seen without the live console.';
    const list = players.filter((p) => !q || p.name.toLowerCase().includes(q));
    if (playersAt && !list.length) ui.players.appendChild(emptyNote(q ? 'accuse' : 'house', q ? 'No one by that name' : 'The hall is empty', q ? 'Check the spelling, or clear the search.' : 'Players appear here as they join.'));
    for (const p of list) {
      const row = el('div', 'court-player');
      const who = el('div', 'court-who');
      who.appendChild(el('b', null, p.name));
      if (p.id) who.appendChild(el('span', 'fine mono', p.id));
      const acts = el('div', 'court-row-acts');
      for (const [kind, label, cls] of [['kick', 'Kick', 'ghost'], ['mute', 'Mute', 'ghost'], ['ban', 'Ban', 'danger']]) {
        const b = btn(label, null, 'small court-needs-live ' + cls);
        b.disabled = !connected();
        b.addEventListener('click', () => {
          openRow = openRow && openRow.name === p.name && openRow.kind === kind ? null : { name: p.name, kind };
          renderPlayers();
        });
        acts.appendChild(b);
      }
      row.append(who, acts);
      ui.players.appendChild(row);
      if (openRow && openRow.name === p.name) row.after(actionForm(p, openRow.kind));
    }
  }

  function actionForm(p, kind) {
    const f = el('div', 'court-form ' + kind);
    const reason = input(kind === 'mute' ? 'Note for the rolls (optional)' : 'Reason (shown to the player and kept in the rolls)', 200, 'grow');
    const days = input('Days (empty = forever)', 5, 'court-days');
    days.inputMode = 'numeric';
    const go = btn(kind === 'ban' ? `Banish ${p.name}` : kind === 'kick' ? `Kick ${p.name}` : `Mute ${p.name}`, kind === 'ban' ? 'i-sword' : null, kind === 'ban' ? 'danger small' : 'primary small');
    const cancel = btn('Cancel', null, 'ghost small');
    cancel.addEventListener('click', () => {
      openRow = null;
      renderPlayers();
    });
    go.addEventListener('click', busy(go, async () => {
      const args = { name: p.name, reason: reason.value.trim() };
      if (kind !== 'kick') args.days = days.value.trim() === '' ? 0 : Number(days.value.trim());
      const r = await act(kind, args);
      if (r.ok) {
        openRow = null;
        await loadPlayers(true);
      }
    }));
    const row = el('div', 'row gap');
    row.appendChild(reason);
    if (kind !== 'kick') row.appendChild(days);
    const row2 = el('div', 'row gap');
    row2.append(go, cancel);
    f.append(row, row2);
    if (kind === 'ban') f.appendChild(el('p', 'fine', 'The game finds the player by name (also offline, from its user registry).'));
    setTimeout(() => reason.focus(), 0);
    return f;
  }

  function renderPluginCommands(list) {
    ui.plugins.replaceChildren();
    // One fold per plugin (more than twenty plugins have staff commands); the first two start open.
    const folds = new Map();
    const foldFor = (plugin) => {
      if (!folds.has(plugin)) {
        const f = el('details', 'court-plug-fold');
        if (folds.size < 2) f.open = true;
        const sum = el('summary', 'mini-h', plugin);
        sum.appendChild(el('span', 'court-plug-count', ''));
        f.appendChild(sum);
        ui.plugins.appendChild(f);
        folds.set(plugin, { f, n: 0, count: sum.lastChild });
      }
      const g = folds.get(plugin);
      g.n++;
      g.count.textContent = String(g.n);
      return g.f;
    };
    for (const d of list) {
      const fold = foldFor(d.plugin);
      const row = el('div', 'court-plug');
      row.appendChild(el('span', 'court-plug-label', d.label));
      const fields = {};
      const line = el('div', 'row gap');
      for (const a of d.args) {
        fields[a] = input(a, 64, 'court-plug-arg');
        line.appendChild(fields[a]);
      }
      const copy = btn('Copy', 'i-copy', 'ghost small');
      copy.title = d.template + (d.perm ? `  (needs ${d.perm})` : '');
      copy.addEventListener('click', busy(copy, async () => {
        const values = {};
        for (const k of Object.keys(fields)) values[k] = fields[k].value;
        const text = await C.copyPluginCommand(d.index, values);
        toast(`Copied: ${text}`);
      }));
      line.appendChild(copy);
      row.appendChild(el('code', 'court-plug-cmd mono', d.template));
      row.appendChild(line);
      fold.appendChild(row);
    }
  }

  function feedLine(l) {
    const kind = l.kind === 'chat' ? 'chat' : l.kind === 'debug' ? 'debug' : l.kind === 'steward' ? 'steward' : 'log';
    const d = el('span', `ln court-ln ${kind} ${l.kind || ''}`);
    d.dataset.k = kind;
    d.appendChild(el('span', 't', time(l.at)));
    if (l.level) d.appendChild(el('span', 'lv', l.level));
    d.appendChild(document.createTextNode(l.text));
    return d;
  }

  async function pollFeed() {
    try {
      const r = await C.feed(sel, feedLast);
      if (r.lines.length) {
        const atBottom = ui.feed.scrollHeight - ui.feed.scrollTop - ui.feed.clientHeight < 40;
        const frag = document.createDocumentFragment();
        for (const l of r.lines) frag.appendChild(feedLine(l));
        ui.feed.appendChild(frag);
        while (ui.feed.childElementCount > FEED_KEEP) ui.feed.firstElementChild.remove();
        if (atBottom) ui.feed.scrollTop = ui.feed.scrollHeight;
      }
      feedLast = r.last;
    } catch {
      /* the server may have been removed */
    }
  }

  async function loadStatus() {
    status = await C.status(sel);
    if (status.players && status.playersAt) {
      players = status.players;
      playersAt = status.playersAt;
    }
    renderStatus();
  }

  async function loadPlayers(force) {
    if (!connected()) {
      renderPlayers();
      return;
    }
    if (!force && Date.now() - lastPlayerPoll < PLAYER_POLL_MS) return;
    lastPlayerPoll = Date.now();
    const r = await C.players(sel);
    players = r.players;
    playersAt = r.at;
    renderPlayers();
  }

  async function loadBans() {
    const r = await C.act(sel, 'banlist', {});
    ui.bans.replaceChildren();
    const bans = r.parsed || [];
    if (!bans.length) {
      ui.bans.appendChild(r.ok ? emptyNote('pardon', 'No one is banished', null) : el('p', 'fine court-empty', r.result || 'No one is banished.'));
      return;
    }
    for (const b of bans) {
      const row = el('div', 'court-ban');
      const who = el('div', 'court-who');
      who.appendChild(el('b', null, `#${b.index} ${b.name}`));
      who.appendChild(el('span', 'fine mono', `${b.id} - ${b.left} left`));
      const u = btn('Unban', 'i-undo', 'ghost small court-needs-live');
      u.addEventListener('click', busy(u, async () => {
        await act('unban', { name: b.id });
        await loadBans();
      }));
      row.append(who, u);
      ui.bans.appendChild(row);
    }
  }

  async function loadRolls() {
    let list = [];
    try {
      list = await C.log(sel);
    } catch {
      return;
    }
    ui.rolls.replaceChildren();
    if (!list.length) {
      ui.rolls.appendChild(emptyNote('scales', 'Nothing judged yet', 'Every kick, ban, notice and save from the Court is written here and to disk.'));
      return;
    }
    for (const r of list.slice(0, 60)) {
      const row = el('div', 'court-roll' + (r.ok ? '' : ' failed'));
      row.appendChild(el('span', 'fine mono court-roll-t', when(r.at)));
      const what = el('span', 'court-roll-what');
      what.appendChild(el('b', null, r.action));
      if (r.target) what.appendChild(document.createTextNode(' ' + r.target));
      if (r.reason) what.appendChild(el('i', null, ' - ' + r.reason));
      row.appendChild(what);
      row.appendChild(el('span', 'court-roll-res', r.ok ? 'done' : 'failed'));
      if (r.result) row.title = r.result;
      ui.rolls.appendChild(row);
    }
  }

  async function act(action, args) {
    const r = await C.act(sel, action, args);
    toast(`${r.summary}: ${r.ok ? 'done' : 'failed'}${r.result && !r.ok ? ' - ' + r.result.split('\n')[0] : ''}`, r.ok ? null : 'bad');
    await pollFeed();
    await loadRolls();
    return r;
  }

  async function refreshAll() {
    try {
      await loadStatus();
    } catch (e) {
      fail(e);
      return;
    }
    renderPlayers();
    await Promise.all([pollFeed(), loadRolls(), loadPlayers(true).catch(() => {})]);
  }

  function tick() {
    if (!visible) return;
    loadStatus()
      .then(() => pollFeed())
      .then(() => loadPlayers(false))
      .catch(() => {});
  }

  function show() {
    build();
    if (!built) return;
    visible = true;
    renderServers().then(refreshAll);
    clearInterval(feedTimer);
    feedTimer = setInterval(() => {
      const v = document.querySelector('.view-court');
      if (!v || v.hidden) {
        visible = false;
        clearInterval(feedTimer);
        return;
      }
      tick();
    }, FEED_POLL_MS);
  }

  window.RealmCourt = { show, select: (id) => (sel = id) };
})();
