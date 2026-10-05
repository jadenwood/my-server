'use strict';

// Realm features (Realm Steward): every plugin's main switches, on or off, per server.
// Builds its DOM inside #features-root and talks only to window.realm.features / fleet / server.
// Main process: lib/features.js (safe edits of oxide/config/<Plugin>.json, then /oxide.reload <Plugin>).
(function () {
  const api = window.realm;
  if (!api || !api.features) return;
  const FT = api.features;

  let root = null;
  let built = false;
  let sel = 's1';
  let model = null;
  let data = null;
  let filter = '';
  const open = new Set(); // plugins whose "more switches" list is open
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
  function toast(msg, kind) {
    const box = document.getElementById('toasts');
    if (!box) return;
    const t = el('div', 'toast' + (kind ? ' ' + kind : ''), msg);
    t.setAttribute('role', kind === 'bad' ? 'alert' : 'status');
    box.appendChild(t);
    setTimeout(() => t.remove(), kind === 'bad' ? 9000 : 5000);
  }
  function fail(e) {
    toast(e && e.message ? e.message : String(e), 'bad');
  }
  function words(p) {
    return p.replace(/\./g, ' · ').replace(/([a-z])([A-Z])/g, '$1 $2');
  }

  // ---------------------------------------------------------------- build

  function build() {
    root = document.getElementById('features-root');
    if (!root || built) return;
    built = true;
    const head = el('header', 'view-head');
    const titles = el('div');
    titles.append(el('p', 'eyebrow', 'What the realm offers'), el('h1', 'view-title', 'Realm Features'));
    head.appendChild(titles);
    const right = el('div', 'ft-headright');
    ui.servers = el('div', 'court-servers');
    ui.servers.setAttribute('role', 'tablist');
    ui.servers.setAttribute('aria-label', 'Server');
    ui.pill = el('span', 'pill big', 'Checking');
    right.append(ui.servers, ui.pill);
    head.appendChild(right);
    root.appendChild(head);

    ui.note = el('p', 'fine ft-note');
    root.appendChild(ui.note);

    const tools = el('div', 'ft-tools');
    ui.find = el('input', 'ft-find');
    ui.find.type = 'search';
    ui.find.placeholder = 'Find a feature';
    ui.find.setAttribute('aria-label', 'Find a feature');
    ui.find.addEventListener('input', () => {
      filter = ui.find.value.trim().toLowerCase();
      renderPlugins();
    });
    ui.data = el('div', 'ft-data');
    tools.append(ui.find, ui.data);
    root.appendChild(tools);

    ui.body = el('div', 'ft-body');
    root.appendChild(ui.body);
  }

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
        refresh();
      });
      ui.servers.appendChild(b);
    }
  }

  function renderHead() {
    if (!model) {
      ui.pill.className = 'pill big bad';
      ui.pill.textContent = 'No server';
      ui.note.textContent = 'Set up a server copy first (Setup).';
      return;
    }
    const live = model.running && model.console;
    ui.pill.className = 'pill big ' + (live ? 'ok' : model.running ? 'warn' : '');
    ui.pill.textContent = live ? 'Live' : model.running ? 'Running, no console' : 'Server stopped';
    ui.note.textContent = live
      ? 'A change is saved to the plugin\'s config and the plugin is reloaded at once (oxide.reload). Players keep playing.'
      : model.running
        ? 'A change is saved to the plugin\'s config. The live console is not connected, so the plugin picks it up when it is reloaded or the server restarts.'
        : `A change is saved to the plugin's config (${model.configDir}) and applies when the server starts. Each change is backed up first.`;
  }

  function renderData() {
    ui.data.replaceChildren();
    if (!data) return;
    for (const s of data.sets) {
      const chip = el('span', 'ft-datachip' + (s.changed ? ' stale' : '') + (s.invalid ? ' bad' : ''));
      chip.title = `${s.plugin}: ${s.files} file${s.files === 1 ? '' : 's'} shipped${s.changed ? `, ${s.changed} newer than on the server` : ', all on the server'}${s.renamed && s.renamed.length ? `; written as ${s.renamed.map((r) => `${r.to} (from ${r.from})`).join(', ')}` : ''}${s.others.length ? `; also on the server: ${s.others.join(', ')}` : ''}`;
      chip.append(el('b', null, s.label), el('span', null, s.version ? `v ${s.version.slice(0, 8)}` : `${s.files} file${s.files === 1 ? '' : 's'}`));
      if (s.changed) chip.appendChild(el('i', null, `${s.changed} to deploy`));
      ui.data.appendChild(chip);
    }
    if (data.sets.some((s) => s.changed)) {
      const b = btn('Update plugins', 'i-plugin', 'small primary', 'Copy the plugins and their data to every server');
      b.addEventListener('click', async () => {
        b.disabled = true;
        try {
          const r = await api.server.deployPlugins('all');
          toast(`Deployed ${r.copied} plugin file(s) and ${r.dataCopied || 0} data file(s).`, 'ok');
          await refresh();
        } catch (e) {
          fail(e);
        } finally {
          b.disabled = false;
        }
      });
      ui.data.appendChild(b);
    }
  }

  function toggle(on, label, onChange, { disabled = false, danger = false } = {}) {
    const b = el('button', 'ft-switch' + (on ? ' on' : '') + (danger ? ' danger' : ''));
    b.type = 'button';
    b.setAttribute('role', 'switch');
    b.setAttribute('aria-checked', on ? 'true' : 'false');
    b.setAttribute('aria-label', label);
    b.disabled = disabled;
    b.appendChild(el('i'));
    b.addEventListener('click', () => onChange(!on, b));
    return b;
  }

  function matches(p) {
    if (!filter) return true;
    const hay = [p.plugin, p.title, p.blurb, ...p.switches.map((s) => s.label)].join(' ').toLowerCase();
    return hay.includes(filter);
  }

  async function change(p, s, value, control) {
    if (s.danger && (s.danger === true ? value === true : value === s.danger)) {
      const text = s.help || 'Are you sure?';
      const title = `${p.title}: ${s.label} ${value === true ? 'on' : `to ${value}`}?`;
      const ok = window.RealmConfirm ? await window.RealmConfirm({ title, text, ok: value === true ? 'Turn on' : `Switch to ${value}` }) : window.confirm(`${title}\n\n${text}`);
      if (!ok) return;
    }
    if (control) control.disabled = true;
    try {
      const r = await FT.set(sel, p.plugin, s.path, value);
      const what = `${p.title}: ${s.label || words(s.path)} ${value === true ? 'on' : value === false ? 'off' : value}`;
      if (!r.changed) toast(`${what} (already so).`);
      else if (r.reload && r.reload.ok) toast(`${what}. ${p.plugin} reloaded.`, 'ok');
      else if (r.reload) toast(`${what}. Saved, but the reload failed: ${r.reload.result || 'no answer'}. It applies at the next start.`, 'bad');
      else toast(`${what}. Saved; applies when ${r.running ? 'the plugin is reloaded' : 'the server starts'}.`, 'ok');
    } catch (e) {
      fail(e);
    }
    await refresh();
  }

  function switchRow(p, s, masterOff) {
    const row = el('div', 'ft-row' + (masterOff && !s.master ? ' dim' : '') + (s.master ? ' master' : ''));
    const txt = el('div', 'ft-label');
    txt.appendChild(el('span', null, s.label));
    if (s.help) txt.appendChild(el('small', null, s.help));
    row.appendChild(txt);
    const usable = p.config === 'ok' && s.present;
    if (s.kind === 'enum') {
      const seg = el('div', 'ft-seg');
      seg.setAttribute('role', 'radiogroup');
      seg.setAttribute('aria-label', `${p.title}: ${s.label}`);
      for (const o of s.options) {
        const b = el('button', 'ft-seg-btn' + (s.value === o ? ' on' : '') + (s.danger === o ? ' danger' : ''), o);
        b.type = 'button';
        b.setAttribute('role', 'radio');
        b.setAttribute('aria-checked', s.value === o ? 'true' : 'false');
        b.disabled = !usable;
        b.addEventListener('click', () => s.value !== o && change(p, s, o, b));
        seg.appendChild(b);
      }
      row.appendChild(seg);
    } else {
      row.appendChild(toggle(s.value === true, `${p.title}: ${s.label}`, (v, b) => change(p, s, v, b), { disabled: !usable, danger: !!s.danger }));
    }
    if (!s.present && p.config === 'ok') row.title = 'Not in this server\'s config yet: start the server once with this plugin version.';
    return row;
  }

  function pluginCard(p) {
    const c = el('article', 'card ft-card' + (p.installed ? '' : ' missing'));
    const head = el('header', 'ft-card-head');
    const t = el('div');
    t.append(el('h3', 'ft-title', p.title), el('span', 'mono ft-plugin', p.plugin));
    head.appendChild(t);
    const st = el('span', 'ft-state');
    if (!p.installed) st.append(el('span', 'ft-chip off', 'not deployed'));
    else if (p.config === 'missing') st.append(el('span', 'ft-chip', 'no config yet'));
    else if (p.config === 'damaged') st.append(el('span', 'ft-chip bad', 'config damaged'));
    head.appendChild(st);
    c.appendChild(head);
    c.appendChild(el('p', 'ft-blurb', p.blurb));
    if (p.config === 'damaged') c.appendChild(el('p', 'fine ft-warn', `oxide\\config\\${p.plugin}.json cannot be read (${p.reason}). Steward will not edit it. Fix it, or delete it so the plugin writes its defaults.`));
    else if (p.config === 'missing') c.appendChild(el('p', 'fine', p.installed ? 'The plugin writes its config the first time it loads. Start the server once.' : 'Deploy it with Update plugins on the Servers screen.'));
    const master = p.switches.find((s) => s.master);
    const masterOff = !!(master && master.present && master.value === false);
    const rows = el('div', 'ft-rows');
    for (const s of p.switches) rows.appendChild(switchRow(p, s, masterOff));
    c.appendChild(rows);
    if (p.extra && p.extra.length) {
      const more = el('button', 'ft-more', `${open.has(p.plugin) ? 'Fewer' : 'More'} switches (${p.extra.length})`);
      more.type = 'button';
      more.setAttribute('aria-expanded', open.has(p.plugin) ? 'true' : 'false');
      more.addEventListener('click', () => {
        if (open.has(p.plugin)) open.delete(p.plugin);
        else open.add(p.plugin);
        renderPlugins();
      });
      c.appendChild(more);
      if (open.has(p.plugin)) {
        const ex = el('div', 'ft-rows ft-extra');
        for (const x of p.extra) ex.appendChild(switchRow(p, { path: x.path, label: words(x.path), kind: 'bool', present: true, value: x.value }, false));
        c.appendChild(ex);
      }
    }
    return c;
  }

  function renderPlugins() {
    ui.body.replaceChildren();
    if (!model) return;
    let any = false;
    for (const g of model.groups) {
      const list = model.plugins.filter((p) => p.group === g && matches(p));
      if (!list.length) continue;
      any = true;
      const sec = el('section', 'ft-group');
      sec.appendChild(el('h2', 'ft-group-title', g));
      const grid = el('div', 'ft-grid');
      for (const p of list) grid.appendChild(pluginCard(p));
      sec.appendChild(grid);
      ui.body.appendChild(sec);
    }
    if (!any) ui.body.appendChild(el('p', 'fine ft-none', `Nothing matches "${filter}".`));
  }

  async function refresh() {
    build();
    try {
      const list = await api.fleet.list();
      if (!list.some((s) => s.id === sel) && list[0]) sel = list[0].id;
      renderServers(list);
    } catch {
      /* tabs only */
    }
    try {
      model = await FT.list(sel);
    } catch (e) {
      model = null;
      fail(e);
    }
    try {
      data = api.server.dataStatus ? await api.server.dataStatus(sel) : null;
    } catch {
      data = null;
    }
    renderHead();
    renderData();
    renderPlugins();
  }

  window.RealmFeatures = { show: refresh, select: (id) => (sel = id) };
})();
