'use strict';

// Crash restarts and daily restarts (steward edition only). Pure decisions; main.js does the work.
//
// Crash restart: an exit that the owner did not ask for restarts after 10 s, 30 s, 1 min, 2 min,
// then 5 min. A run of 30 minutes or longer resets the ladder. Five crashes within 15 minutes trip
// the crash-loop breaker: Realm stops trying and says so (docs/seamless-design.md O2).
//
// Daily restart: before each start Realm writes the seconds until the next restart slot into the
// existing restartTime key. The game's own AutoRestart then broadcasts "The server will be
// restarting in ..." at 60, 30, 15 and 5 minutes and at 30 seconds, and calls Server.Shutdown()
// ([DEC] CodeHatch.Engine.Core.Resetting.AutoRestart, docs/seamless-design.md F10). It does not
// relaunch the process, so Realm restarts it. UNVERIFIED at run time: that the notices show in game
// and that ROK.exe exits afterwards. Realm sends "quit" itself if the server is still up 5 minutes
// after the slot, so a restart happens either way (without the in-game countdown in that case).

const BACKOFF_SECONDS = [10, 30, 60, 120, 300];
const HEALTHY_RESET_MS = 30 * 60 * 1000;
const LOOP_WINDOW_MS = 15 * 60 * 1000;
const LOOP_MAX = 5;
const MIN_RESTART_SECONDS = 10 * 60;
const SCHEDULE_GRACE_MS = 5 * 60 * 1000;
// Absolute cap: this many crashes in a row without one healthy run (HEALTHY_RESET_MS) halts restarts
// even when they are spread out wider than LOOP_WINDOW_MS (for example a server that dies after
// 4 minutes every time would otherwise restart forever at the 5-minute backoff).
const STREAK_MAX = 8;
// An exit only counts as the planned daily restart when the server had been up at least this long.
// restartTime is never below MIN_RESTART_SECONDS, so a genuine scheduled exit always passes; a
// crash on start-up near the planned time does not, and goes through the crash breaker instead.
const SCHEDULED_MIN_UPTIME_MS = 5 * 60 * 1000;

// history: timestamps (ms) of previous unrequested exits, oldest first.
// streak: crashes in a row since the last healthy run (returned for the next call).
// Returns { action: 'restart', delayMs, attempt, history, streak } or { action: 'halt', reason, history, streak }.
function crashDecision(history, now, lastUptimeMs, streak = 0) {
  const recent = (history || []).filter((t) => now - t < LOOP_WINDOW_MS);
  recent.push(now);
  const healthy = lastUptimeMs != null && lastUptimeMs >= HEALTHY_RESET_MS;
  const nextStreak = (healthy || !Number.isInteger(streak) || streak < 0 ? 0 : streak) + 1;
  if (recent.length >= LOOP_MAX) {
    return { action: 'halt', reason: `${recent.length} crashes within 15 minutes. Automatic restarts are paused until you start the server again.`, history: recent, streak: nextStreak };
  }
  if (nextStreak >= STREAK_MAX) {
    return { action: 'halt', reason: `${nextStreak} crashes in a row without a healthy run of 30 minutes. Automatic restarts are paused until you start the server again.`, history: recent, streak: nextStreak };
  }
  let attempt;
  if (lastUptimeMs != null && lastUptimeMs >= HEALTHY_RESET_MS) attempt = 1;
  else attempt = recent.length;
  const delay = BACKOFF_SECONDS[Math.min(attempt, BACKOFF_SECONDS.length) - 1];
  return { action: 'restart', delayMs: delay * 1000, attempt, history: recent, streak: nextStreak };
}

// Next local occurrence of HH:MM strictly after `now` plus a minimum lead time.
function nextSlot(time, now = new Date(), minLeadMs = MIN_RESTART_SECONDS * 1000) {
  const m = /^([01]\d|2[0-3]):([0-5]\d)$/.exec(String(time));
  if (!m) throw new RangeError('time must look like 06:00');
  const d = new Date(now.getTime());
  d.setHours(Number(m[1]), Number(m[2]), 0, 0);
  while (d.getTime() - now.getTime() < minLeadMs) d.setDate(d.getDate() + 1);
  return d;
}

// Value for restartTime (seconds counted from game start). The game counts from when the world
// has loaded, so the measured load time is subtracted to land close to the slot.
// skip: ISO time of a slot that was already used (a restart that just happened), so a server
// restarted shortly before its slot is not planned to restart again at that same slot.
function restartTimeFor(time, now = new Date(), loadSeconds = 0, { skip = null } = {}) {
  let slot = nextSlot(time, now);
  if (skip && slot.toISOString() === skip) slot = nextSlot(time, new Date(slot.getTime() + 60 * 1000));
  const secs = Math.round((slot.getTime() - now.getTime()) / 1000) - Math.max(0, Math.round(loadSeconds || 0));
  return { seconds: Math.max(MIN_RESTART_SECONDS, secs), at: slot.toISOString() };
}

// True when an exit at `exitAt` belongs to the restart planned for `plannedAt` (ISO).
// uptimeMs (optional): how long the server ran; a short run is never the planned restart.
function isScheduledExit(plannedAt, exitAt = Date.now(), uptimeMs = null) {
  const t = Date.parse(plannedAt || '');
  if (!Number.isFinite(t)) return false;
  if (uptimeMs != null && !(uptimeMs >= SCHEDULED_MIN_UPTIME_MS)) return false;
  return exitAt >= t - 20 * 60 * 1000 && exitAt <= t + SCHEDULE_GRACE_MS * 3;
}

// Retention for automatic backups: keep the newest `keep` files whose name has the given label.
function backupsToPrune(names, label, keep = 24) {
  const mine = names.filter((n) => n.endsWith(`-${label}.zip`)).sort();
  return mine.slice(0, Math.max(0, mine.length - keep));
}

module.exports = {
  BACKOFF_SECONDS,
  HEALTHY_RESET_MS,
  LOOP_WINDOW_MS,
  LOOP_MAX,
  SCHEDULE_GRACE_MS,
  STREAK_MAX,
  SCHEDULED_MIN_UPTIME_MS,
  crashDecision,
  nextSlot,
  restartTimeFor,
  isScheduledExit,
  backupsToPrune
};
