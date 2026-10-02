import assert from 'node:assert/strict';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { ConfigError, describeConfig, loadConfig, mergeEnv, readEnvFile } from '../src/config.js';
import { tmpDir } from './helpers.js';

const TOKEN = 'A'.repeat(24) + '.' + 'B'.repeat(6) + '.' + 'C'.repeat(38);
const base = { DISCORD_TOKEN: TOKEN, DISCORD_APPLICATION_ID: '123456789012345678', DISCORD_GUILD_ID: '223456789012345678' };

test('defaults: local chronicle, guild registration, swear off, 60 s status, port 7350', () => {
  const c = loadConfig(base, { baseDir: '/srv/bot' });
  assert.equal(c.chronicleUrl, 'http://127.0.0.1:8787');
  assert.equal(c.register, 'guild');
  assert.equal(c.swear.enabled, false);
  assert.equal(c.status.channelId, null);
  assert.equal(c.status.intervalSec, 60);
  assert.equal(c.join.port, 7350);
  assert.equal(c.stateFile, '/srv/bot/state/bot-state.json');
  assert.deepEqual(c.warnings, []);
});

test('config dir defaults to the "config" folder next to the data dir', () => {
  const c = loadConfig({ ...base, REALM_DATA_DIR: '/x/server/oxide/data' });
  assert.equal(c.configDir, '/x/server/oxide/config');
});

test('missing or placeholder token, bad ids and bad URLs are reported together', () => {
  try {
    loadConfig({ DISCORD_TOKEN: 'paste-your-bot-token-here', DISCORD_APPLICATION_ID: '000000000000000000', REALM_STATUS_CHANNEL_ID: 'general', REALM_PLAYER_APP_URL: 'http://example.org/x', REALM_JOIN_PORT: '99999', REALM_SERVER_ID: 'Bad Id' });
    assert.fail('expected ConfigError');
  } catch (e) {
    assert.ok(e instanceof ConfigError);
    const all = e.problems.join('\n');
    for (const k of ['DISCORD_TOKEN', 'DISCORD_APPLICATION_ID', 'REALM_STATUS_CHANNEL_ID', 'REALM_PLAYER_APP_URL', 'REALM_JOIN_PORT', 'REALM_SERVER_ID']) assert.match(all, new RegExp(k));
  }
});

test('requireDiscord:false allows a check without credentials', () => {
  const c = loadConfig({}, { requireDiscord: false });
  assert.equal(c.token, '');
  assert.equal(c.register, 'global');
});

test('register=guild without a guild id is refused', () => {
  assert.throws(() => loadConfig({ ...base, DISCORD_GUILD_ID: '', REALM_REGISTER_COMMANDS: 'guild' }), /needs DISCORD_GUILD_ID/);
});

test('a non-loopback chronicle URL is allowed with a warning; a URL with credentials is refused', () => {
  assert.match(loadConfig({ ...base, REALM_CHRONICLE_URL: 'http://192.168.1.5:8787' }).warnings[0], /away from this PC/);
  assert.throws(() => loadConfig({ ...base, REALM_CHRONICLE_URL: 'http://u:p@127.0.0.1:8787' }), /REALM_CHRONICLE_URL/);
  assert.throws(() => loadConfig({ ...base, REALM_CHRONICLE_URL: 'file:///etc/passwd' }), /REALM_CHRONICLE_URL/);
  assert.deepEqual(loadConfig({ ...base, REALM_CHRONICLE_URL: 'http://[::1]:8787/' }).warnings, []);
});

test('status interval is clamped to at least 15 s by validation', () => {
  assert.throws(() => loadConfig({ ...base, REALM_STATUS_INTERVAL_SECONDS: '5' }), /REALM_STATUS_INTERVAL_SECONDS/);
  assert.equal(loadConfig({ ...base, REALM_STATUS_INTERVAL_SECONDS: '15' }).status.intervalSec, 15);
});

test('.env file is read, BOM tolerated, and real environment variables win', () => {
  const dir = tmpDir();
  const p = join(dir, '.env');
  writeFileSync(p, '﻿# comment\nREALM_NAME=From File\nREALM_JOIN_PORT=7351\nDISCORD_TOKEN="quoted value"\n');
  const fileEnv = readEnvFile(p);
  assert.equal(fileEnv.REALM_NAME, 'From File');
  assert.equal(fileEnv.DISCORD_TOKEN, 'quoted value');
  const merged = mergeEnv(fileEnv, { REALM_JOIN_PORT: '7400', REALM_NAME: '' });
  assert.equal(merged.REALM_JOIN_PORT, '7400');
  assert.equal(merged.REALM_NAME, 'From File', 'an empty real variable does not blank the file value');
  assert.deepEqual(readEnvFile(join(dir, 'nope.env')), {});
});

test('describeConfig never prints the token', () => {
  const text = describeConfig(loadConfig(base));
  assert.ok(!text.includes(TOKEN));
  assert.match(text, /set, 70 chars/);
});
