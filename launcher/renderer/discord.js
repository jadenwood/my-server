'use strict';

// Discord herald card for Realm Steward > Settings. Builds its own DOM inside the settings grid and
// talks only to window.realm.discord (preload.js). The webhook token never comes back from the main
// process: after saving, the card shows a redacted hint only.
(function () {
  const api = window.realm;
  if (!api || !api.discord) return;
  const Dc = api.discord;

  function el(tag, cls, text) {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = String(text);
    return n;
  }
  function icon(id) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', 'ico');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', '#' + id);
    svg.appendChild(use);
    return svg;
  }
  function btn(label, iconId, cls) {
    const b = el('button', 'btn ' + (cls || ''));
    b.type = 'button';
    if (iconId) b.appendChild(icon(iconId));
    b.appendChild(el('span', null, label));
    return b;
  }
  function toast(msg, kind) {
    const box = document.getElementById('toasts');
    if (!box) return;
    const t = el('div', 'toast' + (kind ? ' ' + kind : ''), msg);
    box.appendChild(t);
    setTimeout(() => t.remove(), kind === 'bad' ? 9000 : 5000);
  }
  function when(t) {
    const d = new Date(t);
    return isNaN(d) ? '' : d.toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  }
  function busy(button, fn) {
    return async () => {
      if (button.disabled) return;
      button.disabled = true;
      try {
        await fn();
      } catch (e) {
        toast(e && e.message ? e.message : String(e), 'bad');
      } finally {
        button.disabled = false;
      }
    };
  }

  let state = null;
  const card = el('article', 'card discord-card');
  card.id = 'discord-card';
  const head = el('header', 'card-head');
  head.appendChild(el('h2', 'card-title', 'Discord herald'));
  const pill = el('span', 'pill', 'Off');
  head.appendChild(pill);
  card.appendChild(head);
  card.appendChild(el('p', 'fine discord-lead', 'Posts new Chronicle events (coronations, claims, betrayals...) to a Discord channel as they happen, in each house\'s colours. Off until you turn it on. Nothing from before you turn it on is posted.'));

  // Webhook URL
  const urlField = el('label', 'field');
  urlField.appendChild(el('span', null, 'Channel webhook URL'));
  const urlRow = el('div', 'row');
  const url = el('input');
  url.type = 'password';
  url.maxLength = 300;
  url.spellcheck = false;
  url.autocomplete = 'off';
  url.placeholder = 'https://discord.com/api/webhooks/...';
  const show = btn('Show', null, 'ghost small');
  show.addEventListener('click', () => {
    url.type = url.type === 'password' ? 'text' : 'password';
    show.lastChild.textContent = url.type === 'password' ? 'Show' : 'Hide';
  });
  const forget = btn('Forget', 'i-x', 'ghost small');
  urlRow.append(url, show, forget);
  urlField.appendChild(urlRow);
  const urlHint = el('small', null, '');
  urlField.appendChild(urlHint);
  card.appendChild(urlField);

  // On/off
  const onRow = el('label', 'discord-toggle');
  const on = el('input');
  on.type = 'checkbox';
  onRow.append(on, el('span', null, 'Post new Chronicle events to Discord'));
  card.appendChild(onRow);

  // Event types
  const typesBox = el('fieldset', 'discord-types');
  typesBox.appendChild(el('legend', null, 'Which events'));
  const typesGrid = el('div', 'discord-type-grid');
  typesBox.appendChild(typesGrid);
  card.appendChild(typesBox);

  // Optional context
  const twin = el('div', 'discord-twin');
  const nameField = el('label', 'field');
  nameField.appendChild(el('span', null, 'Realm name in posts'));
  const realmName = el('input');
  realmName.maxLength = 60;
  realmName.placeholder = 'The Realm';
  nameField.appendChild(realmName);
  const siteField = el('label', 'field');
  siteField.appendChild(el('span', null, 'Website (optional)'));
  const portalUrl = el('input');
  portalUrl.maxLength = 200;
  portalUrl.spellcheck = false;
  portalUrl.placeholder = 'https://your-realm-site/';
  siteField.appendChild(portalUrl);
  siteField.appendChild(el('small', null, 'Each post links to that event on your Realm Portal (portal/README.md).'));
  twin.append(nameField, siteField);
  card.appendChild(twin);

  const actions = el('div', 'row gap');
  const save = btn('Save Discord settings', 'i-check', 'small');
  const test = btn('Send test', null, 'ghost small');
  actions.append(save, test);
  card.appendChild(actions);
  const stats = el('p', 'fine discord-stats', '');
  card.appendChild(stats);
  const how = el('details', 'discord-how');
  how.appendChild(el('summary', null, 'How do I get a webhook URL?'));
  const ol = el('ol');
  for (const s of [
    'In Discord, open the channel for announcements and choose Edit Channel (the gear).',
    'Integrations > Webhooks > New Webhook. Give it a name and avatar if you like.',
    'Copy Webhook URL, paste it above, then Save and Send test.',
    'Treat the URL like a password: anyone with it can post in that channel. It is kept only on this PC' + ' (encrypted by Windows when possible) and never shown again in full.'
  ]) ol.appendChild(el('li', null, s));
  how.appendChild(ol);
  card.appendChild(how);

  function renderTypes() {
    typesGrid.textContent = '';
    const chosen = new Set(state.types);
    let group = null;
    for (const t of state.allTypes) {
      if (t.group !== group) {
        group = t.group;
        typesGrid.appendChild(el('p', 'discord-group', group));
      }
      const lab = el('label', 'discord-type');
      const cb = el('input');
      cb.type = 'checkbox';
      cb.value = t.type;
      cb.checked = chosen.has(t.type);
      lab.append(cb, el('span', null, t.label));
      typesGrid.appendChild(lab);
    }
  }

  function render() {
    if (!state) return;
    pill.className = 'pill ' + (state.lastError ? 'bad' : state.enabled ? 'ok' : '');
    pill.textContent = state.lastError ? 'Problem' : state.enabled ? 'On' : 'Off';
    urlHint.className = state.savedButUnreadable ? 'bad' : '';
    urlHint.textContent = state.savedButUnreadable
      ? 'A webhook was saved under another Windows account or PC and cannot be read here. Paste it again.'
      : state.hasUrl
        ? 'Saved: ' + state.urlHint + (state.encrypted ? '. Encrypted on this PC.' : '. Stored on this PC (Windows encryption not available).')
        : 'Not set. Only discord.com / discordapp.com webhook URLs are accepted.';
    url.placeholder = state.hasUrl ? 'Saved. Paste a new URL to replace it.' : 'https://discord.com/api/webhooks/...';
    forget.disabled = !state.hasUrl;
    test.disabled = !state.hasUrl;
    on.checked = state.enabled;
    if (document.activeElement !== realmName) realmName.value = state.realmName || '';
    if (document.activeElement !== portalUrl) portalUrl.value = state.portalUrl || '';
    const bits = [];
    if (state.sent || state.failed || state.queued) bits.push(state.sent + ' posted', state.failed + ' failed', state.queued + ' waiting');
    if (state.dropped) bits.push(state.dropped + ' dropped (queue full)');
    if (state.lastSentAt) bits.push('last post ' + when(state.lastSentAt));
    if (state.lastError) bits.push('Last problem: ' + state.lastError);
    if (state.enabled && !state.watching) bits.push('No server data folder yet: events are relayed once the server writes its Chronicle.');
    stats.textContent = bits.join(' · ');
  }

  async function refresh(full) {
    try {
      state = await Dc.get();
      if (full) renderTypes();
      render();
    } catch (e) {
      stats.textContent = 'Could not read the Discord settings: ' + (e && e.message ? e.message : e);
    }
  }

  function payload() {
    const p = {
      enabled: on.checked,
      types: Array.from(typesGrid.querySelectorAll('input:checked')).map((c) => c.value),
      realmName: realmName.value,
      portalUrl: portalUrl.value
    };
    if (url.value.trim()) p.url = url.value.trim();
    return p;
  }

  save.addEventListener('click', busy(save, async () => {
    state = await Dc.save(payload());
    url.value = '';
    url.type = 'password';
    show.lastChild.textContent = 'Show';
    renderTypes();
    render();
    toast(state.enabled ? 'Discord herald is on. New Chronicle events will be posted.' : 'Discord settings saved. The herald is off.', 'ok');
  }));
  test.addEventListener('click', busy(test, async () => {
    if (url.value.trim()) {
      state = await Dc.save(payload());
      url.value = '';
      render();
    }
    await Dc.test();
    toast('Test message delivered. Check your Discord channel.', 'ok');
    refresh(false);
  }));
  forget.addEventListener('click', busy(forget, async () => {
    state = await Dc.forget();
    url.value = '';
    render();
    toast('Webhook forgotten. The Discord herald is off.', 'ok');
  }));

  function mount() {
    const grid = document.querySelector('.view-settings .settings-grid');
    if (!grid || card.isConnected) return;
    grid.appendChild(card);
    refresh(true);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount);
  else mount();
  // Keep the counters fresh while Settings is open.
  setInterval(() => {
    const view = card.closest('.view');
    if (card.isConnected && view && !view.hidden) refresh(false);
  }, 15000);
})();
