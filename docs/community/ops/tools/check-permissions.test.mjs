// node --test docs/community/ops/tools/*.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { permissionsInSource, matrixRows, namedInDoc, check, main, pluginPermissions } from './check-permissions.mjs';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '..');
const sink = () => {
  const lines = [];
  return { lines, io: { out: (s) => lines.push(s), err: (s) => lines.push(s) } };
};

test('source parsing: constants and literal registrations', () => {
  const src = `
    private const string PermAdmin = "realmfoo.admin";
    internal const string PermAdmin = "realmbar.admin";
    private const string Permission = "realm.court";
    private const string SaveLabel = "realm.save";
    permission.RegisterPermission("realmbaz.use", this);`;
  assert.deepEqual([...permissionsInSource(src)].sort(), ['realm.court', 'realmbar.admin', 'realmbaz.use', 'realmfoo.admin']);
});

test('doc parsing: rows vs mentions', () => {
  const doc = '| `realmfoo.admin` | x |\n| Owner | `realmbar.admin` |\nGrant realmtypo.admin and realm.court.';
  assert.deepEqual([...matrixRows(doc)], ['realmfoo.admin']);
  assert.deepEqual([...namedInDoc(doc)].sort(), ['realm.court', 'realmbar.admin', 'realmfoo.admin', 'realmtypo.admin']);
});

test('check: missing rows and unknown names are both caught', () => {
  const perms = new Map([['a.admin', 'A.cs'], ['b.admin', 'B.cs']]);
  const r = check(perms, '| `a.admin` | |\nalso c.admin');
  assert.deepEqual(r.missingRows, ['b.admin']);
  assert.deepEqual(r.unknown, ['c.admin']);
  assert.equal(r.ok, false);
  assert.equal(check(perms, '| `a.admin` |\n| `b.admin` |').ok, true);
});

test('CLI exit codes on a temp tree', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'realm-perm-'));
  fs.writeFileSync(path.join(root, 'X.cs'), 'const string PermAdmin = "x.admin";');
  fs.writeFileSync(path.join(root, 'good.md'), '| `x.admin` | owner |');
  fs.writeFileSync(path.join(root, 'bad.md'), '| `y.admin` | owner |');
  let s = sink();
  assert.equal(main(['--plugins', root, '--doc', path.join(root, 'good.md')], s.io), 0);
  s = sink();
  assert.equal(main(['--plugins', root, '--doc', path.join(root, 'bad.md')], s.io), 1);
  assert.ok(s.lines.some((l) => l.startsWith('MISSING ROW: x.admin')));
  assert.ok(s.lines.some((l) => l.startsWith('UNKNOWN') && l.includes('y.admin')));
  assert.equal(main(['--plugins', path.join(root, 'nope')], sink().io), 2);
});

test('the real repo: every plugin permission has a row, and the doc names no unknown permission', () => {
  const perms = pluginPermissions(path.join(REPO, 'plugins'));
  assert.ok(perms.size >= 13, `expected at least 13 permissions, found ${perms.size}`);
  const s = sink();
  const code = main([], s.io);
  assert.equal(code, 0, s.lines.join('\n'));
});
