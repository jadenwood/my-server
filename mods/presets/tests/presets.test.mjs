// node --test mods/presets/tests/   (no dependencies)
//
// Checks every world mood in mods/presets/ without a server or PowerShell:
//   - each <id>/<id>.cfg passes the same rules server/Set-Mood.ps1 enforces (only the proven keys, the game's line
//     format, value types and every readability floor), read from Set-Mood.ps1's own $MoodKeys table;
//   - rotation.json names only moods that exist, and every mood has an entry;
//   - the weather odds written in each mood file's comments and in mods/moods.md are the exact odds of its weights
//     (the game's rule from Weather.ChangeTheWeather, the same enumeration Set-Mood.ps1 uses);
//   - every mood is described in mods/moods.md;
//   - Apply-Preset.ps1 allows exactly the keys Set-Mood.ps1 allows.
// What it does NOT prove: how any mood looks in game (UNVERIFIED).
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const HERE = dirname(fileURLToPath(import.meta.url));
const PRESETS = join(HERE, '..');
const REPO = join(PRESETS, '..', '..');
const WEIGHTS = ['Weather.ClearWeight', 'Weather.CloudyWeight', 'Weather.PrecipitateLowWeight', 'Weather.PrecipitateMediumWeight', 'Weather.PrecipitateHeavyWeight'];

// $MoodKeys from server/Set-Mood.ps1: 'Key' = @{ Type = 'x'; Min = 1; ... }
export function moodKeys(text) {
  const keys = {};
  for (const m of text.matchAll(/^\s*'([A-Za-z.]+)'\s*=\s*@\{([^}]*)\}/gm)) {
    const spec = {};
    for (const kv of m[2].matchAll(/(\w+)\s*=\s*('?)([^;'\s]+)\2/g)) {
      const v = kv[3];
      spec[kv[1]] = kv[2] ? v : v === '$true' ? true : v === '$false' ? false : Number(v);
    }
    keys[m[1]] = spec;
  }
  return keys;
}

export const luminance = (c) => 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];

// Exact odds of Weather.ChangeTheWeather: each weight * Random.Range(0,100); the strictly highest product wins;
// any tie keeps the current weather. Returns 6 numbers: clear, cloudy, low, medium, heavy, no change.
export function weatherOdds(w) {
  const odds = [];
  for (let i = 0; i < 5; i++) {
    let p = 0;
    for (let r = 0; r < 100; r++) {
      const v = w[i] * r;
      let q = 0.01;
      for (let j = 0; j < 5; j++) {
        if (j === i) continue;
        const c = v === 0 ? 0 : w[j] === 0 ? 100 : Math.min(100, Math.ceil(v / w[j]));
        q = q * c / 100;
      }
      p += q;
    }
    odds.push(p);
  }
  odds.push(Math.max(0, 1 - odds.reduce((a, b) => a + b, 0)));
  return odds;
}

const pct = (x) => (x * 100).toFixed(1) + '%';

