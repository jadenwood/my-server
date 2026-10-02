'use strict';

// realm:// deep links. The only accepted form is realm://join/<server id>, where the id must be
// one that the signed server list contains. A link never carries a host, port or password, and
// it never starts the game by itself: the player client always shows a confirmation first.

const LINK_RE = /^realm:\/\/join\/([a-z0-9][a-z0-9-]{0,23})\/?$/;

// Returns { action: 'join', id } or null.
function parseDeepLink(url, knownIds) {
  if (typeof url !== 'string' || url.length > 64) return null;
  const m = LINK_RE.exec(url);
  if (!m) return null;
  const id = m[1];
  if (knownIds) {
    const ok = knownIds instanceof Set ? knownIds.has(id) : Array.isArray(knownIds) && knownIds.includes(id);
    if (!ok) return null;
  }
  return { action: 'join', id };
}

// Windows passes the link as a command-line argument (first start or second-instance argv).
function findDeepLinkArg(argv) {
  if (!Array.isArray(argv)) return null;
  for (const a of argv) if (typeof a === 'string' && /^realm:/i.test(a)) return a;
  return null;
}

module.exports = { parseDeepLink, findDeepLinkArg, LINK_RE };
