// The six great houses of Ostreval, from docs/community/lore.md. Shown by /realm whois next to the live
// data for a house of that name. Players may found any house; these are only the starting families.

export const GREAT_HOUSES = {
  varrow: { words: 'We Stand Our Ground.', seat: 'A stone keep on the hill road below the Old Throne', hook: 'The crown-holders' },
  ashgrove: { words: 'Deep Roots, Long Memory.', seat: 'The orchard-holds of the eastern vale', hook: 'The steady liege or loyal vassal' },
  corvane: { words: 'Every Secret Has a Price.', seat: "The tower of Corvane's Reach, over the river crossing", hook: 'Intrigue' },
  dunmere: { words: 'The Tide Returns.', seat: 'The flooded town of Dunmere, whose bell still rings at low tide', hook: 'The rebels' },
  halloran: { words: 'Loyal Until the Last Coal.', seat: 'The forge-camps of the southern pass', hook: 'Hired swords and ransomers' },
  merrin: { words: 'Slip the Net.', seat: "The river market at Merrin's Ford", hook: 'Traders and go-betweens' },
};

export const loreFor = (name) => GREAT_HOUSES[String(name || '').replace(/^house\s+/i, '').trim().toLowerCase()] || null;
