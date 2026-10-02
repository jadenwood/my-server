// HTML templates for the portal. Every dynamic value goes through esc(); markdown goes through
// lib/markdown.mjs, which escapes all source text. No inline scripts or style attributes, so the
// pages run under a strict Content-Security-Policy (house colours live in assets/houses.css).

import { esc, fmtDate, duration, houseLabel, monogram, safeUrl } from './util.mjs';
import { GROUPS } from './model.mjs';
import { inline } from './markdown.mjs';

// Lore fields are short Markdown fragments ("*them*", `#4a2347`); render them inline, links dropped.
const md = (s) => inline(String(s || ''), () => null);

const ICON_PATHS = {
  crown: 'M3 18h18M4 18 3 7l5 4 4-7 4 7 5-4-1 11M12 13.5v.01',
  crownX: 'M3 18h18M4 18 3 7l5 4 4-7 4 7 5-4-1 11M9 21l6-6M15 21l-6-6',
  flag: 'M5 21V3M5 4h11l-2 4 2 4H5',
  swords: 'M4 4l9 9M4 4h3l9 9M4 4v3l9 9M14 17l3 3M17 14l3 3M20 4l-7 7M20 4h-3l-4 4M20 4v3l-4 4M10 14l-3 3M7 20l-3-3',
  sheath: 'M12 2v4M9 6h6M10 6v14l2 2 2-2V6',
  shield: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6zM12 8v8M8 12h8',
  oath: 'M7 11V6a2 2 0 0 1 4 0v5M11 10V4a2 2 0 0 1 4 0v7M15 9a2 2 0 0 1 4 0v4c0 4-3 8-7 8s-6-2-7-5l-2-4a2 2 0 0 1 3-2l2 2',
  chain: 'M9 15l6-6M8 11l-2 2a3.5 3.5 0 0 0 5 5l2-2M16 13l2-2a3.5 3.5 0 0 0-5-5l-2 2',
  chainX: 'M8 11l-2 2a3.5 3.5 0 0 0 5 5l2-2M16 13l2-2a3.5 3.5 0 0 0-5-5l-2 2M4 4l3 3M20 20l-3-3',
  seal: 'M12 3a6 6 0 1 0 0 12 6 6 0 0 0 0-12zM12 6.5v5M9.5 9h5M8 14l-2 7 6-3 6 3-2-7',
  scroll: 'M6 4h11a2 2 0 0 1 2 2v12a2 2 0 0 0 2 2H8a2 2 0 0 1-2-2V4zM6 4a2 2 0 0 0-2 2v2h2M10 9h6M10 13h6',
  scrollX: 'M6 4h11a2 2 0 0 1 2 2v12a2 2 0 0 0 2 2H8a2 2 0 0 1-2-2V4zM6 4a2 2 0 0 0-2 2v2h2M10 9l6 5M16 9l-6 5',
  coins: 'M8 7a5 2 0 1 0 10 0 5 2 0 1 0-10 0M8 7v4c0 1.1 2.2 2 5 2s5-.9 5-2V7M6 12a5 2 0 1 0 10 0M6 12v4c0 1.1 2.2 2 5 2s5-.9 5-2v-3',
  people: 'M9 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM3 20c0-3 2.7-5 6-5s6 2 6 5M16 5a3 3 0 0 1 0 6M21 20c0-2.5-1.7-4.3-4-4.8',
  hourglass: 'M6 3h12M6 21h12M7 3c0 5 10 5 10 9s-10 4-10 9M17 3c0 5-10 5-10 9s10 4 10 9',
  download: 'M12 3v12M7 10l5 5 5-5M4 19h16',
  play: 'M7 4l13 8-13 8z',
  book: 'M4 5a2 2 0 0 1 2-2h13v16H6a2 2 0 0 0-2 2zM4 5v16M8 7h7',
  cast: 'M3 8V6a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-6M3 12a8 8 0 0 1 8 8M3 16a4 4 0 0 1 4 4M3 20h.01',
  rss: 'M5 19h.01M5 12a7 7 0 0 1 7 7M5 5a14 14 0 0 1 14 14',
  trophy: 'M8 4h8v5a4 4 0 0 1-8 0zM8 6H5a3 3 0 0 0 3 4M16 6h3a3 3 0 0 1-3 4M12 13v4M8 21h8M9 17h6',
  horn: 'M4 10v4l3 1 11 5V4L7 9zM7 9v6M18 9a3 3 0 0 1 0 6',
};

export function icon(name, cls = 'icon') {
  return `<svg class="${cls}" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="${ICON_PATHS[name] || ICON_PATHS.scroll}"/></svg>`;
}

// Swallow-tailed house banner as inline SVG. Colours come from the .dye-<slug> class (houses.css).
export function banner(house, size = 'md') {
  const name = house.name;
  return `<div class="banner banner-${size} dye-${esc(house.slug)}" title="${esc(houseLabel(name))}${house.sigil ? ` (${esc(house.sigil)})` : ''}">
  <svg viewBox="0 0 120 170" aria-hidden="true">
    <rect class="pole" x="2" y="2" width="116" height="9" rx="4"/>
    <path class="cloth" d="M10 9h100v150l-50-26-50 26z"/>
    <path class="hem" d="M16 9v140l44-22 44 22V9"/>
    <circle class="boss" cx="60" cy="62" r="30"/>
    <text class="mono" x="60" y="75" text-anchor="middle">${esc(monogram(name))}</text>
  </svg>
  ${house.sigil ? `<span class="sigil-name">${esc(house.sigil)}</span>` : ''}
</div>`;
}

