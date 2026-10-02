'use strict';

// Emulation of the game's config file reader/writer, SimplifiedProperties.PropertyFile /
// PropertyList / PropertyEntry, and of ServerSettingsFile.Load (CodeHatch.Engine.Networking), read
// from the decompiled Assembly-CSharp.dll of Oxide.ReignOfKings 2.0.3867. The quirks are kept on
// purpose, because a tool that writes ServerSettings.cfg must survive them on the real server:
//
//  - The key is line.Substring(0, indexOf('=') - 1): the one character before '=' is DROPPED, so
//    "maxPlayers='5'" defines the key "maxPlayer". Write "key = 'value'".
//  - '#' starts a comment anywhere on the line, so a value containing '#' must be written as "\#".
//    A '#' before the '=' makes Substring throw (ArgumentOutOfRangeException) and the load fails.
//  - Keys are case-insensitive; a repeated key becomes "<key>2", "<key>3" ... (Tools.GetAlias).
//  - A key the game does not read is kept, but SAVED AS A COMMENT ("# gameMode = \'PvP\'").
//  - The game rewrites the whole file on every start (DedicatedServerBypass.StartServer -> Save) and
//    again when the world slot is chosen. Values it does read keep their text; comments after them are
//    replaced by the game's own comment.
//  - A value that does not convert (Convert.ChangeType) throws PropertyException
//    "Tried to get a <Type> from '<value>'"; ServerSettingsFile.Load logs it and the program exits.
//  - Lines end with CRLF (TextWriter.WriteLine on Windows).

const fs = require('fs');
const path = require('path');

class PropertyException extends Error {
  constructor(message, exceptionType = 'PropertyException') {
    super(message);
    this.exceptionType = exceptionType;
  }
}

const NL = '\r\n';

function replaceCharacters(line) {
  return line.split('\\#').join('%$HASH$%').split("\\'").join('%$QUOTE$%').split('\\\u2018').join('%$LQUOTE$%').split('\\\u2019').join('%$RQUOTE$%');
}
function restoreCharacters(line) {
  return line.split('\\n').join('\n').split('\\r').join('\r').split('%$HASH$%').join('#').split('%$QUOTE$%').join("'").split('%$LQUOTE$%').join('\u2018').split('%$RQUOTE$%').join('\u2019');
}
function escapeCharacters(line) {
  return line.split('\r').join('\\r').split('\n').join('\\n').split('#').join('\\#').split("'").join("\\'").split('\u2018').join('\\\u2018').split('\u2019').join('\\\u2019');
}
function trimQuotes(s) {
  return s.replace(/^[ '\u2018\u2019]+|[ '\u2018\u2019]+$/g, '');
}
// .NET String.Trim() trims Unicode white space; close enough for config text.
const netTrim = (s) => s.replace(/^[\s\uFEFF]+|[\s\uFEFF]+$/g, '');

// C# String.Substring(start, length) semantics, including its exceptions.
function substring(s, start, length) {
  if (start < 0 || length < 0 || start + length > s.length) {
    throw new PropertyException('Index and length must refer to a location within the string.', 'ArgumentOutOfRangeException');
  }
  return s.substr(start, length);
}

// Entry kinds: 'entry' (key = 'value'), 'comment' (Key "#"), 'newline' (Key "\n"), 'array' ("- 'v'"),
// 'raw' (a '{' list or '}' line, kept verbatim: no ServerSettings key lives in a list).
class PropertyFile {
  constructor(file) {
    this.file = file;
    this.entries = [];
    this.firstCreate = false;
    this.modified = false;
  }

  aliasFor(key) {
    let text = key;
    let n = 2;
    const taken = (k) => this.entries.some((e) => e.kind === 'entry' && e.key.toLowerCase() === k.toLowerCase());
    while (taken(text)) text = key + n++;
    return text;
  }

