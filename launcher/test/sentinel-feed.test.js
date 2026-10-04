// The Sentinel screen's main-process side (lib/sentinel-feed.js): the Steward feed and evidence files
// exactly as plugins/RealmSentinel.cs writes them, damaged and hostile input, actions through the Court.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const fsp = require('fs/promises');
const os = require('os');
const path = require('path');
const SF = require('../lib/sentinel-feed');

async function tmp() {
  return fsp.mkdtemp(path.join(os.tmpdir(), 'realm-sentinel-'));
}

// The shape RealmSentinel.WriteFeed serialises (Feed, FeedAlert, FeedSuspect; plugins/docs/RealmSentinel.md).
function feed(over = {}) {
  return {
    Version: 1,
    Generated: '2026-10-04T12:00:00Z',
    Mode: 'watch',
    DataDamaged: false,
    Online: 14,
    Frozen: 1,
    Alerts: [
      { Id: 7, Time: '2026-10-04T11:58:00Z', Kind: 'speed', PlayerId: '76561190000000009', PlayerName: 'Grimsby the Cutpurse', Score: 61.5, Response: 'would freeze', Detail: '23.4 m/s over 3 s (limit 11.0)' },
      { Id: 9, Time: '2026-10-04T11:59:30Z', Kind: 'item_jump', PlayerId: '76561190000000009', PlayerName: 'Grimsby the Cutpurse', Score: 88, Response: 'ban recommended', Detail: '+2400 Iron in 10 s' },
      { Id: 8, Time: '2026-10-04T11:59:00Z', Kind: 'melee_range', PlayerId: '76561190000000004', PlayerName: 'Ysolde Corvane', Score: 31.2, Response: 'alert', Detail: 'hit at 9.1 m' }
    ],
    Suspects: [
      { PlayerId: '76561190000000004', PlayerName: 'Ysolde Corvane', Score: 31.2, PeakScore: 40, Online: true, Frozen: false, LastSeen: '2026-10-04T11:59:00Z', Counts: { melee_range: 2 } },
      { PlayerId: '76561190000000009', PlayerName: 'Grimsby the Cutpurse', Score: 88, PeakScore: 88, Online: true, Frozen: true, LastSeen: '2026-10-04T11:59:30Z', Counts: { speed: 4, item_jump: 1 } }
    ],
    ...over
  };
}

test('parseFeed reads the plugin feed, newest alert and highest score first', () => {
  const p = SF.parseFeed(JSON.stringify(feed()));
  assert.ok(p.ok);
  assert.equal(p.feed.mode, 'watch');
  assert.equal(p.feed.online, 14);
  assert.deepEqual(p.feed.alerts.map((a) => a.id), [9, 8, 7]);
  assert.equal(p.feed.alerts[0].severity, 3);
  assert.equal(p.feed.alerts[2].severity, 1);
  assert.equal(p.feed.alerts[1].severity, 0);
  assert.deepEqual(p.feed.suspects.map((s) => s.playerName), ['Grimsby the Cutpurse', 'Ysolde Corvane']);
  assert.deepEqual(p.feed.suspects[0].counts, { speed: 4, item_jump: 1 });
  assert.equal(SF.parseFeed('﻿' + JSON.stringify(feed())).ok, true, 'a BOM is fine');
});

test('parseFeed refuses damaged and unknown feeds, and cleans hostile text', () => {
  assert.match(SF.parseFeed('{"Version":1,"Alerts":[').reason, /not valid JSON/);
  assert.match(SF.parseFeed('null').reason, /not an object/);
  assert.match(SF.parseFeed(JSON.stringify(feed({ Version: 2 }))).reason, /version 2/);
  const evil = feed({
    Mode: 'enforce',
    Alerts: [{ Id: 1, Time: 'yesterday', Kind: 'speed', PlayerId: '123; /shutdown', PlayerName: 'A‮B\nC\u0000D', Score: 'NaN', Response: 'kicked', Detail: 'x'.repeat(5000) }],
    Suspects: [{ PlayerId: 'abc', PlayerName: 'nope' }, { PlayerId: '42', PlayerName: 'Ok', Counts: { 'bad key!': 3, speed: -1, fly: 2 } }]
  });
  const p = SF.parseFeed(JSON.stringify(evil));
  assert.equal(p.feed.mode, 'enforce');
  const a = p.feed.alerts[0];
  assert.equal(a.playerId, '', 'a non-numeric Steam ID is dropped');
  assert.equal(a.playerName, 'A B C D');
  assert.equal(a.time, null);
  assert.equal(a.score, 0);
  assert.equal(a.detail.length, 300);
  assert.equal(a.severity, 2);
  assert.deepEqual(p.feed.suspects.map((s) => s.playerId), ['42']);
  assert.deepEqual(p.feed.suspects[0].counts, { speed: 0, fly: 2 });
});

