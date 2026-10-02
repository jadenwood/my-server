// Self-service house roles: /realm swear <house> gives the member the role "Sworn to <House>" and takes
// away any other "Sworn to ..." role; /realm forswear takes it away.
//
// This is a Discord badge only. It is NOT linked to the member's game account and proves nothing about
// which house they are in game (no verified account link exists). The replies say so.
//
// Safety rules:
// - Only houses that exist right now (Chronicle /api/state) can be sworn to, so members cannot create
//   arbitrary roles.
// - The bot only touches roles whose name starts with "Sworn to " AND that carry no permissions at all.
//   If an admin gives such a role a permission, the bot refuses to hand it out.
// - Roles it creates have no permissions, are not hoisted and cannot be mentioned.
// - A cooldown stops members flipping between houses; at most REALM_SWEAR_MAX_ROLES roles are created.

import { dyeFor, houseLabel, plain, sameHouse } from './text.js';

export const ROLE_PREFIX = 'Sworn to ';

export const roleNameFor = (house) => `${ROLE_PREFIX}${plain(house, 64).replace(/^house\s+/i, '')}`.slice(0, 100);

const isOathRole = (role) => typeof role?.name === 'string' && role.name.startsWith(ROLE_PREFIX);
const hasNoPermissions = (role) => {
  const bits = role?.permissions?.bitfield;
  return bits === 0n || bits === 0 || bits === '0';
};
// The only roles the bot will ever add or take away: "Sworn to ..." roles with no permissions that no
// integration manages. A "Sworn to ..." role an admin gave permissions to is left alone on every path.
const isBotOathRole = (role) => isOathRole(role) && hasNoPermissions(role) && !role.managed;

const values = (collection) => (collection ? [...(typeof collection.values === 'function' ? collection.values() : Object.values(collection))] : []);

export class SwearRefused extends Error {}

export function createSwearService({ cfg, store, now = Date.now, logger = console }) {
  const busy = new Set();

  async function memberFor(interaction) {
    const guild = interaction.guild;
    if (!guild) throw new SwearRefused('The bot is not fully in this server. Ask an admin to re-invite it with the "bot" scope.');
    let member = interaction.member;
    if (!member?.roles || typeof member.roles.add !== 'function') member = await guild.members.fetch(interaction.user.id);
    return { guild, member };
  }

  function cooldownLeft(userId) {
    const rec = store.data.oaths[userId];
    if (!rec?.at || !cfg.swear.cooldownMin) return 0;
    return Math.max(0, rec.at + cfg.swear.cooldownMin * 60000 - now());
  }

  async function ensureRole(guild, house) {
    const name = roleNameFor(house.name);
    const existing = values(guild.roles.cache).find((r) => r.name.toLowerCase() === name.toLowerCase());
    if (existing) {
      if (!hasNoPermissions(existing)) {
        throw new SwearRefused(`The role "${name}" has permissions attached, so the bot will not hand it out. An admin should remove its permissions.`);
      }
      if (existing.managed) throw new SwearRefused(`The role "${name}" belongs to an integration and cannot be given.`);
      if (!existing.editable) throw new SwearRefused(`The bot's role is below "${name}". An admin should move the bot's role above the "Sworn to ..." roles.`);
      return existing;
    }
    if (!cfg.swear.createRoles) throw new SwearRefused(`There is no "${name}" role yet. Ask an admin to create it (with no permissions).`);
    const count = values(guild.roles.cache).filter(isOathRole).length;
    if (count >= cfg.swear.maxRoles) throw new SwearRefused(`The server already has ${count} "Sworn to ..." roles, the most the bot will create. Ask an admin.`);
    const role = await guild.roles.create({
      name,
      color: dyeFor(house.name),
      hoist: false,
      mentionable: false,
      permissions: [],
      reason: `Realm bot: first oath to ${houseLabel(plain(house.name, 64))}`,
    });
    logger.log(`[swear] created role "${name}"`);
    return role;
  }

  async function swear(interaction, houseInput, state) {
    if (!cfg.swear.enabled) throw new SwearRefused('House roles are switched off on this server.');
    const house = state.houses.find((h) => sameHouse(h.name, houseInput));
    if (!house) {
      const names = state.houses.map((h) => h.name).slice(0, 20).join(', ');
      throw new SwearRefused(`There is no house called "${plain(houseInput, 64)}" in the realm.${names ? ` Houses: ${names}.` : ' No house has been founded yet.'}`);
    }
    const userId = interaction.user.id;
    if (busy.has(userId)) throw new SwearRefused('Your last oath is still being written. Try again in a moment.');
    busy.add(userId);
    try {
      const { guild, member } = await memberFor(interaction);
      const target = roleNameFor(house.name);
      const current = values(member.roles.cache).filter(isBotOathRole);
      if (current.length === 1 && current[0].name.toLowerCase() === target.toLowerCase()) {
        return { changed: false, house, roleName: current[0].name };
      }
      const left = cooldownLeft(userId);
      if (left > 0) throw new SwearRefused(`You swore an oath recently. You may swear again in ${Math.ceil(left / 60000)} min.`);
      const role = await ensureRole(guild, house);
      for (const r of current) {
        if (r.id !== role.id && r.editable !== false) await member.roles.remove(r, `Realm bot: ${interaction.user.id} swore to another house`);
      }
      await member.roles.add(role, `Realm bot: self-declared oath to ${houseLabel(plain(house.name, 64))}`);
      store.data.oaths[userId] = { house: house.name, at: now() };
      await store.save();
      return { changed: true, house, roleName: role.name, removed: current.filter((r) => r.id !== role.id).map((r) => r.name) };
    } finally {
      busy.delete(userId);
    }
  }

  async function forswear(interaction) {
    if (!cfg.swear.enabled) throw new SwearRefused('House roles are switched off on this server.');
    const { member } = await memberFor(interaction);
    const current = values(member.roles.cache).filter(isBotOathRole);
    if (!current.length) return { removed: [] };
    const removed = [];
    for (const r of current) {
      if (r.editable === false) continue;
      await member.roles.remove(r, `Realm bot: ${interaction.user.id} forswore`);
      removed.push(r.name);
    }
    // Keep the timestamp so forswearing does not reset the cooldown.
    if (store.data.oaths[interaction.user.id]) {
      store.data.oaths[interaction.user.id].house = null;
      await store.save();
    }
    return { removed };
  }

  return { swear, forswear, cooldownLeft };
}
