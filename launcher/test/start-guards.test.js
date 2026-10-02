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
