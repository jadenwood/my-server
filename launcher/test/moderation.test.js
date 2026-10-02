// Court moderation: command builders against the game's argument rules, output parsers, the
// moderation log on disk, console preferences, and an end-to-end run against the fake console.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const MOD = require('../lib/moderation');
const AC = require('../lib/admin-console');
const { FakeAdminConsole } = require('./fake-admin-console');

// The game's own argument splitter ([DEC] CommandInfo.Args), ported to check our quoting.
function gameArgs(command) {
  const input = command.replace(/\\ /g, '%_~S').replace(/\\"/g, '%_~Q').replace(/\\'/g, '%_~SQ');
  const re = /(\s["']([^"']+)["']|([^"'\s]+))/g;
  const out = [];
  let m;
  let i = 0;
  while ((m = re.exec(input))) {
    if (i++ === 0) continue; // the label
    const t = m[0].replace(/^["' ]+|["' ]+$/g, '').replace(/%_~S/g, ' ').replace(/%_~Q/g, '"').replace(/%_~SQ/g, "'");
    if (t) out.push(t);
  }
  return out;
}

test('builders produce one console line that the game parses back to the intended arguments', () => {
  const kick = MOD.build('kick', { name: 'Odo the Tall', reason: 'griefing the market' });
  assert.equal(kick.command, '/kick "Odo the Tall" "griefing the market"');
  assert.deepEqual(gameArgs(kick.command), ['Odo the Tall', 'griefing the market']);

  const ban = MOD.build('ban', { name: 'Wren', days: 3, reason: 'spam in chat' });
  assert.equal(ban.command, '/ban Wren 3 spam in chat');
  assert.deepEqual(gameArgs(ban.command), ['Wren', '3', 'spam', 'in', 'chat']);

  const forever = MOD.build('ban', { name: 'Wren', reason: "2 alts, won't stop" });
  // the game would read a leading number as days: Realm moves it after a word
  assert.deepEqual(gameArgs(forever.command).slice(0, 3), ['Wren', 'Reason:', '2']);
  assert.ok(!forever.command.includes("'"));

  assert.equal(MOD.build('ban', { name: 'Wren' }).command, '/ban Wren');
  assert.equal(MOD.build('unban', { name: '76561190000000002' }).command, '/unban 76561190000000002');
  assert.equal(MOD.build('whitelist', { on: true }).command, '/whitelist enable');
  assert.equal(MOD.build('whitelist', { on: false }).command, '/whitelist disable');
  assert.equal(MOD.build('notice', { message: "The king's peace\nholds" }).command, '/notice The king`s peace holds');
  const say = MOD.build('say', { message: 'Hail' });
  assert.equal(say.command, 'Hail');
  assert.equal(say.chat, true);
  assert.equal(MOD.build('save').command, '/realm.save');
  assert.equal(MOD.build('list').command, '/list');
  assert.equal(MOD.build('mute', { name: 'Wren', days: 1 }).command, '/mute Wren 1');
});

test('builders refuse bad input', () => {
  assert.throws(() => MOD.build('kick', { name: '' }), /Choose a player/);
  assert.throws(() => MOD.build('ban', { name: 'Wren', days: -1 }), /whole number/);
  assert.throws(() => MOD.build('ban', { name: 'Wren', days: 1.5 }), /whole number/);
  assert.throws(() => MOD.build('kick', { name: "O'Brien" }), /apostrophe/);
  assert.throws(() => MOD.build('notice', { message: '' }), /Write the message/);
  assert.throws(() => MOD.build('say', { message: '/shutdown' }), /cannot start/);
  assert.throws(() => MOD.build('notice', { message: 'x'.repeat(301) }), /limited/);
  assert.throws(() => MOD.build('rm -rf'), /Unknown Court action/);
  assert.throws(() => MOD.build('__proto__'), /Unknown Court action/);
});

test('plugin admin commands come from the plugins and fill their placeholders', () => {
  const src = ['RealmHouses', 'CrownAndConsequences', 'RealmContracts'].map((p) => fs.readFileSync(path.join(__dirname, '..', '..', 'plugins', `${p}.cs`), 'utf8'));
  for (const d of MOD.PLUGIN_ADMIN) {
    if (d.perm) assert.ok(src.some((s) => s.includes(`"${d.perm}"`)), `permission ${d.perm} exists in a plugin`);
    const verb = d.template.split(' ')[0].slice(1);
    assert.ok(src.some((s) => s.includes(`[ChatCommand("${verb}")]`)), `chat command ${verb} exists`);
  }
  const i = MOD.PLUGIN_ADMIN.findIndex((d) => d.template === '/house pardon {house}');
  assert.equal(MOD.pluginCommand(i, { house: 'Thornvale' }), '/house pardon Thornvale');
  assert.throws(() => MOD.pluginCommand(i, {}), /Fill in/);
});

test('parsers read the exact formats of /list, /banlist and the RealmCourt roster', () => {
  assert.deepEqual(MOD.parsePlayerList(['Online Players(2):', 'Wren, Odo the Tall']), { count: 2, names: ['Wren', 'Odo the Tall'] });
  assert.deepEqual(MOD.parsePlayerList([{ text: 'There are no players online.' }]), { count: 0, names: [] });
  assert.equal(MOD.parsePlayerList(['something else']), null);
  assert.deepEqual(MOD.parseBanList(['#0 Wren |76561190000000002| (203.0.113.9) <infinite days left>', '#1 Odo |7| (1.2.3.4) <2.5 days left>']), [
    { index: 0, name: 'Wren', id: '76561190000000002', ip: '203.0.113.9', left: 'infinite days' },
    { index: 1, name: 'Odo', id: '7', ip: '1.2.3.4', left: '2.5 days' }
  ]);
  assert.deepEqual(MOD.parseRoster(['REALMCOURT|4|players|2', 'REALMCOURT|4|p|11|Wren', 'REALMCOURT|4|p|12|Odo|x']), {
    count: 2,
    players: [{ id: '11', name: 'Wren' }, { id: '12', name: 'Odo|x' }]
  });
  assert.equal(MOD.unknownCommand(["Unknown command '/realm.save'. For help type /help"]), true);
});

test('moderation log: appended to disk, read newest first, filtered, torn lines skipped, rotated', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'court-'));
  const log = new MOD.CourtLog(dir, { maxBytes: 600 });
  await log.append({ server: 's1', action: 'kick', target: 'Wren', reason: 'spam', command: '/kick Wren spam', result: 'Wren was kicked' });
  await log.append({ server: 's2', action: 'ban', target: 'Odo', ok: false, result: 'not found' });
  fs.appendFileSync(log.file, '{"torn": \n');
  const all = await log.read();
  assert.equal(all.length, 2);
  assert.equal(all[0].action, 'ban');
  assert.equal(all[0].ok, false);
  assert.equal((await log.read({ server: 's1' }))[0].target, 'Wren');
  for (let i = 0; i < 10; i++) await log.append({ server: 's1', action: 'notice', reason: 'x'.repeat(50) });
  assert.ok(fs.existsSync(path.join(dir, 'court-log.1.jsonl')), 'rotated');
  fs.rmSync(dir, { recursive: true, force: true });
});

test('console preference defaults to on and persists per server', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'court-'));
  const p = new MOD.ConsolePrefs(dir);
  assert.equal(p.enabled('s1'), true);
  await p.set('s2', false);
  assert.equal(new MOD.ConsolePrefs(dir).enabled('s2'), false);
  assert.equal(new MOD.ConsolePrefs(dir).enabled('s1'), true);
  await assert.rejects(p.set('s9', true), /Unknown server/);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('end to end: Court actions over the console against the fake server', async () => {
  const fake = new FakeAdminConsole({ keepAliveMs: 100 });
  const port = await fake.listen(0);
  const con = new AC.AdminConsole({ port, retryMs: 50 });
  con.attach();
  await new Promise((r) => con.once('connected', r));
  const run = (action, args) => con.send(MOD.build(action, args).command);

  assert.deepEqual(MOD.parsePlayerList((await run('list')).lines).names, ['Wren', 'Odo the Tall']);
  const roster = MOD.parseRoster((await run('roster')).lines);
  assert.equal(roster.players[1].name, 'Odo the Tall');
  await run('kick', { name: 'Odo the Tall', reason: 'afk' });
  assert.equal(MOD.parsePlayerList((await run('list')).lines).count, 1);
  await run('ban', { name: 'Wren', days: 0, reason: 'cheating' });
  const bans = MOD.parseBanList((await run('banlist')).lines);
  assert.equal(bans[0].name, 'Wren');
  await run('unban', { name: bans[0].id });
  assert.equal(MOD.parseBanList((await run('banlist')).lines).length, 0);
  await run('whitelist', { on: true });
  assert.equal(fake.whitelist, true);
  const saved = await run('save');
  assert.ok(saved.lines.some((l) => /REALMCOURT\|save\|ok/.test(l.text)));
  await run('notice', { message: 'Market at dusk' });
  assert.deepEqual(fake.notices, ['Market at dusk']);
  con.close();
  await fake.close();
});
