'use strict';
// World continuity (lib/worlds.js, docs/worlds.md): always the same world, never a new one by accident.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const W = require('../lib/worlds');
const R = require('../lib/realm');

const slot = (n, extra = {}) => ({ slot: n, dir: `Saves/Slot${n}`, files: 40, bytes: 900000, newestMs: 1000 + n, hasLock: false, locked: false, empty: false, ...extra });
const empty = (n) => slot(n, { files: 1, bytes: 300, empty: true });
const worlds = (cfgSlot, slots) => ({ cfgExists: true, cfgSlot, saveLocation: 'Saves/', slots });

test('remembered world: load it, or put it back when worldSlot was reset to -1', () => {
  assert.equal(W.planWorld(worlds(2, [slot(2)]), 2).action, 'load');
  const p = W.planWorld(worlds(-1, [slot(2), empty(3)]), 2);
  assert.equal(p.action, 'pin');
  assert.equal(p.slot, 2);
  assert.match(p.message, /-1/);
  // worldSlot names an empty slot a failed start left behind: still world 2.
  assert.equal(W.planWorld(worlds(3, [slot(2), empty(3)]), 2).action, 'pin');
  // worldSlot names a slot with no folder: the game would create it. Put world 2 back.
  assert.equal(W.planWorld(worlds(9, [slot(2)]), 2).action, 'pin');
});

test('the owner\'s case: world in use by another server is blocked, not silently replaced', () => {
  const p = W.planWorld(worlds(0, [slot(0, { hasLock: true, locked: true })]), 0);
  assert.equal(p.action, 'blocked');
  assert.match(p.message, /Session\.lock is held/);
  // Nothing remembered yet: the configured world is held -> blocked as well.
  assert.equal(W.planWorld(worlds(0, [slot(0, { hasLock: true, locked: true })]), null).action, 'blocked');
  // A Session.lock left by a crash is not held: fine.
  assert.equal(W.planWorld(worlds(0, [slot(0, { hasLock: true, locked: false })]), 0).action, 'load');
});

test('a start that would make a new world while saved worlds exist asks first', () => {
  const p = W.planWorld(worlds(-1, [slot(0, { newestMs: 5 }), empty(3), slot(6, { newestMs: 50 })]), null);
  assert.equal(p.action, 'choose');
  assert.equal(p.suggest, 6, 'the most recently saved world is suggested');
  assert.deepEqual(p.choices.map((c) => c.slot), [0, 6], 'empty slots are not offered as worlds');
  assert.match(p.message, /NEW world/);
  // Two real worlds compete: the file says 6, Realm last ran 0.
  const q = W.planWorld(worlds(6, [slot(0), slot(6)]), 0);
  assert.equal(q.action, 'choose');
  assert.equal(q.suggest, 0);
  // The remembered world was deleted or moved.
  const r = W.planWorld(worlds(-1, [slot(4)]), 2);
  assert.equal(r.action, 'choose');
  assert.match(r.message, /world 2, which this server last ran, is no longer/);
});

test('first world: no settings file yet, or no saved world at all', () => {
  assert.equal(W.planWorld({ cfgExists: false, slots: [] }, null).action, 'new');
  assert.equal(W.planWorld(worlds(-1, []), null).action, 'new');
  assert.equal(W.planWorld(worlds(-1, [empty(0)]), null).action, 'new', 'only an empty first-run slot');
  assert.equal(W.planWorld(worlds(3, [slot(3)]), null).action, 'load', 'an existing world, nothing remembered');
});

function serverFolder(cfgSlot) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'realm-worlds-'));
  fs.mkdirSync(path.join(root, 'Configuration'));
  fs.writeFileSync(path.join(root, 'Configuration', 'ServerSettings.cfg'), `# -- World --\r\nsaveLocation = 'Saves/'\r\nworldSlot = '${cfgSlot}'                 # -1 creates a new world.\r\nallowSaving = 'True'\r\n`);
  return root;
}

test('readWorlds reads the settings and every Slot folder; setWorldSlot rewrites only worldSlot', async () => {
  const root = serverFolder(-1);
  const saves = path.join(root, 'Saves');
  fs.mkdirSync(path.join(saves, 'Slot0', 'Blocks'), { recursive: true });
  for (let i = 0; i < 5; i++) fs.writeFileSync(path.join(saves, 'Slot0', 'Blocks', `p${i}.dat`), Buffer.alloc(20000));
  fs.mkdirSync(path.join(saves, 'Slot3'));
  fs.writeFileSync(path.join(saves, 'Slot3', 'GameSlotInfo'), 'x');
  fs.writeFileSync(path.join(saves, 'Slot3', 'Session.lock'), '');
  fs.mkdirSync(path.join(saves, 'NotASlot'));
  const held = new Set([path.join(saves, 'Slot3', 'Session.lock')]);
  const w = await W.readWorlds(root, { probeLock: async (f) => held.has(f) });
  assert.equal(w.cfgSlot, -1);
  assert.equal(w.saveRoot, saves);
  assert.deepEqual(w.slots.map((s) => [s.slot, s.empty, s.hasLock, s.locked]), [[0, false, false, false], [3, true, true, true]]);
  assert.deepEqual(await W.heldSlots(root, { probeLock: async (f) => held.has(f) }), [3]);

  const r = await W.setWorldSlot(root, 0, R.applyCfg);
  assert.deepEqual(r.changes, [{ key: 'worldSlot', from: '-1', to: '0' }]);
  const text = fs.readFileSync(path.join(root, 'Configuration', 'ServerSettings.cfg'), 'utf8');
  assert.match(text, /^worldSlot = '0'                 # -1 creates a new world\.\r$/m, 'the game\'s own line format is kept');
  assert.equal(await W.runningSlot(root), 0);
  assert.ok(fs.readdirSync(path.join(root, '_realm-backups', 'config')).length >= 1, 'the previous file was backed up');
  await assert.rejects(() => W.setWorldSlot(root, -2, R.applyCfg), RangeError);
});

test('the default lock probe: a missing file or an openable one is not held', async () => {
  const root = serverFolder(0);
  assert.equal(await W.defaultLockProbe(path.join(root, 'nope.lock')), false);
  fs.writeFileSync(path.join(root, 'free.lock'), '');
  assert.equal(await W.defaultLockProbe(path.join(root, 'free.lock')), false);
});

test('an absolute saveLocation is used as it is', () => {
  assert.equal(W.saveRootOf('/srv/rok', 'Saves/'), path.resolve('/srv/rok/Saves'));
  assert.equal(W.saveRootOf('/srv/rok', '/data/worlds'), path.resolve('/data/worlds'));
});
