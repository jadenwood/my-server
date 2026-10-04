// Turns the sanitized data files into what the pages show: merged houses, the Hall of Kings
// (reigns rebuilt from coronation/abdication events), upcoming events and a few records.

import { slug, dyeFor } from './util.mjs';
import { eventKindInfo } from './data.mjs';

// Same labels and groups as chronicle/public/assets/common.js, so the portal and the overlay agree.
export const TYPE_META = {
  coronation:         { label: 'Coronation',       icon: 'crown',   tone: 'gold',  group: 'crown' },
  abdication:         { label: 'The Crown Falls',  icon: 'crownX',  tone: 'blood', group: 'crown' },
  claim_declared:     { label: 'A Claim',          icon: 'flag',    tone: 'blood', group: 'war' },
  rebellion_started:  { label: 'Rebellion',        icon: 'swords',  tone: 'blood', group: 'war' },
  rebellion_ended:    { label: 'Rebellion Ends',   icon: 'sheath',  tone: 'iron',  group: 'war' },
  house_founded:      { label: 'A House Rises',    icon: 'shield',  tone: 'gold',  group: 'houses' },
  oath_sworn:         { label: 'Oath Sworn',       icon: 'oath',    tone: 'gold',  group: 'houses' },
  oath_broken:        { label: 'Oathbreaker',      icon: 'chainX',  tone: 'blood', group: 'houses' },
  treaty_signed:      { label: 'Treaty',           icon: 'seal',    tone: 'moss',  group: 'houses' },
  treaty_broken:      { label: 'Treaty Broken',    icon: 'scrollX', tone: 'blood', group: 'houses' },
  decree:             { label: 'Royal Decree',     icon: 'scroll',  tone: 'gold',  group: 'crown' },
  ransom_set:         { label: 'Ransom',           icon: 'chain',   tone: 'blood', group: 'ransom' },
  ransom_paid:        { label: 'Ransom Paid',      icon: 'coins',   tone: 'gold',  group: 'ransom' },
  released:           { label: 'Set Free',         icon: 'chainX',  tone: 'moss',  group: 'ransom' },
  contract_posted:    { label: 'A Price Is Set',   icon: 'scroll',  tone: 'iron',  group: 'contracts' },
  contract_fulfilled: { label: 'Contract Paid',    icon: 'coins',   tone: 'gold',  group: 'contracts' },
  contract_ended:     { label: 'Contract Lapses',  icon: 'scrollX', tone: 'iron',  group: 'contracts' },
  // RealmSeasons and RealmEvents (same labels as the overlay)
  season_started:      { label: 'A New Season',        icon: 'hourglass', tone: 'gold',  group: 'seasons' },
  season_ended:        { label: "Season's End",        icon: 'trophy',    tone: 'gold',  group: 'seasons' },
  event_started:       { label: 'Realm Event',         icon: 'horn',      tone: 'iron',  group: 'seasons' },
  event_ended:         { label: 'Event Ends',          icon: 'horn',      tone: 'iron',  group: 'seasons' },
  tournament_champion: { label: 'Tournament Champion', icon: 'trophy',    tone: 'gold',  group: 'seasons' },
  hunt_kill:           { label: "The King's Hunt",     icon: 'swords',    tone: 'blood', group: 'seasons' },
  truce_broken:        { label: 'Truce Broken',        icon: 'scrollX',   tone: 'blood', group: 'war' },
  // RealmLaws, RealmDynasties, RealmRenown, RealmTreasury, RealmRavens (registered in all three Chronicle lists)
  law_proclaimed:      { label: 'A Law Proclaimed',       icon: 'scroll',  tone: 'gold',  group: 'law' },
  law_repealed:        { label: 'Law Repealed',           icon: 'scrollX', tone: 'iron',  group: 'law' },
  accusation:          { label: 'Accused',                icon: 'flag',    tone: 'blood', group: 'law' },
  trial_by_combat:     { label: 'Trial by Combat',        icon: 'swords',  tone: 'blood', group: 'law' },
  verdict:             { label: 'Verdict',                icon: 'seal',    tone: 'iron',  group: 'law' },
  pardon:              { label: 'Pardoned',               icon: 'chainX',  tone: 'moss',  group: 'law' },
  dynasty_founded:     { label: 'A Line Is Founded',      icon: 'people',  tone: 'gold',  group: 'law' },
  heir_named:          { label: 'An Heir Is Named',       icon: 'oath',    tone: 'gold',  group: 'law' },
  succession:          { label: 'Succession',             icon: 'scroll',  tone: 'gold',  group: 'law' },
  blood_claim:         { label: 'Blood Claim',            icon: 'flag',    tone: 'blood', group: 'law' },
  blood_restored:      { label: 'The Line Restored',      icon: 'crown',   tone: 'gold',  group: 'law' },
  title_bestowed:      { label: 'Title Bestowed',         icon: 'seal',    tone: 'gold',  group: 'law' },
  title_earned:        { label: 'A Title Earned',         icon: 'seal',    tone: 'gold',  group: 'law' },
  treasury_mint:       { label: 'The Crown Strikes Coin', icon: 'coins',   tone: 'gold',  group: 'treasury' },
  treasury_grant:      { label: 'Royal Largesse',         icon: 'coins',   tone: 'gold',  group: 'treasury' },
  tithe_levied:        { label: 'The Tithe Gathered',     icon: 'scroll',  tone: 'iron',  group: 'treasury' },
  great_trade:         { label: 'A Great Sale',           icon: 'seal',    tone: 'gold',  group: 'treasury' },
  rumour:              { label: 'A Rumour Spreads',       icon: 'scroll',  tone: 'iron',  group: 'treasury' },
  holding_taken:       { label: 'A Holding Taken',        icon: 'flag',    tone: 'blood', group: 'war' },
  census_taken:        { label: 'The Census',             icon: 'people',  tone: 'iron',  group: 'seasons' },
  vote_held:           { label: 'The Realm Votes',        icon: 'people',  tone: 'gold',  group: 'crown' },
  blade_claimed:       { label: 'The Ironbreaker Taken Up', icon: 'blade', tone: 'gold',  group: 'war' },
  blade_lost:          { label: 'The Ironbreaker Returns', icon: 'bladeX', tone: 'iron',  group: 'war' },
};

