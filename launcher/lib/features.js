'use strict';

// Realm features (Realm Steward): turn each plugin's main features on or off by editing its Oxide config,
// oxide/config/<Plugin>.json, safely.
//
//   1. The file is read and must parse as JSON. A damaged config is never touched (the plugin falls back to
//      its defaults, and the owner should see that rather than have Steward paper over it).
//   2. Only a switch this screen knows (CATALOGUE, checked against each plugin's PluginConfig class by
//      test/features.test.js) or a true/false value already in the file can be changed, and only to a value
//      of the same type.
//   3. The edit is textual: exactly the bytes of that one value change. Everything else (comments Oxide
//      never writes, 64-bit Steam IDs that JavaScript numbers cannot hold, number formats) stays as it was.
//      The result is parsed again and must differ from the original in that one value only.
//   4. The old file is copied to <server>\_realm-backups\config-<time>\<Plugin>.json, then the new one is
//      written to a temporary file and renamed into place. If the file changed on disk since it was read,
//      nothing is written.
//   5. A running server reloads the plugin over the admin console (/oxide.reload <Plugin>), which re-reads
//      the config. A stopped server picks it up at the next start.

const fsp = require('fs/promises');
const path = require('path');
const S = require('./safety');

const MAX_CONFIG_BYTES = 2 * 1024 * 1024;
const PLUGIN_RE = /^[A-Z][A-Za-z0-9]{2,40}$/;

