'use strict';

// The Court: moderation actions for Realm Steward, built on the game's own console commands.
// Every command name, argument order and output format here was read from the decompiled command
// handlers ([DEC] CodeHatch.Engine.CoreCommandHandler, CodeHatch.Thrones.ThronesCommandHandler)
// unless marked UNVERIFIED. docs/admin-console.md lists them with their sources.
//
// The console runs commands as the server player, which has every permission
// ([DEC] PlayerExtensions.HasPermission: player.IsServer -> true). Commands only work while
// enableCommands is True in ServerSettings.cfg (the default) ([DEC] CommandManager.ExecuteCommand).

const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const AC = require('./admin-console');

const MAX_REASON = 200;
const MAX_MESSAGE = 300;
const MAX_NAME = 64;

function clean(text, max, what) {
  const s = String(text == null ? '' : text).replace(/[\r\n\t]+/g, ' ').replace(/\s+/g, ' ').trim();
  if (s.length > max) throw new Error(`${what} is limited to ${max} characters.`);
  return s;
}

// Free text that goes through the game's argument parser (reasons, notices): it splits on spaces
// and swallows quote characters ([DEC] CommandInfo.Args), so quotes become a backtick.
function argText(text, max, what) {
  return clean(text, max, what).replace(/["']/g, '`');
}

function needName(name) {
  const s = clean(name, MAX_NAME, 'A player name');
  if (!s) throw new Error('Choose a player first.');
  return s;
}

// Each builder returns { command, summary }. The command is exactly one console line.
const BUILD = {
  // [DEC] ThronesCommandHandler.list (aliases online, players): "Online Players(N):\nA, B" or
  // "There are no players online."
  list: () => ({ command: '/list', summary: 'List online players' }),

  // [DEC] CoreCommandHandler.Kick: /kick [userName] (reason...). The game joins the reason words
  // WITHOUT spaces (string.Join(string.Empty, ...)), so Realm sends the reason as one quoted
  // argument to keep its spaces. Matching is the game's MatchFirstPlayerByName. UNVERIFIED: whether
  // that prefers an exact name over a prefix match; the Court always sends the full listed name.
  kick: ({ name, reason }) => {
    const n = needName(name);
    const r = argText(reason, MAX_REASON, 'The reason');
    return { command: `/kick ${AC.quoteArg(n)}${r ? ' ' + AC.quoteArg(r) : ''}`, summary: `Kick ${n}${r ? ` (${r})` : ''}` };
  },

  // [DEC] CoreCommandHandler.Ban: /ban [userName] (days|reason). A whole number of days first
  // (0 or absent = forever), then the reason. Offline players are found in the user registry.
  ban: ({ name, days, reason }) => {
    const n = needName(name);
    const r = argText(reason, MAX_REASON, 'The reason');
    const d = days == null || days === '' ? 0 : Number(days);
    if (!Number.isInteger(d) || d < 0 || d > 36500) throw new Error('Days must be a whole number from 0 (forever) to 36500.');
    const parts = [`/ban ${AC.quoteArg(n)}`];
    if (d > 0) parts.push(String(d));
    // For a ban forever the game reads the reason from the 2nd word, after trying it as days: a
    // reason that starts with a number would become the number of days.
    if (r) parts.push(d === 0 && /^-?\d+$/.test(r.split(' ')[0]) ? `Reason: ${r}` : r);
    return { command: parts.join(' '), summary: `Ban ${n} ${d > 0 ? `for ${d} day${d === 1 ? '' : 's'}` : 'forever'}${r ? ` (${r})` : ''}` };
  },

  // [DEC] CoreCommandHandler.Unban: /unban [userName|index|steamId].
  unban: ({ name }) => {
    const n = needName(name);
    return { command: `/unban ${AC.quoteArg(n)}`, summary: `Unban ${n}` };
  },

  // [DEC] CoreCommandHandler.Banlist: one line per ban "#i Name |id| (ip) <N days left>".
  banlist: () => ({ command: '/banlist', summary: 'Read the ban list' }),

  // [DEC] CoreCommandHandler.notice: on-screen notice to everyone (Server.Notice).
  notice: ({ message }) => {
    const m = argText(message, MAX_MESSAGE, 'The message');
    if (!m) throw new Error('Write the message first.');
    return { command: `/notice ${m}`, summary: `Notice: ${m}` };
  },

  // [DEC] ThronesCommandHandler popup (alias alert): a popup window for every player.
  popup: ({ message }) => {
    const m = argText(message, MAX_MESSAGE, 'The message');
    if (!m) throw new Error('Write the message first.');
    return { command: `/popup ${m}`, summary: `Popup: ${m}` };
  },

  // [DEC] Console.Submit: text without "/" is said in chat by the server.
  say: ({ message }) => {
    const m = clean(message, MAX_MESSAGE, 'The message');
    if (!m) throw new Error('Write the message first.');
    if (m.startsWith('/')) throw new Error('A chat message cannot start with "/".');
    return { command: m, chat: true, summary: `Chat: ${m}` };
  },

  // [DEC] CoreCommandHandler whitelist subcommands enable|disable.
  whitelist: ({ on }) => ({ command: on ? '/whitelist enable' : '/whitelist disable', summary: on ? 'Whitelist on' : 'Whitelist off' }),

  // [DEC] CoreCommandHandler.Mute: /mute [userName|id] (days); /unmute [userName|id].
  mute: ({ name, days }) => {
    const n = needName(name);
    const d = days == null || days === '' ? 0 : Number(days);
    if (!Number.isInteger(d) || d < 0 || d > 36500) throw new Error('Days must be a whole number (0 = forever).');
    return { command: `/mute ${AC.quoteArg(n)}${d > 0 ? ' ' + d : ''}`, summary: `Mute ${n}${d > 0 ? ` for ${d} days` : ''}` };
  },
  unmute: ({ name }) => {
    const n = needName(name);
    return { command: `/unmute ${AC.quoteArg(n)}`, summary: `Unmute ${n}` };
  },

  // There is no save command in the game. /realm.save comes from plugins/RealmCourt.cs, which
  // registers it in the game's own command table (server-only permission) and calls Game.Save().
  save: () => ({ command: '/realm.save', summary: 'Save the world', needsPlugin: 'RealmCourt' }),

  // Structured player list from plugins/RealmCourt.cs: names and Steam IDs.
  roster: () => ({ command: '/realm.players', summary: 'Read the roster', needsPlugin: 'RealmCourt' }),

  // [DEC] CoreCommandHandler.Shutdown: saves and stops; with the admin console attached the game
  // also sends a Disconnect packet (RestartAfterShutdown = false).
  shutdown: () => ({ command: '/shutdown', summary: 'Save and shut down' }),

  // Oxide's own console command (docs/oxide-rok-api.md section 6.5, src/ReignOfKingsCore.cs:88-103):
  // re-reads the plugin, its config (oxide/config/<Plugin>.json) and its data. Used after Steward
  // changes a plugin's config or data files. UNVERIFIED over the admin console socket on the real
  // server (launcher/README.md, Not verified, test F1).
  reload: ({ plugin }) => {
    const p = pluginName(plugin);
    return { command: `/oxide.reload ${p}`, summary: `Reload ${p}` };
  }
};

function pluginName(name) {
  const s = String(name == null ? '' : name).trim();
  if (!/^[A-Z][A-Za-z0-9]{2,40}$/.test(s)) throw new Error('Choose a plugin first.');
  return s;
}

const ACTIONS = Object.keys(BUILD);

function build(action, args = {}) {
  if (!Object.prototype.hasOwnProperty.call(BUILD, action)) throw new Error(`Unknown Court action "${action}".`);
  return BUILD[action](args && typeof args === 'object' ? args : {});
}

// Admin commands of the Realm plugins (plugins/*.cs). They are chat commands checked against an
// Oxide permission on the PLAYER who types them. From the server console Oxide only runs a chat
// command when it can find the sender as a Covalence player ([DEC] Oxide.ReignOfKings
// ReignOfKingsCore.IOnServerCommand), and the server is not one, so these are for an admin to
// type in game. UNVERIFIED at run time; the Court therefore shows them with a Copy button.
const PLUGIN_ADMIN = [
  { plugin: 'RealmHouses', perm: 'realmhouses.admin', template: '/house sync', label: 'Re-sync houses with guilds', args: [] },
  { plugin: 'RealmHouses', perm: 'realmhouses.admin', template: '/house disband {house}', label: 'Disband a house', args: ['house'] },
  { plugin: 'RealmHouses', perm: 'realmhouses.admin', template: '/house pardon {house}', label: 'Pardon a house (clear broken oaths)', args: ['house'] },
  { plugin: 'RealmHouses', perm: 'realmhouses.admin', template: '/house unlink', label: 'Unlink your house from its guild', args: [] },
  { plugin: 'CrownAndConsequences', perm: 'crownandconsequences.admin', template: '/claim cancel {house}', label: 'Set aside a claim on the throne', args: ['house'] },
  { plugin: 'CrownAndConsequences', perm: 'crownandconsequences.admin', template: '/council', label: 'Change the council (admin may skip the cooldown)', args: [] },
  { plugin: 'CrownAndConsequences', perm: null, template: '/crown', label: 'Show the crown', args: [] },
  { plugin: 'RealmContracts', perm: 'realmcontracts.admin', template: '/contract admin cancel {id}', label: 'Cancel a contract', args: ['id'] },
  { plugin: 'RealmContracts', perm: 'realmcontracts.admin', template: '/contract admin refund {id}', label: 'Refund a contract to its poster', args: ['id'] },
  { plugin: 'RealmContracts', perm: 'realmcontracts.admin', template: '/contract admin pay {id}', label: 'Pay a contract to its fulfiller', args: ['id'] },
  // RealmSentinel (plugins/docs/RealmSentinel.md, Commands). The Sentinel screen fills these per suspect.
  { plugin: 'RealmSentinel', perm: 'realmsentinel.admin', template: '/sentinel status', label: 'Sentinel: mode, checks and the highest scores', args: [] },
  { plugin: 'RealmSentinel', perm: 'realmsentinel.admin', template: '/sentinel report {player}', label: 'Sentinel: evidence for one player', args: ['player'] },
  { plugin: 'RealmSentinel', perm: 'realmsentinel.admin', template: '/sentinel freeze {player} {minutes}', label: 'Sentinel: freeze a player (1-1440 minutes)', args: ['player', 'minutes'] },
  { plugin: 'RealmSentinel', perm: 'realmsentinel.admin', template: '/sentinel unfreeze {player}', label: 'Sentinel: lift a freeze', args: ['player'] },
  { plugin: 'RealmSentinel', perm: 'realmsentinel.admin', template: '/sentinel clear {player}', label: 'Sentinel: clear a score (evidence is kept)', args: ['player'] },
  { plugin: 'RealmSentinel', perm: 'realmsentinel.admin', template: '/sentinel ban {player} confirm', label: 'Sentinel: ban with the evidence kept', args: ['player'] },
  { plugin: 'RealmSentinel', perm: 'realmsentinel.admin', template: '/sentinel peaks', label: 'Sentinel: highest honest values (for tuning)', args: [] },
  { plugin: 'RealmSentinel', perm: 'realmsentinel.admin', template: '/sentinel reload', label: 'Sentinel: re-read its config', args: [] },
  // RealmSculptor, RealmPainter, RealmLegendary (their guides in plugins/docs/). ROADMAP STW-2.
  { plugin: 'RealmSculptor', perm: 'realmsculptor.admin', template: '/sculpt status', label: 'Monuments: totals, running job, protection', args: [] },
  { plugin: 'RealmSculptor', perm: 'realmsculptor.admin', template: '/sculpt placed', label: 'Monuments: the newest placements', args: [] },
  { plugin: 'RealmSculptor', perm: 'realmsculptor.admin', template: '/sculpt list', label: 'Monuments: sculptures loaded and files refused', args: [] },
  { plugin: 'RealmSculptor', perm: 'realmsculptor.admin', template: '/sculpt repair {n}', label: 'Monuments: put back missing blocks of placement #n', args: ['n'] },
  { plugin: 'RealmSculptor', perm: 'realmsculptor.admin', template: '/sculpt reload', label: 'Monuments: read the sculpture files again', args: [] },
  { plugin: 'RealmPainter', perm: 'realmpainter.admin', template: '/paint status', label: 'Signs: queue, art version, sources', args: [] },
  { plugin: 'RealmPainter', perm: 'realmpainter.admin', template: '/paint signs', label: 'Signs: every bound sign', args: [] },
  { plugin: 'RealmPainter', perm: 'realmpainter.admin', template: '/paint redraw all', label: 'Signs: draw every sign again', args: [] },
  { plugin: 'RealmPainter', perm: 'realmpainter.admin', template: '/paint reload', label: 'Signs: read the art bundle again', args: [] },
  { plugin: 'RealmLegendary', perm: 'realmlegendary.admin', template: '/ironbreaker status', label: 'Ironbreaker: who bears it and since when', args: [] },
  { plugin: 'RealmLegendary', perm: 'realmlegendary.admin', template: '/ironbreaker revoke', label: 'Ironbreaker: back to the armoury', args: [] },
  { plugin: 'RealmLegendary', perm: 'realmlegendary.admin', template: '/ironbreaker grant {player}', label: 'Ironbreaker: give it to an online player', args: ['player'] },
  // RealmLaws (plugins/docs/RealmLaws.md). ROADMAP STW-2.
  { plugin: 'RealmLaws', perm: null, template: '/law zone list', label: 'Laws: the law zones', args: [] },
  { plugin: 'RealmLaws', perm: 'realmlaws.admin', template: '/court admin dismiss {case}', label: 'Court: dismiss an open case', args: ['case'] },
  { plugin: 'RealmLaws', perm: 'realmlaws.admin', template: '/court admin verdict {case} {ruling}', label: 'Court: rule on a case (guilty or innocent)', args: ['case', 'ruling'] },
  { plugin: 'RealmLaws', perm: 'realmlaws.admin', template: '/court admin clear {player}', label: 'Court: clear a player\'s sentences and cases', args: ['player'] },
  // Wave 3 (their guides in plugins/docs/). The plugins do the work; Steward only fills the command.
  { plugin: 'RealmQuests', perm: 'realmquests.admin', template: '/quest admin status', label: 'Quests: content files, problems, owed rewards', args: [] },
  { plugin: 'RealmQuests', perm: 'realmquests.admin', template: '/quest admin reload', label: 'Quests: read the content files again', args: [] },
  { plugin: 'RealmQuests', perm: 'realmquests.admin', template: '/quest admin places', label: 'Quests: named places and whether each is marked', args: [] },
  { plugin: 'RealmQuests', perm: 'realmquests.admin', template: '/quest admin reset {player} {board}', label: 'Quests: reset a player\'s board or story', args: ['player', 'board'] },
  { plugin: 'RealmTravel', perm: 'realmtravel.admin', template: '/travel admin status', label: 'Travel: counters and integrations', args: [] },
  { plugin: 'RealmTravel', perm: 'realmtravel.admin', template: '/travel admin list', label: 'Travel: every waystone', args: [] },
  { plugin: 'RealmTravel', perm: 'realmtravel.admin', template: '/kit admin check', label: 'Kits: check every kit\'s items', args: [] },
  { plugin: 'RealmTravel', perm: 'realmtravel.admin', template: '/kit admin reset {player} all', label: 'Kits: clear a player\'s kit claims', args: ['player'] },
  { plugin: 'RealmArena', perm: 'realmarena.admin', template: '/arena admin status', label: 'Arena: duels, games, payouts waiting', args: [] },
  { plugin: 'RealmArena', perm: 'realmarena.admin', template: '/arena admin void {duel}', label: 'Arena: call a duel off, stakes back', args: ['duel'] },
  { plugin: 'RealmArena', perm: 'realmarena.admin', template: '/arena admin unbar {player}', label: 'Arena: lift a bar from the ring', args: ['player'] },
  { plugin: 'RealmArena', perm: 'realmarena.admin', template: '/arena admin settle', label: 'Arena: retry waiting payouts now', args: [] },
  { plugin: 'RealmDominion', perm: 'realmdominion.admin', template: '/dominion admin status', label: 'Dominion: field state, holdings, last payday', args: [] },
  { plugin: 'RealmDominion', perm: 'realmdominion.admin', template: '/dominion admin open {minutes}', label: 'Dominion: open the field by hand', args: ['minutes'] },
  { plugin: 'RealmDominion', perm: 'realmdominion.admin', template: '/dominion admin close', label: 'Dominion: close the field', args: [] },
  { plugin: 'RealmDominion', perm: 'realmdominion.admin', template: '/dominion admin auto', label: 'Dominion: back to the schedule', args: [] },
  { plugin: 'RealmWorld', perm: 'realmworld.admin', template: '/world admin status', label: 'World: what runs, festival, postponed events', args: [] },
  { plugin: 'RealmWorld', perm: 'realmworld.admin', template: '/world admin schedule', label: 'World: the schedule as players see it', args: [] },
  { plugin: 'RealmWorld', perm: 'realmworld.admin', template: '/world admin stop', label: 'World: call off what runs (no rewards)', args: [] },
  { plugin: 'RealmWorld', perm: 'realmworld.admin', template: '/world admin census', label: 'World: take the census now', args: [] },
  { plugin: 'RealmCrafts', perm: 'realmcrafts.admin', template: '/craft admin status', label: 'Guilds: counts, items, next crowning', args: [] },
  { plugin: 'RealmCrafts', perm: 'realmcrafts.admin', template: '/craft admin unmapped', label: 'Guilds: products that matched no profession', args: [] },
  { plugin: 'RealmCrafts', perm: 'realmcrafts.admin', template: '/craft admin cancel {commission}', label: 'Guilds: cancel a commission, hold back to poster', args: ['commission'] },
  { plugin: 'RealmHeraldry', perm: 'realmheraldry.admin', template: '/heraldry status', label: 'Heraldry: switches, last sync, ballots open', args: [] },
  { plugin: 'RealmHeraldry', perm: 'realmheraldry.admin', template: '/heraldry preview', label: 'Heraldry: what a sync would change', args: [] },
  { plugin: 'RealmHeraldry', perm: 'realmheraldry.admin', template: '/heraldry sync', label: 'Heraldry: bring every guild in step now', args: [] },
  { plugin: 'RealmHeraldry', perm: 'realmheraldry.admin', template: '/ballot admin audit {ballot}', label: 'Council: votes by house, to look for alts', args: ['ballot'] },
  { plugin: 'RealmHeraldry', perm: 'realmheraldry.admin', template: '/ballot admin cancel {ballot}', label: 'Council: call a ballot off, deposits back', args: ['ballot'] }
];

// Placeholder rules: a player name is quoted when it has spaces (the plugins read "Old Tom" as one
// argument); numbers are whole numbers in their command's range.
const ARG_RULES = {
  player: (v) => {
    const n = v.replace(/["']/g, '');
    if (!n) throw new Error('Fill in "player" first.');
    return /\s/.test(n) ? `"${n}"` : n;
  },
  minutes: (v) => {
    const n = Number(v);
    if (!Number.isInteger(n) || n < 1 || n > 1440) throw new Error('Minutes must be a whole number from 1 to 1440.');
    return String(n);
  },
  n: (v) => {
    const n = Number(String(v).replace(/^#/, ''));
    if (!Number.isInteger(n) || n < 1 || n > 99999) throw new Error('Use the placement number from /sculpt placed.');
    return String(n);
  },
  // Numbered records (court cases, duels, commissions, ballots); the plugins accept an optional #.
  case: numbered('case'),
  duel: numbered('duel'),
  commission: numbered('commission'),
  ballot: numbered('ballot'),
  // RealmLaws treats anything but "guilty" as an acquittal, so the word is checked here.
  ruling: oneOf('ruling', ['guilty', 'innocent']),
  board: oneOf('board', ['daily', 'weekly', 'story', 'all'])
};

function numbered(what) {
  return (v) => {
    const n = Number(String(v).replace(/^#/, ''));
    if (!Number.isInteger(n) || n < 1 || n > 999999) throw new Error(`Use the ${what} number (for example 12 or #12).`);
    return String(n);
  };
}

function oneOf(what, words) {
  return (v) => {
    const w = String(v).trim().toLowerCase();
    if (!words.includes(w)) throw new Error(`The ${what} is one of: ${words.join(', ')}.`);
    return w;
  };
}

function pluginCommand(index, values = {}) {
  const def = PLUGIN_ADMIN[index];
  if (!def) throw new Error('Unknown plugin command.');
  return def.template.replace(/\{(\w+)\}/g, (_m, k) => {
    const v = clean(values[k], MAX_NAME, k);
    if (!v) throw new Error(`Fill in "${k}" first.`);
    return ARG_RULES[k] ? ARG_RULES[k](v) : v;
  });
}

// The command for a template, by its text (the Sentinel screen fills /sentinel commands per player).
function pluginCommandFor(template, values = {}) {
  const i = PLUGIN_ADMIN.findIndex((d) => d.template === template);
  if (i < 0) throw new Error('Unknown plugin command.');
  return pluginCommand(i, values);
}

// ---------- reading command output ----------

function texts(lines) {
  return (lines || []).map((l) => (typeof l === 'string' ? l : l.text)).filter((t) => typeof t === 'string');
}

// /list output. Returns { count, names } or null when the lines hold no list.
function parsePlayerList(lines) {
  const t = texts(lines);
  for (let i = 0; i < t.length; i++) {
    if (/^There are no players online\.?$/.test(t[i].trim())) return { count: 0, names: [] };
    const m = /^Online Players\((\d+)\):\s*$/.exec(t[i].trim());
    if (m) {
      const count = Number(m[1]);
      const rest = (t[i + 1] || '').trim();
      // Names are joined with ", " by the game; a name containing ", " cannot be told apart.
      const names = rest ? rest.split(', ').map((s) => s.trim()).filter(Boolean) : [];
      return { count, names };
    }
  }
  return null;
}

// RealmCourt roster: "REALMCOURT|<seq>|players|<n>" then "REALMCOURT|<seq>|p|<steamId>|<name>".
function parseRoster(lines) {
  const t = texts(lines);
  let seq = null;
  let count = null;
  const players = [];
  for (const line of t) {
    const parts = line.trim().split('|');
    if (parts[0] !== 'REALMCOURT') continue;
    if (parts[2] === 'players') {
      seq = parts[1];
      count = Number(parts[3]);
      players.length = 0;
    } else if (parts[2] === 'p' && parts[1] === seq) {
      players.push({ id: parts[3], name: parts.slice(4).join('|') });
    }
  }
  return count == null ? null : { count, players };
}

// /banlist output.
function parseBanList(lines) {
  const out = [];
  for (const line of texts(lines)) {
    const m = /^#(\d+) (.*) \|(\d+)\| \((.*)\) <(.+) left>$/.exec(line.trim());
    if (m) out.push({ index: Number(m[1]), name: m[2], id: m[3], ip: m[4], left: m[5] });
  }
  return out;
}

// True when the game rejected the command outright ([DEC] PlayerListener.OnPlayerCommand).
function unknownCommand(lines) {
  return texts(lines).some((t) => /^Unknown command '/.test(t));
}

// ---------- moderation log ----------

// Every Court action is appended to <userData>\court\court-log.jsonl (one JSON object per line).
// The file is rotated at 2 MB (court-log.1.jsonl keeps the previous one).
class CourtLog {
  constructor(dir, { maxBytes = 2 * 1024 * 1024 } = {}) {
    this.dir = dir;
    this.file = path.join(dir, 'court-log.jsonl');
    this.maxBytes = maxBytes;
  }

  async append(entry) {
    const rec = {
      at: new Date().toISOString(),
      server: String(entry.server || ''),
      action: String(entry.action || ''),
      target: entry.target ? String(entry.target).slice(0, MAX_NAME) : null,
      reason: entry.reason ? String(entry.reason).slice(0, MAX_REASON) : null,
      command: entry.command ? String(entry.command).slice(0, AC.MAX_COMMAND) : null,
      ok: entry.ok !== false,
      result: entry.result ? String(entry.result).slice(0, 1000) : null,
      via: entry.via || 'console'
    };
    await fsp.mkdir(this.dir, { recursive: true });
    try {
      const st = await fsp.stat(this.file);
      if (st.size > this.maxBytes) await fsp.rename(this.file, path.join(this.dir, 'court-log.1.jsonl'));
    } catch {
      /* no log yet */
    }
    await fsp.appendFile(this.file, JSON.stringify(rec) + '\n');
    return rec;
  }

  // Newest first.
  async read({ limit = 200, server = null } = {}) {
    let text = '';
    try {
      text = await fsp.readFile(this.file, 'utf8');
    } catch {
      return [];
    }
    const out = [];
    const lines = text.split('\n');
    for (let i = lines.length - 1; i >= 0 && out.length < limit; i--) {
      if (!lines[i].trim()) continue;
      try {
        const rec = JSON.parse(lines[i]);
        if (!server || rec.server === server) out.push(rec);
      } catch {
        /* a torn line from a crash: skip it */
      }
    }
    return out;
  }
}

// ---------- per-server console preference ----------

// Whether Steward starts ROK.exe with -cport and holds the admin console. Default on. Kept in
// <userData>\court\console.json so it does not depend on the shape of the main settings file.
class ConsolePrefs {
  constructor(dir) {
    this.file = path.join(dir, 'console.json');
    this.data = {};
    try {
      const raw = JSON.parse(fs.readFileSync(this.file, 'utf8'));
      if (raw && typeof raw === 'object' && !Array.isArray(raw)) this.data = raw;
    } catch {
      /* first run */
    }
  }

  enabled(id) {
    const v = this.data[id];
    return !(v && v.enabled === false);
  }

  async set(id, enabled) {
    if (!/^s[1-4]$/.test(id)) throw new Error('Unknown server.');
    this.data[id] = { ...(this.data[id] || {}), enabled: !!enabled };
    await fsp.mkdir(path.dirname(this.file), { recursive: true });
    await fsp.writeFile(this.file, JSON.stringify(this.data, null, 2));
    return this.enabled(id);
  }
}

module.exports = {
  ACTIONS,
  PLUGIN_ADMIN,
  build,
  pluginCommand,
  pluginCommandFor,
  parsePlayerList,
  parseRoster,
  parseBanList,
  unknownCommand,
  CourtLog,
  ConsolePrefs
};
