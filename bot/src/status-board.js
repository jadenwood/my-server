// The live status message: one message in REALM_STATUS_CHANNEL_ID that the bot keeps editing.
// - Its id is kept in the state file, so a restart edits the same message instead of posting a new one.
// - It is only edited when the content changed (and at least every 10 minutes), which keeps the bot far
//   below Discord's rate limits.
// - If the message was deleted, a new one is posted. If the channel cannot be reached, it backs off
//   (doubling up to 10 minutes) and logs once per kind of problem.

import { NO_MENTIONS } from './handlers.js';
import { statusEmbed, unavailableEmbed } from './views.js';

const FORCE_REFRESH_MS = 10 * 60 * 1000;
const MAX_BACKOFF_MS = 10 * 60 * 1000;

export function createStatusBoard({ client, cfg, chronicle, realmData, store, logger = console, now = Date.now, timers = globalThis }) {
  let timer = null;
  let stopped = true;
  let lastKey = null;
  let lastWrite = 0;
  let failures = 0;
  let lastProblem = null;
  let running = false;

  const channelId = cfg.status.channelId;

  async function buildPayload() {
    try {
      const [state, events, realmEvents] = await Promise.all([
        chronicle.state(),
        chronicle.events(10),
        realmData.realmEvents().catch(() => null),
      ]);
      return { embeds: [statusEmbed({ cfg, state, events, realmEvents, now: now() })] };
    } catch (e) {
      return { embeds: [unavailableEmbed(cfg)], unavailable: e.message };
    }
  }

  // The comparison ignores the embed timestamp: the plugin refreshes it every 30 s even when nothing changed.
  const keyOf = (payload) => JSON.stringify(payload.embeds.map(({ timestamp, ...rest }) => rest));

  function problem(kind, message) {
    if (lastProblem !== kind) logger.warn(`[status] ${message}`);
    lastProblem = kind;
  }

  async function fetchChannel() {
    const channel = await client.channels.fetch(channelId);
    if (!channel || typeof channel.send !== 'function' || !channel.messages) throw Object.assign(new Error('not a text channel'), { code: 'NOT_TEXT' });
    return channel;
  }

  async function postNew(channel, body) {
    const msg = await channel.send(body);
    store.data.status = { channelId, messageId: msg.id };
    await store.save();
    return 'posted';
  }

  async function tick() {
    if (running) return 'busy';
    running = true;
    try {
      const payload = await buildPayload();
      if (payload.unavailable) problem('chronicle', `Chronicle unavailable: ${payload.unavailable}`);
      const key = keyOf(payload);
      const fresh = now() - lastWrite < FORCE_REFRESH_MS;
      if (key === lastKey && fresh) return 'unchanged';

      const body = { embeds: payload.embeds, allowedMentions: NO_MENTIONS };
      const channel = await fetchChannel();
      let result;
      const saved = store.data.status;
      if (saved?.messageId && saved.channelId === channelId) {
        let msg = null;
        try {
          msg = await channel.messages.fetch(saved.messageId);
        } catch (e) {
          if (e.code !== 10008) throw e; // 10008 Unknown Message: it was deleted
        }
        if (msg && msg.author?.id === client.user?.id) {
          await msg.edit(body);
          result = 'edited';
        } else {
          result = await postNew(channel, body);
        }
      } else {
        result = await postNew(channel, body);
      }
      lastKey = key;
      lastWrite = now();
      failures = 0;
      if (!payload.unavailable) lastProblem = null;
      return result;
    } catch (e) {
      failures++;
      const hint = e.code === 50001 ? 'the bot cannot see the channel (View Channel)'
        : e.code === 50013 ? 'the bot is missing Send Messages / Embed Links / Read Message History in the channel'
        : e.code === 10003 ? 'the channel does not exist (check REALM_STATUS_CHANNEL_ID)'
        : e.code === 'NOT_TEXT' ? 'REALM_STATUS_CHANNEL_ID is not a text channel'
        : e.message;
      problem(`discord:${e.code ?? e.message}`, `cannot update the status message: ${hint}`);
      return 'error';
    } finally {
      running = false;
    }
  }

  function delayMs() {
    const base = cfg.status.intervalSec * 1000;
    return failures ? Math.min(MAX_BACKOFF_MS, base * 2 ** Math.min(failures, 6)) : base;
  }

  function schedule() {
    if (stopped) return;
    timer = timers.setTimeout(async () => {
      await tick();
      schedule();
    }, delayMs());
    timer?.unref?.();
  }

  return {
    async start() {
      if (!channelId) return false;
      stopped = false;
      await tick();
      schedule();
      return true;
    },
    stop() {
      stopped = true;
      if (timer) timers.clearTimeout(timer);
      timer = null;
    },
    tick,
    get failures() {
      return failures;
    },
    delayMs,
  };
}
