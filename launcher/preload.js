'use strict';

// Narrow bridge: named calls only, no raw ipcRenderer, no Node APIs. Every argument is validated
// again in the main process.
const { contextBridge, ipcRenderer } = require('electron');

const PUSH_TYPES = new Set(['progress', 'busy', 'server-lines', 'server-status', 'overlay', 'window', 'closing']);

// Calls resolve with the data, or reject with { message, details, cancelled }.
async function call(channel, ...args) {
  const res = await ipcRenderer.invoke(channel, ...args);
  if (res && res.ok) return res.data;
  const err = (res && res.error) || { message: 'No answer from the app.', details: '' };
  throw Object.assign(new Error(err.message), { details: err.details, cancelled: !!err.cancelled });
}

const c0 = (channel) => () => call(channel);
const c1 = (channel) => (arg) => call(channel, arg);

contextBridge.exposeInMainWorld('realm', {
  appInfo: c0('app:info'),
  getConfig: c0('realm:config'),
  getNews: c0('realm:news'),
  getState: c0('realm:state'),
  getEvents: c1('realm:events'),
  getRealm: c0('realm:realm'),
  copyAddress: c0('realm:copyAddress'),
  play: c0('realm:play'),
  openLink: c1('realm:openLink'),
  copyText: c1('realm:copyText'),
  windowAction: c1('realm:window'),

  settings: {
    get: c0('settings:get'),
    save: c1('settings:save'),
    saveNews: c1('settings:saveNews'),
    browse: c1('settings:browse'),
    openFolder: c1('settings:openFolder')
  },

  setup: {
    status: c0('setup:status'),
    run: c1('setup:run'),
    cancel: c0('setup:cancel'),
    finish: c0('setup:finish')
  },

  server: {
    status: c0('server:status'),
    log: c1('server:log'),
    start: c0('server:start'),
    stop: c0('server:stop'),
    forceStop: c0('server:force'),
    restart: c0('server:restart'),
    command: c1('server:command'),
    deployPlugins: c0('server:deployPlugins'),
    backup: c1('server:backup'),
    listBackups: c0('server:listBackups'),
    restore: c1('server:restore'),
    undoOxide: c0('server:undoOxide')
  },

  overlay: {
    status: c0('overlay:status'),
    copyUrl: c0('overlay:copyUrl')
  },

  // Main -> renderer notifications (progress, console lines, status). Only known types pass.
  onPush: (fn) => {
    if (typeof fn !== 'function') return;
    ipcRenderer.on('realm:push', (_event, msg) => {
      if (msg && PUSH_TYPES.has(msg.type)) fn(msg);
    });
  }
});
