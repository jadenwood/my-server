'use strict';

// Network facts for the steward's Go Public screen and the pre-start port check. Read-only:
// nothing here changes the firewall or the router.

const net = require('net');
const dgram = require('dgram');
const os = require('os');

// [DEC] SteamServer.StartServer logs this once Steam auth is up; it carries the public IP Steam sees.
const STEAM_STARTED_RE = /Steam game server started\.\s*\(IP:\s*([0-9.]+),\s*Logged:\s*(True|False),\s*Secure:\s*(True|False)\)/i;
// [DEC] CoreServer logs this when the uLink listener is open.
const LISTENING_RE = /Server for (\d+) players started on port (\d+)/i;

function parseSteamLine(text) {
  const m = STEAM_STARTED_RE.exec(String(text || ''));
  return m ? { publicIp: m[1], logged: /true/i.test(m[2]), secure: /true/i.test(m[3]) } : null;
}

function parseListeningLine(text) {
  const m = LISTENING_RE.exec(String(text || ''));
  return m ? { maxPlayers: Number(m[1]), port: Number(m[2]) } : null;
}

function ipv4Parts(ip) {
  if (!/^\d{1,3}(\.\d{1,3}){3}$/.test(String(ip))) return null;
  const p = ip.split('.').map(Number);
  return p.every((n) => n <= 255) ? p : null;
}

function isPrivateIPv4(ip) {
  const p = ipv4Parts(ip);
  if (!p) return false;
  return p[0] === 10 || p[0] === 127 || (p[0] === 172 && p[1] >= 16 && p[1] <= 31) || (p[0] === 192 && p[1] === 168) || (p[0] === 169 && p[1] === 254);
}

// 100.64.0.0/10: carrier-grade NAT. A home server behind it cannot be reached by port forwarding.
function isCgnat(ip) {
  const p = ipv4Parts(ip);
  return !!p && p[0] === 100 && p[1] >= 64 && p[1] <= 127;
}

function lanAddresses() {
  const out = [];
  for (const [name, list] of Object.entries(os.networkInterfaces())) {
    for (const a of list || []) {
      if (a.family === 'IPv4' && !a.internal) out.push({ name, address: a.address });
    }
  }
  return out;
}

// Is something already bound to this port? Tries to bind it for a moment and lets go at once.
// Resolves 'free', 'in-use' or 'unknown'.
function probePort(port, proto, host = '0.0.0.0') {
  return new Promise((resolve) => {
    if (proto === 'udp') {
      const s = dgram.createSocket({ type: 'udp4', reuseAddr: false });
      s.once('error', (e) => {
        try {
          s.close();
        } catch {
          /* not bound */
        }
        resolve(e.code === 'EADDRINUSE' || e.code === 'EACCES' ? 'in-use' : 'unknown');
      });
      s.bind({ port, address: host, exclusive: true }, () => s.close(() => resolve('free')));
      return;
    }
    const srv = net.createServer();
    srv.once('error', (e) => resolve(e.code === 'EADDRINUSE' || e.code === 'EACCES' ? 'in-use' : 'unknown'));
    srv.listen({ port, host, exclusive: true }, () => srv.close(() => resolve('free')));
  });
}

function tcpConnect(host, port, timeoutMs = 2500) {
  return new Promise((resolve) => {
    const t0 = Date.now();
    const sock = net.connect({ host, port });
    const done = (ok, error) => {
      sock.destroy();
      resolve({ ok, ms: Date.now() - t0, error });
    };
    sock.setTimeout(timeoutMs, () => done(false, 'timeout'));
    sock.once('connect', () => done(true));
    sock.once('error', (e) => done(false, e.code || e.message));
  });
}

// Which program holds a local port (Windows): [{ pid, name, path }]. Read-only; port is a validated integer.
function portOwners(port, proto, { platform = process.platform } = {}) {
  if (platform !== 'win32' || !Number.isInteger(port) || port < 1 || port > 65535) return Promise.resolve([]);
  const cmd = proto === 'tcp' ? `Get-NetTCPConnection -LocalPort ${port} -State Listen` : `Get-NetUDPEndpoint -LocalPort ${port}`;
  // Get-Process cannot read the path of protected processes (the game client runs under Easy Anti-Cheat);
  // Win32_Process sometimes can, so it is the fallback.
  const script = `${cmd} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique | ForEach-Object { $p = Get-Process -Id $_ -ErrorAction SilentlyContinue; if ($p) { $path = $p.Path; if (-not $path) { $c = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $_) -ErrorAction SilentlyContinue; if ($c) { $path = $c.ExecutablePath } }; '' + $p.Id + '|' + $p.ProcessName + '|' + $path } }`;
  const { execFile } = require('child_process');
  return new Promise((resolve) => {
    execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script], { windowsHide: true, timeout: 15000 }, (err, stdout) => {
      if (err) return resolve([]);
      const out = [];
      for (const line of String(stdout).split(/\r?\n/)) {
        const [pid, name, p] = line.split('|');
        if (pid && name) out.push({ pid: Number(pid), name: name.trim(), path: (p || '').trim() });
      }
      resolve(out);
    });
  });
}

// Plain-English description of who holds a port, for the console and errors.
function describeOwners(owners, serverRoot) {
  if (!owners || !owners.length) return 'another program';
  return owners.map((o) => {
    const base = `${o.name}.exe (pid ${o.pid}${o.path ? ', ' + o.path : ''})`;
    if (/^rok$/i.test(o.name)) {
      const p = (o.path || '').toLowerCase();
      if (serverRoot && p && p.startsWith(String(serverRoot).toLowerCase())) return `${base}: an older copy of this server still running. End it in Task Manager > Details`;
      if (!p || /\\steamapps\\common\\reign of kings\\/i.test(o.path)) return `${base}: most likely the Reign of Kings GAME. Close the game, start the server first, then launch the game`;
      return `${base}: another Reign of Kings server`;
    }
    return base;
  }).join('; ');
}

module.exports = { portOwners, describeOwners, STEAM_STARTED_RE, parseSteamLine, parseListeningLine, isPrivateIPv4, isCgnat, lanAddresses, probePort, tcpConnect };