test('readFeed: missing, damaged (truncated mid-write) and good', async () => {
  const dir = await tmp();
  assert.equal((await SF.readFeed(dir)).state, 'missing');
  fs.writeFileSync(path.join(dir, SF.FEED_FILE), JSON.stringify(feed()).slice(0, 200));
  const d = await SF.readFeed(dir);
  assert.equal(d.state, 'damaged');
  assert.match(d.reason, /not valid JSON/);
  fs.writeFileSync(path.join(dir, SF.FEED_FILE), JSON.stringify(feed()));
  const ok = await SF.readFeed(dir);
  assert.equal(ok.state, 'ok');
  assert.equal(ok.feed.alerts.length, 3);
});

const EVIDENCE = [
  '2026-10-04T11:58:00Z #40 speed Grimsby the Cutpurse (76561190000000009) +12.0 -> 61.5 at 102.3,40.1,-88.0 ping 120 ms: 23.4 m/s over 3 s (limit 11.0)',
  '2026-10-04T11:58:30Z #41 melee_range Ysolde Corvane (76561190000000004) +8.0 -> 31.2 ping 80 ms: hit at 9.1 m',
  '2026-10-04T11:59:30Z #42 item_jump Grimsby the Cutpurse (76561190000000009) +30.0 -> 88.0 at 1,2,3 ping 95 ms: +2400 Iron in 10 s',
  '2026-10-04T12:01:00Z #43 admin_action Grimsby the Cutpurse (76561190000000009): by console: froze for 15 min',
  'garbage line',
  ''
].join('\n');

test('parseEvidenceLine reads both line shapes the plugin writes', () => {
  const e = SF.parseEvidenceLine(EVIDENCE.split('\n')[0]);
  assert.deepEqual(e, { time: '2026-10-04T11:58:00Z', id: 40, kind: 'speed', playerName: 'Grimsby the Cutpurse', playerId: '76561190000000009', points: 12, scoreAfter: 61.5, pos: '102.3,40.1,-88.0', pingMs: 120, detail: '23.4 m/s over 3 s (limit 11.0)', admin: false });
  const b = SF.parseEvidenceLine(EVIDENCE.split('\n')[1]);
  assert.equal(b.pos, null);
  assert.equal(b.pingMs, 80);
  const adm = SF.parseEvidenceLine(EVIDENCE.split('\n')[3]);
  assert.equal(adm.admin, true);
  assert.equal(adm.points, null);
  assert.equal(adm.detail, 'by console: froze for 15 min');
  assert.equal(SF.parseEvidenceLine('garbage line'), null);
});

test('readEvidence: one player, newest first, across daily files', async () => {
  const dir = await tmp();
  const logs = path.join(dir, 'logs', 'RealmSentinel');
  await fsp.mkdir(logs, { recursive: true });
  fs.writeFileSync(path.join(logs, 'realmsentinel_evidence-2026-10-04.txt'), EVIDENCE);
  fs.writeFileSync(path.join(logs, 'realmsentinel_evidence-2026-10-03.txt'), '2026-10-03T20:00:00Z #12 fly Grimsby the Cutpurse (76561190000000009) +5.0 -> 5.0 ping 60 ms: rose 34 m in 10 s\n');
  fs.writeFileSync(path.join(logs, 'realmsentinel_alerts-2026-10-04.txt'), 'not evidence');
  const r = await SF.readEvidence(path.join(dir, 'logs'), '76561190000000009');
  assert.equal(r.files, 2);
  assert.deepEqual(r.lines.map((l) => l.id), [43, 42, 40, 12]);
  assert.equal((await SF.readEvidence(path.join(dir, 'logs'), '76561190000000004')).lines.length, 1);
  assert.deepEqual((await SF.readEvidence(path.join(dir, 'nope'), '1')).lines, []);
  await assert.rejects(SF.readEvidence(path.join(dir, 'logs'), '../x'), /Unknown player/);
  assert.equal((await SF.readEvidence(path.join(dir, 'logs'), '76561190000000009', { limit: 2 })).lines.length, 2);
});

test('commandsFor fills the in-game /sentinel commands, quoting names with spaces', () => {
  const c = SF.commandsFor('Grimsby the Cutpurse');
  assert.deepEqual(c.map((x) => x.command), [
    '/sentinel report "Grimsby the Cutpurse"',
    '/sentinel freeze "Grimsby the Cutpurse" 15',
    '/sentinel unfreeze "Grimsby the Cutpurse"',
    '/sentinel clear "Grimsby the Cutpurse"',
    '/sentinel ban "Grimsby the Cutpurse" confirm'
  ]);
  assert.equal(SF.commandsFor('Wren')[0].command, '/sentinel report Wren');
});

