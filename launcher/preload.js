'use strict';

// Minimal bridge: named calls only, no raw ipcRenderer or Node APIs.
const { contextBridge, ipcRenderer } = require('electron');

const call = (channel) => (...args) => ipcRenderer.invoke(channel, ...args);

contextBridge.exposeInMainWorld('realm', {
  getConfig: call('realm:config'),
  getNews: call('realm:news'),
  getState: call('realm:state'),
  getEvents: (since) => ipcRenderer.invoke('realm:events', since),
  copyAddress: call('realm:copyAddress'),
  play: call('realm:play'),
  openLink: (id) => ipcRenderer.invoke('realm:openLink', id),
  verifyInstall: call('realm:verifyInstall'),
  windowAction: (action) => ipcRenderer.invoke('realm:window', action)
});
