// Wires the pieces to a discord.js Client. The discord.js module is passed in, so tests use a fake.
// Gateway intents: Guilds only (no privileged intents, no message content).

import { createChronicleClient } from './chronicle-client.js';
import { commandDefinitions } from './commands.js';
import { createHandlers, NO_MENTIONS } from './handlers.js';
import { createRealmData } from './realm-data.js';
import { createStateStore } from './state-store.js';
import { createStatusBoard } from './status-board.js';
import { createSwearService } from './swear.js';

export function createRealmBot({ discord, cfg, logger = console, fetchImpl, now = Date.now, timers = globalThis, chronicle, realmData, store }) {
  const { Client, GatewayIntentBits, Events } = discord;
  chronicle ??= createChronicleClient({ baseUrl: cfg.chronicleUrl, fetchImpl, now });
  realmData ??= createRealmData({ dataDir: cfg.dataDir, configDir: cfg.configDir, now });
  store ??= createStateStore(cfg.stateFile, { logger });

  const client = new Client({ intents: [GatewayIntentBits.Guilds], allowedMentions: NO_MENTIONS });
  const swearService = createSwearService({ cfg, store, now, logger });
  const handlers = createHandlers({ cfg, chronicle, realmData, swearService, logger, now });
  const board = createStatusBoard({ client, cfg, chronicle, realmData, store, logger, now, timers });

  async function registerCommands() {
    if (cfg.register === 'off') return 'off';
    const defs = commandDefinitions({ swearEnabled: cfg.swear.enabled });
    if (cfg.register === 'guild') {
      await client.application.commands.set(defs, cfg.guildId);
      return 'guild';
    }
    await client.application.commands.set(defs);
    return 'global';
  }

  client.on(Events.InteractionCreate, (interaction) => handlers.handle(interaction));
  client.once(Events.ClientReady, async (c) => {
    logger.log(`[bot] signed in as ${c.user?.tag ?? 'bot'}; serving ${c.guilds?.cache?.size ?? '?'} server(s)`);
    if (cfg.guildId && c.guilds?.cache && !c.guilds.cache.has(cfg.guildId)) {
      logger.warn('[bot] the bot is not in DISCORD_GUILD_ID yet; open the invite link from the README');
    }
    try {
      const where = await registerCommands();
      if (where !== 'off') logger.log(`[bot] /realm registered (${where})`);
    } catch (e) {
      logger.error(`[bot] could not register /realm (${e.code ?? ''} ${e.message}). Was the bot invited with the applications.commands scope?`);
    }
    if (await board.start()) logger.log(`[bot] status message: channel ${cfg.status.channelId}, every ${cfg.status.intervalSec}s`);
  });
  client.on(Events.Error ?? 'error', (e) => logger.error(`[bot] client error: ${e.message}`));
  client.on(Events.Warn ?? 'warn', (m) => logger.warn(`[bot] ${m}`));

  return {
    client,
    handlers,
    board,
    store,
    async start() {
      await store.load();
      await client.login(cfg.token);
    },
    async stop() {
      board.stop();
      await store.save();
      await client.destroy();
    },
  };
}
