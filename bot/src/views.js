// Builds every reply as plain Discord API objects (embeds and components). No discord.js import here,
// so the views are tested without it. Every embed passes through clampEmbed().

import { EVENT_KINDS } from './realm-data.js';
import { loreFor } from './lore.js';
import { COLORS, clampEmbed, duration, dyeFor, esc, houseLabel, metaFor, plain, sameHouse, truncate, ts } from './text.js';

const CROWN_TYPES = new Set(['coronation', 'abdication', 'claim_declared', 'rebellion_started', 'rebellion_ended', 'decree']);
const EVENT_TYPES = new Set(['event_started', 'event_ended', 'tournament_champion', 'hunt_kill', 'truce_broken', 'season_started', 'season_ended']);

export function eventLine(e, { detail = true, detailMax = 180 } = {}) {
  const m = metaFor(e.type);
  let line = `${m.emoji} **${esc(m.label, 40)}** · ${esc(e.title, 200)} · ${ts(e.ts)} \`#${e.id}\``;
  if (detail && e.detail) line += `\n> ${esc(truncate(e.detail, detailMax), detailMax + 1)}`;
  return line;
}

export function crownLine(state, now) {
  if (!state.king) return 'The Old Throne stands empty. Anyone may sit on it.';
  const house = state.house ? ` of ${esc(houseLabel(state.house), 70)}` : '';
  const since = state.since ? `\nReigning since ${ts(state.since, 'f')} (${duration(now - Date.parse(state.since))})` : '';
  return `👑 **${esc(state.king, 64)}**${house}${since}`;
}

function joinLine(cfg) {
  if (!cfg.join.address) return null;
  return `\`${cfg.join.address}:${cfg.join.port}\` · \`/realm join\` for how`;
}

function eventSummary(realmEvents) {
  if (!realmEvents) return null;
  const lines = [];
  for (const a of realmEvents.active) lines.push(`${EVENT_KINDS[a.kind].emoji} **${EVENT_KINDS[a.kind].name}** is on now, until ${ts(a.end, 't')}`);
  if (!lines.length && realmEvents.upcoming.length) {
    const u = realmEvents.upcoming[0];
    lines.push(`${EVENT_KINDS[u.kind].emoji} Next: **${EVENT_KINDS[u.kind].name}** ${ts(u.start, 'R')}`);
  }
  return lines.length ? lines.join('\n') : null;
}

export function unavailableEmbed(cfg, what = 'the Chronicle') {
  return clampEmbed({
    title: '🪶 The ravens are late',
    description: `The bot could not reach ${what} on the server PC. The game server may still be up; try again in a minute.`,
    color: COLORS.blood,
    footer: { text: cfg.realmName },
  });
}

// One embed for /realm status and for the live status message.
export function statusEmbed({ cfg, state, events = [], realmEvents = null, now = Date.now() }) {
  const fields = [];
  fields.push({ name: 'The Crown', value: crownLine(state, now) });
  fields.push({ name: 'Online', value: state.stale ? 'unknown' : `**${state.online}** / ${state.maxPlayers || '?'}`, inline: true });
  const biggest = [...state.houses].sort((a, b) => b.members - a.members).slice(0, 3);
  fields.push({
    name: 'Houses',
    value: state.houses.length ? `**${state.houses.length}**: ${biggest.map((h) => esc(h.name, 64)).join(', ')}${state.houses.length > 3 ? '…' : ''}` : 'None founded yet',
    inline: true,
  });
  const ev = eventSummary(realmEvents);
  if (ev) fields.push({ name: 'Realm events', value: ev });
  const jl = joinLine(cfg);
  if (jl) fields.push({ name: 'Join', value: jl });
  const recent = events.slice(-3).reverse();
  if (recent.length) fields.push({ name: 'Latest in the Chronicle', value: recent.map((e) => eventLine(e, { detail: false })).join('\n') });

  let description = null;
  if (state.stale) {
    description = state.updated
      ? `⚠️ The server has not reported since ${ts(state.updated)}. It may be down or restarting.`
      : '⚠️ The server has not reported yet. It may be down or the Chronicle plugin is not loaded.';
  }
  return clampEmbed({
    title: `🏰 ${plain(cfg.realmName, 80)}`,
    url: cfg.join.portalUrl || undefined,
    description,
    color: state.stale ? COLORS.iron : state.house ? dyeFor(state.house) : COLORS.gold,
    fields,
    footer: { text: state.stale ? 'Server status: not reporting' : 'Server status: live' },
    timestamp: new Date(state.updated && Date.parse(state.updated) ? Date.parse(state.updated) : now).toISOString(),
  });
}

