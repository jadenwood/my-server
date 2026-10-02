'use strict';

// ServerSettings.cfg as the game writes and rewrites it (PropertyFile + ServerSettingsFile [DEC]).

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const { loadServerSettings, PropertyFile } = require('../lib/props');
const { tmpDir } = require('./helpers');

const cfgIn = (dir) => path.join(dir, 'Configuration', 'ServerSettings.cfg');

test('first run creates the full file: sections, blank lines, comments, CRLF, code defaults', () => {
  const dir = tmpDir();
  const r = loadServerSettings(cfgIn(dir));
  assert.equal(r.ok, true);
  assert.equal(r.firstCreate, true);
  const text = fs.readFileSync(cfgIn(dir), 'utf8');
  assert.ok(!/[^\r]\n/.test(text), 'every line ends with CRLF');
  const lines = text.split('\r\n');
  assert.equal(lines[0], '# -- Server --');
  assert.equal(lines[1], "isPrivate = 'False'   # Hides from the lobby if true.");
  assert.equal(lines[2], "serverName = 'My Server'");
  assert.ok(lines.includes("portNumber = '7350'   # [Port-forward as UDP]"));
  assert.ok(lines.includes("steamAuthPort = '27015'   # The port the steam authenticator will use to communicate with steam."));
  assert.ok(lines.includes("timeBetweenPlayerJoin = '10'   # The seconds the server waits before allowing a player since the last player joined. Default: 10"));
  assert.ok(lines.includes("levelName = 'CrownLand'   # CrownLand, StormWall"));
  assert.ok(lines.includes("blockDecay = '-1'   # The time it takes, in seconds, for blocks to decay outside a crest area. Less than zero default is used."));
  // The sections are separated by one blank line, only on the first run.
  assert.deepEqual(lines.map((l, i) => (l === '' ? i : -1)).filter((i) => i >= 0).length, 5); // 4 separators + the final CRLF
  const keys = lines.filter((l) => /^\w+ = /.test(l)).map((l) => l.split(' ')[0]);
  assert.deepEqual(keys, [
    'isPrivate', 'serverName', 'greeting', 'maxPlayers', 'bindIP', 'portNumber', 'password', 'restartTime', 'enableCommands', 'flyDetection',
    'steamAuthPort', 'timeBetweenPlayerJoin', 'targetFrameRate', 'enablePingLimit', 'pingPort', 'pingLimit', 'pingGraphLength',
    'autoReportBanEnabled', 'reportThresholdSpam', 'daysBannedForSpam', 'reportThresholdExploit', 'daysBannedForExploit',
    'saveLocation', 'worldSlot', 'allowSaving', 'levelName', 'decay', 'blockDecay', 'prefabDecay', 'crestSiege', 'blockCollapsing'
  ]);
  assert.deepEqual(r.settings.maxPlayers, 30);
  assert.equal(r.settings.webApiUrl, '');
});

test('a second load keeps values verbatim and does not mark the file modified', () => {
  const dir = tmpDir();
  loadServerSettings(cfgIn(dir));
  const before = fs.readFileSync(cfgIn(dir), 'utf8');
  const r = loadServerSettings(cfgIn(dir));
  assert.equal(r.firstCreate, false);
  assert.equal(r.file.modified, false);
  assert.equal(r.file.serialize(), before);
});

