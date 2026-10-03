'use strict';

// Realm (player edition) bridge. Named calls only. There is deliberately no server, setup, fleet,
// firewall, publish or settings-of-a-server call here: test/player-edition.test.js checks that the
// exposed API and every channel it can reach stay inside this list.
const { contextBridge, ipcRenderer } = require('electron');

const PUSH_TYPES = new Set(['status', 'servers', 'news', 'update', 'deeplink', 'window']);

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
  takeLink: c0('player:takeLink'),
  windowAction: c1('player:window'),
  // News (signed news.json): announcements, season news, Chronicle highlights, realm changes.
  news: c1('player:news'),
  openNewsLink: c1('player:openNewsLink'),
  // Updates (signed update.json): check, download and verify, run the verified installer.
  update: c0('player:update'),
  checkUpdate: c0('player:updateCheck'),
  downloadUpdate: c0('player:updateDownload'),
  cancelUpdate: c0('player:updateCancel'),
  installUpdate: c0('player:updateInstall'),
  whatsNew: c0('player:whatsNew'),
  // Connection Doctor ("Can't join?"): read-only checks, log classifier, redacted report.
  doctorRun: c0('player:doctorRun'),
  doctorClassify: c1('player:doctorClassify'),
  doctorReport: c1('player:doctorReport'),
  onPush: (fn) => {
    if (typeof fn !== 'function') return;
    ipcRenderer.on('realm:push', (_event, msg) => {
      if (msg && PUSH_TYPES.has(msg.type)) fn(msg);
    });
  }
});