export function kingEmbed({ cfg, state, events = [], now = Date.now() }) {
  const fields = [];
  const house = state.house ? state.houses.find((h) => sameHouse(h.name, state.house)) : null;
  if (house) {
    const vassals = state.houses.filter((h) => sameHouse(h.liege, house.name)).map((h) => esc(h.name, 64));
    fields.push({ name: 'Ruling house', value: `${esc(houseLabel(house.name), 70)}${house.sigil ? `, the ${esc(house.sigil, 64)}` : ''}\n${house.members} sworn${vassals.length ? ` · vassals: ${vassals.join(', ')}` : ''}` });
  }
  const crownEvents = events.filter((e) => CROWN_TYPES.has(e.type)).slice(-4).reverse();
  if (crownEvents.length) fields.push({ name: 'Matters of the crown', value: crownEvents.map((e) => eventLine(e, { detailMax: 120 })).join('\n') });
  return clampEmbed({
    title: state.king ? `👑 ${plain(state.king, 64)}` : '👑 The Old Throne',
    description: crownLine(state, now) + (state.stale ? '\n\n⚠️ The server is not reporting right now; this may be out of date.' : ''),
    color: state.house ? dyeFor(state.house) : COLORS.gold,
    fields,
    footer: { text: cfg.realmName },
  });
}

export function housesEmbed({ cfg, state }) {
  if (!state.houses.length) {
    return clampEmbed({ title: '🛡️ The houses of the realm', description: 'No house has been founded yet. In game: `/house found <Name> <sigil>`.', color: COLORS.gold });
  }
  const byName = new Map(state.houses.map((h) => [h.name.toLowerCase(), h]));
  const isTop = (h) => !h.liege || !byName.has(h.liege.toLowerCase());
  const tops = state.houses.filter(isTop).sort((a, b) => b.members - a.members || a.name.localeCompare(b.name));
  const lines = [];
  const seen = new Set();
  const walk = (h, depth) => {
    if (seen.has(h.name.toLowerCase()) || depth > 4) return;
    seen.add(h.name.toLowerCase());
    const crown = state.house && sameHouse(state.house, h.name) ? ' 👑' : '';
    const pad = depth ? `${' '.repeat(depth)}↳ ` : '';
    lines.push(`${pad}**${esc(h.name, 64)}**${crown}${h.sigil ? ` · ${esc(h.sigil, 64)}` : ''} · ${h.members} sworn`);
    state.houses.filter((v) => sameHouse(v.liege, h.name)).sort((a, b) => b.members - a.members).forEach((v) => walk(v, depth + 1));
  };
  tops.forEach((h) => walk(h, 0));
  state.houses.forEach((h) => walk(h, 0)); // anything left in a liege loop
  return clampEmbed({
    title: `🛡️ The houses of the realm (${state.houses.length})`,
    description: lines.join('\n'),
    color: COLORS.gold,
    footer: { text: 'Vassals are listed under their liege. /realm whois <house> for more.' },
  });
}

export function chronicleEmbed({ cfg, events, count }) {
  const list = events.slice(-count).reverse();
  return clampEmbed({
    title: '📜 The Chronicle of Ostreval',
    url: cfg.join.portalUrl || undefined,
    description: list.length ? list.map((e) => eventLine(e)).join('\n\n') : 'The Chronicle is still blank.',
    color: COLORS.gold,
    footer: { text: list.length ? `Newest first · ${list.length} of the latest entries` : cfg.realmName },
  });
}

export function eventsEmbed({ cfg, realmEvents, events = [] }) {
  const fields = [];
  for (const a of realmEvents.active) {
    const k = EVENT_KINDS[a.kind];
    const bits = [`Ends ${ts(a.end, 'R')} (${ts(a.end, 't')})`];
    if (a.kind === 'tournament' && a.leaders.length) bits.push(`Leading: ${a.leaders.map((l, i) => `${i + 1}. ${esc(l.name, 64)} (${l.kills})`).join(', ')}`);
    if (a.kind === 'kings_hunt' && a.quarry.length) bits.push(`Quarry: ${a.quarry.map((q) => `${esc(q.name, 64)}${q.claimedBy ? ` (taken by ${esc(q.claimedBy, 64)})` : ''}`).join(', ')}`);
    if (a.kind === 'crown_night' && a.captureHouses.length) bits.push(`Houses that took the throne tonight: ${a.captureHouses.map((h) => esc(h, 64)).join(', ')}`);
    fields.push({ name: `${k.emoji} Now: ${k.name}`, value: bits.join('\n') });
  }
  if (realmEvents.upcoming.length) {
    fields.push({
      name: '🗓️ Coming up (your local time)',
      value: realmEvents.upcoming.slice(0, 8).map((u) => `${EVENT_KINDS[u.kind].emoji} **${EVENT_KINDS[u.kind].name}** · ${ts(u.start, 'F')} · ${ts(u.start, 'R')} · ${Math.round((u.end - u.start) / 60000)} min`).join('\n'),
    });
  }
  const recent = events.filter((e) => EVENT_TYPES.has(e.type)).slice(-3).reverse();
  if (recent.length) fields.push({ name: 'Recent results', value: recent.map((e) => eventLine(e, { detailMax: 120 })).join('\n') });
  let description = null;
  if (!fields.length) {
    description = realmEvents.known
      ? 'No realm event is running and none is scheduled in the next week.'
      : 'No realm event schedule was found on the server PC (RealmEvents is not installed or has not run yet).';
  }
  if (realmEvents.error) description = `${description ? `${description}\n` : ''}⚠️ The event files are being rewritten; showing the last good copy.`;
  return clampEmbed({ title: '📯 Realm events', description, color: COLORS.gold, fields, footer: { text: 'Times are shown in your own time zone.' } });
}

