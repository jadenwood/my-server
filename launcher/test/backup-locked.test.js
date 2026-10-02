'use strict';
// The owner's crash: backing up while the server ran hit EBUSY on Saves\Slot3\Session.lock and
// killed the app. Lock files are now skipped and listed, and the backup still completes.
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const R = require('../lib/realm.js');

test('backup skips *.lock files and still writes the zip', async () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'realm-bk-'));
  const root = path.join(home, 'server');
  fs.mkdirSync(path.join(root, 'Saves', 'Slot3'), { recursive: true });
  fs.writeFileSync(path.join(root, 'Saves', 'Slot3', 'World.dat'), 'world');
  fs.writeFileSync(path.join(root, 'Saves', 'Slot3', 'Session.lock'), 'held by server');
  const res = await R.createBackup(root);
  assert.ok(fs.existsSync(res.file));
  assert.strictEqual(res.files, 1);
  assert.deepStrictEqual(res.skipped, ['Saves/Slot3/Session.lock']);
});
