'use strict';

// Publish > News and Publish > Player update (Realm Steward). Builds its DOM inside #pub-news-root and
// #pub-update-root and talks only to window.realm.feeds (lib/publish-feeds.js in the main process).
// The Server list tab stays in app.js. One signing key signs all three files.
(function () {
  const api = window.realm;
  if (!api || !api.feeds) return;
  const FD = api.feeds;

  const TYPE_LABEL = { announcement: 'Announcement', season: 'Season', chronicle: 'Chronicle', realm: "What's new in the realm" };
  const TAG_LABEL = { sculpture: 'New sculpture', sign: 'New sign art', event: 'Event', atmosphere: 'Atmosphere', rules: 'Rules', other: 'Other' };
  const TYPE_ICON = { announcement: 'i-scroll', season: 'i-flame', chronicle: 'i-crown', realm: 'i-banners' };

  let tab = 'list';
  let status = null;
  let items = [];
  let editing = -1; // index in items, or items.length for a new one
  let installer = null;
  let built = false;
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
  function card(title, extraHead, iconId) {
    const c = el('article', 'card');
    const h = el('header', 'card-head');
    const t = el('h2', 'card-title');
    if (iconId) t.appendChild(icon(iconId));
    t.appendChild(document.createTextNode(title));
    h.appendChild(t);
    if (extraHead) h.appendChild(extraHead);
    c.appendChild(h);
    return c;
  }
  function field(label, control, hint) {
    const l = el('label', 'field');
    l.appendChild(el('span', null, label));
    l.appendChild(control);
    if (hint) l.appendChild(el('small', null, hint));
    return l;
  }
  function input(type, max, placeholder) {
    const i = el('input');
    i.type = type || 'text';
    if (max) i.maxLength = max;
    if (placeholder) i.placeholder = placeholder;
    i.spellcheck = false;
    return i;
  }
  function select(options) {
    const s = el('select');
    for (const [v, label] of options) {
      const o = el('option', null, label);
      o.value = v;
      s.appendChild(o);
    }
    return s;
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
  function when(t) {
    const d = new Date(t);
    return isNaN(d) ? '' : d.toLocaleString([], { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  }
  function bytes(n) {
    if (!Number.isFinite(n)) return '';
    const u = ['B', 'KB', 'MB', 'GB'];
    let i = 0;
    while (n >= 1024 && i < u.length - 1) {
      n /= 1024;
      i++;
    }
    return `${n.toFixed(i ? 1 : 0)} ${u[i]}`;
  }
  // ISO UTC <-> the local time a datetime-local input shows.
  function isoToLocal(iso) {
    const d = new Date(iso);
    if (!iso || isNaN(d)) return '';
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
  }
  function localToIso(v) {
    if (!v) return '';
    const d = new Date(v);
    return isNaN(d) ? '' : d.toISOString().replace(/\.\d{3}Z$/, 'Z');
  }
  function nowIso() {
    return new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');
  }
  function slug(title) {
    const base = String(title || 'news').toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 32) || 'news';
    const taken = items.map((x, i) => (i === editing ? null : x.id));
    let id = base;
    for (let n = 2; taken.includes(id); n++) id = `${base.slice(0, 36)}-${n}`;
    return id;
  }
  function factList(rows) {
    const dl = el('dl', 'facts-list');
    for (const [k, v, mono] of rows) {
      const div = el('div');
      div.append(el('dt', null, k), el('dd', mono ? 'mono' : null, v));
      dl.appendChild(div);
    }
    return dl;
  }

  // ---------------------------------------------------------------- tabs

  function setTab(name) {
    tab = name;
    for (const b of document.querySelectorAll('.pub-tab')) {
      const on = b.dataset.pub === name;
      b.classList.toggle('active', on);
      b.setAttribute('aria-selected', on ? 'true' : 'false');
      b.tabIndex = on ? 0 : -1;
    }
    document.getElementById('pub-list').hidden = name !== 'list';
    document.getElementById('pub-news-root').hidden = name !== 'news';
    document.getElementById('pub-update-root').hidden = name !== 'update';
    document.getElementById('pl-write').hidden = name !== 'list';
    document.getElementById('pub-title').textContent = name === 'list' ? 'Publish Server List' : name === 'news' ? 'Publish News' : 'Publish a Player Update';
    if (name !== 'list') refresh();
  }

  function wireTabs() {
    const tabs = [...document.querySelectorAll('.pub-tab')];
    for (const b of tabs) b.addEventListener('click', () => setTab(b.dataset.pub));
    tabs[0].parentElement.addEventListener('keydown', (ev) => {
      const i = tabs.indexOf(document.activeElement);
      if (i < 0) return;
      const to = ev.key === 'ArrowRight' ? tabs[(i + 1) % tabs.length] : ev.key === 'ArrowLeft' ? tabs[(i - 1 + tabs.length) % tabs.length] : ev.key === 'Home' ? tabs[0] : ev.key === 'End' ? tabs[tabs.length - 1] : null;
      if (!to) return;
      ev.preventDefault();
      to.focus();
      setTab(to.dataset.pub);
    });
  }

  // ---------------------------------------------------------------- shared status strip

  function keyBanner(rootEl) {
    const b = el('div', 'pub-banner');
    b.hidden = true;
    rootEl.appendChild(b);
    return b;
  }
  function renderBanner(b) {
    b.replaceChildren();
    let text = null;
    if (status && status.storeError) text = status.storeError;
    else if (status && !status.key) text = 'No signing key yet. Create it on the Server list tab first: news and updates are signed with the same key as servers.json.';
    b.hidden = !text;
    if (text) b.append(icon('i-alert', 'pub-banner-ico'), el('span', null, text));
  }
  function publishedLine(p, what) {
    if (!p || !p.exists) return `No ${what} in the output folder yet.`;
    if (!p.ok) return `The ${what} in the output folder is not signed with your key (${p.reason}). Writing replaces it.`;
    return `Published: version ${p.seq}, ${p.expired ? 'EXPIRED on' : 'valid until'} ${when(p.expires)}.`;
  }

  // ---------------------------------------------------------------- news

  function buildNews(root) {
    ui.newsBanner = keyBanner(root);
    const grid = el('div', 'pub-grid');
    root.appendChild(grid);

    // List of items
    const col1 = el('div', 'pub-col');
    const addBtn = btn('New item', 'i-plus', 'small primary');
    addBtn.addEventListener('click', () => editItem(items.length));
    const chronBtn = btn('From the Chronicle', 'i-crown', 'small ghost', 'Write up a recent Chronicle entry as news');
    chronBtn.addEventListener('click', busy(chronBtn, openChronicle));
    const headBtns = el('div', 'row gap');
    headBtns.append(chronBtn, addBtn);
    const list = card('News items', headBtns, 'i-scroll');
    ui.newsCount = el('p', 'fine');
    ui.newsList = el('div', 'pub-items');
    ui.newsList.setAttribute('role', 'list');
    ui.chron = el('div', 'pub-chron');
    ui.chron.hidden = true;
    list.append(ui.newsCount, ui.chron, ui.newsList);
    col1.appendChild(list);

    const preview = card('As players see it', null, 'i-eye');
    preview.appendChild(el('p', 'fine', 'The player app shows pinned items first, then the newest. Items dated in the future stay hidden until then; an event leaves "What\'s new in the realm" 6 hours after it starts.'));
    ui.preview = el('div', 'pub-preview');
    preview.appendChild(ui.preview);
    col1.appendChild(preview);
    grid.appendChild(col1);

    // Editor + sign
    const col2 = el('div', 'pub-col');
    const ed = card('Item', null, 'i-seal');
    ui.edEmpty = el('p', 'fine', 'Choose an item to edit, or press New item.');
    ui.edForm = el('div', 'pub-form');
    ui.edForm.hidden = true;
    ui.fType = select(Object.entries(TYPE_LABEL));
    ui.fTag = select(Object.entries(TAG_LABEL));
    ui.fTitle = input('text', 100, 'The Grey Heron stands at the harbour gate');
    ui.fBody = el('textarea');
    ui.fBody.maxLength = 600;
    ui.fBody.rows = 4;
    ui.fBody.placeholder = 'Plain text. Line breaks are kept.';
    ui.fDate = input('datetime-local');
    ui.fAt = input('datetime-local');
    ui.fLink = input('url', 300, 'https://...');
    ui.fServer = input('text', 2, 's1');
    ui.fPinned = el('input');
    ui.fPinned.type = 'checkbox';
    const pinLab = el('label', 'check');
    pinLab.append(ui.fPinned, el('span', null, 'Pinned (stays at the top)'));
    ui.tagField = field('What changed', ui.fTag);
    ui.atField = field('Event starts (optional)', ui.fAt, 'Shown as "in 2 days"; dropped 6 hours after it starts.');
    const r1 = el('div', 'row gap');
    r1.append(field('Kind', ui.fType), ui.tagField);
    const r2 = el('div', 'row gap');
    r2.append(field('Posted', ui.fDate, 'A future time schedules it.'), ui.atField);
    const r3 = el('div', 'row gap');
    r3.append(field('Link (optional)', ui.fLink, '"Read more" opens it.'), field('Server (optional)', ui.fServer, 'Only with several servers.'));
    ui.fBodyCount = el('small', 'pub-count');
    const bodyField = field('Text (optional)', ui.fBody);
    bodyField.appendChild(ui.fBodyCount);
    const acts = el('div', 'row gap');
    ui.edSave = btn('Keep item', 'i-check', 'small primary');
    ui.edCancel = btn('Cancel', null, 'small ghost');
    ui.edSave.addEventListener('click', busy(ui.edSave, saveItem));
    ui.edCancel.addEventListener('click', () => editItem(-1));
    acts.append(ui.edSave, ui.edCancel);
    ui.edForm.append(r1, field('Title', ui.fTitle, 'One line, up to 100 characters.'), bodyField, r2, r3, pinLab, acts);
    ui.fType.addEventListener('change', syncForm);
    ui.fBody.addEventListener('input', syncForm);
    ed.append(ui.edEmpty, ui.edForm);
    col2.appendChild(ed);

    const sign = card('Sign and write', null, 'i-key');
    ui.nDays = input('number');
    ui.nDays.min = 1;
    ui.nDays.max = 365;
    ui.nFacts = el('div');
    ui.nWrite = btn('Sign & write news.json', 'i-seal', 'primary');
    ui.nWrite.addEventListener('click', busy(ui.nWrite, writeNews));
    ui.nResult = el('div', 'pub-result');
    sign.append(field('Valid for (days)', ui.nDays, 'Players keep showing the last good feed after it expires, marked as a saved copy.'), ui.nFacts, ui.nWrite, ui.nResult);
    col2.appendChild(sign);
    grid.appendChild(col2);
  }

  function itemState(it) {
    const now = Date.now();
    const d = Date.parse(it.date);
    if (d > now) return { cls: 'sched', text: `Scheduled ${when(it.date)}` };
    if (it.type === 'realm' && it.at && Date.parse(it.at) + 6 * 3600e3 < now) return { cls: 'past', text: 'Event over: hidden' };
    return { cls: '', text: when(it.date) };
  }

  function renderNews() {
    renderBanner(ui.newsBanner);
    const max = status ? status.maxItems : 60;
    ui.newsCount.textContent = `${items.length} of ${max} items. Changes are kept as a draft until you sign.`;
    ui.newsList.replaceChildren();
    if (!items.length) {
      const empty = window.RealmArt ? window.RealmArt.emptyState({ art: 'decree', title: 'No news yet', body: 'Write the first announcement, or turn a Chronicle entry into news.' }) : el('p', 'fine', 'No news yet.');
      ui.newsList.appendChild(empty);
    }
    items.forEach((it, i) => {
      const row = el('div', 'pub-item' + (i === editing ? ' active' : ''));
      row.setAttribute('role', 'listitem');
      const open = el('button', 'pub-item-main');
      open.type = 'button';
      open.appendChild(icon(TYPE_ICON[it.type] || 'i-scroll', 'pub-item-ico'));
      const txt = el('span', 'pub-item-txt');
      const st = itemState(it);
      const title = el('b', null, it.title || '(no title)');
      const meta = el('small', st.cls, `${it.type === 'realm' ? TAG_LABEL[it.tag || 'other'] : TYPE_LABEL[it.type] || it.type} · ${st.text}${it.pinned ? ' · pinned' : ''}`);
      txt.append(title, meta);
      open.appendChild(txt);
      open.addEventListener('click', () => editItem(i));
      const del = btn('', 'i-x', 'small ghost pub-del', `Remove "${it.title}"`);
      del.setAttribute('aria-label', `Remove ${it.title}`);
      del.addEventListener('click', busy(del, async () => {
        items.splice(i, 1);
        if (editing === i) editing = -1;
        else if (editing > i) editing--;
        await saveDraft();
        renderNews();
        renderEditor();
      }));
      row.append(open, del);
      ui.newsList.appendChild(row);
    });
    renderPreview();
    ui.nDays.value = status ? status.news.validDays : 90;
    ui.nFacts.replaceChildren(
      factList([
        ['Next version', status ? String(status.news.nextSeq) : '-'],
        ['Writes to', status ? status.newsFile : '-', true],
        ['Host it at', status && status.newsUrl ? status.newsUrl : 'next to servers.json', true],
        ['Now', status ? publishedLine(status.news.published, 'news.json') : '-']
      ])
    );
    ui.nWrite.disabled = !status || !status.key || !!status.storeError;
  }

  function renderPreview() {
    ui.preview.replaceChildren();
    const now = Date.now();
    const shown = items
      .filter((it) => Date.parse(it.date) <= now + 5 * 60e3)
      .slice()
      .sort((a, b) => (a.pinned === b.pinned ? Date.parse(b.date) - Date.parse(a.date) : a.pinned ? -1 : 1));
    const news = shown.filter((it) => it.type !== 'realm').slice(0, 5);
    const realm = shown.filter((it) => it.type === 'realm' && !(it.at && Date.parse(it.at) + 6 * 3600e3 < now)).slice(0, 3);
    const block = (label, list) => {
      const b = el('div', 'pub-prev-block');
      b.appendChild(el('p', 'eyebrow', label));
      if (!list.length) b.appendChild(el('p', 'fine', 'Nothing to show.'));
      for (const it of list) {
        const r = el('div', 'pub-prev-row');
        r.append(el('b', null, it.title), el('small', null, it.body ? it.body.split('\n')[0].slice(0, 90) : ''));
        b.appendChild(r);
      }
      return b;
    };
    ui.preview.append(block('News', news), block("What's new in the realm", realm));
  }

  function editItem(i) {
    editing = i;
    renderNews();
    renderEditor();
    if (i >= 0) ui.fTitle.focus();
  }

  function renderEditor() {
    const on = editing >= 0;
    ui.edEmpty.hidden = on;
    ui.edForm.hidden = !on;
    if (!on) return;
    const it = items[editing] || { type: 'announcement', title: '', body: '', date: nowIso() };
    ui.fType.value = it.type || 'announcement';
    ui.fTag.value = it.tag || 'other';
    ui.fTitle.value = it.title || '';
    ui.fBody.value = it.body || '';
    ui.fDate.value = isoToLocal(it.date || nowIso());
    ui.fAt.value = isoToLocal(it.at);
    ui.fLink.value = it.link || '';
    ui.fServer.value = it.server || '';
    ui.fPinned.checked = it.pinned === true;
    ui.edSave.querySelector('span').textContent = editing < items.length ? 'Keep changes' : 'Add item';
    syncForm();
  }

  function syncForm() {
    const realm = ui.fType.value === 'realm';
    ui.tagField.hidden = !realm;
    ui.atField.hidden = !realm;
    ui.fBodyCount.textContent = `${ui.fBody.value.length} / 600`;
  }

  async function saveItem() {
    const title = ui.fTitle.value.trim();
    if (!title) throw new Error('Write a title first.');
    const date = localToIso(ui.fDate.value) || nowIso();
    const link = ui.fLink.value.trim();
    if (link && !/^https:\/\//i.test(link)) throw new Error('A link must start with https://');
    const server = ui.fServer.value.trim();
    if (server && !/^s[1-4]$/.test(server)) throw new Error('Server is a server id like s1, or empty.');
    const prev = items[editing];
    const it = { id: prev && prev.id ? prev.id : slug(title), type: ui.fType.value, title, body: ui.fBody.value.replace(/\r/g, ''), date };
    if (ui.fPinned.checked) it.pinned = true;
    if (it.type === 'realm') {
      it.tag = ui.fTag.value;
      const at = localToIso(ui.fAt.value);
      if (at) it.at = at;
    }
    if (link) it.link = link;
    if (server) it.server = server;
    if (editing >= items.length) {
      if (status && items.length >= status.maxItems) throw new Error(`A news feed holds at most ${status.maxItems} items.`);
      items.push(it);
    } else items[editing] = it;
    editing = -1;
    await saveDraft();
    renderNews();
    renderEditor();
    toast('Kept in the draft. Sign & write news.json when you are ready.', 'ok');
  }

  async function saveDraft() {
    await FD.saveNewsDraft(items, Number(ui.nDays.value) || undefined);
  }

  async function openChronicle() {
    if (!ui.chron.hidden) {
      ui.chron.hidden = true;
      return;
    }
    let events = [];
    try {
      events = await api.getEvents(0);
    } catch (e) {
      throw new Error(`The Chronicle is not answering (${e.message}). Start it on the Overlay screen.`);
    }
    ui.chron.replaceChildren(el('p', 'eyebrow', 'Recent Chronicle entries'));
    if (!events.length) ui.chron.appendChild(el('p', 'fine', 'The Chronicle has no entries yet.'));
    for (const ev of events.slice().reverse().slice(0, 12)) {
      const b = el('button', 'pub-chron-row');
      b.type = 'button';
      const meta = window.RealmArt ? window.RealmArt.eventMeta(ev.type) : null;
      if (window.RealmArt) b.appendChild(window.RealmArt.eventMark(ev, 'pub-chron-ico'));
      const t = el('span');
      t.append(el('b', null, ev.title || ''), el('small', null, `${meta ? meta.label : ev.type} · ${when(ev.ts)}`));
      b.appendChild(t);
      b.addEventListener('click', () => {
        editing = items.length;
        ui.chron.hidden = true;
        renderNews();
        renderEditor();
        ui.fType.value = 'chronicle';
        ui.fTitle.value = String(ev.title || '').slice(0, 100);
        ui.fBody.value = String(ev.detail || '').slice(0, 600);
        const d = new Date(ev.ts);
        ui.fDate.value = isoToLocal(isNaN(d) ? nowIso() : d.toISOString());
        syncForm();
        ui.fTitle.focus();
      });
      ui.chron.appendChild(b);
    }
    ui.chron.hidden = false;
  }

  async function writeNews() {
    if (editing >= 0) throw new Error('Keep or cancel the item you are editing first.');
    const r = await FD.writeNews({ items, validDays: Number(ui.nDays.value) });
    ui.nResult.replaceChildren(
      el('p', 'pub-ok', `news.json version ${r.seq} signed and written.`),
      factList([
        ['File', r.file, true],
        ['Items', `${r.items} (${r.shownNow} shown now${r.scheduled ? `, ${r.scheduled} scheduled` : ''})`],
        ['Expires', when(r.expires)],
        ['Key', r.keyId, true]
      ]),
      el('p', 'fine', 'Upload news.json next to servers.json. Nothing was uploaded.')
    );
    toast('news.json signed and written. Nothing was uploaded.', 'ok');
    await refresh();
  }

  // ---------------------------------------------------------------- update

  function buildUpdate(root) {
    ui.updBanner = keyBanner(root);
    const grid = el('div', 'pub-grid');
    root.appendChild(grid);

    const col1 = el('div', 'pub-col');
    const pick = btn('Choose installer', 'i-folder', 'small primary');
    pick.addEventListener('click', busy(pick, async () => {
      const r = await FD.pickInstaller();
      if (!r) return;
      installer = r;
      if (r.version) ui.uVersion.value = r.version;
      renderInstaller();
    }));
    const ic = card('The installer', pick, 'i-download');
    ic.appendChild(el('p', 'fine', 'Pick Realm-Setup-<version>.exe from the release folder (npm run release). Realm reads its size and SHA-256 from the file; players\' apps refuse any download that differs.'));
    ui.inst = el('div', 'pub-inst');
    ic.appendChild(ui.inst);
    col1.appendChild(ic);

    const nc = card("What's in it", null, 'i-scroll');
    ui.uTitle = input('text', 80, 'The Ember Update');
    ui.uNotes = el('textarea');
    ui.uNotes.rows = 6;
    ui.uNotes.placeholder = 'One change per line, up to 20 lines.';
    nc.append(field('Name of the update (optional)', ui.uTitle), field('Patch notes', ui.uNotes, 'Shown under "What\'s in it" and once after the update.'));
    col1.appendChild(nc);
    grid.appendChild(col1);

    const col2 = el('div', 'pub-col');
    const dc = card('Offer it', null, 'i-globe');
    ui.uVersion = input('text', 30, '1.1.0');
    ui.uUrl = input('url', 300, 'https://github.com/you/realm/releases/download/v1.1.0/Realm-Setup-1.1.0.exe');
    ui.uMin = input('text', 30, 'optional, e.g. 1.0.2');
    ui.uHot = el('input');
    ui.uHot.type = 'checkbox';
    const hotLab = el('label', 'check');
    hotLab.append(ui.uHot, el('span', null, 'Hotfix: urgent. The download starts by itself and there is no Later.'));
    ui.uDays = input('number');
    ui.uDays.min = 1;
    ui.uDays.max = 365;
    const r1 = el('div', 'row gap');
    r1.append(field('Version', ui.uVersion), field('Minimum version (optional)', ui.uMin, 'Older apps must update (urgent).'));
    dc.append(r1, field('Download address', ui.uUrl, 'https only. Upload the installer there yourself.'), hotLab, field('Offer for (days)', ui.uDays));
    col2.appendChild(dc);

    const sc = card('Sign and write', null, 'i-key');
    ui.uFacts = el('div');
    ui.uWrite = btn('Sign & write update.json', 'i-seal', 'primary');
    ui.uWrite.addEventListener('click', busy(ui.uWrite, writeUpdate));
    ui.uResult = el('div', 'pub-result');
    sc.append(ui.uFacts, ui.uWrite, ui.uResult);
    col2.appendChild(sc);
    grid.appendChild(col2);
  }

  function renderInstaller() {
    ui.inst.replaceChildren();
    if (!installer) {
      ui.inst.appendChild(el('p', 'fine pub-none', 'No installer chosen yet.'));
      return;
    }
    ui.inst.appendChild(
      factList([
        ['File', installer.name, true],
        ['Size', `${bytes(installer.size)} (${installer.size.toLocaleString()} bytes)`],
        ['SHA-256', installer.sha256, true],
        ['Checked', installer.sums === 'match' ? 'Matches SHA256SUMS.txt in its folder' : 'No SHA256SUMS.txt next to it (hashed from the file)']
      ])
    );
  }

  function renderUpdate() {
    renderBanner(ui.updBanner);
    renderInstaller();
    ui.uFacts.replaceChildren(
      factList([
        ['Next version', status ? String(status.update.nextSeq) : '-'],
        ['Writes to', status ? status.updateFile : '-', true],
        ['Host it at', status && status.updateUrl ? status.updateUrl : 'next to servers.json', true],
        ['Now', status ? publishedLine(status.update.published, 'update.json') : '-']
      ])
    );
    ui.uWrite.disabled = !status || !status.key || !!status.storeError;
  }

  function fillUpdateDraft() {
    const d = (status && status.update.draft) || {};
    if (!ui.uVersion.value && d.version) ui.uVersion.value = d.version;
    if (!ui.uUrl.value && d.url) ui.uUrl.value = d.url;
    if (!ui.uMin.value && d.minVersion) ui.uMin.value = d.minVersion;
    if (!ui.uTitle.value && d.notesTitle) ui.uTitle.value = d.notesTitle;
    if (!ui.uNotes.value && Array.isArray(d.notesItems)) ui.uNotes.value = d.notesItems.join('\n');
    ui.uDays.value = (status && status.update.validDays) || 60;
  }

  async function writeUpdate() {
    if (!installer) throw new Error('Choose the installer first.');
    const r = await FD.writeUpdate({
      installer: installer.path,
      version: ui.uVersion.value.trim(),
      url: ui.uUrl.value.trim(),
      minVersion: ui.uMin.value.trim(),
      hotfix: ui.uHot.checked,
      notesTitle: ui.uTitle.value,
      notesItems: ui.uNotes.value,
      validDays: Number(ui.uDays.value)
    });
    ui.uResult.replaceChildren(
      el('p', 'pub-ok', `update.json version ${r.seq} signed: Realm ${r.version}${r.urgent ? ' (urgent)' : ''}.`),
      factList([
        ['File', r.file, true],
        ['Installer', `${bytes(r.size)} · SHA-256 ${r.sha256.slice(0, 16)}...`],
        ['Offered until', when(r.expires)],
        ['Key', r.keyId, true]
      ]),
      el('p', 'fine', 'Upload the installer to the download address first, then update.json next to servers.json. Nothing was uploaded.')
    );
    toast('update.json signed and written. Nothing was uploaded.', 'ok');
    await refresh();
  }

  // ---------------------------------------------------------------- load

  async function refresh() {
    if (!built) build();
    try {
      status = await FD.status();
    } catch (e) {
      fail(e);
      return;
    }
    if (tab === 'news') {
      if (editing < 0) items = (status.news.items || []).slice();
      renderNews();
      renderEditor();
    } else if (tab === 'update') {
      fillUpdateDraft();
      renderUpdate();
    }
  }

  function build() {
    const n = document.getElementById('pub-news-root');
    const u = document.getElementById('pub-update-root');
    if (!n || !u || built) return;
    built = true;
    buildNews(n);
    buildUpdate(u);
  }

  wireTabs();
  window.RealmPublishFeeds = { refresh, tab: () => tab, setTab };
})();
