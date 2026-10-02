'use strict';

// Realm (player edition) bridge. Named calls only. There is deliberately no server, setup, fleet,
// firewall, publish or settings-of-a-server call here: test/player-edition.test.js checks that the
// exposed API and every channel it can reach stay inside this list.
const { contextBridge, ipcRenderer } = require('electron');

const PUSH_TYPES = new Set(['status', 'servers', 'deeplink', 'window']);

async function call(channel, ...args) {
  const res = await ipcRenderer.invoke(channel, ...args);
  if (res && res.ok) return res.data;
  const err = (res && res.error) || { message: 'No answer from the app.', details: '' };
  throw Object.assign(new Error(err.message), { details: err.details });
}

const c0 = (channel) => () => call(channel);
const c1 = (channel) => (a) => call(channel, a);

contextBridge.exposeInMainWorld('realm', {
  appInfo: c0('app:info'),
  getConfig: c0('player:config'),
  servers: c1('player:servers'),
  status: c1('player:status'),
  installState: c0('player:installState'),
  join: c1('player:join'),
  joinBest: c0('player:joinBest'),
  installGame: c0('player:installGame'),
  copyAddress: c1('player:copyAddress'),
  openLink: c1('player:openLink'),
  prefs: c0('player:prefs'),
  setPrefs: c1('player:setPrefs'),
  takeLink: c0('player:takeLink'),
  windowAction: c1('player:window'),
  // Connection Doctor ("Can't join?"): read-only checks, log classifier, redacted report.
  doctorRun: c0('player:doctorRun'),
  doctorClassify: c1('player:doctorClassify'),
  doctorReport: c1('player:doctorReport'),
  // Realm feed (home panel): latest public Chronicle events and the next announced event.
  feed: c1('player:feed'),
  onPush: (fn) => {
    if (typeof fn !== 'function') return;
    ipcRenderer.on('realm:push', (_event, msg) => {
      if (msg && PUSH_TYPES.has(msg.type)) fn(msg);
    });
  }
});
