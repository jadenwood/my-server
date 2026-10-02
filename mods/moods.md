# World moods

A **world mood** is a named preset for the game's built-in Mods system. It changes fog, sun and moon colour, weather odds and, in one case, the speed of the day. Ostreval looks and plays differently from one season to the next, but nothing on the player's side changes. The server sends the values to each player when they join, and the player's own unmodified Steam copy applies them (see [README.md](README.md) and [docs/mods-keys-from-dll.md](../docs/mods-keys-from-dll.md)).

There are eleven moods. Seven are **season moods**, which last a whole season: four take turns in the season cycle and three are **season looks**, alternatives an owner may pick instead. Four are **overlays**, which run for an event, a weekend or a short story arc and then hand back to the season mood.

| Mood | Kind | In one line | Folder |
|---|---|---|---|
| Grim but Readable | season | The baseline: cool grey light and mixed weather. Used for the Thaw season and between arcs. | `presets/grim-but-readable/` (made by an earlier team, read-only here) |
| Golden Summer | season | Warm honey light, thin haze, almost no rain, slightly longer days. | `presets/golden-summer/` |
| Storm Season | season | Rain most of the time, slate light, wet fog. Clear skies are rare. | `presets/storm-season/` |
| Long Winter | season | Pale cold sun, low grey skies, icy fog, a bright cold moon. | `presets/long-winter/` |
| Spring Rains | season look | Fresh green-gold light, a light mist, showers but never a storm. An alternative to Grim for the Thaw. | `presets/spring-rains/` |
| Hunter's Moon | season look | A copper moon, amber afternoons, valley mist, grey days without storms. An alternative to Storm Season. | `presets/hunters-moon/` |
| First Frost | season look | Crisp blue-white light, a bright cold moon, thin air, mostly clear. An alternative to Long Winter. | `presets/first-frost/` |
| Blood Moon | overlay | Red moon nights under clear skies. The season finale. | `presets/blood-moon/` |
| Crown Night | overlay | A gold moon over a violet haze, clear skies. The weekly Crown Night. | `presets/crown-night/` |
| Truce | overlay | Soft pale light, thin air, no rain. A scheduled Truce of the Realm. | `presets/truce/` |
| Ashfall | overlay | Brown-grey ash haze, an orange sun, dry and overcast. | `presets/ashfall/` |

## The keys moods may use

A mood may only use the twelve keys proven from the game's code in [docs/mods-keys-from-dll.md](../docs/mods-keys-from-dll.md), section 3. `server/Set-Mood.ps1` refuses any other key.

| Key | What it does (from the code) |
|---|---|
| `Atmosphere.FogDensity` | Divides fog visibility. 1.25 means sight in fog is 1/1.25 = 80% as far. |
| `Atmosphere.FogColor` | Multiplies each biome's fog colour, day and night. |
| `Atmosphere.SunColor` | Sets the sunlight and the sun disc. Each frame the light is multiplied by `1.12 - 0.65 × cloud`. |
| `Atmosphere.MoonColor` | The same for the moon. This is the main lever for night brightness. |
| `Atmosphere.IslandLatitude`, `IslandLongitude` | Move the sun's path. Allowed, but no mood uses them. |
| `Weather.ClearWeight` … `PrecipitateHeavyWeight` | Weather odds. See the next section. |
| `Clock.DaySpeed` | Speed of the whole day-night cycle. Moods may only set it relative to the server default (see Golden Summer). |

### How weather weights become odds

At each weather change, `Weather.ChangeTheWeather` multiplies each weight by a random whole number from 0 to 99. The highest product wins **only if it is strictly highest**. On a tie the current weather stays. Two consequences follow:

- **A weight of 0 never wins.** A mood with `PrecipitateHeavyWeight = '0'` never starts a heavy storm. The one exception is a storm already running when the mood is installed, which lasts until the next change.
- **Small differences in weight have large effects.** The odds below are exact. Set-Mood.ps1 computes them by enumerating the game's rule, and they agree with a 100,000-draw simulation to within 0.2 points.