export function joinReply(cfg) {
  const lines = [];
  if (!cfg.join.address) {
    lines.push('The owner has not set the public address yet (REALM_JOIN_ADDRESS). Ask in the server for how to join.');
  } else {
    lines.push(`**Address:** \`${cfg.join.address}\``);
    lines.push(`**Port:** \`${cfg.join.port}\``);
    lines.push('');
    lines.push('**Easiest:** install the Realm player app, then press **Join**. It starts your own Steam copy of Reign of Kings and connects for you.');
    if (cfg.join.serverId) lines.push(`With the app installed, this link opens it straight to the join prompt: \`realm://join/${cfg.join.serverId}\` (paste it into your browser; Discord does not make it clickable).`);
    lines.push('');
    lines.push(`**By hand:** start Reign of Kings from Steam, open the server browser's direct connect, and enter \`${cfg.join.address}\` and port \`${cfg.join.port}\`.`);
  }
  lines.push('');
  lines.push('You need your own copy of Reign of Kings on Steam (app 344760). Nothing about the game is changed or downloaded by the app.');
  const buttons = [];
  if (cfg.join.playerAppUrl) buttons.push({ type: 2, style: 5, label: 'Get the Realm player app', url: cfg.join.playerAppUrl });
  if (cfg.join.portalUrl) buttons.push({ type: 2, style: 5, label: 'Realm website', url: cfg.join.portalUrl });
  return {
    embeds: [clampEmbed({ title: `⚔️ Join ${cfg.realmName}`, description: lines.join('\n'), color: COLORS.gold })],
    components: buttons.length ? [{ type: 1, components: buttons }] : [],
  };
}

export function whoisEmbed({ cfg, state, detail, treaties = [], events = [], house, now = Date.now() }) {
  const lore = loreFor(house.name);
  const vassals = state.houses.filter((h) => sameHouse(h.liege, house.name));
  const fields = [];
  fields.push({ name: 'Sworn', value: `${house.members}`, inline: true });
  fields.push({ name: 'Liege', value: house.liege ? esc(houseLabel(house.liege), 70) : 'None: sworn to no one', inline: true });
  if (vassals.length) fields.push({ name: 'Vassals', value: vassals.map((v) => esc(v.name, 64)).join(', '), inline: true });
  if (state.house && sameHouse(state.house, house.name)) fields.push({ name: 'The crown', value: `Holds the Old Throne: ${esc(state.king || '?', 64)}` });
  if (detail) {
    if (detail.leaders.length) fields.push({ name: 'Led by', value: detail.leaders.map((x) => esc(x, 64)).join(', '), inline: true });
    if (detail.officers.length) fields.push({ name: 'Officers', value: detail.officers.map((x) => esc(x, 64)).join(', '), inline: true });
    if (detail.founded) fields.push({ name: 'Founded', value: ts(detail.founded, 'D'), inline: true });
    const record = [];
    if (detail.oathsBroken) record.push(`⛓️ ${detail.oathsBroken} oath${detail.oathsBroken === 1 ? '' : 's'} broken`);
    if (detail.treatiesBroken) record.push(`🔥 ${detail.treatiesBroken} treat${detail.treatiesBroken === 1 ? 'y' : 'ies'} broken`);
    fields.push({ name: 'Record', value: record.length ? record.join(' · ') : 'Clean: no oath or treaty broken' });
    const live = treaties.filter((t) => (sameHouse(t.a, house.name) || sameHouse(t.b, house.name)) && (!t.expires || t.expires > now));
    if (live.length) {
      fields.push({
        name: 'Treaties',
        value: live.map((t) => `📜 with ${esc(sameHouse(t.a, house.name) ? t.b : t.a, 64)}${t.expires ? `, until ${ts(t.expires, 'D')}` : ''}`).join('\n'),
      });
    }
  }
  if (lore) fields.push({ name: 'Of old', value: `*"${lore.words}"*\nSeat: ${lore.seat}` });
  const mentions = events
    .filter((e) => [e.title, e.detail].some((t) => new RegExp(`\\b${house.name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`, 'i').test(t)))
    .slice(-3)
    .reverse();
  if (mentions.length) fields.push({ name: 'Lately in the Chronicle', value: mentions.map((e) => eventLine(e, { detailMax: 100 })).join('\n') });
  return clampEmbed({
    title: `🛡️ ${houseLabel(plain(house.name, 64))}`,
    description: house.sigil ? `Sigil: **${esc(house.sigil, 64)}**` : null,
    color: dyeFor(house.name),
    fields,
    footer: { text: `${cfg.realmName} · house colour is the overlay's banner dye` },
  });
}