  // PropertyFile.Load: creates an empty file when there is none (FirstCreate, Modified).
  load() {
    if (!fs.existsSync(this.file)) {
      this.firstCreate = true;
      fs.mkdirSync(path.dirname(this.file), { recursive: true });
      fs.writeFileSync(this.file, '');
      this.modified = true;
    }
    const text = fs.readFileSync(this.file, 'utf8').replace(/^\uFEFF/, '');
    const lines = text.length ? text.split(/\r\n|\n|\r/) : [];
    if (lines.length && lines[lines.length - 1] === '') lines.pop(); // TextReader.Peek stops at EOF
    this.entries = [];
    for (const raw of lines) {
      const line = netTrim(raw);
      if (line.startsWith('#') || line.startsWith('=')) this.entries.push(this.parseComment(line));
      else if (line.startsWith('-')) this.entries.push(this.parseArray(line));
      else if (line === '') this.entries.push({ kind: 'newline', loaded: false });
      else if (line.includes('=')) this.entries.push(this.parseEntry(line));
      else if (line.includes('{') || line.includes('}')) this.entries.push({ kind: 'raw', text: line, loaded: true });
      else this.entries.push(this.parseComment(line));
    }
    return true;
  }

  parseComment(line) {
    let l = replaceCharacters(line);
    if (l.startsWith('#')) l = l.substring(1);
    return { kind: 'comment', value: restoreCharacters(netTrim(l)), loaded: false };
  }

  parseArray(line) {
    const l = replaceCharacters(line);
    let hash = l.indexOf('#');
    if (hash < 0) hash = l.length;
    const value = restoreCharacters(trimQuotes(substring(l, 1, hash - 1)));
    let comment = '';
    if (hash + 1 < l.length) comment = restoreCharacters(netTrim(l.substring(hash + 1)));
    return { kind: 'array', value, comment, loaded: true };
  }

  parseEntry(line) {
    const l = replaceCharacters(line);
    const eq = l.indexOf('=');
    let hash = l.indexOf('#');
    if (hash < 0) hash = l.length;
    const key = restoreCharacters(trimQuotes(substring(l, 0, eq - 1)));
    const value = restoreCharacters(trimQuotes(substring(l, eq + 1, hash - eq - 1)));
    let comment = '';
    if (hash + 1 < l.length) comment = restoreCharacters(netTrim(l.substring(hash + 1)));
    return { kind: 'entry', key: this.aliasFor(key), value, comment, loaded: false };
  }

  getEntry(key) {
    let found = null;
    for (const e of this.entries) if (e.kind === 'entry' && e.key.toLowerCase() === key.toLowerCase()) found = e;
    return found;
  }

  contains(key) {
    return !!this.getEntry(key);
  }

  // PropertyList.LoadEntry<T>(key, default, comment) + ServerSettingsFile.LoadEntry (comment overwrite).
  loadEntry(key, type, defaultValue, comment) {
    let e = this.getEntry(key);
    let result;
    if (e) {
      e.loaded = true;
      result = convert(e.value, type);
    } else {
      e = { kind: 'entry', key, value: netToString(defaultValue, type), comment, loaded: true };
      this.entries.push(e);
      this.modified = true;
      result = defaultValue;
    }
    e.comment = comment || '';
    return result;
  }

  loadComment(value) {
    const e = this.entries.find((x) => x.kind === 'comment' && x.value === value && !x.loaded);
    if (e) e.loaded = true;
    else {
      this.entries.push({ kind: 'comment', value, loaded: true });
      this.modified = true;
    }
  }

  newLine() {
    this.entries.push({ kind: 'newline', loaded: true });
    this.modified = true;
  }

  setValue(key, value) {
    const e = this.getEntry(key);
    if (!e) return false;
    if (e.value !== value) {
      e.value = value;
      this.modified = true;
    }
    return true;
  }

  serialize() {
    let out = '';
    for (const e of this.entries) {
      if (e.kind === 'raw') out += e.text + NL;
      else if (e.kind === 'array') out += `- '${escapeCharacters(e.value)}'` + (e.comment ? `   # ${escapeCharacters(e.comment)}` : '') + NL;
      else if (e.kind === 'newline') out += NL;
      else if (e.kind === 'comment') out += `# ${escapeCharacters(e.value)}` + NL;
      else if (!e.loaded) {
        // SaveComment(entry): "# " + Escape(entry.ToString())
        const s = e.comment ? `${e.key} = '${e.value}'   # ${e.comment}` : `${e.key} = '${e.value}'`;
        out += `# ${escapeCharacters(s)}` + NL;
      } else out += `${escapeCharacters(e.key)} = '${escapeCharacters(e.value)}'` + (e.comment ? `   # ${escapeCharacters(e.comment)}` : '') + NL;
    }
    return out;
  }

