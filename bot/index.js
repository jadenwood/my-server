// Realm Discord bot entry point.
//   node index.js           run the bot
//   node index.js --check   check the configuration and the Chronicle service, then exit (no Discord login)

import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createChronicleClient } from './src/chronicle-client.js';
import { ConfigError, describeConfig, loadConfig, mergeEnv, readEnvFile } from './src/config.js';
import { createRealmData } from './src/realm-data.js';
import { loadChronicleLabels, loadExtraTypes } from './src/text.js';

const HERE = dirname(fileURLToPath(import.meta.url));

async function main() {
  const check = process.argv.includes('--check');
  const env = mergeEnv(readEnvFile(join(HERE, '.env')), process.env);
  let cfg;
  try {
    cfg = loadConfig(env, { baseDir: HERE, requireDiscord: !check });
  } catch (e) {
    if (e instanceof ConfigError) {
      console.error(e.message);
      process.exit(2);
    }
    throw e;
  }
  for (const w of cfg.warnings) console.warn(`[config] ${w}`);
  // Event labels: the Chronicle pages' list (registered types) and plugins/docs/*/EVENTS.json (pending ones).
  const registered = loadChronicleLabels(resolve(HERE, '..', 'chronicle', 'public', 'assets', 'common.js'));
  const pending = loadExtraTypes(resolve(HERE, '..', 'plugins', 'docs'));
  console.log(`[bot] event labels: ${registered} from the Chronicle pages, ${pending} pending from plugins/docs`);

  if (check) {
    console.log(describeConfig(cfg));
    const chronicle = createChronicleClient({ baseUrl: cfg.chronicleUrl });
    try {
      const h = await chronicle.health();
      const s = await chronicle.state();
      console.log(`[check] Chronicle service OK: events ${h.events}, state ${h.state}; king: ${s.king ?? '(none)'}; ${s.houses.length} houses; stale: ${s.stale}`);
    } catch (e) {
      console.log(`[check] Chronicle service NOT reachable: ${e.message}`);
    }
    const data = createRealmData({ dataDir: cfg.dataDir, configDir: cfg.configDir });
    const h = await data.houses();
    const ev = await data.realmEvents();
    console.log(`[check] RealmHouses.json: ${h.error ?? `${h.houses.length} houses`}; RealmEvents: ${ev.known ? `${ev.active.length} running, ${ev.upcoming.length} upcoming` : 'not found'}`);
    return;
  }

  const discord = await import('discord.js');
  const { createRealmBot } = await import('./src/bot.js');
  const bot = createRealmBot({ discord, cfg });
  const shutdown = async (sig) => {
    console.log(`[bot] ${sig}: shutting down`);
    try {
      await bot.stop();
    } finally {
      process.exit(0);
    }
  };
  process.on('SIGINT', () => shutdown('SIGINT'));
  process.on('SIGTERM', () => shutdown('SIGTERM'));
  try {
    await bot.start();
  } catch (e) {
    // discord.js reports a bad token as TokenInvalid; never print the token itself.
    console.error(`[bot] could not sign in to Discord: ${e.code ?? ''} ${e.message}`);
    process.exit(1);
  }
}

main().catch((e) => {
  console.error('[bot] fatal:', e);
  process.exit(1);
});