| Mood | Weights (clear / cloudy / low / medium / heavy) | Clear | Cloudy | Light rain | Medium rain | Heavy rain | No change |
|---|---|---|---|---|---|---|---|
| Grim but Readable | 4 / 5 / 4 / 3 / 2 | 23.3% | 43.3% | 23.3% | 8.1% | 1.3% | 0.6% |
| Golden Summer | 8 / 3 / 1 / 0 / 0 | 80.2% | 18.2% | 1.5% | never | never | 0.1% |
| Storm Season | 2 / 4 / 6 / 6 / 5 | 0.5% | 8.8% | 35.0% | 35.0% | 20.0% | 0.8% |
| Long Winter | 3 / 6 / 4 / 3 / 2 | 8.9% | 56.5% | 23.5% | 8.9% | 1.5% | 0.6% |
| Blood Moon | 6 / 3 / 1 / 0 / 0 | 73.6% | 24.0% | 1.9% | never | never | 0.5% |
| Ashfall | 3 / 7 / 1 / 0 / 0 | 20.8% | 77.4% | 1.7% | never | never | 0.2% |
| Crown Night | 7 / 3 / 1 / 0 / 0 | 77.4% | 20.8% | 1.7% | never | never | 0.2% |
| Truce | 6 / 5 / 0 / 0 / 0 | 58.2% | 41.7% | never | never | never | 0.2% |
| Spring Rains | 4 / 4 / 6 / 2 / 0 | 21.3% | 21.3% | 54.5% | 2.1% | never | 0.7% |
| Hunter's Moon | 5 / 5 / 3 / 1 / 0 | 43.4% | 43.4% | 11.9% | 0.3% | never | 0.8% |
| First Frost | 6 / 4 / 1 / 1 / 0 | 65.3% | 32.2% | 1.1% | 1.1% | never | 0.3% |

**UNVERIFIED: how often the weather changes.** The interval is `_WeatherChangeRate`, a Unity-serialized field. Its C# initializer is `2`, but the scene value is not in the DLL. Cloud cover moves towards its target at `_CloudCoverDelta`, which is 0.0006 per second in the code, so a full clear-to-overcast change takes about 28 minutes. If the change interval is short, the weights act as a mix of targets that the sky drifts between, and the table gives the share of time each target is picked.

## Readability floors

Every mood should look different, but in every one a player must still be able to see another player. Set-Mood.ps1 enforces these limits and refuses a mood file that breaks one:

| Rule | Limit | Why |
|---|---|---|
| Moon luminance (Rec. 709: 0.2126 R + 0.7152 G + 0.0722 B) | ≥ 0.55 | Nights are where moods become unplayable. Grim's moon is 0.90. The darkest moon in any mood is Blood Moon's, at 0.60. |
| Sun luminance | ≥ 0.60 | Cloud already scales the light by as little as 0.47 at full cover. |
| Fog colour luminance | ≥ 0.60 | Fog colour multiplies, so a dark fog colour darkens everything at a distance. |
| `FogDensity` | 0.5 to 1.5 | Above 1.5, sight in fog is less than two-thirds of normal, and rain cuts it further (`FogVolumeController.RainyWeatherVisibility`). |
| Colour components | 0 to 1 | Values above 1 are HDR in Unity. What they do on this sky system is UNVERIFIED. |
| `Clock.DaySpeed` | `#@scale` factor 0.5 to 2 only | The scene's real day speed is UNVERIFIED, so a literal value could make days many times longer or shorter. |
| Weather weights | whole numbers 0 to 10, not all 0 | `[Range(0,10)]` in the code. If every weight is 0, the weather never changes. |

The luminance numbers measure light *colour* only. How bright the game looks in the end also depends on the scene's own settings, which the DLL does not contain. **Check every mood in game** (see "Checking a mood" below).

---

## Long Winter, Blood Moon, Golden Summer, Storm Season and Ashfall

All colour and fog values assume the scene defaults are white and `1`, which are the code initializers. Set-Mood.ps1 prints the server's real default next to each value and flags any difference.

