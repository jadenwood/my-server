'use strict';

// Browser preview only. Stands in for preload.js when the UI is served over http(s)
// (scripts/preview-server.mjs). Under Electron the page loads from file:// with the real bridge,
// so this does nothing there.
(function () {
  if (window.realm || !/^https?:$/.test(location.protocol)) return;

  const params = new URLSearchParams(location.search);
  const offline = params.has('offline');
  const minutesAgo = (m) => new Date(Date.now() - m * 60000).toISOString();
  const log = (...a) => console.info('[mock realm]', ...a);
  const ok = (v) => Promise.resolve(v);
  const down = () => Promise.reject(Object.assign(new Error('connect ECONNREFUSED 127.0.0.1:8787'), { details: '' }));

  const config = {
    realmName: 'The Realm',
    tagline: 'Swear an oath. Claim a crown. Answer for it.',
    pollSeconds: 15,
    steamAppId: 344760,
    server: { name: 'Realm - Local Test', address: '127.0.0.1', port: 7350, maxPlayers: 40 },
    links: [{ id: 'discord', label: 'Discord' }, { id: 'chronicle', label: 'Chronicle' }]
  };
  const state = {
    king: 'Aldric Thornvale', house: 'Thornvale', since: minutesAgo(190),
    houses: [
      { name: 'Thornvale', sigil: 'Black Thorn on Gold', liege: null, members: 6 },
      { name: 'Ashmere', sigil: 'Grey Heron', liege: 'Thornvale', members: 4 },
      { name: 'Corrow', sigil: 'Red Stag', liege: null, members: 5 },
      { name: 'Vey', sigil: 'Silver Moth', liege: 'Corrow', members: 2 }
    ],
    online: 11, maxPlayers: 40, updated: minutesAgo(0.3), stale: false
  };
  const events = [
    { id: 41, ts: minutesAgo(190), type: 'coronation', title: 'Aldric Thornvale is crowned', detail: 'House Thornvale holds the throne', actors: [] },
    { id: 42, ts: minutesAgo(150), type: 'oath_sworn', title: 'House Ashmere swears to Thornvale', detail: 'Fealty sworn before the council', actors: [] },
    { id: 43, ts: minutesAgo(95), type: 'decree', title: "Decree: the King's Peace", detail: 'No raiding of the market until dusk', actors: [] },
    { id: 44, ts: minutesAgo(52), type: 'claim_declared', title: 'House Corrow declares a claim', detail: 'Rebellion window opens Saturday', actors: [] },
    { id: 45, ts: minutesAgo(9), type: 'ransom_paid', title: 'Edda Corrow is ransomed', detail: 'House Corrow paid in full', actors: [] }
  ];
  const news = [
    { date: '2026-10-01', title: 'The gates open for local trials', body: 'The Realm is being tested on a single machine before any public opening.' },
    { date: '2026-09-24', title: 'Houses and oaths', body: 'Found a house, choose a sigil and swear fealty to a liege.' },
    { date: '2026-09-17', title: 'Rebellion has rules', body: 'A rebellion must be declared and may only be fought inside its scheduled window.' }
  ];
  const steps = { steam: { done: true }, copy: { done: true }, firstRun: { done: true }, download: { done: true }, install: { done: true }, plugins: { done: true } };
  const serverStatus = { state: 'stopped', ready: false, players: [], copied: true, external: [], testRoot: 'G:\\RealmTest\\server', oxide: 'ROK_Data', oxideBackup: true, cfgExists: true };
  const overlay = { running: true, external: false, url: 'http://127.0.0.1:8787', overlayUrl: 'http://127.0.0.1:8787/overlay', realmUrl: 'http://127.0.0.1:8787/realm', dataDir: 'G:\\RealmTest\\server\\oxide\\data', error: null };

  window.realm = {
    appInfo: () => ok({ version: '0.0.0-preview', platform: 'browser', dev: true }),
    getConfig: () => ok(config),
    getNews: () => ok(news),
    getState: () => (offline ? down() : ok(state)),
    getEvents: (since) => (offline ? down() : ok(events.filter((e) => e.id > (since || 0)))),
    getRealm: () => ok({ state: offline ? null : state, stateError: offline ? 'offline' : null, crown: { council: [{ seat: 'Voice of the Crown', name: 'Mira Ashmere' }], claims: [], decrees: [] } }),
    copyAddress: () => ok('127.0.0.1:7350'),
    play: () => ok(log('would open steam://rungameid/344760')),
    openLink: (id) => ok(log('would open link', id)),
    copyText: () => ok(true),
    windowAction: (a) => ok(log('window', a)),
    settings: {
      get: () => ok({ testRoot: 'G:\\RealmTest\\server', testRootOk: true, serverExe: 'Server', discordUrl: '', steamServer: '', news, cfg: { exists: true, serverName: 'Realm - Local Test', maxPlayers: '30', portNumber: '7350' } }),
      save: () => ok({ saved: [], cfgChanges: [] }),
      saveNews: (l) => ok(l || news),
      browse: () => ok(null),
      openFolder: () => ok(true)
    },
    setup: {
      status: () => ok({ testRoot: 'G:\\RealmTest\\server', rootOk: true, steps, allDone: true, setupComplete: !params.has('setup') }),
      run: () => ok({}),
      cancel: () => ok(false),
      finish: () => ok(true)
    },
    server: {
      status: () => ok(serverStatus),
      log: () => ok([]),
      start: () => ok(serverStatus),
      stop: () => ok(serverStatus),
      forceStop: () => ok(serverStatus),
      restart: () => ok(serverStatus),
      command: () => ok(true),
      deployPlugins: () => ok({ copied: 0 }),
      backup: () => ok({ name: 'realm-saves-preview.zip', size: 1024 }),
      listBackups: () => ok({ dir: 'G:\\RealmTest\\backups', list: [] }),
      restore: () => ok({ restored: 0 }),
      undoOxide: () => ok({ restored: 0, removed: 0 })
    },
    overlay: { status: () => ok(overlay), copyUrl: () => ok(overlay.overlayUrl) },
    onPush: () => {}
  };
})();