const NAV = [
  { id: 'home', href: 'index.html', label: 'The Realm' },
  { id: 'chronicle', href: 'chronicle.html', label: 'The Chronicle' },
  { id: 'houses', href: 'houses.html', label: 'Great Houses' },
  { id: 'kings', href: 'kings.html', label: 'Hall of Kings' },
  { id: 'play', href: 'play.html', label: 'How to play' },
  { id: 'streamers', href: 'streamers.html', label: 'Streamers' },
  { id: 'download', href: 'download.html', label: 'Download', cta: true },
];

export function layout({ site, title, active, body, depth = 0, description = '' }) {
  const up = depth ? '../'.repeat(depth) : '';
  const pageTitle = title ? `${title} · ${site.realmName}` : site.realmName;
  const nav = NAV.map((n) => `<a href="${up}${n.href}" class="${n.cta ? 'nav-cta' : ''}${n.id === active ? ' active' : ''}"${n.id === active ? ' aria-current="page"' : ''}>${esc(n.label)}</a>`).join('');
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'self'; img-src 'self' data:; style-src 'self'; font-src 'self'; script-src 'self'; connect-src 'self'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'">
<meta name="referrer" content="no-referrer">
<title>${esc(pageTitle)}</title>
<meta name="description" content="${esc(description || site.tagline)}">
<meta property="og:title" content="${esc(pageTitle)}">
<meta property="og:description" content="${esc(description || site.tagline)}">
<meta name="theme-color" content="#131417">
<link rel="icon" href="${up}assets/favicon.svg" type="image/svg+xml">
<link rel="alternate" type="application/atom+xml" title="${esc(site.realmName)} Chronicle" href="${up}feed.xml">
<link rel="stylesheet" href="${up}assets/portal.css">
<link rel="stylesheet" href="${up}assets/houses.css">
<script src="${up}assets/portal.js" defer></script>
</head>
<body class="page-${esc(active)}" data-root="${up || './'}">
<a class="skip" href="#main">Skip to content</a>
<header class="topbar">
  <div class="wrap topbar-in">
    <a class="brand" href="${up}index.html">${icon('crown', 'brand-ico')}<span>${esc(site.realmName)}</span></a>
    <input type="checkbox" id="nav-toggle" class="nav-toggle" aria-label="Menu">
    <label for="nav-toggle" class="nav-burger" aria-hidden="true"><span></span><span></span><span></span></label>
    <nav class="nav" aria-label="Main">${nav}</nav>
  </div>
</header>
<main id="main">
${body}
</main>
<footer class="footer">
  <div class="wrap footer-in">
    <p class="footer-mark">${icon('crown')} ${esc(site.realmName)}</p>
    <p class="fine">A community server for <em>Reign of Kings</em>. Play with your own Steam copy of the game; nothing here modifies it. Not affiliated with the game's developer or publisher. Lore and art are original to this community.</p>
    <p class="fine">Built ${esc(fmtDate(new Date(site.generatedAt).toISOString()))} · <a href="${up}feed.xml">${icon('rss')} Chronicle feed</a> · <a href="${up}status.json">status.json</a>${site.discordInvite ? ` · <a href="${esc(site.discordInvite)}" rel="noopener" target="_blank">Discord</a>` : ''}</p>
  </div>
</footer>
</body>
</html>
`;
}

function houseLink(name, model, up = '') {
  const h = model.houses.find((x) => x.name.toLowerCase() === String(name || '').toLowerCase());
  if (!h) return esc(houseLabel(name));
  return `<a class="house-link dye-${esc(h.slug)}" href="${up}houses/${esc(h.slug)}.html"><i class="swatch"></i>${esc(houseLabel(h.name))}</a>`;
}

function eventItem(e, model, up = '', { compact = false } = {}) {
  const groups = e.meta.group;
  const houses = (e.houses || []).map((n) => n.toLowerCase()).join('|');
  const text = `${e.title} ${e.detail} ${e.actors.join(' ')}`.toLowerCase();
  return `<li class="ev tone-${esc(e.meta.tone)}" data-group="${esc(groups)}" data-houses="${esc(houses)}" data-text="${esc(text)}" id="e${e.id}">
  <span class="ev-ico">${icon(e.meta.icon)}</span>
  <div class="ev-body">
    <p class="ev-kicker"><span class="ev-type">${esc(e.meta.label)}</span>${e.ts ? `<time datetime="${esc(e.ts)}" data-rel="${esc(e.ts)}">${esc(fmtDate(e.ts))}</time>` : ''}</p>
    <h3 class="ev-title">${esc(e.title)}</h3>
    ${e.detail ? `<p class="ev-detail">${esc(e.detail)}</p>` : ''}
    ${!compact && (e.houses || []).length ? `<p class="ev-houses">${e.houses.map((n) => houseLink(n, model, up)).join(' ')}</p>` : ''}
  </div>
</li>`;
}

function countdown(at, end) {
  return `<span class="countdown" data-at="${esc(at)}"${end ? ` data-end="${esc(end)}"` : ''}>${esc(fmtDate(at))}</span>`;
}

function upcomingList(model, emptyText) {
  if (!model.upcoming.length) return `<p class="empty">${esc(emptyText)}</p>`;
  const ico = { war: 'swords', season: 'hourglass', treaty: 'seal', herald: 'flag', event: 'horn' };
  return `<ol class="upcoming">${model.upcoming.map((u) => `<li class="up up-${esc(u.kind)}${u.live ? ' live' : ''}">
  <span class="up-ico">${icon(ico[u.kind] || 'flag')}</span>
  <div><h3>${esc(u.title)}</h3>${u.detail ? `<p>${esc(u.detail)}</p>` : ''}
  <p class="when">${u.live ? '<b class="live-tag">Now</b> ' : ''}${countdown(u.at, u.end)}</p></div>
</li>`).join('')}</ol>`;
}

// ---------------------------------------------------------------- home

export function homePage(model, site, news) {
  const s = model.state;
  const crownHouse = s.house ? model.houses.find((h) => h.name.toLowerCase() === s.house.toLowerCase()) : null;
  const status = `<p class="status-pill${model.stale ? ' stale' : ''}" data-status>
  <i class="dot"></i><span data-status-text>${model.stale ? 'Last word from the realm' : 'The realm stirs'}</span>
  <b data-status-online>${s.online}</b>/<span data-status-max>${s.maxPlayers || '?'}</span> in the realm
  ${s.updated ? `<span class="fine">· <time data-rel="${esc(s.updated)}" data-status-updated>${esc(fmtDate(s.updated))}</time></span>` : ''}
</p>`;
  const crownCard = `<section class="crown-card" aria-labelledby="crown-h">
  ${crownHouse ? banner(crownHouse, 'lg') : `<div class="crown-empty">${icon('crown', 'big-ico')}</div>`}
  <div class="crown-body">
    <p class="eyebrow" id="crown-h">The Old Throne</p>
    ${s.king
      ? `<h2 class="king" data-status-king>${esc(s.king)}</h2>
         <p class="reign">${s.house ? `of ${houseLink(s.house, model)} · ` : ''}reigning <b data-since="${esc(s.since || '')}">${esc(duration(model.now - Date.parse(s.since)))}</b></p>`
      : `<h2 class="king" data-status-king>The throne stands empty</h2><p class="reign">Anyone may sit the Old Throne while it is empty. Claims are not needed.</p>`}
    ${model.crown.council.length ? `<p class="council">${model.crown.council.map((c) => `<span><em>${esc(c.seat)}</em> ${esc(c.name)}</span>`).join('')}</p>` : ''}
  </div>
</section>`;
  const latest = model.events.slice(-6).reverse();
  const houses = model.houses.slice(0, 8);
  const newsHtml = (news || []).slice(0, 3).map((n) => `<article class="news"><p class="eyebrow">${esc(fmtDate(n.date + 'T00:00:00Z', false))}</p><h3>${esc(n.title)}</h3><p>${esc(n.body)}</p></article>`).join('');
  const season = model.seasons.current
    ? `<section class="season-band"><p class="eyebrow">${model.seasons.current.number != null ? `Season ${esc(model.seasons.current.number)}` : 'This season'}</p><h2>${esc(model.seasons.current.name)}</h2>${model.seasons.current.theme ? `<p>${esc(model.seasons.current.theme)}</p>` : ''}${model.seasons.current.endsAt ? `<p class="when">Ends ${countdown(model.seasons.current.endsAt)}</p>` : ''}${(model.seasons.standings || []).length ? `<ol class="podium">${model.seasons.standings.slice(0, 3).map((r) => `<li>${icon('trophy')} ${houseLink(r.house, model)} <b>${esc(r.score)}</b></li>`).join('')}</ol><p class="fine"><a href="houses.html">Full standings →</a></p>` : ''}</section>`
    : '';
  const body = `
<section class="hero">
  <div class="wrap hero-in">
    <div class="hero-copy">
      <p class="kicker">${esc(site.kicker)}</p>
      <h1 class="gold">${esc(site.realmName)}</h1>
      <p class="lead">${esc(site.tagline)}</p>
      ${status}
      <p class="cta-row">
        <a class="btn primary" href="download.html">${icon('download')} Get the Realm app</a>
        <a class="btn" href="play.html">${icon('book')} How to play</a>
      </p>
    </div>
    ${crownCard}
  </div>
</section>
<div class="wrap">
  ${season}
  <div class="grid-2">
    <section class="panel" aria-labelledby="up-h">
      <header class="panel-head"><h2 id="up-h">${icon('hourglass')} What comes next</h2></header>
      ${upcomingList(model, 'No claims are declared and no windows are set. The realm is quiet, for now.')}
    </section>
    <section class="panel" aria-labelledby="latest-h">
      <header class="panel-head"><h2 id="latest-h">${icon('scroll')} Lately in the Chronicle</h2><a class="more" href="chronicle.html">The full Chronicle →</a></header>
      ${latest.length ? `<ol class="events compact">${latest.map((e) => eventItem(e, model, '', { compact: true })).join('')}</ol>` : '<p class="empty">The Chronicle is still blank. Its first page is written when the first house is founded.</p>'}
    </section>
  </div>
  <section class="panel" aria-labelledby="houses-h">
    <header class="panel-head"><h2 id="houses-h">${icon('shield')} The Great Houses</h2><a class="more" href="houses.html">All houses →</a></header>
    ${houses.length ? `<div class="banner-row">${houses.map((h) => `<a class="banner-link" href="houses/${esc(h.slug)}.html">${banner(h, 'sm')}<span class="banner-name">${esc(h.name)}</span><span class="fine">${h.members} sworn</span></a>`).join('')}</div>` : '<p class="empty">No house has been founded yet.</p>'}
  </section>
  <div class="grid-3 stats">
    <div class="stat"><b>${model.houses.length}</b><span>houses</span></div>
    <div class="stat"><b>${model.reigns.length}</b><span>reigns recorded</span></div>
    <div class="stat"><b>${model.events.length}</b><span>entries in the Chronicle</span></div>
  </div>
  ${newsHtml ? `<section class="panel" aria-labelledby="news-h"><header class="panel-head"><h2 id="news-h">${icon('flag')} Heralds' news</h2></header><div class="news-grid">${newsHtml}</div></section>` : ''}
</div>`;
  return layout({ site, title: '', active: 'home', body, description: s.king ? `${s.king} holds the Old Throne. ${site.tagline}` : site.tagline });
}

// ---------------------------------------------------------------- chronicle

export function chroniclePage(model, site) {
  const present = new Set(model.events.map((e) => e.meta.group));
  const chips = GROUPS.filter((g) => present.has(g.id))
    .map((g) => `<button type="button" class="chip" data-filter-group="${esc(g.id)}" aria-pressed="false">${esc(g.label)}</button>`).join('');
  const houseOpts = model.houses.map((h) => `<option value="${esc(h.name.toLowerCase())}">${esc(houseLabel(h.name))}</option>`).join('');
  const list = model.events.slice().reverse();
  const body = `
<section class="page-head"><div class="wrap">
  <p class="kicker">Being a true account of the realm</p>
  <h1 class="gold">The Chronicle</h1>
  <p class="lead">Every coronation, oath, betrayal, treaty, decree, claim, rebellion and ransom, as it was written. Public names and public acts only.</p>
</div></section>
<div class="wrap">
  <div class="filters" data-filters hidden>
    <div class="chips" role="group" aria-label="Kinds of event"><button type="button" class="chip" data-filter-group="" aria-pressed="true">Everything</button>${chips}</div>
    <div class="filter-row">
      <label class="field"><span>House</span><select data-filter-house><option value="">Any house</option>${houseOpts}</select></label>
      <label class="field grow"><span>Search</span><input type="search" data-filter-text placeholder="A name, a house, a deed..." maxlength="60"></label>
    </div>
    <p class="fine" data-filter-count>${list.length} entries</p>
  </div>
  ${list.length ? `<ol class="events timeline" data-events>${list.map((e) => eventItem(e, model)).join('')}</ol><p class="empty" data-filter-empty hidden>Nothing in the Chronicle matches. Try fewer filters.</p>` : '<p class="empty">The Chronicle is still blank.</p>'}
</div>`;
  return layout({ site, title: 'The Chronicle', active: 'chronicle', body, description: 'The complete history of the realm: crowns, oaths, betrayals and wars.' });
}

// ---------------------------------------------------------------- houses

function ordinal(n) {
  const s = ['th', 'st', 'nd', 'rd'];
  const v = n % 100;
  return n + (s[(v - 20) % 10] || s[v] || s[0]);
}

function marks(h) {
  const parts = [];
  if (h.oathsBroken) parts.push(`${h.oathsBroken} oath${h.oathsBroken === 1 ? '' : 's'} broken`);
  if (h.treatiesBroken) parts.push(`${h.treatiesBroken} treat${h.treatiesBroken === 1 ? 'y' : 'ies'} broken`);
  return parts;
}

export function standingsTable(model, up = '', limit = 50) {
  const rows = (model.seasons.standings || []).slice(0, limit);
  if (!rows.length) return '';
  return `<div class="table-wrap standings"><table>
<thead><tr><th>#</th><th>House</th><th>Points</th><th>Crown days</th><th>Rebellions won / held</th><th>Treaties kept / broken</th><th>Oaths broken</th><th>Contracts</th><th>Event points</th></tr></thead>
<tbody>${rows.map((r) => `<tr${r.rank === 1 ? ' class="lead-row"' : ''}><td>${r.rank === 1 ? icon('trophy') : esc(r.rank)}</td><td>${houseLink(r.house, model, up)}</td><td><b>${esc(r.score)}</b></td><td>${esc(r.crownDays)}</td><td>${esc(r.rebellionsWon)} / ${esc(r.rebellionsDefended)}</td><td>${esc(r.treatiesKept)} / ${esc(r.treatiesBroken)}</td><td>${esc(r.oathsBroken)}</td><td>${esc(r.contractsFulfilled)}</td><td>${esc(r.eventPoints)}</td></tr>`).join('')}</tbody>
</table></div>
<p class="fine">Points use RealmSeasons' default weights (crown day 10, rebellion won 25, held 15, treaty kept 5, treaty or oath broken -10, contract 2). The server's own settings may weigh them differently; <code>/season standings</code> in game is the final word.</p>`;
}

export function housesPage(model, site) {
  const cards = model.houses.map((h) => `<a class="house-card dye-${esc(h.slug)}${h.isCrown ? ' crowned' : ''}" href="houses/${esc(h.slug)}.html">
  ${banner(h, 'md')}
  <div class="house-card-body">
    <h2>${esc(houseLabel(h.name))}${h.isCrown ? ` <span class="badge gold-badge">${icon('crown')} Holds the crown</span>` : ''}</h2>
    ${h.lore && h.lore.words ? `<p class="words">${md(h.lore.words.replace(/[*"]/g, ''))}</p>` : ''}
    <ul class="facts">
      <li>${icon('people')} ${h.members} sworn</li>
      ${h.liege ? `<li>${icon('oath')} Sworn to House ${esc(h.liege.replace(/^house\s+/i, ''))}</li>` : ''}
      ${h.vassals.length ? `<li>${icon('shield')} ${h.vassals.length} vassal house${h.vassals.length === 1 ? '' : 's'}</li>` : ''}
      ${h.crowns ? `<li>${icon('crown')} ${h.crowns} reign${h.crowns === 1 ? '' : 's'}</li>` : ''}
      ${h.standing ? `<li>${icon('trophy')} ${ordinal(h.standing.rank)} this season · ${esc(h.standing.score)} pts</li>` : ''}
      ${marks(h).map((m) => `<li class="mark">${icon('chainX')} ${esc(m)}</li>`).join('')}
    </ul>
  </div>
</a>`).join('');
  const body = `
<section class="page-head"><div class="wrap">
  <p class="kicker">Banners of Ostreval</p>
  <h1 class="gold">The Great Houses</h1>
  <p class="lead">Every house of the realm, its sigil, its sworn swords and the oaths that bind it. Found your own in game with <code>/house found "&lt;name&gt;" &lt;sigil&gt;</code>.</p>
</div></section>
<div class="wrap">
  ${model.seasons.standings && model.seasons.standings.length ? `<section class="panel"><header class="panel-head"><h2>${icon('trophy')} ${esc(model.seasons.current ? model.seasons.current.name : 'Season')} standings</h2></header>${standingsTable(model)}</section>` : ''}
  ${model.houses.length ? `<div class="house-grid">${cards}</div>` : '<p class="empty">No house has been founded yet. Be the first.</p>'}
</div>`;
  return layout({ site, title: 'Great Houses', active: 'houses', body, description: 'The great houses of the realm, their sigils, oaths and treaties.' });
}

export function housePage(h, model, site) {
  const up = '../';
  const l = h.lore || {};
  const now = model.now;
  const treaties = h.treaties.slice().sort((a, b) => Number(b.active) - Number(a.active) || Date.parse(b.signed || 0) - Date.parse(a.signed || 0));
  const reigns = model.reigns.filter((r) => r.house && r.house.toLowerCase() === h.name.toLowerCase());
  const events = h.events.slice().reverse().slice(0, 60);
  const body = `
<section class="page-head house-head dye-${esc(h.slug)}"><div class="wrap house-head-in">
  ${banner(h, 'xl')}
  <div>
    <p class="kicker">${l.epithet ? esc(l.epithet) : 'A house of the realm'}</p>
    <h1 class="gold">${esc(houseLabel(h.name))}</h1>
    ${l.words ? `<p class="words big">${md(l.words.replace(/[*"]/g, ''))}</p>` : ''}
    <ul class="facts inline">
      <li>${icon('people')} ${h.members} sworn</li>
      ${h.founded ? `<li>${icon('shield')} Founded ${esc(fmtDate(h.founded, false))}</li>` : ''}
      ${h.isCrown ? `<li class="gold-li">${icon('crown')} Holds the crown</li>` : ''}
      ${h.crowns ? `<li>${icon('crown')} ${h.crowns} reign${h.crowns === 1 ? '' : 's'}</li>` : ''}
    </ul>
  </div>
</div></section>
<div class="wrap">
  <div class="grid-2">
    <section class="panel">
      <header class="panel-head"><h2>${icon('oath')} Oaths</h2></header>
      <dl class="dl">
        <dt>Liege</dt><dd>${h.liege ? `${houseLink(h.liege, model, up)}${h.swornSince ? ` <span class="fine">since ${esc(fmtDate(h.swornSince, false))}</span>` : ''}` : 'None. This house answers to no one.'}</dd>
        <dt>Vassals</dt><dd>${h.vassals.length ? h.vassals.map((v) => houseLink(v, model, up)).join(' ') : 'None sworn.'}</dd>
        ${h.leader ? `<dt>Leader</dt><dd>${esc(h.leader)}</dd>` : ''}
        ${h.officers.length ? `<dt>Officers</dt><dd>${h.officers.map(esc).join(', ')}</dd>` : ''}
        <dt>Marks</dt><dd>${marks(h).length ? `<span class="mark">${esc(marks(h).join(' · '))}</span>` : 'No broken oaths or treaties.'}</dd>
        ${h.sigil ? `<dt>Sigil</dt><dd>${esc(h.sigil)}${l.sigil ? `<br><span class="fine">${md(l.sigil)}</span>` : ''}</dd>` : ''}
        ${l.colours ? `<dt>Colours</dt><dd>${md(l.colours)}</dd>` : ''}
        ${l.seat ? `<dt>Seat</dt><dd>${md(l.seat)}</dd>` : ''}
      </dl>
    </section>
    <section class="panel">
      <header class="panel-head"><h2>${icon('seal')} Treaties</h2></header>
      ${treaties.length ? `<ul class="treaties">${treaties.map((t) => `<li class="${t.active ? 'active' : 'lapsed'}">${houseLink(t.with, model, up)} <span class="fine">${t.active ? (t.expires ? `until ${esc(fmtDate(t.expires))}` : 'standing') : `lapsed ${esc(fmtDate(t.expires || t.signed || '', false))}`}</span></li>`).join('')}</ul>` : '<p class="empty">No treaties on record.</p>'}
      ${l.history ? `<h3 class="sub">History</h3><p>${md(l.history)}</p>` : ''}
      ${l.hook ? `<h3 class="sub">Who it suits</h3><p>${md(l.hook)}</p>` : ''}
    </section>
  </div>
  ${h.standing ? `<section class="panel"><header class="panel-head"><h2>${icon('trophy')} This season</h2><span class="fine">${ordinal(h.standing.rank)} of ${model.seasons.standings.length}</span></header>
    <div class="season-stats">
      <div class="stat"><b>${esc(h.standing.score)}</b><span>points</span></div>
      <div class="stat"><b>${esc(h.standing.crownDays)}</b><span>crown days</span></div>
      <div class="stat"><b>${esc(h.standing.rebellionsWon + h.standing.rebellionsDefended)}</b><span>rebellions won or held</span></div>
      <div class="stat"><b>${esc(h.standing.treatiesKept)}</b><span>treaties kept</span></div>
      <div class="stat"><b>${esc(h.standing.contractsFulfilled)}</b><span>contracts</span></div>
      <div class="stat"><b>${esc(h.standing.eventPoints)}</b><span>event points</span></div>
    </div>
    ${h.standing.honours.length ? `<h3 class="sub">Honours</h3><ul class="honours">${h.standing.honours.map((x) => `<li>${icon('trophy')} ${esc(x)}</li>`).join('')}</ul>` : ''}
  </section>` : ''}
  ${reigns.length ? `<section class="panel"><header class="panel-head"><h2>${icon('crown')} Monarchs of this house</h2></header><ol class="reign-list">${reigns.map((r) => reignRow(r, model, up, now)).join('')}</ol></section>` : ''}
  <section class="panel">
    <header class="panel-head"><h2>${icon('scroll')} In the Chronicle</h2><a class="more" href="${up}chronicle.html">The full Chronicle →</a></header>
    ${events.length ? `<ol class="events">${events.map((e) => eventItem(e, model, up)).join('')}</ol>` : '<p class="empty">This house has not yet entered the Chronicle.</p>'}
  </section>
</div>`;
  return layout({ site, title: houseLabel(h.name), active: 'houses', body, depth: 1, description: `${houseLabel(h.name)}${h.sigil ? `, the ${h.sigil}` : ''}: ${h.members} sworn.` });
}

// ---------------------------------------------------------------- kings

function reignRow(r, model, up, now) {
  const how = r.endingText || { abdicated: 'fell or left the throne', succeeded: 'was succeeded', vacant: 'the throne went empty', overthrown: 'overthrown by rebellion', wiped: 'cut short by a wipe' }[r.endedBy] || '';
  return `<li class="reign-row${r.current ? ' current' : ''}">
  <span class="reign-crown">${icon(r.current ? 'crown' : 'crownX')}</span>
  <div>
    <h3>${esc(r.king)}${r.house ? ` <span class="of">of</span> ${houseLink(r.house, model, up)}` : ''}${r.season ? ` <span class="badge season-badge">Season ${esc(r.season)}</span>` : ''}</h3>
    <p class="fine">${r.start ? esc(fmtDate(r.start)) : 'Unknown start'} → ${r.current ? '<b>reigns still</b>' : esc(r.end ? fmtDate(r.end) : 'unknown')}${how ? ` · ${esc(how)}` : ''}</p>
  </div>
  <div class="reign-stats">
    <b${r.current && r.start ? ` data-since="${esc(r.start)}"` : ''}>${esc(r.ms != null ? duration(r.ms) : '?')}</b>
    <span class="fine">${r.decrees} decree${r.decrees === 1 ? '' : 's'} · ${r.rebellions} rebellion${r.rebellions === 1 ? '' : 's'} weathered</span>
  </div>
</li>`;
}

export function kingsPage(model, site) {
  const rec = model.records;
  const reigns = model.reigns.slice().reverse();
  const card = (title, value, sub, ico) => `<div class="record">${icon(ico, 'rec-ico')}<p class="eyebrow">${esc(title)}</p><p class="rec-value">${value}</p>${sub ? `<p class="fine">${sub}</p>` : ''}</div>`;
  const records = [
    rec.longest ? card('Longest reign', esc(rec.longest.king), `${esc(duration(rec.longest.ms))}${rec.longest.house ? ` · ${esc(houseLabel(rec.longest.house))}` : ''}`, 'hourglass') : '',
    rec.mostCrownedHouse ? card('Most crowned house', esc(houseLabel(rec.mostCrownedHouse[0])), `${rec.mostCrownedHouse[1]} reign${rec.mostCrownedHouse[1] === 1 ? '' : 's'}`, 'shield') : '',
    rec.mostCrownedKing && rec.mostCrownedKing[1] > 1 ? card('Crowned most often', esc(rec.mostCrownedKing[0]), `${rec.mostCrownedKing[1]} times`, 'crown') : '',
    rec.faithless ? card('Most faithless', esc(houseLabel(rec.faithless.name)), esc(marks(rec.faithless).join(' · ')), 'chainX') : '',
  ].filter(Boolean).join('');
  const past = model.seasons.past.length
    ? `<section class="panel"><header class="panel-head"><h2>${icon('hourglass')} Seasons past</h2></header><ol class="reign-list">${model.seasons.past.map((s) => `<li class="reign-row"><span class="reign-crown">${icon('trophy')}</span><div><h3>${esc(s.name)}</h3><p class="fine">${esc(fmtDate(s.startedAt || '', false))} → ${esc(fmtDate(s.endsAt || '', false))}</p></div><div class="reign-stats">${s.championHouse ? `<b>${icon('trophy')} ${esc(houseLabel(s.championHouse))}</b>` : s.champion ? `<b>${esc(s.champion)}</b>` : ''}${s.championScore != null ? `<span class="fine">${esc(s.championScore)} points</span>` : ''}${s.longestReign ? `<span class="fine">Longest reign: ${esc(s.longestReign)}</span>` : ''}</div></li>`).join('')}</ol></section>`
    : '';
  const body = `
<section class="page-head"><div class="wrap">
  <p class="kicker">Those who sat the Old Throne</p>
  <h1 class="gold">The Hall of Kings</h1>
  <p class="lead">The crown belongs to the seat, not the blood. Here is everyone who has held it, for how long, and how it ended. Rebuilt from the Chronicle, so reigns older than its retention may be missing.</p>
</div></section>
<div class="wrap">
  ${records ? `<div class="records">${records}</div>` : ''}
  <section class="panel">
    <header class="panel-head"><h2>${icon('crown')} Every reign</h2><span class="fine">${model.reigns.length} recorded</span></header>
    ${reigns.length ? `<ol class="reign-list">${reigns.map((r) => reignRow(r, model, '', model.now)).join('')}</ol>` : '<p class="empty">No one has yet been crowned. The Old Throne waits.</p>'}
  </section>
  ${past}
</div>`;
  return layout({ site, title: 'Hall of Kings', active: 'kings', body, description: 'Every monarch of the realm, their reigns and records.' });
}

// ---------------------------------------------------------------- docs (how to play, rules, lore, streamers)

const DOC_TABS = [
  { id: 'play', href: 'play.html', label: 'How to play' },
  { id: 'rules', href: 'rules.html', label: 'Rules' },
  { id: 'lore', href: 'lore.html', label: 'Lore' },
];

export function docPage({ site, id, title, kicker, rendered, active = 'play', extraTop = '' }) {
  const toc = rendered.headings.filter((h) => h.level === 2);
  const tabs = DOC_TABS.some((t) => t.id === id)
    ? `<nav class="doc-tabs" aria-label="Codex">${DOC_TABS.map((t) => `<a href="${t.href}"${t.id === id ? ' class="active" aria-current="page"' : ''}>${esc(t.label)}</a>`).join('')}</nav>`
    : '';
  const body = `
<section class="page-head"><div class="wrap">
  <p class="kicker">${esc(kicker)}</p>
  <h1 class="gold">${esc(title)}</h1>
  ${tabs}
</div></section>
<div class="wrap doc-wrap">
  ${toc.length > 2 ? `<aside class="toc"><p class="eyebrow">On this page</p><ol>${toc.map((h) => `<li><a href="#${esc(h.id)}">${esc(h.text)}</a></li>`).join('')}</ol></aside>` : ''}
  <article class="doc">${extraTop}${rendered.html}</article>
</div>`;
  return layout({ site, title, active, body });
}

export function streamersPage(model, site, rendered) {
  const list = site.streamers;
  const cards = list.length
    ? `<div class="streamer-grid">${list.map((s) => `<a class="streamer" href="${esc(s.url)}" rel="noopener" target="_blank">
  ${icon('cast', 'big-ico')}
  <div><h3>${esc(s.name)}</h3><p class="fine">${esc(s.platform || new URL(s.url).hostname.replace(/^www\./, ''))}${s.house ? ` · ${esc(houseLabel(s.house))}` : ''}</p>${s.note ? `<p>${esc(s.note)}</p>` : ''}</div>
</a>`).join('')}</div>`
    : `<p class="empty">No creators are listed yet. Stream the realm and ask the stewards to add you.</p>`;
  const top = `<section class="panel"><header class="panel-head"><h2>${icon('cast')} Watch the realm</h2></header>${cards}</section>`;
  return docPage({ site, id: 'streamers', title: 'Streamers', kicker: 'The realm on stream', rendered, active: 'streamers', extraTop: top });
}

// ---------------------------------------------------------------- download

export function downloadPage(model, site) {
  const d = site.download;
  const addr = site.server.address;
  const btn = d.url
    ? `<a class="btn primary big" href="${esc(d.url)}" rel="noopener">${icon('download')} Download Realm${d.version ? ` ${esc(d.version)}` : ''} for Windows</a>`
    : `<span class="btn primary big disabled" aria-disabled="true">${icon('download')} Download coming soon</span><p class="fine">The stewards have not published the installer link yet.</p>`;
  const body = `
<section class="page-head"><div class="wrap">
  <p class="kicker">Take up your banner</p>
  <h1 class="gold">Join the Realm</h1>
  <p class="lead">The Realm app finds the server, shows who holds the crown and joins through <b>your own Steam copy</b> of Reign of Kings. It never changes your game files.</p>
</div></section>
<div class="wrap">
  <div class="grid-2">
    <section class="panel download-card">
      <header class="panel-head"><h2>${icon('download')} The Realm app</h2></header>
      ${btn}
      ${d.sha256 ? `<p class="fine">SHA-256: <code class="hash">${esc(d.sha256)}</code></p>` : ''}
      <ul class="checks">
        <li>Windows 10 or 11, 64-bit.</li>
        <li><b>Reign of Kings</b> in your Steam library (app 344760). Realm does not include the game.</li>
        <li>Installs for your user only. No admin rights needed.</li>
        <li>Windows SmartScreen may warn about a new, unsigned app. Choose <em>More info → Run anyway</em> only if the file came from this page${d.sha256 ? ' and its SHA-256 matches' : ''}.</li>
      </ul>
    </section>
    <section class="panel">
      <header class="panel-head"><h2>${icon('play')} Or join by hand</h2></header>
      <ol class="steps">
        <li>Start Reign of Kings from Steam.</li>
        <li>Open the game's direct-connect box (from the server list).</li>
        <li>Type the address only${addr ? `: <code>${esc(addr)}</code>` : ' (ask in Discord)'}. Put the port${site.server.port ? ` <code>${esc(site.server.port)}</code>` : ''} in its own box. <b>Do not</b> type <code>address:port</code> into the address box: the game then says <em>"Unable to resolve host name"</em>.</li>
        <li>Swear to a house, or found your own. See <a href="play.html">How to play</a>.</li>
      </ol>
      <p class="fine">Cannot connect? The Realm app's <b>Can't join?</b> panel reads the game's own log and explains what went wrong.</p>
    </section>
  </div>
</div>`;
  return layout({ site, title: 'Download', active: 'download', body, description: 'Get the Realm app and join the server with your own Steam copy of Reign of Kings.' });
}

export function notFoundPage(site) {
  const body = `<section class="page-head"><div class="wrap"><p class="kicker">Lost on the King's Road</p><h1 class="gold">No such page</h1><p class="lead">The ravens could not find it. <a href="index.html">Return to the realm</a>.</p></div></section>`;
  return layout({ site, title: 'Not found', active: '', body });
}