### Long Winter

*The hungry months before the Long Thaw.* In the lore the Long Thaw ended Isolde's reign (see [docs/community/lore.md](../docs/community/lore.md)), so a Long Winter season is the realm's memory of the cold before it.

| Key | Value | Effect |
|---|---|---|
| `Atmosphere.FogDensity` | `1.35` | Sight in fog is about 26% shorter. |
| `Atmosphere.FogColor` | `rgba(0.86,0.92,1,1)` | Pale icy-blue fog (luminance 0.91). |
| `Atmosphere.SunColor` | `rgba(0.82,0.9,1,1)` | A thin, cold sun, about 11% dimmer (luminance 0.89). |
| `Atmosphere.MoonColor` | `rgba(0.8,0.9,1,1)` | A bright, cold "snow moon" (luminance 0.89). |
| Weather | 3 / 6 / 4 / 3 / 2 | Low grey skies most of the time. Clear only 9% of changes. |

**Rationale:** winter here means cold colour, closed skies and short sight lines, not darkness. Raids get shorter and closer, and scouts and watchtowers matter more.

**Readability note:** the fog stays below 1.4 because rain cuts visibility again. The moon is set *brighter* than Grim's on purpose, because fog and cloud already darken the nights. If nights look too dark in testing, lower `FogDensity` to 1.25 first, before you touch the moon. **UNVERIFIED:** whether this map can show snow. No Mods key for snow exists, so precipitation will look like the game's rain.

### Blood Moon

*A red moon over Ostreval.* This is an overlay for season finales and, at most once more a season, a Crown Night (`crown_night_blood`); the weekly Crown Night has its own gold mood. It is not meant to run for a whole season.

| Key | Value | Effect |
|---|---|---|
| `Atmosphere.MoonColor` | `rgba(1,0.5,0.42,1)` | A rust-red moon: both the light and the disc (luminance 0.60). |
| `Atmosphere.SunColor` | `rgba(1,0.93,0.86,1)` | A faintly ember-warm day (luminance 0.94). |
| `Atmosphere.FogColor` | `rgba(1,0.88,0.84,1)` | Rose-tinted fog. It is kept subtle because it also shows by day. |
| `Atmosphere.FogDensity` | `1.1` | Sight in fog is about 9% shorter. |
| Weather | 6 / 3 / 1 / 0 / 0 | Mostly clear, so cloud does not hide the moon. Never medium or heavy rain. |

**Rationale:** the event is the moon itself. Clear skies make sure it is seen, because cloud multiplies moonlight by as little as 0.47. Red light at night makes a contested Crown Night look like an occasion on stream.

**Readability note:** this is the darkest moon of any mood (luminance 0.60, against a floor of 0.55). The red channel is kept at full strength, so silhouettes against the sky still read. Weapons, armour and banners will look reddish at night. The UI and nameplates are not lit by the moon (UNVERIFIED in game). If the night is too dark, raise green and blue together, for example to `rgba(1,0.58,0.5,1)` (luminance 0.66).

### Golden Summer

*Harvest season.* This is the "easy" mood, for a launch season, a new-player wave or building weeks.

| Key | Value | Effect |
|---|---|---|
| `Atmosphere.SunColor` | `rgba(1,0.95,0.82,1)` | Warm golden sunlight (luminance 0.95). |
| `Atmosphere.MoonColor` | `rgba(0.95,0.95,1,1)` | A clear, almost white moon (luminance 0.95). |
| `Atmosphere.FogColor` | `rgba(1,0.97,0.9,1)` | A warm haze tint. |
| `Atmosphere.FogDensity` | `0.85` | Sight in fog is about 18% **longer**. This is the only mood that thins fog. |
| Weather | 8 / 3 / 1 / 0 / 0 | Clear 80% of changes. Never medium or heavy rain. |
| `Clock.DaySpeed` | `#@scale 0.9` | The whole cycle runs 10% slower: server default × 0.9. |

**Rationale:** long sight lines, warm light and dry weather. New players can learn the map, and archers and scouts do well.

