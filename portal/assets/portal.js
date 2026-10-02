// Realm Portal: small progressive enhancements. The pages are complete without this script.
// - live relative times and countdowns
// - status.json refresh (when served over http/https; the generator rewrites it on every build)
// - Chronicle filters (kind, house, text)
(function () {
  'use strict';

  function dur(ms) {
    var s = Math.max(0, Math.floor(ms / 1000));
    var d = Math.floor(s / 86400); s -= d * 86400;
    var h = Math.floor(s / 3600); s -= h * 3600;
    var m = Math.floor(s / 60);
    if (d > 0) return d + 'd ' + h + 'h';
    if (h > 0) return h + 'h ' + m + 'm';
    return m + 'm';
  }
  function rel(iso) {
    var t = Date.parse(iso);
    if (isNaN(t)) return null;
    var s = Math.round((Date.now() - t) / 1000);
    if (s < 0) return null;
    if (s < 60) return 'just now';
    if (s < 3600) return Math.floor(s / 60) + 'm ago';
    if (s < 86400) return Math.floor(s / 3600) + 'h ago';
    if (s < 86400 * 14) return Math.floor(s / 86400) + 'd ago';
    return null; // older: keep the absolute date
  }

  function tick() {
    var now = Date.now();
    document.querySelectorAll('[data-since]').forEach(function (n) {
      var t = Date.parse(n.getAttribute('data-since'));
      if (!isNaN(t)) n.textContent = dur(now - t);
    });
    document.querySelectorAll('[data-rel]').forEach(function (n) {
      if (!n.title) n.title = n.textContent;
      var r = rel(n.getAttribute('data-rel'));
      if (r) n.textContent = r;
    });
    document.querySelectorAll('[data-at]').forEach(function (n) {
      if (!n.title) n.title = n.textContent;
      var at = Date.parse(n.getAttribute('data-at'));
      var end = Date.parse(n.getAttribute('data-end') || '');
      if (isNaN(at)) return;
      if (at > now) n.textContent = 'in ' + dur(at - now);
      else if (!isNaN(end) && end > now) n.textContent = 'open, ' + dur(end - now) + ' left';
      else n.textContent = n.title;
    });
  }

  function refreshStatus() {
    if (!/^https?:$/.test(location.protocol) || !document.querySelector('[data-status]')) return;
    var root = document.body.getAttribute('data-root') || './';
    fetch(root + 'status.json', { cache: 'no-store' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (s) {
        if (!s) return;
        var set = function (sel, v) { var n = document.querySelector(sel); if (n && v != null) n.textContent = String(v); };
        set('[data-status-online]', s.online);
        set('[data-status-max]', s.maxPlayers || '?');
        if (s.king) set('[data-status-king]', s.king);
        var pill = document.querySelector('[data-status]');
        pill.classList.toggle('stale', !!s.stale);
        set('[data-status-text]', s.stale ? 'Last word from the realm' : 'The realm stirs');
        var up = document.querySelector('[data-status-updated]');
        if (up && s.updated) up.setAttribute('data-rel', s.updated);
        tick();
      })
      .catch(function () { /* offline or file:// - keep what the page was built with */ });
  }

  function filters() {
    var box = document.querySelector('[data-filters]');
    var list = document.querySelector('[data-events]');
    if (!box || !list) return;
    box.hidden = false;
    var items = Array.prototype.slice.call(list.children);
    var group = '';
    var house = box.querySelector('[data-filter-house]');
    var text = box.querySelector('[data-filter-text]');
    var count = box.querySelector('[data-filter-count]');
    var empty = document.querySelector('[data-filter-empty]');
    var chips = box.querySelectorAll('[data-filter-group]');
    var params = new URLSearchParams(location.search);
    if (params.get('house')) house.value = params.get('house').toLowerCase();
    if (params.get('kind')) group = params.get('kind');
    if (params.get('q')) text.value = params.get('q');
    function apply() {
      var h = house.value;
      var q = text.value.trim().toLowerCase();
      var shown = 0;
      items.forEach(function (li) {
        var ok = (!group || li.getAttribute('data-group') === group) &&
          (!h || ('|' + li.getAttribute('data-houses') + '|').indexOf('|' + h + '|') !== -1) &&
          (!q || li.getAttribute('data-text').indexOf(q) !== -1);
        li.hidden = !ok;
        if (ok) shown++;
      });
      chips.forEach(function (c) { c.setAttribute('aria-pressed', String(c.getAttribute('data-filter-group') === group)); });
      count.textContent = shown + ' of ' + items.length + ' entries';
      if (empty) empty.hidden = shown !== 0;
    }
    chips.forEach(function (c) {
      c.addEventListener('click', function () { group = c.getAttribute('data-filter-group'); apply(); });
    });
    house.addEventListener('change', apply);
    text.addEventListener('input', apply);
    apply();
  }

  document.addEventListener('DOMContentLoaded', function () {
    tick();
    filters();
    refreshStatus();
    setInterval(tick, 30000);
    setInterval(refreshStatus, 60000);
  });
})();