// ---------------------------------------------------------------- feeds

export function statusJson(model, site) {
  const last = model.events[model.events.length - 1] || null;
  return {
    realm: site.realmName,
    king: model.state.king,
    house: model.state.house,
    since: model.state.since,
    online: model.state.online,
    maxPlayers: model.state.maxPlayers,
    updated: model.state.updated,
    stale: model.stale,
    generatedAt: new Date(site.generatedAt).toISOString(),
    lastEvent: last ? { id: last.id, ts: last.ts, type: last.type, title: last.title } : null,
    upcoming: model.upcoming.slice(0, 3).map((u) => ({ title: u.title, at: u.at, end: u.end, kind: u.kind })),
  };
}

export function atomFeed(model, site) {
  const base = site.siteUrl ? site.siteUrl.replace(/\/?$/, '/') : '';
  const items = model.events.slice(-50).reverse();
  const updated = (items[0] && items[0].ts) || new Date(site.generatedAt).toISOString();
  const x = (s) => esc(s);
  return `<?xml version="1.0" encoding="utf-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <title>${x(site.realmName)}: The Chronicle</title>
  <subtitle>${x(site.tagline)}</subtitle>
  <id>${x(base ? `${base}chronicle.html` : `urn:realm:${encodeURIComponent(site.realmName)}:chronicle`)}</id>
  ${base ? `<link rel="alternate" href="${x(base)}chronicle.html"/>` : ''}
  <updated>${x(updated)}</updated>
${items.map((e) => `  <entry>
    <title>${x(`${e.meta.label}: ${e.title}`)}</title>
    <id>${x(base ? `${base}chronicle.html#e${e.id}` : `urn:realm:event:${e.id}`)}</id>
    ${base ? `<link href="${x(base)}chronicle.html#e${e.id}"/>` : ''}
    <updated>${x(e.ts || updated)}</updated>
    <author><name>${x(site.realmName)}</name></author>
    <summary>${x(e.detail || e.title)}</summary>
  </entry>`).join('\n')}
</feed>
`;
}

export function houseCss(model) {
  return model.houses.map((h) => {
    const d = h.dye;
    const n = parseInt(d.slice(1), 16);
    const mix = (amt) => {
      const m = (c) => Math.round(amt >= 0 ? c + (255 - c) * amt : c * (1 + amt));
      const r = m(n >> 16), g = m((n >> 8) & 255), b = m(n & 255);
      return `#${((1 << 24) | (r << 16) | (g << 8) | b).toString(16).slice(1)}`;
    };
    return `.dye-${h.slug}{--dye:${d};--dye-light:${mix(0.16)};--dye-bright:${mix(0.45)};--dye-dark:${mix(-0.38)}}`;
  }).join('\n') + '\n';
}

export { safeUrl };