test('quirks: dropped key character, unknown keys commented out, # needs escaping, aliases, case', () => {
  const dir = tmpDir();
  fs.mkdirSync(path.join(dir, 'Configuration'));
  fs.writeFileSync(
    cfgIn(dir),
    [
      "serverName = 'Realm \\#1 of Ostreval'",
      "maxPlayers='120'", // key becomes "maxPlayer"
      "MAXPLAYERS = '120'", // case-insensitive: this IS maxPlayers
      "gameMode = 'PvP'", // not read by the game
      "password = 'a#b'", // '#' starts a comment
      "greeting = 'x'",
      "greeting = 'y'" // becomes greeting2
    ].join('\n')
  );
  const r = loadServerSettings(cfgIn(dir));
  assert.equal(r.ok, true);
  assert.equal(r.settings.serverName, 'Realm #1 of Ostreval');
  assert.equal(r.settings.maxPlayers, 120);
  assert.equal(r.settings.password, 'a');
  assert.equal(r.settings.greeting, 'x');
  r.file.save();
  const out = fs.readFileSync(cfgIn(dir), 'utf8').split('\r\n');
  assert.ok(out.includes("serverName = 'Realm \\#1 of Ostreval'"));
  assert.ok(out.includes("# maxPlayer = \\'120\\'"), out.join('\n'));
  assert.ok(out.includes("MAXPLAYERS = '120'"));
  assert.ok(out.includes("# gameMode = \\'PvP\\'"));
  assert.ok(out.includes("# greeting2 = \\'y\\'"));
  // Missing keys are appended (no blank section lines when the file already existed).
  assert.ok(out.includes("portNumber = '7350'   # [Port-forward as UDP]"));
  assert.ok(out.indexOf("portNumber = '7350'   # [Port-forward as UDP]") > out.indexOf("# gameMode = \\'PvP\\'"));
  // The commented-out lines survive another load/save unchanged.
  const again = loadServerSettings(cfgIn(dir));
  again.file.save();
  assert.deepEqual(fs.readFileSync(cfgIn(dir), 'utf8').split('\r\n'), out);
});

test('a value that does not convert is a PropertyException, as the game reports it', () => {
  for (const [line, msg] of [
    ["maxPlayers = 'lots'", "Tried to get a Int32 from 'lots'"],
    ["portNumber = '70000'", "Tried to get a UInt16 from '70000'"],
    ["isPrivate = '1'", "Tried to get a Boolean from '1'"],
    ["timeBetweenPlayerJoin = 'soon'", "Tried to get a Single from 'soon'"]
  ]) {
    const dir = tmpDir();
    fs.mkdirSync(path.join(dir, 'Configuration'));
    fs.writeFileSync(cfgIn(dir), line + '\r\n');
    const r = loadServerSettings(cfgIn(dir));
    assert.equal(r.ok, false, line);
    assert.equal(r.error.exceptionType, 'PropertyException');
    assert.equal(r.error.message, msg);
  }
});

test("a '#' before the '=' throws while reading (Substring) and the load fails", () => {
  const dir = tmpDir();
  fs.mkdirSync(path.join(dir, 'Configuration'));
  fs.writeFileSync(cfgIn(dir), "x#y = '1'\r\n");
  const r = loadServerSettings(cfgIn(dir));
  assert.equal(r.ok, false);
  assert.equal(r.error.exceptionType, 'ArgumentOutOfRangeException');
});

test('clamped settings, floats with thousands separators, booleans in any case', () => {
  const dir = tmpDir();
  fs.mkdirSync(path.join(dir, 'Configuration'));
  fs.writeFileSync(cfgIn(dir), ["pingLimit = '5'", "pingGraphLength = '9999'", "timeBetweenPlayerJoin = '1,000.5'", "isPrivate = 'TRUE'"].join('\r\n'));
  const r = loadServerSettings(cfgIn(dir));
  assert.equal(r.settings.pingLimit, 50);
  assert.equal(r.settings.pingGraphLength, 3600);
  assert.equal(r.settings.timeBetweenPlayerJoin, 1000.5);
  assert.equal(r.settings.isPrivate, true);
  // The file keeps what was written; clamping is runtime only.
  assert.match(r.file.serialize(), /pingLimit = '5'/);
});

test('PropertyFile keeps arrays and comments it does not understand', () => {
  const dir = tmpDir();
  const f = path.join(dir, 'x.cfg');
  fs.writeFileSync(f, "# hello\r\n- 'a'\r\n\r\nk = 'v'   # note\r\n");
  const pf = new PropertyFile(f);
  pf.load();
  assert.equal(pf.serialize(), "# hello\r\n- 'a'\r\n\r\n# k = \\'v\\'   \\# note\r\n");
});