export const GROUPS = [
  { id: 'crown', label: 'Crown' },
  { id: 'war', label: 'Claims & war' },
  { id: 'houses', label: 'Houses' },
  { id: 'ransom', label: 'Ransom' },
  { id: 'contracts', label: 'Contracts' },
  { id: 'seasons', label: 'Seasons & events' },
  { id: 'law', label: 'Law & dynasty' },
  { id: 'treasury', label: 'Treasury & rumours' },
  { id: 'other', label: 'Other' },
];

export function metaFor(type, extra = {}) {
  if (TYPE_META[type]) return TYPE_META[type];
  if (extra[type]) return { label: extra[type].label, icon: extra[type].icon, tone: 'iron', group: 'law' };
  return { label: type.replace(/_/g, ' ').replace(/^\w/, (c) => c.toUpperCase()), icon: 'scroll', tone: 'iron', group: 'other' };
}

// Lore for the six starting houses (docs/community/lore.md, "### House X: the Y" sections).
export function parseHouseLore(md) {
  const out = {};
  if (!md) return out;
  const parts = String(md).replace(/\r\n?/g, '\n').split(/^### /m).slice(1);
  for (const part of parts) {
    const head = part.split('\n', 1)[0];
    const m = head.match(/^House\s+([^:]+?)(?::\s*(.+))?$/);
    if (!m) continue;
    const body = part.slice(head.length);
    const field = (label) => {
      const f = body.match(new RegExp(`^-\\s+\\*\\*${label}:\\*\\*\\s*(.+)$`, 'mi'));
      return f ? f[1].trim() : null;
    };
    out[m[1].trim().toLowerCase()] = {
      epithet: m[2] ? m[2].trim() : null,
      sigil: field('Sigil'),
      colours: field('Colours'),
      words: field('Words'),
      seat: field('Seat'),
      history: field('History'),
      hook: field('Play-style hook'),
    };
  }
  return out;
}

const lower = (s) => String(s || '').toLowerCase();

// Which known houses an event is about: "House X" mentions, "<king> of X takes the throne",
// and actors who are on a house roster.
export function housesOfEvent(e, houseNames, rosterIndex = new Map()) {
  const found = [];
  const add = (n) => {
    const real = houseNames.find((h) => lower(h) === lower(n));
    if (real && !found.includes(real)) found.push(real);
  };
  let text = `${e.title} ${e.detail}`;
  // Longest names first so "House Grey Water" is not also counted as "House Grey".
  for (const n of houseNames.slice().sort((a, b) => b.length - a.length)) {
    const bare = n.replace(/^house\s+/i, '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const re = new RegExp(`\\bHouse\\s+${bare}(?![\\p{L}\\p{N}])`, 'giu');
    if (re.test(text)) {
      add(n);
      text = text.replace(re, ' ');
    }
  }
  const crown = e.title.match(/ of (.+?) takes the throne/);
  if (crown) add(crown[1]);
  for (const a of e.actors) if (rosterIndex.has(lower(a))) add(rosterIndex.get(lower(a)));
  return found;
}

export function buildHouses({ state, housesData, events, lore }) {
  const byName = new Map();
  const ensure = (name) => {
    const key = lower(name);
    if (!byName.has(key)) byName.set(key, { name, sigil: null, liege: null, members: 0, founded: null, oathsBroken: 0, treatiesBroken: 0, leader: null, officers: [], roster: [] });
    return byName.get(key);
  };
  for (const h of housesData.houses) Object.assign(ensure(h.name), h);
  // RealmState is refreshed every 30 s by the plugin, so its counts win when both are present.
  for (const h of state.houses) {
    const x = ensure(h.name);
    if (h.sigil) x.sigil = h.sigil;
    x.liege = h.liege;
    x.members = h.members || x.members;
  }
  const names = [...byName.values()].map((h) => h.name);
  const rosterIndex = new Map();
  for (const h of byName.values()) for (const n of h.roster) rosterIndex.set(lower(n), h.name);

  const houses = [...byName.values()].map((h) => {
    const l = lore[lower(h.name)] || null;
    return {
      ...h,
      slug: slug(h.name),
      dye: dyeFor(h.name),
      lore: l,
      vassals: [],
      treaties: [],
      events: [],
      crowns: 0,
      isCrown: !!state.house && lower(state.house) === lower(h.name),
    };
  });
  // Distinct slugs even when two names fold to the same one.
  const used = new Set();
  for (const h of houses) {
    let s = h.slug, n = 2;
    while (used.has(s)) s = `${h.slug}-${n++}`;
    h.slug = s;
    used.add(s);
  }
  const find = (n) => houses.find((h) => lower(h.name) === lower(n));
  for (const h of houses) if (h.liege && find(h.liege)) find(h.liege).vassals.push(h.name);
  const now = Date.now();
  for (const t of housesData.treaties) {
    const active = !t.expires || Date.parse(t.expires) > now;
    for (const [me, other] of [[t.a, t.b], [t.b, t.a]]) {
      const x = find(me);
      if (x) x.treaties.push({ with: other, signed: t.signed, expires: t.expires, active });
    }
  }
  for (const e of events) {
    const hs = housesOfEvent(e, names, rosterIndex);
    e.houses = hs;
    for (const n of hs) find(n).events.push(e);
  }
  houses.sort((a, b) => Number(b.isCrown) - Number(a.isCrown) || b.members - a.members || a.name.localeCompare(b.name));
  return { houses, rosterIndex, names };
}

// Rebuild reigns from the chronicle. A coronation opens a reign; an abdication or the next
// coronation closes it. The current RealmState king closes the list if the log was trimmed.
export function buildReigns(events, state, now = Date.now(), legends = null) {
  if (legends && (legends.hall.length || legends.current)) return reignsFromLegends(events, state, now, legends);
  const reigns = [];
  let cur = null;
  const close = (ts, how) => {
    if (!cur) return;
    cur.end = ts;
    cur.endedBy = how;
    reigns.push(cur);
    cur = null;
  };
  for (const e of events) {
    if (e.type === 'coronation') {
      close(e.ts, 'succeeded');
      const king = e.actors[0] || (e.title.match(/^(.+?)(?: of .+?)? takes the throne/) || [])[1] || 'Unknown';
      let house = (e.title.match(/ of (.+?) takes the throne/) || [])[1] || (e.detail.match(/House (.+?) now holds the crown/) || [])[1] || null;
      if (house && /^no house$/i.test(house)) house = null;
      cur = { king, house, start: e.ts, end: null, endedBy: null, decrees: 0, rebellions: 0, eventId: e.id };
    } else if (e.type === 'abdication') {
      close(e.ts, 'abdicated');
    } else if (cur && e.type === 'decree') {
      cur.decrees++;
    } else if (cur && e.type === 'rebellion_ended') {
      cur.rebellions++;
    }
  }
  if (cur) {
    if (state.king && lower(state.king) !== lower(cur.king)) close(state.since, 'succeeded');
    else reigns.push(cur);
  }
  if (state.king && !(reigns.length && !reigns[reigns.length - 1].end && lower(reigns[reigns.length - 1].king) === lower(state.king))) {
    reigns.push({ king: state.king, house: state.house, start: state.since, end: null, endedBy: null, decrees: 0, rebellions: 0, eventId: null });
  }
  // A reign is current only if RealmState agrees someone is on the throne.
  for (const r of reigns) {
    if (!r.end && !state.king) { r.end = state.updated || null; r.endedBy = r.end ? 'vacant' : null; }
    const s = Date.parse(r.start);
    const e = r.end ? Date.parse(r.end) : now;
    r.ms = Number.isNaN(s) || Number.isNaN(e) ? null : Math.max(0, e - s);
    r.current = !r.end;
  }
  return reigns;
}

// RealmSeasons keeps every reign in RealmLegends.json (across wipes, with how it ended). When that file
// exists it is the Hall of Kings; the Chronicle only adds decree and rebellion counts per reign.
function reignsFromLegends(events, state, now, legends) {
  const list = legends.hall.map((r) => ({ ...r }));
  if (legends.current && !list.some((r) => !r.end && r.king === legends.current.king && r.start === legends.current.start)) list.push({ ...legends.current });
  list.sort((a, b) => Date.parse(a.start) - Date.parse(b.start));
  const ENDINGS = [[/rebell|overthrown/i, 'overthrown'], [/fell|died|slain/i, 'abdicated'], [/left|abdicat/i, 'abdicated'], [/passed|succeed/i, 'succeeded'], [/wipe/i, 'wiped']];
  const reigns = list.map((r) => {
    const s = Date.parse(r.start);
    const e = r.end ? Date.parse(r.end) : now;
    const inside = events.filter((x) => { const t = Date.parse(x.ts); return t >= s && t < e; });
    const how = r.ending ? (ENDINGS.find(([re]) => re.test(r.ending)) || [null, 'other'])[1] : (r.end ? 'succeeded' : null);
    return {
      king: r.king, house: r.house, start: r.start, end: r.end, endedBy: how, endingText: r.ending, season: r.season,
      decrees: inside.filter((x) => x.type === 'decree').length,
      rebellions: inside.filter((x) => x.type === 'rebellion_ended').length,
      eventId: null,
      ms: Number.isNaN(s) || Number.isNaN(e) ? null : Math.max(0, e - s),
      current: !r.end,
    };
  });
  // RealmState is fresher than the legends file (written every 30 s vs on crown change).
  const last = reigns[reigns.length - 1];
  if (state.king && !(last && last.current && lower(last.king) === lower(state.king))) {
    if (last && last.current) { last.current = false; last.end = state.since; last.endedBy = 'succeeded'; }
    const s = Date.parse(state.since);
    reigns.push({ king: state.king, house: state.house, start: state.since, end: null, endedBy: null, endingText: null, season: null, decrees: 0, rebellions: 0, eventId: null, ms: Number.isNaN(s) ? null : Math.max(0, now - s), current: true });
  }
  return reigns;
}

export function buildRecords(reigns, houses) {
  const done = reigns.filter((r) => r.ms != null);
  const longest = done.slice().sort((a, b) => b.ms - a.ms)[0] || null;
  const tally = (key) => {
    const m = new Map();
    for (const r of reigns) if (r[key]) m.set(r[key], (m.get(r[key]) || 0) + 1);
    return [...m.entries()].sort((a, b) => b[1] - a[1])[0] || null;
  };
  const byHouse = tally('house');
  for (const h of houses) h.crowns = reigns.filter((r) => r.house && lower(r.house) === lower(h.name)).length;
  const breaker = houses.slice().sort((a, b) => (b.oathsBroken + b.treatiesBroken) - (a.oathsBroken + a.treatiesBroken))[0];
  return {
    longest,
    mostCrownedKing: tally('king'),
    mostCrownedHouse: byHouse && byHouse[1] > 1 ? byHouse : null,
    faithless: breaker && breaker.oathsBroken + breaker.treatiesBroken > 0 ? breaker : null,
    totalReigns: reigns.length,
  };
}

// What is coming up: rebellion windows of open claims, seasons, treaties running out and
// owner-listed events from the portal config. Future (or running) only, soonest first.
export function buildUpcoming({ crown, seasons, housesData, configEvents = [], realmEvents = { active: [], schedule: [] }, now = Date.now() }) {
  const out = [];
  for (const a of realmEvents.active || []) {
    const info = eventKindInfo(a.kind);
    out.push({ kind: 'event', title: info.title, detail: info.detail, at: a.start, end: a.end });
  }
  const seenKind = new Set((realmEvents.active || []).map((a) => a.kind));
  for (const o of realmEvents.schedule || []) {
    if (seenKind.has(o.kind)) continue; // the next one of each kind is enough
    seenKind.add(o.kind);
    out.push({ kind: 'event', title: o.title, detail: o.detail, at: o.at, end: o.end });
  }
  for (const c of crown.claims) {
    if (c.status === 'ended' || !c.windowStart) continue;
    out.push({
      kind: 'war',
      title: c.house ? `Rebellion window: House ${c.house.replace(/^house\s+/i, '')}` : 'Rebellion window',
      detail: c.against ? `A declared claim against House ${c.against.replace(/^house\s+/i, '')}. Only claimants may contest the throne.` : 'A declared claim. Only claimants may contest the throne.',
      at: c.windowStart,
      end: c.windowEnd,
    });
  }
  if (seasons.current && seasons.current.endsAt) {
    out.push({ kind: 'season', title: `${seasons.current.name} ends`, detail: seasons.current.theme || 'The season closes and its champions are named.', at: seasons.current.endsAt, end: null });
  }
  for (const e of seasons.upcoming) out.push({ kind: 'season', title: e.title, detail: '', at: e.at, end: e.end });
  for (const t of housesData.treaties) {
    if (!t.expires) continue;
    out.push({ kind: 'treaty', title: `Treaty of ${t.a} and ${t.b} runs out`, detail: 'Peace between the two houses ends unless renewed.', at: t.expires, end: null });
  }
  for (const e of Array.isArray(configEvents) ? configEvents : []) {
    if (!e || typeof e.title !== 'string' || typeof e.at !== 'string') continue;
    const at = Date.parse(e.at);
    if (Number.isNaN(at)) continue;
    out.push({ kind: 'herald', title: e.title.slice(0, 120), detail: typeof e.detail === 'string' ? e.detail.slice(0, 300) : '', at: new Date(at).toISOString(), end: typeof e.end === 'string' && !Number.isNaN(Date.parse(e.end)) ? new Date(Date.parse(e.end)).toISOString() : null });
  }
  const seen = new Set();
  return out
    .filter((x) => Date.parse(x.end || x.at) > now)
    .filter((x) => {
      const key = `${x.title.toLowerCase()}|${Date.parse(x.at)}`;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    })
    .map((x) => ({ ...x, live: Date.parse(x.at) <= now }))
    .sort((a, b) => Date.parse(a.at) - Date.parse(b.at))
    .slice(0, 8);
}

export function buildModel(data, { lore = {}, config = {}, now = Date.now() } = {}) {
  const events = data.events.map((e) => ({ ...e, meta: metaFor(e.type, data.typeMeta) }));
  const { houses, names } = buildHouses({ state: data.state, housesData: data.housesData, events, lore });
  const reigns = buildReigns(events, data.state, now, data.legends);
  const records = buildRecords(reigns, houses);
  const upcoming = buildUpcoming({ crown: data.crown, seasons: data.seasons, housesData: data.housesData, configEvents: config.events, realmEvents: data.realmEvents, now });
  // Season standings per house (RealmSeasons), joined onto the house records.
  for (const st of data.seasons.standings || []) {
    const h = houses.find((x) => lower(x.name) === lower(st.house));
    if (h) h.standing = st;
  }
  const updatedMs = data.state.updated ? Date.parse(data.state.updated) : NaN;
  const staleAfter = Number.isFinite(config.staleAfterMinutes) ? config.staleAfterMinutes : 10;
  return {
    now,
    events,
    houses,
    houseNames: names,
    reigns,
    records,
    upcoming,
    state: data.state,
    crown: data.crown,
    seasons: data.seasons,
    stale: Number.isNaN(updatedMs) || now - updatedMs > staleAfter * 60000,
    warnings: data.warnings,
  };
}