**Readability note:** this is the most readable mood. The only risk is glare: warm, bright light on a clear day can wash out pale banners. **About `#@scale`:** the game reads the line `#@scale Clock.DaySpeed = '0.9'` as a comment. Set-Mood.ps1 reads it as "take the server's own `Clock.DaySpeed` default from `Mods\*.defaults.cfg` and multiply it by 0.9", then writes the result as a normal line. The value is relative because the scene's real `DaySpeed` is UNVERIFIED. No key exists for daytime only, so nights also become 10% longer. If you copy the file by hand, the line is skipped.

### Storm Season

*The autumn gales.* Rain most of the time and slate-grey light. Ambushes in the rain and scouting matter.

| Key | Value | Effect |
|---|---|---|
| Weather | 2 / 4 / 6 / 6 / 5 | Rain on 90% of changes. Heavy rain on one in five. Clear 0.5%. |
| `Atmosphere.FogDensity` | `1.2` | Sight in fog is about 17% shorter. |
| `Atmosphere.FogColor` | `rgba(0.82,0.86,0.92,1)` | Wet slate-blue fog (luminance 0.86). |
| `Atmosphere.SunColor` | `rgba(0.85,0.88,0.94,1)` | Slate daylight (luminance 0.88). |
| `Atmosphere.MoonColor` | `rgba(0.88,0.92,1,1)` | A brighter-than-Grim moon (luminance 0.92), against near-permanent cloud. |

**Rationale:** the weather is the identity. The light colours stay close to Grim because the game already darkens stormy skies on its own.

**Readability note:** heavy rain is deliberately *not* the most likely weather. At full cloud the game multiplies sun and moon light by 0.47, and rain also shortens fog visibility. The fog is held at 1.2 for the same reason. If storm nights are too dark, raise the heavy-rain weight's neighbours instead of the moon. For example, 2 / 5 / 6 / 6 / 4 gives cloudy 20%, light and medium rain 35% each, and heavy rain 8.8%.

### Ashfall

*The sky after the burning.* This is an overlay for a war arc, for example after the crown changes hands by force. It is not for a whole season.

| Key | Value | Effect |
|---|---|---|
| `Atmosphere.FogDensity` | `1.45` | Sight in fog is about 31% shorter. This is the heaviest fog of any mood. |
| `Atmosphere.FogColor` | `rgba(0.8,0.74,0.68,1)` | Brown-grey ash (luminance 0.75). |
| `Atmosphere.SunColor` | `rgba(1,0.78,0.6,1)` | A smothered orange sun (luminance 0.81). |
| `Atmosphere.MoonColor` | `rgba(0.92,0.8,0.7,1)` | A dusty amber moon (luminance 0.82). |
| Weather | 3 / 7 / 1 / 0 / 0 | Dry and overcast. No rain washes the ash out. |

**Rationale:** the realm itself looks wounded. The war shows in the sky for the days after it.

**Readability note:** this is the most restrictive mood. It stays legal only because rain almost never comes, so the fog cut is not doubled. Keep it short (seven days at most) and never run it in a season's first week, when new players are still learning the map. If it is too thick, 1.35 still reads as ash.

---

## Five more moods: Crown Night, Truce and three season looks

Two more overlays for the realm's own events, and three **season looks**: alternatives an owner can pick by hand with `-Mood` instead of the cycle's mood (listed under `seasonLooks` in [`presets/rotation.json`](presets/rotation.json); `-Season` does not use them). The same rules apply: only the twelve proven keys, every readability floor kept, colours assume white scene defaults. **What each one looks like is described from the colour maths only. Nobody has seen any of them in game (UNVERIFIED).**

Every mood can be installed with `server\Set-Mood.ps1 -Mood <id>` or, without a full swap, with `presets\grim-but-readable\Apply-Preset.ps1 -Mood <id>` (see [README.md](README.md)).

### Crown Night

*The night the Old Throne is fought for.* The weekly overlay for RealmEvents' Crown Night (`-Event crown_night`). Blood Moon stays the rarer, darker look for the season finale and at most one more Crown Night a season (`-Event crown_night_blood`), so the red moon keeps its weight.

