'use strict';

// Realm Steward bridge: named calls only, no raw ipcRenderer, no Node APIs. Every argument is
// validated again in the main process. (The player edition uses player/preload.js, which exposes
// none of the server, setup, firewall or publish calls below.)
const { contextBridge, ipcRenderer } = require('electron');

const PUSH_TYPES = new Set(['progress', 'busy', 'server-lines', 'server-status', 'overlay', 'window', 'closing', 'fleet', 'sentinel']);

// Calls resolve with the data, or reject with { message, details, cancelled }.
async function call(channel, ...args) {
  const res = await ipcRenderer.invoke(channel, ...args);
  if (res && res.ok) return res.data;
  const err = (res && res.error) || { message: 'No answer from the app.', details: '' };
  throw Object.assign(new Error(err.message), { details: err.details, cancelled: !!err.cancelled });
}

const c0 = (channel) => () => call(channel);
const c1 = (channel) => (a) => call(channel, a);
const c2 = (channel) => (a, b) => call(channel, a, b);

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
    openFolder: c2('settings:openFolder')
  },

  setup: {
    status: c1('setup:status'),
    run: c2('setup:run'),
    cancel: c0('setup:cancel'),
    finish: c1('setup:finish')
  },

  fleet: {
    list: c0('fleet:list'),
    instance: c1('fleet:instance'),
    add: c1('fleet:add'),
    remove: c1('fleet:remove'),
    update: c2('fleet:update')
  },

  // Every server call takes the instance id ('s1'..'s4') first.
  server: {
    status: c1('server:status'),
    log: c2('server:log'),
    start: c2('server:start'),
    prestart: c1('server:prestart'),
    stopHolder: c1('server:stopHolder'),
    adopt: c1('server:adopt'),
    stop: c1('server:stop'),
    forceStop: c1('server:force'),
    restart: c1('server:restart'),
    command: c2('server:command'),
    deployPlugins: c1('server:deployPlugins'),
    dataStatus: c1('server:dataStatus'),
    backup: c2('server:backup'),
    listBackups: c1('server:listBackups'),
    restore: c2('server:restore'),
    undoOxide: c1('server:undoOxide')
  },

  overlay: {
    status: c0('overlay:status'),
    copyUrl: c0('overlay:copyUrl')
  },

  goPublic: {
    status: c1('public:status'),
    setNetwork: c2('public:setNetwork'),
    firewallAdd: c1('public:firewallAdd'),
    firewallRemove: c1('public:firewallRemove'),
    selfTest: c1('public:selfTest')
  },

  publish: {
    status: c0('publish:status'),
    createKey: c1('publish:createKey'),
    write: c1('publish:write'),
    copyKey: c0('publish:copyKey')
  },

  // Publish news and player updates (lib/publish-feeds.js): signed with the same key as servers.json.
  feeds: {
    status: c0('feeds:status'),
    saveNewsDraft: c2('feeds:saveNewsDraft'),
    writeNews: c1('feeds:writeNews'),
    pickInstaller: c0('feeds:pickInstaller'),
    inspectInstaller: c1('feeds:inspectInstaller'),
    writeUpdate: c1('feeds:writeUpdate')
  },

  // The Sentinel screen (lib/sentinel-feed.js): RealmSentinel's feed, evidence and actions. Server id first.
  sentinel: {
    status: c1('sentinel:status'),
    evidence: c2('sentinel:evidence'),
    markSeen: c2('sentinel:markSeen'),
    act: (id, action, args) => call('sentinel:act', id, action, args),
    copy: (id, name, command) => call('sentinel:copy', id, name, command)
  },

  // Realm features (lib/features.js): each plugin's main switches in oxide/config/<Plugin>.json.
  features: {
    list: c1('features:list'),
    set: (id, plugin, pathName, value) => call('features:set', id, plugin, pathName, value)
  },

  // Connection Doctor (read-only checks and log reading; see lib/doctor.js).
  doctor: {
    run: c1('doctor:run'),
    log: c1('doctor:log'),
    classify: c1('doctor:classify'),
    copyReport: c1('doctor:copyReport')
  },

  // Live admin console and the Court (moderation; see lib/court-host.js). Server id first.
  court: {
    status: c1('court:status'),
    act: (id, action, args) => call('court:act', id, action, args),
    players: c1('court:players'),
    feed: c2('court:feed'),
    log: c1('court:log'),
    setConsole: c2('court:setConsole'),
    pluginCommands: c0('court:pluginCommands'),
    copyPluginCommand: c2('court:copyPluginCommand'),
    openLog: c0('court:openLog')
  },

  // Discord herald (opt-in webhook relay; see lib/discord.js). The webhook token never comes back.
  discord: {
    get: c0('discord:get'),
    save: c1('discord:save'),
    test: c0('discord:test'),
    forget: c0('discord:forget')
  },

  // Main -> renderer notifications (progress, console lines, status). Only known types pass.
  onPush: (fn) => {
    if (typeof fn !== 'function') return;
    ipcRenderer.on('realm:push', (_event, msg) => {
      if (msg && PUSH_TYPES.has(msg.type)) fn(msg);
    });
  }
});
