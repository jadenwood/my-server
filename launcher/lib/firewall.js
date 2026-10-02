'use strict';

// Windows Firewall rules for "Go Public" (steward edition only).
//
// Realm adds rules only when the owner clicks "Add firewall rules" and then accepts the Windows
// UAC prompt. Every rule is scoped to that instance's ROK.exe and to its exact ports. One more rule
// BLOCKS the game's admin console ports (TCP 11000-11003), which accept commands from anyone and
// shut the server down when the last client disconnects ([DEC] SocketAdminConsole,
// docs/join-and-scale.md §4.1). "Remove rules" deletes exactly these rule names.
//
// The scripts are built from validated numbers and a validated path only; no free text from the
// renderer reaches them. They run through PowerShell's NetSecurity cmdlets via -EncodedCommand.
// UNVERIFIED: never run on Windows from here. UNVERIFIED: that ROK.exe (not Server.exe) owns the
// sockets (docs/seamless-design.md T8); owners who use Server.exe should check with netstat -ano.

const path = require('path');

const ADMIN_CONSOLE_PORTS = '11000-11003';

function ruleNames(id) {
  if (!/^s[1-4]$/.test(id)) throw new RangeError('bad instance id');
  return {
    game: `Realm ${id} game (UDP)`,
    ping: `Realm ${id} ping (TCP)`,
    query: `Realm ${id} Steam query (UDP)`,
    block: `Realm ${id} admin console BLOCK (TCP)`
  };
}

function checkPort(p) {
  if (!Number.isInteger(p) || p < 1024 || p > 65535) throw new RangeError(`bad port ${p}`);
  return p;
}

// The program path must be an absolute Windows path to ROK.exe without characters that could
// break out of a PowerShell single-quoted string or be read as a wildcard.
function checkProgram(exePath) {
  if (typeof exePath !== 'string' || exePath.length > 260) throw new RangeError('bad program path');
  if (!/^[A-Za-z]:\\/.test(exePath)) throw new RangeError('program path must be absolute');
  // '%' too: New-NetFirewallRule expands %VAR% in -Program, so the rule could name another file.
  if (/["'`$%\r\n\0*?[\]<>|;]/.test(exePath)) throw new RangeError('program path contains characters Realm will not pass to PowerShell');
  if (path.win32.basename(exePath).toLowerCase() !== 'rok.exe') throw new RangeError('rules are scoped to ROK.exe only');
  return exePath;
}

function psQuote(s) {
  return `'${String(s).replace(/'/g, "''")}'`;
}

// The rule set for one instance, as data (shown to the owner before anything runs).
function ruleTable(inst) {
  const n = ruleNames(inst.id);
  const game = checkPort(inst.ports.game);
  const query = checkPort(inst.ports.query);
  return [
    { name: n.game, action: 'Allow', protocol: 'UDP', ports: String(game) },
    { name: n.ping, action: 'Allow', protocol: 'TCP', ports: String(game) },
    { name: n.query, action: 'Allow', protocol: 'UDP', ports: String(query) },
    { name: n.block, action: 'Block', protocol: 'TCP', ports: ADMIN_CONSOLE_PORTS }
  ];
}

function rulesFor(inst, exePath) {
  const program = checkProgram(exePath);
  return ruleTable(inst).map((r) => ({ ...r, program }));
}

function addScript(inst, exePath) {
  const lines = ["$ErrorActionPreference = 'Stop'"];
  const names = Object.values(ruleNames(inst.id));
  // Replace, never duplicate: remove earlier copies of exactly these names first.
  lines.push(`Remove-NetFirewallRule -DisplayName ${names.map(psQuote).join(',')} -ErrorAction SilentlyContinue`);
  for (const r of rulesFor(inst, exePath)) {
    lines.push(
      `New-NetFirewallRule -DisplayName ${psQuote(r.name)} -Group 'Realm' -Direction Inbound -Action ${r.action} -Protocol ${r.protocol} -LocalPort ${r.ports} -Program ${psQuote(r.program)} -Profile Any | Out-Null`
    );
  }
  return lines.join('\r\n');
}

function removeScript(id) {
  const names = Object.values(ruleNames(id));
  return `Remove-NetFirewallRule -DisplayName ${names.map(psQuote).join(',')} -ErrorAction SilentlyContinue\r\nexit 0`;
}

// Reading rules needs no elevation. Prints one "name|enabled|action" line per Realm rule found.
function listScript(id) {
  const names = Object.values(ruleNames(id));
  return `Get-NetFirewallRule -DisplayName ${names.map(psQuote).join(',')} -ErrorAction SilentlyContinue | ForEach-Object { $_.DisplayName + '|' + $_.Enabled + '|' + $_.Action }`;
}

function encode(script) {
  return Buffer.from(script, 'utf16le').toString('base64');
}

// execFile('powershell.exe', args): starts an elevated PowerShell (one UAC prompt), waits for it and
// returns its exit code. If the owner declines the prompt, Start-Process throws and this exits 1223.
function elevatedArgs(script) {
  const inner = `'-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-EncodedCommand','${encode(script)}'`;
  const outer = `try { $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList ${inner}; exit $p.ExitCode } catch { Write-Output $_.Exception.Message; exit 1223 }`;
  return ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', outer];
}

function plainArgs(script) {
  return ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encode(script)];
}

function parseList(stdout) {
  const out = [];
  for (const line of String(stdout || '').split(/\r?\n/)) {
    const [name, enabled, action] = line.trim().split('|');
    if (name && /^Realm s[1-4] /.test(name)) out.push({ name, enabled: /true/i.test(enabled || ''), action: action || '' });
  }
  return out;
}

module.exports = { ADMIN_CONSOLE_PORTS, ruleNames, ruleTable, rulesFor, checkProgram, psQuote, addScript, removeScript, listScript, encode, elevatedArgs, plainArgs, parseList };