| Key | Value | Effect |
|---|---|---|
| `Atmosphere.MoonColor` | `rgba(1,0.92,0.7,1)` | A gold moon, light and disc (luminance 0.92). |
| `Atmosphere.SunColor` | `rgba(1,0.92,0.84,1)` | A warm, dusk-coloured day before the fight (luminance 0.93). |
| `Atmosphere.FogColor` | `rgba(0.9,0.86,0.96,1)` | A faint royal-violet haze (luminance 0.88). |
| `Atmosphere.FogDensity` | `0.95` | Sight in fog is about 5% longer. |
| Weather | 7 / 3 / 1 / 0 / 0 | Clear 77% of changes. Never medium or heavy rain. |

**What it should look like (UNVERIFIED):** warm gold moonlight on stone and armour, a soft violet cast in the distance, clear skies. **Rationale:** Crown Night is a fight at night, and on stream it has to read. Gold moonlight is bright: of the overlays only Truce has a brighter moon. **Readability note:** If banners look washed out, lower red and green together to `rgba(0.95,0.88,0.7,1)` (luminance 0.88).

### Truce

*The realm holds its breath.* An overlay for a scheduled Truce of the Realm long enough to plan a restart around: a truce day, peace talks, a break between seasons (`-Event truce`). A short truce needs no mood, as before.

| Key | Value | Effect |
|---|---|---|
| `Atmosphere.SunColor` | `rgba(1,0.98,0.95,1)` | A soft, faintly warm sun, the closest to plain white of any mood (luminance 0.98). |
| `Atmosphere.MoonColor` | `rgba(0.94,0.96,1,1)` | A pale silver moon (luminance 0.96). |
| `Atmosphere.FogColor` | `rgba(0.96,0.97,1,1)` | A clean, faintly cool haze (luminance 0.97). |
| `Atmosphere.FogDensity` | `0.9` | Sight in fog is about 11% longer. |
| Weather | 6 / 5 / 0 / 0 / 0 | Clear or cloudy only. It never rains during a truce. |

**What it should look like (UNVERIFIED):** quiet, pale and even light, long views, no rain. **Rationale:** a truce should feel different from war at a glance, and riders under a banner of truce should be seen coming. **Readability note:** the most readable mood after Golden Summer; nothing to watch except that it may look flat.

### Spring Rains (season look)

*The realm turning green again.* An alternative to Grim but Readable for the Thaw season.

| Key | Value | Effect |
|---|---|---|
| `Atmosphere.SunColor` | `rgba(0.98,1,0.92,1)` | A fresh sun with a hint of green-gold (luminance 0.99). |
| `Atmosphere.MoonColor` | `rgba(0.9,0.96,1,1)` | A clear, cool moon (luminance 0.95). |
| `Atmosphere.FogColor` | `rgba(0.9,0.97,0.92,1)` | A faintly green mist (luminance 0.95). |
| `Atmosphere.FogDensity` | `1.1` | Sight in fog is about 9% shorter. |
| Weather | 4 / 4 / 6 / 2 / 0 | Light rain on more than half the changes, never a storm. |

**What it should look like (UNVERIFIED):** bright, slightly green light between quick showers. **Rationale:** spring is wet but gentle; showers break up long sight lines without the slog of Storm Season. **Readability note:** light rain shortens sight in fog again (`RainyWeatherVisibility`); if it feels closed in, lower `FogDensity` to `1.0` first.

### Hunter's Moon (season look)

*Late autumn after the harvest.* An alternative to Storm Season for an autumn that should be moody rather than wet. It also suits a King's Hunt week.

