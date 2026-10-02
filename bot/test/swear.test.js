import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { cleanState } from '../src/chronicle-client.js';
import { createStateStore } from '../src/state-store.js';
import { ROLE_PREFIX, SwearRefused, createSwearService, roleNameFor } from '../src/swear.js';
import { SAMPLE_STATE, discordError, fakeGuild, fakeInteraction, fakeMember, fakeRole, quietLogger, testConfig } from './helpers.js';

const STATE = cleanState(SAMPLE_STATE);

function setup({ env = {}, t = { now: 1_000_000 } } = {}) {
  const cfg = testConfig({}, { REALM_SWEAR_COOLDOWN_MINUTES: '60', ...env });
  const store = createStateStore(cfg.stateFile, { logger: quietLogger() });
  const svc = createSwearService({ cfg, store, now: () => t.now, logger: quietLogger() });
  return { cfg, store, svc, t };
}

test('role names: "Sworn to <House>", leading "House" and @ removed', () => {
  assert.equal(roleNameFor('Varrow'), 'Sworn to Varrow');
  assert.equal(roleNameFor('House Varrow'), 'Sworn to Varrow');
  assert.equal(roleNameFor('@everyone'), 'Sworn to everyone');
});

test('first oath creates a permission-less, unhoisted, unmentionable role in the house colour', async () => {
  const { svc, store } = setup();
  const guild = fakeGuild();
  const member = fakeMember();
  const r = await svc.swear(fakeInteraction('swear', {}, { guild, member }), 'varrow', STATE);
  assert.equal(r.changed, true);
  assert.equal(guild.created.length, 1);
  const opts = guild.created[0].createOptions;
  assert.equal(opts.name, 'Sworn to Varrow');
  assert.deepEqual(opts.permissions, []);
  assert.equal(opts.hoist, false);
  assert.equal(opts.mentionable, false);
  assert.equal(opts.color, 0x4a2347);
  assert.deepEqual(member.log.map((x) => x.slice(0, 2)), [['add', 'Sworn to Varrow']]);
  await store.save();
  const saved = JSON.parse(readFileSync(store.path, 'utf8'));
  assert.equal(saved.oaths['444444444444444444'].house, 'Varrow');
});

test('swearing to another house swaps roles and reuses existing ones', async () => {
  const { svc, t } = setup({ env: { REALM_SWEAR_COOLDOWN_MINUTES: '0' } });
  const varrow = fakeRole('Sworn to Varrow');
  const corvane = fakeRole('Sworn to Corvane');
  const unrelated = fakeRole('Moderator', { permissions: 8n });
  const guild = fakeGuild({ roles: [varrow, corvane, unrelated] });
  const member = fakeMember([varrow, unrelated]);
  const r = await svc.swear(fakeInteraction('swear', {}, { guild, member }), 'Corvane', STATE);
  assert.deepEqual(r.removed, ['Sworn to Varrow']);
  assert.equal(guild.created.length, 0);
  assert.ok(member.roles.cache.has(corvane.id));
  assert.ok(!member.roles.cache.has(varrow.id));
  assert.ok(member.roles.cache.has(unrelated.id), 'other roles are never touched');
  t.now += 1;
  const again = await svc.swear(fakeInteraction('swear', {}, { guild, member }), 'corvane', STATE);
  assert.equal(again.changed, false);
});

test('cooldown between oaths; forswearing does not reset it', async () => {
  const { svc, t } = setup();
  const guild = fakeGuild();
  const member = fakeMember();
  await svc.swear(fakeInteraction('swear', {}, { guild, member }), 'Varrow', STATE);
  await svc.forswear(fakeInteraction('forswear', {}, { guild, member }));
  t.now += 30 * 60000;
  await assert.rejects(svc.swear(fakeInteraction('swear', {}, { guild, member }), 'Corvane', STATE), /swear again in 30 min/);
  t.now += 31 * 60000;
  const r = await svc.swear(fakeInteraction('swear', {}, { guild, member }), 'Corvane', STATE);
  assert.equal(r.changed, true);
});

test('only houses that exist in the Chronicle can be sworn to', async () => {
  const { svc } = setup();
  const guild = fakeGuild();
  await assert.rejects(svc.swear(fakeInteraction('swear', {}, { guild }), 'Admins', STATE), (e) => e instanceof SwearRefused && /no house called "Admins"/.test(e.message) && /Varrow/.test(e.message));
  assert.equal(guild.created.length, 0);
});

test('a "Sworn to" role that carries permissions is never handed out', async () => {
  const { svc } = setup();
  const guild = fakeGuild({ roles: [fakeRole('Sworn to Varrow', { permissions: 8n })] });
  const member = fakeMember();
  await assert.rejects(svc.swear(fakeInteraction('swear', {}, { guild, member }), 'Varrow', STATE), /has permissions attached/);
  assert.equal(member.log.length, 0);
});

