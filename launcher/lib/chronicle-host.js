'use strict';

// Runs the Chronicle web service (chronicle/server.js createApp) inside the client's main process,
// bound to 127.0.0.1:8787 only. It reads the plugin data files; it never writes to them.

const http = require('http');
const path = require('path');
const fsp = require('fs/promises');
const { pathToFileURL } = require('url');

const HOST = '127.0.0.1';
const PORT = 8787;

class ChronicleHost {
  constructor(chronicleDir) {
    this.dir = chronicleDir;
    this.server = null;
    this.dataDir = null;
    this.error = null;
    this.external = false;
    this.mod = null;
  }

  get url() {
    return `http://${HOST}:${PORT}`;
  }

  status() {
    return { running: !!this.server, external: this.external, url: this.url, overlayUrl: `${this.url}/overlay`, realmUrl: `${this.url}/realm`, dataDir: this.dataDir, error: this.error };
  }

  async load() {
    if (!this.mod) {
      const file = path.join(this.dir, 'server.js');
      await fsp.access(file);
      this.mod = await import(pathToFileURL(file).href);
    }
    return this.mod;
  }

  async start(dataDir) {
    if (this.server && this.dataDir === dataDir) return this.status();
    await this.stop();
    this.dataDir = dataDir;
    this.error = null;
    this.external = false;
    try {
      const { createApp } = await this.load();
      const server = http.createServer(createApp({ dataDir, host: HOST, port: PORT, staleAfterSec: 180 }));
      await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(PORT, HOST, () => {
          server.off('error', reject);
          resolve();
        });
      });
      server.on('error', (e) => {
        this.error = e.message;
      });
      this.server = server;
    } catch (e) {
      if (e && e.code === 'EADDRINUSE') {
        // Another Chronicle (for example one started by Realm.bat) already serves this port; use it.
        this.external = true;
        this.error = `Port ${PORT} is already in use by another program (perhaps a Chronicle started from Realm.bat). The client shows what that one serves.`;
      } else {
        this.error = `The Chronicle could not start: ${e && e.message ? e.message : e}`;
      }
    }
    return this.status();
  }

  async stop() {
    const s = this.server;
    this.server = null;
    if (s) {
      if (typeof s.closeAllConnections === 'function') s.closeAllConnections();
      await new Promise((r) => s.close(() => r()));
    }
  }
}

module.exports = { ChronicleHost, HOST, PORT };
