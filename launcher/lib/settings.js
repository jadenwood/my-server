'use strict';

// Client settings, kept in the per-user app data folder (never next to the game or server).

const fsp = require('fs/promises');
const path = require('path');
const S = require('./safety');
const { writeFileAtomic } = require('./fsops');

const DEFAULTS = Object.freeze({
  version: 1,
  testRoot: S.DEFAULT_TEST_SERVER,
  steamServer: '',
  serverExe: 'Server',
  discordUrl: '',
  news: null,
  setupComplete: false,
  // Server instances s1..s4 (lib/fleet.js); s1's folder is testRoot.
  instances: [],
  // Last "Publish server list" inputs and the list's sequence number.
  publish: {}
});

class Settings {
  constructor(dir, defaults = {}) {
    this.file = path.join(dir, 'realm-settings.json');
    this.data = { ...DEFAULTS, ...defaults };
  }

  async load() {
    try {
      const raw = JSON.parse((await fsp.readFile(this.file, 'utf8')).replace(/^\uFEFF/, ''));
      if (raw && typeof raw === 'object') {
        for (const k of Object.keys(DEFAULTS)) if (k in raw) this.data[k] = raw[k];
      }
    } catch {
      /* first run */
    }
    if (typeof this.data.testRoot !== 'string' || !this.data.testRoot) this.data.testRoot = DEFAULTS.testRoot;
    if (this.data.serverExe !== 'ROK') this.data.serverExe = 'Server';
    if (typeof this.data.steamServer !== 'string') this.data.steamServer = '';
    if (typeof this.data.discordUrl !== 'string' || (this.data.discordUrl && !S.isHttpsUrl(this.data.discordUrl))) this.data.discordUrl = '';
    if (this.data.news !== null) {
      try {
        this.data.news = S.validateNews(this.data.news);
      } catch {
        this.data.news = null;
      }
    }
    this.data.setupComplete = this.data.setupComplete === true;
    if (!Array.isArray(this.data.instances)) this.data.instances = [];
    if (!this.data.publish || typeof this.data.publish !== 'object' || Array.isArray(this.data.publish)) this.data.publish = {};
    return this.data;
  }

  get(key) {
    return this.data[key];
  }

  async update(patch) {
    Object.assign(this.data, patch);
    await writeFileAtomic(this.file, JSON.stringify(this.data, null, 2));
    return this.data;
  }
}

module.exports = { Settings, DEFAULTS };