| Key | Value | Effect |
|---|---|---|
| `Atmosphere.MoonColor` | `rgba(1,0.78,0.55,1)` | A low copper moon (luminance 0.81). |
| `Atmosphere.SunColor` | `rgba(1,0.9,0.76,1)` | Amber afternoons (luminance 0.91). |
| `Atmosphere.FogColor` | `rgba(0.96,0.9,0.8,1)` | A warm grey-amber valley mist (luminance 0.91). |
| `Atmosphere.FogDensity` | `1.2` | Sight in fog is about 17% shorter. |
| Weather | 5 / 5 / 3 / 1 / 0 | Clear or grey in equal measure, some light rain, never a storm. |

**What it should look like (UNVERIFIED):** copper nights and amber days with mist in the low ground. **Rationale:** closer, quieter sight lines for hunting country, without Storm Season's rain. **Readability note:** the copper moon is darker than most (0.81) but well above the floor, and red is kept full so silhouettes read. If nights are too dark, raise green and blue together, for example to `rgba(1,0.84,0.64,1)`.

### First Frost (season look)

*The first cold, clear days of winter.* A readable alternative to Long Winter, for a new-player wave or a server that finds Long Winter too closed in.

| Key | Value | Effect |
|---|---|---|
| `Atmosphere.SunColor` | `rgba(0.9,0.95,1,1)` | A crisp, cold sun (luminance 0.94). |
| `Atmosphere.MoonColor` | `rgba(0.85,0.93,1,1)` | A bright, cold moon (luminance 0.92). |
| `Atmosphere.FogColor` | `rgba(0.92,0.96,1,1)` | A clean frost-blue haze (luminance 0.95). |
| `Atmosphere.FogDensity` | `0.95` | Sight in fog is about 5% longer. Cold air is clear air. |
| Weather | 6 / 4 / 1 / 1 / 0 | Mostly clear, never a storm. |

**What it should look like (UNVERIFIED):** blue-white light, long clear views, a bright moon. **Rationale:** winter's colour without winter's walls of fog. **Readability note:** no Mods key exists for snow, so any precipitation looks like the game's rain.

---

## Rotation plan

The plan ties moods to the two schedule systems that already exist. It is kept in [`presets/rotation.json`](presets/rotation.json), and `Set-Mood.ps1 -Season` and `-Event` read it from there.

- **Seasons** come from `plugins/RealmSeasons.cs`. They are numbered, and the default length is 28 days (`DefaultSeasonDays`).
- **Events** come from `plugins/RealmEvents.cs`: Crown Night (default Saturday 19:00 UTC for 90 minutes), Royal Tournament, King's Hunt and the Truce.

