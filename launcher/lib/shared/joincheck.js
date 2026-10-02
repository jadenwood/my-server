'use strict';

// Connection Doctor, player edition ("Can't join?"): the player-side subset of the checks.
// Read-only: the game install (Steam's app manifest), whether Steam runs and is signed in (registry,
// via lib/shared/steam.js), the game's own log files, and one A2S status query per listed server.
// No child_process here (the player-edition test allows it only in lib/shared/steam.js).

const GL = require('./gamelog');
const ST = require('./steam');
const A2S = require('./a2s');

const step = (id, label, status, detail, fix = '') => ({ id, label, status, detail, fix });

// input: { servers:[{id,name,address,port,queryPort}], clientScan:{findings}|null, clientLogs:[] }
// deps:  { game(), steam(), a2s(host, port) }
async function runPlayerChecks(input, deps) {
  const steps = [];
  const g = await deps.game();
  if (g.installed === true) steps.push(step('game', 'Reign of Kings installed', 'ok', `Found in ${g.library}.`));
  else if (g.installed === false) steps.push(step('game', 'Reign of Kings installed', 'bad', g.steam === false ? 'Steam is not installed for this Windows user.' : 'Reign of Kings is not installed in any Steam library on this PC.', g.steam === false ? 'Install Steam, sign in, then install Reign of Kings.' : 'Press "Install in Steam" in Realm.'));
  else steps.push(step('game', 'Reign of Kings installed', 'skip', 'Only checked on Windows.'));

  const st = await deps.steam();
  if (st.running === true && st.signedIn !== false) steps.push(step('steam', 'Steam', 'ok', `Steam is running${st.signedIn ? ' and signed in' : ''}.`));
  else if (st.running === true) steps.push(step('steam', 'Steam', 'bad', 'Steam is running but nobody is signed in.', 'Sign in to Steam (not offline mode); the game needs it to join.'));
  else if (st.running === false) steps.push(step('steam', 'Steam', 'bad', 'Steam is not running.', 'Start Steam and sign in, then press Join in Realm.'));
  else steps.push(step('steam', 'Steam', 'skip', 'Only checked on Windows.'));

  const cf = input.clientScan && input.clientScan.findings;
  const v = cf ? GL.verdict(cf, { resolvedBy: ['connecting'] }) : null;
  const ok = cf ? cf.find((f) => f.id === 'connecting') : null;
  if (v) steps.push(step('client-log', 'Your game log', v.severity, `${v.title}: "${v.match}"${v.time ? ` (${v.time})` : ''}`, v.fix));
  else if (ok) steps.push(step('client-log', 'Your game log', 'info', `The last join got as far as "${ok.match}"${ok.time ? ` (${ok.time})` : ''}. If it failed after that, the game shows the reason only in a popup: paste it below.`));
  else steps.push(step('client-log', 'Your game log', 'info', (input.clientLogs || []).length ? 'No known error in your game log.' : 'No game log found yet. It appears after the game has been started once.'));

  const servers = (input.servers || []).slice(0, 6);
  const results = await Promise.all(servers.map((s) => deps.a2s(s.address, s.queryPort).then((r) => [s, r])));
  for (const [s, r] of results) {
    if (r.ok) steps.push(step(`server-${s.id}`, s.name, 'ok', `Online, ${r.rttMs} ms. In the game: address ${s.address}, port ${s.port} (separate boxes).`));
    else steps.push(step(`server-${s.id}`, s.name, 'warn', `No status answer from ${s.address} (${r.error}).`, 'The server may be offline or restarting, or your network blocks UDP. Check Discord, then try again in a few minutes. Players on the server owner\'s home network must use its LAN address.'));
  }
  if (!servers.length) steps.push(step('servers', 'Servers', 'warn', 'Realm has no server list yet.', 'Check your internet connection and press Refresh.'));

  const bad = steps.find((x) => x.status === 'bad');
  const warn = steps.find((x) => x.status === 'warn');
  const pick = bad || warn;
  return {
    at: new Date().toISOString(),
    verdict: pick ? { status: pick.status, title: `${pick.label}: ${pick.detail}`, fix: pick.fix } : { status: 'ok', title: 'No problem found on this PC. If joining still fails, paste the message the game showed below.', fix: '' },
    steps
  };
}

// ctx: { handle, app, clipboard, servers(), installState(), appId }
function registerPlayer(ctx) {
  let last = null;

  async function clientLogs() {
    const install = await ctx.installState();
    // Development only (automated screenshots on Linux): a folder that imitates the game install.
    if (ctx.devGameDir) return { install, logs: await GL.resolveLogs(GL.clientLogCandidates(ctx.devGameDir, { P: require('path') })) };
    const dir = await GL.findGameDir(install, { appId: ctx.appId, parseVdf: ST.parseVdf });
    return { install, logs: await GL.resolveLogs(GL.clientLogCandidates(dir)) };
  }

  ctx.handle('player:doctorRun', async () => {
    const { install, logs } = await clientLogs();
    const scans = [];
    for (const l of logs) {
      try {
        scans.push(GL.scan((await GL.readFrom(l.path, null, { tail: 256 * 1024 })).text, { source: 'game' }));
      } catch {
        /* unreadable */
      }
    }
    let base = 0;
    const findings = [];
    for (const s of scans) {
      for (const f of s.findings) findings.push({ ...f, line: f.line + base });
      base += 1e6;
    }
    findings.sort((a, b) => b.line - a.line);
    const servers = ctx.servers().map((s) => ({ id: s.id, name: s.name, address: s.address, port: s.port, queryPort: s.queryPort }));
    const r = await runPlayerChecks(
      { servers, clientScan: { findings }, clientLogs: logs },
      { game: async () => install, steam: () => ST.steamState(), a2s: (h, p) => A2S.queryInfo(h, p, { timeoutMs: 1500 }) }
    );
    last = { ...r, findings, logs };
    return { ...r, findings, logs: logs.map((l) => ({ kind: l.kind, label: l.label })) };
  });

  ctx.handle('player:doctorClassify', (text) => {
    if (typeof text !== 'string' || text.length > 4000) throw new TypeError('message must be text (at most 4000 characters)');
    const findings = GL.scan(text, { source: 'pasted' }).findings;
    return { findings, verdict: GL.verdict(findings) };
  });

  ctx.handle('player:doctorReport', async (extra) => {
    if (!last) throw Object.assign(new Error('Run the check first.'), { friendly: true });
    const pasted = extra && typeof extra.pasted === 'string' ? extra.pasted.slice(0, 1000) : '';
    const logs = [];
    for (const l of last.logs.slice(0, 1)) {
      try {
        const t = (await GL.readFrom(l.path, null, { tail: 32 * 1024 })).text;
        logs.push({ label: l.label, lines: t.split(/\r?\n/).filter((x) => x.trim()) });
      } catch {
        /* skip */
      }
    }
    const text = GL.buildReport({ app: 'Realm', version: ctx.app.getVersion(), platform: process.platform, title: "Can't join", verdict: last.verdict, steps: last.steps, findings: last.findings, logs, pasted });
    ctx.clipboard.writeText(text);
    return { chars: text.length, text };
  });
}

module.exports = { runPlayerChecks, registerPlayer };
