// Slash command definitions as raw Discord API JSON (application command type 1, CHAT_INPUT).
// One top-level command, /realm, with a subcommand each. Registered by scripts/register-commands.js or
// on start (REALM_REGISTER_COMMANDS).

const SUB = 1; // ApplicationCommandOptionType.Subcommand
const STRING = 3;
const INTEGER = 4;

export const CHRONICLE_MAX = 15;

export function commandDefinitions({ swearEnabled = true } = {}) {
  const houseOption = (description) => ({ type: STRING, name: 'house', description, required: true, autocomplete: true, min_length: 1, max_length: 64 });
  const options = [
    { type: SUB, name: 'status', description: 'Who holds the crown, who is online, and what is happening in the realm' },
    { type: SUB, name: 'king', description: 'The monarch on the Old Throne, the ruling house and recent matters of the crown' },
    { type: SUB, name: 'houses', description: 'Every house of the realm, with vassals listed under their liege' },
    {
      type: SUB,
      name: 'chronicle',
      description: 'The latest entries in the Chronicle of Ostreval',
      options: [{ type: INTEGER, name: 'count', description: `How many entries (1 to ${CHRONICLE_MAX}, default 5)`, required: false, min_value: 1, max_value: CHRONICLE_MAX }],
    },
    { type: SUB, name: 'events', description: 'Realm events running now and coming up (Crown Night, tournament, hunt, truce)' },
    { type: SUB, name: 'join', description: 'How to join: server address, port and the Realm player app' },
    { type: SUB, name: 'whois', description: 'Everything known about one house', options: [houseOption('The house name')] },
  ];
  if (swearEnabled) {
    options.push({ type: SUB, name: 'swear', description: 'Take the Discord role "Sworn to <House>" (self-declared, not linked to the game)', options: [houseOption('The house to swear to')] });
    options.push({ type: SUB, name: 'forswear', description: 'Give up your "Sworn to ..." Discord role' });
  }
  return [
    {
      type: 1,
      name: 'realm',
      description: 'The Realm of Ostreval: crown, houses, chronicle and how to join',
      contexts: [0], // guild only
      integration_types: [0], // installed to a server
      options,
    },
  ];
}

// Checks the definitions against Discord's documented limits (also used by the tests).
export function validateDefinitions(defs) {
  const problems = [];
  const NAME = /^[-_\p{Ll}\p{Lo}\p{N}]{1,32}$/u;
  const check = (o, path) => {
    if (!NAME.test(o.name)) problems.push(`${path}: bad name "${o.name}"`);
    if (typeof o.description !== 'string' || o.description.length < 1 || o.description.length > 100) problems.push(`${path}: description must be 1-100 characters`);
    if (o.options) {
      if (o.options.length > 25) problems.push(`${path}: more than 25 options`);
      const names = new Set();
      let seenOptional = false;
      for (const c of o.options) {
        if (names.has(c.name)) problems.push(`${path}: duplicate option ${c.name}`);
        names.add(c.name);
        if (c.type !== SUB) {
          if (c.required && seenOptional) problems.push(`${path}: required option ${c.name} after an optional one`);
          if (!c.required) seenOptional = true;
        }
        check(c, `${path} ${c.name}`);
      }
    }
  };
  for (const d of defs) check(d, `/${d.name}`);
  const size = JSON.stringify(defs).length;
  if (size > 8000) problems.push(`definitions are ${size} characters; keep well under Discord's 8000 total`);
  return problems;
}