  save() {
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    fs.writeFileSync(this.file, this.serialize());
    this.modified = false;
  }
}

// .NET ToString of the default values the game writes.
function netToString(v, type) {
  if (type === 'bool') return v ? 'True' : 'False';
  if (type === 'float') return Number.isInteger(v) ? String(v) : String(v);
  return v == null ? '' : String(v);
}

const NET_TYPE_NAME = { bool: 'Boolean', int: 'Int32', ushort: 'UInt16', float: 'Single', string: 'String' };

// Convert.ChangeType(string, T) with the invariant/en-US culture.
function convert(text, type) {
  const fail = () => {
    throw new PropertyException(`Tried to get a ${NET_TYPE_NAME[type]} from '${text}'`);
  };
  const t = text.trim();
  switch (type) {
    case 'string':
      return text;
    case 'bool':
      if (/^true$/i.test(t)) return true;
      if (/^false$/i.test(t)) return false;
      return fail();
    case 'int':
    case 'ushort': {
      if (!/^[+-]?\d+$/.test(t)) return fail();
      const n = Number(t);
      const [lo, hi] = type === 'int' ? [-2147483648, 2147483647] : [0, 65535];
      if (!Number.isSafeInteger(n) || n < lo || n > hi) return fail();
      return n;
    }
    case 'float': {
      // NumberStyles.Float | AllowThousands
      if (!/^[+-]?(\d[\d,]*)?(\.\d*)?([eE][+-]?\d+)?$/.test(t) || !/\d/.test(t.replace(/[eE].*$/, ''))) return fail();
      const n = Number(t.replace(/,/g, ''));
      if (!Number.isFinite(n)) return fail();
      return n;
    }
    default:
      throw new Error('unknown type ' + type);
  }
}

