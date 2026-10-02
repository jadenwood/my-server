'use strict';

(function () {
  const api = window.realm;
  const $ = (id) => document.getElementById(id);

  if (!api) {
    $('toast').textContent = 'Launcher bridge unavailable.';
    $('toast').classList.add('bad');
    return;
  }

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
    released: ['k-bond', 'i-chain']
  };
  const MAX_EVENTS = 12;
  const STALE_MS = 2 * 60 * 1000;

  let config = null;
  let events = [];
  let lastEventId = 0;
  let toastTimer = null;

  function el(tag, cls, text) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text != null) node.textContent = String(text);
    return node;
  }

  function icon(id, cls) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', cls);
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', '#' + id);
    svg.appendChild(use);
    return svg;
  }

  function toast(message, bad) {
    const t = $('toast');
    t.textContent = message;
    t.classList.toggle('bad', !!bad);
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { t.textContent = ''; }, 6000);
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

  function renderConfig() {
    $('realm-name').textContent = config.realmName;
    $('tagline').textContent = config.tagline;
    document.title = config.realmName + ' Launcher';
    $('server-name').textContent = config.server.name;
    $('server-address').textContent = config.server.address + ':' + config.server.port;
    $('howto-ip').textContent = config.server.address;
    $('howto-port').textContent = String(config.server.port);

    const links = $('links');
    links.replaceChildren();
    for (const link of config.links) {
      if (link.id === 'chronicle') {
        $('chronicle-link').hidden = false;
        continue;
      }
      const b = el('button', 'ghost', link.label);
      b.dataset.link = link.id;
      links.appendChild(b);
    }
  }

  async function renderNews() {
    const list = $('news');
    list.replaceChildren();
    const news = await api.getNews();
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

  function setStatus(kind, label, detail) {
    const pill = $('status-pill');
    pill.className = 'pill ' + kind;
    pill.textContent = label;
    $('status-detail').textContent = detail;
  }

  async function refreshState() {
    const res = await api.getState();
    if (!res.ok) {
      setStatus('bad', 'Unreachable', 'The Chronicle service did not answer (' + res.error + '). The game server may still be up.');
      $('players').textContent = '-';
      $('house-count').textContent = '-';
      return;
    }
    const s = res.state || {};
    const age = Date.now() - Date.parse(s.updated);
    if (Number.isFinite(age) && age < STALE_MS) {
      setStatus('ok', 'Online', 'Reported by the Chronicle ' + ago(s.updated) + '.');
    } else {
      setStatus('warn', 'Quiet', s.updated ? 'Last report ' + ago(s.updated) + '. The server may be down.' : 'No report yet.');
    }
    const online = Number.isFinite(s.online) ? s.online : '-';
    const max = Number.isFinite(s.maxPlayers) ? s.maxPlayers : '-';
    $('players').textContent = online + ' / ' + max;
    $('house-count').textContent = Array.isArray(s.houses) ? String(s.houses.length) : '-';

    if (s.king) {
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
      list.appendChild(el('li', 'empty', 'The Chronicle is silent.'));
      return;
    }
    for (const e of events) {
      const [kind, glyph] = EVENT_STYLE[e.type] || ['k-house', 'i-scroll'];
      const li = el('li', 'event ' + kind);
      const body = el('div');
      body.append(el('p', 'event-title', e.title));
      const meta = [ago(e.ts), e.detail].filter(Boolean).join(' · ');
      body.append(el('p', 'event-meta', meta));
      li.append(icon(glyph, 'event-ico'), body);
      list.appendChild(li);
    }
  }

  async function refreshEvents() {
    const res = await api.getEvents(lastEventId);
    if (!res.ok) {
      if (!events.length) renderEvents();
      return;
    }
    const fresh = res.events.filter((e) => e && Number.isInteger(e.id) && e.id > lastEventId);
    if (fresh.length) {
      lastEventId = Math.max(lastEventId, ...fresh.map((e) => e.id));
      events = fresh.concat(events).sort((a, b) => b.id - a.id).slice(0, MAX_EVENTS);
    }
    renderEvents();
  }

  async function poll() {
    await Promise.allSettled([refreshState(), refreshEvents()]);
  }

  function bind() {
    $('play').addEventListener('click', async () => {
      try {
        await api.play();
        toast('Opening Steam. Then connect to ' + config.server.address + ':' + config.server.port + '.');
      } catch {
        toast('Could not open Steam. Is it installed?', true);
      }
    });

    $('copy-address').addEventListener('click', async () => {
      const text = await api.copyAddress();
      toast('Copied ' + text + '. Paste it in the in-game direct connect field.');
    });

    $('verify').addEventListener('click', async () => {
      const res = await api.verifyInstall();
      const hit = res.results.find((r) => r.exists);
      if (hit) toast('Reign of Kings folder found: ' + hit.path);
      else if (!res.results.length) toast('No install paths set in config.json.', true);
      else toast('Install folder not found. Check installPaths in config.json.', true);
    });

    document.addEventListener('click', (ev) => {
      const linkBtn = ev.target.closest('[data-link]');
      if (linkBtn) api.openLink(linkBtn.dataset.link);
      const winBtn = ev.target.closest('[data-win]');
      if (winBtn) api.windowAction(winBtn.dataset.win);
    });
  }

  async function start() {
    config = await api.getConfig();
    renderConfig();
    bind();
    renderNews();
    await poll();
    setInterval(poll, config.pollSeconds * 1000);
  }

  start();
})();
