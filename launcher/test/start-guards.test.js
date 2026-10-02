'use strict';
const { test } = require('node:test');
const assert = require('node:assert');
const N = require('../lib/netcheck.js');
const G = require('../lib/shared/gamelog.js');

test('portOwners is a no-op off Windows and rejects bad ports', async () => {
  assert.deepStrictEqual(await N.portOwners(7350, 'udp', { platform: 'linux' }), []);
  assert.deepStrictEqual(await N.portOwners(70000, 'udp', { platform: 'win32' }), []);
  assert.deepStrictEqual(await N.portOwners('7350; calc', 'udp', { platform: 'win32' }), []);
});

test('the port-clash message points at a leftover ROK.exe', () => {
  const rule = G.RULES.find((r) => r.id === 'game-port-taken');
  assert.ok(rule && rule.re.test('The port 7350 is already being used by another application.'));
  assert.match(rule.fix, /Task Manager/);
});

test('describeOwners tells the game client apart from a leftover server', () => {
  const root = 'G:\\RealmTest\\server';
  assert.match(N.describeOwners([{ pid: 4356, name: 'ROK', path: '' }], root), /Server\.exe watchdog/);
  assert.match(N.describeOwners([{ pid: 1, name: 'ROK', path: 'G:\\SteamLibrary\\steamapps\\common\\Reign Of Kings\\ROK.exe' }], root), /GAME/);
  assert.match(N.describeOwners([{ pid: 2, name: 'ROK', path: 'G:\\RealmTest\\server\\ROK.exe' }], root), /older copy of this server/);
  assert.match(N.describeOwners([{ pid: 3, name: 'svchost', path: 'C:\\Windows\\System32\\svchost.exe' }], root), /^svchost\.exe/);
  assert.strictEqual(N.describeOwners([], root), 'another program');
});

test('Doctor explains the Steam GameMismatch refusal', () => {
  const r = G.classify('Failed to authenticate 420LUFFY (76561198211695796) with Steam. (k_EBeginAuthSessionResultGameMismatch)');
  assert.strictEqual(r.id, 'steam-auth-game-mismatch');
  assert.match(r.fix, /344760/);
});
