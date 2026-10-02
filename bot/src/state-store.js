// The bot's own small state file (status message id, oath cooldowns). Written atomically: a temp file
// in the same folder, then rename, so a crash never leaves half a file. A broken file is set aside as
// <name>.broken-<time> and the bot starts clean instead of refusing to run.

import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

const EMPTY = () => ({ version: 1, status: { channelId: null, messageId: null }, oaths: {} });

export function createStateStore(path, { logger = console } = {}) {
  let data = EMPTY();
  let writing = Promise.resolve();

  return {
    path,
    get data() {
      return data;
    },
    async load() {
      let text;
      try {
        text = await readFile(path, 'utf8');
      } catch (e) {
        if (e.code === 'ENOENT') return data;
        throw e;
      }
      try {
        const parsed = JSON.parse(text);
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('not an object');
        data = { ...EMPTY(), ...parsed };
        if (!data.status || typeof data.status !== 'object') data.status = EMPTY().status;
        if (!data.oaths || typeof data.oaths !== 'object') data.oaths = {};
      } catch (e) {
        const aside = `${path}.broken-${Date.now()}`;
        logger.warn(`[state] ${path} is unreadable (${e.message}); moved it to ${aside} and starting clean`);
        await rename(path, aside).catch(() => {});
        data = EMPTY();
      }
      return data;
    },
    // Serialised so two quick saves never interleave.
    save() {
      const snapshot = JSON.stringify(data, null, 2);
      writing = writing.then(async () => {
        await mkdir(dirname(path), { recursive: true });
        const tmp = `${path}.tmp-${process.pid}`;
        await writeFile(tmp, snapshot, 'utf8');
        await rename(tmp, path);
      }).catch((e) => logger.error(`[state] could not save ${path}: ${e.message}`));
      return writing;
    },
  };
}