**A mood takes effect only at a restart.** The server reads `Mods\` when it starts and sends the values to each player when they join (`UnityModManager.LoadModHandler` / `OnPlayerJoin`). No live reload is known, so this is UNVERIFIED. So you swap moods at a planned restart, never in the middle of an event. Players who are online at the restart reconnect and get the new mood.

### Season cycle

`Set-Mood.ps1 -Season <n>` picks `seasonCycle[(n - 1) mod 4]`:

| Season | Mood | Why it comes there |
|---|---|---|
| 1, 5, 9 … | Golden Summer | The most readable mood, for launch and the new-player waves that follow a season start. |
| 2, 6, 10 … | Storm Season | The houses are established, and rain rewards scouting and ambushes. |
| 3, 7, 11 … | Long Winter | The hardest season mood. It is the build-up to a "Thaw". |
| 4, 8, 12 … | Grim but Readable | The Thaw: the familiar baseline, and a breather before summer comes round again. |

**When to swap:** at the first planned restart after the season-end ceremony. With `AutoStartNextSeason`, the new season starts at once, so its first restart is the swap:

1. Stop the test server.
2. Run `server\Backup-Saves.ps1`.
3. Run `server\Set-Mood.ps1 -Season <new season number> -WhatIf`, then run it again without `-WhatIf`.
4. Start the server and run `server\Set-Mood.ps1 -Status`.

### Overlays

| Event key (`-Event`) | Mood | When | Ends |
|---|---|---|---|
| `season_finale` | Blood Moon | The last Saturday of each season: restart that morning, before the final Crown Night. | `-Return` at the Sunday restart. |
| `crown_night` | Crown Night | Any Crown Night: restart before it (default Saturday 19:00 UTC). | `-Return` at the next restart. |
| `crown_night_blood` | Blood Moon | At most one more Crown Night per season instead of `crown_night`, so the red moon stays special. | `-Return` at the next restart. |
| `war_arc` | Ashfall | Admin call after a forced change of crown (a rebellion won). At most once per season, at most 7 days, never in week 1. | `-Return`. |
| `royal_tournament` | Golden Summer | Tournament day, so PvP ranking is not decided by weather. Skip it during a Golden Summer season, where it already applies. | `-Return`. |
| `truce` | Truce | A Truce of the Realm scheduled long enough to plan a restart around (a truce day, peace talks). | `-Return`. |

- The King's Hunt has no mood, and neither does a short truce: they need no restart. Hunter's Moon suits a hunt week if the owner wants one (`-Mood hunters-moon`).
- **Season looks** are not in the cycle. To use one, run `-Mood spring-rains` (or `hunters-moon`, `first-frost`) at the season's first restart instead of `-Season <n>`. `seasonLooks` in `rotation.json` records which cycle mood each one stands in for.
- `-Event` remembers the season mood that was active, and `-Return` puts it back. If you stack overlays, for example Ashfall during a Blood Moon weekend, `-Return` still goes back to the season mood, not to the first overlay.
- A Realm Chronicle entry is not written by Set-Mood.ps1, because it runs while the server is stopped. Announce a mood change in the season-start post or the news feed, as you do for other changes. **No new Chronicle event types are needed.**

---

## Set-Mood.ps1

File: [`server/Set-Mood.ps1`](../server/Set-Mood.ps1). It works with Windows PowerShell 5.1.

```powershell
cd <repo>\server
.\Set-Mood.ps1 -List                       # every mood, its values and exact weather odds, and the rotation
.\Set-Mood.ps1 -Mood long-winter -WhatIf   # dry run: which Mods\<Name>.cfg each line goes to, and the server defaults
.\Set-Mood.ps1 -Mood long-winter
.\Set-Mood.ps1 -Season 3                   # the season cycle picks the mood
.\Set-Mood.ps1 -Event season_finale        # overlay; remembers the season mood
.\Set-Mood.ps1 -Return                     # back to the season mood
.\Set-Mood.ps1 -Status                     # active mood; checks every line survived the server's rewrite
.\Set-Mood.ps1 -Clear                      # remove every mood line: the server defaults apply
.\Set-Mood.ps1 -RestoreLatest              # put back the newest backup (the current state is backed up first)
```

Here is what it does to keep the server safe:

- **Test copy only.** The script refuses any folder under `steamapps`, `SteamLibrary` or `Steam`, or one whose name contains `Reign Of Kings Dedicated Server`, even if the marker is present. It also uses `Assert-RealmTestCopy` from `server/RealmCommon.ps1`, which requires the `.realm-test-copy` marker made by `New-TestServer.ps1` and refuses `C:`.
- **Server stopped.** The script refuses to run while `Server.exe` or `ROK.exe` is running from that folder, because the server rewrites `Mods\*.cfg` when it starts.
- **Backup first.** Before every write, it copies all `Mods\<Name>.cfg` override files and the mood state file to `G:\RealmTest\backups\moods-<time>-<mood>\`. That folder must be outside the server folder. `-RestoreLatest` puts the newest one back, and before doing so it saves the current state as a `pre-restore` backup.
- **Nothing guessed.** The script finds the right `Mods\<Name>.cfg` by looking up each key in the server's own `Mods\*.defaults.cfg`, because the file name is UNVERIFIED. A key the server does not list is skipped with a warning.
- **A clean swap.** It removes every mood-managed key line from every override file, then writes the new mood. Keys that the old mood set and the new one does not fall back to the server default. Duplicate lines (which the game would rename and ignore) are collapsed. Other keys, comments and `*.defaults.cfg` are never touched. Neither are `Apply-Preset.ps1`'s `.realm-backup` files.
- **Locale guard.** If the server's defaults show comma decimals, the script refuses to change anything (see docs/mods-keys-from-dll.md, section 3.4).
- **Validation.** Every mood file is checked against the proven keys, value types and readability floors before anything is written.
- **State.** The active mood, the overlay's return mood and every line written are stored in `<server>\.realm-mood.json`. This file is outside `Mods\`.

**Coexistence with `presets/grim-but-readable/Apply-Preset.ps1`:** both scripts write the same keys. Apply-Preset.ps1 now takes `-Mood <id>` for any mood (and `-List`), but it only adds or replaces that mood's lines: lines an earlier mood set stay, and it skips Golden Summer's relative `#@scale` day speed. Prefer Set-Mood.ps1, which swaps whole moods and keeps state. If you run `Apply-Preset.ps1 -Revert` later, it restores the files as they were before the *first* Apply-Preset.ps1 change. That undoes any mood set since then.

