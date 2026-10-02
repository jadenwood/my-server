'use strict';

// "Join best server": a pure scoring function over the signed list and the live status.
//
// status per id: { online: true|false|null, players: int|null, maxPlayers: int|null, pingMs: int|null }
// Rules: never pick a server known to be offline; avoid full servers (the game would hold the
// player in its built-in queue, [DEC] CoreServer.PlayerShouldBeQueued); prefer servers that are
// lively but not crowded (30-90% full); then lower ping. Unknown status scores below known-good.

function fillOf(st, server) {
  const max = (st && Number.isInteger(st.maxPlayers) && st.maxPlayers > 0 ? st.maxPlayers : server.maxPlayers) || 0;
  if (!st || !Number.isInteger(st.players) || !max) return null;
  return { players: st.players, max, ratio: st.players / max };
}

function scoreServer(server, status) {
  if (status && status.online === false) return { score: -Infinity, why: 'offline' };
  const st = status || { online: null };
  let score = 0;
  const why = [];
  if (st.online === true) score += 1000;
  else why.push('status unknown');
  const fill = fillOf(st, server);
  if (fill) {
    if (fill.players >= fill.max) {
      score -= 800;
      why.push(`full (${fill.players}/${fill.max}), you would queue`);
    } else {
      if (fill.ratio >= 0.3 && fill.ratio <= 0.9) score += 200;
      else if (fill.ratio < 0.3) score += 120 + fill.ratio * 200;
      else score += 100;
      why.push(`${fill.players}/${fill.max} players`);
    }
  }
  if (Number.isFinite(st.pingMs)) {
    score -= Math.min(st.pingMs, 1000) / 2;
    why.push(`${Math.round(st.pingMs)} ms`);
  } else score -= 150;
  return { score, why: why.join(', ') };
}

// Returns { id, why } or null when nothing is joinable.
function pickBest(servers, statuses) {
  let best = null;
  for (const s of servers || []) {
    const r = scoreServer(s, statuses && statuses[s.id]);
    if (r.score === -Infinity) continue;
    if (!best || r.score > best.score) best = { id: s.id, score: r.score, why: r.why };
  }
  return best ? { id: best.id, why: best.why } : null;
}

module.exports = { pickBest, scoreServer };