function harness(dir, { consoleReady = true } = {}) {
  const handlers = {};
  const pushes = [];
  const acts = [];
  const copied = [];
  const root = path.join(dir, 'server');
  const api = SF.registerSteward({
    handle: (ch, fn) => (handlers[ch] = fn),
    userData: path.join(dir, 'profile'),
    fleet: () => [{ id: 's1' }],
    instOf: (id) => {
      if (!/^s[1-4]$/.test(id)) throw new Error('unknown server');
      return { id };
    },
    rootOf: () => root,
    oxideDir: async () => path.join(root, 'oxide'),
    court: {
      consoleReady: () => consoleReady,
      act: async (id, action, args) => {
        acts.push({ id, action, args });
        return { ok: true, summary: action };
      }
    },
    clipboard: { writeText: (t) => copied.push(t) },
    push: (m) => pushes.push(m),
    log: () => {},
    pollMs: 1e9
  });
  return { handlers, pushes, acts, copied, root, api };
}

test('registerSteward: status, unseen alerts, live push, mark seen', async () => {
  const dir = await tmp();
  const h = harness(dir);
  try {
    let st = await h.handlers['sentinel:status']('s1');
    assert.equal(st.state, 'missing');
    assert.equal(st.installed, false);
    const data = path.join(h.root, 'oxide', 'data');
    await fsp.mkdir(data, { recursive: true });
    await fsp.mkdir(path.join(h.root, 'oxide', 'plugins'), { recursive: true });
    fs.writeFileSync(path.join(h.root, 'oxide', 'plugins', 'RealmSentinel.cs'), '// plugin');
    fs.writeFileSync(path.join(data, SF.FEED_FILE), JSON.stringify(feed()));
    await h.api.poll();
    assert.equal(h.pushes.at(-1).unseen, 3);
    assert.equal(h.pushes.at(-1).fresh, null, 'the first read is not a fresh alert');
    st = await h.handlers['sentinel:status']('s1');
    assert.equal(st.state, 'ok');
    assert.equal(st.installed, true);
    assert.equal(st.console, true);
    assert.equal(st.unseen, 3);
    assert.equal(st.feed.suspects[0].commands[0].command, '/sentinel report "Grimsby the Cutpurse"');
    // A new alert arrives.
    const f2 = feed();
    f2.Alerts.push({ Id: 10, Time: '2026-10-04T12:02:00Z', Kind: 'fly', PlayerId: '76561190000000004', PlayerName: 'Ysolde Corvane', Score: 45, Response: 'alert', Detail: 'rose 40 m' });
    await new Promise((r) => setTimeout(r, 20));
    fs.writeFileSync(path.join(data, SF.FEED_FILE), JSON.stringify(f2));
    const t = new Date(Date.now() + 5000);
    fs.utimesSync(path.join(data, SF.FEED_FILE), t, t);
    await h.api.poll();
    assert.equal(h.pushes.at(-1).unseen, 4);
    assert.deepEqual(h.pushes.at(-1).fresh, { playerName: 'Ysolde Corvane', kind: 'fly', response: 'alert', score: 45 });
    await h.handlers['sentinel:markSeen']('s1', 10);
    assert.equal(h.pushes.at(-1).unseen, 0);
    assert.equal((await h.handlers['sentinel:status']('s1')).unseen, 0);
    // The seen mark survives a restart of Steward.
    assert.equal(new SF.SeenStore(path.join(dir, 'profile', 'sentinel')).get('s1'), 10);
  } finally {
    h.api.stop();
  }
});

test('registerSteward: actions go through the Court; /sentinel commands are copied', async () => {
  const dir = await tmp();
  const h = harness(dir);
  try {
    await h.handlers['sentinel:act']('s1', 'kick', { name: 'Grimsby the Cutpurse', kind: 'speed' });
    await h.handlers['sentinel:act']('s1', 'ban', { name: 'Grimsby the Cutpurse', kind: 'item_jump', days: 7 });
    await h.handlers['sentinel:act']('s1', 'reload');
    assert.deepEqual(h.acts, [
      { id: 's1', action: 'kick', args: { name: 'Grimsby the Cutpurse', reason: 'Sentinel: speed' } },
      { id: 's1', action: 'ban', args: { name: 'Grimsby the Cutpurse', days: 7, reason: 'Sentinel: item_jump' } },
      { id: 's1', action: 'reload', args: { plugin: 'RealmSentinel' } }
    ]);
    await assert.rejects(h.handlers['sentinel:act']('s1', 'shutdown', {}), /Unknown Sentinel action/);
    await assert.rejects(h.handlers['sentinel:act']('s1', 'kick', { name: '' }), /Choose a player/);
    assert.throws(() => h.handlers['sentinel:copy']('s1', 'Wren', 'nuke'), /Unknown Sentinel command/);
    assert.equal(h.handlers['sentinel:copy']('s1', 'Old Tom', 'freeze'), '/sentinel freeze "Old Tom" 15');
    assert.deepEqual(h.copied, ['/sentinel freeze "Old Tom" 15']);
  } finally {
    h.api.stop();
  }
});