test('role above the bot, integration-managed role, and role creation off', async () => {
  const { svc } = setup();
  const high = fakeGuild({ roles: [fakeRole('Sworn to Varrow', { editable: false })] });
  await assert.rejects(svc.swear(fakeInteraction('swear', {}, { guild: high }), 'Varrow', STATE), /move the bot's role above/);
  const managed = fakeGuild({ roles: [fakeRole('Sworn to Varrow', { managed: true })] });
  await assert.rejects(svc.swear(fakeInteraction('swear', {}, { guild: managed }), 'Varrow', STATE), /integration/);
  const { svc: noCreate } = setup({ env: { REALM_SWEAR_CREATE_ROLES: 'false' } });
  await assert.rejects(noCreate.swear(fakeInteraction('swear', {}, { guild: fakeGuild() }), 'Varrow', STATE), /no "Sworn to Varrow" role yet/);
});

test('the bot creates at most REALM_SWEAR_MAX_ROLES roles', async () => {
  const { svc } = setup({ env: { REALM_SWEAR_MAX_ROLES: '2' } });
  const guild = fakeGuild({ roles: [fakeRole(`${ROLE_PREFIX}A`), fakeRole(`${ROLE_PREFIX}B`)] });
  await assert.rejects(svc.swear(fakeInteraction('swear', {}, { guild }), 'Varrow', STATE), /already has 2/);
});

test('missing Manage Roles surfaces as the Discord error for the handler to explain', async () => {
  const { svc } = setup();
  const guild = fakeGuild({ failCreate: discordError(50013, 'Missing Permissions') });
  await assert.rejects(svc.swear(fakeInteraction('swear', {}, { guild }), 'Varrow', STATE), (e) => e.code === 50013);
});

test('disabled, and no guild in cache', async () => {
  const { svc } = setup({ env: { REALM_SWEAR_ENABLED: 'false' } });
  await assert.rejects(svc.swear(fakeInteraction('swear'), 'Varrow', STATE), /switched off/);
  await assert.rejects(svc.forswear(fakeInteraction('forswear')), /switched off/);
  const { svc: on } = setup();
  await assert.rejects(on.swear(fakeInteraction('swear', {}, { guild: null }), 'Varrow', STATE), /re-invite/);
});

test('a raw API member (guild not cached) is fetched as a full GuildMember', async () => {
  const { svc } = setup();
  const guild = fakeGuild();
  const real = fakeMember();
  guild.members.fetch = async (id) => {
    assert.equal(id, '444444444444444444');
    return real;
  };
  await svc.swear(fakeInteraction('swear', {}, { guild, member: { roles: ['123'] } }), 'Varrow', STATE);
  assert.equal(real.log.length, 1);
});

test('double-clicks: a second oath while the first is in flight is refused', async () => {
  const { svc } = setup({ env: { REALM_SWEAR_COOLDOWN_MINUTES: '0' } });
  const guild = fakeGuild();
  let release;
  const create = guild.roles.create;
  guild.roles.create = (o) => new Promise((r) => { release = () => r(create(o)); });
  const member = fakeMember();
  const first = svc.swear(fakeInteraction('swear', {}, { guild, member }), 'Varrow', STATE);
  await new Promise((r) => setImmediate(r));
  await assert.rejects(svc.swear(fakeInteraction('swear', {}, { guild, member }), 'Corvane', STATE), /still being written/);
  release();
  await first;
});

test('forswear removes only "Sworn to" roles the bot can manage', async () => {
  const { svc } = setup();
  const a = fakeRole('Sworn to Varrow');
  const locked = fakeRole('Sworn to Corvane', { editable: false });
  const mod = fakeRole('Moderator');
  const member = fakeMember([a, locked, mod]);
  const r = await svc.forswear(fakeInteraction('forswear', {}, { member }));
  assert.deepEqual(r.removed, ['Sworn to Varrow']);
  assert.ok(member.roles.cache.has(mod.id) && member.roles.cache.has(locked.id));
  const none = await svc.forswear(fakeInteraction('forswear', {}, { member: fakeMember() }));
  assert.deepEqual(none.removed, []);
});

test('swear and forswear never take away a "Sworn to ..." role that carries permissions', async () => {
  const { svc } = setup({ env: { REALM_SWEAR_COOLDOWN_MINUTES: '0' } });
  const varrow = fakeRole('Sworn to Varrow');
  const privileged = fakeRole('Sworn to the Crown', { permissions: 8n });
  const integration = fakeRole('Sworn to Bots', { managed: true });
  const guild = fakeGuild({ roles: [varrow, privileged, integration] });
  const member = fakeMember([varrow, privileged, integration]);
  const r = await svc.swear(fakeInteraction('swear', {}, { guild, member }), 'Corvane', STATE);
  assert.deepEqual(r.removed, ['Sworn to Varrow']);
  assert.ok(member.roles.cache.has(privileged.id), 'permissioned role kept on swear');
  assert.ok(member.roles.cache.has(integration.id), 'managed role kept on swear');
  const f = await svc.forswear(fakeInteraction('forswear', {}, { guild, member }));
  assert.deepEqual(f.removed, ['Sworn to Corvane']);
  assert.ok(member.roles.cache.has(privileged.id), 'permissioned role kept on forswear');
  assert.ok(member.roles.cache.has(integration.id), 'managed role kept on forswear');
});
