'use strict';

// Browser preview only. Stands in for preload.js when the UI is served over
// http(s) (scripts/preview-server.mjs). Under Electron the page loads from
// file:// with the real bridge, so this does nothing there.
(function () {
  if (window.realm || !/^https?:$/.test(location.protocol)) return;

  const params = new URLSearchParams(location.search);
  const offline = params.has('offline');
  const minutesAgo = (m) => new Date(Date.now() - m * 60000).toISOString();

  const config = {
    realmName: 'The Realm',
    tagline: 'Swear an oath. Claim a crown. Answer for it.',
    chronicleUrl: 'http://127.0.0.1:8787',
    pollSeconds: 15,
    server: { name: 'Realm - Local Test', address: '127.0.0.1', port: 7350 },
    steamAppId: 344760,
    links: [
      { id: 'discord', label: 'Discord' },
      { id: 'chronicle', label: 'Chronicle' }
    ]
  };

  const state = {
    king: 'Aldric Thornvale',
    house: 'Thornvale',
    since: minutesAgo(190),
    houses: [
      { name: 'Thornvale', sigil: 'black thorn on gold', liege: null, members: 6 },
      { name: 'Ashmere', sigil: 'grey heron on blue', liege: 'Thornvale', members: 4 },
      { name: 'Corrow', sigil: 'red stag on black', liege: null, members: 5 }
    ],
    online: 11,
    maxPlayers: 40,
    updated: minutesAgo(0.3)
  };

  const events = [
    { id: 41, ts: minutesAgo(190), type: 'coronation', title: 'Aldric Thornvale is crowned', detail: 'House Thornvale holds the throne', actors: ['Aldric Thornvale'] },
    { id: 42, ts: minutesAgo(150), type: 'oath_sworn', title: 'House Ashmere swears to Thornvale', detail: 'Fealty sworn before the council', actors: ['Mira Ashmere', 'Aldric Thornvale'] },
    { id: 43, ts: minutesAgo(95), type: 'decree', title: 'Decree: the King\'s Peace', detail: 'No raiding of the market until dusk', actors: ['Aldric Thornvale'] },
    { id: 44, ts: minutesAgo(52), type: 'claim_declared', title: 'House Corrow declares a claim', detail: 'Rebellion window opens Saturday 20:00', actors: ['Bran Corrow'] },
    { id: 45, ts: minutesAgo(31), type: 'ransom_set', title: 'Ransom set for Edda Corrow', detail: 'Capped at 400 gold', actors: ['Edda Corrow'] },
    { id: 46, ts: minutesAgo(9), type: 'ransom_paid', title: 'Edda Corrow is ransomed', detail: 'House Corrow paid in full', actors: ['Edda Corrow'] },
    { id: 47, ts: minutesAgo(2), type: 'house_founded', title: 'House Vey is founded', detail: 'Sigil: silver moth on green', actors: ['Tamsin Vey'] }
  ];

  const news = [
    { date: '2026-10-01', title: 'The gates open for local trials', body: 'The Realm is being tested on a single machine before any public opening. Expect resets, rough edges and a crown that changes hands often.' },
    { date: '2026-09-24', title: 'Houses and oaths', body: 'Found a house, choose a sigil and swear fealty to a liege. Breaking an oath is recorded in the Chronicle for all to read.' },
    { date: '2026-09-17', title: 'Rebellion has rules', body: 'A rebellion must be declared and may only be fought inside its scheduled window. Ransoms are capped. The crown rules with a council, not alone.' }
  ];

  const fail = { ok: false, error: 'connect ECONNREFUSED 127.0.0.1:8787' };
  const log = (...a) => console.info('[mock realm]', ...a);

  window.realm = {
    getConfig: async () => config,
    getNews: async () => news,
    getState: async () => (offline ? fail : { ok: true, state }),
    getEvents: async (since) => (offline ? fail : { ok: true, events: events.filter((e) => e.id > (since || 0)) }),
    copyAddress: async () => {
      const text = config.server.address + ':' + config.server.port;
      try { await navigator.clipboard.writeText(text); } catch { /* preview only */ }
      return text;
    },
    play: async () => log('would open steam://rungameid/' + config.steamAppId),
    openLink: async (id) => log('would open link', id),
    verifyInstall: async () => ({ found: true, results: [{ path: 'G:\\SteamLibrary\\steamapps\\common\\Reign Of Kings', exists: true }] }),
    windowAction: async (action) => log('window', action)
  };
})();