### Checking a mood (needs the owner's PC)

1. Run the mood with `-WhatIf` and read the "default" column. If a colour default is not `rgba(1,1,1,1)` or `FogDensity` is not `1`, the scene differs from the code, and the look will differ from what is described here.
2. Apply the mood, start the server and run `-Status`. Every line should say `ok`. `MISSING` means the server commented out or dropped the line.
3. Join on `127.0.0.1` and look at noon, dusk, midnight and, if you can, in rain. **The pass mark is the same as for Grim:** another player is still visible at 30 to 40 m at night. The guide for this is in [README.md](README.md).
4. If a mood fails, use the fix in its readability note above. Change the mood file, not the server's `Mods\` file, and run Set-Mood.ps1 again.

## Tests

[`presets/tests/presets.test.mjs`](presets/tests/presets.test.mjs) (Node, runs anywhere: `node --test mods/presets/tests/`) checks every mood file with the same rules as Set-Mood.ps1 (only the proven keys, the line format, value types and every readability floor), that `rotation.json` names only moods that exist and every mood has an entry, that the weather odds in each mood file's comments and in the table above are the exact odds of its weights, that every mood is described here, and that Apply-Preset.ps1 and Set-Mood.ps1 allow the same twelve keys.

[`presets/tests/Test-SetMood.ps1`](presets/tests/Test-SetMood.ps1) builds a fake test copy, with a marker, made-up `Environment.defaults.cfg` and unrelated override files, and runs Set-Mood.ps1 through 75 checks (the five new list checks and the new `crown_night` mapping were not run in this environment, which has no PowerShell: UNVERIFIED until the next `pwsh` run). They cover: list and odds, `-WhatIf` writing nothing, apply, swap, relative `DaySpeed`, a no-op re-apply, stacked overlays and return, season rotation, drift in `-Status`, clear, restore, Steam and marker and backup-location refusals, ten kinds of bad mood file, undeclared keys and comma-decimal refusal.

```powershell
pwsh -File mods/presets/tests/Test-SetMood.ps1                                   # Linux/macOS
powershell -File mods\presets\tests\Test-SetMood.ps1 -WorkDir G:\RealmTest\mood-selftest   # Windows: not on C:
```

The handler name `Environment` in the test is made up. The real file name is UNVERIFIED, and the tests check that the script finds it from the defaults instead of assuming it.

## Still UNVERIFIED (needs a running server)

Everything in [README.md](README.md#still-unverified-needs-a-running-server) applies here. The file name, whether the dedicated server lists the `Atmosphere.*` keys, the real scene defaults, the locale and the build match are all unproven. In addition:

- How every mood **looks**. All the numbers here are colour maths on the code's formulas. Nobody has seen a mood in game yet.
- How often the weather changes (`_WeatherChangeRate`, a serialized scene value), and so how long a storm lasts.
- Whether this map can show snow (Long Winter, First Frost). No Mods key exists for snow.
- Whether `Clock.DaySpeed` has a scene curve (`DaySpeedModifier`) that makes the 0.9 factor feel different by day and by night.
- Whether a mood can be applied without a restart. The plan assumes it cannot.
- What colour values above 1 would do. Moods avoid them.