// Main switches per plugin. path is the JSON path in oxide/config/<plugin>.json (field names of the plugin's
// PluginConfig, as Oxide's Config.WriteObject writes them). master: turning it off turns the plugin's
// features off. danger: asks before turning on.
const CATALOGUE = [
  // --- Play
  { plugin: 'RealmQuests', group: 'Play', title: 'Quests and deeds', blurb: 'Daily and weekly tasks, the season story, deeds and house goals.', switches: [
    { path: 'Enabled', label: 'Quests', master: true },
    { path: 'DailiesEnabled', label: 'Daily tasks' },
    { path: 'WeekliesEnabled', label: 'Weekly tasks' },
    { path: 'StoryEnabled', label: 'The season story' },
    { path: 'AchievementsEnabled', label: 'Deeds (achievements)' },
    { path: 'HouseGoalsEnabled', label: 'Weekly house goals' },
    { path: 'MarksRewards', label: 'Rewards in marks' },
    { path: 'ItemRewards', label: 'Rewards in items' },
    { path: 'UsePopups', label: 'Popups' }
  ] },
  { plugin: 'RealmArena', group: 'Play', title: 'The Arena', blurb: 'Duels to the first fall, the ladder, team duels, brackets, trial by combat, tavern games.', switches: [
    { path: 'Enabled', label: 'Arena', master: true },
    { path: 'Duels.Enabled', label: 'Duels' },
    { path: 'Ranked.Enabled', label: 'Ranked ladder' },
    { path: 'Wagers.Enabled', label: 'Stakes in marks' },
    { path: 'Teams.Enabled', label: 'Team duels' },
    { path: 'Champion.Enabled', label: 'Weekly Champion of the Ring' },
    { path: 'Tournament.Enabled', label: 'Bracket tournaments' },
    { path: 'Trials.Enabled', label: 'Trial by combat' },
    { path: 'Tavern.Enabled', label: 'Tavern dice and cards' },
    { path: 'UsePopups', label: 'Popups' }
  ] },
  { plugin: 'RealmTravel', group: 'Play', title: 'Roads and waystones', blurb: 'Waystones, travel for a toll, /home, directions and kits.', switches: [
    { path: 'General.Enabled', label: 'Travel', master: true },
    { path: 'Travel.Enabled', label: 'Travel between waystones' },
    { path: 'Home.Enabled', label: '/home to your crest' },
    { path: 'Discovery.Enabled', label: 'Discovering waystones' },
    { path: 'Roads.Enabled', label: '/road directions' },
    { path: 'Kits.Enabled', label: 'Kits' },
    { path: 'General.UsePopups', label: 'Popups' }
  ] },
  { plugin: 'RealmDominion', group: 'Play', title: 'Dominion', blurb: 'Holdings taken in the War Hours, garrisons, daily income.', switches: [
    { path: 'Enabled', label: 'Dominion', master: true },
    { path: 'PauseDuringTruce', label: 'Pause in a truce' },
    { path: 'RequireRaidHours', label: 'Only in raid hours' },
    { path: 'AnnounceCaptures', label: 'Herald captures' },
    { path: 'PaintBoards', label: 'Dominion sign board' },
    { path: 'PublishMap', label: 'Map file for the portal' },
    { path: 'UsePopups', label: 'Popups' }
  ] },
  { plugin: 'RealmEvents', group: 'Play', title: 'Realm events', blurb: 'Crown Night, the Royal Tournament, the King\'s Hunt and the Truce.', switches: [
    { path: 'EnableCrownNight', label: 'Crown Night' },
    { path: 'EnableTournament', label: 'Royal Tournament' },
    { path: 'EnableKingsHunt', label: "King's Hunt" },
    { path: 'EnableTruce', label: 'Truce of the Realm' },
    { path: 'TruceEnforced', label: 'Truce blocks damage' }
  ] },
  { plugin: 'RealmCrafts', group: 'Play', title: 'The guilds', blurb: 'Professions, ranks and perks, house workshops, the weekly Master Crafter, commissions.', switches: [
    { path: 'General.Enabled', label: 'Guilds', master: true },
    { path: 'Gathering.Enabled', label: 'XP for gathering' },
    { path: 'Crafting.Enabled', label: 'XP for crafting' },
    { path: 'Workshops.Enabled', label: 'House workshops' },
    { path: 'Weekly.Enabled', label: 'Weekly Master Crafter' },
    { path: 'Commissions.Enabled', label: 'Commission board' },
    { path: 'Dominion.Enabled', label: 'Holding bonuses' },
    { path: 'Rewards.QuestReports', label: 'Count toward quests' },
    { path: 'General.UsePopups', label: 'Popups' }
  ] },
  { plugin: 'RealmWorld', group: 'Play', title: 'The living world', blurb: 'Treasure hunts, the blood moon, caravans, legends, festivals and the census.', switches: [
    { path: 'General.Enabled', label: 'World events', master: true },
    { path: 'Treasure.Enabled', label: 'Treasure hunts' },
    { path: 'BloodMoon.Enabled', label: 'Blood moon' },
    { path: 'Caravan.Enabled', label: 'Caravans' },
    { path: 'Legends.Enabled', label: 'Legendary beasts' },
    { path: 'Festivals.Enabled', label: 'Festivals' },
    { path: 'Census.Enabled', label: 'Weekly census' },
    { path: 'General.AvoidRealmEvents', label: 'Wait for Realm events to end' },
    { path: 'General.UsePopups', label: 'Popups' }
  ] },
  { plugin: 'RealmLegendary', group: 'Play', title: 'The Ironbreaker', blurb: 'The one legendary blade, won at the tournament and taken by the bearer\'s slayer.', switches: [
    { path: 'PassToSlayer', label: 'Passes to the slayer' },
    { path: 'ChatTitleEnabled', label: 'Bearer title in chat' },
    { path: 'UsePopups', label: 'Popups' }
  ] },
  // --- Politics
  { plugin: 'CrownAndConsequences', group: 'Crown and houses', title: 'The crown', blurb: 'The Old Throne, claims, rebellion windows, decrees and ransom.', switches: [
    { path: 'GateThroneCaptureToWindows', label: 'Throne only in rebellion windows' },
    { path: 'ClaimRequiresHouseLeader', label: 'Only house leaders claim' },
    { path: 'OnlyClaimantsMayCapture', label: 'Only claimants take the throne' },
    { path: 'AllowCaptureWhenThroneVacant', label: 'Anyone may take an empty throne' }
  ] },
  { plugin: 'RealmHouses', group: 'Crown and houses', title: 'Houses', blurb: 'Houses, oaths of fealty and treaties.', switches: [
    { path: 'LinkGameGuilds', label: 'Link to the game\'s guilds' },
    { path: 'BroadcastEvents', label: 'Herald house news' },
    { path: 'ChronicleEnabled', label: 'Write to the Chronicle' },
    { path: 'UsePopups', label: 'Popups' }
  ] },
  { plugin: 'RealmLaws', group: 'Crown and houses', title: 'Laws and trials', blurb: 'Laws, accusations, juries, outlawry and exile.', switches: [
    { path: 'CouncilMayAccuse', label: 'The council may accuse' },
    { path: 'TrialByCombat', label: 'Trial by combat' },
    { path: 'CombatInArena', label: 'Trials fought in the Arena' },
    { path: 'OfferOutlawryToContracts', label: 'Outlaws get bounties' }
  ] },
  { plugin: 'RealmHeraldry', group: 'Crown and houses', title: 'Colours and the council', blurb: 'House colours on the game\'s guild banners, council elections and the realm\'s votes.', switches: [
    { path: 'Heraldry.Enabled', label: 'House colours on guilds' },
    { path: 'Heraldry.SyncGuildNames', label: 'Guilds bear the house name' },
    { path: 'Heraldry.SyncBannerColours', label: 'Banner colours' },
    { path: 'Heraldry.Enforce', label: 'Restore colours if changed' },
    { path: 'Council.Enabled', label: 'Council elections' },
    { path: 'Referendum.Enabled', label: 'Realm votes' },
    { path: 'Voters.RequireHouse', label: 'Only house members vote' },
    { path: 'General.UsePopups', label: 'Popups' }
  ] },
  { plugin: 'RealmDynasties', group: 'Crown and houses', title: 'Dynasties', blurb: 'Bloodlines, heirs, succession and blood claims.', switches: [
    { path: 'BroadcastEvents', label: 'Herald dynasty news' },
    { path: 'ChronicleEnabled', label: 'Write to the Chronicle' },
    { path: 'DelegateToCrownClaim', label: 'Blood claims press a crown claim' }
  ] },
  { plugin: 'RealmRavens', group: 'Crown and houses', title: 'Ravens', blurb: 'Letters, spies and rumours.', switches: [
    { path: 'AllowAnonymous', label: 'Anonymous letters' },
    { path: 'SpiesEnabled', label: 'Spies' },
    { path: 'InterceptionEnabled', label: 'Intercepting letters' },
    { path: 'RumoursEnabled', label: 'Rumours' }
  ] },
  // --- Economy
  { plugin: 'RealmTreasury', group: 'Coin', title: 'Treasury', blurb: 'Marks, purses, the market, vaults, mint and tithe.', switches: [
    { path: 'RewardsEnabled', label: 'Rewards in marks' },
    { path: 'TitheMarks', label: 'Tithe in marks' },
    { path: 'TitheItems', label: 'Tithe in items' },
    { path: 'ObserveGameTax', label: 'Follow the game\'s throne tax' }
  ] },
  { plugin: 'RealmContracts', group: 'Coin', title: 'Contracts', blurb: 'Bounties, deliveries and mercenary work.', switches: [
    { path: 'ItemEscrow', label: 'Item escrow' },
    { path: 'RebelsArePublicEnemies', label: 'Rebels are public enemies' }
  ] },
  // --- Players and safety
  { plugin: 'RealmHerald', group: 'Players and safety', title: 'The Herald', blurb: 'Welcome, first steps, the /realm hub, tips and popups.', switches: [
    { path: 'WelcomeNewPlayers', label: 'Welcome new players' },
    { path: 'FirstStepsPath', label: 'First steps' },
    { path: 'ShowMotdOnJoin', label: 'Message of the day' },
    { path: 'TipsEnabled', label: 'Tips' },
    { path: 'UsePopups', label: 'Popups' },
    { path: 'WelcomePopup', label: 'Welcome popup' }
  ] },
  { plugin: 'RealmWarden', group: 'Players and safety', title: 'The Warden', blurb: 'New-player protection, raid hours, combat log, reports, names and chat.', switches: [
    { path: 'NewPlayerProtection.Enabled', label: 'New-player protection' },
    { path: 'RaidHours.Enabled', label: 'Raid hours' },
    { path: 'CombatLog.Enabled', label: 'Combat-log flags' },
    { path: 'Reports.Enabled', label: '/warden report' },
    { path: 'Names.Enabled', label: 'Name rules' },
    { path: 'Chat.Enabled', label: 'Chat rules' },
    { path: 'Chat.FilterWords', label: 'Word filter' }
  ] },
  { plugin: 'RealmSentinel', group: 'Players and safety', title: 'The Sentinel', blurb: 'Server-side cheat watch. Start in watch mode; enforce after two clean weeks.', switches: [
    { path: 'General.Enabled', label: 'Sentinel', master: true },
    { path: 'Responses.Mode', label: 'Mode', kind: 'enum', options: ['watch', 'enforce'], danger: 'enforce', help: 'Enforce freezes and kicks by itself. ROADMAP SEN-4: only after two beta weeks with no alerts on honest players.' },
    { path: 'Movement.Enabled', label: 'Movement checks' },
    { path: 'Combat.Enabled', label: 'Combat checks' },
    { path: 'Items.Enabled', label: 'Item checks' },
    { path: 'Flood.Enabled', label: 'Chat and command floods' },
    { path: 'Connections.Enabled', label: 'Reconnect cycling' },
    { path: 'Names.Enabled', label: 'Staff-name impersonation' },
    { path: 'Responses.AutoBan', label: 'Ban by itself', danger: true, help: 'Off for 1.0 (ROADMAP SEN-4). A ban should be a person\'s decision.' },
    { path: 'Alerts.WriteFeed', label: 'Feed for this screen' }
  ] },
  { plugin: 'RealmStats', group: 'Players and safety', title: 'Statistics', blurb: 'Pseudonymous daily player statistics.', switches: [
    { path: 'AllowOptOut', label: 'Players may opt out' },
    { path: 'TrackDeaths', label: 'Count deaths' },
    { path: 'TrackHouses', label: 'Count houses' }
  ] },
  // --- The record and the world
  { plugin: 'RealmSeasons', group: 'Record and world', title: 'Seasons', blurb: 'Numbered seasons, standings, the Hall of Kings.', switches: [
    { path: 'AutoStartFirstSeason', label: 'Start Season 1 by itself' },
    { path: 'AutoEndSeason', label: 'End seasons on schedule' },
    { path: 'AutoStartNextSeason', label: 'Start the next season by itself' }
  ] },
  { plugin: 'RealmRenown', group: 'Record and world', title: 'Renown', blurb: 'Renown, infamy and titles worn in chat.', switches: [
    { path: 'ChatPrefixEnabled', label: 'Titles in chat' },
    { path: 'AnnounceTitles', label: 'Herald new titles' },
    { path: 'ChronicleTitles', label: 'Titles in the Chronicle' }
  ] },
  { plugin: 'RealmChronicle', group: 'Record and world', title: 'The Chronicle', blurb: 'The event log behind the overlay, portal and Discord.', switches: [
    { path: 'FloodBudget.Enabled', label: 'Flood budget per plugin' },
    { path: 'EchoToConsole', label: 'Echo entries to the console' }
  ] },
  { plugin: 'RealmPainter', group: 'Record and world', title: 'Painted signs', blurb: 'Realm\'s art and live boards on signs (/paint).', switches: [
    { path: 'Enabled', label: 'Sign painting', master: true },
    { path: 'LiveBoards', label: 'Live boards' },
    { path: 'ProtectBoundSigns', label: 'Protect bound signs' }
  ] },
  { plugin: 'RealmSculptor', group: 'Record and world', title: 'Monuments', blurb: 'Monuments from the game\'s own blocks (/sculpt).', switches: [
    { path: 'Enabled', label: 'Monuments', master: true },
    { path: 'ProtectSculptures', label: 'Protect monuments' },
    { path: 'GuardDecay', label: 'Guard against decay' }
  ] }
];

