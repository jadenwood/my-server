// Registers (or prints) the /realm slash command without starting the bot.
//   node scripts/register-commands.js            register to DISCORD_GUILD_ID (or globally if it is empty)
//   node scripts/register-commands.js --global   register globally (can take up to an hour to show)
//   node scripts/register-commands.js --clear    remove the bot's commands from the guild (or globally with --global)
//   node scripts/register-commands.js --print    print the JSON and exit (no token needed, no network)

import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { commandDefinitions, validateDefinitions } from '../src/commands.js';
import { ConfigError, loadConfig, mergeEnv, readEnvFile } from '../src/config.js';

const HERE = dirname(fileURLToPath(import.meta.url));
const BOT_DIR = join(HERE, '..');
const args = new Set(process.argv.slice(2));

const env = mergeEnv(readEnvFile(join(BOT_DIR, '.env')), process.env);
let cfg;
try {
  cfg = loadConfig(env, { baseDir: BOT_DIR, requireDiscord: !args.has('--print') });
} catch (e) {
  console.error(e instanceof ConfigError ? e.message : e);
  process.exit(2);
}

const defs = args.has('--clear') ? [] : commandDefinitions({ swearEnabled: cfg.swear.enabled });
const problems = validateDefinitions(defs);
if (problems.length) {
  console.error(`Command definitions are not valid:\n  ${problems.join('\n  ')}`);
  process.exit(1);
}
if (args.has('--print')) {
  console.log(JSON.stringify(defs, null, 2));
  process.exit(0);
}

const { REST, Routes } = await import('discord.js');
const rest = new REST({ version: '10' }).setToken(cfg.token);
const global = args.has('--global') || !cfg.guildId;
const route = global ? Routes.applicationCommands(cfg.applicationId) : Routes.applicationGuildCommands(cfg.applicationId, cfg.guildId);
try {
  const out = await rest.put(route, { body: defs });
  console.log(`${args.has('--clear') ? 'Cleared' : 'Registered'} ${Array.isArray(out) ? out.length : 0} command(s) ${global ? 'globally' : `in server ${cfg.guildId}`}.`);
} catch (e) {
  console.error(`Discord refused the commands: ${e.status ?? ''} ${e.code ?? ''} ${e.message}`);
  process.exit(1);
}
