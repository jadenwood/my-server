'use strict';

// The other files the game creates in Configuration/ on its first start (CoreServer.Start loads them
// before it checks for the first run). Names: CodeHatch.Build.GameInfo.ConfigSettings [DEC].
// Content:
//   BannedPlayers.cfg, Whitelist.cfg, ChatMutes.cfg, VoiceMutes.cfg, Users.cfg: rebuilt from their
//     Load() methods [DEC] (UserBans, UserWhitelist, PlayerMuteManager, UserRegistrar.SetupConfig).
//   Permissions.cfg, DeathMessages.cfg: the real files hold the game's default ranks and death
//     messages, which are long and were NOT reproduced. The simulator writes a short stand-in that
//     says so. UNVERIFIED: their real content.
// Each file is only written when it does not exist, like PropertyFile.Load.

const fs = require('fs');
const path = require('path');

const NL = '\r\n';

const TEMPLATES = {
  'BannedPlayers.cfg': ['# Banned Players'],
  'Whitelist.cfg': ['# Whitelisted Players', "enabled = 'False'   # Anonymous players cannot connect while this is true."],
  'ChatMutes.cfg': ['# -- Muted Players --'],
  'VoiceMutes.cfg': ['# -- Muted Players --'],
  'Users.cfg': ['blacklist {', "    - 'admin'", "    - 'server'", "    - 'administrator'", '}'],
  'Permissions.cfg': ['# rok-sim stand-in: the real file lists the default ranks and permissions (not reproduced).'],
  'DeathMessages.cfg': ["defaultMessage = '%Victim% has died.'", '# rok-sim stand-in: the real file lists every death message (not reproduced).']
};

// Order in CoreServer.Start: Users, Permissions, DeathMessages, BannedPlayers, Whitelist, ChatMutes, VoiceMutes.
const LOAD_ORDER = ['Users.cfg', 'Permissions.cfg', 'DeathMessages.cfg', 'BannedPlayers.cfg', 'Whitelist.cfg', 'ChatMutes.cfg', 'VoiceMutes.cfg'];

// Log lines the loaders write on success [DEC].
const LOADED_LINES = {
  'Users.cfg': 'User registration loaded.',
  'BannedPlayers.cfg': 'User bans loaded.',
  'Whitelist.cfg': 'User whitelist loaded.'
};

function ensureConfigFiles(configDir) {
  const created = [];
  fs.mkdirSync(configDir, { recursive: true });
  for (const name of LOAD_ORDER) {
    const p = path.join(configDir, name);
    if (fs.existsSync(p)) continue;
    fs.writeFileSync(p, TEMPLATES[name].join(NL) + NL);
    created.push(name);
  }
  return created;
}

// Whitelist.cfg "enabled" switch, as the game reads it (case-insensitive key, quoted value).
function readWhitelistEnabled(configDir) {
  try {
    const t = fs.readFileSync(path.join(configDir, 'Whitelist.cfg'), 'utf8');
    const m = /^\s*enabled\s*=\s*'?\s*(true|false)/im.exec(t);
    return m ? /true/i.test(m[1]) : false;
  } catch {
    return false;
  }
}

module.exports = { ensureConfigFiles, readWhitelistEnabled, LOAD_ORDER, LOADED_LINES, TEMPLATES };