// ServerSettingsFile.Load, in order. [key, type, default, comment]
const SECTIONS = [
  ['-- Server --', [
    ['isPrivate', 'bool', false, 'Hides from the lobby if true.'],
    ['serverName', 'string', 'My Server', ''],
    ['greeting', 'string', '', 'Displayed to players when they join. Leave empty to disable. %user% = User who joined, %server% = Name of server.'],
    ['maxPlayers', 'int', 30, ''],
    ['bindIP', 'string', '0.0.0.0', 'The IP to bind the server to. Default: 0.0.0.0'],
    ['portNumber', 'ushort', 7350, '[Port-forward as UDP]'],
    ['password', 'string', '', ''],
    ['restartTime', 'int', 0, '0 seconds to disable.'],
    ['enableCommands', 'bool', true, 'When set to false, all commands are disabled.'],
    ['flyDetection', 'bool', false, 'If true, the server will check for players using fly hack.'],
    ['steamAuthPort', 'int', 27015, 'The port the steam authenticator will use to communicate with steam.'],
    ['timeBetweenPlayerJoin', 'float', 10, 'The seconds the server waits before allowing a player since the last player joined. Default: 10'],
    ['targetFrameRate', 'int', 60, 'Default: 60'],
    ['?webApiUrl', 'string', '', ''],
    ['?webApiKey', 'string', '', '']
  ]],
  ['-- Ping Limit --', [
    ['enablePingLimit', 'bool', false, 'Kick players who exceed the ping limit while this is enabled.'],
    ['pingPort', 'ushort', 7350, '[Port-forward as TCP] The port dedicated to managing player pings. This number can be shared with the gameplay port.'],
    ['pingLimit', 'ushort', 200, '[Minimum = 50, Maximum = 10000] An average ping limit in milliseconds. eg. 200 = 0.2 seconds'],
    ['pingGraphLength', 'ushort', 60, '[Minimum = 10, Maximum = 3600] The length of the graph used to calculate the average ping as seconds. A longer graph gives more room for latency spikes. eg. 120 = 2 minutes']
  ]],
  ['-- Automatic Report Ban --', [
    ['autoReportBanEnabled', 'bool', false, 'Ban players who accumulate too many spam or exploit report points while this is enabled.'],
    ['reportThresholdSpam', 'float', 3, '[Minimum = 0, Maximum = 100] A point threshold used to ban players when valid reports are submitted. Reports are worth less points when repeated by players or guildmates. eg. 0 = instant ban on any valid report, 3 = ~3 valid reports.'],
    ['daysBannedForSpam', 'int', 1, '[Minimum = 0] 0 days will ban a player forever. Repeat bans will increase the duration additively.'],
    ['reportThresholdExploit', 'float', 5, '[Minimum = 0, Maximum = 100] A point threshold used to ban players when valid reports are submitted. Reports are worth less points when repeated by players or guildmates. eg. 0 = instant ban on any valid report, 5 = ~5 valid reports.'],
    ['daysBannedForExploit', 'int', 7, '[Minimum = 0] 0 days will ban a player forever. Repeat bans will increase the duration additively.']
  ]],
  ['-- World --', [
    ['saveLocation', 'string', 'Saves/', 'The location to save the worlds slots.'],
    ['worldSlot', 'int', -1, '-1 creates a new world.'],
    ['allowSaving', 'bool', true, ''],
    ['levelName', 'string', 'CrownLand', 'CrownLand, StormWall']
  ]],
  ['-- Game --', [
    ['decay', 'bool', false, 'If placed objects and blocks decay and destroy over time.'],
    ['#Changing these values will not affect pre-existing objects.'],
    ['blockDecay', 'float', -1, 'The time it takes, in seconds, for blocks to decay outside a crest area. Less than zero default is used.'],
    ['prefabDecay', 'float', -1, 'The time it takes, in seconds, for non-blocks to decay outside a crest area. Less than zero default is used.'],
    ['crestSiege', 'bool', false, 'If the crest area can be under siege.'],
    ['blockCollapsing', 'bool', true, 'If blocks collapse when not attached to the ground.'],
    ['?debugFillPages', 'bool', false, ''],
    ['?profileToFile', 'bool', false, 'If true the game will start with profiling enabled and write the results to file.'],
    ['?dumpHeatmapsToFile', 'bool', false, ''],
    ['?forceExitOnShutdown', 'bool', false, '']
  ]]
];

const CLAMPS = {
  pingLimit: [50, 10000],
  pingGraphLength: [10, 3600],
  reportThresholdSpam: [0, 100],
  reportThresholdExploit: [0, 100],
  daysBannedForSpam: [0, Infinity],
  daysBannedForExploit: [0, Infinity]
};

// Returns { ok, settings, file, firstCreate, error }. On a PropertyException (or a Substring
// exception while reading the file) ok is false: the game logs the exception and exits.
function loadServerSettings(cfgPath) {
  const pf = new PropertyFile(cfgPath);
  const settings = {};
  try {
    pf.load();
    let first = true;
    for (const [title, keys] of SECTIONS) {
      if (!first && pf.firstCreate) pf.newLine();
      first = false;
      pf.loadComment(title);
      for (const k of keys) {
        if (k[0].startsWith('#')) {
          pf.loadComment(k[0].substring(1));
          continue;
        }
        const optional = k[0].startsWith('?');
        const key = optional ? k[0].substring(1) : k[0];
        if (optional && !pf.contains(key)) {
          settings[key] = k[2];
          continue;
        }
        let v = pf.loadEntry(key, k[1], k[2], k[3]);
        if (CLAMPS[key]) v = Math.min(CLAMPS[key][1], Math.max(CLAMPS[key][0], k[1] === 'float' ? v : Math.trunc(v)));
        settings[key] = v;
      }
    }
    if (pf.modified) pf.save();
    return { ok: true, settings, file: pf, firstCreate: pf.firstCreate };
  } catch (e) {
    if (e instanceof PropertyException) return { ok: false, settings, file: pf, firstCreate: pf.firstCreate, error: e };
    throw e;
  }
}

module.exports = {
  PropertyFile,
  PropertyException,
  loadServerSettings,
  convert,
  escapeCharacters,
  SECTIONS
};