// Parses one mood file the way Set-Mood.ps1 does; throws with file:line on any problem.
export function readMood(file, keys) {
  const entries = [];
  const seen = new Set();
  readFileSync(file, 'utf8').split(/\r?\n/).forEach((line, i) => {
    const at = `${file}:${i + 1}`;
    let m = line.match(/^\s*#@scale\s+([^=\s]+)\s+=\s*'?([^'#]*)'?/);
    const scale = !!m;
    if (!m) {
      if (/^\s*(#|$)/.test(line)) return;
      if (/^\s*[^#=\s][^=]*[^\s=]=/.test(line)) throw new Error(`${at}: no space before '='`);
      m = line.match(/^\s*([^#=\s][^=]*?)\s+=\s*'?([^'#]*)'?/);
      if (!m) throw new Error(`${at}: not a key = 'value' line`);
    }
    const key = m[1].trim();
    const val = m[2].trim();
    const spec = keys[key];
    if (!spec) throw new Error(`${at}: '${key}' is not a proven mood key`);
    if (seen.has(key)) throw new Error(`${at}: '${key}' is set twice`);
    seen.add(key);
    if (scale) {
      if (!spec.Scale) throw new Error(`${at}: '${key}' cannot be given with #@scale`);
      const f = Number(val);
      const lo = spec.ScaleMin ?? 0.5, hi = spec.ScaleMax ?? 2;
      if (!(f >= lo && f <= hi)) throw new Error(`${at}: #@scale factor out of ${lo}..${hi}`);
      entries.push({ key, value: val, scale: true });
      return;
    }
    if (spec.ScaleOnly) throw new Error(`${at}: '${key}' must be relative (#@scale)`);
    if (spec.Type === 'float') {
      const d = Number(val);
      if (!/^-?\d+(\.\d+)?$/.test(val)) throw new Error(`${at}: '${key}' needs a dot-decimal number`);
      if (d < spec.Min || d > spec.Max) throw new Error(`${at}: '${key}' = ${val} is outside ${spec.Min}..${spec.Max}`);
    } else if (spec.Type === 'weight') {
      if (!/^\d{1,2}$/.test(val) || Number(val) > 10) throw new Error(`${at}: '${key}' must be a whole number 0..10`);
    } else if (spec.Type === 'color') {
      const c = val.match(/^rgba\(([^)]*)\)$/);
      const parts = c ? c[1].split(',').map(Number) : [];
      if (parts.length !== 4 || parts.some((x) => Number.isNaN(x))) throw new Error(`${at}: '${key}' must be rgba(r,g,b,a)`);
      if (parts.some((x) => x < 0 || x > 1)) throw new Error(`${at}: '${key}' components must be 0..1`);
      if (luminance(parts) < spec.MinLum) throw new Error(`${at}: '${key}' luminance ${luminance(parts).toFixed(2)} is below the floor ${spec.MinLum}`);
    }
    entries.push({ key, value: val, scale: false });
  });
  if (!entries.length) throw new Error(`${file} has no mood lines`);
  const w = WEIGHTS.map((k) => entries.find((e) => e.key === k));
  if (w.some(Boolean) && w.every((e) => !e || Number(e.value) === 0)) throw new Error(`${file}: every weather weight is 0`);
  return entries;
}

const setMood = readFileSync(join(REPO, 'server', 'Set-Mood.ps1'), 'utf8');
const KEYS = moodKeys(setMood);
const ids = readdirSync(PRESETS, { withFileTypes: true })
  .filter((d) => d.isDirectory() && existsSync(join(PRESETS, d.name, d.name + '.cfg'))).map((d) => d.name).sort();
const rotation = JSON.parse(readFileSync(join(PRESETS, 'rotation.json'), 'utf8'));
const moodsMd = readFileSync(join(PRESETS, '..', 'moods.md'), 'utf8');
const weightsOf = (entries) => {
  const w = WEIGHTS.map((k) => entries.find((e) => e.key === k));
  return w.every(Boolean) ? w.map((e) => Number(e.value)) : null;
};

test('Set-Mood.ps1 still allows exactly the twelve proven keys', () => {
  assert.equal(Object.keys(KEYS).length, 12);
  assert.equal(KEYS['Atmosphere.MoonColor'].MinLum, 0.55);
  assert.equal(KEYS['Clock.DaySpeed'].ScaleOnly, true);
  const doc = readFileSync(join(REPO, 'docs', 'mods-keys-from-dll.md'), 'utf8');
  for (const k of Object.keys(KEYS)) assert.ok(doc.includes(k), `${k} is in docs/mods-keys-from-dll.md`);
});

test('every mood file passes the rules Set-Mood.ps1 enforces', () => {
  assert.ok(ids.length >= 11, `found ${ids.length} moods`);
  for (const id of ids) {
    assert.match(id, /^[a-z0-9][a-z0-9-]*$/);
    readMood(join(PRESETS, id, id + '.cfg'), KEYS);
  }
});

test('the rule checker refuses bad mood lines', () => {
  const dir = mkdtempSync(join(tmpdir(), 'realm-mood-'));
  const check = (text) => { const f = join(dir, 'm.cfg'); writeFileSync(f, text); return () => readMood(f, KEYS); };
  try {
    assert.throws(check("Atmosphere.FogDensity= '1'"), /no space before/);
    assert.throws(check("Atmosphere.Sky = '1'"), /not a proven mood key/);
    assert.throws(check("Atmosphere.FogDensity = '1'\nAtmosphere.FogDensity = '1.1'"), /set twice/);
    assert.throws(check("Atmosphere.FogDensity = '1.6'"), /outside 0.5..1.5/);
    assert.throws(check("Atmosphere.FogDensity = '1,2'"), /dot-decimal/);
    assert.throws(check("Atmosphere.MoonColor = 'rgba(1,0.3,0.3,1)'"), /below the floor 0.55/);
    assert.throws(check("Atmosphere.SunColor = 'rgba(1.2,1,1,1)'"), /0..1/);
    assert.throws(check("Weather.ClearWeight = '11'"), /0..10/);
    assert.throws(check("Clock.DaySpeed = '2'"), /must be relative/);
    assert.throws(check("#@scale Clock.DaySpeed = '3'"), /factor out of/);
    assert.throws(check(WEIGHTS.map((k) => `${k} = '0'`).join('\n')), /every weather weight is 0/);
    assert.throws(check('# only a comment'), /no mood lines/);
    assert.equal(check("#@scale Clock.DaySpeed = '0.9'\nWeather.ClearWeight = '3'")().length, 2);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('rotation.json and the mood folders agree', () => {
  const moods = rotation.moods;
  assert.deepEqual(Object.keys(moods).sort(), ids, 'every mood folder has a rotation entry and every entry a folder');
  for (const [id, m] of Object.entries(moods)) {
    assert.ok(['season', 'overlay'].includes(m.kind), `${id} kind`);
    assert.ok(m.name && m.summary, `${id} has a name and a summary`);
  }
  assert.equal(rotation.seasonCycle.length, 4);
  for (const id of rotation.seasonCycle) assert.equal(moods[id] && moods[id].kind, 'season', `${id} in the cycle is a season mood`);
  for (const [ev, id] of Object.entries(rotation.events)) assert.ok(ids.includes(id), `event ${ev} -> ${id} exists`);
  for (const [cycleMood, looks] of Object.entries(rotation.seasonLooks || {})) {
    assert.ok(rotation.seasonCycle.includes(cycleMood), `${cycleMood} is in the cycle`);
    for (const id of looks) assert.equal(moods[id] && moods[id].kind, 'season', `${id} is a season look`);
  }
  assert.equal(rotation.events.crown_night, 'crown-night');
  assert.equal(rotation.events.truce, 'truce');
});

test('weather odds in the mood files are the exact odds of their weights', () => {
  const names = ['clear', 'cloudy', 'light rain', 'medium rain', 'heavy rain', 'no change'];
  for (const id of ids) {
    const file = join(PRESETS, id, id + '.cfg');
    const w = weightsOf(readMood(file, KEYS));
    if (!w) continue;
    const text = readFileSync(file, 'utf8');
    const odds = weatherOdds(w);
    for (let i = 0; i < 6; i++) {
      const m = text.match(new RegExp('#[^\\n]*?\\b' + names[i] + ' (\\d+\\.\\d)%'));
      if (m) assert.equal(m[1] + '%', pct(odds[i]), `${id}: ${names[i]} in the comment`);
    }
  }
});

test('mods/moods.md describes every mood, with the exact weather odds', () => {
  const rows = [...moodsMd.matchAll(/^\| ([^|]+?) \| (\d+) \/ (\d+) \/ (\d+) \/ (\d+) \/ (\d+) \|([^\n]*)$/gm)];
  const byName = Object.fromEntries(rows.map((r) => [r[1].trim(), r]));
  for (const id of ids) {
    const name = rotation.moods[id].name;
    assert.ok(moodsMd.includes(`presets/${id}/`), `${id} has its folder listed in moods.md`);
    const row = byName[name];
    assert.ok(row, `${name} has a row in the weather odds table`);
    const w = [2, 3, 4, 5, 6].map((i) => Number(row[i]));
    assert.deepEqual(w, weightsOf(readMood(join(PRESETS, id, id + '.cfg'), KEYS)), `${name}: table weights = file weights`);
    const cells = row[7].split('|').map((c) => c.trim()).filter(Boolean);
    const odds = weatherOdds(w);
    for (let i = 0; i < 6; i++) {
      const want = i < 5 && w[i] === 0 ? 'never' : pct(odds[i]);
      assert.equal(cells[i], want, `${name}: column ${i + 1}`);
    }
  }
});

test('Apply-Preset.ps1 allows the same keys as Set-Mood.ps1', () => {
  const apply = readFileSync(join(PRESETS, 'grim-but-readable', 'Apply-Preset.ps1'), 'utf8');
  const block = (apply.match(/\$provenKeys\s*=\s*@\(([\s\S]*?)\)/) || [])[1] || '';
  const allowed = [...block.matchAll(/'([A-Za-z.]+)'/g)].map((m) => m[1]).sort();
  assert.deepEqual(allowed, Object.keys(KEYS).sort());
  assert.match(apply, /\[string\]\$Mood = 'grim-but-readable'/);
});

test('the odds enumeration matches the documented Grim and Storm values', () => {
  assert.deepEqual(weatherOdds([4, 5, 4, 3, 2]).map(pct), ['23.3%', '43.3%', '23.3%', '8.1%', '1.3%', '0.6%']);
  assert.equal(pct(weatherOdds([2, 4, 6, 6, 5])[4]), '20.0%');
});
