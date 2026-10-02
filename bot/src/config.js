// Configuration: read from the process environment, with an optional .env file next to index.js.
// Real environment variables always win over the file. The token is never logged or echoed back.

import { readFileSync } from 'node:fs';
import { dirname, isAbsolute, join, resolve } from 'node:path';
import { parseEnv } from 'node:util';

export const DEFAULT_DATA_DIR = 'G:\\RealmTest\\server\\oxide\\data';
const SNOWFLAKE = /^\d{17,20}$/;
const PLACEHOLDER_TOKEN = 'paste-your-bot-token-here';

export class ConfigError extends Error {
  constructor(problems) {
    super(`Bot configuration is not valid:\n  - ${problems.join('\n  - ')}`);
    this.problems = problems;
  }
}

// Reads KEY=VALUE pairs from a .env file. Missing file = empty. Never throws for a missing file.
export function readEnvFile(path) {
  let text;
  try {
    text = readFileSync(path, 'utf8');
  } catch (e) {
    if (e.code === 'ENOENT') return {};
    throw e;
  }
  return parseEnv(text.replace(/^\uFEFF/, ''));
}

export function mergeEnv(fileEnv, processEnv) {
  const out = { ...fileEnv };
  for (const [k, v] of Object.entries(processEnv)) if (v !== undefined && v !== '') out[k] = v;
  return out;
}

const bool = (v, dflt) => {
  if (v === undefined || v === '') return dflt;
  return /^(1|true|yes|on)$/i.test(String(v).trim());
};

function intIn(v, dflt, lo, hi, name, problems) {
  if (v === undefined || v === '') return dflt;
  const n = Number(v);
  if (!Number.isInteger(n) || n < lo || n > hi) {
    problems.push(`${name} must be a whole number from ${lo} to ${hi} (got "${v}")`);
    return dflt;
  }
  return n;
}

function httpsUrl(v, name, problems) {
  if (!v) return null;
  let u;
  try {
    u = new URL(v);
  } catch {
    problems.push(`${name} is not a valid URL`);
    return null;
  }
  if (u.protocol !== 'https:' || u.username || u.password) {
    problems.push(`${name} must be a plain https:// address`);
    return null;
  }
  return u.toString();
}

export function isLoopbackHost(host) {
  const h = host.replace(/^\[|\]$/g, '').toLowerCase();
  return h === 'localhost' || h === '::1' || /^127\.\d+\.\d+\.\d+$/.test(h);
}