const GROUPS = ['Play', 'Crown and houses', 'Coin', 'Players and safety', 'Record and world'];

// ---------- a JSON scanner that keeps byte positions ----------

// Returns the value at path as { start, end, type, value } with text offsets, or null. Throws on bad JSON.
function locate(text, keys) {
  let i = 0;
  const ws = () => {
    while (i < text.length && /\s/.test(text[i])) i++;
  };
  const fail = (what) => {
    throw new SyntaxError(`${what} at position ${i}`);
  };
  function str() {
    if (text[i] !== '"') fail('expected a string');
    const s = i;
    i++;
    while (i < text.length && text[i] !== '"') i += text[i] === '\\' ? 2 : 1;
    if (i >= text.length) fail('unterminated string');
    i++;
    return JSON.parse(text.slice(s, i));
  }
  // Parses one value; when want is the remaining key path, returns the span found inside it.
  function value(want) {
    ws();
    const c = text[i];
    if (c === '{') {
      i++;
      ws();
      let found = null;
      if (text[i] === '}') {
        i++;
        return { found };
      }
      for (;;) {
        ws();
        const k = str();
        ws();
        if (text[i] !== ':') fail('expected :');
        i++;
        ws();
        const start = i;
        const r = value(want && want.length && want[0] === k ? want.slice(1) : null);
        if (want && want.length && want[0] === k) {
          if (want.length === 1) found = { start, end: i, raw: text.slice(start, i) };
          else if (r.found) found = r.found;
        }
        ws();
        if (text[i] === ',') {
          i++;
          continue;
        }
        if (text[i] === '}') {
          i++;
          return { found };
        }
        fail('expected , or }');
      }
    }
    if (c === '[') {
      i++;
      ws();
      if (text[i] === ']') {
        i++;
        return { found: null };
      }
      for (;;) {
        value(null);
        ws();
        if (text[i] === ',') {
          i++;
          continue;
        }
        if (text[i] === ']') {
          i++;
          return { found: null };
        }
        fail('expected , or ]');
      }
    }
    if (c === '"') {
      str();
      return { found: null };
    }
    const m = /^(true|false|null|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/.exec(text.slice(i, i + 64));
    if (!m) fail('unexpected character');
    i += m[0].length;
    return { found: null };
  }
  const t0 = text.charCodeAt(0) === 0xfeff ? 1 : 0;
  i = t0;
  const r = value(keys);
  ws();
  if (i !== text.length) fail('extra text after the JSON');
  if (!r.found) return null;
  const raw = r.found.raw;
  const type = raw === 'true' || raw === 'false' ? 'bool' : raw[0] === '"' ? 'string' : raw === 'null' ? 'null' : /^[[{]/.test(raw) ? 'object' : 'number';
  return { start: r.found.start, end: r.found.end, raw, type, value: type === 'bool' ? raw === 'true' : type === 'string' ? JSON.parse(raw) : undefined };
}

function getPath(obj, keys) {
  let o = obj;
  for (const k of keys) {
    if (!o || typeof o !== 'object' || Array.isArray(o) || !Object.prototype.hasOwnProperty.call(o, k)) return undefined;
    o = o[k];
  }
  return o;
}

// Every true/false leaf in the config (objects only, not inside lists), as dotted paths.
function boolLeaves(obj, prefix = '', out = []) {
  if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return out;
  for (const [k, v] of Object.entries(obj)) {
    if (typeof v === 'boolean') out.push(prefix + k);
    else if (v && typeof v === 'object' && !Array.isArray(v) && out.length < 400) boolLeaves(v, prefix + k + '.', out);
  }
  return out;
}

function sameExcept(a, b, keys) {
  const strip = (o) => {
    const c = JSON.parse(JSON.stringify(o));
    let p = c;
    for (const k of keys.slice(0, -1)) p = p[k];
    delete p[keys[keys.length - 1]];
    return JSON.stringify(c);
  };
  return strip(a) === strip(b);
}

// Pure edit: returns the new text, or throws a friendly error.
function editConfigText(text, dotted, next) {
  const keys = String(dotted).split('.');
  if (!keys.every((k) => /^[A-Za-z_][A-Za-z0-9_]{0,60}$/.test(k))) throw friendlyErr('That setting name is not valid.');
  let before;
  try {
    before = JSON.parse(text.replace(/^﻿/, ''));
  } catch (e) {
    throw friendlyErr(`The config file is damaged (${e.message.slice(0, 80)}). Steward does not edit a damaged file; fix it or delete it so the plugin writes its defaults.`);
  }
  if (!before || typeof before !== 'object' || Array.isArray(before)) throw friendlyErr('The config file is not a JSON object.');
  const at = locate(text, keys);
  if (!at) throw friendlyErr(`${dotted} is not in this config file. Start the server once with the plugin so it writes its full config.`);
  let raw;
  if (at.type === 'bool') {
    if (typeof next !== 'boolean') throw friendlyErr(`${dotted} is on or off.`);
    raw = next ? 'true' : 'false';
  } else if (at.type === 'string') {
    if (typeof next !== 'string' || !/^[a-z]{1,20}$/.test(next)) throw friendlyErr(`${dotted} takes a word.`);
    raw = JSON.stringify(next);
  } else throw friendlyErr(`${dotted} is not a switch.`);
  const out = text.slice(0, at.start) + raw + text.slice(at.end);
  const after = JSON.parse(out.replace(/^﻿/, ''));
  if (getPath(after, keys) !== next || !sameExcept(before, after, keys)) throw new Error('self-check failed: the edit changed more than one value');
  return { text: out, old: at.value, changed: at.raw !== raw };
}

function friendlyErr(message) {
  return Object.assign(new Error(message), { friendly: true });
}

// ---------- reading a server's configs ----------

async function readConfig(file) {
  let st;
  try {
    st = await fsp.stat(file);
  } catch {
    return { state: 'missing' };
  }
  if (st.size > MAX_CONFIG_BYTES) return { state: 'damaged', reason: 'larger than 2 MB' };
  const text = await fsp.readFile(file, 'utf8');
  try {
    const obj = JSON.parse(text.replace(/^﻿/, ''));
    if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return { state: 'damaged', reason: 'not a JSON object' };
    return { state: 'ok', obj, text, mtimeMs: st.mtimeMs, size: st.size };
  } catch (e) {
    return { state: 'damaged', reason: e.message.slice(0, 120) };
  }
}

// oxideDir: <server>\oxide. Returns the screen's model.
async function listFeatures(oxideDir) {
  const out = [];
  for (const entry of CATALOGUE) {
    const installed = await fsp.access(path.join(oxideDir, 'plugins', `${entry.plugin}.cs`)).then(() => true, () => false);
    const cfg = await readConfig(path.join(oxideDir, 'config', `${entry.plugin}.json`));
    const switches = entry.switches.map((s) => {
      const v = cfg.state === 'ok' ? getPath(cfg.obj, s.path.split('.')) : undefined;
      const kind = s.kind || 'bool';
      const present = kind === 'bool' ? typeof v === 'boolean' : typeof v === 'string';
      return { path: s.path, label: s.label, kind, options: s.options || null, master: !!s.master, danger: s.danger || false, help: s.help || null, present, value: present ? v : null };
    });
    const known = new Set(entry.switches.map((s) => s.path));
    const extra = cfg.state === 'ok' ? boolLeaves(cfg.obj).filter((p) => !known.has(p)).map((p) => ({ path: p, value: getPath(cfg.obj, p.split('.')) })) : [];
    out.push({ plugin: entry.plugin, group: entry.group, title: entry.title, blurb: entry.blurb, installed, config: cfg.state, reason: cfg.reason || null, switches, extra });
  }
  return out;
}

// Changes one switch. deps: { reload(plugin) -> Promise<{ok,result}>|null, log(entry) }
async function setFeature(root, oxideDir, plugin, dotted, next, deps = {}) {
  if (!PLUGIN_RE.test(String(plugin))) throw friendlyErr('Unknown plugin.');
  const entry = CATALOGUE.find((e) => e.plugin === plugin);
  if (!entry) throw friendlyErr(`${plugin} has no switches on this screen.`);
  const file = path.join(oxideDir, 'config', `${plugin}.json`);
  const cfg = await readConfig(file);
  if (cfg.state === 'missing') throw friendlyErr(`${plugin} has not written its config yet (oxide\\config\\${plugin}.json). Start the server once with the plugin, then try again.`);
  if (cfg.state === 'damaged') throw friendlyErr(`oxide\\config\\${plugin}.json is damaged (${cfg.reason}). Steward does not edit a damaged file.`);
  const sw = entry.switches.find((s) => s.path === dotted);
  if (sw && sw.kind === 'enum' && !sw.options.includes(next)) throw friendlyErr(`${sw.label} is one of: ${sw.options.join(', ')}.`);
  if (!sw && !(typeof next === 'boolean' && typeof getPath(cfg.obj, String(dotted).split('.')) === 'boolean')) throw friendlyErr(`${dotted} is not a switch in ${plugin}'s config.`);
  const edit = editConfigText(cfg.text, dotted, next);
  if (!edit.changed) return { plugin, path: dotted, value: next, changed: false, backup: null, reload: null };
  // Refuse if the plugin (or anyone) rewrote the file meanwhile.
  const now = await fsp.stat(file);
  if (now.mtimeMs !== cfg.mtimeMs || now.size !== cfg.size) throw friendlyErr(`${plugin}'s config changed on disk while Steward was reading it. Look again and retry.`);
  const backupDir = path.join(root, '_realm-backups', `config-${S.timestamp()}`);
  await fsp.mkdir(backupDir, { recursive: true });
  const backup = path.join(backupDir, `${plugin}.json`);
  await fsp.copyFile(file, backup);
  const tmp = `${file}.realm-part`;
  await fsp.writeFile(tmp, edit.text);
  await fsp.rename(tmp, file);
  if (deps.log) await deps.log({ plugin, path: dotted, from: edit.old, to: next });
  const reload = deps.reload ? await deps.reload(plugin) : null;
  return { plugin, path: dotted, value: next, changed: true, backup, reload };
}

// deps: { handle, instOf, rootOf(id), oxideDir(root), court, mgr(id), log }
function registerSteward(deps) {
  const idOf = (id) => deps.instOf(id === undefined ? 's1' : id).id;
  async function where(id) {
    const root = deps.rootOf(id);
    if (!root) throw friendlyErr('No server copy is set up yet.');
    const ox = (await deps.oxideDir(root)) || path.join(root, 'oxide');
    return { root, ox };
  }
  deps.handle('features:list', async (id) => {
    const sid = idOf(id);
    const w = await where(sid);
    const m = deps.mgr(sid);
    return { id: sid, running: m.isRunning(), console: !!(deps.court && deps.court.consoleReady(sid)), configDir: path.join(w.ox, 'config'), groups: GROUPS, plugins: await listFeatures(w.ox) };
  });
  deps.handle('features:set', async (id, plugin, dotted, value) => {
    const sid = idOf(id);
    const w = await where(sid);
    const res = await setFeature(w.root, w.ox, String(plugin), String(dotted), value, {
      log: async (e) => {
        if (deps.court && deps.court.courtLog) await deps.court.courtLog.append({ server: sid, action: 'feature', target: e.plugin, reason: `${e.path}: ${e.from} -> ${e.to}`, ok: true, via: 'features' }).catch(() => {});
        const m = deps.mgr(sid);
        if (m) m.log('sys', `Realm features: ${e.plugin} ${e.path} ${e.from} -> ${e.to}.`);
      },
      reload: async (p) => (deps.court && deps.court.consoleReady(sid) ? deps.court.act(sid, 'reload', { plugin: p }).catch((e) => ({ ok: false, result: e.message })) : null)
    });
    return { ...res, running: deps.mgr(sid).isRunning() };
  });
}

module.exports = { CATALOGUE, GROUPS, locate, editConfigText, getPath, boolLeaves, readConfig, listFeatures, setFeature, registerSteward };
