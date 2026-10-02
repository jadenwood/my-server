// Routes /realm interactions (and house-name autocomplete) to the views. Works on the duck-typed
// interaction API of discord.js v14, so tests drive it with plain objects.

import { ChronicleUnavailable } from './chronicle-client.js';
import { CHRONICLE_MAX } from './commands.js';
import { SwearRefused } from './swear.js';
import { COLORS, clampEmbed, dyeFor, esc, houseLabel, plain, sameHouse } from './text.js';
import {
  chronicleEmbed, eventsEmbed, housesEmbed, joinReply, kingEmbed, statusEmbed, unavailableEmbed, whoisEmbed,
} from './views.js';

export const EPHEMERAL = 64; // MessageFlags.Ephemeral
export const NO_MENTIONS = Object.freeze({ parse: [] });

// Subcommands whose answer only matters to the person asking.
const PRIVATE = new Set(['join', 'swear', 'forswear']);

// Discord API error codes worth a friendly message.
const DISCORD_ERRORS = {
  50013: 'The bot is missing a permission it needs (Manage Roles for house roles). Ask an admin.',
  50001: 'The bot cannot see that channel or server. Ask an admin to check its access.',
  10011: 'That role was just deleted. Try again.',
};

export function createHandlers({ cfg, chronicle, realmData, swearService, logger = console, now = Date.now }) {
  async function safeRealmEvents() {
    try {
      return await realmData.realmEvents();
    } catch (e) {
      logger.warn(`[realm] event files: ${e.message}`);
      return null;
    }
  }

  const routes = {
    async status() {
      const [state, events, realmEvents] = await Promise.all([chronicle.state(), chronicle.events(10), safeRealmEvents()]);
      return { embeds: [statusEmbed({ cfg, state, events, realmEvents, now: now() })] };
    },
    async king() {
      const [state, events] = await Promise.all([chronicle.state(), chronicle.events(200)]);
      return { embeds: [kingEmbed({ cfg, state, events, now: now() })] };
    },
    async houses() {
      return { embeds: [housesEmbed({ cfg, state: await chronicle.state() })] };
    },
    async chronicle(i) {
      const count = Math.min(CHRONICLE_MAX, Math.max(1, i.options.getInteger('count') ?? 5));
      return { embeds: [chronicleEmbed({ cfg, events: await chronicle.events(count), count })] };
    },
    async events() {
      const realmEvents = (await safeRealmEvents()) || { active: [], upcoming: [], known: false, error: 'unreadable' };
      let events = [];
      try {
        events = await chronicle.events(100);
      } catch (e) {
        if (!(e instanceof ChronicleUnavailable)) throw e;
      }
      return { embeds: [eventsEmbed({ cfg, realmEvents, events })] };
    },
    async join() {
      return joinReply(cfg);
    },
    async whois(i) {
      const input = i.options.getString('house', true);
      const state = await chronicle.state();
      const house = state.houses.find((h) => sameHouse(h.name, input));
      if (!house) {
        const close = state.houses.filter((h) => h.name.toLowerCase().includes(input.replace(/^house\s+/i, '').trim().toLowerCase())).slice(0, 5);
        return {
          embeds: [clampEmbed({
            title: 'No such house',
            description: `The Chronicle knows no house called **${esc(input, 64)}**.${close.length ? ` Did you mean ${close.map((h) => `**${esc(h.name, 64)}**`).join(', ')}?` : ''}`,
            color: COLORS.iron,
          })],
          flags: EPHEMERAL,
        };
      }
      const [{ houses, treaties }, events] = await Promise.all([
        realmData.houses().catch(() => ({ houses: [], treaties: [] })),
        chronicle.events(300),
      ]);
      const detail = houses.find((h) => sameHouse(h.name, house.name)) || null;
      return { embeds: [whoisEmbed({ cfg, state, detail, treaties, events, house, now: now() })] };
    },
    async swear(i) {
      const input = i.options.getString('house', true);
      const state = await chronicle.state();
      const r = await swearService.swear(i, input, state);
      const name = esc(houseLabel(r.house.name), 70);
      const text = r.changed
        ? `You have bent the knee to **${name}** and now carry the role **${esc(r.roleName, 100)}**.${r.removed?.length ? ` Your old oath (${r.removed.map((x) => esc(x, 100)).join(', ')}) is set aside.` : ''}`
        : `You are already sworn to **${name}**.`;
      return {
        embeds: [clampEmbed({
          title: '🤝 Oath taken',
          description: `${text}\n\nThis role is self-declared on Discord. It is not linked to your game account and does not change anything in game. To join the house in game, ask its leader for \`/house invite\`.`,
          color: dyeFor(r.house.name),
        })],
      };
    },
    async forswear(i) {
      const r = await swearService.forswear(i);
      return {
        embeds: [clampEmbed({
          title: r.removed.length ? '⛓️ Oath set aside' : 'No oath to set aside',
          description: r.removed.length ? `Removed: ${r.removed.map((x) => `**${esc(x, 100)}**`).join(', ')}.` : 'You carry no "Sworn to ..." role.',
          color: COLORS.iron,
        })],
      };
    },
  };

  async function onCommand(interaction) {
    const sub = interaction.options.getSubcommand(false);
    const route = routes[sub];
    if (!route) {
      return interaction.reply({ content: 'Unknown command. Try `/realm status`.', flags: EPHEMERAL, allowedMentions: NO_MENTIONS });
    }
    const ephemeral = PRIVATE.has(sub);
    await interaction.deferReply(ephemeral ? { flags: EPHEMERAL } : {});
    let payload;
    try {
      payload = await route(interaction);
    } catch (e) {
      payload = errorPayload(e, sub);
    }
    // Ephemeral-ness is fixed by deferReply; editReply ignores flags.
    const { flags, ...rest } = payload;
    return interaction.editReply({ ...rest, allowedMentions: NO_MENTIONS });
  }

  function errorPayload(e, sub) {
    if (e instanceof ChronicleUnavailable) {
      logger.warn(`[realm] /realm ${sub}: ${e.message}`);
      return { embeds: [unavailableEmbed(cfg)] };
    }
    if (e instanceof SwearRefused) return { embeds: [clampEmbed({ title: 'Not done', description: esc(e.message, 600), color: COLORS.iron })] };
    if (e && DISCORD_ERRORS[e.code]) {
      logger.warn(`[realm] /realm ${sub}: Discord error ${e.code} ${e.message}`);
      return { embeds: [clampEmbed({ title: 'Not done', description: DISCORD_ERRORS[e.code], color: COLORS.iron })] };
    }
    logger.error(`[realm] /realm ${sub} failed:`, e);
    return { embeds: [clampEmbed({ title: 'Something went wrong', description: 'The bot hit an error. The owner can find details in its console.', color: COLORS.blood })] };
  }

  async function onAutocomplete(interaction) {
    const focused = interaction.options.getFocused(true);
    let choices = [];
    if (focused?.name === 'house') {
      try {
        const state = await chronicle.state();
        const q = String(focused.value || '').replace(/^house\s+/i, '').trim().toLowerCase();
        choices = state.houses
          .filter((h) => !q || h.name.toLowerCase().includes(q))
          .sort((a, b) => (a.name.toLowerCase().startsWith(q) ? 0 : 1) - (b.name.toLowerCase().startsWith(q) ? 0 : 1) || b.members - a.members)
          .slice(0, 25)
          .map((h) => ({ name: plain(`${houseLabel(h.name)}${h.sigil ? ` (${h.sigil})` : ''}`, 100) || 'House', value: h.name.slice(0, 100) }));
      } catch {
        choices = [];
      }
    }
    return interaction.respond(choices);
  }

  async function handle(interaction) {
    try {
      if (interaction.isAutocomplete?.()) {
        if (interaction.commandName === 'realm') await onAutocomplete(interaction);
        return;
      }
      if (interaction.isChatInputCommand?.() && interaction.commandName === 'realm') await onCommand(interaction);
    } catch (e) {
      // Interaction expired (10062) or already answered (40060): nothing left to tell the user.
      logger.error(`[realm] interaction failed: ${e?.code ?? ''} ${e?.message ?? e}`);
    }
  }

  return { handle, routes };
}