// Builds the validated config. `baseDir` resolves relative paths (the bot folder).
// With { requireDiscord: false } the Discord credentials may be missing (used by --check and by tests).
export function loadConfig(env, { baseDir = process.cwd(), requireDiscord = true } = {}) {
  const problems = [];
  const warnings = [];

  const token = (env.DISCORD_TOKEN || '').trim();
  if (requireDiscord) {
    if (!token || token === PLACEHOLDER_TOKEN) problems.push('DISCORD_TOKEN is not set (put it in bot/.env)');
    else if (/\s/.test(token) || token.length < 50) problems.push('DISCORD_TOKEN does not look like a bot token');
  }

  const applicationId = (env.DISCORD_APPLICATION_ID || '').trim();
  const guildId = (env.DISCORD_GUILD_ID || '').trim();
  const zero = (s) => /^0+$/.test(s);
  if (requireDiscord && (!SNOWFLAKE.test(applicationId) || zero(applicationId))) problems.push('DISCORD_APPLICATION_ID must be your application id (17 to 20 digits)');
  if (guildId && (!SNOWFLAKE.test(guildId) || zero(guildId))) problems.push('DISCORD_GUILD_ID must be a server id (17 to 20 digits)');

  let register = (env.REALM_REGISTER_COMMANDS || (guildId ? 'guild' : 'global')).trim().toLowerCase();
  if (!['guild', 'global', 'off'].includes(register)) {
    problems.push('REALM_REGISTER_COMMANDS must be guild, global or off');
    register = 'off';
  }
  if (register === 'guild' && !guildId) problems.push('REALM_REGISTER_COMMANDS=guild needs DISCORD_GUILD_ID');

  const statusChannelId = (env.REALM_STATUS_CHANNEL_ID || '').trim() || null;
  if (statusChannelId && !SNOWFLAKE.test(statusChannelId)) problems.push('REALM_STATUS_CHANNEL_ID must be a channel id (17 to 20 digits)');

  let chronicleUrl = (env.REALM_CHRONICLE_URL || 'http://127.0.0.1:8787').trim().replace(/\/+$/, '');
  try {
    const u = new URL(chronicleUrl);
    if (!['http:', 'https:'].includes(u.protocol)) throw new Error('scheme');
    if (u.username || u.password || u.search || u.hash) throw new Error('extra');
    if (!isLoopbackHost(u.hostname)) warnings.push(`REALM_CHRONICLE_URL points away from this PC (${u.hostname}); the Chronicle service is meant to stay local`);
  } catch {
    problems.push('REALM_CHRONICLE_URL must be an http(s) address such as http://127.0.0.1:8787');
    chronicleUrl = 'http://127.0.0.1:8787';
  }

  const dataDir = resolve(baseDir, env.REALM_DATA_DIR || DEFAULT_DATA_DIR);
  const configDir = env.REALM_CONFIG_DIR ? resolve(baseDir, env.REALM_CONFIG_DIR) : join(dirname(dataDir), 'config');

  const joinAddress = (env.REALM_JOIN_ADDRESS || '').trim();
  if (joinAddress && !/^[A-Za-z0-9.-]{1,253}$/.test(joinAddress) && !/^\[?[0-9a-fA-F:]+\]?$/.test(joinAddress)) {
    problems.push('REALM_JOIN_ADDRESS must be a host name or IP address (no scheme, no port)');
  }
  const serverId = (env.REALM_SERVER_ID || '').trim();
  if (serverId && !/^[a-z0-9-]{1,24}$/.test(serverId)) problems.push('REALM_SERVER_ID must be 1 to 24 lower-case letters, digits or "-" (the id in the player app\'s server list)');

  const stateFile = env.REALM_BOT_STATE_FILE || 'state/bot-state.json';

  const config = {
    token,
    applicationId,
    guildId: guildId || null,
    register,
    status: {
      channelId: statusChannelId,
      intervalSec: intIn(env.REALM_STATUS_INTERVAL_SECONDS, 60, 15, 86400, 'REALM_STATUS_INTERVAL_SECONDS', problems),
    },
    chronicleUrl,
    dataDir,
    configDir,
    realmName: (env.REALM_NAME || 'The Realm of Ostreval').trim().slice(0, 80),
    join: {
      address: joinAddress || null,
      port: intIn(env.REALM_JOIN_PORT, 7350, 1, 65535, 'REALM_JOIN_PORT', problems),
      serverId: serverId || null,
      playerAppUrl: httpsUrl((env.REALM_PLAYER_APP_URL || '').trim(), 'REALM_PLAYER_APP_URL', problems),
      portalUrl: httpsUrl((env.REALM_PORTAL_URL || '').trim(), 'REALM_PORTAL_URL', problems),
    },
    swear: {
      enabled: bool(env.REALM_SWEAR_ENABLED, false),
      createRoles: bool(env.REALM_SWEAR_CREATE_ROLES, true),
      cooldownMin: intIn(env.REALM_SWEAR_COOLDOWN_MINUTES, 60, 0, 10080, 'REALM_SWEAR_COOLDOWN_MINUTES', problems),
      maxRoles: intIn(env.REALM_SWEAR_MAX_ROLES, 25, 1, 100, 'REALM_SWEAR_MAX_ROLES', problems),
    },
    stateFile: isAbsolute(stateFile) ? stateFile : resolve(baseDir, stateFile),
    warnings,
  };
  if (problems.length) throw new ConfigError(problems);
  return config;
}

// A copy that is safe to print: the token is replaced by its length.
export function describeConfig(config) {
  return JSON.stringify({ ...config, token: config.token ? `(set, ${config.token.length} chars)` : '(not set)' }, null, 2);
}
